namespace OpenClaw.Shared.Dashboards;

/// <summary>What a widget cell renders in its body.</summary>
public enum BoardWidgetBodyKind
{
    /// <summary>Ticketed web content in the isolated sandbox host.</summary>
    Frame,
    /// <summary>MCP app view: not hosted on Windows; shown as a placeholder.</summary>
    McpApp,
    /// <summary>Capabilities requested; the user must allow or reject.</summary>
    NeedsApproval,
    /// <summary>The user rejected the requested capabilities.</summary>
    Rejected,
    /// <summary>Native <c>session:report</c> renderer.</summary>
    Report,
    /// <summary><c>session:website</c>: a validated link that opens in the browser.</summary>
    Website,
    /// <summary>Trusted core kind without a native renderer yet.</summary>
    UnsupportedCorePlugin,
    /// <summary>Plugin kind not advertised by the Gateway (disabled or unknown plugin).</summary>
    DisabledPlugin,
    /// <summary>Content cannot load (no ticket was minted for an HTML widget).</summary>
    Unavailable,
}

/// <summary>
/// Chooses a widget body, matching upstream <c>board-widget-cell.solid.tsx</c>
/// and <c>lib/board/widgets/index.ts</c>: pending and rejected grants block
/// content; plugin kinds render only when the Gateway advertises them.
/// </summary>
public static class BoardWidgetBodyPolicy
{
    public const string ReportKind = "session:report";
    public const string WebsiteKind = "session:website";
    public const string ProgressKind = "session:progress";
    public const string BrowserDashboardKind = "browser:dashboard";

    private static readonly string[] s_coreKinds = [ProgressKind, ReportKind, WebsiteKind, BrowserDashboardKind];

    public static BoardWidgetBodyKind Resolve(BoardWidget widget, IReadOnlyList<PluginWidgetKind> advertisedKinds)
    {
        if (widget.GrantState == BoardWidgetGrantState.Pending)
            return BoardWidgetBodyKind.NeedsApproval;
        if (widget.GrantState == BoardWidgetGrantState.Rejected)
            return BoardWidgetBodyKind.Rejected;
        if (widget.ContentKind == BoardWidgetContentKind.McpApp)
            return BoardWidgetBodyKind.McpApp;
        if (widget.ContentKind == BoardWidgetContentKind.Html || !string.IsNullOrEmpty(widget.FrameUrl))
            return string.IsNullOrEmpty(widget.FrameUrl) || string.IsNullOrEmpty(widget.ViewTicket)
                ? BoardWidgetBodyKind.Unavailable
                : BoardWidgetBodyKind.Frame;

        if (!IsAdvertisedCoreKind(widget, advertisedKinds))
            return BoardWidgetBodyKind.DisabledPlugin;
        return widget.PluginKind switch
        {
            ReportKind => BoardWidgetBodyKind.Report,
            WebsiteKind => BoardWidgetBodyKind.Website,
            _ => BoardWidgetBodyKind.UnsupportedCorePlugin,
        };
    }

    /// <summary>Label of an advertised core plugin kind, if any.</summary>
    public static string? AdvertisedLabel(BoardWidget widget, IReadOnlyList<PluginWidgetKind> advertisedKinds) =>
        IsAdvertisedCoreKind(widget, advertisedKinds)
            ? advertisedKinds.First(kind => kind.Kind == widget.PluginKind).Label
            : null;

    private static bool IsAdvertisedCoreKind(BoardWidget widget, IReadOnlyList<PluginWidgetKind> advertisedKinds)
    {
        var kind = widget.PluginKind;
        if (kind is null || Array.IndexOf(s_coreKinds, kind) < 0)
            return false;
        var pluginId = widget.PluginId;
        return advertisedKinds.Any(entry => entry.Kind == kind && entry.PluginId == pluginId);
    }
}
