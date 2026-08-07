using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatAssistantContentPresentationTests
{
    [Theory]
    [InlineData(1200u, 774u, 1200, 774)]
    [InlineData(4096u, 1024u, 2048, 512)]
    public void ImageDecodePolicy_BoundsDecodeSize(
        uint width,
        uint height,
        int expectedWidth,
        int expectedHeight)
    {
        Assert.True(ChatAssistantImageDecodePolicy.TryGetDecodeSize(
            width,
            height,
            out var decodeWidth,
            out var decodeHeight));
        Assert.Equal(expectedWidth, decodeWidth);
        Assert.Equal(expectedHeight, decodeHeight);
    }

    [Theory]
    [InlineData(1u, 20_000u)]
    [InlineData(16_384u, 16_384u)]
    public void ImageDecodePolicy_RejectsUnsafeDimensions(uint width, uint height)
    {
        Assert.False(ChatAssistantImageDecodePolicy.TryGetDecodeSize(
            width,
            height,
            out _,
            out _));
    }

    [Fact]
    public void Project_StructuredPathLikeFileName_UsesLeafName()
    {
        var presentation = ChatAssistantContentProjector.Project(
        [
            new ChatMessageContentPartInfo
            {
                Kind = ChatMessageContentPartKind.Media,
                Media = new ChatMediaContentInfo
                {
                    Kind = ChatMediaContentKind.Image,
                    Source = ChatMediaContentSource.Structured,
                    FileName = "/home/openclaw/private/banner.png",
                },
            },
        ]);

        Assert.Equal("banner.png", Assert.Single(presentation!.Media).DisplayName);
    }

    [Fact]
    public void Project_UsesSafeFilenameWithoutExposingLegacySource()
    {
        var media = new ChatMediaContentInfo
        {
            Kind = ChatMediaContentKind.Image,
            Source = ChatMediaContentSource.LegacyDirective,
            FileName = "banner.png",
        };

        var presentation = ChatAssistantContentProjector.Project(
        [
            new ChatMessageContentPartInfo
            {
                Kind = ChatMessageContentPartKind.Media,
                Media = media,
            },
        ]);

        var item = Assert.Single(presentation!.Media);
        Assert.Equal("banner.png", item.DisplayName);
        Assert.DoesNotContain("/", item.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryProjection_MediaPart_RemainsChronological()
    {
        var message = new ChatMessageInfo
        {
            Role = "assistant",
            ContentParts =
            [
                new ChatMessageContentPartInfo
                {
                    Kind = ChatMessageContentPartKind.Text,
                    Text = "Before",
                },
                new ChatMessageContentPartInfo
                {
                    Kind = ChatMessageContentPartKind.Media,
                    Media = new ChatMediaContentInfo
                    {
                        Kind = ChatMediaContentKind.Image,
                        Source = ChatMediaContentSource.Structured,
                        FileName = "banner.png",
                    },
                },
                new ChatMessageContentPartInfo
                {
                    Kind = ChatMessageContentPartKind.Text,
                    Text = "After",
                },
            ],
        };

        var parts = ChatHistoryReplayProjection.Project([message]).ToArray();

        Assert.Collection(
            parts,
            part => Assert.Equal("Before", part.Text),
            part => Assert.Equal(
                ChatMessageContentPartKind.Media,
                Assert.Single(part.AssistantContentParts).Kind),
            part => Assert.Equal("After", part.Text));
    }

    [Fact]
    public void BuildRenderPlan_CapsImagesWithoutReorderingOtherMedia()
    {
        var media = Enumerable.Range(1, 5)
            .Select(index => Presentation(ChatMediaContentKind.Image, $"image-{index}.png"))
            .Append(Presentation(ChatMediaContentKind.Audio, "audio.mp3"))
            .ToArray();

        var plan = ChatAssistantContentProjector.BuildRenderPlan(media);

        Assert.Equal(1, plan.OmittedImages);
        Assert.Equal(
            new[] { "image-1.png", "image-2.png", "image-3.png", "image-4.png", "audio.mp3" },
            plan.Media.Select(item => item.DisplayName));
    }

    [Fact]
    public void MergeLiveUpdate_DoesNotReplaceLegacyReferenceWithStructuredReference()
    {
        var legacy = ChatAssistantContentProjector.Project(
        [
            new ChatMessageContentPartInfo
            {
                Kind = ChatMessageContentPartKind.Media,
                Media = new ChatMediaContentInfo
                {
                    Kind = ChatMediaContentKind.Image,
                    Source = ChatMediaContentSource.LegacyDirective,
                    FileName = "banner.png",
                },
            },
        ])!;
        var structured = ChatAssistantContentProjector.Project(
        [
            new ChatMessageContentPartInfo
            {
                Kind = ChatMessageContentPartKind.Media,
                Media = new ChatMediaContentInfo
                {
                    Kind = ChatMediaContentKind.Image,
                    Source = ChatMediaContentSource.Structured,
                    ArtifactId = "artifact-unavailable",
                },
            },
        ])!;

        var merged = ChatAssistantContentProjector.MergeLiveUpdate(legacy, structured);

        Assert.Equal(
            ChatMediaContentSource.LegacyDirective,
            Assert.Single(merged.Media).Reference.Source);
    }

    private static ChatAssistantMediaPresentation Presentation(
        ChatMediaContentKind kind,
        string name) =>
        new(
            kind,
            name,
            null,
            null,
            new ChatMediaContentInfo { Kind = kind, FileName = name });
}
