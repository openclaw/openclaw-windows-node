namespace OpenClaw.Shared;

/// <summary>
/// P2 (companion reliability): retention policy for the session catalog when a
/// refresh returns no rows. A transient or failed sessions.list must not delete a
/// catalog we already hold; an explicit authoritative count still wins.
/// </summary>
internal static class SessionCatalogRetention
{
    internal static bool ShouldPreserveOnEmpty(int incomingKeyCount, int heldCount, int? authoritativeCount)
        => incomingKeyCount == 0 && heldCount > 0 && authoritativeCount != 0;
}
