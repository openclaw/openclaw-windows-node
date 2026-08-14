using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests;

[SupportedOSPlatform("windows")]
public class ActivationRouterTests
{
    private const string Scheme = "openclaw";

    private static string UniquePipeName() =>
        DeepLinkSecurityPolicy.BuildPipeName(Guid.NewGuid().ToString("N"), "test-user", 0);

    private static ActivationRouter CreateRouter() => new(Scheme, UniquePipeName());

    private sealed class FakeSink : IActivationPlanSink
    {
        public List<ActivationRoute> Dispatched { get; } = new();
        public List<ActivationConfirmation> Confirmations { get; } = new();
        public bool ConfirmResult { get; set; } = true;

        public Task DispatchAsync(ActivationRoute route, CancellationToken cancellationToken)
        {
            Dispatched.Add(route);
            return Task.CompletedTask;
        }

        public Task<bool> ConfirmAsync(ActivationConfirmation confirmation, CancellationToken cancellationToken)
        {
            Confirmations.Add(confirmation);
            return Task.FromResult(ConfirmResult);
        }
    }

    private sealed class BlockingConfirmationSink : IActivationPlanSink
    {
        private readonly TaskCompletionSource<bool> _releaseConfirmation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ConfirmationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ActivationRoute> Dispatched { get; } = new();

        public Task DispatchAsync(ActivationRoute route, CancellationToken cancellationToken)
        {
            Dispatched.Add(route);
            return Task.CompletedTask;
        }

        public async Task<bool> ConfirmAsync(
            ActivationConfirmation confirmation,
            CancellationToken cancellationToken)
        {
            ConfirmationStarted.TrySetResult(true);
            return await _releaseConfirmation.Task.WaitAsync(cancellationToken);
        }

        public void ReleaseConfirmation(bool result) => _releaseConfirmation.TrySetResult(result);
    }

    private static LaunchActivationInput Input(
        string? protocolUri = null,
        IReadOnlyList<string>? args = null,
        string? postSetupLaunch = null,
        bool setupShown = false) =>
        new(protocolUri, args ?? Array.Empty<string>(), postSetupLaunch, setupShown);

    #region PlanLaunch precedence

    [Fact]
    public void PlanLaunch_ProtocolUriTakesPrecedenceOverCommandLineAndPostSetup()
    {
        var router = CreateRouter();
        var plan = router.PlanLaunch(Input(
            protocolUri: $"{Scheme}://settings",
            args: new[] { "app.exe", $"{Scheme}://dashboard" },
            postSetupLaunch: "chat"));

        var dispatch = Assert.IsType<ActivationPlan.Dispatch>(plan);
        Assert.IsType<ActivationRoute.OpenHub>(dispatch.Route);
        Assert.Equal("settings", ((ActivationRoute.OpenHub)dispatch.Route).Page);
    }

    [Fact]
    public void PlanLaunch_CommandLineArgUsedWhenNoProtocolUri()
    {
        var router = CreateRouter();
        var plan = router.PlanLaunch(Input(
            args: new[] { "app.exe", $"{Scheme}://dashboard" },
            postSetupLaunch: "chat"));

        var dispatch = Assert.IsType<ActivationPlan.Dispatch>(plan);
        Assert.IsType<ActivationRoute.OpenDashboard>(dispatch.Route);
    }

    [Fact]
    public void PlanLaunch_IgnoresNonDeepLinkCommandLineArg()
    {
        var router = CreateRouter();
        var plan = router.PlanLaunch(Input(
            args: new[] { "app.exe", "--post-setup-restart" },
            postSetupLaunch: "chat"));

        var dispatch = Assert.IsType<ActivationPlan.Dispatch>(plan);
        var route = Assert.IsType<ActivationRoute.OpenHub>(dispatch.Route);
        Assert.Equal("chat", route.Page);
    }

