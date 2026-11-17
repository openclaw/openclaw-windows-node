// Read-only conversation tabs. Bind the current provider/history before Open;
// requests retain identities only, never cached tool payloads or write authority.
// Tool details follow the loaded history; the session dashboard follows the session.
using OpenClaw.Chat;
using OpenClaw.Shared.Dashboards;

namespace OpenClawTray.Chat;

/// <summary>UI-thread-owned tab selection and layout for one mounted conversation.</summary>
internal sealed class ChatConversationPanelState
{
    internal const double MinimumPanelWidth = 280;
    internal const double MaximumPanelWidth = 800;
    internal const double MinimumConversationWidth = 360;
    internal const double ResizeStep = 16;
    internal const double DividerWidth = 12;
    /// <summary>Tab width that must remain beside window controls before the header joins the caption row.</summary>
    internal const double MinimumTitleRowTabsWidth = 160;
    internal const string DetailsSlot = "details";
    internal const string SubagentsSlot = "subagents";
    internal const string DashboardSlot = "dashboard";
    internal const string FilesSlot = "files";
    // Closing a dashboard suppresses automatic reveals for that session while chat stays mounted.
    private readonly HashSet<string> _dismissedDashboards = new(StringComparer.Ordinal);
    // The dashboard tab was revealed automatically and the user has not opened it yet.
    private bool _dashboardUnseen;
    internal const string FileSlot = "file";
    private object? _provider;
    private string? _session;
    private long _connectionGeneration;
    private long _timelineGeneration;
    private long _historyRevision;
    private long _profileRevision;
    private IReadOnlyList<ChatTimelineItem> _entries = [];
    private Func<bool> _isCurrent = () => false;
    private Func<bool> _subagentsCurrent = () => false;
    private readonly List<string> _tabs = [];

    internal event Action? Changed;
    internal IReadOnlyList<string> Tabs => _tabs;
    internal string? Selected { get; private set; }
    internal ChatToolDetailRequest? Details { get; private set; }
    internal BoardSessionTarget? Dashboard { get; private set; }
    /// <summary>The selection came from an automatic reveal: show it beside a wide conversation only, without focus.</summary>
    internal bool Passive { get; private set; }
    internal ChatWorkspaceFileRequest? File { get; private set; }
    internal double Width { get; private set; } = 440;
    internal long Revision { get; private set; }
    internal long OpenVersion { get; private set; }
    /// <summary>The user minimized the panel. Tabs are kept; an explicit open shows it again, automatic reveals do not.</summary>
    internal bool Hidden { get; private set; }
    /// <summary>The panel was opened with no tabs and shows the panel launcher.</summary>
    internal bool LauncherOpen { get; private set; }
    /// <summary>The user asked the panel to fill the conversation area.</summary>
    internal bool Expanded { get; private set; }
    /// <summary>Host-reported: the panel is on screen.</summary>
    internal bool Presented { get; private set; }
    /// <summary>Host-reported: the panel cannot sit beside the conversation, so it already fills the area.</summary>
    internal bool Narrow { get; private set; }
    internal bool IsOpen => !Hidden && (_tabs.Count > 0 || LauncherOpen);
    internal bool CanExpand => Presented && !Narrow;
    internal bool IsExpanded => Expanded && CanExpand;
    /// <summary>Height of the window chrome the joined panel header covers, or 0 when the host offers none.</summary>
    internal double TitleRowHeight { get; private set; }
    /// <summary>Top band of <see cref="TitleRowHeight"/> shared with the caption buttons, where tabs are centered.</summary>
    internal double TitleRowCaptionHeight { get; private set; }
    /// <summary>Width at the end of the caption row taken by window controls, which the panel header must leave clear.</summary>
    internal double TitleRowEndInset { get; private set; }
    /// <summary>Width at the start of the caption row taken by window controls; applies while the panel fills the area.</summary>
    internal double TitleRowStartInset { get; private set; }
    /// <summary>
    /// The mounted tab strip, published so window chrome can keep its tabs clickable under a title bar
    /// without searching the chat's visual tree. Opaque here so this state stays free of UI types.
    /// </summary>
    internal object? TabStrip { get; private set; }
    /// <summary>Host-reported: the panel header shares the window's caption row and the panel reaches the window top.</summary>
    internal bool JoinsTitleRow { get; private set; }

