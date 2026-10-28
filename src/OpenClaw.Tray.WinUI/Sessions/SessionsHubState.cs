using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;

namespace OpenClawTray.Sessions;

internal enum SessionsHubTab
{
    Sessions,
    Worktrees,
}

internal enum SessionsHubLoad
{
    Idle,
    Loading,
    Loaded,
    Failed,
    Unsupported,
}

/// <summary>Roster filters as the user typed them; <see cref="ToQuery"/> normalizes them for the Gateway.</summary>
internal sealed record SessionsHubFilters(
    string? AgentId = null,
    string Search = "",
    SessionArchiveFilter Status = SessionArchiveFilter.Active,
    string ActiveMinutes = "",
    string Limit = "50",
    bool IncludeGlobal = true,
    bool IncludeUnknown = false,
    bool ShowCompleted = false,
    bool ShowBackground = false)
{
    public SessionListQuery ToQuery(int offset = 0, int? limitOverride = null) => new()
    {
        AgentId = AgentId,
        Search = Search.Trim(),
        Archive = Status,
        ActiveMinutes = SessionListQuery.ParsePositiveInteger(ActiveMinutes),
        IncludeGlobal = IncludeGlobal,
        IncludeUnknown = IncludeUnknown,
        Limit = limitOverride ?? SessionListQuery.ParsePositiveInteger(Limit) ?? SessionListQuery.DefaultLimit,
        Offset = offset,
    };

    /// <summary>True when filters, not an empty Gateway, may explain an empty list.</summary>
    public bool IsNarrowed =>
        Search.Trim().Length > 0
        || (Status == SessionArchiveFilter.Active && SessionListQuery.ParsePositiveInteger(ActiveMinutes) is not null)
        || !IncludeGlobal;

    /// <summary>The Windows completed/background toggles stay client-side and apply to the active roster.</summary>
    public IEnumerable<SessionInfo> ApplyClientFilters(IEnumerable<SessionInfo> rows) =>
        rows.Where(row => SessionDisplayResolver.IsVisible(row, ShowBackground))
            .Where(row => Status != SessionArchiveFilter.Active || ShowCompleted || !SessionRunState.IsCompleted(row));

    public SessionsHubFilters Cleared() => this with
    {
        Search = "", ActiveMinutes = "", IncludeGlobal = true, IncludeUnknown = false,
    };
}

/// <summary>A transient page message; <see cref="Action"/> renders as the InfoBar action (Undo, Retry).</summary>
internal sealed record SessionsHubNotice(
    string Title,
    string Message,
    InfoBarSeverity Severity,
    string? ActionLabel = null,
    Action? Action = null);

internal sealed record TranscriptSearchState(
    string Query = "",
    bool IsOpen = false,
    bool IsSearching = false,
    string? Error = null,
    SessionSearchResult? Result = null);

internal sealed record WorktreesState(
    SessionsHubLoad Load = SessionsHubLoad.Idle,
    IReadOnlyList<WorktreeRecord>? Rows = null,
    string? Error = null,
    string? BusyId = null,
    bool IsCleaning = false);

/// <summary>Immutable snapshot rendered by <see cref="SessionsHubRoot"/>.</summary>
internal sealed record SessionsHubState
{
    public SessionsHubTab Tab { get; init; }
    public bool IsConnected { get; init; }
    public SessionsHubPermissions Permissions { get; init; } = SessionsHubPermissions.Disconnected;
    public IReadOnlyList<SessionsHubAgent> Agents { get; init; } = Array.Empty<SessionsHubAgent>();
    public SessionsHubFilters Filters { get; init; } = new();
    public SessionsGroupBy GroupBy { get; init; }
    public SessionsSortColumn SortColumn { get; init; } = SessionsSortColumn.Updated;
    public bool SortDescending { get; init; } = true;
    public int PageSize { get; init; } = SessionsHubProjection.DefaultPageSize;
    public int PageIndex { get; init; }

    public SessionsHubLoad Load { get; init; }
    public string? LoadError { get; init; }
    public IReadOnlyList<SessionInfo> Rows { get; init; } = Array.Empty<SessionInfo>();
    public bool HasMore { get; init; }
    public int? NextOffset { get; init; }
    public int? TotalCount { get; init; }
    public IReadOnlyList<string> GroupCatalog { get; init; } = Array.Empty<string>();
    public bool GroupsSupported { get; init; }

    public string? DetailsKey { get; init; }
    public (string? Key, string Text, string? SessionId) LabelDraft { get; init; } = (null, "", null);
    public bool SelectionMode { get; init; }
    public SessionsHubSelection Selection { get; init; } = SessionsHubSelection.Empty;
    public bool IsBusy { get; init; }
    /// <summary>A roster reload is in flight; appending a page now would mix filters.</summary>
    public bool IsReloading { get; init; }
    public SessionsHubNotice? Notice { get; init; }

    public TranscriptSearchState Transcript { get; init; } = new();
    public IReadOnlyList<GatewayUserProfile> Profiles { get; init; } = Array.Empty<GatewayUserProfile>();
    public string? SelfProfileId { get; init; }
    public WorktreesState Worktrees { get; init; } = new();
    /// <summary>The open confirmation or input dialog; rendered as a controlled Reactor ContentDialog.</summary>
    public SessionsHubDialog? Dialog { get; init; }

    public SessionInfo? DetailsRow => DetailsKey is null ? null : Rows.FirstOrDefault(row => row.Key == DetailsKey);
}

internal enum SessionsHubDialogKind
{
    Confirm,
    Prompt,
    NewWorktree,
}

/// <summary>
/// One pending dialog. <see cref="Completion"/> resolves with the final dialog on confirm, null on cancel;
/// text and worktree fields are drafts updated as the user types.
/// </summary>
internal sealed record SessionsHubDialog(
    SessionsHubDialogKind Kind,
    string Title,
    string Body,
    string ConfirmLabel,
    bool Destructive,
    TaskCompletionSource<SessionsHubDialog?> Completion)
{
    public string Text { get; init; } = "";
    public string? Placeholder { get; init; }
    public string Repo { get; init; } = "";
    public string Name { get; init; } = "";
    public string BaseRef { get; init; } = "";
    public IReadOnlyList<string> Branches { get; init; } = Array.Empty<string>();
    public string? Status { get; init; }

    public bool IsValid => Kind switch
    {
        SessionsHubDialogKind.Prompt => Text.Trim().Length > 0,
        SessionsHubDialogKind.NewWorktree => Repo.Trim().Length > 0 && OpenClaw.Shared.Sessions.WorktreesProjection.IsValidName(Name.Trim()),
        _ => true,
    };
}

/// <summary>An agent as the page shows it; <paramref name="Emoji"/> is its identity avatar when set.</summary>
internal sealed record SessionsHubAgent(string Id, string Name, string? Emoji = null);
