// Allocates history chrome once. Example: EmbeddedPageTopAllocation(embedded, paneOpen, supportsInset).
namespace OpenClawTray.Presentation;

/// <summary>Pure content allocation policy; navigation identity and pane preferences remain host-owned.</summary>
internal static class WorkspaceSettingsLayout
{
    public const double NavigationChromeHeight = 56;

    public static double PageTopInset(bool contentOnly, bool paneOpen) =>
        !contentOnly ? NavigationChromeHeight : 0;

    /// <summary>Every embedded page clears the native chrome band, regardless of pane visibility.</summary>
    public static (double HostMarginTop, double PageTopInset) EmbeddedPageTopAllocation(
        bool embedded, bool workspacePaneOpen, bool supportsInset)
    {
        if (!embedded) return (0, 0);
        return (0, NavigationChromeHeight);
    }
}
