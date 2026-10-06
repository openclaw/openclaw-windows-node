using System.Reflection;
using OpenClaw.Connection;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class SetupNativeChatPresentationTests
{
    private static readonly GatewayRecord Gateway = new() { Id = "setup", Url = "wss://example.test/" };
    private static SetupNativeNavigationRequest Request => new(new(
        new(SetupCompletionIntent.CustodianOnboarding, Gateway.Id, GatewayDashboardBinding.Capture(Gateway),
            "provider/model", "verified", 1, IdentityBinding: new string('A', 64), SessionKey: "agent:verified:main"),
        new(SetupNativeDestination.Chat, "agent:verified:main")));

    [Fact]
    public void SameClientRecoveryRetainsHostAndHealthyRefreshDoesNotReseed()
    {
        var h = new Harness();
        h.Activate();
        h.Drain();
        Assert.Equal(Request.Completion.Target.SessionKey, h.MountedTarget);
        h.SetConnected(false);
        h.Drain();
        Assert.Equal(SetupNativeChatWarning.Unavailable, h.Presentation.Warning);
        Assert.False(h.Presentation.IsReady);
        Assert.Equal(Request.Completion.Target.SessionKey, h.MountedTarget);
        Assert.Equal(0, h.Disposals);
        h.SetConnected(true);
        h.Drain();
        Assert.Equal(SetupNativeChatWarning.None, h.Presentation.Warning);
        Assert.Equal(Request.Completion.Target.SessionKey, h.MountedTarget);
        Assert.Equal(1, h.Mounts);
        Assert.Null(h.LastRenderTarget);
        h.Observer.Request("settings");
        h.Drain();
        Assert.Null(h.LastRenderTarget);
        Assert.Equal(1, h.Mounts);
    }

    [Fact]
    public void UnavailableBeforeFirstMountStillSeedsExactTargetOnRecovery()
    {
        var h = new Harness();
        h.SetConnected(false);
        h.Activate();
        h.Drain();
        Assert.Equal(0, h.Mounts);
        Assert.False(h.Presentation.IsReady);
        h.SetConnected(true);
        h.Drain();
        Assert.Equal(Request.Completion.Target.SessionKey, h.LastRenderTarget);
        Assert.True(h.Presentation.IsReady);
    }

    [Fact]
    public void RetainedUnavailableHostIsDiscardedWhenAuthorityFailsOnRecovery()
    {
        var h = new Harness();
        h.Activate();
        h.Drain();
        h.SetConnected(false);
        h.Drain();
        Assert.Equal(0, h.Disposals);
        h.RequireOwner = () => throw new SetupNativeOwnershipException();
        h.SetConnected(true);
        h.Drain();
        Assert.Equal(1, h.Disposals);
        Assert.Null(h.MountedTarget);
        Assert.False(h.Presentation.IsReady);
        Assert.Equal(SetupNativeChatWarning.AuthorityUnconfirmed, h.Presentation.Warning);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PendingVoiceWaitsThroughUnavailableOrDeferredEvaluation(bool unavailable)
    {
        var h = new Harness();
        h.Activate();
        h.Drain();
        var voice = new OpenClawTray.Chat.PendingVoiceActivation();
        voice.Request(nativeSurface: true);
        if (unavailable)
            h.SetConnected(false);
        else
            h.Provider = null;
        h.Observer.Request("voice wait");
        h.Drain();
        Assert.False(voice.TryConsume(true, h.Presentation.IsReady));
        h.DuringCheck = () => Assert.False(voice.TryConsume(true, h.Presentation.IsReady));
        h.Provider ??= new();
        h.SetConnected(true);
        h.Observer.Request("provider ready");
        h.Drain();
        Assert.True(voice.TryConsume(true, h.Presentation.IsReady));
        Assert.False(voice.TryConsume(true, h.Presentation.IsReady));
    }

    [Fact]
    public void BindingClearResetsWithoutRenderingOrRetryingEvenIfNextNavigationFails()
    {
        var h = new Harness();
        h.Activate();
        h.SetConnected(false);
        h.Drain();
        Assert.Equal(SetupNativeChatWarning.Unavailable, h.Presentation.Warning);
        h.Clear();
        // No Settings or renderer is needed to clear the old presentation.
        Assert.Equal(SetupNativeChatWarning.None, h.Presentation.Warning);
        Assert.Equal(0, h.Manager.StateHandlers);
        Assert.Equal(0, h.Manager.ClientHandlers);
        h.SetConnected(true);
        h.Drain();
        Assert.Equal(0, h.Mounts);
        Assert.Equal(SetupNativeChatWarning.None, h.Presentation.Warning);
        Assert.Equal(0, h.Manager.Mutations);
    }

    [Fact]
    public void SameDestinationRetainsUnresolvedWarning()
    {
        var binding = new SetupNativeChatBinding();
        var request = Request;
        binding.Bind(request);
        var presentation = new SetupNativeChatPresentation();
        presentation.Bind(request);
        presentation.Evaluate(() => throw new SetupNativeOwnershipException(), _ => true, _ => true, () => { }, () => { });
        binding.RetainForDestination(request.WorkspaceDestination!);
        Assert.False(presentation.Bind(binding.Request));
        Assert.Equal(SetupNativeChatWarning.AuthorityUnconfirmed, presentation.Warning);
        binding.RetainForDestination(new(WorkspacePageId.Notifications));
        Assert.True(presentation.Bind(binding.Request));
        Assert.Equal(SetupNativeChatWarning.None, presentation.Warning);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReplacementStateAndProviderDeliveryOrdersNeverRenderOldProvider(bool stateFirst)
    {
        var h = new Harness();
        h.Activate();
        h.Drain();
        var oldProvider = h.Provider;
        var replacement = DispatchProxy.Create<IOperatorGatewayClient, ClientProxy>();
        h.Manager.Client = replacement;
        if (stateFirst)
        {
            h.Manager.RaiseState();
            h.Drain();
            Assert.Equal(1, h.Mounts);
            Assert.Equal(SetupNativeChatWarning.None, h.Presentation.Warning);
        }
        // App's subscriber enqueues the provider swap before the page's
        // OperatorClientChanged subscriber, including when notifications race.
        h.Queue.Enqueue(() => { h.Provider = new(); h.Observer.Request("provider change"); });
        h.Manager.RaiseClientChanged(replacement);
        if (!stateFirst) h.Manager.RaiseState();
        h.Drain();
        Assert.NotSame(oldProvider, h.Provider);
        Assert.Equal(SetupNativeChatWarning.None, h.Presentation.Warning);
        Assert.Same(h.Provider, h.RenderedProvider);
        Assert.Equal(2, h.Mounts);
    }

    [Fact]
    public void ReplacementBeforeHandshakeStaysUnavailableUntilSameClientConnects()
    {
        var h = new Harness();
        h.Activate();
        h.Drain();
        var replacement = DispatchProxy.Create<IOperatorGatewayClient, ClientProxy>();
        ((ClientProxy)replacement).Connected = false;
        h.Manager.Client = replacement;
        h.Manager.Snapshot = h.Manager.Snapshot with { OperatorState = RoleConnectionState.Connecting };
        h.Queue.Enqueue(() => h.Provider = new());
        h.Manager.RaiseClientChanged(replacement);
        h.Drain();
        Assert.Equal(SetupNativeChatWarning.Unavailable, h.Presentation.Warning);
        h.SetConnected(true);
        h.Drain();
        Assert.Equal(SetupNativeChatWarning.None, h.Presentation.Warning);
        Assert.Equal(Request.Completion.Target.SessionKey, h.MountedTarget);
    }

    [Fact]
    public void MultipleReplacementCallbacksCannotAssociateAnOldProviderWithLatestClient()
    {
        var h = new Harness();
        h.Activate();
        h.Drain();
        var first = DispatchProxy.Create<IOperatorGatewayClient, ClientProxy>();
        var second = DispatchProxy.Create<IOperatorGatewayClient, ClientProxy>();
        h.Manager.Client = first;
        h.Queue.Enqueue(() => h.Provider = new());
        h.Manager.RaiseClientChanged(first);
        h.Manager.Client = second;
        var finalProvider = new object();
        h.Queue.Enqueue(() => h.Provider = finalProvider);
        h.Manager.RaiseClientChanged(second);
        h.Manager.RaiseState();
        h.Drain();
        Assert.Same(finalProvider, h.RenderedProvider);
        Assert.True(h.Observer.IsProviderCurrent(second, finalProvider));
        Assert.False(h.Observer.IsProviderCurrent(first, finalProvider));
    }

    [Fact]
    public void DelayedProviderNotificationCannotRelabelTheExistingProvider()
    {
        var h = new Harness();
        h.Activate();
        h.Drain();
        var replacement = DispatchProxy.Create<IOperatorGatewayClient, ClientProxy>();
        h.Manager.Client = replacement;
        h.Manager.RaiseClientChanged(replacement);
        h.Manager.RaiseState();
        h.Drain();
        Assert.False(h.Observer.IsProviderCurrent(replacement, h.Provider));
        Assert.Equal(1, h.Mounts);
        h.Provider = new();
        h.Observer.Request("provider change");
        h.Drain();
        Assert.True(h.Observer.IsProviderCurrent(replacement, h.Provider));
        Assert.Equal(2, h.Mounts);
    }

    [Fact]
    public void NodeOnlyChangesDoNotCheckIdentityDisposeRemountOrReseed()
    {
        var h = new Harness();
        h.Activate();
        h.Drain();
        var checks = h.Checks;
        for (var i = 0; i < 20; i++)
        {
            h.Manager.Snapshot = h.Manager.Snapshot with
            {
                NodeState = i % 2 == 0 ? RoleConnectionState.Connecting : RoleConnectionState.Connected,
                NodeDeviceId = i.ToString(),
                NodeError = i.ToString(),
                OverallState = i % 2 == 0 ? OverallConnectionState.Degraded : OverallConnectionState.Ready,
            };
            h.Manager.RaiseState();
        }
        Assert.Empty(h.Queue);
        Assert.Equal(checks, h.Checks);
        Assert.Equal(1, h.Mounts);
        Assert.Equal(0, h.Disposals);
    }

    [Fact]
    public void DisconnectReconnectBurstAndEventDuringEvaluationDoNotLoseFinalRefresh()
    {
        var h = new Harness();
        h.Activate();
        h.Drain();
        var checks = h.Checks;
        h.SetConnected(false);
        h.SetConnected(true);
        Assert.Single(h.Queue);
        h.Drain();
        Assert.Equal(checks + 1, h.Checks);
        Assert.Equal(1, h.Mounts);
        h.DuringCheck = () => { h.DuringCheck = null; h.SetConnected(false); h.SetConnected(true); };
        h.Observer.Request("explicit recheck");
        h.Drain();
        Assert.Equal(checks + 3, h.Checks);
        Assert.Equal(SetupNativeChatWarning.None, h.Presentation.Warning);
    }

    [Theory]
    [InlineData("unload")]
    [InlineData("reload")]
    [InlineData("equal rebind")]
    [InlineData("manager replacement")]
    public void StaleCallbacksCannotTouchNewLifetime(string change)
    {
        var h = new Harness();
        h.Activate();
        h.Drain();
        h.SetConnected(false);
        var stale = h.Queue.Dequeue();
        var checks = h.Checks;
        if (change == "manager replacement")
        {
            var manager = DispatchProxy.Create<IGatewayConnectionManager, ManagerProxy>();
            h.Observer.Reconcile(true, h.Request, manager);
        }
        else
        {
            h.Observer.Reconcile(false, h.Request, h.Connection);
            if (change != "unload")
            {
                var request = change == "equal rebind" ? h.Request with { } : h.Request;
                Assert.Equal(h.Request, request);
                h.Observer.Reconcile(true, request, h.Connection);
            }
        }
        stale();
        Assert.Equal(checks, h.Checks);
        Assert.Equal(0, h.Disposals);
        Assert.Contains("stale callback ignored", h.Diagnostics);
    }

    [Fact]
    public void RepeatedActivationIsIdempotentAndUnloadDetachesBothExactHandlers()
    {
        var h = new Harness();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            for (var i = 0; i < 10; i++) h.Activate();
            Assert.Equal(1, h.Manager.StateHandlers);
            Assert.Equal(1, h.Manager.ClientHandlers);
            h.Drain();
            h.Observer.Reconcile(false, h.Request, h.Connection);
            Assert.Equal(0, h.Manager.StateHandlers);
            Assert.Equal(0, h.Manager.ClientHandlers);
        }
    }

    [Fact]
    public void ReloadWithSameObservedClientAndProviderKeepsHealthyHostBeforeQueuedRefresh()
    {
        var h = new Harness();
        h.Activate();
        h.Drain();
        h.Observer.Reconcile(false, h.Request, h.Connection);
        h.Activate();
        h.Evaluate();
        Assert.Equal(SetupNativeChatWarning.None, h.Presentation.Warning);
        Assert.Null(h.LastRenderTarget);
        Assert.Equal(1, h.Mounts);
        Assert.Equal(0, h.Disposals);
        h.Drain();
        Assert.Equal(1, h.Mounts);
    }

    [Fact]
    public void DispatchRejectionDoesNotRunInlineOrLeaveQueuePermanentlyPending()
    {
        var h = new Harness { AcceptDispatch = false };
        h.Activate();
        Assert.Equal(0, h.Checks);
        Assert.Contains("provider dispatch unavailable", h.Diagnostics);
        h.SetConnected(false);
        Assert.Contains("refresh dispatch unavailable", h.Diagnostics);
        h.AcceptDispatch = true;
        h.Activate();
        h.SetConnected(true);
        h.Drain();
        Assert.Equal(SetupNativeChatWarning.None, h.Presentation.Warning);
        Assert.Equal(1, h.Mounts);
    }

    [Fact]
    public void RendererFailureIsContainedAndExplicitRecheckRunsFullCheckWithoutTransportMutation()
    {
        var h = new Harness { RenderFails = true };
        h.Activate();
        h.Drain();
        Assert.Equal(SetupNativeChatWarning.RenderingFailed, h.Presentation.Warning);
        Assert.Null(h.MountedTarget);
        h.RenderFails = false;
        var checks = h.Checks;
        h.Observer.Request("explicit recheck");
        h.Drain();
        Assert.Equal(checks + 1, h.Checks);
        Assert.Equal(SetupNativeChatWarning.None, h.Presentation.Warning);
        Assert.Equal(Request.Completion.Target.SessionKey, h.MountedTarget);
        Assert.Equal(0, h.Manager.Mutations);
    }

    [Fact]
    public void PendingProviderHidesWithoutDisposalOrFalseReadinessAndReusesHealthyTarget()
    {
        var state = new SetupNativeChatPresentation();
        var client = DispatchProxy.Create<IOperatorGatewayClient, ClientProxy>();
        state.Bind(Request);
        var deferred = 0;
        void Block() => Assert.Fail("Provider confirmation must not dispose a healthy host");
        state.Evaluate(() => client, _ => true, _ => true, Block, () => deferred++);
        Assert.True(state.IsReady);
        state.Evaluate(() => client, _ => false, _ => throw new Xunit.Sdk.XunitException("Must defer"),
            Block, () => deferred++);
        Assert.Equal(1, deferred);
        Assert.False(state.IsReady);
        Assert.Equal(SetupNativeChatWarning.None, state.Warning);
        state.Evaluate(() => client, _ => true, target =>
        {
            Assert.Null(target);
            return true;
        }, Block, () => deferred++);
        Assert.True(state.IsReady);
    }

    [Fact]
    public void NullProviderDefersUntilProviderNotificationWithoutAConnectionEvent()
    {
        var h = new Harness { Provider = null };
        h.Activate();
        h.Drain();
        Assert.False(h.Observer.IsProviderCurrent(h.Manager.Client!, null));
        Assert.False(h.Presentation.IsReady);
        Assert.Equal(SetupNativeChatWarning.None, h.Presentation.Warning);
        Assert.Equal(0, h.Mounts);
        h.Provider = new object();
        h.Observer.Request("provider change");
        h.Drain();
        Assert.True(h.Presentation.IsReady);
        Assert.Equal(Request.Completion.Target.SessionKey, h.MountedTarget);
        Assert.Equal(1, h.Mounts);
    }

    [Fact]
    public void UnexpectedAuthorityAndRendererErrorsAreNotSwallowed()
    {
        var state = new SetupNativeChatPresentation();
        state.Bind(Request);
        Assert.Throws<InvalidOperationException>(() => state.Evaluate(
            () => throw new InvalidOperationException("not a known availability failure"), _ => true, _ => true, () => { }, () => { }));
        var client = DispatchProxy.Create<IOperatorGatewayClient, ClientProxy>();
        Assert.Throws<IOException>(() => state.Evaluate(() => client, _ => true,
            _ => throw new IOException(), () => { }, () => { }));
    }

    [Fact]
    public void IncompleteRendererKeepsWarningAndExactTargetForNextCheck()
    {
        var state = new SetupNativeChatPresentation();
        state.Bind(Request);
        var client = DispatchProxy.Create<IOperatorGatewayClient, ClientProxy>();
        var blocked = false;
        state.Evaluate(() => client, _ => true, _ => false, () => blocked = true, () => { });
        Assert.True(blocked);
        Assert.Equal(SetupNativeChatWarning.RenderingFailed, state.Warning);
        state.Evaluate(() => client, _ => true, target =>
        {
            Assert.Equal(Request.Completion.Target.SessionKey, target);
            return true;
        }, () => Assert.Fail("Successful rendering must not block"), () => { });
        Assert.Equal(SetupNativeChatWarning.None, state.Warning);
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData("endpoint")]
    [InlineData("session")]
    [InlineData("signer")]
    [InlineData("persisted identity")]
    [InlineData("final client race")]
    [InlineData("final state race")]
    public void FullExistingAuthorityChecksStillBlockWithoutRendering(string mismatch)
    {
        using var identity = new IdentityFixture();
        var h = identity.Harness;
        if (mismatch == "gateway") identity.Registry.SetActive(null);
        if (mismatch == "endpoint") identity.Registry.AddOrUpdate(Gateway with { Url = "wss://other.test/" });
        if (mismatch == "session") h.Client.Session = "agent:verified:different";
        if (mismatch == "signer") h.Client.Signer = "another-signer";
        if (mismatch == "persisted identity") File.Delete(identity.IdentityFile);
        if (mismatch.StartsWith("final", StringComparison.Ordinal))
        {
            h.Manager.OnClientRead = count =>
            {
                if (count != 2) return;
                if (mismatch == "final client race")
                    h.Manager.Client = DispatchProxy.Create<IOperatorGatewayClient, ClientProxy>();
                else
                    h.Manager.Snapshot = h.Manager.Snapshot with { OperatorState = RoleConnectionState.Connecting };
            };
        }
        var state = new SetupNativeChatPresentation();
        state.Bind(identity.Request);
        state.Evaluate(() => identity.Request.GetConnectedClient(identity.Registry, h.Connection),
            _ => true, _ => throw new Xunit.Sdk.XunitException("Must not mount"), () => { }, () => { });
        Assert.Equal(SetupNativeChatWarning.AuthorityUnconfirmed, state.Warning);
    }

    [Fact]
    public void PersistedIdentityCanBeRecheckedWithoutAConnectionEventOrReceiptReplay()
    {
        using var identity = new IdentityFixture();
        var savedIdentity = File.ReadAllBytes(identity.IdentityFile);
        File.Delete(identity.IdentityFile);
        var h = identity.Harness;
        h.Request = identity.Request;
        h.RequireOwner = () => identity.Request.GetConnectedClient(identity.Registry, h.Connection);
        h.Activate();
        h.Drain();
        Assert.Equal(SetupNativeChatWarning.AuthorityUnconfirmed, h.Presentation.Warning);
        File.WriteAllBytes(identity.IdentityFile, savedIdentity);
        h.Observer.Request("explicit recheck");
        h.Drain();
        Assert.Equal(SetupNativeChatWarning.None, h.Presentation.Warning);
        Assert.Equal(0, h.Manager.Mutations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VisualRecoveryDoesNotReacquireSettledReceiptButRealHandoffStillConsumes(bool settleUnavailable)
    {
        using var temp = new TempDirectory();
        var h = new Harness();
        var store = new SetupDashboardHandoffStore(temp.Path);
        var handle = store.Issue(h.Request.Completion);
        var opens = 0;
        var launcher = new SetupNativeHandoffLauncher(() => Gateway,
            (_, _) => Task.FromResult(new SetupVerifiedNativeRoute(h.Request.Completion.Verification,
                h.Request.Completion.Target.SessionKey)),
            (_, _) => { opens++; return settleUnavailable
                ? Task.FromException(new SetupNativeConnectionUnavailableException()) : Task.CompletedTask; }, _ => { });
        Assert.Equal(!settleUnavailable, await launcher.OpenAsync(store, handle));
        h.Activate();
        h.SetConnected(false);
        h.Drain();
        h.SetConnected(true);
        h.Observer.Request("explicit recheck");
        h.Drain();
        Assert.Equal(SetupNativeChatWarning.None, h.Presentation.Warning);
        Assert.Equal(1, opens);
        Assert.False(await launcher.OpenAsync(store, handle));
        using var retry = store.Acquire(handle, explicitRetry: true).Lease;
        Assert.Equal(settleUnavailable, retry is not null);
    }

    [Fact]
    public void AvailabilitySubtypePreservesBaseCatchAndOriginalAuthorityOrder()
    {
        Assert.IsAssignableFrom<InvalidOperationException>(Assert.Throws<SetupNativeConnectionUnavailableException>(
            () => Request.GetConnectedClient(null, null)));
        Assert.Throws<SetupNativeConnectionUnavailableException>(
            () => Request.RequireCurrent(Gateway, Gateway.Id, Request.Completion.Target.SessionKey, false));
        Assert.Throws<SetupNativeOwnershipException>(() => Request.RequireCurrent(null, null, null, false));
    }

    private sealed class IdentityFixture : IDisposable
    {
        private readonly TempDirectory _directory = new();
        public Harness Harness { get; } = new();
        public GatewayRegistry Registry { get; }
        public SetupNativeNavigationRequest Request { get; }
        public string IdentityFile { get; }
        public IdentityFixture()
        {
            Registry = new(_directory.Path);
            Registry.AddOrUpdate(Gateway);
            Registry.SetActive(Gateway.Id);
            var path = Registry.GetIdentityDirectory(Gateway.Id);
            var identity = new DeviceIdentity(path);
            identity.Initialize();
            Harness.Client.Signer = identity.DeviceId;
            var request = SetupNativeChatPresentationTests.Request;
            Request = new(request.Completion with
            {
                Verification = request.Completion.Verification with
                { IdentityBinding = SetupCompletionAuthority.CaptureIdentity(path, identity.DeviceId) },
            });
            IdentityFile = Path.Combine(path, "device-key-ed25519.json");
        }
        public void Dispose() => _directory.Dispose();
    }

    private sealed class Harness
    {
        public readonly Queue<Action> Queue = new();
        public readonly List<string> Diagnostics = [];
        public readonly SetupNativeChatPresentation Presentation = new();
        public readonly IGatewayConnectionManager Connection = DispatchProxy.Create<IGatewayConnectionManager, ManagerProxy>();
        public ManagerProxy Manager => (ManagerProxy)Connection;
        public ClientProxy Client => (ClientProxy)Manager.Client!;
        public readonly SetupNativeChatRefresh Observer;
        public SetupNativeNavigationRequest Request = SetupNativeChatPresentationTests.Request;
        public object? Provider = new();
        public object? RenderedProvider;
        public bool AcceptDispatch = true;
        public bool RenderFails;
        public int Checks, Mounts, Disposals;
        public string? MountedTarget, LastRenderTarget;
        public Action? DuringCheck;
        public Func<IOperatorGatewayClient>? RequireOwner;
        public Harness()
        {
            Observer = new(action =>
            {
                if (!AcceptDispatch) return false;
                Queue.Enqueue(action);
                return true;
            }, () => Provider, (_, _, _) => Evaluate(), Diagnostics.Add);
        }
        public void Activate()
        {
            Presentation.Bind(Request);
            Observer.Reconcile(true, Request, Connection);
        }
        public void Clear()
        {
            Presentation.Bind(null);
            Observer.Reconcile(true, null, Connection);
        }
        public void SetConnected(bool connected)
        {
            Client.Connected = connected;
            Manager.Snapshot = Manager.Snapshot with
            { OperatorState = connected ? RoleConnectionState.Connected : RoleConnectionState.Connecting };
            Manager.RaiseState();
        }
        public void Evaluate()
        {
            Observer.Reconcile(true, Request, Connection);
            Presentation.Evaluate(() =>
            {
                Checks++;
                DuringCheck?.Invoke();
                if (RequireOwner is not null) return RequireOwner();
                if (!Client.Connected) throw new SetupNativeConnectionUnavailableException();
                return Manager.Client!;
            }, client => Observer.IsProviderCurrent(client, Provider), target =>
            {
                LastRenderTarget = target;
                if (RenderFails) throw new InvalidOperationException("synthetic renderer failure");
                if (MountedTarget is null || target is not null || !ReferenceEquals(RenderedProvider, Provider)) Mounts++;
                MountedTarget = target ?? MountedTarget;
                RenderedProvider = Provider;
                return true;
            }, () =>
            {
                if (MountedTarget is not null) Disposals++;
                MountedTarget = null;
            }, () => { });
        }
        public void Drain()
        {
            var remaining = 50;
            while (Queue.TryDequeue(out var action))
            {
                Assert.True(--remaining > 0, "Refresh must not poll or spin");
                action();
            }
        }
    }

    public class ClientProxy : DispatchProxy
    {
        public bool Connected = true;
        public string Session = "agent:verified:main";
        public string Signer = "test-signer";
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_IsConnectedToGateway" => Connected,
            "get_MainSessionKey" => Session,
            "get_AuthenticatedSigningDeviceId" => Signer,
            _ => throw new Xunit.Sdk.XunitException("Unexpected client operation: " + method.Name),
        };
    }

    public class ManagerProxy : DispatchProxy
    {
        public IOperatorGatewayClient? Client = DispatchProxy.Create<IOperatorGatewayClient, ClientProxy>();
        public GatewayConnectionSnapshot Snapshot = new() { GatewayId = Gateway.Id, OperatorState = RoleConnectionState.Connected };
        private EventHandler<GatewayConnectionSnapshot>? _state;
        private EventHandler<OperatorClientChangedEventArgs>? _client;
        public int StateHandlers => _state?.GetInvocationList().Length ?? 0;
        public int ClientHandlers => _client?.GetInvocationList().Length ?? 0;
        public int Mutations;
        private int _reads;
        public Action<int>? OnClientRead;
        public void RaiseState() => _state?.Invoke(this, Snapshot);
        public void RaiseClientChanged(IOperatorGatewayClient? client) =>
            _client?.Invoke(this, new() { NewClient = client });
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_CurrentSnapshot": return Snapshot;
                case "get_OperatorClient": OnClientRead?.Invoke(++_reads); return Client;
                case "add_StateChanged": _state += (EventHandler<GatewayConnectionSnapshot>)args![0]!; return null;
                case "remove_StateChanged": _state -= (EventHandler<GatewayConnectionSnapshot>)args![0]!; return null;
                case "add_OperatorClientChanged": _client += (EventHandler<OperatorClientChangedEventArgs>)args![0]!; return null;
                case "remove_OperatorClientChanged": _client -= (EventHandler<OperatorClientChangedEventArgs>)args![0]!; return null;
                default: Mutations++; throw new Xunit.Sdk.XunitException("Unexpected manager operation: " + method.Name);
            }
        }
    }
}
