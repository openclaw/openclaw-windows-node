// Projects Settings chrome from the host width without changing navigation identity.
// Usage: WorkspaceSettingsLayout.Project(hostWidth, embedded).
namespace OpenClawTray.Presentation;

/// <summary>Pure layout policy shared by the window applicator and Settings pane.</summary>
internal readonly record struct WorkspaceSettingsLayout(bool Narrow, bool Embedded)
{
    public const double NavigationLabelWidth = 720;
    public const double NavigationChromeHeight = 56;

    /// <summary>Uses window width, not the smaller embedded page width.</summary>
    public static WorkspaceSettingsLayout Project(double hostWidth, bool embedded) =>
        new(hostWidth > 0 && hostWidth < NavigationLabelWidth,
            embedded && !(hostWidth > 0 && hostWidth < NavigationLabelWidth));

    /// <summary>A narrow expansion is temporary and never changes the wide-pane preference.</summary>
    public bool PaneOpen(bool preferredOpen, bool narrowExpanded) =>
        Narrow ? narrowExpanded : preferredOpen;

    /// <summary>Full Settings needs a history row above content when its own rail is collapsed.</summary>
    public static double PageTopInset(bool contentOnly, bool paneOpen) =>
        !contentOnly && !paneOpen ? NavigationChromeHeight : 0;

    /// <summary>
    /// Allocates the collapsed Workspace pane's history band exactly once. An opted-in embedded
    /// page keeps its full-frame backdrop and receives the inset; ordinary pages retain the
    /// existing outer host margin.
    /// </summary>
    public static (double HostMarginTop, double PageTopInset) EmbeddedPageTopAllocation(
        bool embedded, bool workspacePaneOpen, bool supportsInset)
    {
        if (!embedded || workspacePaneOpen)
            return (0, 0);

        return supportsInset
            ? (0, NavigationChromeHeight)
            : (NavigationChromeHeight, 0);
    }
}
