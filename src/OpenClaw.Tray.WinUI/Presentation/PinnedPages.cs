using System.Collections.Immutable;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

/// <summary>One native Settings destination the user may pin to the main sidebar.</summary>
/// <param name="Tag">Canonical Settings route tag, resolved through the single Workspace history at click time.</param>
/// <param name="LabelKey">Existing Settings navigation label resource, so pins never carry a competing vocabulary.</param>
/// <param name="IconAsset">File name (without extension) under Assets/SidebarIcons shared with the Settings navigation.</param>
internal sealed record PinnablePage(string Tag, string LabelKey, string IconAsset);

/// <summary>
/// Owner of the pinnable native page catalog and of pin list rules. Home (fixed), Settings, the
/// This Computer group and Debug (diagnostics-gated) are not pinnable; agent-scoped routes use the
/// <c>cron</c> tag, which is normalized to the selected agent when the route is admitted.
/// Pins are a local, per-Gateway shortcut list and never grant access to a page.
/// </summary>
internal static class PinnedPages
{
    /// <summary>Scope used before any Gateway is saved. The parentheses keep it distinct from any generated Gateway id.</summary>
    public const string UnconfiguredScope = "(unconfigured)";

    public static ImmutableArray<PinnablePage> Catalog { get; } =
    [
        new("connection", "HubWindow_NavigationViewItem_88.Content", "Connection"),
        new("sessions", "HubWindow_NavigationViewItem_91.Content", "Sessions"),
        new("skills", "HubWindow_NavigationViewItem_97.Content", "Skills"),
        new("channels", "HubWindow_NavigationViewItem_109.Content", "Channels"),
        new("instances", "HubWindow_NavigationViewItem_112.Content", "Instances"),
        new("cron", "HubWindow_NavigationViewItem_124.Content", "Cron"),
        new("agentevents", "HubWindow_NavigationViewItem_94.Content", "AgentEvents"),
        new("bindings", "HubWindow_NavigationViewItem_115.Content", "Bindings"),
        new("config", "HubWindow_NavigationViewItem_118.Content", "Config"),
        new("usage", "HubWindow_NavigationViewItem_121.Content", "Usage"),
    ];

    public static string ScopeKey(string? activeGatewayId) =>
        string.IsNullOrWhiteSpace(activeGatewayId) ? UnconfiguredScope : activeGatewayId.Trim();

    public static PinnablePage? Find(string? tag) =>
        tag is null ? null : Catalog.FirstOrDefault(page => string.Equals(page.Tag, tag, StringComparison.Ordinal));

    /// <summary>
    /// Storage normalization: keeps order, drops null/blank entries and duplicates. Unknown tags are
    /// preserved so a newer or older build never erases pins it does not recognize.
    /// </summary>
    public static ImmutableArray<string> Normalize(IEnumerable<string>? stored)
    {
        if (stored is null)
            return [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (var raw in stored)
        {
            var tag = raw?.Trim();
            if (!string.IsNullOrEmpty(tag) && seen.Add(tag))
                result.Add(tag);
        }
        return result.ToImmutable();
    }

    /// <summary>Pins append to the end of the stored order; unpinning removes only that tag.</summary>
    public static ImmutableArray<string> Toggle(IEnumerable<string>? current, string tag, bool pinned)
    {
        var normalized = Normalize(current);
        if (Find(tag) is null)
            return normalized;
        if (!pinned)
            return normalized.Remove(tag);
        return normalized.Contains(tag) ? normalized : normalized.Add(tag);
    }

    /// <summary>
    /// Route for invoking a pin: the generic stored tag is resolved against the assistant currently
    /// selected in Chat, so changing that assistant retargets agent-scoped pins without rewriting storage.
    /// </summary>
    public static string ResolveRoute(string tag, string? chatAgentId) =>
        WorkspaceNavigation.NormalizeSettingsTag(tag, string.IsNullOrWhiteSpace(chatAgentId) ? "main" : chatAgentId);

    /// <summary>Availability controls invocation and enabled state, never whether a saved pin is shown.</summary>
    public static bool IsAvailable(string tag, ConnectionStatus status) =>
        !HubPageRegistry.IsGatewayPageTag(tag) ||
        GatewayNavVisibilityDebouncePolicy.GetDecision(status, debounceDisconnected: false)
            == GatewayNavVisibilityDecision.ShowNow;

    /// <summary>Effective shortcuts: catalog-allowed pages in stored order, including unavailable destinations.</summary>
    public static ImmutableArray<PinnablePage> Visible(IEnumerable<string>? stored) =>
        [.. Normalize(stored)
            .Select(Find)
            .OfType<PinnablePage>()];
}
