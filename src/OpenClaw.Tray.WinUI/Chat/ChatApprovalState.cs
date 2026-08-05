namespace OpenClawTray.Chat;

/// <summary>
/// Owns bounded approval-request identity deduplication and alternate-ID
/// correlation. The conversation-state root serializes every call.
/// </summary>
internal sealed class ChatApprovalState
{
    private const int SeenCapacity = 128;

    private readonly LinkedList<string> _seenOrder = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _alternateIds =
        new(StringComparer.Ordinal);

    internal bool MarkSeen(string requestId, string? alternateId)
    {
        if (string.IsNullOrEmpty(requestId))
            return true;

        RecordAlternateId(requestId, alternateId);
        if (IsSeen(requestId) ||
            IsDistinct(requestId, alternateId) && IsSeen(alternateId!))
        {
            return false;
        }

        AddSeen(requestId);
        if (IsDistinct(requestId, alternateId))
            AddSeen(alternateId!);

        while (_seenOrder.Count > SeenCapacity)
        {
            var oldest = _seenOrder.First!.Value;
            _seenOrder.RemoveFirst();
            Evict(oldest);
        }

        return true;
    }

    internal bool Matches(
        string pendingId,
        string primaryId,
        string alternateId)
    {
        if (string.IsNullOrEmpty(pendingId))
            return false;

        _alternateIds.TryGetValue(pendingId, out var pendingAlternate);
        return MatchesOne(primaryId) || MatchesOne(alternateId);

        bool MatchesOne(string value) =>
            !string.IsNullOrEmpty(value) &&
            (string.Equals(value, pendingId, StringComparison.Ordinal) ||
             !string.IsNullOrEmpty(pendingAlternate) &&
             string.Equals(value, pendingAlternate, StringComparison.Ordinal));
    }

    internal void Reset()
    {
        _seen.Clear();
        _seenOrder.Clear();
        _alternateIds.Clear();
    }

    private void AddSeen(string approvalId)
    {
        if (_seen.Add(approvalId))
            _seenOrder.AddLast(approvalId);
    }

    private bool IsSeen(string approvalId) =>
        _seen.Contains(approvalId) ||
        _alternateIds.TryGetValue(approvalId, out var alternateId) &&
        _seen.Contains(alternateId);

    private void RecordAlternateId(string requestId, string? alternateId)
    {
        if (!IsDistinct(requestId, alternateId))
            return;
        _alternateIds[requestId] = alternateId!;
        _alternateIds[alternateId!] = requestId;
    }

    private void Evict(string approvalId)
    {
        _seen.Remove(approvalId);
        if (!_alternateIds.TryGetValue(approvalId, out var alternateId))
            return;

        _alternateIds.Remove(approvalId);
        if (_alternateIds.TryGetValue(alternateId, out var reverse) &&
            string.Equals(reverse, approvalId, StringComparison.Ordinal))
        {
            _alternateIds.Remove(alternateId);
        }

        if (!_seen.Remove(alternateId))
            return;

        for (var node = _seenOrder.First; node is not null; node = node.Next)
        {
            if (!string.Equals(node.Value, alternateId, StringComparison.Ordinal))
                continue;
            _seenOrder.Remove(node);
            break;
        }
    }

    private static bool IsDistinct(string requestId, string? alternateId) =>
        !string.IsNullOrEmpty(alternateId) &&
        !string.Equals(alternateId, requestId, StringComparison.Ordinal);
}
