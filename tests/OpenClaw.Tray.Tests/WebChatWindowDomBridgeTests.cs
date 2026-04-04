using OpenClawTray.Windows;

namespace OpenClaw.Tray.Tests;

public class WebChatWindowDomBridgeTests
{
    [Fact]
    public void BuildSetDraftScript_ClearsWhenDraftIsBlank()
    {
        var script = WebChatVoiceDomBridge.BuildSetDraftScript(string.Empty);

        Assert.Equal("window.__openClawTrayVoice?.clearDraft?.();", script);
    }

    [Fact]
    public void BuildSetDraftScript_SerializesDraftText()
    {
        var script = WebChatVoiceDomBridge.BuildSetDraftScript("hello from voice");

        Assert.Contains("setDraft", script);
        Assert.Contains("\"hello from voice\"", script);
    }

    [Fact]
    public void DocumentCreatedScript_ClearsLegacyTurnsHost()
    {
        Assert.Contains("openclaw-tray-voice-turns", WebChatVoiceDomBridge.DocumentCreatedScript);
        Assert.Contains("clearLegacyTurnsHost", WebChatVoiceDomBridge.DocumentCreatedScript);
        Assert.Equal("window.__openClawTrayVoice?.setTurns?.([]);", WebChatVoiceDomBridge.ClearLegacyTurnsScript);
    }
}
