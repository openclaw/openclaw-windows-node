using OpenClawTray.Services;

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
}
