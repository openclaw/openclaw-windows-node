using OpenClaw.Connection;
using System.Text;

namespace OpenClaw.Tray.Tests;

public class GatewayTerminalLaunchCommandBuilderTests
{
    [Fact]
    public void Build_WslWithWindowsTerminal_UsesExplicitTerminalPathAndPositionalArguments()
    {
        var access = GatewayHostAccessClassifier.Classify(new GatewayRecord
        {
            Id = "local",
            Url = "ws://127.0.0.1:18789",
            SetupManagedDistroName = "OpenClawGateway",
        });

        var command = GatewayTerminalLaunchCommandBuilder.Build(access, @"C:\Users\me\AppData\Local\Microsoft\WindowsApps\wt.exe");

        Assert.Equal(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\wt.exe", command.FileName);
        Assert.True(command.UsesWindowsTerminal);
        Assert.Equal([
            "new-tab",
            "--title",
            "OpenClaw Gateway (OpenClawGateway)",
            "wsl.exe",
            "-d",
            "OpenClawGateway"
        ], command.Arguments);
    }

    [Fact]
    public void Build_WslWithoutWindowsTerminal_FallsBackToWslExe()
    {
        var access = GatewayHostAccessClassifier.Classify(new GatewayRecord
        {
            Id = "local",
            Url = "ws://127.0.0.1:18789",
            SetupManagedDistroName = "OpenClawGateway",
        });

        var command = GatewayTerminalLaunchCommandBuilder.Build(access, windowsTerminalPath: null);

        Assert.Equal("wsl.exe", command.FileName);
        Assert.False(command.UsesWindowsTerminal);
        Assert.Equal(["-d", "OpenClawGateway"], command.Arguments);
    }

    [Fact]
    public void Build_SshTerminal_UsesUserAtHostWithoutTunnelForwardingArguments()
    {
        var access = GatewayHostAccessClassifier.Classify(new GatewayRecord
        {
            Id = "ssh",
            Url = "ws://127.0.0.1:18789",
            SshTunnel = new SshTunnelConfig("alice", "gateway.example.test", 18789, 18790),
        });

        var command = GatewayTerminalLaunchCommandBuilder.Build(access, windowsTerminalPath: null);

        Assert.Equal("ssh.exe", command.FileName);
        Assert.Equal(["alice@gateway.example.test"], command.Arguments);
        Assert.DoesNotContain("-N", command.Arguments);
        Assert.DoesNotContain("-L", command.Arguments);
        Assert.DoesNotContain("-p", command.Arguments);
    }

    [Fact]
    public void Build_NativeTerminal_OpensPowerShellForManagedTask()
    {
        var access = GatewayHostAccessClassifier.Classify(new GatewayRecord
        {
            Id = "native",
            Url = "ws://127.0.0.1:18789",
            IsLocal = true,
            SetupManagedNativeTaskName = "OpenClaw Gateway (OpenClawGateway)",
        });

        var command = GatewayTerminalLaunchCommandBuilder.Build(access, windowsTerminalPath: null);

        Assert.Equal("powershell.exe", command.FileName);
        Assert.False(command.UsesWindowsTerminal);
        Assert.Contains("-NoExit", command.Arguments);
        Assert.Contains("-EncodedCommand", command.Arguments);
        var script = DecodePowerShellEncodedCommand(command.Arguments);
        Assert.Contains("Use: openclaw gateway status --json", script);
        Assert.Contains("$env:OPENCLAW_PROFILE = $profile", script);
        Assert.Contains("OpenClawTray\\native-cli", script);
        Assert.Contains("Set-Location -LiteralPath $stateDir", script);
        Assert.Contains("OpenClawGateway", script);
    }

    [Fact]
    public void Build_NativeWithWindowsTerminal_UsesTaskNameInTitle()
    {
        var access = GatewayHostAccessClassifier.Classify(new GatewayRecord
        {
            Id = "native",
            Url = "ws://127.0.0.1:18789",
            IsLocal = true,
            SetupManagedNativeTaskName = "OpenClaw Gateway (OpenClawGateway)",
        });

        var command = GatewayTerminalLaunchCommandBuilder.Build(access, @"C:\Users\me\AppData\Local\Microsoft\WindowsApps\wt.exe");

        Assert.Equal(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\wt.exe", command.FileName);
        Assert.True(command.UsesWindowsTerminal);
        Assert.Contains("OpenClaw Gateway (OpenClawGateway)", command.Arguments);
        Assert.Contains("powershell.exe", command.Arguments);
        Assert.DoesNotContain(command.Arguments, argument => argument.Contains(';', StringComparison.Ordinal));
    }

    [Fact]
    public void Build_RejectsRecordsWithoutTerminalAccess()
    {
        var access = GatewayHostAccessClassifier.Classify(new GatewayRecord
        {
            Id = "remote",
            Url = "wss://gateway.example.test",
        });

        Assert.Throws<InvalidOperationException>(() =>
            GatewayTerminalLaunchCommandBuilder.Build(access, windowsTerminalPath: null));
    }

    private static string DecodePowerShellEncodedCommand(IReadOnlyList<string> arguments)
    {
        var index = -1;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] == "-EncodedCommand")
            {
                index = i;
                break;
            }
        }

