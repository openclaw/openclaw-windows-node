using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace OpenClawTray.Pages;

/// <summary>
/// Channels page — single-column Expander layout. See <c>ChannelsPage.xaml</c>
/// for the architectural comment. Data flows from the gateway's
/// <c>channels.status</c> response → <see cref="ChannelsStatusSnapshot"/> →
/// <see cref="ChannelRecord"/>s → Expander cards built imperatively below.
///
/// Follows the single-app-model pattern (see <c>docs/DATA_FLOW_ARCHITECTURE.md</c>):
/// the page observes <see cref="AppState"/> directly via <see cref="INotifyPropertyChanged"/>
/// and routes commands through <see cref="App"/> globally; no <c>HubWindow</c> coupling.
/// </summary>
public sealed partial class ChannelsPage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current;
    private AppState? _appState;

    /// <summary>
    /// When we last received a snapshot from the gateway. The snapshot itself
    /// lives on <see cref="AppState.ChannelsSnapshot"/> so other surfaces can
    /// observe it — only the receive-timestamp is page-local because
    /// <see cref="ChannelsAggregator"/> needs it to stamp records.
    /// </summary>
    private DateTime _latestSnapshotAt;
    private CancellationTokenSource? _refreshCts;

    /// <summary>
    /// Atomic config-cache snapshot (root + baseHash bundled). Replaced
    /// wholesale by <see cref="CaptureConfigSnapshot"/> so callers can read
    /// a single field and get a consistent pair — see Hanselman review
    /// HIGH-2: reading root and baseHash as two separate fields opens a
    /// TOCTOU window where a fresh config.get can advance baseHash while
    /// the patch is still being built from the older root, letting the
    /// gateway accept a stale-raw + fresh-hash combo that silently clobbers
    /// interim changes.
    /// </summary>
    private sealed record ConfigSnapshot(JsonElement Root, string? BaseHash)
    {
        public static ConfigSnapshot Empty { get; } = new(default, null);
        public bool HasRoot => Root.ValueKind != JsonValueKind.Undefined && Root.ValueKind != JsonValueKind.Null;
    }

    private ConfigSnapshot _configSnapshot = ConfigSnapshot.Empty;

    /// <summary>
    /// Single-slot TaskCompletionSource for an in-flight config.get fetch.
    /// Reused by every concurrent <see cref="EnsureConfigLoadedAsync"/> call
    /// so two saves clicked back-to-back share one fetch instead of racing
    /// each other's TCS overwrites (Hanselman review HIGH-1).
    /// </summary>
    private TaskCompletionSource<bool>? _configLoadTcs;

    /// <summary>Tracks the channel id for each Expander so per-channel pushes can target the right card.</summary>
    private readonly Dictionary<string, Expander> _expanderById = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Preserves per-channel <c>IsExpanded</c> across refreshes (don't collapse on every push).</summary>
    private readonly HashSet<string> _expandedIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-channel form input cache. Survives the tear-down-and-rebuild that
    /// happens on every snapshot refresh, so a user who types a token + clicks
    /// Save still sees their values after Render() rebuilds the expander tree.
    /// Cleared for a given channel only after a successful save completes.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, string>> _formValuesByChannel =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Most recent save outcome surfaced at the page level (above the channel
    /// list) so it survives the per-channel Expander rebuild that fires after
    /// each Save → Refresh cycle.
    /// </summary>
    private (string ChannelId, string Title, string Message, InfoBarSeverity Severity)? _pendingSaveBanner;

    /// <summary>
    /// True when the pending banner is from a control action (Start/Stop/Logout)
    /// rather than a config Save. Render() must NOT rewrite the banner title to
    /// "is running" or clear cached form values for action banners — that's
    /// only correct for Save flows where the form was just submitted.
    /// </summary>
    private bool _pendingBannerIsAction;

    /// <summary>
    /// Gates concurrent refreshes so a burst of channel-health pushes plus a user
    /// click don't trigger overlapping <c>channels.status</c> requests. The CTS
    /// only suppresses stale UI updates — the semaphore prevents duplicate calls.
    /// </summary>
    private readonly System.Threading.SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>
    /// Cancellation token for in-flight linking flows (QR scan polls). Distinct
    /// from <see cref="_refreshCts"/> so the user can Refresh without aborting
    /// an active linking session.
    /// </summary>
    private CancellationTokenSource? _linkingCts;

    public ChannelsPage()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
        // User-dismiss of the save banner clears the pending state so it
        // doesn't reappear on the next snapshot refresh. Also collapse
        // Visibility so the dismissed bar doesn't keep claiming layout
        // space in the StackPanel (see SetInfoBarOpen comment).
        SaveBanner.Closed += (_, _) =>
        {
            _pendingSaveBanner = null;
            _pendingBannerIsAction = false;
            SaveBanner.Visibility = Visibility.Collapsed;
        };
        ErrorBar.Closed += (_, _) => ErrorBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Toggle both <see cref="InfoBar.IsOpen"/> and <see cref="UIElement.Visibility"/>
    /// in lock-step. WinUI's <c>InfoBar.IsOpen=false</c> only collapses the
    /// bar's internal content — the control element itself stays Visible,
    /// so the parent <c>StackPanel</c> keeps applying its 16-px Spacing
    /// around the (empty) bar. Stacking four conditional bars that way
    /// leaves a ~60–80 px gap below the header when nothing is open.
    /// Setting Visibility too reclaims that space.
    /// </summary>
    private static void SetInfoBarOpen(InfoBar bar, bool open)
    {
        bar.IsOpen = open;
        bar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Cancel + dispose all per-page tokens. Re-enabling the Refresh button
        // here covers the back-to-back-cancel race where neither call reaches
        // its finally block in the !cts.IsCancellationRequested branch.
        try { _refreshCts?.Cancel(); _refreshCts?.Dispose(); } catch { }
        _refreshCts = null;
        try { _linkingCts?.Cancel(); _linkingCts?.Dispose(); } catch { }
        _linkingCts = null;
        SetRefreshBusy(false);

        if (_appState != null)
        {
            _appState.PropertyChanged -= OnAppStateChanged;
            _appState = null;
        }
    }

    /// <summary>
    /// Hook from <c>HubWindow.InitializeCurrentPage</c> — subscribes to
    /// <see cref="AppState"/> and triggers an initial <c>channels.status</c> fetch.
    /// </summary>
    public void Initialize()
    {
        _appState = CurrentApp.AppState;
        if (_appState != null)
            _appState.PropertyChanged += OnAppStateChanged;

        SetInfoBarOpen(NotConnectedBar, CurrentApp.GatewayClient == null);

        // Render whatever AppState already holds (lets the user re-enter the
        // page without a gateway round-trip) and then kick off a fresh fetch
        // so the snapshot stays current.
        var cached = _appState?.ChannelsSnapshot;
        if (cached != null)
            Render(cached);

        // Adopt any config already in AppState so SaveAsync doesn't have to
        // wait on a fresh fetch if the user goes straight to saving.
        if (_appState?.Config is { } existingConfig)
            CaptureConfigSnapshot(existingConfig);

        _ = RefreshAsync();

        // Warm the config cache. Required by SaveAsync — the gateway only
        // accepts full-config patches, so we need the current config + baseHash
        // before we can write channel credentials.
        if (CurrentApp.GatewayClient != null)
            _ = CurrentApp.GatewayClient.RequestConfigAsync();
    }

    /// <summary>
    /// React to <see cref="AppState"/> updates. Three properties matter:
    /// <list type="bullet">
    /// <item><see cref="AppState.ChannelsSnapshot"/> — the rich snapshot
    /// (Updated by us after a <c>channels.status</c> fetch; other surfaces
    /// may write to it too). Re-render directly.</item>
    /// <item><see cref="AppState.Channels"/> — slim per-event health array
    /// pushed by the gateway. Signals something changed; refresh the rich
    /// snapshot to keep metadata current.</item>
    /// <item><see cref="AppState.Config"/> — full gateway config snapshot
    /// (required by SaveAsync to build atomic config.patch payloads).
    /// Unwrap into <see cref="_configSnapshot"/> (an atomic record bundling
    /// the unwrapped root + baseHash so they can't desync)
    /// and release any awaiters.</item>
    /// </list>
    /// </summary>
    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.ChannelsSnapshot):
                if (_appState?.ChannelsSnapshot is { } snap)
                    Render(snap);
                break;
            case nameof(AppState.Channels):
                _ = RefreshAsync(probe: false);
                break;
            case nameof(AppState.Config):
                if (_appState?.Config is { } cfg)
                    CaptureConfigSnapshot(cfg);
                break;
        }
    }

    /// <summary>
    /// Unwrap the <c>config.get</c> envelope (<c>{ path, exists, raw, parsed }</c>)
    /// into the actual config root plus the baseHash used by
    /// <c>config.patch</c> for optimistic-concurrency. baseHash extraction
    /// follows the same chain ConfigPage uses: prefer the gateway's
    /// <c>baseHash</c> field, fall back to <c>hash</c>, fall back to
    /// SHA256(raw). Publishes the (root, baseHash) pair atomically by
    /// replacing the single <see cref="_configSnapshot"/> field — readers
    /// never see a torn write.
    /// </summary>
    private void CaptureConfigSnapshot(JsonElement envelope)
    {
        var snapshot = envelope.Clone();
        var root = snapshot.TryGetProperty("parsed", out var parsed) ? parsed
            : (snapshot.TryGetProperty("config", out var inner) ? inner : snapshot);

        string? baseHash = null;
        if (snapshot.TryGetProperty("baseHash", out var bh) && bh.ValueKind == JsonValueKind.String)
            baseHash = bh.GetString();
        else if (snapshot.TryGetProperty("hash", out var hashEl) && hashEl.ValueKind == JsonValueKind.String)
            baseHash = hashEl.GetString();
        else if (snapshot.TryGetProperty("raw", out var rawEl) && rawEl.ValueKind == JsonValueKind.String)
        {
            var rawContent = rawEl.GetString();
            if (rawContent != null)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(rawContent);
                var hash = System.Security.Cryptography.SHA256.HashData(bytes);
                baseHash = Convert.ToHexStringLower(hash);
            }
        }

        // Atomic publish: a single field swap so readers always see a
        // consistent (root, baseHash) pair.
        _configSnapshot = new ConfigSnapshot(root.Clone(), baseHash);

        // Release ALL waiters on the current in-flight load (if any).
        var tcs = _configLoadTcs;
        if (tcs != null)
        {
            _configLoadTcs = null;
            tcs.TrySetResult(true);
        }
    }

    /// <summary>
    /// Block until a config snapshot is available, with a generous timeout
    /// so users on slow gateways still get a definitive outcome rather than
    /// a silent hang.
    ///
    /// Concurrency model (post-Hanselman HIGH-1 fix):
    /// <list type="bullet">
    /// <item>If the cache is already populated → return true immediately.</item>
    /// <item>If a load is already in flight → await THAT load's TCS
    /// (never overwrite it). Multiple concurrent saves share one fetch.</item>
    /// <item>Otherwise start a new fetch, store the TCS, fire the request.</item>
    /// <item>After the timeout, re-check the cache directly — another path
    /// (the page-warm fetch in Initialize, a parallel save, an unrelated
    /// gateway push) may have populated it while our TCS was racing.</item>
    /// </list>
    /// </summary>
    private async Task<bool> EnsureConfigLoadedAsync(int timeoutMs = 5000)
    {
        if (_configSnapshot.HasRoot) return true;
        var client = CurrentApp.GatewayClient;
        if (client == null) return false;

        // Reuse any in-flight load TCS so concurrent saves share one fetch.
        // Only start a new fetch if no load is currently in progress.
        var tcs = _configLoadTcs;
        if (tcs == null)
        {
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _configLoadTcs = tcs;
            _ = client.RequestConfigAsync();
        }

        await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        // Re-check the cache rather than gating on our specific TCS winning:
        // another path (warm-up fetch, parallel save, gateway push) may have
        // populated the cache while our timer was running. Caring only about
        // whether the cache is now usable is what callers actually need.
        return _configSnapshot.HasRoot;
    }

    private async void OnRefreshAll(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync(bool probe = true)
    {
        var client = CurrentApp.GatewayClient;
        if (client == null)
        {
            SetInfoBarOpen(NotConnectedBar, true);
            return;
        }
        SetInfoBarOpen(NotConnectedBar, false);

        // Replace any superseded CTS, disposing the old one so we don't leak
        // its internal handle.
        var oldCts = _refreshCts;
        _refreshCts = new CancellationTokenSource();
        var cts = _refreshCts;
        if (oldCts != null)
        {
            try { oldCts.Cancel(); oldCts.Dispose(); } catch { }
        }

        // Coalesce concurrent calls (user clicks + push deltas) — only one
        // gateway request in flight at a time. If we can't acquire immediately,
        // skip: the in-flight call will reflect the latest state shortly.
        if (!await _refreshGate.WaitAsync(0))
            return;

        SetRefreshBusy(true);
        try
        {
            var snapshot = await client.GetChannelsStatusAsync(probe);
            if (cts.IsCancellationRequested) return;
            if (snapshot == null)
            {
                ErrorBar.Title = "Couldn't refresh channels";
                ErrorBar.Message = "The gateway didn't return a channels.status response. Try Refresh again.";
                SetInfoBarOpen(ErrorBar, true);
                return;
            }
            SetInfoBarOpen(ErrorBar, false);
            _latestSnapshotAt = DateTime.UtcNow;
            // Publish into AppState — single source of truth. Setting the
            // property fires PropertyChanged which calls Render via
            // OnAppStateChanged; no need to call Render directly here.
            if (_appState != null)
                _appState.ChannelsSnapshot = snapshot;
            else
                Render(snapshot); // tests / scenarios with no AppState
        }
        finally
        {
            // Always re-enable the button — OnUnloaded also calls SetRefreshBusy(false)
            // as a belt-and-braces for the cancel-during-cancel race.
            SetRefreshBusy(false);
            _refreshGate.Release();
        }
    }

    private void SetRefreshBusy(bool busy)
    {
        RefreshButton.IsEnabled = !busy;
    }

    private void Render(ChannelsStatusSnapshot snapshot)
    {
        // Always allow the built-in fallback. Reasoning: showing the user
        // common channels they can attempt to set up is more useful than
        // honest emptiness — when their gateway truly doesn't support a
        // channel, the inline form / Show QR error path will surface the
        // failure with a real diagnostic. Empty-page-when-connected is the
        // worst of both worlds: no path forward AND no error to act on.
        //
        // GuideBar (top InfoBar) doubles as the "your gateway didn't list any
        // channels" hint AND the page intro: shown only when the snapshot is
        // empty, hidden once the gateway reports at least one channel so the
        // page stays clean for users past the initial-setup phase.
        var records = ChannelsAggregator.Aggregate(
            snapshot,
            _latestSnapshotAt == default ? DateTime.UtcNow : _latestSnapshotAt,
            useBuiltInFallback: true);
        var configured = records.Where(r => r.IsConfigured).ToList();
        var available = records.Where(r => !r.IsConfigured).ToList();

        _expanderById.Clear();
        ConfiguredList.Children.Clear();
        AvailableList.Children.Clear();

        foreach (var rec in configured)
            ConfiguredList.Children.Add(BuildExpander(rec));
        foreach (var rec in available)
            AvailableList.Children.Add(BuildExpander(rec));

        ConfiguredSection.Visibility = configured.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AvailableSection.Visibility = available.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Surface the "connected-but-empty-response" case on the merged
        // GuideBar so the user knows the channels below are a generic preview
        // rather than a definitive list of what their gateway supports. Also
        // doubles as the page's "how channels work" intro for that state.
        var connected = CurrentApp.GatewayClient != null;
        var gatewayReportedSomething = snapshot.ChannelOrder.Count > 0
            || snapshot.Channels.Count > 0
            || (snapshot.ChannelMeta?.Count ?? 0) > 0;
        SetInfoBarOpen(GuideBar, connected && !gatewayReportedSomething && records.Count > 0);

        // Re-paint any pending save banner. Lives at page level so it survives
        // the per-channel Expander rebuild that just happened. If the save was
        // successful AND the channel did transition to configured, upgrade the
        // banner to "running" so the user gets confirmation; if it didn't,
        // change to a warning so the user knows the gateway accepted config
        // but didn't actually start the channel.
        //
        // The "upgrade to 'is running'" path is ONLY safe for Save-flow banners.
        // For control-action banners (Stop/Start/Logout) the title is already
        // truthful ("X stopped", "X starting…") and rewriting it would lie about
        // state — especially after Stop, where the override would say "is running"
        // and additionally wipe the user's cached form drafts.
        if (_pendingSaveBanner is { } pending)
        {
            var record = records.FirstOrDefault(r => string.Equals(r.Id, pending.ChannelId, StringComparison.OrdinalIgnoreCase));
            if (!_pendingBannerIsAction && pending.Severity == InfoBarSeverity.Success && record != null && !record.IsConfigured)
            {
                // Gateway accepted config.set but the channel still isn't
                // configured/running. Don't lie about it.
                SaveBanner.Severity = InfoBarSeverity.Warning;
                SaveBanner.Title = $"{pending.ChannelId}: config saved, but not running yet";
                SaveBanner.Message = "The gateway accepted the settings but didn't start the channel. Expand the channel below — the Status section and the diagnostic disclosure will show why.";
            }
            else if (!_pendingBannerIsAction && pending.Severity == InfoBarSeverity.Success && record != null && record.IsConfigured)
            {
                SaveBanner.Severity = InfoBarSeverity.Success;
                SaveBanner.Title = $"{pending.ChannelId} is running";
                SaveBanner.Message = "Configuration saved and the channel is up.";
                // Clear the cached form values now that we're confirmed running.
                _formValuesByChannel.Remove(pending.ChannelId);
            }
            else
            {
                SaveBanner.Severity = pending.Severity;
                SaveBanner.Title = pending.Title;
                SaveBanner.Message = pending.Message;
            }
            SetInfoBarOpen(SaveBanner, true);
        }
        else
        {
            SetInfoBarOpen(SaveBanner, false);
        }

        MetaText.Text = records.Count == 0
            ? (connected
                ? "this gateway didn't report any channels"
                : "connect to a gateway to see what channels are available")
            : (connected && !gatewayReportedSomething
                ? $"showing {records.Count} common channels (your gateway didn't list any — some may not work)"
                : $"{configured.Count} configured · {available.Count} available to add");
    }

    // ─── Expander construction ─────────────────────────────────────────────

    private Expander BuildExpander(ChannelRecord record)
    {
        var (dotBrushKey, badgeText, badgeSeverity, subtitles) = ResolveHeaderState(record);

        var header = BuildHeader(record, dotBrushKey, badgeText, badgeSeverity, subtitles);
        var body = BuildBody(record);

        // Symmetric 24 px horizontal padding — matches the Fluent 2 page
        // gutter and the design-skill tokens.md "card padding" norm.
        //
        // Vertical padding scales with how "rich" the card is:
        //   * Configured channels (isRichRow=true)   → 32 px top/bottom
        //     gives the title + multi-line activity strip real breathing
        //     room from the card chrome. 24 was reading as "title too
        //     close to top border, last subtitle too close to bottom" in
        //     manual review.
        //   * Unconfigured previews (compact)        → 24 px top/bottom
        //     a single-tagline card doesn't need the same vertical room;
        //     keeping previews compact preserves the visual asymmetry
        //     that signals "this is just a discoverability suggestion,
        //     not a real-state card".
        //
        // Using IsConfigured rather than subtitle count handles the
        // malformed-status edge case where a configured channel happens
        // to return an empty subtitle list (still deserves the
        // configured-card padding).
        var isRichRow = record.IsConfigured || subtitles.Count >= 2;
        var expander = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = _expandedIds.Contains(record.Id),
            Padding = isRichRow
                ? new Thickness(24, 32, 24, 32)
                : new Thickness(24, 24, 24, 24),
            Header = header,
            Content = body,
        };
        expander.Expanding += (_, _) => _expandedIds.Add(record.Id);
        expander.Collapsed += (_, _) => _expandedIds.Remove(record.Id);

        // ──────────────────────────────────────────────────────────────────
        // WinUI Expander template gotcha
        // ──────────────────────────────────────────────────────────────────
        // Setting `Expander.Padding` ONLY pads the expanded body content —
        // it does NOT touch the always-visible header. The template's
        // ExpanderHeader uses two theme resources baked into its setters:
        //
        //   ExpanderHeaderPadding        default: (16, 0, 8, 0)  ← 0 V !
        //   ExpanderHeaderMinHeight      default: 64 px
        //
        // The 0 top/bottom padding plus MinHeight=64 leaves the title
        // visually pinned ~8 px from the card border regardless of what
        // we set on Padding. A pixel-measured baseline confirmed the
        // Telegram card had ~7 px above the title and ~2 px below the
        // last subtitle, even with `Padding = (24, 32, 24, 32)`.
        //
        // Local Resources["ExpanderHeaderPadding"] = ... DOES NOT work
        // here: the template setter resolves the ThemeResource lookup
        // when the style/template is applied (control loaded), which
        // happens before / outside the visual subtree where our local
        // Resources are visible. Verified empirically in this codebase —
        // overriding via Resources left the card pixel-identical.
        //
        // The robust fix is to apply Margin to the header element
        // itself. Margin lives INSIDE the template's ContentPresenter
        // and stacks ON TOP OF whatever ExpanderHeaderPadding the
        // template applies, so the header content always gets at least
        // `headerVPadding` px of breathing room above and below.
        //
        // Calibration (verified empirically by screenshot pixel-counting
        // at 100% DPI on the OpenClaw hub window):
        //
        //   Margin     Telegram-card visible (top / bottom)
        //   ──────    ──────────────────────────────────────
        //   32         48 px / 45 px   (much too generous)
        //   16         28 px / 25 px   (top hits floor of range)
        //   20         32 px / 29 px   (lands cleanly in 28-32 ✓)
        //
        // The Expander template adds an effectively-fixed ~12 px on top
        // and ~9 px on bottom on top of our Margin (chevron column
        // intrinsic centering + ContentRoot Grid measurement). That
        // means setting Margin=20 delivers the 32/29 px of visible
        // breathing room the design called for, with ~3 px of natural
        // asymmetry due to text-descender pixels being included in the
        // bottom measurement (cap-height-to-border reads visually
        // symmetric).
        //
        // For compact (unconfigured) cards we scale down to keep the
        // intentional "this is just a discoverability suggestion"
        // asymmetry from the original design: Margin=14 lands ~24/21
        // visible — close to the originally-intended 24/24 once the
        // template overhead is accounted for.
        var headerVPadding = isRichRow ? 20.0 : 14.0;
        header.Margin = new Thickness(0, headerVPadding, 0, headerVPadding);

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(expander, record.Label);
        _expanderById[record.Id] = expander;
        return expander;
    }

    private FrameworkElement BuildHeader(
        ChannelRecord record,
        string dotBrushKey,
        string badgeText,
        BadgeSeverity badgeSeverity,
        IReadOnlyList<string> subtitles)
    {
        // Win 11 Settings-style header layout: 2 columns. With all action
        // buttons moved into a CONTROLS section at the top of the body,
        // the header is now pure status:
        //
        //   Col 0: status dot      (Auto)
        //   Col 1: title + subtitle stack (1*)
        //
        // The expand chevron is rendered by the Expander template at the
        // right edge — outside our Grid.
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Column 0: status dot. Top-aligned so it sits with the title row,
        // not vertically centered with the whole content stack (which
        // would float it down between subtitle lines on rich cards).
        // 6 px top offset aligns the dot's vertical center with the
        // BodyStrongTextBlockStyle title's cap-height baseline.
        var dot = new Ellipse
        {
            Width = 12, Height = 12,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0),
        };
        if (Application.Current.Resources[dotBrushKey] is Brush brush) dot.Fill = brush;
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        // Column 1: title + subtitle stack. Top-aligned so the title row
        // is at a predictable top distance from the card chrome on both
        // short (1-subtitle) and tall (3-subtitle) cards.
        //
        // Two-tier rhythm:
        //   * Spacing=8 between top row and subtitle group (groups feel
        //     related but distinct from the title row)
        //   * Subtitle lines themselves cluster at Spacing=4 inside a
        //     nested stack (related metadata reads as a paragraph, not
        //     three independent rows)
        var textColumn = new StackPanel
        {
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        topRow.Children.Add(new TextBlock
        {
            Text = record.Label,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        topRow.Children.Add(BuildBadge(badgeText, badgeSeverity));
        textColumn.Children.Add(topRow);

        if (subtitles.Count > 0)
        {
            var subtitleStack = new StackPanel { Spacing = 4 };
            foreach (var line in subtitles)
            {
                if (string.IsNullOrEmpty(line)) continue;
                subtitleStack.Children.Add(new TextBlock
                {
                    Text = line,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
            if (subtitleStack.Children.Count > 0)
                textColumn.Children.Add(subtitleStack);
        }
        Grid.SetColumn(textColumn, 1);
        grid.Children.Add(textColumn);

        return grid;
    }

    private Border BuildBadge(string text, BadgeSeverity severity)
    {
        var (bgKey, fgKey) = severity switch
        {
            BadgeSeverity.Success  => ("SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush"),
            BadgeSeverity.Caution  => ("SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush"),
            BadgeSeverity.Critical => ("SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush"),
            _                      => ("ControlFillColorSecondaryBrush", "TextFillColorSecondaryBrush"),
        };
        var bg = Application.Current.Resources.TryGetValue(bgKey, out var bgObj) && bgObj is Brush b
            ? b
            : (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"];
        var fg = Application.Current.Resources.TryGetValue(fgKey, out var fgObj) && fgObj is Brush f
            ? f
            : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

        return new Border
        {
            Background = bg,
            // Pill: CornerRadius must equal at least half the rendered height
            // for full half-circle ends. Setting MinHeight and matching the
            // radius keeps the shape consistent regardless of the inner
            // TextBlock's font metrics on different DPIs — relying on
            // CornerRadius=999 alone was getting rendered as a soft-rounded
            // rectangle on Win11 (radius clamped against the bar's actual
            // measured height which is shorter than expected for small text).
            CornerRadius = new CornerRadius(10),
            MinHeight = 20,
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                // FontSize=11 is an intentional exception to tokens.md's
                // 12 px minimum — this is a status chip, not body text, and
                // FontSize=12 makes the pill visually chunky against the
                // surrounding 14 px text. Keep the override here only.
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private Button BuildHeaderActionButton(string glyph, string label, string? tag, Func<string?, Task> handler)
    {
        // Real WinUI command button — not a caption-sized chip. Specs from
        // the Fluent critique: MinHeight=32 (standard control height),
        // 16 px icon (legible — was 12), BodyTextBlockStyle for the label
        // (looks tappable — caption made it read as inline metadata),
        // 12 px horizontal padding (matches default Button rhythm).
        var btn = new Button
        {
            MinHeight = 32,
            Padding = new Thickness(12, 5, 12, 5),
            Tag = tag,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var icon = FluentIconCatalog.Build(glyph, size: 16);
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        btn.Content = stack;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, label);
        // Reentrancy guard — disable the button while the async handler runs
        // so a rapid double-click can't send duplicate gateway commands (or,
        // for handlers that show a ContentDialog, crash with COMException
        // because WinUI doesn't allow two simultaneous ShowAsync calls).
        // The button is rebuilt on every Render(), so even if a re-enable
        // race orphans this instance, the next render gets a fresh button.
        btn.Click += async (s, _) =>
        {
            var clicked = (Button)s;
            if (!clicked.IsEnabled) return;
            clicked.IsEnabled = false;
            try
            {
                await handler(clicked.Tag as string);
            }
            finally
            {
                clicked.IsEnabled = true;
            }
        };
        return btn;
    }

    // ─── Body construction ─────────────────────────────────────────────────

    private FrameworkElement BuildBody(ChannelRecord record)
    {
        var stack = new StackPanel { Spacing = 16, Margin = new Thickness(0, 8, 0, 0) };

        // iMessage and other Windows-incompatible channels: show the setup
        // guide (which explains *why* and points at relevant docs) but skip
        // Status/Linking/Configuration since nothing here will actually work.
        if (record.IsUnavailableOnWindows)
        {
            var unavailableGuide = BuildSetupGuide(record);
            if (unavailableGuide != null) stack.Children.Add(unavailableGuide);
            else stack.Children.Add(BuildInfoText(
                "This channel requires a macOS host. It can't be configured from a Windows machine."));
            return stack;
        }

        // CONTROLS section — every state-changing action for configured
        // channels lives here, at the TOP of the body. Header has no
        // action buttons; the chevron is the only header-level
        // affordance. This gives every card the same mental model:
        // expand to act, header to read state.
        var controls = BuildControlsSection(record);
        if (controls != null)
            stack.Children.Add(controls);

        // Getting started — only for unconfigured channels. The user doesn't
        // need to read setup instructions for something that's already running.
        if (!record.IsConfigured)
        {
            var guide = BuildSetupGuide(record);
            if (guide != null) stack.Children.Add(guide);
        }

        // Status K-V section removed entirely. With the multi-line activity
        // strip on the card surface (Polling mode · Last event 23 s ago · …)
        // there's no remaining value in repeating the same data as a key/
        // value grid inside the expander body, regardless of configured
        // state.

        // Linking section (WhatsApp/Signal) — body is built lazily when the user opens the section.
        if (record.Capabilities.HasFlag(ChannelCapabilities.CanShowQr))
            stack.Children.Add(BuildLinkingPlaceholder(record));

        // Configuration section — inline credential form for channels we have
        // explicit field definitions for (Telegram bot token, Discord webhook,
        // Slack tokens, Google Chat webhook, Nostr key/relays).
        //
        // For configured channels the section header reads "Replace
        // credentials" so the form's purpose is clear; for unconfigured
        // channels it reads "Configuration" because that's where the
        // channel becomes configured in the first place.
        var inlineForm = BuildInlineConfigForm(record);
        var configSectionTitle = record.IsConfigured ? "Replace credentials" : "Configuration";
        stack.Children.Add(BuildSection(configSectionTitle, inlineForm));

        // "Install plugin on your gateway" panel. Hidden entirely when the
        // channel is already running — if it's up, the plugin is provably
        // loaded and showing install instructions is misleading. Shown for
        // unconfigured channels (plugin state unknown) AND for configured-
        // but-not-running channels (missing plugin is one likely cause).
        if (!record.IsRunning)
            stack.Children.Add(BuildInstallPluginPanel(record));

        return stack;
    }

    /// <summary>
    /// CONTROLS section — every state-changing action for the channel,
    /// grouped at the top of the expanded body. Returns null for
    /// unconfigured channels (nothing meaningful to "control" yet) and
    /// for channels with no applicable actions.
    ///
    /// Contents per channel state:
    ///   * Configured + running, non-QR  → [Stop] + [Disconnect and forget credentials]
    ///   * Configured + not running, non-QR → [Start] + [Disconnect and forget credentials]
    ///   * Configured + running, QR (WhatsApp/Signal) → [Logout] (the QR-flow analog of Stop; unlinks the device, scan a fresh QR to relink)
    ///   * Configured + linked-but-not-connected, QR → [Logout]
    /// </summary>
    private FrameworkElement? BuildControlsSection(ChannelRecord record)
    {
        if (!record.IsConfigured) return null;

        var caps = record.Capabilities;
        var hasStart = caps.HasFlag(ChannelCapabilities.CanStart);
        var hasStop = caps.HasFlag(ChannelCapabilities.CanStop);
        var hasLogout = caps.HasFlag(ChannelCapabilities.CanLogout);
        var isQr = caps.HasFlag(ChannelCapabilities.CanShowQr);

        if (!hasStart && !hasStop && !hasLogout) return null;

        var stack = new StackPanel { Spacing = 8 };

        // Caption explains the action set at a glance — what's the
        // lightweight option vs the destructive one. Keeps users from
        // accidentally clicking Disconnect when they meant Stop.
        string caption;
        if (isQr && hasLogout)
        {
            caption = "Unlink this device from the channel. Scan a fresh QR to reconnect — your account stays paired on your phone.";
        }
        else if (hasStop && hasLogout)
        {
            caption = "Pause the channel temporarily — credentials are kept so you can start it again. Or disconnect entirely to clear them.";
        }
        else if (hasStart && hasLogout)
        {
            caption = "Start the channel — it'll begin handling messages with the stored credentials. Or disconnect entirely to clear them.";
        }
        else if (hasStop)
        {
            caption = "Pause the channel — credentials are kept so you can start it again.";
        }
        else if (hasStart)
        {
            caption = "Start the channel — it'll begin handling messages with the stored credentials.";
        }
        else
        {
            caption = "";
        }

        if (!string.IsNullOrEmpty(caption))
        {
            stack.Children.Add(new TextBlock
            {
                Text = caption,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        if (hasStart)
            buttonRow.Children.Add(BuildHeaderActionButton(FluentIconCatalog.ChannelStart, "Start", record.Id, channelId => StartChannelAsync(channelId!)));
        if (hasStop)
        {
            var stopBtn = new Button
            {
                Content = "Stop",
                MinHeight = 32,
                Padding = new Thickness(12, 5, 12, 5),
            };
            // Reentrancy guard — see BuildHeaderActionButton for rationale.
            stopBtn.Click += async (_, _) =>
            {
                if (!stopBtn.IsEnabled) return;
                stopBtn.IsEnabled = false;
                try { await StopChannelAsync(record.Id); }
                finally { stopBtn.IsEnabled = true; }
            };
            buttonRow.Children.Add(stopBtn);
        }

        // QR channels: Logout = unlink the device (lightweight, can re-scan QR).
        // Non-QR channels: Logout = clear credentials (destructive — label says so).
        if (hasLogout && isQr)
        {
            buttonRow.Children.Add(BuildHeaderActionButton(FluentIconCatalog.ChannelLogout, "Logout", record.Id, channelId => LogoutAsync(channelId!, isQr: true)));
        }
        else if (hasLogout && !isQr)
        {
            var disconnectBtn = new Button
            {
                Content = "Disconnect and forget credentials",
                MinHeight = 32,
                Padding = new Thickness(12, 5, 12, 5),
            };
            // Reentrancy guard is especially important here — LogoutAsync shows
            // a ContentDialog, and WinUI throws COMException if a second
            // ShowAsync starts before the first closes.
            disconnectBtn.Click += async (_, _) =>
            {
                if (!disconnectBtn.IsEnabled) return;
                disconnectBtn.IsEnabled = false;
                try { await LogoutAsync(record.Id, isQr: false); }
                finally { disconnectBtn.IsEnabled = true; }
            };
            buttonRow.Children.Add(disconnectBtn);
        }

        stack.Children.Add(buttonRow);
        return BuildSection("Controls", stack);
    }

    private static FrameworkElement BuildSection(string title, FrameworkElement content)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 80,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        panel.Children.Add(content);
        return panel;
    }

    /// <summary>
    /// Per-channel "Getting started" card. Shown only for unconfigured channels
    /// so the user has a concrete, channel-specific path forward instead of a
    /// generic "Open Config page" stub. Each guide is a short numbered list
    /// describing exactly what to do for that channel, plus an external help
    /// link when there's a canonical third-party page to visit.
    /// </summary>
    private FrameworkElement? BuildSetupGuide(ChannelRecord record)
    {
        var (headline, steps) = ResolveSetupGuide(record.Id);
        if (headline == null) return null;

        var card = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
        };

        var stack = new StackPanel { Spacing = 6 };

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var icon = FluentIconCatalog.Build("\uE946", 16); // Info glyph
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        headerRow.Children.Add(icon);
        headerRow.Children.Add(new TextBlock
        {
            Text = headline,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(headerRow);

        for (int i = 0; i < steps!.Length; i++)
        {
            var stepRow = new Grid();
            stepRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            stepRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var num = new TextBlock
            {
                Text = (i + 1).ToString() + ".",
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetColumn(num, 0);
            stepRow.Children.Add(num);

            var body = new TextBlock
            {
                Text = steps[i],
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(body, 1);
            stepRow.Children.Add(body);

            stack.Children.Add(stepRow);
        }

        // External help link, if we have one for this channel. Clicking the
        // HyperlinkButton opens the user's default browser via the standard
        // WinUI mechanism (we don't need a code-behind handler).
        var (linkText, linkUrl) = ResolveExternalHelpLink(record.Id);
        if (!string.IsNullOrEmpty(linkUrl))
        {
            var helpLink = new HyperlinkButton
            {
                Content = linkText,
                NavigateUri = new Uri(linkUrl),
                Padding = new Thickness(0, 4, 0, 0),
            };
            stack.Children.Add(helpLink);
        }

        card.Child = stack;
        return card;
    }

    /// <summary>
    /// External help URL for each channel. Where the credentials come from a
    /// phone app (WhatsApp / Signal) we point at the canonical "Linked devices"
    /// help page — useful when the user doesn't know where the toggle lives.
    /// Where the channel is Windows-incompatible (iMessage) we link to a doc
    /// explaining why.
    /// </summary>
    private static (string? Text, string? Url) ResolveExternalHelpLink(string channelId) =>
        channelId.ToLowerInvariant() switch
        {
            "whatsapp"   => ("WhatsApp Linked devices help →",     "https://faq.whatsapp.com/378279004439436/"),
            "signal"     => ("Signal Linked devices help →",       "https://support.signal.org/hc/en-us/articles/360007320551"),
            "telegram"   => ("How to create a Telegram bot →",     "https://core.telegram.org/bots/features#botfather"),
            "discord"    => ("How to create a Discord webhook →",  "https://support.discord.com/hc/en-us/articles/228383668"),
            "googlechat" => ("How to add a Google Chat webhook →", "https://developers.google.com/chat/how-tos/webhooks"),
            "slack"      => ("Slack app dashboard →",              "https://api.slack.com/apps"),
            "nostr"      => ("About Nostr →",                      "https://nostr.com/"),
            "imessage"   => ("About macOS Messages →",             "https://support.apple.com/guide/messages/welcome/mac"),
            _ => (null, null),
        };

    /// <summary>
    /// Channel-specific setup content. Returns (null, null) for channels we
    /// don't have explicit guidance for — the generic Configuration section
    /// still renders, so plugin channels aren't left without any signposting.
    /// </summary>
    private static (string? Headline, string[]? Steps) ResolveSetupGuide(string channelId) =>
        channelId.ToLowerInvariant() switch
        {
            "whatsapp" => ("Link your WhatsApp phone", new[]
            {
                "Click \"Show QR\" in the Linking section below.",
                "On your phone: WhatsApp → Settings → Linked devices → Link a device.",
                "Scan the QR code that appears here.",
            }),
            "signal" => ("Link your Signal phone", new[]
            {
                "Click \"Show QR\" in the Linking section below.",
                "On your phone: Signal → Settings → Linked devices → Link new device.",
                "Scan the QR code that appears here.",
            }),
            "telegram" => ("Connect Telegram via a bot", new[]
            {
                "Open Telegram and send a message to @BotFather.",
                "Send /newbot and follow the prompts. Copy the bot token at the end.",
                "Paste the token into the Configuration form below.",
                "Click \"Save and start\". The channel will start automatically.",
            }),
            "discord" => ("Connect Discord via a webhook", new[]
            {
                "Open your Discord server settings → Integrations → Webhooks.",
                "Click \"New Webhook\", give it a name, and copy the webhook URL.",
                "Paste the URL into the Configuration form below.",
                "Click \"Save and start\".",
            }),
            "googlechat" => ("Connect Google Chat via a webhook", new[]
            {
                "In Google Chat, open the space → Manage webhooks → Add webhook.",
                "Copy the webhook URL.",
                "Paste the URL into the Configuration form below.",
                "Click \"Save and start\".",
            }),
            "slack" => ("Connect Slack via an app", new[]
            {
                "Create a Slack app at api.slack.com/apps and install it to your workspace.",
                "Copy the bot token (xoxb-…) and the signing secret.",
                "Paste both into the Configuration form below.",
                "Click \"Save and start\".",
            }),
            "nostr" => ("Connect Nostr via relays", new[]
            {
                "Generate or paste a private key (nsec).",
                "Pick one or more relay URLs (e.g. wss://relay.damus.io).",
                "Paste both into the Configuration form below.",
                "Click \"Save and start\".",
            }),
            "imessage" => ("iMessage is macOS-only", new[]
            {
                "iMessage reads the local Messages.app database, which only exists on macOS.",
                "To use iMessage with OpenClaw, run the gateway on a Mac instead of this Windows host.",
                "All other channels in this list work fine from a Windows-hosted gateway.",
            }),
            // Generic fallback for plugin channels we don't have explicit
            // guidance for. Better than leaving the section blank — at least
            // the user knows it's a plugin and where to find its settings.
            _ => ("Connect this channel", new[]
            {
                $"\"{channelId}\" is a plugin channel. Refer to its documentation for the fields it needs.",
                $"Use \"Open Config page\" in the Configuration section below to add settings under channels.{channelId}.",
                "Save the config; OpenClaw will start the channel automatically.",
            }),
        };

    // ─── Inline credential form (no Config page detour) ─────────────────────

    /// <summary>One field rendered in the inline config form.</summary>
    private sealed record ConfigField(
        string Path,
        string Label,
        string Placeholder,
        bool Sensitive,
        bool Required,
        bool Multiline = false,
        string? HelpText = null);

    /// <summary>
    /// Per-channel inline-form schema. Fields were validated against the
    /// gateway test fixtures (src/cli/config-cli.test.ts and related tests
    /// confirm channels.telegram.botToken, channels.slack.botToken/signingSecret,
    /// channels.discord.webhookUrl, etc.). Returns null for channels without an
    /// inline form — those still get the "Open Config page" stub.
    /// </summary>
    private static IReadOnlyList<ConfigField>? ResolveConfigFields(string channelId) =>
        channelId.ToLowerInvariant() switch
        {
            "telegram" => new[]
            {
                new ConfigField(
                    "channels.telegram.botToken",
                    "Bot token",
                    "123456:ABCdef...",
                    Sensitive: true,
                    Required: true,
                    HelpText: "Get from @BotFather (/newbot)."),
            },
            "discord" => new[]
            {
                new ConfigField(
                    "channels.discord.webhookUrl",
                    "Webhook URL",
                    "https://discord.com/api/webhooks/...",
                    Sensitive: true,
                    Required: true,
                    HelpText: "Server Settings → Integrations → Webhooks → New Webhook."),
            },
            "googlechat" => new[]
            {
                new ConfigField(
                    "channels.googlechat.webhookUrl",
                    "Webhook URL",
                    "https://chat.googleapis.com/v1/spaces/...",
                    Sensitive: true,
                    Required: true,
                    HelpText: "Open a space → Manage webhooks → Add webhook."),
            },
            "slack" => new[]
            {
                new ConfigField(
                    "channels.slack.botToken",
                    "Bot token",
                    "xoxb-...",
                    Sensitive: true,
                    Required: true,
                    HelpText: "OAuth tokens from your Slack app."),
                new ConfigField(
                    "channels.slack.signingSecret",
                    "Signing secret",
                    "",
                    Sensitive: true,
                    Required: true,
                    HelpText: "Basic Information → App Credentials."),
            },
            "nostr" => new[]
            {
                new ConfigField(
                    "channels.nostr.nsec",
                    "Private key (nsec)",
                    "nsec1...",
                    Sensitive: true,
                    Required: true),
                new ConfigField(
                    "channels.nostr.relays",
                    "Relay URLs",
                    "wss://relay.damus.io",
                    Sensitive: false,
                    Required: true,
                    Multiline: true,
                    HelpText: "One per line."),
            },
            _ => null,
        };

    /// <summary>
    /// Build the Configuration body for a channel. For channels in
    /// <see cref="ResolveConfigFields"/> we render an inline form that writes
    /// directly to the gateway via <c>config.set</c> — no Config page detour.
    /// For unknown channels we fall back to the generic "Open Config page" stub.
    /// </summary>
    private FrameworkElement BuildInlineConfigForm(ChannelRecord record)
    {
        var fields = ResolveConfigFields(record.Id);
        if (fields == null) return BuildConfigPlaceholder(record);

        var stack = new StackPanel { Spacing = 10 };

        // Cached form state for THIS channel — survives the Expander rebuild
        // that fires after every snapshot refresh (Save → Refresh → Render →
        // new Expander instance). Without this the user's typed token vanishes.
        if (!_formValuesByChannel.TryGetValue(record.Id, out var cached))
        {
            cached = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _formValuesByChannel[record.Id] = cached;
        }

        // Field inputs — track the FrameworkElement (TextBox / PasswordBox) so
        // Save can read the value and validate "required".
        var inputs = new Dictionary<string, FrameworkElement>();

        foreach (var field in fields)
        {
            var row = new StackPanel { Spacing = 4 };

            // Label with optional "required" marker.
            var label = new TextBlock
            {
                Text = field.Required ? $"{field.Label} *" : field.Label,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            };
            row.Children.Add(label);

            // Input: PasswordBox for sensitive, multi-line TextBox for relay lists,
            // single-line TextBox otherwise.
            FrameworkElement input;
            var restored = cached.TryGetValue(field.Path, out var prior) ? prior : null;
            if (field.Sensitive)
            {
                var pb = new PasswordBox
                {
                    PlaceholderText = field.Placeholder,
                    PasswordRevealMode = PasswordRevealMode.Peek,
                };
                if (!string.IsNullOrEmpty(restored)) pb.Password = restored;
                // Mirror PasswordChanged events into the cache so the value
                // survives the next rebuild.
                pb.PasswordChanged += (s, _) =>
                {
                    if (s is PasswordBox box) cached[field.Path] = box.Password;
                };
                input = pb;
            }
            else if (field.Multiline)
            {
                var tb = new TextBox
                {
                    PlaceholderText = field.Placeholder,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    MinHeight = 72,
                };
                if (!string.IsNullOrEmpty(restored)) tb.Text = restored;
                tb.TextChanged += (s, _) =>
                {
                    if (s is TextBox box) cached[field.Path] = box.Text;
                };
                input = tb;
            }
            else
            {
                var tb = new TextBox { PlaceholderText = field.Placeholder };
                if (!string.IsNullOrEmpty(restored)) tb.Text = restored;
                tb.TextChanged += (s, _) =>
                {
                    if (s is TextBox box) cached[field.Path] = box.Text;
                };
                input = tb;
            }
            row.Children.Add(input);
            inputs[field.Path] = input;

            // Help text.
            if (!string.IsNullOrEmpty(field.HelpText))
            {
                row.Children.Add(new TextBlock
                {
                    Text = field.HelpText,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            stack.Children.Add(row);
        }

        // Save row.
        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        var saveBtn = new Button
        {
            Content = record.IsConfigured ? "Save changes" : "Save and start",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        var openConfigBtn = new Button
        {
            Content = "Open Config page",
        };
        openConfigBtn.Click += (_, _) => ((IAppCommands)CurrentApp).Navigate("config");
        actionRow.Children.Add(saveBtn);
        actionRow.Children.Add(openConfigBtn);
        stack.Children.Add(actionRow);

        async Task SaveAsync()
        {
            // This is a Save flow — let Render upgrade the success banner to
            // "is running" / "config saved, but not running yet" and clear
            // cached form drafts on success.
            _pendingBannerIsAction = false;

            var client = CurrentApp.GatewayClient;
            if (client == null)
            {
                _pendingSaveBanner = (record.Id, "Not connected",
                    "Connect to a gateway before saving channel config.",
                    InfoBarSeverity.Error);
                ApplyPendingSaveBanner();
                return;
            }

            // Validate required + collect values from the inputs.
            var values = new List<(string Path, string Value)>();
            var multilinePaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                var raw = inputs[field.Path] switch
                {
                    PasswordBox pb => pb.Password,
                    TextBox tb => tb.Text,
                    _ => string.Empty,
                };
                if (field.Required && string.IsNullOrWhiteSpace(raw))
                {
                    _pendingSaveBanner = (record.Id, "Missing field",
                        $"{field.Label} is required.",
                        InfoBarSeverity.Error);
                    ApplyPendingSaveBanner();
                    return;
                }
                if (string.IsNullOrWhiteSpace(raw)) continue;
                values.Add((field.Path, raw.Trim()));
                if (field.Multiline) multilinePaths.Add(field.Path);
            }

            if (values.Count == 0) return;

            saveBtn.IsEnabled = false;
            try
            {
                _pendingSaveBanner = (record.Id, $"Saving {record.Id}…",
                    $"Writing {values.Count} field(s) and enabling the channel.",
                    InfoBarSeverity.Informational);
                ApplyPendingSaveBanner();

                // Gateway v2026.5+ rejects per-field config.set { path, value }
                // with "must have required property 'raw'". Build an atomic
                // config.patch payload from the cached config + the user's
                // new values, send it as a single round-trip, and surface
                // the gateway's actual response.
                if (!await EnsureConfigLoadedAsync())
                {
                    _pendingSaveBanner = (record.Id, $"Couldn't load gateway config",
                        "The gateway didn't return its current config — can't safely save without it. Try Refresh and save again.",
                        InfoBarSeverity.Error);
                    ApplyPendingSaveBanner();
                    return;
                }

                // Snapshot once: read both root and baseHash from the same
                // atomic publish so a concurrent config.get can't desync
                // them under us (Hanselman review HIGH-2).
                var configForThisSave = _configSnapshot;
                if (!configForThisSave.HasRoot)
                {
                    // Shouldn't happen — EnsureConfigLoadedAsync only returns
                    // true when the snapshot is populated — but defend
                    // against a race where the snapshot was reset between
                    // the load returning and this read.
                    _pendingSaveBanner = (record.Id, $"Couldn't load gateway config",
                        "The gateway's config was cleared mid-save. Try Refresh and save again.",
                        InfoBarSeverity.Error);
                    ApplyPendingSaveBanner();
                    return;
                }

                var updates = values.Select(v => (v.Path, (object)v.Value)).ToList();
                var buildResult = ChannelConfigPatchBuilder.BuildPatch(
                    configForThisSave.Root,
                    record.Id,
                    updates,
                    multilinePaths);

                if (buildResult.BlockedReason != null)
                {
                    _pendingSaveBanner = (record.Id, $"Save blocked for {record.Id}",
                        buildResult.BlockedReason,
                        InfoBarSeverity.Warning);
                    ApplyPendingSaveBanner();
                    return;
                }

                var patchResult = await client.PatchConfigDetailedAsync(buildResult.Patch!.Value, configForThisSave.BaseHash);

                if (!patchResult.Ok)
                {
                    var detail = patchResult.Error ?? "The gateway didn't respond.";
                    var title = $"Save failed for {record.Id}";
                    if (patchResult.LooksLikeStaleBaseHash)
                    {
                        // Config changed elsewhere. Refresh the cache so the
                        // next retry uses a fresh baseHash.
                        _ = client.RequestConfigAsync();
                        _pendingSaveBanner = (record.Id, title,
                            $"Your gateway config changed elsewhere (e.g., from the Config page). " +
                            $"We refreshed the cache — try Save again. Details: {detail}",
                            InfoBarSeverity.Warning);
                    }
                    else
                    {
                        _pendingSaveBanner = (record.Id, title,
                            $"{detail} If this looks like a wire-format mismatch, open the Config page for direct JSON editing.",
                            InfoBarSeverity.Error);
                    }
                    ApplyPendingSaveBanner();
                    return;
                }

                _pendingSaveBanner = (record.Id, $"{record.Id} config saved",
                    "Waiting for the gateway to confirm the channel is running…",
                    InfoBarSeverity.Success);
                ApplyPendingSaveBanner();
                // Invalidate the snapshot so a rapid second save MUST wait
                // for a fresh baseHash — protects against silent clobber in
                // the baseHash-null case (Hanselman review HIGH-2 follow-on).
                // The fresh config.get below will repopulate the snapshot.
                _configSnapshot = ConfigSnapshot.Empty;
                _ = client.RequestConfigAsync();
                await RefreshAsync(probe: true);
            }
            finally
            {
                saveBtn.IsEnabled = true;
            }
        }
        saveBtn.Click += async (_, _) => await SaveAsync();

        return stack;
    }

    /// <summary>
    /// Push <see cref="_pendingSaveBanner"/> to the page-level
    /// <c>SaveBanner</c> InfoBar. Used as a stand-alone update when we don't
    /// want to trigger a full Render() (e.g. validation errors before any
    /// gateway call).
    /// </summary>
    private void ApplyPendingSaveBanner()
    {
        if (_pendingSaveBanner is not { } p)
        {
            SetInfoBarOpen(SaveBanner, false);
            return;
        }
        SaveBanner.Severity = p.Severity;
        SaveBanner.Title = p.Title;
        SaveBanner.Message = p.Message;
        SetInfoBarOpen(SaveBanner, true);
    }

    private static FrameworkElement BuildStatusKv(ChannelRecord record)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var pairs = BuildStatusPairs(record);
        for (int i = 0; i < pairs.Count; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < pairs.Count; i++)
        {
            var (key, value) = pairs[i];
            var keyBlock = new TextBlock
            {
                Text = key,
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Margin = new Thickness(0, 4, 0, 4),
            };
            Grid.SetRow(keyBlock, i);
            Grid.SetColumn(keyBlock, 0);
            grid.Children.Add(keyBlock);

            var valBlock = new TextBlock
            {
                Text = value,
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 4),
            };
            Grid.SetRow(valBlock, i);
            Grid.SetColumn(valBlock, 1);
            grid.Children.Add(valBlock);
        }

        return grid;
    }

    private static List<(string Key, string Value)> BuildStatusPairs(ChannelRecord record)
    {
        var raw = record.RawStatus;
        var pairs = new List<(string, string)>();

        if (raw.ValueKind != JsonValueKind.Object)
        {
            pairs.Add(("Status", record.IsConfigured ? "configured" : "not configured"));
            return pairs;
        }

        // Lifecycle status
        var configured = GetBool(raw, "configured");
        var running = GetBool(raw, "running");
        var connected = GetBool(raw, "connected");
        var linked = GetBool(raw, "linked");
        var lastError = GetString(raw, "lastError") ?? GetString(raw, "error");

        // Lowercase short forms per naming.md status vocabulary.
        if (!string.IsNullOrEmpty(lastError))
            pairs.Add(("Status", "error"));
        else if (connected == true)
            pairs.Add(("Status", "connected"));
        else if (running == true)
            pairs.Add(("Status", "running"));
        else if (configured == true)
            pairs.Add(("Status", "configured"));
        else
            pairs.Add(("Status", "not configured"));

        if (linked == true)
        {
            // WhatsApp-style self.e164 if present
            if (raw.TryGetProperty("self", out var self) && self.ValueKind == JsonValueKind.Object)
            {
                var e164 = GetString(self, "e164") ?? GetString(self, "jid");
                if (!string.IsNullOrEmpty(e164))
                    pairs.Add(("Linked as", e164));
                else
                    pairs.Add(("Linked", "Yes"));
            }
            else
            {
                pairs.Add(("Linked", "Yes"));
            }
        }

        // Auth age (WA)
        if (raw.TryGetProperty("authAgeMs", out var ageMs) && ageMs.ValueKind == JsonValueKind.Number && ageMs.TryGetDouble(out var ageDouble))
            pairs.Add(("Auth age", FormatAge(ageDouble)));

        // Reconnect attempts (WA)
        if (raw.TryGetProperty("reconnectAttempts", out var attempts) && attempts.ValueKind == JsonValueKind.Number && attempts.TryGetInt32(out var att) && att > 0)
            pairs.Add(("Reconnect attempts", att.ToString()));

        // Probe info (TG/Discord/Signal/GoogleChat/iMessage)
        if (raw.TryGetProperty("probe", out var probe) && probe.ValueKind == JsonValueKind.Object)
        {
            var ok = GetBool(probe, "ok");
            var elapsed = probe.TryGetProperty("elapsedMs", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var ed) ? (double?)ed : null;
            var version = GetString(probe, "version");
            var probeError = GetString(probe, "error");
            if (ok == true)
            {
                var parts = new List<string> { "OK" };
                if (elapsed.HasValue) parts.Add($"{(int)elapsed.Value} ms");
                if (!string.IsNullOrEmpty(version)) parts.Add(version);
                pairs.Add(("Last probe", string.Join(" · ", parts)));
            }
            else if (ok == false)
            {
                pairs.Add(("Last probe", $"Failed · {probeError ?? "unknown error"}"));
            }
        }

        // Channel-specific identifiers
        if (GetString(raw, "botUsername") is { Length: > 0 } botUsername)
            pairs.Add(("Bot", "@" + botUsername));
        if (GetString(raw, "webhookUrl") is { Length: > 0 } webhook)
            pairs.Add(("Webhook", webhook));
        if (GetString(raw, "baseUrl") is { Length: > 0 } baseUrl)
            pairs.Add(("Base URL", baseUrl));

        // Accounts
        if (record.Accounts.Count > 0)
            pairs.Add(("Accounts", record.Accounts.Count == 1 ? "1" : record.Accounts.Count.ToString()));

        if (!string.IsNullOrEmpty(lastError))
            pairs.Add(("Last error", lastError));

        return pairs;
    }

    private FrameworkElement BuildLinkingPlaceholder(ChannelRecord record)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = "LINKING",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 80,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        var qrImage = new Image
        {
            Width = 180,
            Height = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
        };

        // Initial message is a call-to-action, not "scan this QR" — the QR
        // doesn't exist yet. RenderQrAsync (and the success path) replace this
        // text with the real instructions once a QR is on screen.
        var messageBlock = new TextBlock
        {
            Text = "Click \"Show QR\" to start linking your phone to this device.",
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        };

        // Collapsed diagnostic detail — populated by StartLinkingAsync when a
        // call fails so the user can see exactly what the gateway said
        // instead of just our paraphrased error message.
        var diagnostic = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = false,
            Header = "Why isn't this working?",
            Visibility = Visibility.Collapsed,
        };
        var diagnosticBody = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Margin = new Thickness(0, 8, 0, 0),
        };
        diagnostic.Content = diagnosticBody;

        var buttonsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var showQrBtn = new Button { Content = "Show QR", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var relinkBtn = new Button { Content = "Relink" };
        buttonsRow.Children.Add(showQrBtn);
        buttonsRow.Children.Add(relinkBtn);

        // Re-entrancy lock: disable both buttons while a linking flow is in flight
        // so rapid Show QR / Relink clicks can't spawn parallel web.login.start calls.
        async Task RunLinking(bool force)
        {
            showQrBtn.IsEnabled = false;
            relinkBtn.IsEnabled = false;
            try
            {
                await StartLinkingAsync(qrImage, messageBlock, diagnostic, diagnosticBody, record.Id, force);
            }
            finally
            {
                showQrBtn.IsEnabled = true;
                relinkBtn.IsEnabled = true;
            }
        }
        showQrBtn.Click += async (_, _) => await RunLinking(force: false);
        relinkBtn.Click += async (_, _) => await RunLinking(force: true);

        stack.Children.Add(qrImage);
        stack.Children.Add(messageBlock);
        stack.Children.Add(diagnostic);
        stack.Children.Add(buttonsRow);
        return stack;
    }

    private async Task StartLinkingAsync(
        Image qrImage,
        TextBlock messageBlock,
        Expander diagnostic,
        TextBlock diagnosticBody,
        string channelId,
        bool force)
    {
        // Local helper: show the diagnostic disclosure with method/params/response
        // info so the user can see *exactly* what the gateway did or didn't do.
        void ShowDiagnostic(string method, object @params, string? error, string? rawResponse)
        {
            var parts = new List<string>
            {
                $"Method:   {method}",
                $"Channel:  {channelId}",
                $"Params:   {System.Text.Json.JsonSerializer.Serialize(@params)}",
            };
            if (!string.IsNullOrEmpty(error)) parts.Add($"Error:    {error}");
            if (!string.IsNullOrEmpty(rawResponse))
            {
                // Trim verbose stack traces; keep first 1000 chars (plenty for diagnosis,
                // small enough to read in the disclosure).
                var trimmed = rawResponse.Length > 1000 ? rawResponse[..1000] + "…" : rawResponse;
                parts.Add($"Response: {trimmed}");
            }
            diagnosticBody.Text = string.Join("\n", parts);
            diagnostic.Visibility = Visibility.Visible;
        }
        void HideDiagnostic()
        {
            diagnostic.IsExpanded = false;
            diagnostic.Visibility = Visibility.Collapsed;
            diagnosticBody.Text = "";
        }

        var client = CurrentApp.GatewayClient;
        if (client == null)
        {
            qrImage.Visibility = Visibility.Collapsed;
            messageBlock.Text = "Not connected to a gateway. Open Connection settings to connect first.";
            HideDiagnostic();
            return;
        }

        // Cancel any previous linking session before starting a new one.
        var oldLinking = _linkingCts;
        _linkingCts = new CancellationTokenSource();
        var ct = _linkingCts.Token;
        if (oldLinking != null)
        {
            try { oldLinking.Cancel(); oldLinking.Dispose(); } catch { }
        }

        messageBlock.Text = "Requesting QR code from the gateway…";
        HideDiagnostic();

        var startParams = new { force, timeoutMs = 30000 };
        var start = await client.WebLoginStartAsync(force);
        if (ct.IsCancellationRequested) return;

        if (start == null)
        {
            // Only happens when the websocket is not connected; no gateway response
            // to surface.
            qrImage.Visibility = Visibility.Collapsed;
            messageBlock.Text = $"Couldn't link {channelId}. Not connected to the gateway.";
            return;
        }

        if (!string.IsNullOrEmpty(start.Error))
        {
            qrImage.Visibility = Visibility.Collapsed;
            messageBlock.Text = $"Couldn't link {channelId}. The gateway returned an error — see details below.";
            ShowDiagnostic("web.login.start", startParams, start.Error, start.RawResponse);
            return;
        }
        if (start.Connected)
        {
            messageBlock.Text = !string.IsNullOrEmpty(start.Message)
                ? start.Message
                : $"{channelId} is already linked.";
            qrImage.Visibility = Visibility.Collapsed;
            await RefreshAsync(probe: false);
            return;
        }
        if (string.IsNullOrEmpty(start.QrDataUrl))
        {
            // Gateway accepted the call (no Error) but returned no QR — show the
            // raw response so the user can see what it did say.
            qrImage.Visibility = Visibility.Collapsed;
            messageBlock.Text = !string.IsNullOrEmpty(start.Message)
                ? start.Message
                : $"Gateway didn't return a QR for {channelId}. See details below for what it returned.";
            ShowDiagnostic("web.login.start", startParams, null, start.RawResponse);
            return;
        }

        await RenderQrAsync(qrImage, messageBlock, start.QrDataUrl);
        if (ct.IsCancellationRequested) return;
        messageBlock.Text = !string.IsNullOrEmpty(start.Message)
            ? start.Message
            : channelId.Equals("whatsapp", StringComparison.OrdinalIgnoreCase)
                ? "Open WhatsApp on your phone → Settings → Linked devices → scan this QR."
                : "Open the mobile app's linked-devices screen and scan this QR.";

        // Long-poll once for completion
        var waitParams = new { currentQrDataUrl = start.QrDataUrl, timeoutMs = 30000 };
        var waitResult = await client.WebLoginWaitAsync(start.QrDataUrl, timeoutMs: 30000);
        if (ct.IsCancellationRequested) return;
        if (waitResult == null)
        {
            messageBlock.Text = "Still waiting — click Show QR again if the code has expired.";
            return;
        }
        if (!string.IsNullOrEmpty(waitResult.Error))
        {
            messageBlock.Text = $"Link wait failed for {channelId}. See details below.";
            ShowDiagnostic("web.login.wait", waitParams, waitResult.Error, waitResult.RawResponse);
            return;
        }
        if (waitResult.Connected)
        {
            messageBlock.Text = !string.IsNullOrEmpty(waitResult.Message)
                ? waitResult.Message
                : $"{channelId} linked.";
            qrImage.Visibility = Visibility.Collapsed;
            await RefreshAsync(probe: false);
        }
        else if (!string.IsNullOrEmpty(waitResult.QrDataUrl) && waitResult.QrDataUrl != start.QrDataUrl)
        {
            // QR rotated; show the new one.
            await RenderQrAsync(qrImage, messageBlock, waitResult.QrDataUrl);
            if (ct.IsCancellationRequested) return;
            if (!string.IsNullOrEmpty(waitResult.Message))
                messageBlock.Text = waitResult.Message;
        }
        else
        {
            messageBlock.Text = "Still waiting — click Show QR again if the code has expired.";
        }
    }

    private static async Task RenderQrAsync(Image target, TextBlock messageBlock, string dataUrl)
    {
        try
        {
            var commaIdx = dataUrl.IndexOf(',');
            if (commaIdx <= 0)
            {
                target.Visibility = Visibility.Collapsed;
                messageBlock.Text = "QR decode failed: malformed data URL from gateway.";
                return;
            }
            var base64 = dataUrl[(commaIdx + 1)..];
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                target.Visibility = Visibility.Collapsed;
                messageBlock.Text = "QR decode failed: invalid base64 from gateway.";
                return;
            }

            // The stream must outlive the SetSourceAsync call (BitmapImage reads
            // it during decode) but should be disposed after — its native COM
            // handle leaks otherwise.
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            target.Source = bitmap;
            target.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            // Surface decode failures to the user — silent hide makes gateway faults
            // look like UX bugs.
            target.Visibility = Visibility.Collapsed;
            messageBlock.Text = $"QR decode failed: {ex.Message}";
        }
    }

    private FrameworkElement BuildConfigPlaceholder(ChannelRecord record)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = record.IsConfigured
                ? $"Edit this channel's settings in the gateway Config page."
                : $"After following the steps above, save the config to start the channel.",
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        var btn = new Button
        {
            Content = "Open Config page",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        btn.Click += (_, _) =>
        {
            ((IAppCommands)CurrentApp).Navigate("config");
        };
        stack.Children.Add(btn);
        return stack;
    }

    /// <summary>
    /// "Install plugin on your gateway" section. The gateway doesn't expose
    /// plugin install over the operator wire (verified against
    /// <c>openclaw/src/gateway/server-methods-list.ts</c> — only
    /// <c>channels.{status,start,stop,logout}</c> and <c>config.*</c> are
    /// available), so this section shows the exact CLI command the user
    /// needs to run on the gateway host, plus a one-click Copy button and
    /// a docs link.
    ///
    /// Renders as a flat section (no nested Expander) — the channel card
    /// is itself the Expander, and stacking an Expander inside an Expander
    /// is the "open a card to open another card" anti-pattern. Callers
    /// already gate the call: <see cref="BuildBody"/> only invokes this
    /// when <c>!record.IsRunning</c>.
    /// </summary>
    private FrameworkElement BuildInstallPluginPanel(ChannelRecord record)
    {
        var pkg = $"@openclaw/{record.Id}";
        var cmd = $"openclaw plugins install {pkg}";

        var body = new StackPanel { Spacing = 10 };

        body.Children.Add(new TextBlock
        {
            Text = $"Channel plugins are loaded by the gateway, not the tray — so installs happen on the machine that hosts your gateway. If {record.Id} isn't coming up after Save, the plugin may not be installed yet.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        // Read-only command row: TextBox with the command, Copy button next to it.
        var cmdRow = new Grid();
        cmdRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cmdRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cmdBox = new TextBox
        {
            Text = cmd,
            IsReadOnly = true,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas, Cascadia Mono, monospace"),
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(cmdBox, 0);
        cmdRow.Children.Add(cmdBox);

        var copyBtn = new Button { Content = "Copy" };
        copyBtn.Click += (_, _) =>
        {
            try
            {
                var pkgData = new DataPackage();
                pkgData.SetText(cmd);
                Clipboard.SetContent(pkgData);
                // Briefly indicate copied — flip the label, restore after a short delay.
                copyBtn.Content = "Copied";
                _ = Task.Delay(1200).ContinueWith(_ =>
                {
                    if (DispatcherQueue == null) return;
                    DispatcherQueue.TryEnqueue(() => copyBtn.Content = "Copy");
                }, TaskScheduler.Default);
            }
            catch
            {
                copyBtn.Content = "Copy failed";
            }
        };
        Grid.SetColumn(copyBtn, 1);
        cmdRow.Children.Add(copyBtn);

        body.Children.Add(cmdRow);

        body.Children.Add(new TextBlock
        {
            Text = "Run this command on the machine that hosts your gateway. After it finishes, come back and click Refresh.",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });

        var docsLink = new HyperlinkButton
        {
            Content = "Plugin install docs →",
            NavigateUri = new Uri("https://docs.openclaw.ai/plugins/install"),
            Padding = new Thickness(0),
        };
        body.Children.Add(docsLink);

        return BuildSection("Install plugin on your gateway", body);
    }

    private async Task LogoutAsync(string channelId, bool isQr)
    {
        var client = CurrentApp.GatewayClient;
        if (client == null)
        {
            ErrorBar.Title = "Not connected";
            ErrorBar.Message = isQr
                ? "Connect to a gateway before unlinking this device."
                : "Connect to a gateway before disconnecting this channel.";
            SetInfoBarOpen(ErrorBar, true);
            return;
        }
        // QR channels: Logout = unlink the device (lightweight — re-scan a
        // new QR to reconnect, your account stays paired on your phone).
        // Non-QR channels: Logout = forget stored credentials (destructive —
        // you'll need to re-enter the bot token / webhook URL to reconnect).
        var dialog = new ContentDialog
        {
            Title = isQr
                ? $"Unlink {channelId} from this device?"
                : $"Disconnect {channelId} and forget credentials?",
            Content = isQr
                ? "This unlinks the channel from this device. Your account stays paired on your phone — scan a fresh QR to reconnect."
                : "This signs out the channel and clears the stored credentials. You'll need to re-enter them to reconnect.",
            PrimaryButtonText = isQr ? "Unlink" : "Disconnect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // A ContentDialog is already on screen (double-click race). Drop
            // this invocation silently — the first dialog will drive the action.
            return;
        }
        if (result != ContentDialogResult.Primary) return;

        var ok = await client.LogoutChannelAsync(channelId);
        if (!ok)
        {
            ErrorBar.Title = isQr ? "Unlink failed" : "Disconnect failed";
            ErrorBar.Message = isQr
                ? $"Could not unlink {channelId}. The gateway may not support this action."
                : $"Could not disconnect {channelId}. The gateway may not support this action.";
            SetInfoBarOpen(ErrorBar, true);
        }
        await RefreshAsync(probe: false);
    }

    /// <summary>
    /// Pause a running channel via <c>channels.stop</c>. Lightweight
    /// counterpart to <see cref="LogoutAsync"/> — keeps credentials in
    /// the gateway config so the channel can be started again later
    /// without re-entering the bot token / webhook URL / etc. Wired to
    /// the "Stop" button in the body CONTROLS section on non-QR running
    /// channels (Telegram, Discord, Slack, GoogleChat, Nostr); for
    /// WhatsApp/Signal the analogous action is Logout (unlinks the device).
    /// </summary>
    private async Task StopChannelAsync(string channelId)
    {
        // Action-flow banner — Render must not rewrite the title to "is running"
        // or clear form drafts (see _pendingBannerIsAction docs).
        _pendingBannerIsAction = true;

        var client = CurrentApp.GatewayClient;
        if (client == null)
        {
            _pendingSaveBanner = (channelId, "Not connected",
                "Connect to a gateway before stopping channels.",
                InfoBarSeverity.Error);
            ApplyPendingSaveBanner();
            return;
        }

        _pendingSaveBanner = (channelId, $"Stopping {channelId}…",
            "Asking the gateway to pause this channel. Credentials are kept so you can start it again.",
            InfoBarSeverity.Informational);
        ApplyPendingSaveBanner();

        bool ok;
        try
        {
            ok = await client.StopChannelAsync(channelId);
        }
        catch (Exception ex)
        {
            // Don't let the network exception become an unobserved task fault —
            // the user clicked Stop, they deserve to see why it didn't work.
            _pendingSaveBanner = (channelId, $"Couldn't stop {channelId}",
                $"Lost connection to the gateway while stopping ({ex.GetType().Name}). Try Refresh.",
                InfoBarSeverity.Error);
            ApplyPendingSaveBanner();
            return;
        }

        if (!ok)
        {
            _pendingSaveBanner = (channelId, $"Couldn't stop {channelId}",
                "The gateway didn't accept channels.stop. Try Refresh or check the gateway log.",
                InfoBarSeverity.Error);
            ApplyPendingSaveBanner();
            return;
        }

        _pendingSaveBanner = (channelId, $"{channelId} stopped",
            "Channel paused. Press Start to resume — credentials are kept.",
            InfoBarSeverity.Success);
        ApplyPendingSaveBanner();
        await RefreshAsync(probe: false);
    }

    /// <summary>
    /// Try to start a channel via <c>channels.start</c>. Used as the recovery
    /// affordance when a channel is configured but didn't come up on its own
    /// (e.g., after a save that didn't auto-start, or after a gateway restart).
    /// Surfaces gateway errors on the page-level <c>SaveBanner</c> — including
    /// the telltale "unknown channel" which means the channel's plugin isn't
    /// loaded on the gateway host and the user needs to install it via the
    /// gateway-host CLI.
    /// </summary>
    private async Task StartChannelAsync(string channelId)
    {
        // Action-flow banner (see _pendingBannerIsAction docs).
        _pendingBannerIsAction = true;

        var client = CurrentApp.GatewayClient;
        if (client == null)
        {
            _pendingSaveBanner = (channelId, "Not connected",
                "Connect to a gateway before starting channels.",
                InfoBarSeverity.Error);
            ApplyPendingSaveBanner();
            return;
        }

        _pendingSaveBanner = (channelId, $"Starting {channelId}…",
            "Asking the gateway to start this channel.",
            InfoBarSeverity.Informational);
        ApplyPendingSaveBanner();

        var result = await client.StartChannelDetailedAsync(channelId);
        if (result == null || !result.Ok)
        {
            _pendingSaveBanner = (channelId, $"Couldn't start {channelId}",
                result?.Error ?? "The gateway didn't respond.",
                InfoBarSeverity.Error);
            ApplyPendingSaveBanner();
            return;
        }

        if (result.LooksLikeMissingPlugin)
        {
            // Strong signal the plugin isn't loaded on the gateway host.
            _pendingSaveBanner = (channelId,
                $"Gateway doesn't have the {channelId} plugin installed",
                $"Install it on your gateway host: openclaw plugins install @openclaw/{channelId}. See the channel's expander for the copyable command.",
                InfoBarSeverity.Warning);
            ApplyPendingSaveBanner();
            return;
        }

        // Re-fetch the snapshot so Render() can upgrade the banner to "running"
        // if the channel transitioned. If it didn't, Render() leaves a
        // "started but not running yet" warning.
        _pendingSaveBanner = (channelId, $"{channelId} start requested",
            "Waiting for the gateway to confirm the channel is running…",
            InfoBarSeverity.Success);
        ApplyPendingSaveBanner();
        await RefreshAsync(probe: true);
    }

    // ─── Header state resolution (dot color, badge, subtitle) ─────────────

    private static (string DotBrushKey, string BadgeText, BadgeSeverity Severity, IReadOnlyList<string> Subtitles) ResolveHeaderState(ChannelRecord record)
    {
        var raw = record.RawStatus;

        // Status badge text follows naming.md status vocabulary: lowercase short
        // forms (connected / connecting / disconnected / reconnecting, etc.).
        // Stays consistent with the tray/Mission-Control status lines.

        IReadOnlyList<string> Single(string s) => new[] { s };

        // Unavailable on this OS short-circuit
        if (record.IsUnavailableOnWindows)
            return ("SystemFillColorNeutralBrush", "unavailable on Windows", BadgeSeverity.Neutral, Single(ResolveTagline(record.Id)));

        if (raw.ValueKind != JsonValueKind.Object)
        {
            return record.IsConfigured
                ? ("SystemFillColorSuccessBrush", "configured", BadgeSeverity.Success, Array.Empty<string>())
                : ("SystemFillColorNeutralBrush", "not configured", BadgeSeverity.Neutral, Single(ResolveTagline(record.Id)));
        }

        var running = GetBool(raw, "running");
        var connected = GetBool(raw, "connected");
        var linked = GetBool(raw, "linked");
        var configured = GetBool(raw, "configured");
        var lastError = GetString(raw, "lastError") ?? GetString(raw, "error");

        // Error path
        if (!string.IsNullOrEmpty(lastError) && running != true)
            return ("SystemFillColorCriticalBrush", "error", BadgeSeverity.Critical, BuildErrorSubtitleLines(raw, lastError!));

        // WhatsApp-style flow: linked/connected
        if (record.Capabilities.HasFlag(ChannelCapabilities.CanShowQr))
        {
            if (configured == false) return ("SystemFillColorNeutralBrush", "not configured", BadgeSeverity.Neutral, Single(ResolveTagline(record.Id)));
            if (linked == false) return ("SystemFillColorCriticalBrush", "not linked", BadgeSeverity.Critical, Single("Scan a QR to link this device"));
            if (connected == true) return ("SystemFillColorSuccessBrush", "connected", BadgeSeverity.Success, BuildWhatsAppSubtitleLines(raw));
            if (running == true) return ("SystemFillColorCautionBrush", "running", BadgeSeverity.Caution, BuildWhatsAppSubtitleLines(raw));
            return ("SystemFillColorCautionBrush", "linked", BadgeSeverity.Caution, BuildWhatsAppSubtitleLines(raw));
        }

        // Generic flow
        if (running == true)
        {
            return ("SystemFillColorSuccessBrush", "running", BadgeSeverity.Success, BuildGenericSubtitleLines(record, raw));
        }
        if (configured == true)
            return ("SystemFillColorCautionBrush", "configured", BadgeSeverity.Caution, BuildGenericSubtitleLines(record, raw));

        return ("SystemFillColorNeutralBrush", "not configured", BadgeSeverity.Neutral, Single(ResolveTagline(record.Id)));
    }

    private static IReadOnlyList<string> BuildErrorSubtitleLines(JsonElement raw, string lastError)
    {
        // Two-line layout for error cards: error message on top, last-probe
        // recency below. Splitting these visually highlights the failure
        // without burying it in a comma-separated list.
        var lines = new List<string> { Truncate(lastError, 80) };
        if (raw.TryGetProperty("lastProbeAt", out var ts) && ts.ValueKind == JsonValueKind.Number && ts.TryGetDouble(out var d))
            lines.Add("Last probe " + FormatRelative(d));
        return lines;
    }

    private static IReadOnlyList<string> BuildWhatsAppSubtitleLines(JsonElement raw)
    {
        // Line 1: identity (phone / JID). Line 2: activity (auth age,
        // last message). Single-account WhatsApp connection is the common
        // case so we don't bother surfacing account count.
        var lines = new List<string>();

        if (raw.TryGetProperty("self", out var self) && self.ValueKind == JsonValueKind.Object)
        {
            var e164 = GetString(self, "e164") ?? GetString(self, "jid");
            if (!string.IsNullOrEmpty(e164)) lines.Add("Linked as " + e164);
        }

        var activityParts = new List<string>();
        if (raw.TryGetProperty("authAgeMs", out var age) && age.ValueKind == JsonValueKind.Number && age.TryGetDouble(out var ageD))
            activityParts.Add("Auth age " + FormatAge(ageD));
        if (raw.TryGetProperty("lastMessageAt", out var lm) && lm.ValueKind == JsonValueKind.Number && lm.TryGetDouble(out var lmd))
            activityParts.Add("Last message " + FormatRelative(lmd));
        if (activityParts.Count > 0) lines.Add(string.Join(" · ", activityParts));

        return lines;
    }

    private static IReadOnlyList<string> BuildGenericSubtitleLines(ChannelRecord record, JsonElement raw)
    {
        // Multi-line activity card for configured channels:
        //   Line 1: identity / mode / account count
        //   Line 2: activity timestamps + probe time
        //   Line 3: caution (reconnect attempts > 0)
        // Splitting across lines makes the card visibly bigger than an
        // unconfigured preview row, which keeps a single-line tagline.
        var lines = new List<string>();
        var generic = ChannelsStatusParser.ExtractGeneric(raw);

        // Line 1: identity / mode summary.
        var line1Parts = new List<string>();
        if (GetString(raw, "botUsername") is { Length: > 0 } bot) line1Parts.Add("@" + bot);
        else if (GetString(raw, "webhookUrl") is { Length: > 0 } hook) line1Parts.Add(Truncate(hook, 48));
        if (generic?.Mode is { Length: > 0 } mode)
            line1Parts.Add(char.ToUpperInvariant(mode[0]) + mode[1..] + " mode");
        if (record.Accounts.Count > 1)
            line1Parts.Add($"{record.Accounts.Count} accounts");
        if (line1Parts.Count > 0) lines.Add(string.Join(" · ", line1Parts));

        // Line 2: activity / probe.
        var line2Parts = new List<string>();
        if (generic?.LastEventAt is double lastEvent && lastEvent > 0)
            line2Parts.Add("Last event " + FormatRelative(lastEvent));
        else if (generic?.LastProbeAt is double lastProbe && lastProbe > 0)
            line2Parts.Add("Last probe " + FormatRelative(lastProbe));
        if (generic?.Probe is { Ok: true, ElapsedMs: double el })
            line2Parts.Add($"{(int)el} ms probe");
        if (line2Parts.Count > 0) lines.Add(string.Join(" · ", line2Parts));

        // Line 3: caution (reconnect attempts).
        if (generic?.ReconnectAttempts is int n && n > 0)
            lines.Add($"{n} reconnect attempt{(n == 1 ? "" : "s")}");

        if (lines.Count == 0 && record.IsConfigured)
            lines.Add("Configured");
        return lines;
    }

    /// <summary>
    /// Per-channel one-liner shown as the card subtitle when the channel
    /// isn't configured yet. Replaces the generic "Click to expand and
    /// configure" hint — each tagline tells the user up-front what the
    /// integration shape is (QR-linked phone, OAuth bot, webhook URL, …)
    /// so they can pick the right channel without expanding each one.
    /// </summary>
    private static string ResolveTagline(string channelId) => channelId.ToLowerInvariant() switch
    {
        "whatsapp"   => "Link your phone — scan a QR to connect",
        "telegram"   => "Bot integration — paste your bot token to set up",
        "discord"    => "Webhook integration — paste a Discord webhook URL",
        "googlechat" => "Webhook integration — paste a Google Chat webhook",
        "slack"      => "OAuth app — install a Slack app to connect",
        "signal"     => "Link your phone — scan a QR to connect",
        "imessage"   => "macOS-only — requires running the gateway on a Mac",
        "nostr"      => "Decentralized — paste your nsec and relays",
        _            => "Plugin channel — expand to configure",
    };

    // ─── Tiny utility helpers ─────────────────────────────────────────────

    private static FrameworkElement BuildInfoText(string text)
    {
        return new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
    }

    private static string FormatAge(double ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays} d";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours} h";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes} m";
        return $"{(int)ts.TotalSeconds} s";
    }

    private static string FormatRelative(double epochMs)
    {
        var when = DateTimeOffset.FromUnixTimeMilliseconds((long)epochMs);
        var diff = DateTimeOffset.UtcNow - when;
        if (diff.TotalSeconds < 0) return "just now";
        if (diff.TotalMinutes < 1) return $"{(int)diff.TotalSeconds} s ago";
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes} m ago";
        if (diff.TotalDays < 1) return $"{(int)diff.TotalHours} h ago";
        return $"{(int)diff.TotalDays} d ago";
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static string? GetString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? GetBool(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private enum BadgeSeverity { Neutral, Success, Caution, Critical }
}
