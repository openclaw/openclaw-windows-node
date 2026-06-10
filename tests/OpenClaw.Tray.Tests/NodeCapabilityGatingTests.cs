using OpenClaw.Shared;
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
        Assert.True(NodeCapabilityGating.ShouldRegisterSystemRun(null));
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
        Assert.True(NodeCapabilityGating.ShouldRegisterSystemRun(s));
    }

    [Fact]
    public void BrowserProxyGatewayRegistration_RequiresGatewayClientAndSharedToken()
    {
        var s = NewSettings();

        Assert.False(NodeCapabilityGating.ShouldRegisterBrowserProxy(s, sharedGatewayToken: null, hasGatewayClient: true));
        Assert.False(NodeCapabilityGating.ShouldRegisterBrowserProxy(s, sharedGatewayToken: "   ", hasGatewayClient: true));
        Assert.False(NodeCapabilityGating.ShouldRegisterBrowserProxy(s, sharedGatewayToken: "shared-token", hasGatewayClient: false));
        Assert.True(NodeCapabilityGating.ShouldRegisterBrowserProxy(s, sharedGatewayToken: "shared-token", hasGatewayClient: true));
    }

    [Fact]
    public void BrowserProxyGatewayRegistration_RespectsUserToggle()
    {
        var s = NewSettings();
        s.NodeBrowserProxyEnabled = false;

        Assert.False(NodeCapabilityGating.ShouldRegisterBrowserProxy(s, sharedGatewayToken: "shared-token", hasGatewayClient: true));
    }

    [Fact]
    public void SystemRun_OnlyDisabledWhenExplicitlySetToFalse()
    {
        var s = NewSettings();
        Assert.True(NodeCapabilityGating.ShouldRegisterSystemRun(s));
        s.NodeSystemRunEnabled = false;
        Assert.False(NodeCapabilityGating.ShouldRegisterSystemRun(s));
        s.NodeSystemRunEnabled = true;
        Assert.True(NodeCapabilityGating.ShouldRegisterSystemRun(s));
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
        s.NodeSystemRunEnabled = false;

        Assert.False(NodeCapabilityGating.ShouldRegisterCanvas(s));
        Assert.False(NodeCapabilityGating.ShouldRegisterScreen(s));
        Assert.False(NodeCapabilityGating.ShouldRegisterCamera(s));
        Assert.False(NodeCapabilityGating.ShouldRegisterLocation(s));
        Assert.False(NodeCapabilityGating.ShouldRegisterBrowserProxy(s));
        Assert.False(NodeCapabilityGating.ShouldRegisterSystemRun(s));
    }

    // ── GetLocalNodeCapabilities ──────────────────────────────────────────────

    [Fact]
    public void GetLocalNodeCapabilities_NullNodes_ReturnsNull()
    {
        Assert.Null(NodeCapabilityGating.GetLocalNodeCapabilities(null, "device-1"));
    }

    [Fact]
    public void GetLocalNodeCapabilities_EmptyNodes_ReturnsNull()
    {
        Assert.Null(NodeCapabilityGating.GetLocalNodeCapabilities([], "device-1"));
    }

    [Fact]
    public void GetLocalNodeCapabilities_NullDeviceId_ReturnsNull()
    {
        var nodes = new[] { new GatewayNodeInfo { NodeId = "device-1" } };
        Assert.Null(NodeCapabilityGating.GetLocalNodeCapabilities(nodes, null));
    }

    [Fact]
    public void GetLocalNodeCapabilities_EmptyDeviceId_ReturnsNull()
    {
        var nodes = new[] { new GatewayNodeInfo { NodeId = "device-1" } };
        Assert.Null(NodeCapabilityGating.GetLocalNodeCapabilities(nodes, ""));
    }

    [Fact]
    public void GetLocalNodeCapabilities_NoMatchingNode_ReturnsNull()
    {
        var nodes = new[]
        {
            new GatewayNodeInfo { NodeId = "device-1", Capabilities = ["canvas"] },
            new GatewayNodeInfo { NodeId = "device-2", Capabilities = ["screen"] },
        };
        Assert.Null(NodeCapabilityGating.GetLocalNodeCapabilities(nodes, "device-99"));
    }

    [Fact]
    public void GetLocalNodeCapabilities_MatchingNode_ReturnsCapabilities()
    {
        var nodes = new[]
        {
            new GatewayNodeInfo { NodeId = "device-1", Capabilities = ["canvas", "screen"] },
            new GatewayNodeInfo { NodeId = "device-2", Capabilities = ["location"] },
        };
        var result = NodeCapabilityGating.GetLocalNodeCapabilities(nodes, "device-1");
        Assert.NotNull(result);
        Assert.Equal(new[] { "canvas", "screen" }, result);
    }

    [Fact]
    public void GetLocalNodeCapabilities_MatchingNodeCaseInsensitive_ReturnsCapabilities()
    {
        var nodes = new[]
        {
            new GatewayNodeInfo { NodeId = "Device-ABC", Capabilities = ["canvas"] },
        };
        var result = NodeCapabilityGating.GetLocalNodeCapabilities(nodes, "device-abc");
        Assert.NotNull(result);
        Assert.Equal(new[] { "canvas" }, result);
    }

    [Fact]
    public void GetLocalNodeCapabilities_NodeWithNoCapabilities_ReturnsEmptyList()
    {
        var nodes = new[]
        {
            new GatewayNodeInfo { NodeId = "device-1", Capabilities = [] },
        };
        var result = NodeCapabilityGating.GetLocalNodeCapabilities(nodes, "device-1");
        Assert.NotNull(result);
        Assert.Empty(result);
    }
}
