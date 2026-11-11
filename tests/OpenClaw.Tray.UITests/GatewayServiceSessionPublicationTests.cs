// Exercises GatewayService session publication through its real native dispatcher.
// Run: dotnet test tests/OpenClaw.Tray.UITests --filter FullyQualifiedName~GatewayServiceSessionPublicationTests
using System.Reflection;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.UITests;

/// <summary>Protects the existing session array and preview cache from queued stale callbacks.</summary>
[Collection(UICollection.Name)]
public sealed class GatewayServiceSessionPublicationTests(UIThreadFixture ui)
{
    private static readonly TimeSpan DispatchDeadline = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_UI_DISPATCH_TEST_TIMEOUT_SECONDS"), out var seconds) && seconds > 0
            ? seconds : 10);

    public class GatewayProxy : DispatchProxy
    {
        private readonly Dictionary<string, Delegate?> _handlers = new();
        public long? Epoch = 7;
        public bool Connected = true;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            if (name.StartsWith("add_", StringComparison.Ordinal) || name.StartsWith("remove_", StringComparison.Ordinal))
            {
                var key = name[(name.IndexOf('_') + 1)..];
                _handlers.TryGetValue(key, out var handler);
                _handlers[key] = name.StartsWith("add_", StringComparison.Ordinal)
                    ? Delegate.Combine(handler, (Delegate)args![0]!)
                    : Delegate.Remove(handler, (Delegate)args![0]!);
                return null;
            }
            return name switch
            {
                "get_SessionMutationConnectionEpoch" => Epoch,
                "get_IsConnectedToGateway" => Connected,
                "get_IsAgentsListUnsupported" => false,
                "RequestSessionPreviewAsync" => Task.CompletedTask,
                _ => throw new NotSupportedException(name),
            };
        }

        public void RaiseSessions(SessionInfo[] rows) =>
            ((EventHandler<SessionInfo[]>?)_handlers.GetValueOrDefault("SessionsUpdated"))?.Invoke(this, rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentPublication_NotifiesOnlySessionsAndPrunesPreviews(bool legacyEpoch)
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        var wire = (GatewayProxy)client;
        if (legacyEpoch) wire.Epoch = null;
        AppState state = null!;
        GatewayService service = null!;
        var notifications = new List<string?>();
        SessionInfo[] rows = [new() { Key = "agent:main:current", Status = "queued" }];
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                state = new AppState(ui.Dispatcher);
                service = new GatewayService(state, ui.Dispatcher);
                service.AttachClient(client, null);
                state.SetSessionPreview("agent:main:current", new SessionPreviewInfo());
                state.SetSessionPreview("agent:main:removed", new SessionPreviewInfo());
                state.PropertyChanged += (_, args) =>
                {
                    notifications.Add(args.PropertyName);
                    Assert.True(ui.Dispatcher.HasThreadAccess);
                    Assert.Same(rows, state.Sessions);
                };
                QueueWhileDispatcherIsHeld(wire, rows);
                Assert.Empty(state.Sessions);
            });
            // The fixture queues this behind the production callback. No timing sleep or mock dispatcher.
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(rows, state.Sessions);
                Assert.Equal(new[] { nameof(AppState.Sessions) }, notifications);
                Assert.NotNull(state.GetSessionPreview("agent:main:current"));
                Assert.Null(state.GetSessionPreview("agent:main:removed"));
            });
        }
        finally
        {
            if (service is not null) await ui.RunOnUIAsync(() => service.AttachClient(null, client));
        }
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("reattach")]
    [InlineData("reconnect")]
    [InlineData("disconnect")]
    [InlineData("epoch-unavailable")]
    public async Task ChangedConnectionBeforeDispatch_PreservesRowsAndPreviews(string change)
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
        var wire = (GatewayProxy)client;
        IOperatorGatewayClient current = client;
        AppState state = null!;
        GatewayService service = null!;
        SessionInfo[] retained = [new() { Key = "agent:main:retained" }];
        var sessionNotifications = 0;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                state = new AppState(ui.Dispatcher) { Sessions = retained };
                state.SetSessionPreview("agent:main:retained", new SessionPreviewInfo());
                service = new GatewayService(state, ui.Dispatcher);
                service.AttachClient(client, null);
                state.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(AppState.Sessions)) sessionNotifications++;
                };
                QueueWhileDispatcherIsHeld(wire, []);
                switch (change)
                {
                    case "replacement":
                        current = DispatchProxy.Create<IOperatorGatewayClient, GatewayProxy>();
                        service.AttachClient(current, client);
                        break;
                    case "reattach": service.AttachClient(client, client); break;
                    case "reconnect": wire.Epoch++; break;
                    case "disconnect": wire.Connected = false; break;
                    case "epoch-unavailable": wire.Epoch = null; break;
                }
            });
            await ui.RunOnUIAsync(() =>
            {
                Assert.Same(retained, state.Sessions);
                Assert.NotNull(state.GetSessionPreview("agent:main:retained"));
                Assert.Equal(0, sessionNotifications);
                // Positive control: the current connection still publishes and prunes normally.
                var live = (GatewayProxy)current;
                live.Connected = true;
                live.Epoch = 9;
                live.RaiseSessions([]);
                Assert.Empty(state.Sessions);
                Assert.Null(state.GetSessionPreview("agent:main:retained"));
                Assert.Equal(1, sessionNotifications);
            });
        }
        finally
        {
            if (service is not null) await ui.RunOnUIAsync(() => service.AttachClient(null, current));
        }
    }

    private static void QueueWhileDispatcherIsHeld(GatewayProxy wire, SessionInfo[] rows)
    {
        // Only synchronous event delivery runs on the worker. It enqueues publication and
        // returns without waiting for the UI, so holding the dispatcher establishes ordering.
        Task.Run(() => wire.RaiseSessions(rows)).WaitAsync(DispatchDeadline).GetAwaiter().GetResult();
    }
}