    /// <summary>Whether a header of <paramref name="panelWidth"/> keeps usable tab space beside the caption-row controls.</summary>
    internal bool CanJoinTitleRow(double panelWidth, bool fillsArea = false) =>
        TitleRowHeight > 0 &&
        panelWidth - TitleRowEndInset - (fillsArea ? TitleRowStartInset : 0) >= MinimumTitleRowTabsWidth;
    internal bool IsReady => _session is not null && _isCurrent();
    internal bool AreSubagentsReady => _session is not null && _subagentsCurrent();

    /// <summary>
    /// Provider/session replacement discards all tabs. Connection or history
    /// replacement discards details and subagents but keeps the session
    /// dashboard; a file tab survives history replacement but not a new
    /// connection. Streaming updates retain them all.
    /// </summary>
    internal void Bind(object provider, string? session, long connectionGeneration,
        long timelineGeneration, long historyRevision, IReadOnlyList<ChatTimelineItem> entries,
        Func<bool> isCurrent, long profileRevision = 0, Func<bool>? subagentsCurrent = null)
    {
        var selectedBefore = Selected;
        if (!ReferenceEquals(_provider, provider) || _session != session)
        {
            Clear();
            Revision++;
        }
        else if (_connectionGeneration != connectionGeneration || _timelineGeneration != timelineGeneration ||
            _historyRevision != historyRevision)
        {
            Remove(DetailsSlot);
            Remove(SubagentsSlot);
            Remove(FilesSlot);
            // A file read depends on the session and connection, not on which history page is loaded.
            if (_connectionGeneration != connectionGeneration) Remove(FileSlot);
            Revision++;
        }
        else if (_profileRevision != profileRevision)
        {
            // A verified same-account refresh may keep the tab, but not its old callbacks.
            Revision++;
        }
        _provider = provider;
        _session = session;
        _connectionGeneration = connectionGeneration;
        _timelineGeneration = timelineGeneration;
        _historyRevision = historyRevision;
        _profileRevision = profileRevision;
        _entries = entries;
        _isCurrent = isCurrent;
        _subagentsCurrent = subagentsCurrent ?? (() => false);
        if (!AreSubagentsReady && (_tabs.Contains(SubagentsSlot) || _tabs.Contains(FilesSlot) || _tabs.Contains(FileSlot)))
        {
            var selected = Selected is SubagentsSlot or FilesSlot or FileSlot;
            Remove(SubagentsSlot);
            Remove(FilesSlot);
            Remove(FileSlot);
            if (selected) Selected = _tabs.LastOrDefault();
        }
        // A fallback chosen here was not requested by the user: never move focus or cover a narrow conversation.
        if (Selected is not null && Selected != selectedBefore) Passive = true;
    }

    internal ChatTimelineItem? Resolve(ChatToolDetailRequest request) =>
        IsReady ? request.Resolve(_session, _timelineGeneration, _historyRevision, _entries) : null;

    /// <summary>The captured revision rejects delayed callbacks after navigation or replay.</summary>
    internal bool Open(ChatToolDetailRequest request, long expectedRevision)
    {
        if (Revision != expectedRevision || Resolve(request) is null) return false;
        Details = request;
        Show(DetailsSlot);
        return true;
    }

    internal bool OpenSubagents(long expectedRevision)
    {
        if (Revision != expectedRevision || !AreSubagentsReady) return false;
        Show(SubagentsSlot);
        return true;
    }

    /// <summary>Files share the Subagents admission: the current session on the current connection.</summary>
    internal bool OpenFiles(long expectedRevision)
    {
        if (Revision != expectedRevision || !AreSubagentsReady) return false;
        Show(FilesSlot);
        return true;
    }

    /// <summary>
    /// Shows one workspace file of the bound session. Like Subagents it needs only the current,
    /// connected session; the Gateway authorizes the read itself.
    /// </summary>
    internal bool OpenFile(ChatWorkspaceFileRequest request, long expectedRevision)
    {
        if (Revision != expectedRevision || !AreSubagentsReady || request.SessionKey != _session) return false;
        File = request;
        FileOpenVersion++;
        Show(FileSlot);
        return true;
    }

