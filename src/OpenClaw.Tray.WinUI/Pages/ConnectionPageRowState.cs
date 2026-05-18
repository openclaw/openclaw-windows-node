using OpenClaw.Connection;

namespace OpenClawTray.Pages;

/// <summary>
/// Pure helpers describing the per-row state mapping on the
/// <see cref="ConnectionPage"/> saved-gateways list. Extracted from the
/// WinUI page so unit tests (which run as pure net10.0) can pin the
/// predicate without touching XAML types.
/// </summary>
internal static class ConnectionPageRowState
{
    /// <summary>
    /// Returns true when the active row's overflow menu should offer
    /// "Disconnect". Tear-down is meaningful while the connection is
    /// live (Connected / Ready / Degraded) or in transit
    /// (Connecting / PairingRequired). It is NOT meaningful while the
    /// teardown itself is in flight (Disconnecting) — re-entering would
    /// race the connection manager. Error and Idle return false because
    /// those states render a [Connect] button (no badge) and the Recovery
    /// card / Welcome panel above already exposes Disconnect when needed.
    /// </summary>
    internal static bool CanDisconnectFromBadge(OverallConnectionState state) => state is
        OverallConnectionState.Connected
        or OverallConnectionState.Ready
        or OverallConnectionState.Degraded
        or OverallConnectionState.Connecting
        or OverallConnectionState.PairingRequired;

    /// <summary>
    /// Returns true when the active row should render a status badge.
    /// Returns false for Idle (row renders [Connect]) and Error (the row
    /// also renders [Connect] so the user can explicitly retry; the
    /// status strip up top already carries the "broken" signal).
    /// </summary>
    internal static bool HasActiveRowBadge(OverallConnectionState state) => state is
        OverallConnectionState.Connected
        or OverallConnectionState.Ready
        or OverallConnectionState.Degraded
        or OverallConnectionState.Connecting
        or OverallConnectionState.PairingRequired
        or OverallConnectionState.Disconnecting;
}
