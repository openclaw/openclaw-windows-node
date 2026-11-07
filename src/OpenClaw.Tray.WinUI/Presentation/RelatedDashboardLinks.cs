// <summary>
// Selects the Dashboard pages shown under "Related settings" at the bottom of a
// Settings page. Paths come only from HubSearchCatalog.DashboardRoutes and are
// gated by HubSearchCatalog.SupportsDashboard, so this is a page projection of the
// verified route catalog, not a second route table.
// Usage:
//   var links = RelatedDashboardLinks.For("sandbox", self?.ServerVersion, canAdmin);
//   foreach (var link in links) ((IAppCommands)app).OpenDashboard(link.Path);
// </summary>

namespace OpenClawTray.Presentation;

/// <summary>One verified Dashboard destination related to a native Settings page.</summary>
internal sealed record RelatedDashboardLink(string Id, string Path)
{
    /// <summary>Title shared with the Dashboard search destination.</summary>
    public string TitleKey => $"Search_Dashboard_{Id}";

    public string DescriptionKey => $"RelatedSettings_{Id}_Description";
}

internal static class RelatedDashboardLinks
{
    /// <summary>
    /// Route ids from <see cref="HubSearchCatalog.DashboardRoutes"/>, in display order.
    /// A page's own counterpart is left to the footer Dashboard button. Connection and
    /// Config are deliberately absent while their page-level Dashboard buttons are retired.
    /// </summary>
    internal static readonly IReadOnlyDictionary<HubPageKind, string[]> PageRoutes =
        new Dictionary<HubPageKind, string[]>
        {
            [HubPageKind.Instances] = ["Systems", "Approvals", "Logs"],
            [HubPageKind.Cron] = ["Automation", "Activity", "Logs"],
            [HubPageKind.Usage] = ["Activity", "Models"],
            [HubPageKind.Sessions] = ["Activity", "Logs"],
            [HubPageKind.DefaultModel] = ["Models"],
            [HubPageKind.LocalAi] = ["Models"],
            [HubPageKind.Permissions] = ["Approvals", "Security", "Secrets"],
            [HubPageKind.Sandbox] = ["Approvals", "Security", "Secrets"],
            [HubPageKind.Debug] = ["Logs", "Activity"],
        };

    /// <summary>
    /// Returns the related Dashboard links for a page tag, or none when the Gateway
    /// version has no verified routes. Admin-only routes require <paramref name="canAdmin"/>.
    /// </summary>
    internal static IReadOnlyList<RelatedDashboardLink> For(string? pageTag, string? gatewayVersion, bool canAdmin)
    {
        if (!HubSearchCatalog.SupportsDashboard(gatewayVersion) ||
            HubPageRegistry.ResolvePage(pageTag) is not { } page ||
            !PageRoutes.TryGetValue(page, out var ids))
            return [];

        return ids
            .Select(id => HubSearchCatalog.DashboardRoutes.Single(route => route.Id == id))
            .Where(route => canAdmin || !route.Admin)
            .Select(route => new RelatedDashboardLink(route.Id, route.Path))
            .ToArray();
    }
}