    /// <summary>Increments on every file open, so reopening the same file performs one fresh read.</summary>
    internal long FileOpenVersion { get; private set; }

    /// <summary>Remount key for the File tab body: the requested file and the open that showed it.</summary>
    internal string? FileBodyKey => File is { } file ? $"{file}|{FileOpenVersion}" : null;

    /// <summary>Explicit open: selects and focuses the bound session's dashboard and clears its dismissal.</summary>
    internal bool OpenDashboard(BoardSessionTarget target)
    {
        if (_session is null || target.SessionKey != _session) return false;
        _dismissedDashboards.Remove(target.SessionKey);
        Dashboard = target;
        Show(DashboardSlot);
        return true;
    }

    /// <summary>Automatic reveal: adds the tab unless dismissed, without replacing an existing selection.</summary>
    internal bool RevealDashboard(BoardSessionTarget target)
    {
        if (_session is null || target.SessionKey != _session || !CanRevealDashboard(target.SessionKey)) return false;
        _tabs.Add(DashboardSlot);
        Dashboard = target;
        _dashboardUnseen = true;
        LauncherOpen = false;
        if (Selected is null)
        {
            Selected = DashboardSlot;
            Passive = true;
        }
        Changed?.Invoke();
        return true;
    }

    internal bool CanRevealDashboard(string sessionKey) =>
        !_tabs.Contains(DashboardSlot) && !_dismissedDashboards.Contains(sessionKey);

    /// <summary>The user asked to see a passively revealed panel.</summary>
    internal void Engage()
    {
        if (!Passive) return;
        Passive = false;
        if (Selected == DashboardSlot) _dashboardUnseen = false;
        Changed?.Invoke();
    }

    /// <summary>User tab selection. Reselecting the current tab is ignored so a control echo cannot end a passive reveal.</summary>
    internal void Select(string slot)
    {
        if (!_tabs.Contains(slot) || Selected == slot) return;
        Selected = slot;
        Passive = false;
        if (slot == DashboardSlot) _dashboardUnseen = false;
        Changed?.Invoke();
    }

    internal void Close(string slot)
    {
        if (slot == DashboardSlot && Dashboard is { } dashboard)
            _dismissedDashboards.Add(dashboard.SessionKey);
        if (Remove(slot)) Changed?.Invoke();
    }

    /// <summary>
    /// Side-panel toggle: minimizes a panel shown beside the conversation, otherwise shows it,
    /// restoring the split when expanded. With no tabs it shows the panel launcher.
    /// </summary>
    internal void ToggleOpen()
    {
        if (Presented && !IsExpanded)
        {
            Hide();
            return;
        }
        Hidden = false;
        Expanded = false;
        LauncherOpen = _tabs.Count == 0;
        Passive = false;
        if (Selected == DashboardSlot) _dashboardUnseen = false;
        OpenVersion++;
        Changed?.Invoke();
    }

    /// <summary>Minimizes the panel, keeping its tabs, and ends any expansion or launcher.</summary>
    internal void Hide()
    {
        if (Hidden && !Expanded && !LauncherOpen) return;
        Hidden = true;
        Expanded = false;
        LauncherOpen = false;
        Changed?.Invoke();
    }

    /// <summary>Switches an open panel between beside the conversation and filling its area.</summary>
    internal void ToggleExpanded()
    {
        if (!IsOpen) return;
        Expanded = !Expanded;
        Passive = false;
        if (Selected == DashboardSlot) _dashboardUnseen = false;
        Changed?.Invoke();
    }

    /// <summary>The panel host reports what it shows so window chrome can mirror it.</summary>
    internal void SetPresentation(bool presented, bool narrow, bool joinsTitleRow = false)
    {
        if (Presented == presented && Narrow == narrow && JoinsTitleRow == joinsTitleRow) return;
        Presented = presented;
        Narrow = narrow;
        JoinsTitleRow = joinsTitleRow;
        Changed?.Invoke();
    }

