using System.Diagnostics;
using System.Net;
using OpenClaw.Connection.NativeGateway;

namespace OpenClaw.Connection.Tests;

public sealed class NativeGatewayWindowsProcessHostTests
{
    [Fact]
    public async Task WindowsJobCreationAndShutdown_DoesNotOwnTheTestProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Exercise suspended creation and job assignment with a harmless OS executable.
        // This is process-host proof only, not proof of an installed MSIX gateway or alias.
        var host = new WindowsNativeGatewayProcessHost();
        var command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe");
        await using var owned = await host.StartAsync(new NativeGatewayStartSpec(
            command, Directory.GetCurrentDirectory(), 18789, new Dictionary<string, string>()), default);
        using var self = Process.GetCurrentProcess();
        Assert.False(owned.Owns(new WindowsTcpListenerInfo(
            IPAddress.Loopback, 18789, self.Id, self.ProcessName, null, self.StartTime.ToUniversalTime())));
        await owned.DisposeAsync();
        Assert.True(owned.HasExited);
    }

    [Fact]
    public async Task LiveAncestry_RejectsSiblingsSelfExitedProcessesAndUnpackagedAnchors()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var self = Process.GetCurrentProcess();
        using var first = StartSleepingChild();
        using var second = StartSleepingChild();
        try
        {
            Assert.True(WindowsPackagedProcessAncestry.IsLiveDescendant(self, first));
            Assert.False(WindowsPackagedProcessAncestry.IsLiveDescendant(self, self));
            Assert.False(WindowsPackagedProcessAncestry.IsLiveDescendant(first, second));
            Assert.False(WindowsPackagedProcessAncestry.IsLiveDescendant(first, self));
            Assert.False(WindowsPackagedProcessAncestry.OwnsDescendant(
                self, first, "OpenClaw.Gateway_123456789abcd"));
            first.Kill();
            await first.WaitForExitAsync();
            Assert.False(WindowsPackagedProcessAncestry.IsLiveDescendant(self, first));
        }
        finally
        {
            if (!first.HasExited) first.Kill();
            if (!second.HasExited) second.Kill();
            await first.WaitForExitAsync();
            await second.WaitForExitAsync();
        }
    }

    private static Process StartSleepingChild()
    {
        var info = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add("Start-Sleep -Seconds 30");
        var process = Process.Start(info) ?? throw new InvalidOperationException("Test child failed to start.");
        _ = process.SafeHandle;
        return process;
    }

    [Fact]
    public void EnvironmentBlock_OverridesInheritedStateAndIsDoubleNullTerminated()
    {
        var block = WindowsNativeGatewayProcessHost.BuildEnvironmentBlock(new Dictionary<string, string>
        {
            ["OPENCLAW_STATE_DIR"] = @"D:\gateway-state",
            ["OPENCLAW_CONFIG_PATH"] = @"D:\gateway-state\openclaw.json",
            ["OPENCLAW_SUPERVISOR_MODE"] = "external",
        });
        Assert.EndsWith("\0\0", block);
        var entries = block.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(entries, e => e.StartsWith("OPENCLAW_STATE_DIR=", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(@"OPENCLAW_STATE_DIR=D:\gateway-state", entries);
        Assert.Contains(@"OPENCLAW_CONFIG_PATH=D:\gateway-state\openclaw.json", entries);
        Assert.Contains("OPENCLAW_SUPERVISOR_MODE=external", entries);
    }
}
