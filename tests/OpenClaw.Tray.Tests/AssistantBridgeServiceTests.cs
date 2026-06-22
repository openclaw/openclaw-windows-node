using OpenClaw.Shared;
using OpenClawTray.Services;
using System.Diagnostics;

namespace OpenClaw.Tray.Tests;

public sealed class AssistantBridgeServiceTests
{
    [Fact]
    public void ParseStatus_ReadsListenServiceAndRecentTurns()
    {
        const string json = """
        {
          "generated_at": "2026-06-21T19:40:30+00:00",
          "user_id": "owner",
          "assistant": {
            "listen_service": {
              "allow_cloud": true,
              "configured": true,
              "input_mode": "always-listening",
              "log_file": "C:\\Users\\RipauvGohil\\AppData\\Local\\OpenClaw\\runtime\\assistant-listen-owner.log",
              "pid": 20656,
              "speak_aloud": true,
              "status": "running",
              "transcriber": "local-whisper"
            },
            "recent_turns": [
              {
                "created_at": "2026-06-21T19:26:59+00:00",
                "source": "local-whisper",
                "input_text": "OpenClaw, say only live voice smoke ok.",
                "display_response_text": "live voice smoke ok",
                "provider": "local-openai-compatible",
                "model_profile": "local-private",
                "stage": "answered",
                "total_ms": 23502
              }
            ]
          },
          "voice": {
            "preferred_input_device": "Microphone (Yeti Nano)",
            "preferred_output_device": "LG TV SSCR2 (NVIDIA High Definition Audio)"
          }
        }
        """;

        var snapshot = AssistantBridgeService.ParseStatus(json);

        Assert.True(snapshot.IsAvailable);
        Assert.Equal("owner", snapshot.UserId);
        Assert.Equal("2026-06-21T19:40:30+00:00", snapshot.GeneratedAt);
        Assert.Equal("Microphone (Yeti Nano)", snapshot.PreferredInputDevice);
        Assert.Equal("LG TV SSCR2 (NVIDIA High Definition Audio)", snapshot.PreferredOutputDevice);
        Assert.True(snapshot.ListenService.IsRunning);
        Assert.True(snapshot.ListenService.AllowCloud);
        Assert.True(snapshot.ListenService.SpeakAloud);
        Assert.Equal(20656, snapshot.ListenService.Pid);
        Assert.Equal("local-whisper", snapshot.ListenService.Transcriber);
        Assert.Single(snapshot.RecentTurns);
        Assert.Equal("OpenClaw, say only live voice smoke ok.", snapshot.RecentTurns[0].InputText);
        Assert.Equal("live voice smoke ok", snapshot.RecentTurns[0].ResponseText);
        Assert.Equal(23502, snapshot.RecentTurns[0].TotalMs);
    }

    [Fact]
    public void ResolveOpenClawCli_PrefersBackendRootEnvironmentOverride()
    {
        var oldValue = Environment.GetEnvironmentVariable("OPENCLAW_BACKEND_ROOT");
        var root = Path.Combine(Path.GetTempPath(), "OpenClaw.Tray.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var scripts = Path.Combine(root, ".venv", "Scripts");
            Directory.CreateDirectory(scripts);
            var exe = Path.Combine(scripts, "openclaw.exe");
            File.WriteAllText(exe, "");

            Environment.SetEnvironmentVariable("OPENCLAW_BACKEND_ROOT", root);

            var launcher = AssistantBridgeService.ResolveOpenClawCli();

            Assert.NotNull(launcher);
            Assert.Equal(exe, launcher!.ExecutablePath);
            Assert.Equal(root, launcher.WorkingDirectory);
            Assert.Empty(launcher.PrefixArgs);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAW_BACKEND_ROOT", oldValue);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task StartListenServiceAsync_DefaultsToLocalAssistantRouting()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenClaw.Tray.Tests", Guid.NewGuid().ToString("N"));
        var argsPath = Path.Combine(root, "args.txt");
        Directory.CreateDirectory(root);
        try
        {
            var scriptPath = Path.Combine(root, "capture-args.ps1");
            File.WriteAllText(
                scriptPath,
                $"Set-Content -LiteralPath {PsQuote(argsPath)} -Value ($args -join [Environment]::NewLine)");
            var launcher = PowerShellScriptLauncher(scriptPath, root);
            var service = new AssistantBridgeService(
                NullLogger.Instance,
                "owner",
                () => launcher);

            var result = await service.StartListenServiceAsync();

            Assert.True(result.Success, result.ErrorMessage);
            var args = File.ReadAllLines(argsPath);
            Assert.Contains("assistant", args);
            Assert.Contains("listen-service", args);
            Assert.Contains("start", args);
            Assert.Contains("--store-turn", args);
            Assert.Contains("--json", args);
            Assert.DoesNotContain("--allow-cloud", args);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task StartListenServiceAsync_KillsTimedOutBackendCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), "OpenClaw.Tray.Tests", Guid.NewGuid().ToString("N"));
        var pidPath = Path.Combine(root, "pid.txt");
        Directory.CreateDirectory(root);
        int? pid = null;
        try
        {
            var scriptPath = Path.Combine(root, "sleep.ps1");
            File.WriteAllText(
                scriptPath,
                $"Set-Content -LiteralPath {PsQuote(pidPath)} -Value $PID; Start-Sleep -Seconds 30");
            var launcher = PowerShellScriptLauncher(scriptPath, root);
            var service = new AssistantBridgeService(
                NullLogger.Instance,
                "owner",
                () => launcher,
                TimeSpan.FromSeconds(2));

            var result = await service.StartListenServiceAsync();

            Assert.False(result.Success);
            Assert.Contains("timed out", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            pid = await ReadPidAsync(pidPath);
            Assert.True(
                await WaitForProcessExitAsync(pid.Value),
                $"Expected timed-out backend process {pid.Value} to be killed.");
        }
        finally
        {
            if (pid is int runningPid)
                TryKillProcess(runningPid);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static OpenClawCliLauncher PowerShellScriptLauncher(string scriptPath, string workingDirectory)
    {
        var exe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (!File.Exists(exe))
            exe = "powershell.exe";

        return new OpenClawCliLauncher(
            exe,
            workingDirectory,
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath]);
    }

    private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    private static async Task<int> ReadPidAsync(string pidPath)
    {
        for (var i = 0; i < 50; i++)
        {
            if (File.Exists(pidPath) &&
                int.TryParse((await File.ReadAllTextAsync(pidPath)).Trim(), out var pid))
            {
                return pid;
            }

            await Task.Delay(100);
        }

        Assert.Fail("Timed-out backend command did not write its process id.");
        return 0;
    }

    private static async Task<bool> WaitForProcessExitAsync(int pid)
    {
        for (var i = 0; i < 50; i++)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    private static void TryKillProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