    /// <summary>
    /// Window chrome offers its caption row to the panel header. Pass 0 height to keep the
    /// header inside the conversation area (hosts without a custom title bar never call this).
    /// A <paramref name="captionHeight"/> of 0 means the whole <paramref name="height"/>.
    /// </summary>
    internal void SetTitleRow(double height, double endInset, double startInset = 0, double captionHeight = 0)
    {
        if (!double.IsFinite(height) || !double.IsFinite(endInset) ||
            !double.IsFinite(startInset) || !double.IsFinite(captionHeight)) return;
        height = Math.Max(0, height);
        endInset = Math.Max(0, endInset);
        startInset = Math.Max(0, startInset);
        captionHeight = captionHeight > 0 ? Math.Min(captionHeight, height) : height;
        // Sub-DIP layout jitter must not re-render the chat.
        if (Math.Abs(TitleRowHeight - height) < 0.5 && Math.Abs(TitleRowEndInset - endInset) < 0.5 &&
            Math.Abs(TitleRowStartInset - startInset) < 0.5 && Math.Abs(TitleRowCaptionHeight - captionHeight) < 0.5)
            return;
        TitleRowHeight = height;
        TitleRowEndInset = endInset;
        TitleRowStartInset = startInset;
        TitleRowCaptionHeight = captionHeight;
        Changed?.Invoke();
    }

    /// <summary>The panel host publishes or withdraws its mounted tab strip.</summary>
    internal void SetTabStrip(object? tabStrip)
    {
        if (ReferenceEquals(TabStrip, tabStrip)) return;
        TabStrip = tabStrip;
        Changed?.Invoke();
    }

    /// <summary>
    /// Adopts a user drag order from the native tab strip. Only a permutation of the open tabs is
    /// accepted, so a stale order from before an open or close cannot add, drop or resurrect a tab.
    /// </summary>
    internal bool Reorder(IReadOnlyList<string> order)
    {
        if (order.Count != _tabs.Count || order.SequenceEqual(_tabs) ||
            !order.OrderBy(slot => slot, StringComparer.Ordinal).SequenceEqual(
                _tabs.OrderBy(slot => slot, StringComparer.Ordinal), StringComparer.Ordinal))
            return false;
        _tabs.Clear();
        _tabs.AddRange(order);
        Changed?.Invoke();
        return true;
    }

    internal void Invalidate()
    {
        Clear();
        _entries = [];
        _isCurrent = () => false;
        _subagentsCurrent = () => false;
        Revision++;
        Changed?.Invoke();
    }

    private void Show(string slot)
    {
        if (!_tabs.Contains(slot)) _tabs.Add(slot);
        Selected = slot;
        Passive = false;
        Hidden = false;
        LauncherOpen = false;
        if (slot == DashboardSlot) _dashboardUnseen = false;
        OpenVersion++;
        Changed?.Invoke();
    }

    private bool Remove(string slot)
    {
        var index = _tabs.IndexOf(slot);
        if (index < 0) return false;
        _tabs.RemoveAt(index);
        if (slot == DetailsSlot) Details = null;
        if (slot == FileSlot) File = null;
        if (slot == DashboardSlot)
        {
            Dashboard = null;
            _dashboardUnseen = false;
        }
        if (Selected == slot)
        {
            Selected = _tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)];
            // Falling back onto a dashboard the user never opened keeps it passive.
            Passive = Selected == DashboardSlot && _dashboardUnseen;
        }
        if (_tabs.Count == 0) Expanded = false;
        return true;
    }

    private void Clear()
    {
        _tabs.Clear();
        Selected = null;
        Details = null;
        File = null;
        Dashboard = null;
        Passive = false;
        _dashboardUnseen = false;
        LauncherOpen = false;
        Expanded = false;
    }

    internal static double MaximumWidthForViewport(double viewportWidth) =>
        Math.Clamp(viewportWidth - MinimumConversationWidth - DividerWidth, MinimumPanelWidth, MaximumPanelWidth);

    internal double EffectiveWidth(double viewportWidth) => Math.Min(Width, MaximumWidthForViewport(viewportWidth));

    internal void Resize(double width, double maximumWidth = MaximumPanelWidth)
    {
        if (!double.IsFinite(width)) throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(maximumWidth) || maximumWidth < MinimumPanelWidth)
            throw new ArgumentOutOfRangeException(nameof(maximumWidth));
        Width = Math.Clamp(width, MinimumPanelWidth, Math.Min(maximumWidth, MaximumPanelWidth));
        Changed?.Invoke();
    }
}
