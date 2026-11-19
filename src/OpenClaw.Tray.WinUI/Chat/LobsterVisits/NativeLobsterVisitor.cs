// The composer lobster: a native visitor that occasionally perches on the new-session composer's top edge, ported from
// the Control UI's LobsterPet. It owns one LobsterVisitOwner per composer opening (seeded from the session and a fresh
// draw), measures the composer's free top lane, draws the owner's snapshots with LobsterArtworkCanvas and plays the
// upstream keyframes with LobsterMotionPlayer. Hover shows its name, a click startles it, press-and-hold pets it, and
// right-click offers Dismiss or Dismiss and don't show again, which turns the device-local setting off.
// Windows behavior: decorative and pointer-only like upstream (Raw accessibility view, never focusable); hidden in High
// Contrast; calm and stationary when Windows animation effects are off; acts and loops pause while the window is
// minimized or hidden and stop when the composer unmounts.
// OpenClaw 4cffd43ee043275f6a4c693aaacee8d8e5a61288: ui/src/components/lobster-pet.tsx, lobster-pet-scene-view.tsx,
// lobster-pet-dismiss-menu.tsx and ui/src/styles/lobster-pet.css (MIT; see THIRD_PARTY_NOTICES.md).
//
// Usage (the composer overlays the writing surface's grid cell):
//   NativeLobsterVisitorElement.NativeLobsterVisitor()
//       .Set(visitor => visitor.Update(new LobsterVisitorInputs(thread.Id, connected, runs, enabled,
//           session.DisableLobsterVisits)));
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor.Wrappers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OpenClawTray.Helpers;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace OpenClawTray.Chat.LobsterVisits;

[GenerateReactorWrapper(typeof(NativeLobsterVisitor), AutoDiscover = false, RegisterAssembly = false)]
internal partial record NativeLobsterVisitorElement;

/// <summary>What the composer tells its visitor on every render.</summary>
/// <param name="ThreadId">The composer's session; a new session is a new opening with a new cast.</param>
/// <param name="Connected">Gateway connection; offline only after <see cref="LobsterOfflineTracker.DelayMs"/>.</param>
/// <param name="Runs">Every session's run state, for busy mode and run reactions.</param>
/// <param name="VisitsEnabled">The device-local Lobster visits setting.</param>
/// <param name="DisableVisits">Persists "Dismiss and don't show again".</param>
internal sealed record LobsterVisitorInputs(
    string ThreadId,
    bool Connected,
    IReadOnlyList<LobsterSessionRun> Runs,
    bool VisitsEnabled,
    Action? DisableVisits);

/// <summary>Runs the owner's timers on the UI dispatcher.</summary>
internal sealed class DispatcherLobsterScheduler(DispatcherQueue queue) : ILobsterVisitScheduler
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public double ElapsedMs => _clock.Elapsed.TotalMilliseconds;

    public DateTime LocalNow => DateTime.Now;

    public IDisposable Schedule(double delayMs, Action callback)
    {
        var timer = queue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMs));
        var handle = new Handle(timer);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!handle.Cancelled)
                callback();
        };
        timer.Start();
        return handle;
    }

    private sealed class Handle(DispatcherQueueTimer timer) : IDisposable
    {
        internal bool Cancelled { get; private set; }

        public void Dispose()
        {
            Cancelled = true;
            timer.Stop();
        }
    }
}

internal sealed class NativeLobsterVisitor : Control
{
    /// <summary>
    /// Data-isolated runs only: a fixed visitor seed for every opening, so UI proof gets a known cast and schedule.
    /// Ignored for normal profiles.
    /// </summary>
    internal const string SeedOverrideVariable = "OPENCLAW_LOBSTER_VISITS_SEED";

    // Upstream clip-path inset(-88px 0 0): the visitor may climb this far above the ledge. Nothing paints below it.
    private const double Headroom = 88;
    private const double ViewBoxWidth = LobsterArtwork.ViewBoxWidth;
    private const double ViewBoxHeight = LobsterArtwork.ViewBoxHeight;
    private static readonly global::Windows.UI.Color HeartColor = ColorHelper.FromArgb(255, 0xff, 0x5c, 0x8a);
    private static readonly global::Windows.UI.Color SparkleColor = ColorHelper.FromArgb(255, 0xff, 0xe2, 0x7a);
    private static readonly global::Windows.UI.Color[] PartyZ =
    [
        ColorHelper.FromArgb(255, 0xff, 0xd1, 0x66), ColorHelper.FromArgb(255, 0x5c, 0xd9, 0xc4), ColorHelper.FromArgb(255, 0xff, 0x5c, 0x8a),
    ];

