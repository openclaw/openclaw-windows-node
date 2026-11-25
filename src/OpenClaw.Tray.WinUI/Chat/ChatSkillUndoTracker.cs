// Remembers learned-skill Undo requests by Skill Workshop review so a virtualized chat row
// can remount without forgetting a completed Undo or sending a second request while one is pending.
//
// Usage:
//   var tracker = new ChatSkillUndoTracker(() => bridge.CanUndoSkillChanges, bridge.UndoSkillChangesAsync);
//   if (tracker.CanUndo && tracker.StatusOf(notice) != ChatSkillUndoStatus.Undone)
//       await tracker.UndoAsync(notice);
using OpenClaw.Shared;

namespace OpenClawTray.Chat;

public enum ChatSkillUndoStatus
{
    Idle,
    Pending,
    Undone,
    Failed,
}

/// <summary>
/// Single-flight Undo state keyed by agent and review run. It never retries: a failed or unknown
/// outcome stays <see cref="ChatSkillUndoStatus.Failed"/> until the user asks again, which the
/// Gateway answers idempotently with <c>already-undone</c> when the first request had committed.
/// </summary>
public sealed class ChatSkillUndoTracker
{
    private readonly Func<bool> _canUndo;
    private readonly Func<ChatSkillChangeNotice, CancellationToken, Task<ChatSkillUndoOutcome>> _undo;
    private readonly object _gate = new();
    private readonly Dictionary<(string AgentId, string RunId), Entry> _entries = new();

    public ChatSkillUndoTracker(
        Func<bool> canUndo,
        Func<ChatSkillChangeNotice, CancellationToken, Task<ChatSkillUndoOutcome>> undo)
    {
        _canUndo = canUndo ?? throw new ArgumentNullException(nameof(canUndo));
        _undo = undo ?? throw new ArgumentNullException(nameof(undo));
    }

    public bool CanUndo => _canUndo();

    public ChatSkillUndoStatus StatusOf(ChatSkillChangeNotice notice)
    {
        lock (_gate)
            return _entries.TryGetValue(Key(notice), out var entry) ? entry.Status : ChatSkillUndoStatus.Idle;
    }

    /// <summary>The in-flight request for <paramref name="notice"/>, or null when none is pending.</summary>
    public Task<ChatSkillUndoStatus>? PendingTask(ChatSkillChangeNotice notice)
    {
        lock (_gate)
            return _entries.TryGetValue(Key(notice), out var entry) && entry.Status == ChatSkillUndoStatus.Pending
                ? entry.Task : null;
    }

    /// <summary>Starts Undo unless one is pending or already succeeded; returns the settled status.</summary>
    public Task<ChatSkillUndoStatus> UndoAsync(ChatSkillChangeNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var key = Key(notice);
        TaskCompletionSource<ChatSkillUndoStatus> completion;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing) &&
                existing.Status is ChatSkillUndoStatus.Pending or ChatSkillUndoStatus.Undone)
                return existing.Task;
            completion = new TaskCompletionSource<ChatSkillUndoStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            _entries[key] = new Entry(ChatSkillUndoStatus.Pending, completion.Task);
        }

        _ = RunAsync(notice, key, completion);
        return completion.Task;
    }

    private async Task RunAsync(
        ChatSkillChangeNotice notice,
        (string AgentId, string RunId) key,
        TaskCompletionSource<ChatSkillUndoStatus> completion)
    {
        ChatSkillUndoStatus status;
        try
        {
            status = await _undo(notice, CancellationToken.None).ConfigureAwait(false) == ChatSkillUndoOutcome.Undone
                ? ChatSkillUndoStatus.Undone
                : ChatSkillUndoStatus.Failed;
        }
        catch (Exception)
        {
            status = ChatSkillUndoStatus.Failed;
        }

        lock (_gate)
            _entries[key] = new Entry(status, completion.Task);
        completion.TrySetResult(status);
    }

    private static (string AgentId, string RunId) Key(ChatSkillChangeNotice notice) => (notice.AgentId, notice.RunId);

    private sealed record Entry(ChatSkillUndoStatus Status, Task<ChatSkillUndoStatus> Task);
}
