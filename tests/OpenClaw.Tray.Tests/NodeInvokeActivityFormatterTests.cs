using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Privacy regression tests for the activity-stream details formatter that
/// powers both the recent-activity menu and the support bundle.
///
/// The end-to-end persistence path is:
///   NodeService.OnNodeInvokeCompleted (capability handler exception)
///     → App.OnNodeInvokeCompleted
///     → NodeInvokeActivityFormatter.BuildDetails
///     → ActivityStreamService.Add
///     → ActivityStreamService.BuildSupportBundle (when user shares logs)
///
/// For privacy-sensitive commands (mic / camera / screen), no caller-supplied
/// arg or runtime detail may reach support bundles. This test pins that.
/// </summary>
[Collection(ActivityStreamServiceCollection.Name)]
public sealed class NodeInvokeActivityFormatterTests : IDisposable
{
    public NodeInvokeActivityFormatterTests() => ActivityStreamService.Clear();
    public void Dispose() => ActivityStreamService.Clear();

    [Theory]
    [InlineData("stt.transcribe")]
    [InlineData("stt.listen")]
    [InlineData("stt.status")]
    [InlineData("camera.snap")]
    [InlineData("camera.clip")]
    [InlineData("screen.snapshot")]
    [InlineData("screen.record")]
    public void PrivacySensitive_FailedInvoke_OmitsErrorTextFromDetails(string command)
    {
        const string secret = "secret-language-or-device-detail";
        var details = NodeInvokeActivityFormatter.BuildDetails(command, ok: false, durationMs: 4321, error: secret);

        Assert.Equal("privacy-sensitive · 4321 ms · error", details);
        Assert.DoesNotContain(secret, details);
    }

    [Fact]
    public void PrivacySensitive_FailedInvoke_SecretDoesNotReachSupportBundle()
    {
        const string secret = "secret-language-or-device-detail";
        var details = NodeInvokeActivityFormatter.BuildDetails("stt.transcribe", ok: false, durationMs: 1234, error: secret);

        ActivityStreamService.Add(
            category: "node.invoke",
            title: "node.invoke failed: stt.transcribe",
            details: details,
            nodeId: "test-node");

        var bundle = ActivityStreamService.BuildSupportBundle();
        Assert.DoesNotContain(secret, bundle);
        Assert.Contains("privacy-sensitive · 1234 ms · error", bundle);
    }

    [Fact]
    public void PrivacySensitive_SuccessfulInvoke_OmitsAllDetail()
    {
        var details = NodeInvokeActivityFormatter.BuildDetails("stt.transcribe", ok: true, durationMs: 800, error: null);
        Assert.Equal("privacy-sensitive · 800 ms", details);
    }

    [Fact]
    public void NonPrivacySensitive_FailedInvoke_KeepsErrorForDiagnostics()
    {
        // Non-privacy-sensitive commands (metadata / exec) keep the error text
        // because they're useful for diagnostics and don't carry mic/camera args.
        var details = NodeInvokeActivityFormatter.BuildDetails(
            "device.status",
            ok: false,
            durationMs: 50,
            error: "gateway unreachable");

        Assert.Equal("metadata · 50 ms · gateway unreachable", details);
    }

    [Fact]
    public void NonPrivacySensitive_FailedInvoke_NullError_FallsBackToUnknown()
    {
        var details = NodeInvokeActivityFormatter.BuildDetails("device.status", ok: false, durationMs: 0, error: null);
        Assert.Equal("metadata · 0 ms · unknown error", details);
    }

    [Fact]
    public void Exec_FailedInvoke_KeepsErrorForDiagnostics()
    {
        var details = NodeInvokeActivityFormatter.BuildDetails(
            "system.run",
            ok: false,
            durationMs: 100,
            error: "exit code 1");

        Assert.Equal("exec · 100 ms · exit code 1", details);
    }

    [Fact]
    public void NegativeDuration_ClampsToZero()
    {
        var details = NodeInvokeActivityFormatter.BuildDetails("device.status", ok: true, durationMs: -7, error: null);
        Assert.Equal("metadata · 0 ms", details);
    }

    [Theory]
    [InlineData("stt.transcribe", "privacy-sensitive")]
    [InlineData("STT.Transcribe", "privacy-sensitive")]
    [InlineData("stt.listen", "privacy-sensitive")]
    [InlineData("Stt.Listen", "privacy-sensitive")]
    [InlineData("stt.status", "privacy-sensitive")]
    [InlineData("stt.future-command", "privacy-sensitive")] // any new stt.* defaults privacy-sensitive
    [InlineData("camera.snap", "privacy-sensitive")]
    [InlineData("camera.clip", "privacy-sensitive")]
    [InlineData("screen.snapshot", "privacy-sensitive")]
    [InlineData("screen.record", "privacy-sensitive")]
    [InlineData("system.run", "exec")]
    [InlineData("system.run.shell", "exec")]
    [InlineData("device.status", "metadata")]
    [InlineData("tts.speak", "privacy-sensitive")] // TTS errors can leak ElevenLabs key fragments / device names
    [InlineData("tts.future-command", "privacy-sensitive")] // any future tts.* defaults privacy-sensitive
    [InlineData("", "metadata")]
    public void GetPrivacyClass_KnownCommands(string command, string expected)
    {
        Assert.Equal(expected, NodeInvokeActivityFormatter.GetPrivacyClass(command));
    }
}