        Assert.True(index >= 0 && index + 1 < arguments.Count);
        return Encoding.Unicode.GetString(Convert.FromBase64String(arguments[index + 1]));
    }

    [Fact]
    public void BuildGatewayDoctor_WithWindowsTerminal_OpensThemedTabWithSemicolonFreeKeepOpen()
    {
        var access = GatewayHostAccessClassifier.Classify(new GatewayRecord
        {
            Id = "local",
            Url = "ws://127.0.0.1:18789",
            SetupManagedDistroName = "OpenClawGateway",
        });

        var command = GatewayTerminalLaunchCommandBuilder.BuildGatewayDoctor(
            access, @"C:\Users\me\AppData\Local\Microsoft\WindowsApps\wt.exe");

        // Keep-open must NOT contain ';': Windows Terminal splits its command line
        // on ';' even inside quotes. We use '|| true && exec bash' instead.
        var script = $"{WslGatewayControlCommandBuilder.OpenClawWslPathPrefix} && openclaw doctor || true && exec bash";
        Assert.DoesNotContain(";", script);
        Assert.Equal(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\wt.exe", command.FileName);
        Assert.True(command.UsesWindowsTerminal);
        Assert.Equal([
            "new-tab",
            "--title",
            "OpenClaw doctor (OpenClawGateway)",
            "wsl.exe",
            "-d",
            "OpenClawGateway",
            "--",
            "bash",
            "-lc",
            script
        ], command.Arguments);
    }

    [Fact]
    public void BuildGatewayDoctor_WithoutWindowsTerminal_FallsBackToWslExe()
    {
        var access = GatewayHostAccessClassifier.Classify(new GatewayRecord
        {
            Id = "local",
            Url = "ws://127.0.0.1:18789",
            SetupManagedDistroName = "OpenClawGateway",
        });

        var command = GatewayTerminalLaunchCommandBuilder.BuildGatewayDoctor(access, windowsTerminalPath: null);

        var script = $"{WslGatewayControlCommandBuilder.OpenClawWslPathPrefix} && openclaw doctor || true && exec bash";
        Assert.Equal("wsl.exe", command.FileName);
        Assert.False(command.UsesWindowsTerminal);
        Assert.Equal(["-d", "OpenClawGateway", "--", "bash", "-lc", script], command.Arguments);
    }

    [Fact]
    public void BuildGatewayDoctor_RejectsSshGateway()
    {
        var access = GatewayHostAccessClassifier.Classify(new GatewayRecord
        {
            Id = "ssh",
            Url = "ws://127.0.0.1:18789",
            SshTunnel = new SshTunnelConfig("alice", "gateway.example.test", 18789, 18790),
        });

        Assert.Throws<InvalidOperationException>(() =>
            GatewayTerminalLaunchCommandBuilder.BuildGatewayDoctor(access, windowsTerminalPath: null));
    }
}
