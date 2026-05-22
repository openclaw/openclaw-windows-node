namespace OpenClaw.Shared.ExecApprovals;

// Determines whether the coordinator can present a UI prompt for this request.
// Doc 08 F1 lists four inputs to canPresent: requestSessionKey, activeSessionKey,
// lastInputSeconds, desktopInteractive. Only requestSessionKey is passed by the
// coordinator — the other three are encapsulated inside the implementation:
//   activeSessionKey: provided by whatever tracks the active tray session.
//   lastInputSeconds: read via Win32 GetLastInputInfo (OQ-F1).
//   desktopInteractive: read via OpenInputDesktop / WTSQuerySessionInformation (OQ-F1).
// Keeping these out of the interface keeps the coordinator UI-free (rail 10) and
// testable without Win32. Must never throw — fail to false (no UI available).
public interface ICanPresentEvaluator
{
    bool CanPresent(string? requestSessionKey);
}

// Default for PR7: UI not wired yet. Everything routes to FallbackDecision.
public sealed class AlwaysCannotPresentEvaluator : ICanPresentEvaluator
{
    public static readonly AlwaysCannotPresentEvaluator Instance = new();
    public bool CanPresent(string? requestSessionKey) => false;
}

// Test double: always reports UI available. Used in coordinator tests to
// exercise the prompt path with the null prompt handler.
public sealed class AlwaysCanPresentEvaluator : ICanPresentEvaluator
{
    public static readonly AlwaysCanPresentEvaluator Instance = new();
    public bool CanPresent(string? requestSessionKey) => true;
}
