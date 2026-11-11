// Classifies reported measurement freshness. Usage: SystemsFreshness.IsLastKnown(row, connected, failed, now).
namespace OpenClaw.Shared.Systems;

/// <summary>Decides whether shown measurements are live or a last-known report.</summary>
public static class SystemsFreshness
{
    public static readonly TimeSpan GatewayStaleAfter = TimeSpan.FromSeconds(30);
    // Nodes publish once a minute; allow one missed report before marking their data stale.
    public static readonly TimeSpan NodeStaleAfter = TimeSpan.FromSeconds(120);

    public static TimeSpan StaleAfter(string id) =>
        id == SystemsProjection.GatewayId ? GatewayStaleAfter : NodeStaleAfter;

    /// <summary>
    /// True when the shown measurements are a last-known report rather than live data:
    /// disconnected, offline, a failed refresh, or an old sample.
    /// </summary>
    public static bool IsLastKnown(SystemRow row, bool connected, bool refreshFailed, DateTimeOffset now) =>
        !connected ||
        row.Status != SystemStatus.Online ||
        refreshFailed ||
        (row.MeasuredAt is { } at && now - at > StaleAfter(row.Id));
}