    private readonly Canvas _stage = new();
    private readonly RectangleGeometry _clip = new();
    private readonly Canvas _motion = new() { RenderTransform = new CompositeTransform() };
    private readonly SpriteHost _sprite = new()
    {
        Background = new SolidColorBrush(Colors.Transparent), Visibility = Visibility.Collapsed,
        RenderTransform = new CompositeTransform(),
    };
    private readonly Grid _body = new() { RenderTransform = new CompositeTransform() };
    private readonly Viewbox _artHost = new() { Stretch = Stretch.Uniform };
    private readonly Canvas _effects = new() { IsHitTestVisible = false };
    private readonly TextBlock _heart = Glyph("\u2665");
    private readonly TextBlock[] _z = [Glyph("z"), Glyph("z"), Glyph("Z")];
    private readonly Ellipse[] _bubbles = [new(), new(), new()];
    private readonly TextBlock[] _sparkles = [Glyph("\u2726"), Glyph("\u2726")];
    private readonly Viewbox _broom = new() { Stretch = Stretch.Fill, Opacity = 0, RenderTransform = new CompositeTransform() };
    private readonly Viewbox _balloon = new() { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed, RenderTransform = new CompositeTransform() };
    private readonly Ellipse _entryBubble = new() { Opacity = 0, StrokeThickness = 1.5, RenderTransform = new CompositeTransform() };
    private readonly MenuFlyout _menu = new() { Placement = FlyoutPlacementMode.TopEdgeAlignedLeft };
    // One tooltip for the visitor's lifetime: replacing it on every render would restart its hover delay.
    private readonly ToolTip _nameTip = new();
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private readonly List<FrameworkElement> _watched = [];

    private LobsterVisitorInputs? _inputs;
    private DispatcherLobsterScheduler? _scheduler;
    private LobsterVisitOwner? _owner;
    private LobsterOfflineTracker? _offline;
    private LobsterVisitInputs? _applied;
    private string _opening = "";
    private FrameworkElement? _surface;
    private UIElement? _pointerRoot;
    private Point _origin;
    private double _composerWidth;
    private bool _loaded, _measureQueued, _animationsEnabled = true, _highContrast, _hostVisible = true;
    private string _accent = "#7f77dd", _accentHover = "#9a93e8";
    private (LobsterLook Look, bool Grumpy, bool Offline, string Accent)? _artKey;
    private LobsterArtworkCanvas? _art;
    private LobsterVisitSnapshot? _shown;
    private LobsterLook? _recordedLook;
    private bool _idleHoldsEyes;
    private LobsterMotionPlayer? _idle, _act, _travel, _entrance, _leave;
    private Control? _focusReturn;
    private ScrollViewer? _overflowHost;
    private bool _overflowHostWasUnclipped;

