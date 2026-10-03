using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class DashboardBrowserShellTests
{
    [Fact]
    public void TryOpen_InvalidTarget_ReturnsFalseWithoutAProcessId()
    {
        Assert.False(DashboardBrowserShell.TryOpen("", out var processId));
        Assert.Null(processId);
    }

    [Fact]
    public void InterpretStartedProcess_NullProcess_IsSuccessfulActivationWithoutAProcessId()
    {
        Assert.True(DashboardBrowserShell.InterpretStartedProcess(null, out var processId));
        Assert.Null(processId);
    }
}
