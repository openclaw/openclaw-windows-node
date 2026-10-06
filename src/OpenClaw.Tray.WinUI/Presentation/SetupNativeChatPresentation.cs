using OpenClaw.Connection;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;

namespace OpenClawTray.Presentation;

internal enum SetupNativeChatWarning { None, Unavailable, AuthorityUnconfirmed, RenderingFailed }

/// <summary>Page-local presentation only. Evaluation never settles or retries a setup receipt.</summary>
internal sealed class SetupNativeChatPresentation
{
    private SetupNativeNavigationRequest? _request;
    private bool _restoreTarget;

    public SetupNativeChatWarning Warning { get; private set; }
    public bool IsReady { get; private set; }

    public bool Bind(SetupNativeNavigationRequest? request)
    {
        if (ReferenceEquals(_request, request)) return false;
        _request = request;
        _restoreTarget = request is not null;
        Warning = SetupNativeChatWarning.None;
        IsReady = false;
        return true;
    }

    public void Evaluate(
        Func<IOperatorGatewayClient> requireOwner,
        Func<IOperatorGatewayClient, bool> providerReady,
        Func<string?, bool> render,
        Action block,
        Action defer)
    {
        if (_request is null) return;
        IsReady = false;
        IOperatorGatewayClient client;
        try
        {
            client = requireOwner();
        }
        catch (SetupNativeConnectionUnavailableException)
        {
            // Keep mount-local draft state while offline. Reuse still requires
            // the full authority check and the exact provider association.
            Warning = SetupNativeChatWarning.Unavailable;
            defer();
            return;
        }
        catch (SetupNativeOwnershipException)
        {
            Fail(SetupNativeChatWarning.AuthorityUnconfirmed, block);
            return;
        }

        if (!providerReady(client))
        {
            // The manager can be ahead of App's dispatched provider notification.
            // Defer mounting without inventing a connectivity or authority error.
            Warning = SetupNativeChatWarning.None;
            defer();
            return;
        }

        try
        {
            if (!render(_restoreTarget ? _request.Completion.Target.SessionKey : null))
            {
                Fail(SetupNativeChatWarning.RenderingFailed, block);
                return;
            }
            _restoreTarget = false;
            Warning = SetupNativeChatWarning.None;
            IsReady = true;
        }
        catch (InvalidOperationException)
        {
            Fail(SetupNativeChatWarning.RenderingFailed, block);
        }
    }

    private void Fail(SetupNativeChatWarning warning, Action block)
    {
        // Save the exact request's target before the host clears its mounted thread.
        _restoreTarget = true;
        Warning = warning;
        block();
    }
}

