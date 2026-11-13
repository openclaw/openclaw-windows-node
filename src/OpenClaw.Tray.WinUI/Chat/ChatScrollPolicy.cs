// Sole decision owner for conversation transcript scrolling.
//
// ChatScrollPolicy is a pure state machine with three modes:
//   Landing   - opening, switching or sending reaches the latest message once, then Following.
//   Following - streaming, reflow and viewport resize keep the bottom pinned exactly.
//   Reading   - content changes never move what the reader is looking at.
// Only a user action (scrolling up, keyboard reader navigation, toggling a disclosure)
// leaves Following; any other movement is drift and is pinned back. Reaching the
// bottom, Jump to latest or sending re-enters it.
// A toggled disclosure keeps its header in place by anchoring from the viewport top.
//
// The native adapter in ReactorItemsViewScrollController.cs translates WinUI events
// into these inputs and applies the returned command; it makes no decision itself.
// Layout changes must not patch scrolling: a broken rule is fixed here, with a test.
//
// Usage:
//   var policy = new ChatScrollPolicy();
//   var command = policy.Request(ChatScrollRequest.Open, hasRows: true); // LandOnTail
//   command = policy.LandingCompleted(new(offset, scrollableHeight));   // Following
//   command = policy.ContentChanged(new(offset, scrollableHeight));     // PinToBottom when a gap remains
//   if (policy.BeginPin(settledGeometry)) { /* bring the tail row to the viewport bottom */ }
namespace OpenClawTray.Chat;

internal enum ChatScrollMode
{
    Landing,
    Following,
    Reading,
}

/// <summary>History load, session switch and message send all request an ordinary landing.</summary>
internal enum ChatScrollRequest
{
    Open,
    JumpToLatest,
}

internal enum ChatScrollAction
{
    None,
    /// <summary>Abandon pending native navigation without moving.</summary>
    Cancel,
    /// <summary>Scroll to the tail item and confirm that the view landed at the bottom.</summary>
    LandOnTail,
    /// <summary>Scroll a newly appended tail item to the viewport bottom.</summary>
    ScrollToTail,
    /// <summary>Bring the tail row back to the viewport bottom once layout settles.</summary>
    PinToBottom,
}

/// <summary>
/// Which viewport edge the native ScrollView keeps its anchor item at. Bottom keeps the
/// tail edge (and, when reading, the item near the viewport bottom); Top keeps a toggled
/// disclosure header or keyboard reader destination in place while content below it changes.
/// </summary>
internal enum ChatScrollAnchor
{
    Bottom,
    Top,
}

internal readonly record struct ChatScrollGeometry(double Offset, double ScrollableHeight)
{
    public double Gap => ScrollableHeight - Offset;
    public bool AtBottom => TailNavigationPolicy.IsAtBottom(Offset, ScrollableHeight);
    public bool NearBottom => Gap <= ChatScrollPolicy.FollowThreshold;
}

internal readonly record struct ChatScrollCommand(ChatScrollAction Action, ChatScrollAnchor Anchor);

internal sealed class ChatScrollPolicy
{
    /// <summary>Distance from the bottom that still counts as reaching it.</summary>
    public const double FollowThreshold = 60;
    /// <summary>Gap-to-bottom treated as exactly at the bottom.</summary>
    public const double AtBottomTolerance = 1;
    /// <summary>
    /// Bounds the follow corrections one content revision or viewport size may start without
    /// observing the bottom. A correction can itself remeasure virtual rows, so only a new
    /// Entries revision or resize, never another native extent event, grants a new budget.
    /// </summary>
    public const int MaxPinsWithoutReachingBottom = 8;

    private bool _awaitingRows;
    private bool _headerAnchored;
    private bool _userScrolling;
    private double? _lastOffset;
    private int _pinsWithoutReachingBottom;

    public ChatScrollMode Mode { get; private set; } = ChatScrollMode.Following;
    public ChatScrollAnchor Anchor { get; private set; } = ChatScrollAnchor.Bottom;
    /// <summary>Jump to latest waits for rendered geometry before landing.</summary>
    public bool LandAfterRender { get; private set; }
    /// <summary>A keyboard reader destination is in flight; the adapter tracks its native correlation.</summary>
    public bool ReaderNavigationPending { get; private set; }
    /// <summary>Landing with rows to land on.</summary>
    public bool IsLanding => Mode == ChatScrollMode.Landing && !_awaitingRows;
    /// <summary>
    /// Jump to latest is offered only while reading. It never depends on the raw offset, so an
    /// overshoot, a pending pin or a landing in progress cannot flash it.
    /// </summary>
    public bool ShowsJumpToLatest => Mode == ChatScrollMode.Reading;

