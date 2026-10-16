using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenClaw.TestSupport.Ui;

namespace OpenClaw.Tray.IntegrationTests;

public sealed class LvtOwnedTargetTests
{
    [Fact]
    public void AReplacedProcessLifetimeIsRejectedBeforeWindowAccess()
    {
        using var process = Process.GetCurrentProcess();
        Assert.Throws<InvalidOperationException>(() =>
            LvtUiaSession.ValidateTarget(process, DateTime.UnixEpoch, 0));
    }

    [Fact]
    public void MissingWindowIsNotAcceptedBecauseTheProcessIsAlive()
    {
        using var process = Process.GetCurrentProcess();
        Assert.Throws<InvalidOperationException>(() =>
            LvtUiaSession.ValidateTarget(process, process.StartTime.ToUniversalTime(), 0));
    }

    [Fact]
    public void UnownedDesktopWindowCannotBecomeAnAppTarget()
    {
        using var process = Process.GetCurrentProcess();
        var desktop = GetDesktopWindow();
        Assert.NotEqual(nint.Zero, desktop);
        Assert.Throws<InvalidOperationException>(() =>
            LvtUiaSession.ValidateTarget(process, process.StartTime.ToUniversalTime(), desktop));
    }

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();
}
