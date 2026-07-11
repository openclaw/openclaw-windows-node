using OpenClawTray.Services;

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

        NativeGatewayKeepAliveService.ClearUserStopped();
        Assert.False(NativeGatewayKeepAliveService.IsUserStopped("OpenClaw Gateway (one)"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR", _previousLocalDataDir);
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