    public NativeLobsterVisitor()
    {
        IsTabStop = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        Template = (ControlTemplate)Application.Current.Resources["ChatWorkingClawTemplate"];

        _stage.Clip = _clip;
        _body.Children.Add(_artHost);
        _body.Children.Add(_effects);
        _broom.Child = LobsterArtworkCanvas.BuildSprite(LobsterArtwork.Sprite(LobsterSprites.Broom), 24, 40);
        _balloon.Child = LobsterArtworkCanvas.BuildSprite(LobsterArtwork.Sprite(LobsterSprites.EntryBalloon), 40, 62);
        _effects.Children.Add(_balloon);
        _effects.Children.Add(_entryBubble);
        foreach (var sparkle in _sparkles)
        {
            sparkle.Foreground = new SolidColorBrush(SparkleColor);
            _effects.Children.Add(sparkle);
        }
        foreach (var z in _z)
        {
            z.FontFamily = new FontFamily("Consolas");
            z.FontWeight = FontWeights.Bold;
            _effects.Children.Add(z);
        }
        foreach (var bubble in _bubbles)
        {
            bubble.Opacity = 0;
            bubble.StrokeThickness = 1;
            bubble.RenderTransform = new CompositeTransform();
            _effects.Children.Add(bubble);
        }
        _heart.Foreground = new SolidColorBrush(HeartColor);
        _effects.Children.Add(_heart);
        _effects.Children.Add(_broom);
        _sprite.Children.Add(_body);
        // Raw view keeps the decoration out of Narrator's content tree while proof tooling can still locate it.
        AutomationProperties.SetAccessibilityView(_sprite, AccessibilityView.Raw);
        AutomationProperties.SetAutomationId(_sprite, "ChatLobsterVisitorSprite");
        ToolTipService.SetToolTip(_sprite, _nameTip);
        _motion.Children.Add(_sprite);
        _stage.Children.Add(_motion);

        var dismiss = new MenuFlyoutItem { Text = Localized("ChatLobster_Dismiss", "Dismiss") };
        dismiss.Click += (_, _) => _owner?.Dismiss(permanently: false);
        var never = new MenuFlyoutItem { Text = Localized("ChatLobster_DismissForever", "Dismiss and don't show again") };
        never.Click += (_, _) => _owner?.Dismiss(permanently: true);
        _menu.Items.Add(dismiss);
        _menu.Items.Add(never);
        _menu.Closed += OnMenuClosed;

        _sprite.PointerPressed += OnPointerPressed;
        _sprite.PointerReleased += OnPointerReleased;
        _sprite.PointerCanceled += OnPointerLost;
        _sprite.PointerExited += OnPointerLost;
        _sprite.PointerCaptureLost += OnPointerLost;
        _sprite.RightTapped += OnRightTapped;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        ((ContentPresenter)GetTemplateChild("ClawRoot")).Content = _stage;
    }

    /// <summary>Applied on every composer render; cheap when nothing changed.</summary>
    internal void Update(LobsterVisitorInputs inputs)
    {
        var previous = _inputs;
        _inputs = inputs;
        if (_owner is null)
            return;
        if (!string.Equals(previous?.ThreadId, inputs.ThreadId, StringComparison.Ordinal))
            QueueMeasure();
        _offline?.Update(inputs.Connected);
        ApplyInputs();
        QueueMeasure();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded)
            return;
        _loaded = true;
        _surface = (VisualTreeHelper.GetParent(this) as Panel)?.Children.OfType<FrameworkElement>()
            .FirstOrDefault(child => AutomationProperties.GetAutomationId(child) == "ChatWritingSurface");
        _opening = Random.Shared.NextDouble().ToString("R", CultureInfo.InvariantCulture);
        _scheduler = new DispatcherLobsterScheduler(DispatcherQueue);
        _offline = new LobsterOfflineTracker(_scheduler);
        _offline.Changed += ApplyInputs;
        var journal = LobsterVisitJournalFile.For(System.IO.Path.Combine(AppIdentity.ResolveLocalDataDirectory(), LobsterVisitJournalFile.FileName));
        _owner = new LobsterVisitOwner(_scheduler, journal);
        _owner.Changed += Render;
        _owner.VisitsDisabled += () => _inputs?.DisableVisits?.Invoke();