    public ChatScrollCommand Request(ChatScrollRequest request, bool hasRows)
    {
        StartLanding();
        _awaitingRows = !hasRows;
        LandAfterRender = hasRows && request == ChatScrollRequest.JumpToLatest;
        return Emit(hasRows ? ChatScrollAction.LandOnTail : ChatScrollAction.Cancel);
    }

    /// <summary>The transcript was (re)loaded into the visual tree; it lands at the tail again.</summary>
    public ChatScrollCommand Loaded(bool hasRows)
    {
        if (!hasRows)
            return Emit(ChatScrollAction.None);

        var landAfterRender = LandAfterRender;
        StartLanding();
        _lastOffset = null;
        LandAfterRender = landAfterRender;
        return Emit(ChatScrollAction.LandOnTail);
    }

    public ChatScrollCommand RowsChanged(bool hasRows, bool tailChanged)
    {
        if (!hasRows)
        {
            if (Mode == ChatScrollMode.Landing)
            {
                if (_awaitingRows)
                    return Emit(ChatScrollAction.None);
                _awaitingRows = true;
                LandAfterRender = false;
            }
            return Emit(ChatScrollAction.Cancel);
        }

        if (Mode == ChatScrollMode.Landing && _awaitingRows)
        {
            _awaitingRows = false;
            return Emit(ChatScrollAction.LandOnTail);
        }

        return Emit(tailChanged && Mode == ChatScrollMode.Following
            ? ChatScrollAction.ScrollToTail
            : ChatScrollAction.None);
    }

    /// <summary>Landing reached the bottom or exhausted its bounded retries; either way, follow.</summary>
    public ChatScrollCommand LandingCompleted(ChatScrollGeometry? geometry)
    {
        if (Mode != ChatScrollMode.Landing)
            return Emit(ChatScrollAction.None);

        _awaitingRows = false;
        LandAfterRender = false;
        _userScrolling = false;
        // A landing that was already at the bottom raises no ViewChanged; seed the baseline.
        if (geometry is { } landed)
            _lastOffset = landed.Offset;
        return Follow(geometry);
    }

    /// <summary>The transcript's Entries revision changed (a publication, including same-key streaming).</summary>
    public ChatScrollCommand ContentRevisionChanged()
    {
        _pinsWithoutReachingBottom = 0;
        return Emit(ChatScrollAction.None);
    }

    /// <summary>
    /// Extent changed: streaming growth, reflow or realized rows. While following, any gap,
    /// including growth below the anchored tail or overshoot past a re-estimated extent, pins.
    /// Reading (including a toggled disclosure or keyboard reader) and landing never pin here.
    /// </summary>
    public ChatScrollCommand ContentChanged(ChatScrollGeometry geometry)
    {
        Observe(geometry);
        if (Mode == ChatScrollMode.Following && !geometry.AtBottom)
            return Emit(ChatScrollAction.PinToBottom);
        if (Mode == ChatScrollMode.Reading && geometry.AtBottom && !ReaderNavigationPending)
            return Follow(geometry);
        return Emit(ChatScrollAction.None);
    }

    /// <summary>The viewport changed size; its new geometry is only known after layout.</summary>
    public ChatScrollCommand ViewportResized()
    {
        _pinsWithoutReachingBottom = 0;
        return Emit(Mode == ChatScrollMode.Following ? ChatScrollAction.PinToBottom : ChatScrollAction.None);
    }

    /// <summary>The scroll position changed, from any source.</summary>
    public ChatScrollCommand ViewChanged(ChatScrollGeometry geometry)
    {
        var previous = _lastOffset;
        _lastOffset = geometry.Offset;
        Observe(geometry);
        if (Mode == ChatScrollMode.Landing || ReaderNavigationPending || previous is not { } before)
            return Emit(ChatScrollAction.None);

        // Only a user scroll that leaves the bottom leaves Following. Movement without user
        // input (virtualized extent re-estimates, the view's own corrections, programmatic
        // focus) is drift and is pinned back.
        var movedDown = geometry.Offset > before + AtBottomTolerance;
        if (Mode == ChatScrollMode.Following)
        {
            if (_userScrolling && geometry.Offset < before && geometry.Gap > AtBottomTolerance)
            {
                Mode = ChatScrollMode.Reading;
                return Emit(ChatScrollAction.None);
            }
            return Emit(geometry.AtBottom ? ChatScrollAction.None : ChatScrollAction.PinToBottom);
        }

        if (geometry.AtBottom || (movedDown && geometry.NearBottom && !_headerAnchored))
            return Follow(geometry);
        return Emit(ChatScrollAction.None);
    }

