using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public class ChatComposerSubmissionPolicyTests
{
    [Fact]
    public void ShouldClearInput_OnlyClearsTheSubmittedDraft()
    {
        Assert.True(ChatComposerSubmissionPolicy.ShouldClearInput(3, 3));
        Assert.False(ChatComposerSubmissionPolicy.ShouldClearInput(3, 4));
    }

    [Fact]
    public void RemoveSubmittedAttachments_PreservesAttachmentsAddedWhileSending()
    {
        var submitted = new ChatAttachment { FileName = "submitted.txt" };
        var addedLater = new ChatAttachment { FileName = "later.txt" };

        var remaining = ChatComposerSubmissionPolicy.RemoveSubmittedAttachments(
            [submitted, addedLater],
            [submitted]);

        Assert.Equal([addedLater], remaining);
    }

    [Fact]
    public void RemoveSubmittedAttachments_UsesReferenceEqualityNotValueEquality()
    {
        var original = new ChatAttachment { FileName = "file.txt" };
        var duplicate = new ChatAttachment { FileName = "file.txt" };

        var remaining = ChatComposerSubmissionPolicy.RemoveSubmittedAttachments(
            [original, duplicate],
            [original]);

        Assert.Single(remaining);
        Assert.Same(duplicate, remaining[0]);
    }

    [Fact]
    public void RemoveSubmittedAttachments_ReturnsOriginalListWhenNothingRemoved()
    {
        var a = new ChatAttachment { FileName = "a.txt" };
        var b = new ChatAttachment { FileName = "b.txt" };
        IReadOnlyList<ChatAttachment> current = [a, b];

        var remaining = ChatComposerSubmissionPolicy.RemoveSubmittedAttachments(
            current,
            [new ChatAttachment { FileName = "other.txt" }]);

        Assert.Same(current, remaining);
    }

    [Fact]
    public void ShouldClearInput_PreservesTextWhenUserEditedDuringSend()
    {
        // Simulates: user submits at revision 5, then types more (revision 6),
        // then send completes. Input should NOT be cleared.
        long submittedRevision = 5;
        long currentRevision = 6;
        Assert.False(ChatComposerSubmissionPolicy.ShouldClearInput(submittedRevision, currentRevision));
    }
}