    [Fact]
    public void PlanLaunch_PostSetupChatFallback_WhenNoOtherCandidate()
    {
        var router = CreateRouter();
        var plan = router.PlanLaunch(Input(postSetupLaunch: "chat"));

        var dispatch = Assert.IsType<ActivationPlan.Dispatch>(plan);
        var route = Assert.IsType<ActivationRoute.OpenHub>(dispatch.Route);
        Assert.Equal("chat", route.Page);
    }

    [Fact]
    public void PlanLaunch_ReturnsIgnore_WhenNoCandidatePresent()
    {
        var router = CreateRouter();
        var plan = router.PlanLaunch(Input());
        Assert.IsType<ActivationPlan.Ignore>(plan);
    }

    [Fact]
    public void PlanLaunch_ReturnsIgnore_WhenSetupShownDuringStartup_EvenWithCandidate()
    {
        var router = CreateRouter();
        var plan = router.PlanLaunch(Input(protocolUri: $"{Scheme}://settings", setupShown: true));
        Assert.IsType<ActivationPlan.Ignore>(plan);
    }

    [Fact]
    public void PlanLaunch_InvalidUri_ReturnsIgnore()
    {
        var router = CreateRouter();
        var plan = router.PlanLaunch(Input(protocolUri: "not-a-uri"));
        Assert.IsType<ActivationPlan.Ignore>(plan);
    }

    [Fact]
    public void PlanLaunch_StateChangingRoute_ReturnsConfirmWithRedactedPrompt()
    {
        var router = CreateRouter();
        var plan = router.PlanLaunch(Input(protocolUri: $"{Scheme}://agent?message=secret-value"));

        var confirm = Assert.IsType<ActivationPlan.Confirm>(plan);
        Assert.IsType<ActivationRoute.SendMessage>(confirm.Route);
        Assert.Equal("send a message to the agent", confirm.Prompt.ActionDisplayName);
        Assert.DoesNotContain("secret-value", confirm.Prompt.RedactedInput);
    }

    [Fact]
    public void ResolveLaunchCandidate_MatchesPlanLaunchPrecedence()
    {
        var router = CreateRouter();
        var input = Input(
            args: new[] { "app.exe", $"{Scheme}://dashboard" },
            postSetupLaunch: "chat");

        Assert.Equal($"{Scheme}://dashboard", router.ResolveLaunchCandidate(input));
    }

    [Fact]
    public void ResolveLaunchCandidate_IgnoresSetupShownFlag()
    {
        // Unlike PlanLaunch, candidate resolution alone does not gate on setup-shown; the
        // secondary-instance forwarding path needs the raw candidate regardless.
        var router = CreateRouter();
        var input = Input(protocolUri: $"{Scheme}://settings", setupShown: true);
        Assert.Equal($"{Scheme}://settings", router.ResolveLaunchCandidate(input));
    }

    #endregion

    #region PlanToast

    [Fact]
    public void PlanToast_OpenChat_CarriesSessionKey()
    {
        var router = CreateRouter();
        var plan = router.PlanToast("action=open_chat;sessionKey=abc123");
        var dispatch = Assert.IsType<ActivationPlan.Dispatch>(plan);
        var route = Assert.IsType<ActivationRoute.OpenChat>(dispatch.Route);
        Assert.Equal("abc123", route.SessionKey);
    }

    [Fact]
    public void PlanToast_OpenUrl_CarriesUrl()
    {
        var router = CreateRouter();
        var plan = router.PlanToast("action=open_url;url=https://example.com");
        var dispatch = Assert.IsType<ActivationPlan.Dispatch>(plan);
        var route = Assert.IsType<ActivationRoute.OpenUrl>(dispatch.Route);
        Assert.Equal("https://example.com", route.Uri);
    }

    [Fact]
    public void PlanToast_UnknownAction_ReturnsIgnore()
    {
        var router = CreateRouter();
        var plan = router.PlanToast("action=not_a_real_action");
        Assert.IsType<ActivationPlan.Ignore>(plan);
    }

