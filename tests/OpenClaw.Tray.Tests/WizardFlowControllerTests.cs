using System;
using System.Text.Json;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClawTray.Onboarding.Services;
using Xunit;

namespace OpenClaw.Tray.Tests;

public class WizardFlowControllerTests
{
    private sealed class FakeWizardGateway : IWizardGateway
    {
        public bool IsConnectedToGateway { get; set; } = true;
        public event EventHandler<ConnectionStatus>? StatusChanged;

        public Task<JsonElement> SendWizardRequestAsync(string method, object? parameters = null, int timeoutMs = 30000) =>
            throw new NotSupportedException();

        public void Raise(ConnectionStatus status)
        {
            IsConnectedToGateway = status == ConnectionStatus.Connected;
            StatusChanged?.Invoke(this, status);
        }
    }

    private static JsonElement Payload(string sessionId = "session-new")
    {
        using var doc = JsonDocument.Parse("{\"sessionId\":\"" + sessionId + "\",\"step\":{\"type\":\"note\",\"id\":\"step-1\",\"message\":\"Ready\"}}");
        return doc.RootElement.Clone();
    }

    private static async Task<WizardRecoveryResult> RecoverAsync(
        Exception exception,
        FakeWizardGateway gateway,
        WizardRecoveryGuardState guard,
        Func<Task<JsonElement>> startWizardAsync)
    {
        var context = WizardFlowController.CaptureRequestContext(guard);
        return await WizardFlowController.TryRecoverAsync(exception, gateway, guard, context, startWizardAsync);
    }

