namespace OpenClawTray.Chat;

/// <summary>
/// Explicit load-earlier presentation state. Unknown/unavailable is deliberately distinct from an
/// authoritative exhaustion, and a transient failure is a retryable error rather than "complete".
/// </summary>
public enum ChatLoadOlderState
{
    Unavailable,
    Available,
    Loading,
    Exhausted,
    Error,
}