    [Fact]
    public void PlanToast_NullArgument_ReturnsIgnore()
    {
        var router = CreateRouter();
        var plan = router.PlanToast(null);
        Assert.IsType<ActivationPlan.Ignore>(plan);
    }

    #endregion

    #region DispatchPlanAsync

    [Fact]
    public async Task DispatchPlanAsync_Dispatch_CallsSinkDispatchOnly()
    {
        var router = CreateRouter();
        var sink = new FakeSink();
        var route = new ActivationRoute.OpenSetup();

        var applied = await router.DispatchPlanAsync(new ActivationPlan.Dispatch(route), sink, CancellationToken.None);

        Assert.True(applied);
        Assert.Single(sink.Dispatched);
        Assert.Empty(sink.Confirmations);
    }

    [Fact]
    public async Task DispatchPlanAsync_ConfirmAllowed_DispatchesAfterConfirmation()
    {
        var router = CreateRouter();
        var sink = new FakeSink { ConfirmResult = true };
        var route = new ActivationRoute.RestartSshTunnel();
        var prompt = new ActivationConfirmation("restart the SSH tunnel", "redacted");

        var applied = await router.DispatchPlanAsync(new ActivationPlan.Confirm(route, prompt), sink, CancellationToken.None);

        Assert.True(applied);
        Assert.Single(sink.Confirmations);
        Assert.Single(sink.Dispatched);
    }

    [Fact]
    public async Task DispatchPlanAsync_ConfirmDenied_NeverDispatches()
    {
        var router = CreateRouter();
        var sink = new FakeSink { ConfirmResult = false };
        var route = new ActivationRoute.RestartSshTunnel();
        var prompt = new ActivationConfirmation("restart the SSH tunnel", "redacted");

        var applied = await router.DispatchPlanAsync(new ActivationPlan.Confirm(route, prompt), sink, CancellationToken.None);

        Assert.False(applied);
        Assert.Single(sink.Confirmations);
        Assert.Empty(sink.Dispatched);
    }

    [Fact]
    public async Task DispatchPlanAsync_Ignore_NeverDispatchesOrConfirms()
    {
        var router = CreateRouter();
        var sink = new FakeSink();

        var applied = await router.DispatchPlanAsync(new ActivationPlan.Ignore(), sink, CancellationToken.None);

        Assert.False(applied);
        Assert.Empty(sink.Dispatched);
        Assert.Empty(sink.Confirmations);
    }

    [Fact]
    public async Task DispatchPlanAsync_AfterStop_IsRejectedWithoutCallingSink()
    {
        var router = CreateRouter();
        var sink = new FakeSink();
        await router.StopAsync();

        var applied = await router.DispatchPlanAsync(
            new ActivationPlan.Dispatch(new ActivationRoute.OpenSetup()),
            sink,
            CancellationToken.None);

        Assert.False(applied);
        Assert.Empty(sink.Dispatched);
        Assert.Empty(sink.Confirmations);
    }

    [Fact]
    public async Task StopAsync_CancelsAndDrainsInFlightDirectConfirmation()
    {
        var router = CreateRouter();
        var sink = new BlockingConfirmationSink();
        var dispatchTask = router.DispatchPlanAsync(
            new ActivationPlan.Confirm(
                new ActivationRoute.RestartSshTunnel(),
                new ActivationConfirmation("restart the SSH tunnel", "redacted")),
            sink,
            CancellationToken.None);
        await sink.ConfirmationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await router.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(dispatchTask.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dispatchTask);
        sink.ReleaseConfirmation(true);
        Assert.Empty(sink.Dispatched);
    }

    #endregion

    #region Forwarded activation IPC