    [Fact]
    public async Task OperationCanceledException_InvokesWizardStartExactlyOnce()
    {
        var gateway = new FakeWizardGateway();
        var guard = new WizardRecoveryGuardState();
        var starts = 0;

        var result = await RecoverAsync(new OperationCanceledException("lost"), gateway, guard, () =>
        {
            starts++;
            return Task.FromResult(Payload());
        });

        Assert.Equal(WizardRecoveryKind.Recovered, result.Kind);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task WizardNotFound_InvokesWizardStartExactlyOnce()
    {
        var gateway = new FakeWizardGateway();
        var guard = new WizardRecoveryGuardState();
        var starts = 0;

        var result = await RecoverAsync(new InvalidOperationException("wizard not found"), gateway, guard, () =>
        {
            starts++;
            return Task.FromResult(Payload());
        });

        Assert.Equal(WizardRecoveryKind.Recovered, result.Kind);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task SuccessfulRecoveryReset_AllowsSecondIndependentLossToRecover()
    {
        var gateway = new FakeWizardGateway();
        var guard = new WizardRecoveryGuardState();
        var starts = 0;

        var first = await RecoverAsync(new OperationCanceledException("first"), gateway, guard, () =>
        {
            starts++;
            return Task.FromResult(Payload("s1"));
        });
        Assert.Equal(WizardRecoveryKind.Recovered, first.Kind);
        Assert.True(WizardFlowController.IsStartPayload(first.Payload!.Value));
        guard.ResetAfterSuccessfulStart();

        var second = await RecoverAsync(new InvalidOperationException("wizard not running"), gateway, guard, () =>
        {
            starts++;
            return Task.FromResult(Payload("s2"));
        });

        Assert.Equal(WizardRecoveryKind.Recovered, second.Kind);
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task RecoveryFailure_DoesNotLoopOrRetryStartRecursively()
    {
        var gateway = new FakeWizardGateway();
        var guard = new WizardRecoveryGuardState();
        var starts = 0;

        var result = await RecoverAsync(new OperationCanceledException("lost"), gateway, guard, () =>
        {
            starts++;
            throw new InvalidOperationException("gateway unhealthy");
        });

        Assert.Equal(WizardRecoveryKind.Failed, result.Kind);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task RecoveryFailureFollowedByStaleClosure_DoesNotStartAgain_BeforeUserRestart()
    {
        var gateway = new FakeWizardGateway();
        var guard = new WizardRecoveryGuardState();
        var context = WizardFlowController.CaptureRequestContext(guard);
        var starts = 0;
        string? sessionId = "lost-session";
        JsonElement? stepPayload = Payload("lost-session");

        var first = await WizardFlowController.TryRecoverAsync(new OperationCanceledException("lost-a"), gateway, guard, context, () =>
        {
            starts++;
            throw new InvalidOperationException("gateway unhealthy");
        });

        Assert.Equal(WizardRecoveryKind.Failed, first.Kind);
        sessionId = null;
        stepPayload = null;

        var second = await WizardFlowController.TryRecoverAsync(new OperationCanceledException("lost-b"), gateway, guard, context, () =>
        {
            starts++;
            return Task.FromResult(Payload("unexpected-second-start"));
        });

        Assert.Null(sessionId);
        Assert.Null(stepPayload);
        Assert.Equal(WizardRecoveryKind.AlreadyAttempted, second.Kind);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task MissingScopeError_DoesNotTriggerRecovery()
    {
        var gateway = new FakeWizardGateway();
        var guard = new WizardRecoveryGuardState();
        var starts = 0;

        var result = await RecoverAsync(new InvalidOperationException("missing scope: operator.write"), gateway, guard, () =>
        {
            starts++;
            return Task.FromResult(Payload());
        });

        Assert.Equal(WizardRecoveryKind.NotEligible, result.Kind);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task ConcurrentStaleClosures_OnlyOneStartsWizard()
    {
        var gateway = new FakeWizardGateway();
        var guard = new WizardRecoveryGuardState();
        var context = WizardFlowController.CaptureRequestContext(guard);
        var starts = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<JsonElement> StartAsync()
        {
            starts++;
            return gate.Task.ContinueWith(_ => Payload(), TaskScheduler.Default);
        }

        var first = WizardFlowController.TryRecoverAsync(new OperationCanceledException("lost-a"), gateway, guard, context, StartAsync);
        var second = WizardFlowController.TryRecoverAsync(new OperationCanceledException("lost-b"), gateway, guard, context, StartAsync);
        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, starts);
        Assert.Single(results, r => r.Kind == WizardRecoveryKind.Recovered);
        Assert.Single(results, r => r.Kind == WizardRecoveryKind.AlreadyAttempted);
    }

    [Fact]
    public async Task TimeoutWhileConnected_DoesNotTriggerRecovery()
    {
        var gateway = new FakeWizardGateway { IsConnectedToGateway = true };
        var guard = new WizardRecoveryGuardState();
        var starts = 0;

        var result = await RecoverAsync(new TimeoutException("slow step"), gateway, guard, () =>
        {
            starts++;
            return Task.FromResult(Payload());
        });

        Assert.Equal(WizardRecoveryKind.NotEligible, result.Kind);
        Assert.Equal(0, starts);
        Assert.Equal(WizardFlowController.SlowStepRetryMessage, "Setup is taking longer than expected. Retry?");
    }

    [Fact]
    public async Task TimeoutWhileDisconnected_TriggersRecovery()
    {
        var gateway = new FakeWizardGateway { IsConnectedToGateway = false };
        var guard = new WizardRecoveryGuardState();
        var starts = 0;

        var result = await RecoverAsync(new TimeoutException("lost step"), gateway, guard, () =>
        {
            starts++;
            return Task.FromResult(Payload());
        });

        Assert.Equal(WizardRecoveryKind.Recovered, result.Kind);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task TimeoutAfterDisconnectReconnectDuringRequest_TriggersRecovery()
    {
        var gateway = new FakeWizardGateway { IsConnectedToGateway = true };
        var guard = new WizardRecoveryGuardState();
        var context = WizardFlowController.CaptureRequestContext(guard);
        guard.ObserveConnectionStatus(ConnectionStatus.Disconnected);
        gateway.IsConnectedToGateway = true;
        var starts = 0;

        var result = await WizardFlowController.TryRecoverAsync(new TimeoutException("lost step"), gateway, guard, context, () =>
        {
            starts++;
            return Task.FromResult(Payload());
        });

        Assert.Equal(WizardRecoveryKind.Recovered, result.Kind);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task RestartWizardAction_ClearsStateResetsGuardAndStartsFreshWizard()
    {
        var guard = new WizardRecoveryGuardState();
        Assert.True(guard.TryMarkRestartAttempted());
        string? sessionId = "stale-session";
        JsonElement? stepPayload = Payload("stale-session");
        var starts = 0;

        var payload = await WizardFlowController.RestartWizardAsync(
            guard,
            () =>
            {
                sessionId = null;
                stepPayload = null;
            },
            () =>
            {
                starts++;
                return Task.FromResult(Payload("fresh-session"));
            });

        Assert.Null(sessionId);
        Assert.Null(stepPayload);
        Assert.False(guard.HasRestartedForCurrentLostSession);
        Assert.Equal(1, starts);
        Assert.True(WizardFlowController.IsStartPayload(payload));
    }

    // ── TryResumeWithSessionAsync tests ──────────────────────────────────────

    [Fact]
    public async Task TryResumeWithSessionAsync_WhenSessionAlive_CallsNextNotStart()
    {
        var gateway = new FakeWizardGateway { IsConnectedToGateway = true };
        var nextCalled = false;
        var startCalled = false;
        using var doc = JsonDocument.Parse("{\"done\":false,\"step\":{\"id\":\"ch-step\",\"type\":\"select\"}}");
        var channelsPayload = doc.RootElement.Clone();

        var (resumed, returnedPayload) = await WizardFlowController.TryResumeWithSessionAsync(
            "session-alive",
            gateway,
            async sid => { nextCalled = true; return channelsPayload; },
            async () => { startCalled = true; return default; });

        Assert.True(resumed);
        Assert.True(nextCalled);
        Assert.False(startCalled);
        Assert.Equal("ch-step", returnedPayload.GetProperty("step").GetProperty("id").GetString());
    }

    [Fact]
    public async Task TryResumeWithSessionAsync_WhenSessionNotFound_FallsBackToStart()
    {
        var gateway = new FakeWizardGateway { IsConnectedToGateway = true };
        var startPayload = Payload("new-session");

        var (resumed, returnedPayload) = await WizardFlowController.TryResumeWithSessionAsync(
            "session-gone",
            gateway,
            async sid => throw new InvalidOperationException("wizard not found"),
            async () => startPayload);

        Assert.False(resumed);
        Assert.Equal("new-session", returnedPayload.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task TryResumeWithSessionAsync_WhenNoSessionId_FallsBackToStart()
    {
        var gateway = new FakeWizardGateway { IsConnectedToGateway = true };
        var startPayload = Payload("s1");
        var nextCalled = false;

        var (resumed, _) = await WizardFlowController.TryResumeWithSessionAsync(
            null,
            gateway,
            async sid => { nextCalled = true; return default; },
            async () => startPayload);

        Assert.False(resumed);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task TryResumeWithSessionAsync_WhenTimeoutException_FallsBackToStart()
    {
        var gateway = new FakeWizardGateway { IsConnectedToGateway = true };
        var startPayload = Payload("s1");
        var startCalled = false;

        var (resumed, _) = await WizardFlowController.TryResumeWithSessionAsync(
            "session-timeout",
            gateway,
            async sid => throw new TimeoutException("resume timed out"),
            async () => { startCalled = true; return startPayload; });

        Assert.False(resumed);
        Assert.True(startCalled);
    }

    [Fact]
    public async Task TryResumeWithSessionAsync_WhenDisconnected_FallsBackToStart()
    {
        var gateway = new FakeWizardGateway { IsConnectedToGateway = false };
        var startPayload = Payload("s1");
        var nextCalled = false;
        var startCalled = false;

        var (resumed, _) = await WizardFlowController.TryResumeWithSessionAsync(
            "session-alive",
            gateway,
            async sid => { nextCalled = true; return default; },
            async () => { startCalled = true; return startPayload; });

        Assert.False(resumed);
        Assert.False(nextCalled);
        Assert.True(startCalled);
    }

    // ── WaitForConnectionAsync tests ────────────────────────────────────────────

    [Fact]
    public async Task WaitForConnectionAsync_WhenAlreadyConnected_ReturnsTrueImmediately()
    {
        var gateway = new FakeWizardGateway { IsConnectedToGateway = true };
        var pollCount = 0;

        var result = await WizardFlowController.WaitForConnectionAsync(
            gateway, maxPollCount: 30, delayAsync: () => { pollCount++; return Task.CompletedTask; });

        Assert.True(result);
        Assert.Equal(0, pollCount); // no polling needed
    }

    [Fact]
    public async Task WaitForConnectionAsync_WhenReconnectsAfterTwoPolls_ReturnsTrueAndCallsNextNotStart()
    {
        // Simulates: disconnected → reconnects after 2 polls → TryResumeWithSessionAsync calls wizard.next
        var gateway = new FakeWizardGateway { IsConnectedToGateway = false };
        var pollCount = 0;
        var nextCalled = false;
        var startCalled = false;

        using var doc = JsonDocument.Parse("{\"done\":false,\"step\":{\"id\":\"ch-step\",\"type\":\"select\"}}");
        var channelsPayload = doc.RootElement.Clone();
        var startPayload = Payload("new-session");

        var result = await WizardFlowController.WaitForConnectionAsync(
            gateway, maxPollCount: 30, delayAsync: () =>
            {
                pollCount++;
                if (pollCount >= 2) gateway.IsConnectedToGateway = true; // reconnect after 2 polls
                return Task.CompletedTask;
            });

        Assert.True(result);
        Assert.Equal(2, pollCount);

        // After wait, TryResumeWithSessionAsync should now see connected=True and call wizard.next
        var (resumed, _) = await WizardFlowController.TryResumeWithSessionAsync(
            "session-alive",
            gateway,
            async sid => { nextCalled = true; return channelsPayload; },
            async () => { startCalled = true; return startPayload; });

        Assert.True(resumed);
        Assert.True(nextCalled);
        Assert.False(startCalled);
    }

    [Fact]
    public async Task WaitForConnectionAsync_WhenTimesOut_ReturnsFalse()
    {
        var gateway = new FakeWizardGateway { IsConnectedToGateway = false };
        var pollCount = 0;

        var result = await WizardFlowController.WaitForConnectionAsync(
            gateway, maxPollCount: 5, delayAsync: () => { pollCount++; return Task.CompletedTask; });

        Assert.False(result);
        Assert.Equal(5, pollCount);
    }

    [Fact]
    public async Task WaitForConnectionAsync_WhenCancelledDuringPolling_ThrowsOperationCanceledException()
    {
        var gateway = new FakeWizardGateway { IsConnectedToGateway = false };
        using var cts = new System.Threading.CancellationTokenSource();
        var pollCount = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WizardFlowController.WaitForConnectionAsync(
                gateway,
                maxPollCount: 30,
                delayAsync: () =>
                {
                    pollCount++;
                    if (pollCount >= 2)
                        cts.Cancel();
                    return Task.CompletedTask;
                },
                cancellationToken: cts.Token));

        Assert.True(pollCount >= 2);
    }
}
