using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public class GatewayMediaMessageProjectionTests
{
    private const string ObservedLocalFileName =
        "f4f160f1-07b9-4eb5-8de4-2b12c403d0fe-0d950ec0-98f0-4398-a7fe-c9b9131e8b5a-clipboard.png";
    private const string ObservedGatewayFileName =
        "f4f160f1-07b9-4eb5-8de4-2b12c403d0fe-0d950ec0-98f0-4398-a7fe.png";

    [Fact]
    public void ValidEnvelope_ProjectsSafeDescriptorAndCleanProse()
    {
        var projection = GatewayMediaMessageProjection.Project(
            "[media attached: media://inbound/pasted%20image---7f122605-290a-467c-a5df-8a744c093004.png (image/PNG)]\r\nDescribe this");

        Assert.True(projection.HasMediaEnvelope);
        Assert.Equal("Describe this", projection.ReconciliationText);
        Assert.Equal("Describe this", projection.ResidualText);
        var attachment = Assert.Single(projection.Attachments);
        Assert.Equal(ChatAttachmentOrigin.GatewayReference, attachment.Origin);
        Assert.Equal("pasted image.png", attachment.DisplayFileName);
        Assert.Equal("image/png", attachment.MimeType);
        Assert.True(attachment.IsImage);
        Assert.Null(attachment.PreviewCacheKey);
    }

    [Fact]
    public void MultipleEnvelopeLines_PreserveOrderAndFileClassification()
    {
        var projection = GatewayMediaMessageProjection.Project(
            "[media attached: media://inbound/photo.jpg (image/jpeg)]\n" +
            "[media attached: media://inbound/notes.txt (text/plain)]\nhello");

        Assert.Collection(
            projection.Attachments,
            image =>
            {
                Assert.Equal("photo.jpg", image.DisplayFileName);
                Assert.True(image.IsImage);
            },
            file =>
            {
                Assert.Equal("notes.txt", file.DisplayFileName);
                Assert.False(file.IsImage);
            });
        Assert.NotEmpty(projection.AttachmentPresentationSignature);
        Assert.NotEmpty(projection.AttachmentCorrelationSignature);
    }

    [Fact]
    public void EncodedPathAndControls_AreReducedToPrintableBoundedLeaf()
    {
        var longName = new string('a', 220);
        var projection = GatewayMediaMessageProjection.Project(
            $"[media attached: media://inbound/folder%2F..%2F{longName}%0Aname.txt (text/plain)]");

        var attachment = Assert.Single(projection.Attachments);
        Assert.DoesNotContain("/", attachment.DisplayFileName);
        Assert.DoesNotContain("\n", attachment.DisplayFileName);
        Assert.True(attachment.DisplayFileName.Length <= 160);
    }

    [Theory]
    [InlineData("[media attached: https://inbound/file.png (image/png)]")]
    [InlineData("[media attached: media://outbound/file.png (image/png)]")]
    [InlineData("[media attached: media://inbound/file.png (not a mime)]")]
    [InlineData("[media attached: media://inbound/file.png image/png)]")]
    [InlineData("[media attached: media://inbound/ (image/png)]")]
    public void MalformedOrUnsupportedLookalike_RemainsOrdinaryProse(string text)
    {
        var projection = GatewayMediaMessageProjection.Project(text);

        Assert.False(projection.HasMediaEnvelope);
        Assert.Empty(projection.Attachments);
        Assert.Equal(text, projection.ResidualText);
    }

    [Fact]
    public void EmbeddedEnvelope_RemainsOrdinaryProse()
    {
        const string text = "User-authored text\n[media attached: media://inbound/file.png (image/png)]";

        var projection = GatewayMediaMessageProjection.Project(text);

        Assert.False(projection.HasMediaEnvelope);
        Assert.Equal(text, projection.ResidualText);
    }

    [Fact]
    public void GatewayProjection_NeverEmitsPrivateMarkersOrPreviewKeys()
    {
        var projection = GatewayMediaMessageProjection.Project(
            "[media attached: media://inbound/file.png (image/png)]");

        Assert.False(projection.ResidualText.Contains('\u200B'));
        Assert.All(projection.Attachments, attachment =>
        {
            Assert.Equal(ChatAttachmentOrigin.GatewayReference, attachment.Origin);
            Assert.False(attachment.CanAccessPreviewCache);
        });
    }

    [Fact]
    public void LocalPresentations_UseOpaquePreviewKeysAndOriginalSafeNames()
    {
        var presentations = GatewayMediaMessageProjection.CreateLocalPresentations(
            [
                new ChatAttachment
                {
                    Type = "image",
                    MimeType = "image/png",
                    FileName = @"C:\fake\original.png",
                },
            ],
            () => "opaque-key");

        var presentation = Assert.Single(presentations);
        Assert.Equal(ChatAttachmentOrigin.Local, presentation.Origin);
        Assert.Equal("original.png", presentation.DisplayFileName);
        Assert.Equal("opaque-key", presentation.PreviewCacheKey);
        Assert.True(presentation.CanAccessPreviewCache);
    }

    [Fact]
    public void LocalPresentations_PreserveCanonicalLookingNamesAndSourceOrder()
    {
        const string canonicalLookingName =
            "report---7f122605-290a-467c-a5df-8a744c093004.png";
        var keys = new Queue<string>(["first-key", "second-key"]);
        var presentations = GatewayMediaMessageProjection.CreateLocalPresentations(
            [
                new ChatAttachment
                {
                    Type = "image",
                    MimeType = "image/png",
                    FileName = "",
                },
                new ChatAttachment
                {
                    Type = "image",
                    MimeType = "image/png",
                    FileName = canonicalLookingName,
                },
            ],
            () => keys.Dequeue());

        Assert.Collection(
            presentations,
            first =>
            {
                Assert.Equal("image", first.DisplayFileName);
                Assert.Equal("first-key", first.PreviewCacheKey);
            },
            second =>
            {
                Assert.Equal(canonicalLookingName, second.DisplayFileName);
                Assert.Equal("second-key", second.PreviewCacheKey);
            });
    }

    [Fact]
    public void ObservedGatewayRewrite_ChangesPresentationButPreservesCorrelationSignature()
    {
        var local = GatewayMediaMessageProjection.CreateLocalPresentations(
            [
                new ChatAttachment
                {
                    Type = "image",
                    MimeType = "image/png",
                    FileName = ObservedLocalFileName,
                },
            ],
            () => "local-preview");
        var gateway = GatewayMediaMessageProjection.Project(
            $"[media attached: media://inbound/{ObservedGatewayFileName} (image/png)]\nidentical caption");

        Assert.NotEqual(
            GatewayMediaMessageProjection.BuildAttachmentPresentationSignature(local),
            gateway.AttachmentPresentationSignature);
        Assert.Equal(
            GatewayMediaMessageProjection.BuildAttachmentCorrelationSignature(local),
            gateway.AttachmentCorrelationSignature);
    }

    [Fact]
    public void RemoteSameFilename_CannotResolveLocalPreviewBytes()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var cache = new Dictionary<string, byte[]>
        {
            ["opaque-local-key"] = bytes,
            ["same.png"] = new byte[] { 9 },
        };
        var local = new ChatAttachmentPresentation(
            ChatAttachmentOrigin.Local,
            "same.png",
            "image/png",
            IsImage: true,
            PreviewCacheKey: "opaque-local-key");
        var remote = new ChatAttachmentPresentation(
            ChatAttachmentOrigin.GatewayReference,
            "same.png",
            "image/png",
            IsImage: true);

        Assert.True(ChatAttachmentPreviewResolver.TryGetBytes(local, cache, out var resolved));
        Assert.Same(bytes, resolved);
        Assert.False(ChatAttachmentPreviewResolver.TryGetBytes(remote, cache, out _));
    }

    [Fact]
    public void PreviewCache_RejectsDecodedOverflowBeforeStorage()
    {
        Assert.False(ChatImagePreviewCache.TryDecodeBoundedBase64(
            Convert.ToBase64String([1, 2, 3, 4, 5]),
            maximumBytes: 4,
            out var bytes));
        Assert.Empty(bytes);
    }

    [Fact]
    public void PreviewCache_EvictsOldestEntriesWithinCountBound()
    {
        ChatImagePreviewCache.Clear();
        try
        {
            for (var index = 0; index <= ChatImagePreviewCache.MaximumEntries; index++)
            {
                Assert.True(ChatImagePreviewCache.TryStoreBase64(
                    $"preview-{index}",
                    Convert.ToBase64String([(byte)index])));
            }

            Assert.Equal(ChatImagePreviewCache.MaximumEntries, ChatImagePreviewCache.Count);
            Assert.False(ChatImagePreviewCache.Contains("preview-0"));
            Assert.True(ChatImagePreviewCache.Contains(
                $"preview-{ChatImagePreviewCache.MaximumEntries}"));
            Assert.True(ChatImagePreviewCache.TotalBytes <=
                ChatImagePreviewCache.MaximumTotalBytes);
        }
        finally
        {
            ChatImagePreviewCache.Clear();
        }
    }

    [Fact]
    public void AttachmentOnlyEcho_AmbiguousMatchingCandidatesAreNotConsumed()
    {
        var incoming = GatewayMediaMessageProjection.Project(
            "[media attached: media://inbound/same.png (image/png)]");
        var candidates = new[]
        {
            new ChatPendingEchoCandidate("one", "", incoming.AttachmentCorrelationSignature),
            new ChatPendingEchoCandidate("two", "", incoming.AttachmentCorrelationSignature),
        };

        Assert.Null(ChatAttachmentEchoCorrelation.SelectMatchingMessageId(candidates, incoming));
    }

    [Fact]
    public void PlainTextEcho_SingleMediaCandidateIsNotConsumed()
    {
        var media = GatewayMediaMessageProjection.Project(
            "[media attached: media://inbound/same.png (image/png)]\nsame caption");
        var plain = GatewayMediaMessageProjection.Project("same caption");
        var candidates = new[]
        {
            new ChatPendingEchoCandidate("media", "same caption", media.AttachmentCorrelationSignature),
        };

        Assert.Null(ChatAttachmentEchoCorrelation.SelectMatchingMessageId(candidates, plain));
    }

    [Fact]
    public void MediaEcho_RequiresMatchingAttachmentCorrelationSignature()
    {
        var incoming = GatewayMediaMessageProjection.Project(
            "[media attached: media://inbound/right.png (image/png)]\ncaption");
        var wrong = GatewayMediaMessageProjection.Project(
            "[media attached: media://inbound/wrong.pdf (application/pdf)]\ncaption");
        var candidates = new[]
        {
            new ChatPendingEchoCandidate("wrong", "caption", wrong.AttachmentCorrelationSignature),
            new ChatPendingEchoCandidate("right", "caption", incoming.AttachmentCorrelationSignature),
        };

        Assert.Equal(
            "right",
            ChatAttachmentEchoCorrelation.SelectMatchingMessageId(candidates, incoming));
    }

    [Fact]
    public void CaptionedMediaEcho_WithTwoSameMimeCandidatesIsAmbiguousAfterFilenameRewrite()
    {
        var incoming = GatewayMediaMessageProjection.Project(
            $"[media attached: media://inbound/{ObservedGatewayFileName} (image/png)]\nsame caption");
        var firstLocal = GatewayMediaMessageProjection.CreateLocalPresentations(
            [
                new ChatAttachment
                {
                    Type = "image",
                    MimeType = "image/png",
                    FileName = ObservedLocalFileName,
                },
            ],
            () => "first-preview");
        var secondLocal = GatewayMediaMessageProjection.CreateLocalPresentations(
            [
                new ChatAttachment
                {
                    Type = "image",
                    MimeType = "image/png",
                    FileName = "another-local-name.png",
                },
            ],
            () => "second-preview");
        var candidates = new[]
        {
            new ChatPendingEchoCandidate(
                "first",
                "same caption",
                GatewayMediaMessageProjection.BuildAttachmentCorrelationSignature(firstLocal)),
            new ChatPendingEchoCandidate(
                "second",
                "same caption",
                GatewayMediaMessageProjection.BuildAttachmentCorrelationSignature(secondLocal)),
        };

        Assert.Null(ChatAttachmentEchoCorrelation.SelectMatchingMessageId(candidates, incoming));
    }

    [Fact]
    public void NoEnvelopeEcho_MixedPlainAndAttachmentCandidatesAreAmbiguous()
    {
        var incoming = GatewayMediaMessageProjection.Project("same prose");
        var candidates = new[]
        {
            new ChatPendingEchoCandidate("media", "same prose", "1|9:image/png|8:file.png|"),
            new ChatPendingEchoCandidate("plain", "same prose", string.Empty),
        };

        Assert.Null(ChatAttachmentEchoCorrelation.SelectMatchingMessageId(candidates, incoming));
    }

    [Fact]
    public void NoEnvelopeEcho_AllPlainCandidatesPreserveFifo()
    {
        var incoming = GatewayMediaMessageProjection.Project("same prose");
        var candidates = new[]
        {
            new ChatPendingEchoCandidate("first", "same prose", string.Empty),
            new ChatPendingEchoCandidate("second", "same prose", string.Empty),
        };

        Assert.Equal(
            "first",
            ChatAttachmentEchoCorrelation.SelectMatchingMessageId(candidates, incoming));
    }
}