/// <summary>
/// Observes, but never owns, the manager. Each subscription is an activation token:
/// even reloading the same request rejects work from the previous activation.
/// </summary>
internal sealed class SetupNativeChatRefresh(
    Func<Action, bool> enqueue,
    Func<object?> getProvider,
    Action<SetupNativeNavigationRequest, IGatewayConnectionManager, string> refresh,
    Action<string> diagnostic)
{
    private readonly object _gate = new();
    private Subscription? _current;
    private ProviderObservation? _providerObservation;

    public void Reconcile(bool active, SetupNativeNavigationRequest? request, IGatewayConnectionManager? manager)
    {
        lock (_gate)
        {
            if (_current is { } current && active && ReferenceEquals(current.Request, request) &&
                ReferenceEquals(current.Manager, manager))
            {
                if ((!current.ProviderConfirmed || !ReferenceEquals(current.Provider, getProvider())) &&
                    current.ProviderConfirmations == 0)
                    ConfirmProvider(current, current.Manager.OperatorClient);
                return;
            }
            Detach();
            if (request is null) _providerObservation = null;
            if (!active || request is null || manager is null) return;
            var subscription = new Subscription(request, manager);
            _current = subscription;
            // A hidden/reloaded page may keep its healthy host and draft. Reuse
            // only an already observed exact manager/client/provider association.
            if (_providerObservation is { } known && ReferenceEquals(known.Manager, manager) &&
                ReferenceEquals(known.Client, manager.OperatorClient) &&
                ReferenceEquals(known.Provider, getProvider()))
            {
                subscription.ProviderClient = known.Client;
                subscription.Provider = known.Provider;
                subscription.ProviderConfirmed = true;
            }
            subscription.StateChanged = (_, snapshot) => Observe(subscription, snapshot);
            subscription.ClientChanged = (_, args) => ConfirmProvider(subscription, args.NewClient);
            manager.StateChanged += subscription.StateChanged;
            manager.OperatorClientChanged += subscription.ClientChanged;
            subscription.Observed = OperatorFacts.Read(manager, manager.CurrentSnapshot);
            ConfirmProvider(subscription, manager.OperatorClient);
        }
    }

    public bool IsProviderCurrent(IOperatorGatewayClient client, object? provider)
    {
        lock (_gate)
            return provider is not null && _current is { ProviderConfirmed: true } current &&
                ReferenceEquals(current.ProviderClient, client) &&
                ReferenceEquals(current.Provider, provider);
    }

    public void Request(string source)
    {
        lock (_gate)
        {
            if (_current is { } current) Queue(current, source);
        }
    }

    private void Observe(Subscription subscription, GatewayConnectionSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, subscription)) return;
            var facts = OperatorFacts.Read(subscription.Manager, snapshot);
            if (facts == subscription.Observed) return;
            subscription.Observed = facts;
            Queue(subscription, "operator state");
        }
    }

    private void ConfirmProvider(Subscription subscription, IOperatorGatewayClient? client)
    {
        lock (_gate) subscription.ProviderConfirmations++;
        // App registered first and queues/applies its provider replacement before
        // this callback. Use the event's exact client, not a later manager client.
        if (!enqueue(() =>
        {
            lock (_gate)
            {
                subscription.ProviderConfirmations--;
                if (!ReferenceEquals(_current, subscription) ||
                    !ReferenceEquals(subscription.Manager.OperatorClient, client))
                {
                    diagnostic("stale callback ignored");
                    return;
                }
                var provider = getProvider();
                if (provider is not null && _providerObservation is { } previous &&
                    !ReferenceEquals(previous.Client, client) && ReferenceEquals(previous.Provider, provider))
                {
                    subscription.ProviderConfirmed = false;
                    diagnostic("provider notification pending");
                    return;
                }
                subscription.ProviderClient = client;
                subscription.Provider = provider;
                subscription.ProviderConfirmed = true;
                _providerObservation = new(subscription.Manager, client, subscription.Provider);
                Queue(subscription, "provider change");
            }
        }))
        {
            lock (_gate)
            {
                subscription.ProviderConfirmations--;
                subscription.ProviderConfirmed = false;
            }
            diagnostic("provider dispatch unavailable");
        }
    }

    private sealed record ProviderObservation(
        IGatewayConnectionManager Manager, IOperatorGatewayClient? Client, object? Provider);

    private void Queue(Subscription subscription, string source)
    {
        subscription.Source = source;
        if (subscription.Queued) return;
        subscription.Queued = true;
        if (enqueue(() =>
        {
            string latestSource;
            lock (_gate)
            {
                if (!ReferenceEquals(_current, subscription))
                {
                    diagnostic("stale callback ignored");
                    return;
                }
                // Release before evaluation so a change arriving during the full
                // check schedules another pass rather than losing the recovery.
                subscription.Queued = false;
                latestSource = subscription.Source;
            }
            refresh(subscription.Request, subscription.Manager, latestSource);
        })) return;
        subscription.Queued = false;
        diagnostic("refresh dispatch unavailable");
    }

    private void Detach()
    {
        if (_current is not { } current) return;
        _current = null;
        current.Manager.StateChanged -= current.StateChanged;
        current.Manager.OperatorClientChanged -= current.ClientChanged;
    }

    private sealed class Subscription(SetupNativeNavigationRequest request, IGatewayConnectionManager manager)
    {
        public SetupNativeNavigationRequest Request { get; } = request;
        public IGatewayConnectionManager Manager { get; } = manager;
        public EventHandler<GatewayConnectionSnapshot>? StateChanged;
        public EventHandler<OperatorClientChangedEventArgs>? ClientChanged;
        public OperatorFacts? Observed;
        public IOperatorGatewayClient? ProviderClient;
        public object? Provider;
        public bool ProviderConfirmed;
        public int ProviderConfirmations;
        public bool Queued;
        public string Source = "binding";
    }

    // No node fields or identity-file reads: this is only a refresh optimization.
    private sealed record OperatorFacts(
        IOperatorGatewayClient? Client, string? GatewayId, string? GatewayUrl,
        RoleConnectionState State, string? DeviceId, bool Connected, string? Session, string? Signer)
    {
        public static OperatorFacts Read(IGatewayConnectionManager manager, GatewayConnectionSnapshot snapshot)
        {
            var client = manager.OperatorClient;
            return new(client, snapshot.GatewayId, snapshot.GatewayUrl, snapshot.OperatorState,
                snapshot.OperatorDeviceId, client?.IsConnectedToGateway == true,
                client?.MainSessionKey, client?.AuthenticatedSigningDeviceId);
        }
    }
}
