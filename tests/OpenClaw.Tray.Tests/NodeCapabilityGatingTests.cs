using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pins the optional-capability gating that drives both the gateway client
/// path and the MCP-only path inside <c>NodeService.RegisterCapabilities</c>.
///
/// Privacy-sensitive defaults must be **off** even when settings are missing.
/// A regression that flips Stt/Tts to default-on would silently advertise
/// stt.transcribe / tts.speak the moment the tray launches with a fresh
/// settings file, with no user opt-in.
/// </summary>
public sealed class NodeCapabilityGatingTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private SettingsManager NewSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openclaw-tray-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return new SettingsManager(dir);
    }

    [Fact]
    public void NullSettings_DefaultOnCapabilities_AreEnabled()
    {
        // Defensive default: when settings are not yet loaded, we still
        // advertise the non-privacy-sensitive capabilities so the node is
        // usable immediately.
        Assert.True(NodeCapabilityGating.ShouldRegisterCanvas(null));
        Assert.True(NodeCapabilityGating.ShouldRegisterScreen(null));
        Assert.True(NodeCapabilityGating.ShouldRegisterCamera(null));
        Assert.True(NodeCapabilityGating.ShouldRegisterLocation(null));
        Assert.True(NodeCapabilityGating.ShouldRegisterBrowserProxy(null));
    }

    [Fact]
    public void NullSettings_PrivacySensitiveCapabilities_AreDisabled()
    {
        // Privacy invariant: TTS and STT must require an explicit user
        // opt-in. A null/missing settings object must not enable mic capture
        // or speaker output.
        Assert.False(NodeCapabilityGating.ShouldRegisterTts(null));
        Assert.False(NodeCapabilityGating.ShouldRegisterStt(null));
    }

    [Fact]
    public void DefaultSettings_PrivacySensitiveCapabilities_AreDisabled()
    {
        var s = NewSettings();
        Assert.False(NodeCapabilityGating.ShouldRegisterTts(s));
        Assert.False(NodeCapabilityGating.ShouldRegisterStt(s));
    }

    [Fact]
    public void DefaultSettings_OtherCapabilities_AreEnabled()
    {
        var s = NewSettings();
        Assert.True(NodeCapabilityGating.ShouldRegisterCanvas(s));
        Assert.True(NodeCapabilityGating.ShouldRegisterScreen(s));
        Assert.True(NodeCapabilityGating.ShouldRegisterCamera(s));
        Assert.True(NodeCapabilityGating.ShouldRegisterLocation(s));
        Assert.True(NodeCapabilityGating.ShouldRegisterBrowserProxy(s));
    }

    [Fact]
    public void Tts_OnlyAdvertisedWhenExplicitlyEnabled()
    {
        var s = NewSettings();
        Assert.False(NodeCapabilityGating.ShouldRegisterTts(s));
        s.NodeTtsEnabled = true;
        Assert.True(NodeCapabilityGating.ShouldRegisterTts(s));
        s.NodeTtsEnabled = false;
        Assert.False(NodeCapabilityGating.ShouldRegisterTts(s));
    }

    [Fact]
    public void Stt_OnlyAdvertisedWhenExplicitlyEnabled()
    {
        var s = NewSettings();
        Assert.False(NodeCapabilityGating.ShouldRegisterStt(s));
        s.NodeSttEnabled = true;
        Assert.True(NodeCapabilityGating.ShouldRegisterStt(s));
        s.NodeSttEnabled = false;
        Assert.False(NodeCapabilityGating.ShouldRegisterStt(s));
    }

    [Fact]
    public void TtsAndStt_Independent()
    {
        // A user who enables only TTS (output) must not silently enable STT
        // (input), and vice versa. Each capability is its own consent surface.
        var s = NewSettings();
        s.NodeTtsEnabled = true;
        s.NodeSttEnabled = false;
        Assert.True(NodeCapabilityGating.ShouldRegisterTts(s));
        Assert.False(NodeCapabilityGating.ShouldRegisterStt(s));

        s.NodeTtsEnabled = false;
        s.NodeSttEnabled = true;
        Assert.False(NodeCapabilityGating.ShouldRegisterTts(s));
        Assert.True(NodeCapabilityGating.ShouldRegisterStt(s));
    }

    [Fact]
    public void DefaultOnCapabilities_OnlyDisabledWhenExplicitlySetToFalse()
    {
        var s = NewSettings();
        s.NodeCanvasEnabled = false;
        s.NodeScreenEnabled = false;
        s.NodeCameraEnabled = false;
        s.NodeLocationEnabled = false;
        s.NodeBrowserProxyEnabled = false;

        Assert.False(NodeCapabilityGating.ShouldRegisterCanvas(s));
        Assert.False(NodeCapabilityGating.ShouldRegisterScreen(s));
        Assert.False(NodeCapabilityGating.ShouldRegisterCamera(s));
        Assert.False(NodeCapabilityGating.ShouldRegisterLocation(s));
        Assert.False(NodeCapabilityGating.ShouldRegisterBrowserProxy(s));
    }
}
