// Allocates history chrome once. Example: EmbeddedPageTopAllocation(embedded, paneOpen, supportsInset).
namespace OpenClawTray.Presentation;

/// <summary>Pure content allocation policy; navigation identity and pane preferences remain host-owned.</summary>
internal static class WorkspaceSettingsLayout
{
    public const double NavigationChromeHeight = 56;

    public static double PageTopInset(bool contentOnly, bool paneOpen) =>
        !contentOnly && !paneOpen ? NavigationChromeHeight : 0;

    /// <summary>Opted-in pages retain full-frame backdrops; ordinary pages keep the existing outer margin.</summary>
    public static (double HostMarginTop, double PageTopInset) EmbeddedPageTopAllocation(
        bool embedded, bool workspacePaneOpen, bool supportsInset)
    {
        if (!embedded || workspacePaneOpen) return (0, 0);
        return supportsInset ? (0, NavigationChromeHeight) : (NavigationChromeHeight, 0);
    }
}