    /// <summary>The ScrollView became idle or a scrollbar drag ended.</summary>
    public ChatScrollCommand Settled(ChatScrollGeometry geometry)
    {
        _userScrolling = false;
        return ContentChanged(geometry);
    }

    /// <summary>Pointer, wheel, keyboard, keyboard focus, touch or scrollbar input in the transcript.</summary>
    public ChatScrollCommand UserInput(ChatScrollGeometry? geometry)
    {
        _userScrolling = true;
        if (geometry is { } current)
            _lastOffset = current.Offset;
        ReaderNavigationPending = false;
        _headerAnchored = false;
        _pinsWithoutReachingBottom = 0;
        Anchor = ChatScrollAnchor.Bottom;
        if (Mode == ChatScrollMode.Landing)
        {
            // Input cancels landing; input that already left the tail does not pull the reader back.
            _awaitingRows = false;
            LandAfterRender = false;
            Mode = geometry is { NearBottom: false } ? ChatScrollMode.Reading : ChatScrollMode.Following;
        }
        else if (Mode == ChatScrollMode.Reading && geometry is { AtBottom: true })
        {
            Mode = ChatScrollMode.Following;
        }
        return Emit(ChatScrollAction.Cancel);
    }

    /// <summary>The reader toggled a disclosure: its header stays in place and its details grow downward.</summary>
    public ChatScrollCommand DisclosureToggled(ChatScrollGeometry? geometry)
    {
        var command = UserInput(geometry);
        Mode = ChatScrollMode.Reading;
        Anchor = ChatScrollAnchor.Top;
        _headerAnchored = true;
        return Emit(command.Action);
    }

    /// <summary>
    /// A keyboard-focused control requested native bring-into-view. A destination outside the
    /// follow range leaves Following before the native offset moves.
    /// </summary>
    public ChatScrollCommand ReaderNavigationStarting(double targetGap)
    {
        if (Mode == ChatScrollMode.Landing || targetGap <= FollowThreshold)
            return Emit(ChatScrollAction.None);

        Mode = ChatScrollMode.Reading;
        Anchor = ChatScrollAnchor.Top;
        ReaderNavigationPending = true;
        return Emit(ChatScrollAction.None);
    }

    public ChatScrollCommand ReaderNavigationCompleted(ChatScrollGeometry geometry)
    {
        if (!ReaderNavigationPending)
            return Emit(ChatScrollAction.None);

        ReaderNavigationPending = false;
        _lastOffset = geometry.Offset;
        return geometry.NearBottom && !_headerAnchored ? Follow(geometry) : Emit(ChatScrollAction.None);
    }

    /// <summary>Revalidates deferred native work immediately before it moves the view.</summary>
    public bool Permits(ChatScrollAction action) => action switch
    {
        ChatScrollAction.LandOnTail => IsLanding,
        ChatScrollAction.ScrollToTail => Mode == ChatScrollMode.Following,
        _ => false,
    };

    /// <summary>
    /// Admits one deferred follow bring against the settled live geometry: never while
    /// reading, never when already at the bottom, and within the pin budget.
    /// </summary>
    public bool BeginPin(ChatScrollGeometry geometry)
    {
        Observe(geometry);
        if (Mode != ChatScrollMode.Following || geometry.AtBottom
            || _pinsWithoutReachingBottom >= MaxPinsWithoutReachingBottom)
            return false;

        _pinsWithoutReachingBottom++;
        // Movement after this correction is not attributable to earlier input.
        _userScrolling = false;
        return true;
    }

    private void StartLanding()
    {
        Mode = ChatScrollMode.Landing;
        Anchor = ChatScrollAnchor.Bottom;
        _headerAnchored = false;
        _userScrolling = false;
        _pinsWithoutReachingBottom = 0;
        ReaderNavigationPending = false;
    }

    private ChatScrollCommand Follow(ChatScrollGeometry? geometry)
    {
        Mode = ChatScrollMode.Following;
        Anchor = ChatScrollAnchor.Bottom;
        _headerAnchored = false;
        return Emit(geometry is { AtBottom: false } ? ChatScrollAction.PinToBottom : ChatScrollAction.None);
    }

    private void Observe(ChatScrollGeometry geometry)
    {
        if (geometry.AtBottom)
            _pinsWithoutReachingBottom = 0;
    }

    private ChatScrollCommand Emit(ChatScrollAction action) => new(action, Anchor);
}
