using OpenClawTray.Chat;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class AgentAvatarPresentationTests
{
    // Expected values computed with the Control UI's agent-avatar implementation
    // (openclaw/openclaw bec69fc8, ui/src/components/agent-avatar-face.tsx) so every client draws the same face.
    [Theory]
    [InlineData("main", 3935363592u, 3, 1, "D6C066", "F6EFD5", "473D15")]
    [InlineData("forge", 2310183952u, 3, 2, "F6EFD5", "F6EFD5", "473D15")]
    [InlineData("scout", 3273829637u, 3, 0, "D5D9F6", "D5D9F6", "151B47")]
    [InlineData("Assistant", 3662687431u, 3, 1, "D6A266", "F6E7D5", "473015")]
    [InlineData("research", 1658572492u, 1, 0, "D6C066", "F6EFD5", "473D15")]
    public void Face_MatchesControlUiGeneratedAvatar(
        string agentId, uint seed, int eye, int mouth, string background, string pale, string ink)
    {
        Assert.Equal(seed, AgentAvatarPresentation.Seed(agentId));

        var face = AgentAvatarPresentation.Face(agentId);

        Assert.Equal((eye, mouth), (face.Eye, face.Mouth));
        Assert.Equal(background, Hex(face.Background));
        Assert.Equal(pale, Hex(face.Pale));
        Assert.Equal(ink, Hex(face.Ink));
    }

    [Theory]
    [InlineData("✨", "✨")]
    [InlineData("  🦞 ", "🦞")]
    [InlineData("OC", "OC")]
    [InlineData("A", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("TooLongXY", null)]
    [InlineData("a b", null)]
    [InlineData("a/b", null)]
    [InlineData("a\\b", null)]
    [InlineData("a.png", null)]
    [InlineData("data:x", null)]
    [InlineData("a\u200Bb", null)]
    [InlineData("a\u202Eb", null)]
    public void TextAvatar_AcceptsOnlyShortGlyphs(string value, string? expected) =>
        Assert.Equal(expected, AgentAvatarPresentation.TextAvatar(value));

    [Theory]
    [InlineData("data:image/png;base64,AQID", "data:image/png;base64,AQID")]
    [InlineData("https://images.example/a.png", "https://images.example/a.png")]
    [InlineData("http://images.example/a.png", null)]
    [InlineData("avatars/main.png", null)]
    [InlineData("C:\\avatars\\main.png", null)]
    [InlineData("file:///C:/avatars/main.png", null)]
    [InlineData("data:text/html,x", null)]
    [InlineData(" ", null)]
    public void ImageUrl_AcceptsOnlyInlineImagesAndHttps(string value, string? expected) =>
        Assert.Equal(expected, AgentAvatarPresentation.ImageUrl(value));

    [Fact]
    public void Compose_PrefersRosterLabelsThenGatewayIdentityThenId()
    {
        var hydrated = new ChatAgentIdentity("main", "Assistant", null, null);

        Assert.Equal("Roster", Compose(rosterName: "Roster", rosterIdentityName: "Identity", hydrated: hydrated).Name);
        Assert.Equal("Identity", Compose(rosterIdentityName: "Identity", hydrated: hydrated).Name);
        Assert.Equal("Assistant", Compose(hydrated: hydrated).Name);
        Assert.Equal("main", Compose().Name);
        Assert.Equal("main", Compose(hydrated: hydrated with { AgentId = "other" }).Name);
    }

    [Fact]
    public void Compose_PrefersRosterTextAvatarAndLetsGatewayIdentityOwnTheImage()
    {
        var hydrated = new ChatAgentIdentity("main", null, "https://images.example/fresh.png", "🤖");

        var withRoster = Compose(rosterEmoji: "✨", rosterAvatar: "🦞",
            rosterAvatarUrl: "https://images.example/stale.png", hydrated: hydrated);
        Assert.Equal("✨", withRoster.Emoji);
        Assert.Equal("https://images.example/fresh.png", withRoster.AvatarUrl);

        Assert.Equal("🦞", Compose(rosterEmoji: "A", rosterAvatar: "🦞").Emoji);
        Assert.Equal("🤖", Compose(rosterAvatar: "avatars/main.png", hydrated: hydrated).Emoji);
        Assert.Null(Compose(rosterAvatarUrl: "https://images.example/stale.png",
            hydrated: hydrated with { AvatarUrl = null }).AvatarUrl);
        Assert.Equal("https://images.example/roster.png",
            Compose(rosterAvatar: "https://images.example/roster.png").AvatarUrl);
    }

    private static WorkspaceAgent Compose(string? rosterName = null, string? rosterIdentityName = null,
        string? rosterEmoji = null, string? rosterAvatar = null, string? rosterAvatarUrl = null,
        ChatAgentIdentity? hydrated = null) =>
        AgentAvatarPresentation.Compose("main", null, rosterName, rosterIdentityName, rosterEmoji,
            rosterAvatar, rosterAvatarUrl, hydrated);

    private static string Hex(AgentAvatarColor color) => $"{color.R:X2}{color.G:X2}{color.B:X2}";
}
