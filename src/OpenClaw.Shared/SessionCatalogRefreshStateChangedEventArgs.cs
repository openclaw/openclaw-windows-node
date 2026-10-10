using System;

namespace OpenClaw.Shared;

/// <summary>
/// Raised after a session-catalog refresh transition is accepted (retry needed, or cleared),
/// outside any acquisition lock. Lets UI surface a retry/error state even when the session
/// collection itself did not change (timeout, rejected page, failed send, parse failure).
/// </summary>
public sealed class SessionCatalogRefreshStateChangedEventArgs : EventArgs
{
    public bool RetryRequired { get; init; }

    /// <summary>True while a bounded acquisition is in progress (begin..terminal). UI disables
    /// Retry until this returns to false at an actual terminal transition, not at socket-send completion.</summary>
    public bool AcquisitionInProgress { get; init; }
}