    [Fact]
    public async Task ForwardThenListen_DispatchesRouteFromForwardedDeepLink()
    {
        var pipeName = UniquePipeName();
        await using var listener = new ActivationRouter(Scheme, pipeName);
        var forwarder = new ActivationRouter(Scheme, pipeName);
        var sink = new FakeSink();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await listener.StartForwardedActivationListenerAsync(sink, cts.Token);

        var sent = false;
        for (var attempt = 0; attempt < 20 && !sent; attempt++)
        {
            sent = await forwarder.ForwardToPrimaryAsync($"{Scheme}://settings", cts.Token);
            if (!sent)
                await Task.Delay(50, cts.Token);
        }

        Assert.True(sent);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (sink.Dispatched.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(25, cts.Token);

        var route = Assert.Single(sink.Dispatched);
        var hub = Assert.IsType<ActivationRoute.OpenHub>(route);
        Assert.Equal("settings", hub.Page);
    }

    [Fact]
    public async Task ForwardToPrimaryAsync_RejectsInvalidUriBeforeConnecting()
    {
        var forwarder = CreateRouter();
        var result = await forwarder.ForwardToPrimaryAsync("not-a-valid-uri", CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task ForwardToPrimaryAsync_RejectsOversizedPayloadBeforeConnecting()
    {
        var forwarder = CreateRouter();
        var oversized = $"{Scheme}://send?message=" + new string('a', 9000);
        var result = await forwarder.ForwardToPrimaryAsync(oversized, CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task StopAsync_IsIdempotent()
    {
        var router = CreateRouter();
        var sink = new FakeSink();
        using var cts = new CancellationTokenSource();
        await router.StartForwardedActivationListenerAsync(sink, cts.Token);

        await router.StopAsync();
        await router.StopAsync();
    }

    [Fact]
    public async Task Listener_AcceptsNextForwardWhileConfirmationIsPending()
    {
        var pipeName = UniquePipeName();
        await using var listener = new ActivationRouter(Scheme, pipeName);
        var forwarder = new ActivationRouter(Scheme, pipeName);
        var sink = new BlockingConfirmationSink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await listener.StartForwardedActivationListenerAsync(sink, cts.Token);

        Assert.True(await ForwardWithRetryAsync(
            forwarder,
            $"{Scheme}://agent?message=hello",
            cts.Token));
        await sink.ConfirmationStarted.Task.WaitAsync(cts.Token);

        Assert.True(await ForwardWithRetryAsync(
            forwarder,
            $"{Scheme}://settings",
            cts.Token));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!sink.Dispatched.Exists(route => route is ActivationRoute.OpenHub { Page: "settings" }) &&
               DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, cts.Token);
        }

        Assert.Contains(sink.Dispatched, route => route is ActivationRoute.OpenHub { Page: "settings" });
        sink.ReleaseConfirmation(false);
    }

    [Fact]
    public async Task StopAsync_CancelsPendingConfirmationAndPreventsLateDispatch()
    {
        var pipeName = UniquePipeName();
        var listener = new ActivationRouter(Scheme, pipeName);
        var forwarder = new ActivationRouter(Scheme, pipeName);
        var sink = new BlockingConfirmationSink();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await listener.StartForwardedActivationListenerAsync(sink, cts.Token);

        Assert.True(await ForwardWithRetryAsync(
            forwarder,
            $"{Scheme}://agent?message=hello",
            cts.Token));
        await sink.ConfirmationStarted.Task.WaitAsync(cts.Token);

        await listener.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        sink.ReleaseConfirmation(true);
        Assert.DoesNotContain(sink.Dispatched, route => route is ActivationRoute.SendMessage);
        await listener.DisposeAsync();
    }

    private static async Task<bool> ForwardWithRetryAsync(
        ActivationRouter forwarder,
        string uri,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await forwarder.ForwardToPrimaryAsync(uri, cancellationToken))
                return true;
            await Task.Delay(50, cancellationToken);
        }

        return false;
    }

    #endregion
}