        _animationsEnabled = _uiSettings.AnimationsEnabled;
        _highContrast = _accessibility.HighContrast;
        ReadAccent();
        _uiSettings.AnimationsEnabledChanged += OnSystemSettingChanged;
        // AccessibilitySettings.HighContrastChanged needs a CoreWindow and throws ERROR_NOT_FOUND in WinUI 3 desktop
        // windows; high-contrast and accent changes arrive through ColorValuesChanged, as ChatThemeChanges uses.
        _uiSettings.ColorValuesChanged += OnSystemSettingChanged;
        if (XamlRoot is { } root)
        {
            _hostVisible = root.IsHostVisible;
            root.Changed += OnXamlRootChanged;
            _pointerRoot = root.Content;
            _pointerRoot?.AddHandler(PointerMovedEvent, new PointerEventHandler(OnRootPointerMoved), handledEventsToo: true);
        }
        _owner.SetReducedMotion(!_animationsEnabled);
        _owner.SetHostVisible(_hostVisible && !_highContrast);
        Watch(this, _surface);
        AllowOverflow();
        if (_inputs is { } inputs)
        {
            _offline.Update(inputs.Connected);
            MeasureScene();
            _applied = Inputs(inputs);
            _owner.Connect(_applied);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (!_loaded)
            return;
        _loaded = false;
        _menu.Hide();
        if (_overflowHost is { } overflowHost)
            overflowHost.CanContentRenderOutsideBounds = _overflowHostWasUnclipped;
        _overflowHost = null;
        foreach (var element in _watched)
            element.SizeChanged -= OnWatchedSizeChanged;
        _watched.Clear();
        _uiSettings.AnimationsEnabledChanged -= OnSystemSettingChanged;
        _uiSettings.ColorValuesChanged -= OnSystemSettingChanged;
        if (XamlRoot is { } root)
            root.Changed -= OnXamlRootChanged;
        _pointerRoot?.RemoveHandler(PointerMovedEvent, new PointerEventHandler(OnRootPointerMoved));
        _pointerRoot = null;
        if (_owner is { } owner)
        {
            owner.Changed -= Render;
            owner.Dispose();
        }
        _owner = null;
        _offline?.Dispose();
        _offline = null;
        _scheduler = null;
        _applied = null;
        StopAll();
        _sprite.Visibility = Visibility.Collapsed;
        _shown = null;
        _recordedLook = null;
    }

    private LobsterVisitInputs Inputs(LobsterVisitorInputs inputs)
    {
        var seed = AppIdentity.IsIsolated
            && uint.TryParse(Environment.GetEnvironmentVariable(SeedOverrideVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var pinned)
                ? pinned
                : LobsterComposerVisits.Seed(_opening, inputs.ThreadId);
        // The Hub draws the top lane only, so floor moves have no lane; passing floorEnabled false is equivalent.
        return new LobsterVisitInputs(
            seed,
            LobsterVisitPlans.ResolveMode(_offline?.OfflineStable != true, inputs.Runs),
            inputs.VisitsEnabled,
            ResidentEnabled: true,
            FloorEnabled: false,
            LobsterVisitPlans.ResolveOutcome(inputs.Runs));
    }

    private void ApplyInputs()
    {
        if (_owner is not { } owner || _inputs is not { } inputs)
            return;
        var next = Inputs(inputs);
        if (next == _applied)
            return;
        if (_applied is null)
        {
            _applied = next;
            owner.Connect(next);
            return;
        }
        _applied = next;
        owner.Update(next);
    }

    // Measurement: the composer box is the writing surface, and the placement pickers above it are the only top
    // obstacle (upstream's new-session surroundings). The Hub draws no floor, so footer and controls are not measured.

    // Upstream's pet overflows its composer into the thread. The composer area scrolls in a ScrollViewer, which clips
    // the headroom when the composer docks at the bottom (an empty session while offline), so a mounted visitor lets
    // that viewer render outside its bounds and restores it on unmount.
    private void AllowOverflow()
    {
        for (var node = VisualTreeHelper.GetParent(this); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not ScrollViewer scroll)
                continue;
            _overflowHost = scroll;
            _overflowHostWasUnclipped = scroll.CanContentRenderOutsideBounds;
            scroll.CanContentRenderOutsideBounds = true;
            return;
        }
    }

