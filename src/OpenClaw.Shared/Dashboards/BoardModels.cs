using System.Collections.Immutable;
using System.Text.Json;

namespace OpenClaw.Shared.Dashboards;

/// <summary>Content kind of a board widget (<c>BoardWidgetSchema.contentKind</c>).</summary>
public enum BoardWidgetContentKind
{
    Html,
    McpApp,
    Plugin,
}

/// <summary>Capability approval state of a widget (<c>grantState</c>).</summary>
public enum BoardWidgetGrantState
{
    None,
    Pending,
    Granted,
    Rejected,
}

public enum BoardWidgetPresentation
{
    Card,
    FullBleed,
    Frameless,
}

public enum BoardWidgetHeightMode
{
    Auto,
    Fixed,
}

/// <summary>Capabilities a widget declared and must be granted before use.</summary>
public sealed record BoardWidgetDeclared(
    ImmutableArray<string> NetOrigins,
    ImmutableArray<string> Tools)
{
    public static BoardWidgetDeclared Empty { get; } = new([], []);

    public bool IsEmpty => NetOrigins.IsDefaultOrEmpty && Tools.IsDefaultOrEmpty;
}

public sealed record BoardTab(string TabId, string Title, int Position, string ChatDock);

/// <summary>
/// One widget from a board snapshot. Ticket and sandbox fields are minted by
/// <c>board.get</c> only for widgets whose content may load (grant state
/// <c>none</c> or <c>granted</c>).
/// </summary>
public sealed record BoardWidget
{
    public required string Name { get; init; }
    public required string TabId { get; init; }
    public string? Title { get; init; }
    public required BoardWidgetContentKind ContentKind { get; init; }
    public string? PluginKind { get; init; }
    /// <summary>Plugin props as raw JSON (cloned, detached from the response document).</summary>
    public JsonElement? Props { get; init; }
    public BoardWidgetPresentation Presentation { get; init; } = BoardWidgetPresentation.Card;
    /// <summary>Absent height mode means auto by upstream design.</summary>
    public BoardWidgetHeightMode HeightMode { get; init; } = BoardWidgetHeightMode.Auto;
    public required int SizeW { get; init; }
    public required int SizeH { get; init; }
    public required int Position { get; init; }
    public BoardWidgetGrantState GrantState { get; init; } = BoardWidgetGrantState.None;
    public required int Revision { get; init; }
    public string? InstanceId { get; init; }
    public ImmutableArray<string> DeclaredSummary { get; init; } = [];
    public BoardWidgetDeclared Declared { get; init; } = BoardWidgetDeclared.Empty;
    public string? FrameUrl { get; init; }
    public string? ViewTicket { get; init; }
    public int? ViewTicketTtlMs { get; init; }
    public string? ViewGeneration { get; init; }
    public string? SandboxUrl { get; init; }
    public int? SandboxPort { get; init; }
    public string? SandboxOrigin { get; init; }
    public string? KindLabel { get; init; }

    /// <summary>Visible label: title, else the stable widget name.</summary>
    public string Label => string.IsNullOrWhiteSpace(Title) ? Name : Title!;

    /// <summary>Plugin id prefix of <see cref="PluginKind"/> (<c>plugin:widget</c>).</summary>
    public string PluginId
    {
        get
        {
            var kind = PluginKind?.Trim();
            if (string.IsNullOrEmpty(kind))
                return "unknown";
            var separator = kind.IndexOf(':');
            var id = separator < 0 ? kind : kind[..separator];
            return string.IsNullOrWhiteSpace(id) ? "unknown" : id;
        }
    }
}

public sealed record BoardSnapshot(
    string SessionKey,
    int Revision,
    ImmutableArray<BoardTab> Tabs,
    ImmutableArray<BoardWidget> Widgets)
{
    /// <summary>Tabs in display order: position, then id.</summary>
    public ImmutableArray<BoardTab> OrderedTabs =>
        [.. Tabs.OrderBy(tab => tab.Position).ThenBy(tab => tab.TabId, StringComparer.Ordinal)];

    /// <summary>Widgets of one tab in canonical order: position, then name.</summary>
    public ImmutableArray<BoardWidget> WidgetsForTab(string tabId) =>
        [.. Widgets
            .Where(widget => string.Equals(widget.TabId, tabId, StringComparison.Ordinal))
            .OrderBy(widget => widget.Position)
            .ThenBy(widget => widget.Name, StringComparer.Ordinal)];

    /// <summary>
    /// Upstream default tab: the first ordered tab, else the first widget's tab.
    /// Returns an empty string for an empty board.
    /// </summary>
    public string DefaultTabId =>
        OrderedTabs.FirstOrDefault()?.TabId ?? Widgets.FirstOrDefault()?.TabId ?? "";
}

/// <summary>A board target: the session key and its owning agent.</summary>
public sealed record BoardSessionTarget(string SessionKey, string? AgentId);

/// <summary>Projected actor (<c>SessionCreatedActorSchema</c>) with display label.</summary>
public sealed record DashboardActor(string? Id, string? Label);

/// <summary>One dashboard gallery row from <c>sessions.list</c> with <c>rowMode:"dashboard"</c>.</summary>
public sealed record DashboardSessionRow
{
    public required SessionInfo Session { get; init; }
    public string Key => Session.Key;
    public string? AgentId => Session.AgentId;
    public string? Status { get; init; }
    public long? UpdatedAtMs { get; init; }
    public string? LastMessagePreview { get; init; }
    public DashboardActor? CreatedActor { get; init; }
    public DashboardActor? OwnerActor { get; init; }
    public bool Archived { get; init; }

    /// <summary>Upstream live indicator: the session's run status is <c>running</c>.</summary>
    public bool IsLive => string.Equals(Status, "running", StringComparison.Ordinal);
}

public sealed record DashboardSessionPage(
    ImmutableArray<DashboardSessionRow> Rows,
    bool HasMore,
    int? NextOffset,
    int Count);

/// <summary><c>board.command</c> event payload.</summary>
public sealed record BoardCommandEvent(string SessionKey, string Kind, string? TabId, string? Dock);

/// <summary><c>board.changed</c> event payload.</summary>
public sealed record BoardChangedEvent(string SessionKey, int Revision, string? Widget);

/// <summary>Trusted core plugin widget kind advertised by the Gateway hello (<c>controlUiWidgetKinds</c>).</summary>
public sealed record PluginWidgetKind(string PluginId, string Kind, string Label);
