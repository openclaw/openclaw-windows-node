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

    private static string JsonOutputPath(ProcessStartInfo startInfo) =>
        startInfo.ArgumentList[startInfo.ArgumentList.IndexOf("--json-output") + 1];

    private sealed class OrderedRuntime(List<string> order) : ILocalAiRuntime
    {
        public bool HasReleasableOwnership { get; set; }
        public Exception? ReleaseFailure { get; init; }
        public LocalAiRuntimeSnapshot Snapshot { get; } =
            LocalAiRuntimeSnapshot.Initial(new Uri("http://127.0.0.1:1"), DateTimeOffset.UnixEpoch);
        public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged { add { } remove { } }

        public Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default)
        {
            order.Add("stop");
            return Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> ReleaseOwnershipAsync(CancellationToken cancellationToken = default)
        {
            order.Add("release");
            return ReleaseFailure is null ? Task.FromResult(Snapshot) : Task.FromException<LocalAiRuntimeSnapshot>(ReleaseFailure);
        }

        public Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalAiRuntimeSnapshot> ResumeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
