using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public sealed class AssistantMediaDirectiveParserTests
{
    [Fact]
    public void Project_AssistantAbsolutePath_ProducesMediaWithoutExposingPath()
    {
        const string raw =
            "Here is the image.\nMEDIA:/home/openclaw/.openclaw/workspace/downloads/banner.png";

        var projection = AssistantMediaDirectiveParser.Project("assistant", raw);

        Assert.Equal("Here is the image.", projection.Text);
        Assert.DoesNotContain("/home/openclaw", projection.Text, StringComparison.Ordinal);
        var media = Assert.Single(
            projection.ContentParts,
            part => part.Kind == ChatMessageContentPartKind.Media).Media;
        Assert.NotNull(media);
        Assert.Equal(ChatMediaContentKind.Image, media.Kind);
        Assert.Equal(ChatMediaContentSource.LegacyDirective, media.Source);
        Assert.Equal("banner.png", media.FileName);
    }

    [Fact]
    public void Project_UserDirective_RemainsInertText()
    {
        const string raw = "MEDIA:/home/openclaw/private.png";

        var projection = AssistantMediaDirectiveParser.Project("user", raw);

        Assert.Equal(raw, projection.Text);
        Assert.DoesNotContain(
            projection.ContentParts,
            part => part.Kind == ChatMessageContentPartKind.Media);
    }

    [Fact]
    public void Project_FencedDirective_RemainsInertText()
    {
        const string raw = "```text\nMEDIA:/home/openclaw/example.png\n```";

        var projection = AssistantMediaDirectiveParser.Project("assistant", raw);

        Assert.Equal(raw, projection.Text);
        Assert.DoesNotContain(
            projection.ContentParts,
            part => part.Kind == ChatMessageContentPartKind.Media);
    }

    [Fact]
    public void Project_MidLineDirective_RemainsInertText()
    {
        const string raw = "Example: MEDIA:/home/openclaw/example.png";

        var projection = AssistantMediaDirectiveParser.Project("assistant", raw);

        Assert.Equal(raw, projection.Text);
        Assert.DoesNotContain(
            projection.ContentParts,
            part => part.Kind == ChatMessageContentPartKind.Media);
    }

    [Theory]
    [InlineData("MEDIA:../../.env")]
    [InlineData("MEDIA:~someone/private.png")]
    [InlineData("MEDIA:file:///home/openclaw/../private.png")]
    public void Project_InvalidPathLikeDirective_RedactsSource(string raw)
    {
        var projection = AssistantMediaDirectiveParser.Project("assistant", raw);

        Assert.Equal(string.Empty, projection.Text);
        var media = Assert.Single(
            projection.ContentParts,
            part => part.Kind == ChatMessageContentPartKind.Media).Media;
        Assert.NotNull(media);
        Assert.Equal(ChatMediaContentSource.Unavailable, media.Source);
        Assert.Null(media.GatewaySource);
    }

    [Fact]
    public void Project_QuotedPathWithSpaces_ProducesSingleMediaReference()
    {
        var projection = AssistantMediaDirectiveParser.Project(
            "assistant",
            "MEDIA:\"/home/openclaw/My Images/banner light.png\"");

        var media = Assert.Single(
            projection.ContentParts,
            part => part.Kind == ChatMessageContentPartKind.Media).Media;
        Assert.NotNull(media);
        Assert.Equal("banner light.png", media.FileName);
    }

    [Fact]
    public void Project_MultipleIndependentSources_PreservesOrder()
    {
        var projection = AssistantMediaDirectiveParser.Project(
            "assistant",
            "MEDIA:/tmp/one.png /tmp/two.mp4");

        var media = projection.ContentParts
            .Where(part => part.Kind == ChatMessageContentPartKind.Media)
            .Select(part => part.Media)
            .ToArray();
        Assert.Collection(
            media,
            item => Assert.Equal("one.png", item?.FileName),
            item => Assert.Equal("two.mp4", item?.FileName));
    }

    [Fact]
    public void Project_TooManySources_CapsMediaReferencesAndRedactsRemainder()
    {
        var sources = Enumerable.Range(1, AssistantMediaDirectiveParser.MaxMediaReferences + 5)
            .Select(index => $"/tmp/image-{index}.png");
        var projection = AssistantMediaDirectiveParser.Project(
            "assistant",
            $"MEDIA:{string.Join(' ', sources)}");

        Assert.Equal(string.Empty, projection.Text);
        Assert.Equal(
            AssistantMediaDirectiveParser.MaxMediaReferences,
            projection.ContentParts.Count(part => part.Kind == ChatMessageContentPartKind.Media));
    }

    [Fact]
    public void Project_Ipv4MappedPrivateHttpsSource_RemainsInert()
    {
        const string text = "MEDIA:https://[::ffff:192.168.1.1]/private.png";

        var projection = AssistantMediaDirectiveParser.Project("assistant", text);

        Assert.DoesNotContain(
            projection.ContentParts,
            part => part.Kind == ChatMessageContentPartKind.Media);
        Assert.Equal(text, projection.Text);
    }

    [Theory]
    [InlineData("MEDIA:https://0.0.0.0/a.png")]
    [InlineData("MEDIA:https://0.1.2.3/a.png")]
    [InlineData("MEDIA:https://100.64.0.1/a.png")]
    [InlineData("MEDIA:https://100.119.0.78/a.png")]
    [InlineData("MEDIA:https://[::ffff:0.0.0.1]/a.png")]
    [InlineData("MEDIA:https://[::ffff:100.64.0.1]/a.png")]
    public void Project_ThisNetworkAndCgnatHttps_RemainsInert(string text)
    {
        var projection = AssistantMediaDirectiveParser.Project("assistant", text);

        Assert.DoesNotContain(
            projection.ContentParts,
            part => part.Kind == ChatMessageContentPartKind.Media);
        Assert.Equal(text, projection.Text);
        Assert.False(AssistantMediaDirectiveParser.IsSendableLegacySource(text["MEDIA:".Length..]));
    }

    [Theory]
    [InlineData("https://example.com/a.png", "example.com")]
    [InlineData("https://1.1.1.1/a.png", "1.1.1.1")]
    public void Project_PublicHttps_KeepsTheGatewaySource(string source, string host)
    {
        var projection = AssistantMediaDirectiveParser.Project("assistant", "MEDIA:" + source);

        var media = Assert.Single(
            projection.ContentParts,
            part => part.Kind == ChatMessageContentPartKind.Media).Media;
        Assert.NotNull(media);
        Assert.Equal(source, media.GatewaySource);
        Assert.Equal(host, new Uri(media.GatewaySource!).Host);
        Assert.True(AssistantMediaDirectiveParser.IsSendableLegacySource(source));
    }
}
