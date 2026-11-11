// Exercises Inbox invalidation through the real authenticated fixture socket, not injected list events.
// Run with dotnet test --filter FullyQualifiedName~InboxGatewaySourceWireTests.
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class InboxGatewaySourceWireTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private const string FailureId = "gateway:cronFailed:fixture-inbox-cron";

    [Fact]
    public async Task CronWireEventsRefreshFailuresRecoverySchedulerRemovalAndReconnect()
    {
        var scenario = GatewayScenario.CreateInboxFeed();
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var server = await FixtureGatewayServer.StartAsync(scenario, token);
        using var identity = new TempDirectory("inbox-wire-");
        using var client = Client(server, token, identity);
        await ConnectAsync(client);
        var ui = new SerializedDispatcher();
        var source = new InboxGatewaySource(ui, useTimer: false);
        try
        {
            ui.Invoke(() => source.AttachClient(client));
            await WaitForAsync(ui, source, () => source.Items.Any(item => item.Id == FailureId));

            scenario.SetInboxAutomationState(failed: false);
            await server.PublishCronChangedAsync();
            await WaitForAsync(ui, source, () => source.Items.All(item => item.Id != FailureId));

            scenario.SetInboxAutomationState(failed: false, overdue: true);
            await server.PublishCronChangedAsync();
            await WaitForAsync(ui, source, () => source.Items.Any(item => item.Kind == InboxGatewayItemKind.AutomationOverdue));
            scenario.CronSchedulerEnabled = false;
            await server.PublishCronChangedAsync();
            await WaitForAsync(ui, source, () => source.Items.All(item => item.Kind != InboxGatewayItemKind.AutomationOverdue));

            scenario.SetInboxAutomationState(failed: true);
            await server.PublishCronChangedAsync();
            await WaitForAsync(ui, source, () => source.Items.Any(item => item.Id == FailureId));
            await client.DisconnectAsync();
            await WaitForAsync(ui, source, () => source.Items.Count == 0);
            scenario.SetInboxAutomationState(failed: false, overdue: true);
            scenario.CronSchedulerEnabled = true;
            await ConnectAsync(client);
            await WaitForAsync(ui, source, () => source.Items.Any(item => item.Kind == InboxGatewayItemKind.AutomationOverdue));
            Assert.DoesNotContain(source.Items, item => item.Id == FailureId);

            scenario.SetInboxAutomationState(failed: false, removed: true);
            await server.PublishCronChangedAsync();
            await WaitForAsync(ui, source, () => source.Items.All(item => item.Kind is not
                (InboxGatewayItemKind.AutomationFailed or InboxGatewayItemKind.AutomationOverdue)));
            Assert.All(server.Requests.Where(request => request.Method is "cron.list" or "cron.status" && request.Outcome != "pending"),
                request => Assert.Equal("ok", request.Outcome));
            Assert.Empty(server.UnexpectedRequests);
        }
        finally
        {
            ui.Invoke(source.Dispose);
            await client.DisconnectAsync();
        }
    }

    [Fact]
    public async Task ReplacedClientCronEventsCannotRefreshOrChangeTheCurrentInbox()
    {
        var oldScenario = GatewayScenario.CreateInboxFeed();
        var currentScenario = GatewayScenario.CreateInboxFeed();
        currentScenario.SetInboxAutomationState(failed: false);
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var oldServer = await FixtureGatewayServer.StartAsync(oldScenario, token);
        await using var currentServer = await FixtureGatewayServer.StartAsync(currentScenario, token);
        using var oldIdentity = new TempDirectory("inbox-wire-old-");
        using var currentIdentity = new TempDirectory("inbox-wire-current-");
        using var old = Client(oldServer, token, oldIdentity);
        using var current = Client(currentServer, token, currentIdentity);
        await ConnectAsync(old);
        await ConnectAsync(current);
        var ui = new SerializedDispatcher();
        var source = new InboxGatewaySource(ui, useTimer: false);
        try
        {
            ui.Invoke(() => source.AttachClient(old));
            await WaitForAsync(ui, source, () => source.Items.Any(item => item.Id == FailureId));
            ui.Invoke(() => source.AttachClient(current));
            await WaitForAsync(ui, source, () => source.Items.Any(item => item.Kind == InboxGatewayItemKind.ModelAuth)
                && source.Items.All(item => item.Id != FailureId));
            var oldReads = oldServer.Requests.Count(request => request.Method == "cron.list");
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            old.CronEventReceived += (_, _) => observed.TrySetResult();
            await oldServer.PublishCronChangedAsync();
            await observed.Task.WaitAsync(Deadline);
            ui.Invoke(() => Assert.DoesNotContain(source.Items, item => item.Id == FailureId));
            Assert.Equal(oldReads, oldServer.Requests.Count(request => request.Method == "cron.list"));

            currentScenario.SetInboxAutomationState(failed: true);
            await currentServer.PublishCronChangedAsync();
            await WaitForAsync(ui, source, () => source.Items.Any(item => item.Id == FailureId));
        }
        finally
        {
            ui.Invoke(source.Dispose);
            await old.DisconnectAsync();
            await current.DisconnectAsync();
        }
    }

    private static OpenClawGatewayClient Client(FixtureGatewayServer server, string token, TempDirectory identity) =>
        new(server.Endpoint.AbsoluteUri, token, NullLogger.Instance, identityPath: identity.Path,
            ignoreStoredDeviceToken: true, persistHandshakeDeviceTokens: false);

    private static async Task ConnectAsync(OpenClawGatewayClient client)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnHandshake(object? sender, EventArgs args) => ready.TrySetResult();
        client.HandshakeSucceeded += OnHandshake;
        try
        {
            await client.ConnectAsync();
            await ready.Task.WaitAsync(Deadline);
        }
        finally { client.HandshakeSucceeded -= OnHandshake; }
    }

    private static async Task WaitForAsync(SerializedDispatcher ui, InboxGatewaySource source, Func<bool> predicate)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, EventArgs args)
        {
            if (predicate()) ready.TrySetResult();
        }
        ui.Invoke(() => { source.Changed += Check; Check(null, EventArgs.Empty); });
        try { await ready.Task.WaitAsync(Deadline); }
        finally { ui.Invoke(() => source.Changed -= Check); }
    }

    // Serializes the owner's dispatcher callbacks and test actions without requiring a native desktop.
    private sealed class SerializedDispatcher : IUiDispatcher
    {
        private readonly object _sync = new();
        public bool HasThreadAccess => Monitor.IsEntered(_sync);
        public bool TryEnqueue(Action action) { Invoke(action); return true; }
        public void Invoke(Action action) { lock (_sync) action(); }
    }
}
