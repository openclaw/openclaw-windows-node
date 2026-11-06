using OpenClaw.Shared.Usage;

namespace OpenClawTray.Presentation.Usage;

/// <summary>Selects the current Gateway summary for the expanded session.</summary>
internal static class UsageSessionDetailProjection
{
    /// <summary>
    /// The single-session read covers the selected range and scope, so it is preferred. The list row is the
    /// fallback while that read loads or when it fails.
    /// </summary>
    public static SessionUsageRow? SummaryRow(SessionUsageRow? listRow, SessionUsageRow? detail) => detail ?? listRow;
}
