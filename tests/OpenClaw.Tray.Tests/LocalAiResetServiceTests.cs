using System.Diagnostics;
using OpenClaw.Connection.LocalAi;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class LocalAiResetServiceTests
{
    [Fact]
    public async Task Reset_StopsAndReleasesOwnershipBeforeEngineDeletesFiles()
    {
        var order = new List<string>();
        var runtime = new OrderedRuntime(order) { HasReleasableOwnership = true };
        ProcessStartInfo? started = null;
        var service = new LocalAiResetService(runtime, (startInfo, _) =>
        {
            order.Add("engine");
            started = startInfo;
            File.WriteAllText(JsonOutputPath(startInfo), """{"outcome":"Success"}""");
            return Task.FromResult(0);
        }, () => "OpenClaw.Tray.WinUI.exe");

        LocalAiResetResult result = await service.ResetAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(["stop", "release", "engine"], order);
        Assert.Equal(["--reset-local-ai", "--confirm-destructive", "--json-output"], started!.ArgumentList.Take(3));
        Assert.False(File.Exists(JsonOutputPath(started)));
    }

    [Fact]
    public async Task Reset_UnconfirmedOwnershipRelease_NeverRunsEngine()
    {
        var order = new List<string>();
        var runtime = new OrderedRuntime(order)
        {
            HasReleasableOwnership = true,
            ReleaseFailure = new InvalidOperationException("The original Gateway has not confirmed withdrawal."),
        };
        var service = new LocalAiResetService(runtime, (_, _) =>
        {
            order.Add("engine");
            return Task.FromResult(0);
        }, () => "OpenClaw.Tray.WinUI.exe");

        LocalAiResetResult result = await service.ResetAsync();

        Assert.Equal(LocalAiResetOutcome.Failed, result.Outcome);
        Assert.Contains("not confirmed withdrawal", result.Message);
        Assert.DoesNotContain("engine", order);
    }

    [Fact]
    public async Task Reset_WithoutNativeOwnership_SkipsRelease()
    {
        var order = new List<string>();
        var service = new LocalAiResetService(new OrderedRuntime(order), (startInfo, _) =>
        {
            order.Add("engine");
            return Task.FromResult(0);
        }, () => "OpenClaw.Tray.WinUI.exe");

        await service.ResetAsync();

        Assert.Equal(["stop", "engine"], order);
    }

    [Fact]
    public async Task Reset_EngineFailure_ReportsEngineMessage()
    {
        var service = new LocalAiResetService(new OrderedRuntime([]), (startInfo, _) =>
        {
            File.WriteAllText(JsonOutputPath(startInfo),
                """{"outcome":"Failed","message":"Local AI gateway settings changed after setup."}""");
            return Task.FromResult(1);
        }, () => "OpenClaw.Tray.WinUI.exe");

        LocalAiResetResult result = await service.ResetAsync();

        Assert.Equal(LocalAiResetOutcome.Failed, result.Outcome);
        Assert.Equal("Local AI gateway settings changed after setup.", result.Message);
    }

    [Fact]
    public async Task Reset_EngineExitWithoutJson_IsFailureWithExitCode()
    {
        var service = new LocalAiResetService(new OrderedRuntime([]), (_, _) => Task.FromResult(2),
            () => "OpenClaw.Tray.WinUI.exe");

        LocalAiResetResult result = await service.ResetAsync();

        Assert.Equal(LocalAiResetOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task Reset_Cancelled_WaitsForEngineToStopBeforeReturning()
    {
        var order = new List<string>();
        var engineStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new LocalAiResetService(new OrderedRuntime(order), async (_, ct) =>
        {
            engineStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // Simulates the kill-and-drain that must finish before the reset reports.
                await Task.Delay(100, CancellationToken.None);
                lock (order) order.Add("engine-exited");
                throw;
            }
            return 0;
        }, () => "OpenClaw.Tray.WinUI.exe");
        using var cts = new CancellationTokenSource();

        Task<LocalAiResetResult> reset = service.ResetAsync(cts.Token);
        await engineStarted.Task;
        cts.Cancel();
        LocalAiResetResult result = await reset;

        bool engineExitedBeforeReturn;
        lock (order) engineExitedBeforeReturn = order.Contains("engine-exited");
        Assert.True(engineExitedBeforeReturn);
        Assert.Equal(LocalAiResetOutcome.Cancelled, result.Outcome);
    }

    [Fact]
    public async Task Reset_EngineTimeout_StopsEngineAndReportsTimeout()
    {
        var order = new List<string>();
        var service = new LocalAiResetService(new OrderedRuntime(order), async (_, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                lock (order) order.Add("engine-stopped");
                throw;
            }
            return 0;
        }, () => "OpenClaw.Tray.WinUI.exe", timeout: TimeSpan.FromMilliseconds(100));

        LocalAiResetResult result = await service.ResetAsync();

        Assert.Equal(LocalAiResetOutcome.Failed, result.Outcome);
        Assert.Contains("timed out after", result.Message);
        bool engineStopped;
        lock (order) engineStopped = order.Contains("engine-stopped");
        Assert.True(engineStopped);
    }

    [Fact]
    public async Task Reset_UnconfirmedStop_NeverRunsEngine()
    {
        var order = new List<string>();
        var initial = new OrderedRuntime([]).Snapshot;
        var runtime = new OrderedRuntime(order)
        {
            HasReleasableOwnership = true,
            StopResult = initial with
            {
                State = LocalAiRuntimeState.Failed,
                Ownership = LocalAiOwnership.CompanionManaged,
                GatewayRouteRequiresResolution = true,
                Detail = "listener remains running",
            },
        };
        var service = new LocalAiResetService(runtime, (_, _) =>
        {
            order.Add("engine");
            return Task.FromResult(0);
        }, () => "OpenClaw.Tray.WinUI.exe");

        LocalAiResetResult result = await service.ResetAsync();

        Assert.Equal(LocalAiResetOutcome.Failed, result.Outcome);
        Assert.Contains("nothing was removed", result.Message);
        Assert.Contains("listener remains running", result.Message);
        Assert.Equal(["stop"], order);
    }

    [Fact]
    public async Task Reset_StopWithUnreadableReceiptAndNoRoute_RunsEngine()
    {
        // An unbound runtime that cannot load its receipt reports Failed with no owner and no
        // route to resolve: the exact case reset exists to repair.
        var order = new List<string>();
        var initial = new OrderedRuntime([]).Snapshot;
        var runtime = new OrderedRuntime(order)
        {
            StopResult = initial with
            {
                State = LocalAiRuntimeState.Failed,
                Ownership = LocalAiOwnership.None,
                GatewayRouteRequiresResolution = false,
            },
        };
        var service = new LocalAiResetService(runtime, (_, _) =>
        {
            order.Add("engine");
            return Task.FromResult(0);
        }, () => "OpenClaw.Tray.WinUI.exe");

        await service.ResetAsync();

        Assert.Equal(["stop", "engine"], order);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_StopRefusedForMissingOwner_RerunsEngineOnlyWithoutNativeOwnership(bool nativeOwnership)
    {
        // A rerun after a reset released native ownership but its engine step failed: nothing
        // runs and Stop has no Gateway owner to withdraw through.
        var order = new List<string>();
        var runtime = new OrderedRuntime(order)
        {
            HasReleasableOwnership = nativeOwnership,
            StopFailure = new InvalidOperationException("No WSL Gateway owns this Local AI installation."),
        };
        var service = new LocalAiResetService(runtime, (_, _) =>
        {
            order.Add("engine");
            return Task.FromResult(0);
        }, () => "OpenClaw.Tray.WinUI.exe");

        LocalAiResetResult result = await service.ResetAsync();

        if (nativeOwnership)
        {
            Assert.Equal(LocalAiResetOutcome.Failed, result.Outcome);
            Assert.Contains("No WSL Gateway owns", result.Message);
            Assert.Equal(["stop"], order);
        }
        else
        {
            Assert.True(result.Succeeded);
            Assert.Equal(["stop", "engine"], order);
        }
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("release")]
    public async Task Reset_PhaseTimeout_CancelsAndSettlesOperationBeforeReporting(string phase)
    {
        var order = new List<string>();
        var runtime = new OrderedRuntime(order) { HasReleasableOwnership = true, BlockPhase = phase };
        var service = new LocalAiResetService(runtime, (_, _) =>
        {
            lock (order) order.Add("engine");
            return Task.FromResult(0);
        }, () => "OpenClaw.Tray.WinUI.exe", timeout: TimeSpan.FromMilliseconds(100));

        LocalAiResetResult result = await service.ResetAsync();

        string[] observed;
        lock (order) observed = [.. order];
        Assert.Equal(LocalAiResetOutcome.Failed, result.Outcome);
        Assert.Contains("timed out after", result.Message);
        Assert.Contains($"{phase}-settled", observed);
        Assert.DoesNotContain("engine", observed);
    }

    private static string JsonOutputPath(ProcessStartInfo startInfo) =>
        startInfo.ArgumentList[startInfo.ArgumentList.IndexOf("--json-output") + 1];

    private sealed class OrderedRuntime(List<string> order) : ILocalAiRuntime
    {
        public bool HasReleasableOwnership { get; set; }
        public Exception? ReleaseFailure { get; init; }
        public LocalAiRuntimeSnapshot? StopResult { get; init; }
        public Exception? StopFailure { get; init; }
        /// <summary>"stop" or "release": that call blocks until cancelled, then settles before rethrowing.</summary>
        public string? BlockPhase { get; init; }
        public LocalAiRuntimeSnapshot Snapshot { get; } =
            LocalAiRuntimeSnapshot.Initial(new Uri("http://127.0.0.1:1"), DateTimeOffset.UnixEpoch) with
            {
                GatewayRouteRequiresResolution = false,
            };
        public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged { add { } remove { } }

        public async Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default)
        {
            lock (order) order.Add("stop");
            await BlockIfRequestedAsync("stop", cancellationToken);
            if (StopFailure is not null) throw StopFailure;
            return StopResult ?? Snapshot;
        }

        public async Task<LocalAiRuntimeSnapshot> ReleaseOwnershipAsync(CancellationToken cancellationToken = default)
        {
            lock (order) order.Add("release");
            await BlockIfRequestedAsync("release", cancellationToken);
            if (ReleaseFailure is not null) throw ReleaseFailure;
            return Snapshot;
        }

        private async Task BlockIfRequestedAsync(string phase, CancellationToken cancellationToken)
        {
            if (BlockPhase != phase) return;
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(50, CancellationToken.None);
                lock (order) order.Add($"{phase}-settled");
                throw;
            }
        }

        public Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalAiRuntimeSnapshot> ResumeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
