using OpenClaw.Shared;
using System.Globalization;
using System.Text;

namespace OpenClawTray.Chat;

internal static class ChatComposerSubmissionPolicy
{
    internal static bool IsCommandPrompt(string message)
    {
        foreach (var character in message.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(character) ||
                Rune.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.Format)
                continue;
            return character.Value == '/';
        }
        return false;
    }

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
