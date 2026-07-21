using OpenClaw.Shared;

namespace OpenClawTray.Chat;

internal static class ChatComposerSubmissionPolicy
{
    public static bool ShouldClearInput(long submittedRevision, long currentRevision) =>
        submittedRevision == currentRevision;

    public static IReadOnlyList<ChatAttachment> RemoveSubmittedAttachments(
        IReadOnlyList<ChatAttachment> currentAttachments,
        IReadOnlyList<ChatAttachment> submittedAttachments)
    {
        if (currentAttachments.Count == 0 || submittedAttachments.Count == 0)
            return currentAttachments;

        var remaining = currentAttachments
            .Where(current => !submittedAttachments.Any(submitted => ReferenceEquals(current, submitted)))
            .ToArray();
        return remaining.Length == currentAttachments.Count ? currentAttachments : remaining;
    }
}