    private void QueueMeasure()
    {
        if (_measureQueued || !_loaded)
            return;
        _measureQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _measureQueued = false;
            MeasureScene();
        });
    }

    private void MeasureScene()
    {
        if (!_loaded || _owner is not { } owner || _surface is not { ActualWidth: > 0, ActualHeight: > 0 } surface)
            return;
        var composer = Bounds(surface);
        var pickers = (VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(this)) as Panel)?.Children.OfType<FrameworkElement>()
            .FirstOrDefault(child => AutomationProperties.GetAutomationId(child) == "ChatPlacementPickers");
        Watch(pickers);
        var obstacles = pickers is { ActualWidth: > 0, ActualHeight: > 0 } ? new[] { Box(Bounds(pickers)) } : [];
        _origin = new Point(composer.Left, composer.Top);
        _composerWidth = composer.Width;
        _clip.Rect = new Rect(composer.Left, composer.Top - Headroom, composer.Width, Headroom);
        owner.UpdateScene(LobsterSceneResolver.Resolve(Box(composer), null, [], obstacles, null, composer.Left));
        Render();

        Rect Bounds(FrameworkElement element) =>
            element.TransformToVisual(_stage).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        static LobsterBox Box(Rect rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private void Watch(params FrameworkElement?[] elements)
    {
        foreach (var element in elements)
        {
            if (element is null || _watched.Contains(element))
                continue;
            element.SizeChanged += OnWatchedSizeChanged;
            _watched.Add(element);
        }
    }

    private void OnWatchedSizeChanged(object sender, SizeChangedEventArgs args) => QueueMeasure();

    // System settings and window state.

    private void OnSystemSettingChanged(UISettings sender, object args) => DispatcherQueue?.TryEnqueue(() =>
    {
        if (!_loaded || _owner is not { } owner)
            return;
        _animationsEnabled = _uiSettings.AnimationsEnabled;
        _highContrast = _accessibility.HighContrast;
        ReadAccent();
        owner.SetReducedMotion(!_animationsEnabled);
        owner.SetHostVisible(_hostVisible && !_highContrast);
        Render();
    });

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (sender.IsHostVisible == _hostVisible || _owner is not { } owner)
            return;
        _hostVisible = sender.IsHostVisible;
        owner.SetHostVisible(_hostVisible && !_highContrast);
        Render();
    }

    private void ReadAccent()
    {
        static string Hex(global::Windows.UI.Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
        if (Application.Current.Resources.TryGetValue("SystemAccentColor", out var accent) && accent is global::Windows.UI.Color color)
            _accent = Hex(color);
        if (Application.Current.Resources.TryGetValue("SystemAccentColorLight1", out var hover) && hover is global::Windows.UI.Color light)
            _accentHover = Hex(light);
    }

    // Rendering.

    private void Render()
    {
        var snapshot = _owner?.Snapshot();
        if (!_loaded || _highContrast || snapshot is not { ShowsResident: true } s || s.Scene.Top is not { } lane)
        {
            if (_sprite.Visibility == Visibility.Visible)
            {
                StopAll();
                _sprite.Visibility = Visibility.Collapsed;
                _menu.Hide();
            }
            _shown = null;
            _recordedLook = null;
            return;
        }

        EnsureArt(s);
        var scale = s.Look.Scale;
        var width = 16 * scale;
        var height = 14 * scale;
        LayoutSprite(s, lane, width, height);
        if (!s.MenuOpen && _menu.IsOpen)
            _menu.Hide();

        var previous = _shown;
        _shown = s;
        _sprite.Visibility = Visibility.Visible;
        if (_hostVisible && s.Presence == LobsterPresence.In && _recordedLook != s.Look && _applied is { } inputs)
        {
            _recordedLook = s.Look;
            _ = RecordSightingAsync(s.Look, inputs.Seed, DateTimeOffset.Now);
        }

        var holdsEyes = LobsterMotion.HoldsEyesClosed(s.Act);
        ApplyBase(s, width, height, holdsEyes);
        if (!_animationsEnabled || !_hostVisible)
        {
            StopAll();
            _balloon.Visibility = Visibility.Collapsed;
            // Reduced motion upstream drops the away transition: a leaving visitor simply disappears.
            _sprite.Opacity = s.Presence == LobsterPresence.Leaving ? 0 : 1;
            return;
        }
        _sprite.Opacity = 1;

        if (_idle is null || previous is null || previous.Facing != s.Facing || _idleHoldsEyes != holdsEyes)
            StartIdle(s, holdsEyes);

        if (s.Presence == LobsterPresence.Leaving)
        {
            if (previous?.Presence != LobsterPresence.Leaving)
            {
                Stop(ref _act);
                Stop(ref _travel);
                Stop(ref _entrance);
                _leave = Play(LobsterMotion.Leave(), width, height);
            }
            return;
        }
        Stop(ref _leave);

        if (s.Entering && (previous is not { Entering: true } || previous.Presence != LobsterPresence.In))
        {
            Stop(ref _entrance);
            _balloon.Visibility = s.Entrance == LobsterEntrance.Balloon ? Visibility.Visible : Visibility.Collapsed;
            _entrance = Play(LobsterMotion.ForEntrance(s.Entrance), width, height);
        }
        else if (!s.Entering)
        {
            Stop(ref _entrance);
            _balloon.Visibility = Visibility.Collapsed;
        }

        if (s.Travel != previous?.Travel)
        {
            Stop(ref _travel);
            if (s.Travel is { } travel)
            {
                var fromX = travel.From.X - travel.To.X;
                var fromY = travel.From.Y - travel.To.Y;
                _travel = Play(travel.Hop
                    ? LobsterMotion.TravelHop(fromX, fromY, travel.DurationMs)
                    : [LobsterMotion.TravelWalk(fromX, fromY, travel.DurationMs)], width, height);
            }
        }

        if (s.Act != previous?.Act || previous is null)
        {
            Stop(ref _act);
            if (s.Act is { } act)
            {
                var animations = LobsterMotion.ForAct(act);
                // Upstream's hop landing rule out-ranks act animations on the body during a floor hop.
                if (s.Travel is { Hop: true })
                    animations = animations.Where(animation => animation.Target != LobsterMotionTarget.Body).ToArray();
                _act = Play(animations, width, height);
            }
        }
    }

    private static async Task RecordSightingAsync(LobsterLook look, uint seed, DateTimeOffset seenAt)
    {
        try
        {
            await LobsterdexFile.ForProfile(AppIdentity.ResolveLocalDataDirectory())
                .RecordAsync(look.Palette.Id, LobsterLooks.Name(look, seed), look.Shiny, seenAt)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A collection failure must not block or change visits. Log only the failure category.
            Services.Logger.Warn($"Could not record a Lobsterdex sighting ({ex.GetType().Name}).");
        }
    }

    private void EnsureArt(LobsterVisitSnapshot s)
    {
        var key = (s.Look, s.Grumpy, s.Mode == LobsterVisitMode.Offline, _accent);
        if (_artKey == key && _art is not null)
            return;
        StopAll();
        _artKey = key;
        _shown = null;
        var artwork = LobsterArtwork.Compose(s.Look, s.Grumpy, key.Item3, DateTimeOffset.Now, _accent, _accentHover);
        _art = LobsterArtworkCanvas.Build(artwork);
        _artHost.Child = _art.Root;

        var claw = Brush(artwork, "var(--lob-claw)") ?? new SolidColorBrush(ColorHelper.FromArgb(255, 0xff, 0x77, 0x5f));
        var party = s.Look.Accessory == LobsterAccessory.Party;
        for (var i = 0; i < _z.Length; i++)
            _z[i].Foreground = party ? new SolidColorBrush(PartyZ[i]) : claw;
        foreach (var bubble in _bubbles)
            bubble.Stroke = claw;
        _entryBubble.Stroke = Brush(artwork, "var(--lob-glint)") ?? new SolidColorBrush(ColorHelper.FromArgb(255, 0, 0xe5, 0xcc));
        foreach (var sparkle in _sparkles)
            sparkle.Visibility = s.Look.Shiny ? Visibility.Visible : Visibility.Collapsed;
    }

    private static SolidColorBrush? Brush(LobsterArtwork artwork, string value) =>
        LobsterPaint.Resolve(value, artwork.Variables) is { } c ? new SolidColorBrush(ColorHelper.FromArgb(c.A, c.R, c.G, c.B)) : null;

    private void LayoutSprite(LobsterVisitSnapshot s, LobsterLane lane, double width, double height)
    {
        var x = lane.Start + (lane.End - lane.Start) * s.SpotPct / 100;
        _sprite.Width = _body.Width = _artHost.Width = _effects.Width = width;
        _sprite.Height = _body.Height = _artHost.Height = _effects.Height = height;
        Canvas.SetLeft(_sprite, _origin.X + x - width / 2);
        Canvas.SetTop(_sprite, _origin.Y + lane.Y - height);
        if (!string.Equals(_nameTip.Content as string, s.Title, StringComparison.Ordinal))
            _nameTip.Content = s.Title;

        // Effect spans sit at upstream's left/bottom percentages of the body box.
        var scale = s.Look.Scale;
        Place(_heart, .46, .78, 5 * scale);
        foreach (var z in _z)
            Place(z, .58, .72, 4.5 * scale);
        Place(_sparkles[0], .12, .64, 4 * scale);
        Place(_sparkles[1], .76, .82, 4 * scale);
        foreach (var bubble in _bubbles)
        {
            bubble.Width = bubble.Height = 2 * scale;
            Canvas.SetLeft(bubble, .30 * width);
            Canvas.SetTop(bubble, height - .55 * height - bubble.Height);
        }
        _broom.Width = 4.4 * scale;
        _broom.Height = 7.4 * scale;
        Canvas.SetLeft(_broom, .74 * width);
        Canvas.SetTop(_broom, height - .04 * height - _broom.Height);
        _balloon.Width = 6.5 * scale;
        _balloon.Height = 10 * scale;
        Canvas.SetLeft(_balloon, .24 * width);
        Canvas.SetTop(_balloon, height - .92 * height - _balloon.Height);
        // inset: -12% -8%
        _entryBubble.Width = width * 1.16;
        _entryBubble.Height = height * 1.24;
        Canvas.SetLeft(_entryBubble, -.08 * width);
        Canvas.SetTop(_entryBubble, -.12 * height);

        void Place(TextBlock glyph, double left, double bottom, double fontSize)
        {
            glyph.FontSize = fontSize;
            glyph.LineHeight = fontSize;
            Canvas.SetLeft(glyph, left * width);
            Canvas.SetTop(glyph, height - bottom * height - fontSize);
        }
    }

    // Values that hold when no animation drives a property: facing, the vigil pose and the eyes during nap and pet.
    private void ApplyBase(LobsterVisitSnapshot s, double width, double height, bool holdsEyes)
    {
        if (_art is null)
            return;
        if (_art.Svg.RenderTransform is CompositeTransform svg)
        {
            svg.ScaleX = s.Facing;
            svg.ScaleY = 1;
        }
        var body = (CompositeTransform)_body.RenderTransform;
        if (_act is null && _entrance is null)
        {
            body.CenterX = width / 2;
            body.CenterY = height / 2;
        }
        var vigil = s.Vigil && s.Act is null;
        body.TranslateY = vigil ? .06 * height : 0;
        body.ScaleY = vigil ? .94 : 1;
        if (_art.Element(LobsterArtRole.EyesOpen) is { } open)
            open.Opacity = holdsEyes ? 0 : 1;
        if (_art.Element(LobsterArtRole.EyesClosed) is { } closed)
            closed.Opacity = holdsEyes ? 1 : 0;
    }

    private void StartIdle(LobsterVisitSnapshot s, bool holdsEyes)
    {
        Stop(ref _idle);
        _idleHoldsEyes = holdsEyes;
        var animations = new List<LobsterAnimation> { LobsterMotion.Breathe(LobsterLooks.BreatheDelaySeconds(s.Look), s.Facing) };
        if (!holdsEyes)
            animations.AddRange(LobsterMotion.Blink(s.Look.BlinkDelaySeconds));
        if (s.Look.Shiny)
            animations.Add(LobsterMotion.Sparkle());
        _idle = Play(animations, 16 * s.Look.Scale, 14 * s.Look.Scale);
    }

    private LobsterMotionPlayer? Play(IEnumerable<LobsterAnimation> animations, double width, double height)
    {
        var player = new LobsterMotionPlayer();
        var any = false;
        foreach (var animation in animations)
        {
            var count = animation.Target switch
            {
                LobsterMotionTarget.Z => _z.Length,
                LobsterMotionTarget.Bubble => _bubbles.Length,
                LobsterMotionTarget.Sparkle => _sparkles.Length,
                _ => 1,
            };
            for (var index = 0; index < count; index++)
            {
                if (Target(animation.Target, index) is not { } target)
                    continue;
                var (w, h) = animation.Target switch
                {
                    LobsterMotionTarget.Svg or LobsterMotionTarget.ClawLeft or LobsterMotionTarget.ClawRight
                        or LobsterMotionTarget.Antennae or LobsterMotionTarget.EyesOpen or LobsterMotionTarget.EyesClosed
                        => (ViewBoxWidth, ViewBoxHeight),
                    LobsterMotionTarget.Motion or LobsterMotionTarget.Sprite or LobsterMotionTarget.Body => (width, height),
                    _ => (target.Width is > 0 ? target.Width : target.ActualWidth, target.Height is > 0 ? target.Height : target.ActualHeight),
                };
                player.Add(animation, target, w, h, index);
                any = true;
            }
        }
        if (!any)
            return null;
        player.Start();
        return player;
    }

    private FrameworkElement? Target(LobsterMotionTarget target, int index) => target switch
    {
        LobsterMotionTarget.Motion => _motion,
        LobsterMotionTarget.Sprite => _sprite,
        LobsterMotionTarget.Body => _body,
        LobsterMotionTarget.Svg => _art?.Svg,
        LobsterMotionTarget.ClawLeft => _art?.Element(LobsterArtRole.ClawLeft),
        LobsterMotionTarget.ClawRight => _art?.Element(LobsterArtRole.ClawRight),
        LobsterMotionTarget.Antennae => _art?.Element(LobsterArtRole.Antennae),
        LobsterMotionTarget.EyesOpen => _art?.Element(LobsterArtRole.EyesOpen),
        LobsterMotionTarget.EyesClosed => _art?.Element(LobsterArtRole.EyesClosed),
        LobsterMotionTarget.Balloon => _balloon,
        LobsterMotionTarget.EntryBubble => _entryBubble,
        LobsterMotionTarget.Heart => _heart,
        LobsterMotionTarget.Broom => _broom,
        LobsterMotionTarget.Z => _z[index],
        LobsterMotionTarget.Bubble => _bubbles[index],
        LobsterMotionTarget.Sparkle => _sparkles[index],
        _ => null,
    };

    private static void Stop(ref LobsterMotionPlayer? player)
    {
        player?.Stop();
        player = null;
    }

    private void StopAll()
    {
        Stop(ref _idle);
        Stop(ref _act);
        Stop(ref _travel);
        Stop(ref _entrance);
        Stop(ref _leave);
    }

    // Input. Upstream's sprite is pointer-only and aria-hidden: it never joins the tab order. Keyboard and assistive
    // technology users control visits with the Settings toggle.

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(_sprite).Properties;
        // A secondary press must not move focus before the menu records its return target.
        args.Handled = true;
        if (point.IsLeftButtonPressed)
            _owner?.HandleHoldStart();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (args.GetCurrentPoint(_sprite).Properties.PointerUpdateKind != Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased)
            return;
        args.Handled = true;
        _owner?.HandleHoldEnd();
    }

    private void OnPointerLost(object sender, PointerRoutedEventArgs args) => _owner?.HandleHoldCancel();

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        args.Handled = true;
        if (_owner?.OpenMenu() != true)
            return;
        // Upstream's menu returns focus to whatever held it when the menu opened, normally the editor.
        _focusReturn = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as Control : null;
        _menu.ShowAt(_sprite, new FlyoutShowOptions { Position = args.GetPosition(_sprite), ShowMode = FlyoutShowMode.Standard });
    }

    private void OnMenuClosed(object? sender, object args)
    {
        if (_owner?.Snapshot() is { MenuOpen: true })
            _owner.CloseMenu();
        if (_focusReturn is { } target && target.XamlRoot is not null)
            target.Focus(FocusState.Programmatic);
        _focusReturn = null;
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_owner is not { } owner || _shown is not { Presence: LobsterPresence.In, Act: null } || _sprite.Visibility != Visibility.Visible)
            return;
        var pointerX = args.GetCurrentPoint(_stage).Position.X;
        var center = _sprite.TransformToVisual(_stage).TransformPoint(new Point(_sprite.Width / 2, 0)).X;
        owner.HandlePointerMoved(pointerX, center);
    }

    private static TextBlock Glyph(string text)
    {
        var glyph = new TextBlock
        {
            Text = text,
            Opacity = 0,
            IsHitTestVisible = false,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            RenderTransform = new CompositeTransform(),
        };
        AutomationProperties.SetAccessibilityView(glyph, AccessibilityView.Raw);
        return glyph;
    }

    private static string Localized(string key, string fallback)
    {
        var value = LocalizationHelper.GetString(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal) ? fallback : value;
    }

    // The sprite's peer lives only in the Raw view, like upstream's aria-hidden sprite: Narrator's content tree skips
    // it, while UI automation proof can still find its bounds.
    private sealed class SpriteHost : Grid
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
    }
}
