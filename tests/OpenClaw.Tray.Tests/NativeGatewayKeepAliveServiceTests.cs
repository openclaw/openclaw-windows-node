using OpenClawTray.Services;
using OpenClaw.SetupEngine;

namespace OpenClaw.Tray.Tests;

public class NativeGatewayKeepAliveServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"native-keepalive-{Guid.NewGuid():N}");
    private readonly string? _previousLocalDataDir;

    public NativeGatewayKeepAliveServiceTests()
    {
        _previousLocalDataDir = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR");
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR", _root);
    }

    [Fact]
    public void UserStoppedMarker_IsScopedToTaskName()
    {
        NativeGatewayKeepAliveService.RecordUserStopped("OpenClaw Gateway (one)");

        Assert.True(NativeGatewayKeepAliveService.IsUserStopped("OpenClaw Gateway (one)"));
        Assert.False(NativeGatewayKeepAliveService.IsUserStopped("OpenClaw Gateway (two)"));
        Assert.False(NativeGatewayKeepAliveService.IsUserStopped("OpenClaw Gateway (one)"));

        NativeGatewayKeepAliveService.ClearUserStopped();
        Assert.False(NativeGatewayKeepAliveService.IsUserStopped("OpenClaw Gateway (one)"));
    }

    [Fact]
    public async Task LifecycleCoordinator_SerializesConcurrentCommands()
    {
        var active = 0;
        var maxActive = 0;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var coordinator = new NativeGatewayLifecycleCoordinator(async (taskName, action, ct) =>
        {
            var current = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, current);
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task.WaitAsync(ct);
            }
            Interlocked.Decrement(ref active);
            return new NativeGatewayControlResult(taskName, action, 0, "ok", "");
        });

        var first = coordinator.RunAsync("OpenClaw Gateway (one)", NativeGatewayControlAction.Status);
        await firstEntered.Task;
        var second = coordinator.RunAsync("OpenClaw Gateway (one)", NativeGatewayControlAction.Status);
        await Task.Delay(25);
        Assert.Equal(1, maxActive);
        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task LifecycleCoordinator_TracksSuccessfulStopAndRestartIntent()
    {
        var coordinator = new NativeGatewayLifecycleCoordinator((taskName, action, _) =>
            Task.FromResult(new NativeGatewayControlResult(taskName, action, 0, "ok", "")));
        const string taskName = "OpenClaw Gateway (one)";

        await coordinator.RunAsync(taskName, NativeGatewayControlAction.Stop);
        Assert.True(NativeGatewayKeepAliveService.IsUserStopped(taskName));

        await coordinator.RunAsync(taskName, NativeGatewayControlAction.Restart);
        Assert.False(NativeGatewayKeepAliveService.IsUserStopped(taskName));
    }

    [Fact]
    public void ManagedNativeRecord_AcceptsLegacyFalseLocalFlagOnlyForLoopback()
    {
        Assert.True(NativeGatewayKeepAliveService.IsManagedNativeRecord(new OpenClaw.Connection.GatewayRecord
        {
            Url = "ws://127.0.0.1:18789",
            IsLocal = false,
            SetupManagedNativeTaskName = "OpenClaw Gateway (OpenClawGateway)",
        }));
        Assert.False(NativeGatewayKeepAliveService.IsManagedNativeRecord(new OpenClaw.Connection.GatewayRecord
        {
            Url = "wss://remote.example.test",
            IsLocal = false,
            SetupManagedNativeTaskName = "OpenClaw Gateway (OpenClawGateway)",
        }));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR", _previousLocalDataDir);
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
