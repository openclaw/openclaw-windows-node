namespace OpenClaw.Shared;

/// <summary>
/// Narrow, pure action policy for the tray session-catalog retry surface. The UI handler consumes
/// this so the retry-coalescing rule is unit-testable off the UI thread. It deliberately does NOT
/// coalesce explicit OpenClawGatewayClient.RequestSessionsAsync calls (those supersede by design);
/// it only decides whether the visible Retry action may run.
/// </summary>
public static class SessionCatalogRetryActionPolicy
{
    /// <summary>Retry is enabled only while a retry is actually needed and no acquisition/send is in flight.</summary>
    public static bool IsRetryEnabled(bool connected, bool retryRequired, bool acquisitionInProgress, bool sendInFlight)
        => connected && retryRequired && !acquisitionInProgress && !sendInFlight;

    /// <summary>Same rule, used to guard a click so repeat presses cannot supersede the current deadline.</summary>
    public static bool CanStartRetry(bool connected, bool retryRequired, bool acquisitionInProgress, bool sendInFlight)
        => IsRetryEnabled(connected, retryRequired, acquisitionInProgress, sendInFlight);

    /// <summary>The retry prompt is shown only while connected and a retry is required.</summary>
    public static bool IsRetryPromptVisible(bool connected, bool retryRequired)
        => connected && retryRequired;
}
