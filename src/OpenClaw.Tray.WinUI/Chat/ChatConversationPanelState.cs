// Read-only conversation tabs. Bind the current provider/history before Open;
// requests retain identities only, never cached tool payloads or write authority.
using OpenClaw.Chat;

namespace OpenClawTray.Chat;

/// <summary>UI-thread-owned tab selection and layout for one mounted conversation.</summary>
internal sealed class ChatConversationPanelState
{
    internal const double MinimumPanelWidth = 280;
    internal const double MaximumPanelWidth = 800;
    internal const double MinimumConversationWidth = 360;
    internal const double ResizeStep = 16;
    internal const double DividerWidth = 12;
    internal const string DetailsSlot = "details";
    private object? _provider;
    private string? _session;
    private long _connectionGeneration;
    private long _timelineGeneration;
    private long _historyRevision;
    private long _profileRevision;
    private IReadOnlyList<ChatTimelineItem> _entries = [];
    private Func<bool> _isCurrent = () => false;
    private readonly List<string> _tabs = [];

    internal event Action? Changed;
    internal IReadOnlyList<string> Tabs => _tabs;
    internal string? Selected { get; private set; }
    internal ChatToolDetailRequest? Details { get; private set; }
    internal double Width { get; private set; } = 440;
    internal long Revision { get; private set; }
    internal long OpenVersion { get; private set; }
    internal bool IsReady => _session is not null && _isCurrent();

    /// <summary>Session/source replacement discards tabs; streaming updates retain them.</summary>
    internal void Bind(object provider, string? session, long connectionGeneration,
        long timelineGeneration, long historyRevision, IReadOnlyList<ChatTimelineItem> entries,
        Func<bool> isCurrent, long profileRevision = 0)
    {
        if (!ReferenceEquals(_provider, provider) || _session != session ||
            _connectionGeneration != connectionGeneration || _timelineGeneration != timelineGeneration ||
            _historyRevision != historyRevision)
        {
            _tabs.Clear();
            Selected = null;
            Details = null;
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
    }

    internal ChatTimelineItem? Resolve(ChatToolDetailRequest request) =>
        IsReady ? request.Resolve(_session, _timelineGeneration, _historyRevision, _entries) : null;

    /// <summary>The captured revision rejects delayed callbacks after navigation or replay.</summary>
    internal bool Open(ChatToolDetailRequest request, long expectedRevision)
    {
        if (Revision != expectedRevision || Resolve(request) is null) return false;
        if (!_tabs.Contains(DetailsSlot)) _tabs.Add(DetailsSlot);
        Details = request;
        Selected = DetailsSlot;
        OpenVersion++;
        Changed?.Invoke();
        return true;
    }

    internal void Select(string slot)
    {
        if (!_tabs.Contains(slot) || Selected == slot) return;
        Selected = slot;
        Changed?.Invoke();
    }

    internal void Close(string slot)
    {
        var index = _tabs.IndexOf(slot);
        if (index < 0) return;
        _tabs.RemoveAt(index);
        if (slot == DetailsSlot) Details = null;
        if (Selected == slot)
            Selected = _tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)];
        Changed?.Invoke();
    }

    internal void Invalidate()
    {
        _tabs.Clear();
        Selected = null;
        Details = null;
        _entries = [];
        _isCurrent = () => false;
        Revision++;
        Changed?.Invoke();
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
