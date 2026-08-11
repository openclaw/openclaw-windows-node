using OpenClaw.Shared;

namespace OpenClaw.Shared.Sessions;

/// <summary>
/// Canonical interpretation of the Gateway's latest-run outcome and live-run flag.
/// Keep this separate from presentation: the Gateway records outcomes per run, while a
/// session remains available for a later turn after that run finishes.
/// </summary>
public enum SessionRunStatus
{
    Unknown,
    Running,
    Completed,
    Failed,
    Stopped,
    TimedOut,
}

/// <summary>The intentionally small set of session states shown by the Windows app.</summary>
public enum SessionDisplayState
{
    Working,
    Ready,
    NeedsAttention,
}

public static class SessionRunState
{
    public static SessionRunStatus ResolveStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "active" or "running" => SessionRunStatus.Running,
        "done" or "completed" => SessionRunStatus.Completed,
        "error" or "failed" or "failure" => SessionRunStatus.Failed,
        "killed" or "aborted" or "cancelled" or "canceled" => SessionRunStatus.Stopped,
        "timeout" or "timed_out" => SessionRunStatus.TimedOut,
        _ => SessionRunStatus.Unknown,
    };

    /// <summary>
    /// The Gateway's current-run flag is authoritative when present. Older Gateways
    /// omit it, so only those payloads fall back to the latest recorded outcome.
    /// </summary>
    public static bool IsWorking(SessionInfo session)
    {
        if (session.HasActiveRun is { } hasActiveRun)
            return hasActiveRun;

        return ResolveStatus(session.Status) == SessionRunStatus.Running;
    }

    public static SessionDisplayState GetDisplayState(SessionInfo session)
    {
        if (IsWorking(session))
            return SessionDisplayState.Working;

        return ResolveStatus(session.Status) is SessionRunStatus.Failed or SessionRunStatus.TimedOut
            ? SessionDisplayState.NeedsAttention
            : SessionDisplayState.Ready;
    }

    /// <summary>Stable priority for compact lists: live work, attention, then ready sessions.</summary>
    public static int GetDisplaySortOrder(SessionInfo session) => GetDisplayState(session) switch
    {
        SessionDisplayState.Working => 0,
        SessionDisplayState.NeedsAttention => 1,
        _ => 2,
    };

    /// <summary>Successful completed runs may be hidden from the compact session list.</summary>
    public static bool IsCompleted(SessionInfo session) =>
        !IsWorking(session)
        && !session.AbortedLastRun
        && ResolveStatus(session.Status) == SessionRunStatus.Completed;

    /// <summary>
    /// A stopped run is useful context in a row or transcript, but is not a
    /// fourth primary session status: the session is still ready for another turn.
    /// </summary>
    public static bool HasStoppedLastRun(SessionInfo session) =>
        session.AbortedLastRun || ResolveStatus(session.Status) == SessionRunStatus.Stopped;
}
