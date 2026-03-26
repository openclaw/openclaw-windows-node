using OpenClawTray.Windows;

namespace OpenClaw.Tray.Tests;

public class WebChatWindowDomBridgeTests
{
    [Fact]
    public void BuildDraftScript_ClearsWhenDraftIsBlank()
    {
        var script = WebChatWindow.BuildDraftScript(string.Empty);

        Assert.Equal("window.__openClawTrayVoice?.clearDraft?.();", script);
    }

    [Fact]
    public void BuildTurnsScript_SerializesOutgoingTurns()
    {
        var turns = new[]
        {
            new WebChatWindow.VoiceConversationTurnMirror("outgoing", "hello from voice")
        };

        var script = WebChatWindow.BuildTurnsScript(turns);

        Assert.Contains("setTurns", script);
        Assert.Contains("\"direction\":\"outgoing\"", script);
        Assert.Contains("\"text\":\"hello from voice\"", script);
    }

    [Fact]
    public void VoiceIntegrationScript_AnchorsTurnsBesideComposer()
    {
        Assert.Contains("getTurnsAnchor", WebChatWindow.TrayVoiceIntegrationScript);
        Assert.Contains("insertBefore(host, anchor)", WebChatWindow.TrayVoiceIntegrationScript);
    }
}
