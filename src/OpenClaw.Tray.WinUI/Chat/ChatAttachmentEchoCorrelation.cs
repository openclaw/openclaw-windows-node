namespace OpenClawTray.Chat;

internal sealed record ChatPendingEchoCandidate(
    string MessageId,
    string Text,
    string AttachmentCorrelationSignature);

internal static class ChatAttachmentEchoCorrelation
{
    internal static string? SelectMatchingMessageId(
        IReadOnlyList<ChatPendingEchoCandidate> candidates,
        GatewayMediaMessageProjectionResult incoming) =>
        SelectMatchingMessageId(
            candidates,
            incoming.ReconciliationText,
            incoming.AttachmentCorrelationSignature,
            incoming.HasMediaEnvelope);

    internal static string? SelectMatchingMessageId(
        IReadOnlyList<ChatPendingEchoCandidate> candidates,
        string text,
        string attachmentCorrelationSignature,
        bool hasMediaEnvelope)
    {
        var matching = candidates.Where(candidate =>
            string.Equals(candidate.Text, text, StringComparison.Ordinal) &&
            (!hasMediaEnvelope ||
             string.Equals(
                 candidate.AttachmentCorrelationSignature,
                 attachmentCorrelationSignature,
                 StringComparison.Ordinal)))
            .ToArray();

        if (matching.Length == 0)
            return null;
        if (hasMediaEnvelope && matching.Length != 1)
            return null;
        if (!hasMediaEnvelope &&
            matching.Any(candidate => candidate.AttachmentCorrelationSignature.Length > 0))
        {
            return null;
        }

        return matching[0].MessageId;
    }
}
