using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

// ════════════════════════════════════════════════════════════════════════════
// ConnectionPage - Lobby & Cockpit & Recovery
// ────────────────────────────────────────────────────────────────────────────
// Visual policy:
//   • Page mode (Lobby / Cockpit / Recovery / Add) computed by
//     ConnectionPagePlan.Build(...) (pure projection).
//   • Every status surface uses a 4-px colored LEFT border - accent brush
//     resolved via ConnectionPagePlan.AccentToBrushKey.
//   • This code-behind never mutates connection state directly; every
//     connection-touching action goes through GatewayConnectionManager,
//     GatewayRegistry, SettingsManager (untouched here).
// ════════════════════════════════════════════════════════════════════════════
public sealed partial class ConnectionPage : Page
{
    // ─── DI / services ───
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current!;
    private AppState? _appState;
    private IGatewayConnectionManager? _connectionManager;
    private GatewayRegistry? _gatewayRegistry;
    private GatewayDiscoveryService? _discoveryService;
    private global::Windows.UI.ViewManagement.AccessibilitySettings? _accessibilitySettings;
    private IGatewayTerminalLauncher? _terminalLauncher;
    private WslGatewayController? _wslGatewayController;

    // ─── UI state ───
    private UserIntent _userIntent = UserIntent.None;
    private GatewayConnectionSnapshot _lastSnapshot = GatewayConnectionSnapshot.Idle;
    private bool _suppressNodeModeToggle;
    private bool _suppressConnectionToggle;
    private ConnectionPagePlan _currentPlan = new();
    private GatewayHostAccessPlan _activeHostAccessPlan = GatewayHostAccessPlan.None();
    private bool _gatewayHostActionInProgress;
    private CancellationTokenSource? _gatewayHostActionCts;
    private string? _gatewayHostStatusGatewayId;
    // Tracks which gateway record the Add Gateway form is currently editing
    // (set by OnSavedRowEdit / OnEditTunnelSettings; null = creating a brand
    // new record). Used by DoDirectConnectFromAddFormAsync so a URL change
    // updates the original record instead of orphaning it as a duplicate.
    private string? _editingGatewayId;

    // Last gateway URL decoded from a pasted setup code, used to drive the
    // transport-security advice for the Setup-code method. Null when no valid
    // code is decoded.
    private string? _lastDecodedSetupUrl;
    // ─── Reconnect-mask state ───
    // Toggling Node mode forces the connection manager to tear down the
    // WS and rebuild it (so the gateway sees the role change). That brief
    // Disconnecting → Disconnected → Connecting → Connected transition was
    // showing through every visual surface (strip headline, operator card,
    // active-row badge) as a "you're disconnected!" flicker, which is wrong
    // - the user's *intent* is still to be connected, the connection layer
    // is just round-tripping. We mask the gateway/operator visuals during
    // this window and let them resume once the snapshot stabilizes again.
    private GatewayConnectionSnapshot? _lastStableSnapshot;
    private DateTime _suppressReconnectVisualsUntilUtc = DateTime.MinValue;
    private Microsoft.UI.Xaml.DispatcherTimer? _reconnectMaskTimer;
    // Set true by the snapshot-event path the moment we observe a transient
    // state while the mask is armed; consumed by the stable-state branch to
    // know "the reconnect actually happened, drop the mask early". Without
    // this flag, RefreshFromSnapshot(_lastSnapshot) called immediately after
    // BeginReconnectMask() would disarm the mask before any transient
    // snapshot ever arrives, so the flicker fix never engaged.
    private bool _maskHasObservedTransient;

    // ─── Fingerprint caches ───
    // Rebuilding rows/chips on every snapshot tick causes visible churn.
    // Fingerprints let us update only when the rendered inputs change.
    private string? _savedGatewaysFingerprint;
    private string? _glanceChipsFingerprint;
    private string? _capabilityPillsFingerprint;

    public ConnectionPage()
    {
        InitializeComponent();
    }

    private IGatewayTerminalLauncher TerminalLauncher =>
        _terminalLauncher ??= new GatewayTerminalLauncher(new OpenClawTray.AppLogger());

    private WslGatewayController WslGatewayController =>
        _wslGatewayController ??= new WslGatewayController(
            new WslExeCommandRunner(new OpenClawTray.AppLogger()),
            new OpenClawTray.AppLogger());

    // ─── Initialization ───────────────────────────────────────────────

    public void Initialize()
    {
        _appState = ((App)Application.Current!).AppState!;
        _appState.PropertyChanged += OnAppStateChanged;
        _connectionManager = CurrentApp.ConnectionManager;
        _gatewayRegistry = CurrentApp.Registry;
        var settings = CurrentApp.Settings;
        if (settings == null) return;

        // Local-WSL install entry points are only useful until setup has
        // created its managed local WSL gateway record.
        //   • WelcomeLocalWslSetupCard - get-started CTA on the empty-state
        //     Welcome screen for first-run users.
        //   • AddLocalWslItem - third method tab inside the Add Gateway
        //     form, alongside Direct and Setup code.
        UpdateLocalWslSetupVisibility();

        if (_connectionManager != null)
            _connectionManager.StateChanged += OnManagerStateChanged;
        if (_gatewayRegistry != null)
            _gatewayRegistry.Changed += OnRegistryChanged;

        ActualThemeChanged += OnPageActualThemeChanged;
        TrySubscribeAccessibilitySettings();
        Unloaded += OnPageUnloaded;

        // Initialize Node mode toggle from settings (suppressed event)
        _suppressNodeModeToggle = true;
        NodeModeToggle.IsOn = settings.EnableNodeMode;
        _suppressNodeModeToggle = false;

        // RefreshFromSnapshot below already calls LoadSavedGateways with the
        // up-to-date snapshot - calling it standalone here would render the
        // active row as "disconnected" for one frame before the snapshot pass
        // flips it to "Connected".
        RefreshFromSnapshot(_connectionManager?.CurrentSnapshot ?? GatewayConnectionSnapshot.Idle);

        // Eagerly refresh pending-pairing lists so the banner reflects truth
        // the moment the user navigates here (rather than waiting for the
        // gateway to push the next node.pair.requested broadcast). Both calls
        // are tracked + idempotent on the gateway side.
        if (CurrentApp.GatewayClient is { IsConnectedToGateway: true } client)
        {
            _ = Task.Run(async () =>
            {
                try { await client.RequestNodePairListAsync(); }
                catch (Exception ex) { Services.Logger.Warn($"[ConnectionPage] Eager node-pair refresh failed: {ex.Message}"); }
                try { await client.RequestDevicePairListAsync(); }
                catch (Exception ex) { Services.Logger.Warn($"[ConnectionPage] Eager device-pair refresh failed: {ex.Message}"); }
            });
        }

        // Push any already-resolved pending lists into the page immediately
        // - the AppState may have been populated on a prior visit and the
        // PropertyChanged subscriber only fires on future changes.
        if (_appState?.NodePairList is { } existingNode)
            UpdatePairingRequests(existingNode);
        if (_appState?.DevicePairList is { } existingDevice)
            UpdateDevicePairingRequests(existingDevice);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_connectionManager != null)
            _connectionManager.StateChanged -= OnManagerStateChanged;
        if (_gatewayRegistry != null)
            _gatewayRegistry.Changed -= OnRegistryChanged;
        ActualThemeChanged -= OnPageActualThemeChanged;
        if (_accessibilitySettings != null)
        {
            _accessibilitySettings.HighContrastChanged -= OnHighContrastChanged;
            _accessibilitySettings = null;
        }
        _discoveryService?.Dispose();
        _discoveryService = null;
        if (_reconnectMaskTimer != null)
        {
            _reconnectMaskTimer.Stop();
            _reconnectMaskTimer.Tick -= OnReconnectMaskTimeout;
            _reconnectMaskTimer = null;
        }
        _gatewayHostActionCts?.Cancel();
        if (!_gatewayHostActionInProgress)
        {
            _gatewayHostActionCts?.Dispose();
            _gatewayHostActionCts = null;
        }
        _gatewayHostActionInProgress = false;
        if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
    }

    private void OnPageActualThemeChanged(FrameworkElement sender, object args)
    {
        RefreshAfterThemeVisualChange();
    }

    private void OnHighContrastChanged(
        global::Windows.UI.ViewManagement.AccessibilitySettings sender,
        object args)
    {
        DispatcherQueue?.TryEnqueue(RefreshAfterThemeVisualChange);
    }

    private void RefreshAfterThemeVisualChange()
    {
        _capabilityPillsFingerprint = null;
        RefreshFromSnapshot(_lastSnapshot);
    }

    private void TrySubscribeAccessibilitySettings()
    {
        try
        {
            _accessibilitySettings = new global::Windows.UI.ViewManagement.AccessibilitySettings();
            _accessibilitySettings.HighContrastChanged += OnHighContrastChanged;
        }
        catch (Exception ex)
        {
            Services.Logger.Warn($"[ConnectionPage] Could not subscribe to High Contrast changes: {ex.Message}");
            _accessibilitySettings = null;
        }
    }

    private void OnManagerStateChanged(object? sender, GatewayConnectionSnapshot snapshot)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            _lastSnapshot = snapshot;
            RefreshFromSnapshot(snapshot);
        });
    }

    private void OnRegistryChanged(object? sender, GatewayRegistryChangedEventArgs e)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            UpdateLocalWslSetupVisibility();
            LoadSavedGateways();
            RefreshFromSnapshot(_lastSnapshot);
        });
    }

    private void UpdateLocalWslSetupVisibility()
    {
        var localSetupAvailable = !WslKeepAlivePolicy.HasSetupManagedLocalGateway(_gatewayRegistry?.GetAll());
        var localSetupVisibility = localSetupAvailable
            ? Visibility.Visible
            : Visibility.Collapsed;

        WelcomeLocalWslSetupCard.Visibility = localSetupVisibility;
        AddLocalWslItem.Visibility = localSetupVisibility;

        if (!localSetupAvailable && AddLocalWslItem.IsSelected)
        {
            AddDirectItem.IsSelected = true;
            ShowAddPane("direct");
        }
    }

    // ─── Plan apply ───────────────────────────────────────────────────

    private void RefreshFromSnapshot(GatewayConnectionSnapshot snapshot)
    {
        // Reconnect-mask: see field comment. While the mask window is open,
        // pretend the gateway/operator state is still at its last-stable
        // value, so the strip headline, operator card, and active-row badge
        // don't churn through Disconnecting → Disconnected → Connecting just
        // because the user toggled Node mode. The Node-related fields pass
        // through so the Node card itself still reflects reality.
        if (IsStableState(snapshot.OverallState))
        {
            _lastStableSnapshot = snapshot;
            // Only drop the mask early once the transient reconnect we were
            // waiting for has actually started AND completed. Without this
            // guard the very first refresh that happens immediately after
            // BeginReconnectMask() (still carrying the pre-toggle stable
            // snapshot) would disarm the mask before any transient state
            // arrives - defeating the entire fix.
            if (_maskHasObservedTransient)
            {
                _suppressReconnectVisualsUntilUtc = DateTime.MinValue;
                _reconnectMaskTimer?.Stop();
                _maskHasObservedTransient = false;
            }
        }
        else if (IsTransientState(snapshot.OverallState)
                 && DateTime.UtcNow < _suppressReconnectVisualsUntilUtc)
        {
            _maskHasObservedTransient = true;
        }

        bool maskActive = _lastStableSnapshot != null
                          && DateTime.UtcNow < _suppressReconnectVisualsUntilUtc
                          && IsTransientState(snapshot.OverallState);

        var effective = maskActive
            ? _lastStableSnapshot! with
            {
                NodeState = snapshot.NodeState,
                NodeError = snapshot.NodeError,
                NodePairingStatus = snapshot.NodePairingStatus,
                NodePairingRequestId = snapshot.NodePairingRequestId,
                NodeDeviceId = snapshot.NodeDeviceId,
            }
            : snapshot;

        // Cache the snapshot so downstream visuals (e.g. the connection toggle
        // in ApplyStripVisuals) read from the fresh state, not the previous
        // turn's value. Without this, the toggle defaulted to OFF on first
        // load even when the snapshot already reported Connected.
        _lastSnapshot = effective;

        var savedCount = _gatewayRegistry?.GetAll().Count ?? 0;
        var activeRecord = _gatewayRegistry?.GetActive();
        var self = CurrentApp.AppState?.GatewaySelf;
        var settings = CurrentApp.Settings;
        var localNode = NodeCapabilityGating.GetLocalNodeInfo(
            _appState?.Nodes, CurrentApp.NodeFullDeviceId);

        var plan = ConnectionPagePlan.Build(
            effective,
            activeRecord,
            self,
            settings,
            savedCount,
            userIntent: _userIntent,
            localNode: localNode);

        _currentPlan = plan;
        ApplyPlan(plan);

        // Rebuild saved-gateway rows so the active row's "Connected" badge /
        // background highlight reflects the live snapshot. Cheap - list is
        // typically < 10 entries and only re-runs on real state transitions.
        LoadSavedGateways();
    }

    private void ApplyPlan(ConnectionPagePlan plan)
    {
        // ─── Page mode visibility ───
        bool isWelcome  = plan.Mode == ConnectionPageMode.Welcome;
        bool isCockpit  = plan.Mode == ConnectionPageMode.Cockpit;
        bool isRecovery = plan.Mode == ConnectionPageMode.Recovery;
        bool isAdding   = plan.Mode == ConnectionPageMode.AddGateway;

        // Operator + Node cards are normally tied to an active operator session.
        // Local MCP-only mode has no operator session, but still needs the Node
        // card so users can see that MCP is serving local tools.
        bool hasOperatorSession = _lastSnapshot.OverallState is
            OverallConnectionState.Connected
            or OverallConnectionState.Ready
            or OverallConnectionState.Degraded
            or OverallConnectionState.Connecting
            or OverallConnectionState.PairingRequired
            or OverallConnectionState.Disconnecting;
        var hasStandaloneNodeCard = plan.NodeCard != NodeCardState.Hidden && !hasOperatorSession;
        bool showRoles = (hasOperatorSession || hasStandaloneNodeCard) && !isAdding && !isRecovery;
        CockpitPanel.Visibility = showRoles ? Visibility.Visible : Visibility.Collapsed;
        OperatorSection.Visibility = showRoles && plan.OperatorCard != OperatorCardState.Hidden
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Bottom section: exactly one of these is visible
        //   • SavedGatewaysCard  - Cockpit / Recovery (always present when registry has items)
        //   • WelcomeAddTilesCard - Welcome (empty registry)
        //   • AddGatewayPanel    - AddGateway sub-view
        SavedGatewaysCard.Visibility    = (isCockpit || isRecovery) ? Visibility.Visible : Visibility.Collapsed;
        WelcomeAddTilesCard.Visibility  = isWelcome ? Visibility.Visible : Visibility.Collapsed;
        AddGatewayPanel.Visibility      = isAdding ? Visibility.Visible : Visibility.Collapsed;

        // Recovery's help block sits above the always-visible gateways list.
        RecoveryPanel.Visibility = isRecovery ? Visibility.Visible : Visibility.Collapsed;

        // ─── Status strip ───
        ApplyStripVisuals(plan);
        ApplyGatewayHostAccess(plan);

        // ─── Cards (only meaningful when we have an operator session
        // and we're not in a focused sub-view) ───
        if (showRoles)
        {
            ApplyOperatorCard(plan);
            ApplyNodeCard(plan);
        }

        // ─── Recovery body ───
        if (isRecovery)
        {
            ApplyRecoveryBody(plan);
        }
    }

    private void ApplyStripVisuals(ConnectionPagePlan plan)
    {
        var accentBrush = ResolveBrush(ConnectionPagePlan.AccentToBrushKey(plan.StripAccent));
        // Card border stays neutral; the strip glyph carries the accent colour.

        StripGlyph.Glyph = plan.StripGlyph;
        StripGlyph.Foreground = plan.StripAccent == ConnectionAccent.Neutral
            ? ResolveBrush("TextFillColorSecondaryBrush")
            : accentBrush;

        StripProgress.IsActive = plan.StripShowProgress;
        StripProgress.Visibility = plan.StripShowProgress ? Visibility.Visible : Visibility.Collapsed;
        // Filled circle for the success "connected" state; checkmark glyph
        // would compete with the gateway-list ACTIVE badge meaning.
        bool useDot = !plan.StripShowProgress && plan.StripAccent == ConnectionAccent.Success;
        StripDot.Fill = accentBrush;
        StripDot.Visibility = useDot ? Visibility.Visible : Visibility.Collapsed;
        StripGlyph.Visibility = (plan.StripShowProgress || useDot)
            ? Visibility.Collapsed
            : Visibility.Visible;

        StripHeadline.Text = plan.StripHeadline ?? "";
        StripSub.Text = plan.StripSub ?? "";
        StripSub.Visibility = string.IsNullOrEmpty(plan.StripSub) ? Visibility.Collapsed : Visibility.Visible;

        // Primary action button - show only for actions the connection
        // toggle can't already do. The toggle covers Connect / Reconnect /
        // Retry / Cancel; CopyApproveCommand and Rep were removed because
        // their visible UI (inline Copy button next to the command, plus
        // RecoveryAuthPasteBlock for re-pair) already exposes the action
        // beneath the strip. After this clean-up, RestartTunnel is the
        // only action that actually surfaces here in practice.
        // Also suppress in AddGateway since the form has its own
        // Save & Connect CTA.
        bool suppressStripCta = plan.Mode == ConnectionPageMode.AddGateway
            || plan.StripPrimaryAction is ConnectionPrimaryAction.Connect
                                       or ConnectionPrimaryAction.Reconnect
                                       or ConnectionPrimaryAction.Retry
                                       or ConnectionPrimaryAction.Cancel;
        if (!suppressStripCta &&
            plan.StripPrimaryAction != ConnectionPrimaryAction.None &&
            plan.StripPrimaryLabel != null)
        {
            StripPrimaryButton.Content = plan.StripPrimaryLabel;
            StripPrimaryButton.Visibility = Visibility.Visible;
        }
        else
        {
            StripPrimaryButton.Visibility = Visibility.Collapsed;
        }

        // Connection toggle - visible when there's an active gateway record.
        // ON = currently connected (or attempting); tap OFF to disconnect.
        // OFF = idle/disconnecting; tap ON to reconnect.
        bool hasActive = plan.RelevantGatewayId != null;
        // "ON" = the gateway is or is trying to be connected. Error state
        // is a terminal failure - toggle should read OFF so the affordance
        // ("tap to disconnect") stops lying to the user; tapping ON re-tries.
        bool toggleOn = _lastSnapshot.OverallState is
            OverallConnectionState.Connecting
            or OverallConnectionState.Connected
            or OverallConnectionState.Ready
            or OverallConnectionState.Degraded
            or OverallConnectionState.PairingRequired;
        ConnectionToggle.Visibility = hasActive ? Visibility.Visible : Visibility.Collapsed;
        // Avoid recursive Toggled events while we sync from snapshot
        _suppressConnectionToggle = true;
        ConnectionToggle.IsOn = toggleOn;
        _suppressConnectionToggle = false;

        // Dashboard icon - top-right of the gateway header. Only when there's
        // a healthy connection (no point opening the dashboard of a broken one)
        // and we're not in the focused Add Gateway sub-view.
        bool showDashboard = plan.Mode == ConnectionPageMode.Cockpit
                          && plan.StripAccent == ConnectionAccent.Success
                          && plan.ActiveGatewayDisplayName != null;
        StripDashboardButton.Visibility = showDashboard ? Visibility.Visible : Visibility.Collapsed;

        // Glance chips (presence • channels • $today • topology) - when connected.
        ApplyGlanceChips(plan, hasActive && toggleOn);
    }

    private void ApplyGatewayHostAccess(ConnectionPagePlan plan)
    {
        var activeRecord = _gatewayRegistry?.GetActive();
        _activeHostAccessPlan = GatewayHostAccessClassifier.Classify(activeRecord);
        if (_gatewayHostStatusGatewayId is not null &&
            !string.Equals(_gatewayHostStatusGatewayId, _activeHostAccessPlan.GatewayId, StringComparison.Ordinal))
        {
            ClearGatewayHostActionStatus();
        }

        var showInPageMode = plan.Mode is ConnectionPageMode.Cockpit or ConnectionPageMode.Recovery;
        var showTerminal = showInPageMode &&
                           _activeHostAccessPlan.CanOpenTerminal &&
                           !_activeHostAccessPlan.CanControlWslGateway;
        StripTerminalButton.Visibility = showTerminal ? Visibility.Visible : Visibility.Collapsed;
        StripTerminalButton.IsEnabled = !_gatewayHostActionInProgress;
        ToolTipService.SetToolTip(StripTerminalButton, _activeHostAccessPlan.TerminalTooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            StripTerminalButton,
            _activeHostAccessPlan.TerminalLabel);

        var showWslControls = showInPageMode && _activeHostAccessPlan.CanControlWslGateway;
        GatewayHostControlsSection.Visibility = showWslControls ? Visibility.Visible : Visibility.Collapsed;
        if (!showWslControls)
        {
            ClearGatewayHostActionStatus();
            return;
        }

        GatewayHostControlsDescriptionText.Text = LocalizationHelper.Format(
            "ConnectionPage_GatewayHostControlsDescription_Format",
            _activeHostAccessPlan.DistroName);
        GatewayHostOpenTerminalButton.IsEnabled = !_gatewayHostActionInProgress && _activeHostAccessPlan.CanOpenTerminal;
        GatewayHostStartButton.IsEnabled = !_gatewayHostActionInProgress;
        GatewayHostStopButton.IsEnabled = !_gatewayHostActionInProgress;
        GatewayHostRestartButton.IsEnabled = !_gatewayHostActionInProgress;
        GatewayHostActionProgress.IsActive = _gatewayHostActionInProgress;
        GatewayHostActionProgress.Visibility = _gatewayHostActionInProgress ? Visibility.Visible : Visibility.Collapsed;
        GatewayHostActionStatusPanel.Visibility = _gatewayHostActionInProgress || !string.IsNullOrWhiteSpace(GatewayHostActionStatusText.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ToolTipService.SetToolTip(GatewayHostOpenTerminalButton, _activeHostAccessPlan.TerminalTooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            GatewayHostOpenTerminalButton,
            _activeHostAccessPlan.TerminalLabel);
    }

    private void SetGatewayHostActionStatus(string message, bool isError = false)
    {
        _gatewayHostStatusGatewayId = string.IsNullOrWhiteSpace(message) ? null : _activeHostAccessPlan.GatewayId;
        GatewayHostActionStatusText.Text = message;
        GatewayHostActionStatusText.Foreground = isError
            ? ResolveBrush("SystemFillColorCriticalBrush")
            : ResolveBrush("TextFillColorSecondaryBrush");
        GatewayHostActionStatusPanel.Visibility = string.IsNullOrWhiteSpace(message) && !_gatewayHostActionInProgress
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ClearGatewayHostActionStatus()
    {
        _gatewayHostStatusGatewayId = null;
        GatewayHostActionStatusText.Text = string.Empty;
        GatewayHostActionStatusPanel.Visibility = _gatewayHostActionInProgress
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Public entry point invoked by HubWindow when sessions / channels /
    /// usage data refresh. Cheaper than re-running the full snapshot pipeline.
    /// </summary>
    public void OnGlanceDataChanged()
    {
        // Re-run the strip/operator pass with the cached snapshot so chips
        // pick up the new data without bouncing the rest of the page. Derive
        // visibility from _lastSnapshot directly - ConnectionToggle.IsOn is
        // a downstream visual signal, not the source of truth, and during
        // any future regression where they diverge we'd render stale chips.
        if (_currentPlan == null) return;
        bool show = _currentPlan.RelevantGatewayId != null
                    && _lastSnapshot.OverallState is
                        OverallConnectionState.Connecting
                        or OverallConnectionState.Connected
                        or OverallConnectionState.Ready
                        or OverallConnectionState.Degraded
                        or OverallConnectionState.PairingRequired;
        ApplyGlanceChips(_currentPlan, show);
        ApplyOperatorCard(_currentPlan);
    }

    private void ApplyGlanceChips(ConnectionPagePlan plan, bool show)
    {
        if (!show)
        {
            const string emptyFp = "<empty>";
            if (_glanceChipsFingerprint == emptyFp) return;
            _glanceChipsFingerprint = emptyFp;
            GlanceChipsHost.ItemsSource = Array.Empty<Border>();
            return;
        }

        var self = _appState?.GatewaySelf;
        var channels = _appState?.Channels;
        var cost = _appState?.UsageCost;
        var activeRec = _gatewayRegistry?.GetActive();

        // Compute the fingerprint from the same inputs the chips render so
        // we can short-circuit when nothing observable changed. Cheaper than
        // re-templating ~5 chip Borders every snapshot tick.
        var topology = ClassifyTopology(activeRec);
        var hostname = Environment.MachineName;
        int? presence = self?.PresenceCount;
        int channelTotal = channels?.Length ?? 0;
        int channelOk = ConnectionPageChannelMetrics.CountHealthyChannels(channels);
        double todayAmount = 0d;
        if (cost?.Daily is { Count: > 0 })
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var entry = cost.Daily.FirstOrDefault(d => d.Date == today);
            todayAmount = entry?.TotalCost ?? 0d;
        }
        // Include cost-data presence (daily count) separately from the
        // amount so the chip correctly appears/disappears on $0.00 days
        // - without this the fingerprint can be identical when daily list
        // appears/empties at $0.00, leaving a stale chip state.
        int dailyCount = cost?.Daily?.Count ?? 0;
        var sharedToken = activeRec?.SharedGatewayToken;
        var hasSharedToken = !string.IsNullOrEmpty(sharedToken);
        var fp = $"{topology}|{hostname}|{presence}|{channelOk}/{channelTotal}|{dailyCount}|${todayAmount:0.00}|st={sharedToken?.Length ?? 0}";
        if (_glanceChipsFingerprint == fp) return;
        _glanceChipsFingerprint = fp;

        var chips = new List<Border>(5);

        // 1. Topology - Local / via SSH / LAN / Tailscale / Remote.
        //    Tells the user what kind of gateway they're talking to.
        if (topology != null)
        {
            chips.Add(BuildGlanceChip(Helpers.FluentIconCatalog.ServerEnvironment, topology, neutral: true));
        }

        // 2. Local hostname - "on PERSEID". Tells the user what device the
        //    tray (and therefore Operator + Node) runs on. Free signal.
        if (!string.IsNullOrWhiteSpace(hostname))
        {
            chips.Add(BuildGlanceChip(Helpers.FluentIconCatalog.Hostname, string.Format(LocalizationHelper.GetString("ConnectionPage_OnHostname"), hostname), neutral: true));
        }

        // 3. Presence count - "1 client" / "3 clients" if the gateway has
        //    multiple operators connected.
        if (presence is int n && n > 0)
        {
            var label = n == 1 ? LocalizationHelper.GetString("ConnectionPage_ClientSingular") : string.Format(LocalizationHelper.GetString("ConnectionPage_ClientsPlural"), n);
            chips.Add(BuildGlanceChip(Helpers.FluentIconCatalog.People, label, neutral: true));
        }

        // 4. Channels glance - "2/2 channels ready" when at least one channel
        //    is configured. Counts linked-and-ok channels.
        if (channelTotal > 0)
        {
            var label = channelTotal == 1
                ? string.Format(LocalizationHelper.GetString("ConnectionPage_ChannelsSingular"), channelOk, channelTotal)
                : string.Format(LocalizationHelper.GetString("ConnectionPage_ChannelsPlural"), channelOk, channelTotal);
            chips.Add(BuildGlanceChip(Helpers.FluentIconCatalog.Channels, label, neutral: channelOk == channelTotal));
        }

        // 5. Today's $ - falls back to "$0.00 today" so the chip stays in a
        //    consistent slot even on a fresh day with no usage yet.
        if (cost?.Daily is { Count: > 0 })
        {
            var label = todayAmount > 0
                ? string.Format(LocalizationHelper.GetString("ConnectionPage_CostToday"), todayAmount.ToString("0.00"))
                : LocalizationHelper.GetString("ConnectionPage_CostTodayZero");
            chips.Add(BuildGlanceChip(Helpers.FluentIconCatalog.Money, label, neutral: true));
        }

        // 6. Shared token - click to copy.
        if (hasSharedToken)
        {
            var chip = BuildGlanceChip(Helpers.FluentIconCatalog.Lock, LocalizationHelper.GetString("ConnectionPage_SharedTokenChip"), neutral: true);
            ToolTipService.SetToolTip(chip, LocalizationHelper.GetString("ConnectionPage_TapToCopySharedToken"));
            chip.Tapped += (_, _) =>
            {
                ClipboardHelper.CopyText(sharedToken!);
            };
            chips.Add(chip);
        }

        GlanceChipsHost.ItemsSource = chips;
    }

    /// <summary>
    /// Pure classifier mapping the active gateway record to a short topology
    /// label. SSH-tunneled gateways take precedence (the tunnel is the
    /// transport story regardless of the inner host). Otherwise we look at
    /// the URL host: localhost = Local; *.ts.net / 100.64.0.0/10 = Tailscale;
    /// RFC1918 / .local = LAN; everything else = Remote.
    /// </summary>
    private static string? ClassifyTopology(GatewayRecord? rec)
    {
        if (rec == null) return null;
        if (rec.SshTunnel != null) return LocalizationHelper.GetString("ConnectionPage_TopologyViaSshTunnel");
        if (string.IsNullOrEmpty(rec.Url)) return null;
        try
        {
            var uri = new Uri(rec.Url);
            var host = uri.Host;
            if (host == "localhost" || host == "127.0.0.1" || host == "::1") return LocalizationHelper.GetString("ConnectionPage_TopologyLocal");
            if (host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)) return LocalizationHelper.GetString("ConnectionPage_TopologyTailscale");
            if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return LocalizationHelper.GetString("ConnectionPage_TopologyLAN");
            // RFC1918 private ranges
            if (host.StartsWith("10.") || host.StartsWith("192.168.") ||
                (host.StartsWith("172.") && IsPrivate172(host)))
                return LocalizationHelper.GetString("ConnectionPage_TopologyLAN");
            // Tailnet CGNAT range 100.64.0.0/10
            if (host.StartsWith("100."))
            {
                var parts = host.Split('.');
                if (parts.Length >= 2 && int.TryParse(parts[1], out var second) &&
                    second >= 64 && second <= 127)
                    return LocalizationHelper.GetString("ConnectionPage_TopologyTailscale");
            }
            return LocalizationHelper.GetString("ConnectionPage_TopologyRemote");
        }
        catch (Exception ex)
        {
            Services.Logger.Debug($"[ConnectionPage] ClassifyTopology failed for url '{rec.Url}': {ex.Message}");
            return null;
        }
    }

    private static bool IsPrivate172(string host)
    {
        var parts = host.Split('.');
        if (parts.Length < 2) return false;
        return int.TryParse(parts[1], out var second) && second >= 16 && second <= 31;
    }

    private Border BuildGlanceChip(string glyph, string label, bool neutral)
    {
        var fgKey = neutral ? "TextFillColorSecondaryBrush" : "SystemFillColorCautionBrush";
        var bgKey = neutral ? "SubtleFillColorSecondaryBrush" : "SystemFillColorCautionBackgroundBrush";
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        stack.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 11,
            Foreground = ResolveBrush(fgKey),
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = ResolveBrush(fgKey),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Background = ResolveBrush(bgKey),
            Child = stack,
        };
    }

    private void ApplyOperatorCard(ConnectionPagePlan plan)
    {
        // No prose body - the card's identity ("Operator") + the active
        // sessions caption + the deep links carry meaning. Dim transient states.
        bool linksEnabled = plan.OperatorCard == OperatorCardState.Active
                         || plan.OperatorCard == OperatorCardState.Idle;
        OperatorSessionsLink.IsEnabled = linksEnabled;
        OperatorInstancesLink.IsEnabled = linksEnabled;
        OperatorSection.Opacity = linksEnabled ? 1.0 : 0.65;

        // Status sub-row (mirrors PermissionsPage NodeStatusDot pattern):
        // colored dot + descriptive label that reflects the live state.
        var sessions = _appState?.Sessions;
        int activeSessions = sessions?.Count(s =>
            string.Equals(s.Status, "active", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Status, "running", StringComparison.OrdinalIgnoreCase)) ?? 0;

        var (statusGlyph, statusBrushKey, statusText) = plan.OperatorCard switch
        {
            OperatorCardState.Active when activeSessions > 0 => (
                Helpers.FluentIconCatalog.StatusOk,
                "SystemFillColorSuccessBrush",
                activeSessions == 1
                    ? LocalizationHelper.GetString("ConnectionPage_OperatorActiveOneSession")
                    : string.Format(LocalizationHelper.GetString("ConnectionPage_OperatorActiveSessions"), activeSessions)),
            OperatorCardState.Active => (
                Helpers.FluentIconCatalog.StatusOk,
                "SystemFillColorSuccessBrush",
                LocalizationHelper.GetString("ConnectionPage_OperatorActiveNoSessions")),
            OperatorCardState.Idle => (
                Helpers.FluentIconCatalog.CapabilityOff,
                "SystemFillColorNeutralBrush",
                LocalizationHelper.GetString("ConnectionPage_OperatorDisconnected")),
            OperatorCardState.Connecting => (
                Helpers.FluentIconCatalog.Sync,
                "SystemFillColorCautionBrush",
                LocalizationHelper.GetString("ConnectionPage_OperatorConnecting")),
            OperatorCardState.Paused => (
                Helpers.FluentIconCatalog.Sync,
                "SystemFillColorCautionBrush",
                LocalizationHelper.GetString("ConnectionPage_OperatorReconnecting")),
            _ => (Helpers.FluentIconCatalog.CapabilityOff, "SystemFillColorNeutralBrush", LocalizationHelper.GetString("ConnectionPage_OperatorDisconnected")),
        };
        OperatorStatusIcon.Glyph = statusGlyph;
        OperatorStatusIcon.Foreground = ResolveBrush(statusBrushKey);
        OperatorStatusText.Text = statusText;
    }

    private void ApplyNodeCard(ConnectionPagePlan plan)
    {
        // Hide the entire Node card border when the projection says Hidden
        // (e.g. during Connecting before the node role even comes online).
        if (plan.NodeCard == NodeCardState.Hidden)
        {
            NodeCardBorder.Visibility = Visibility.Collapsed;
            return;
        }
        NodeCardBorder.Visibility = Visibility.Visible;

        var settings = CurrentApp.Settings;

        var capCount = plan.NodeEffectiveCapabilities.Count;

        // Body text (warning/error detail under the status text) only surfaces
        // for warning/error/pairing states.
        bool showBody = plan.NodeCard is NodeCardState.OnPermissionsIncomplete
                                       or NodeCardState.OnNodeApprovalRequired
                                       or NodeCardState.OnNodeReapprovalRequired
                                       or NodeCardState.OnNodePairingRequired
                                       or NodeCardState.OnNodeRejected
                                       or NodeCardState.OnNodeRateLimited
                                       or NodeCardState.OnNodeError;
        var bodyText = plan.NodeCard switch
        {
            NodeCardState.OnPermissionsIncomplete => LocalizationHelper.GetString("ConnectionPage_NodeBodyNoCapabilities"),
            NodeCardState.OnNodeApprovalRequired  => LocalizationHelper.GetString("ConnectionPage_NodeBodyApprovalRequired"),
            NodeCardState.OnNodeReapprovalRequired => LocalizationHelper.GetString("ConnectionPage_NodeBodyReapprovalRequired"),
            NodeCardState.OnNodePairingRequired   => LocalizationHelper.GetString("ConnectionPage_NodeBodyAwaitingApproval"),
            NodeCardState.OnNodeRejected          => LocalizationHelper.GetString("ConnectionPage_NodeBodyPairingRejected"),
            NodeCardState.OnNodeRateLimited       => LocalizationHelper.GetString("ConnectionPage_NodeBodyRateLimited"),
            NodeCardState.OnNodeError             => plan.NodeErrorDetail ?? LocalizationHelper.GetString("ConnectionPage_NodeBodyError"),
            _ => "",
        };
        var bodyBrushKey = plan.NodeCard switch
        {
            NodeCardState.OnNodeRejected or NodeCardState.OnNodeError => "SystemFillColorCriticalBrush",
            _ => "SystemFillColorCautionBrush",
        };
        NodeBodyText.Text = bodyText;
        NodeBodyText.Foreground = ResolveBrush(bodyBrushKey);
        NodeBodyText.Visibility = showBody ? Visibility.Visible : Visibility.Collapsed;

        // Status sub-row: dot color + status label, mirrors PermissionsPage.
        var (nodeGlyph, nodeBrushKey, nodeStatusText) = plan.NodeCard switch
        {
            NodeCardState.OnHealthy => (
                Helpers.FluentIconCatalog.StatusOk,
                "SystemFillColorSuccessBrush",
                capCount == 1 ? LocalizationHelper.GetString("ConnectionPage_NodeActiveOneCapability") : string.Format(LocalizationHelper.GetString("ConnectionPage_NodeActiveCapabilities"), capCount)),
            NodeCardState.OnNodeConnecting => (
                Helpers.FluentIconCatalog.Sync,
                "SystemFillColorCautionBrush",
                LocalizationHelper.GetString("ConnectionPage_NodeStarting")),
            NodeCardState.OffMcpOnly => (
                Helpers.FluentIconCatalog.Terminal,
                "SystemFillColorAttentionBrush",
                LocalizationHelper.GetString("ConnectionPage_NodeMcpOnly")),
            NodeCardState.OnPermissionsIncomplete => (
                Helpers.FluentIconCatalog.StatusWarn,
                "SystemFillColorCautionBrush",
                LocalizationHelper.GetString("ConnectionPage_NodeActiveNoCapabilities")),
            NodeCardState.OnNodeApprovalRequired => (
                Helpers.FluentIconCatalog.Lock,
                "SystemFillColorCautionBrush",
                LocalizationHelper.GetString("ConnectionPage_NodeApprovalRequired")),
            NodeCardState.OnNodeReapprovalRequired => (
                Helpers.FluentIconCatalog.Lock,
                "SystemFillColorCautionBrush",
                LocalizationHelper.GetString("ConnectionPage_NodeReapprovalRequired")),
            NodeCardState.OnNodePairingRequired => (
                Helpers.FluentIconCatalog.Lock,
                "SystemFillColorCautionBrush",
                LocalizationHelper.GetString("ConnectionPage_NodeAwaitingPairing")),
            NodeCardState.OnNodeRejected => (
                Helpers.FluentIconCatalog.StatusErr,
                "SystemFillColorCriticalBrush",
                LocalizationHelper.GetString("ConnectionPage_NodePairingRejected")),
            NodeCardState.OnNodeRateLimited => (
                Helpers.FluentIconCatalog.StatusWarn,
                "SystemFillColorCautionBrush",
                LocalizationHelper.GetString("ConnectionPage_NodeRateLimited")),
            NodeCardState.OnNodeError => (
                Helpers.FluentIconCatalog.StatusErr,
                "SystemFillColorCriticalBrush",
                LocalizationHelper.GetString("ConnectionPage_NodeError")),
            NodeCardState.Off => (
                Helpers.FluentIconCatalog.CapabilityOff,
                "SystemFillColorCriticalBrush",
                LocalizationHelper.GetString("ConnectionPage_NodeModeDisabledText")),
            _ => (Helpers.FluentIconCatalog.CapabilityOff, "SystemFillColorCriticalBrush", LocalizationHelper.GetString("ConnectionPage_NodeModeDisabledText")),
        };
        NodeStatusIcon.Glyph = nodeGlyph;
        NodeStatusIcon.Foreground = ResolveBrush(nodeBrushKey);
        NodeStatusText.Text = nodeStatusText;
        // When Node mode is off, also tint the status text red as a subtle
        // hint that this PC isn't sharing capabilities. Other states keep
        // the default Body foreground so they read like normal status copy.
        NodeStatusText.Foreground = plan.NodeCard == NodeCardState.Off
            ? ResolveBrush("SystemFillColorCriticalBrush")
            : ResolveBrush("TextFillColorPrimaryBrush");

        if (plan.NodeCard == NodeCardState.OffMcpOnly)
        {
            NodeCapabilityText.Visibility = Visibility.Visible;
            NodeCapabilityText.Text = LocalizationHelper.Format(
                "ConnectionPage_NodeMcpOnlyReachable", NodeService.McpServerUrl);
            NodeCapabilityPillsHost.Visibility = Visibility.Collapsed;
            NodeTechnicalDetailsExpander.Visibility = Visibility.Collapsed;
            NodePendingDeclarationsPanel.Visibility = Visibility.Collapsed;

            var mcpError = CurrentApp.ActiveNodeService?.McpStartupError;
            if (!string.IsNullOrEmpty(mcpError))
            {
                NodeStatusIcon.Glyph = Helpers.FluentIconCatalog.StatusErr;
                NodeStatusIcon.Foreground = ResolveBrush("SystemFillColorCriticalBrush");
                NodeStatusText.Text = LocalizationHelper.GetString("ConnectionPage_NodeMcpError");
                NodeStatusText.Foreground = ResolveBrush("SystemFillColorCriticalBrush");
                NodeCapabilityText.Visibility = Visibility.Collapsed;
                NodeBodyText.Text = mcpError;
                NodeBodyText.Foreground = ResolveBrush("SystemFillColorCriticalBrush");
                NodeBodyText.Visibility = Visibility.Visible;
            }
        }
        else
        {
            // Pending declarations are visible for approval context but never
            // counted as the active node contract.
            bool showSurfaces = settings != null && plan.NodeCard != NodeCardState.Off
                                                 && plan.NodeCard != NodeCardState.Hidden
                                                 && plan.NodeCard != NodeCardState.OnNodeConnecting;

            NodeCapabilityText.Visibility = Visibility.Collapsed;

            if (showSurfaces && settings is not null)
            {
                var pillFp = BuildCapabilityPillFingerprint(
                    plan.NodeCard,
                    plan.NodeEffectiveCapabilities,
                    plan.NodePendingDeclaredCapabilities,
                    settings);
                if (_capabilityPillsFingerprint != pillFp)
                {
                    _capabilityPillsFingerprint = pillFp;
                    NodeCapabilityPillsHost.Child = BuildCapabilityPills(
                        plan.NodeEffectiveCapabilities,
                        plan.NodePendingDeclaredCapabilities,
                        settings);
                }

                NodeCapabilityPillsHost.Visibility =
                    NodeCapabilityPillsHost.Child is WrapPanel { Children.Count: > 0 }
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }
            else
            {
                NodeCapabilityPillsHost.Visibility = Visibility.Collapsed;
                _capabilityPillsFingerprint = null;
            }

            bool hasTechnicalSurfaces = plan.NodeEffectiveCapabilities.Count > 0
                                     || plan.NodeEffectiveCommands.Count > 0
                                     || plan.NodeEffectivePermissions.Count > 0;
            NodeTechnicalDetailsExpander.Visibility =
                showSurfaces && hasTechnicalSurfaces ? Visibility.Visible : Visibility.Collapsed;
            if (NodeTechnicalDetailsExpander.Visibility == Visibility.Collapsed)
                NodeTechnicalDetailsExpander.IsExpanded = false;

            if (showSurfaces)
            {
                SetSurfaceInlines(NodeTechCapabilityText,
                    "ConnectionPage_NodeEffectiveCapabilities",
                    FormatSurfaceList(plan.NodeEffectiveCapabilities));
                SetSurfaceInlines(NodeTechCommandText,
                    "ConnectionPage_NodeEffectiveCommands",
                    FormatSurfaceList(plan.NodeEffectiveCommands));
                SetSurfaceInlines(NodeTechPermissionText,
                    "ConnectionPage_NodeEffectivePermissions",
                    FormatPermissionList(plan.NodeEffectivePermissions));
            }

            var showPendingDeclarations = showSurfaces &&
                (plan.NodeApprovalState is GatewayNodeApprovalState.PendingApproval or
                    GatewayNodeApprovalState.PendingReapproval ||
                 plan.NodePendingDeclaredCapabilities.Count > 0 ||
                 plan.NodePendingDeclaredCommands.Count > 0 ||
                 plan.NodePendingDeclaredPermissions.Count > 0);
            NodePendingDeclarationsPanel.Visibility = showPendingDeclarations
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (showPendingDeclarations)
            {
                NodePendingCapabilityText.Text = BuildNodeSurfaceListString(
                    "ConnectionPage_NodePendingDeclaredCapabilities",
                    plan.NodePendingDeclaredCapabilities);
                NodePendingCommandText.Text = BuildNodeSurfaceListString(
                    "ConnectionPage_NodePendingDeclaredCommands",
                    plan.NodePendingDeclaredCommands);
                NodePendingPermissionText.Text = BuildNodePermissionListString(
                    "ConnectionPage_NodePendingDeclaredPermissions",
                    plan.NodePendingDeclaredPermissions);
            }
        }

        // Sync toggle from current settings (suppress event)
        _suppressNodeModeToggle = true;
        if (settings != null) NodeModeToggle.IsOn = settings.EnableNodeMode;
        _suppressNodeModeToggle = false;

        // Command-trust actions are always copy-only. Exact commands approve
        // one validated request; discovery commands only list pending requests.
        // This page never auto-approves.
        if (!string.IsNullOrEmpty(plan.NodeTrustApproveCommand))
        {
            NodeTrustApproveCmdBox.Visibility = Visibility.Visible;
            NodeTrustApproveHelpText.Text = LocalizationHelper.GetString(
                plan.NodeTrustCommandApprovesRequest
                    ? "ConnectionPage_NodeTrustApprovalHelp"
                    : "ConnectionPage_NodeTrustDiscoveryHelp");
            NodeTrustApproveCmdText.Text = plan.NodeTrustApproveCommand;
        }
        else
        {
            NodeTrustApproveCmdBox.Visibility = Visibility.Collapsed;
            NodeTrustApproveCmdText.Text = "";
        }

        // Role pairing and node-list trust approval share the same explicit
        // reconnect action, but only role pairing uses the pairing command box.
        var isNodePairingRequired =
            plan.NodeCard == NodeCardState.OnNodePairingRequired &&
            plan.NodeApproveCommand != null;
        var canReconnectAfterNodeTrustApproval =
            plan.NodeTrustCommandApprovesRequest &&
            plan.NodeCard is NodeCardState.OnNodeApprovalRequired or
                NodeCardState.OnNodeReapprovalRequired;
        if (isNodePairingRequired)
        {
            NodeApproveCmdBox.Visibility = Visibility.Visible;
            NodeApproveCmdText.Text = plan.NodeApproveCommand;
        }
        else
        {
            NodeApproveCmdBox.Visibility = Visibility.Collapsed;
        }

        if (isNodePairingRequired || canReconnectAfterNodeTrustApproval)
        {
            NodeReconnectButton.Content = LocalizationHelper.GetString(
                canReconnectAfterNodeTrustApproval
                    ? "ConnectionPage_NodeReconnectAfterApproval"
                    : "ConnectionPage_Connect2.Content");
            NodeReconnectButton.Visibility = Visibility.Visible;
        }
        else
        {
            NodeReconnectButton.Visibility = Visibility.Collapsed;
        }

        // Permissions link is always visible (entry point even when sharing is off);
        // Voice and Skills deep links were removed from the simplified node card.
    }

    private void ApplyRecoveryBody(ConnectionPagePlan plan)
    {
        RecoveryBulletsPanel.Children.Clear();
        RecoveryTunnelBlock.Visibility = Visibility.Collapsed;
        RecoveryAuthPasteBlock.Visibility = Visibility.Collapsed;
        RecoveryApproveCmdBlock.Visibility = Visibility.Collapsed;
        RecoveryRepairResultText.Visibility = Visibility.Collapsed;

        RecoveryHelpHeaderText.Text = plan.Recovery switch
        {
            RecoveryCategory.Auth => LocalizationHelper.GetString("ConnectionPage_RecoveryHeaderAuth"),
            RecoveryCategory.Pairing => LocalizationHelper.GetString("ConnectionPage_RecoveryHeaderPairing"),
            RecoveryCategory.Tunnel => LocalizationHelper.GetString("ConnectionPage_RecoveryHeaderTunnel"),
            RecoveryCategory.Server => LocalizationHelper.GetString("ConnectionPage_RecoveryHeaderServer"),
            RecoveryCategory.TokenDrift => LocalizationHelper.GetString("ConnectionPage_RecoveryHeaderTokenDrift"),
            RecoveryCategory.Scope => LocalizationHelper.GetString("ConnectionPage_RecoveryHeaderScope"),
            RecoveryCategory.Tls => LocalizationHelper.GetString("ConnectionPage_RecoveryHeaderTls"),
            RecoveryCategory.RateLimited => LocalizationHelper.GetString("ConnectionPage_RecoveryHeaderRateLimited"),
            RecoveryCategory.Tailscale => "Check Tailscale access",
            _ => LocalizationHelper.GetString("ConnectionPage_RecoveryHeaderServer"),
        };

        var bullets = plan.Recovery switch
        {
            RecoveryCategory.Auth => new[]
            {
                LocalizationHelper.GetString("ConnectionPage_RecoveryAuthBullet1"),
                LocalizationHelper.GetString("ConnectionPage_RecoveryAuthBullet2"),
            },
            RecoveryCategory.Pairing => new[]
            {
                LocalizationHelper.GetString("ConnectionPage_RecoveryPairingBullet1"),
                LocalizationHelper.GetString("ConnectionPage_RecoveryPairingBullet2"),
            },
            RecoveryCategory.Tunnel => new[]
            {
                LocalizationHelper.GetString("ConnectionPage_RecoveryTunnelBullet1"),
                LocalizationHelper.GetString("ConnectionPage_RecoveryTunnelBullet2"),
            },
            RecoveryCategory.Server => new[]
            {
                LocalizationHelper.GetString("ConnectionPage_RecoveryServerBullet1"),
                LocalizationHelper.GetString("ConnectionPage_RecoveryServerBullet2"),
                LocalizationHelper.GetString("ConnectionPage_RecoveryServerBullet3"),
            },
            RecoveryCategory.TokenDrift => new[]
            {
                LocalizationHelper.GetString("ConnectionPage_RecoveryTokenDriftBullet1"),
                LocalizationHelper.GetString("ConnectionPage_RecoveryTokenDriftBullet2"),
            },
            RecoveryCategory.Scope => new[]
            {
                LocalizationHelper.GetString("ConnectionPage_RecoveryScopeBullet1"),
                LocalizationHelper.GetString("ConnectionPage_RecoveryScopeBullet2"),
            },
            RecoveryCategory.Tls => new[]
            {
                LocalizationHelper.GetString("ConnectionPage_RecoveryTlsBullet1"),
                LocalizationHelper.GetString("ConnectionPage_RecoveryTlsBullet2"),
            },
            RecoveryCategory.RateLimited => new[]
            {
                LocalizationHelper.GetString("ConnectionPage_RecoveryRateLimitedBullet1"),
                LocalizationHelper.GetString("ConnectionPage_RecoveryRateLimitedBullet2"),
            },
            RecoveryCategory.Tailscale => new[]
            {
                "Confirm Tailscale is running and signed in on this Windows PC.",
                "Confirm this PC and the generated WSL gateway belong to the same tailnet.",
                "Open the managed WSL gateway terminal as root and check tailscaled, Tailscale Serve, and the OpenClaw gateway service. Funnel is unsupported; remove any Funnel route. Companion keeps using WSS and never falls back to localhost.",
            },
            _ => new[]
            {
                LocalizationHelper.GetString("ConnectionPage_RecoveryDefaultBullet1"),
                LocalizationHelper.GetString("ConnectionPage_RecoveryDefaultBullet2"),
                LocalizationHelper.GetString("ConnectionPage_RecoveryDefaultBullet3"),
            },
        };

        foreach (var b in bullets)
            RecoveryBulletsPanel.Children.Add(BuildBulletRow(b));

        // Sub-blocks
        if (plan.Recovery == RecoveryCategory.Tunnel)
        {
            RecoveryTunnelBlock.Visibility = Visibility.Visible;
            RecoveryTunnelDetailText.Text = plan.RecoveryDetail ?? LocalizationHelper.GetString("ConnectionPage_SshTunnelIsDownText");
        }
        // Auth, token drift, and scope problems are all repaired by pasting a
        // fresh setup code (re-pair), which also upgrades scopes on the gateway.
        if (plan.Recovery is RecoveryCategory.Auth
                          or RecoveryCategory.TokenDrift
                          or RecoveryCategory.Scope)
        {
            RecoveryAuthPasteBlock.Visibility = Visibility.Visible;
        }
        if (plan.Recovery == RecoveryCategory.Pairing && plan.RecoveryApproveCommand != null)
        {
            RecoveryApproveCmdBlock.Visibility = Visibility.Visible;
            RecoveryApproveCmdText.Text = plan.RecoveryApproveCommand;
        }
    }

    private static Border BuildBulletRow(string text)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var bullet = new TextBlock
        {
            Text = "•",
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        Grid.SetColumn(bullet, 0);
        var label = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(bullet);
        grid.Children.Add(label);
        return new Border { Child = grid };
    }

    private enum CapabilityPillState { Active, Pending, Off }

    private WrapPanel BuildCapabilityPills(
        IReadOnlyList<string> effective,
        IReadOnlyList<string> pendingDeclared,
        SettingsManager settings)
    {
        var panel = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        var effectiveSet = new HashSet<string>(
            effective.Where(c => !string.IsNullOrWhiteSpace(c)),
            StringComparer.OrdinalIgnoreCase);
        var pendingSet = new HashSet<string>(
            pendingDeclared.Where(c => !string.IsNullOrWhiteSpace(c)),
            StringComparer.OrdinalIgnoreCase);
        var isHighContrast = IsHighContrastEnabled();

        var canonical = new (string Name, string LabelKey, string Glyph, bool Enabled)[]
        {
            ("browser",  "PermissionsPage_Cap_Browser_Label",  FluentIconCatalog.Browser,  settings.NodeBrowserProxyEnabled),
            ("camera",   "PermissionsPage_Cap_Camera_Label",   FluentIconCatalog.Camera,   settings.NodeCameraEnabled),
            ("canvas",   "PermissionsPage_Cap_Canvas_Label",   FluentIconCatalog.Canvas,   settings.NodeCanvasEnabled),
            ("screen",   "PermissionsPage_Cap_Screen_Label",   FluentIconCatalog.Screen,   settings.NodeScreenEnabled),
            ("location", "PermissionsPage_Cap_Location_Label", FluentIconCatalog.Location, settings.NodeLocationEnabled),
            ("tts",      "PermissionsPage_Cap_Tts_Label",      FluentIconCatalog.Voice,    settings.NodeTtsEnabled),
            ("stt",      "PermissionsPage_Cap_Stt_Label",      FluentIconCatalog.Speech,   settings.NodeSttEnabled),
        };

        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, labelKey, glyph, enabled) in canonical)
        {
            var state = effectiveSet.Contains(name)
                ? CapabilityPillState.Active
                : (pendingSet.Contains(name) || enabled)
                    ? CapabilityPillState.Pending
                    : CapabilityPillState.Off;
            panel.Children.Add(MakeCapabilityPill(LocalizationHelper.GetString(labelKey), glyph, state, isHighContrast));
            shown.Add(name);
        }

        var extras = effective.Select(c => (Name: c, State: CapabilityPillState.Active))
            .Concat(pendingDeclared.Select(c => (Name: c, State: CapabilityPillState.Pending)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !shown.Contains(x.Name));
        foreach (var (name, state) in extras)
        {
            if (!shown.Add(name)) continue;
            var (label, glyph) = name.Trim().ToLowerInvariant() switch
            {
                "device" => (LocalizationHelper.GetString("ConnectionPage_NodeCap_Device"), FluentIconCatalog.Devices),
                "system" => (LocalizationHelper.GetString("ConnectionPage_NodeCap_System"), FluentIconCatalog.System),
                _ => (HumanizeNodeToken(name), FluentIconCatalog.System),
            };
            panel.Children.Add(MakeCapabilityPill(label, glyph, state, isHighContrast));
        }

        return panel;
    }

    private const double CapabilityPillFillOpacity = 0.14;

    private Border MakeCapabilityPill(string label, string glyph, CapabilityPillState state, bool isHighContrast)
    {
        var (fillBrush, iconBrush, textBrush, stateKey, stateGlyph) = state switch
        {
            CapabilityPillState.Active => (
                TintBrush(
                    "SystemFillColorSuccessBrush",
                    "SystemFillColorSuccessBackgroundBrush",
                    CapabilityPillFillOpacity,
                    isHighContrast),
                ResolveBrush("SystemFillColorSuccessBrush"),
                ResolveBrush("SystemFillColorSuccessBrush"),
                "ConnectionPage_NodePillState_Active",
                null),
            CapabilityPillState.Pending => (
                TintBrush(
                    "SystemFillColorCautionBrush",
                    "SystemFillColorCautionBackgroundBrush",
                    CapabilityPillFillOpacity,
                    isHighContrast),
                ResolveBrush("SystemFillColorCautionBrush"),
                ResolveBrush("SystemFillColorCautionBrush"),
                "ConnectionPage_NodePillState_Pending",
                FluentIconCatalog.StatusWarn),
            _ => (
                ResolveBrush("SubtleFillColorTertiaryBrush"),
                ResolveBrush("TextFillColorTertiaryBrush"),
                ResolveBrush("TextFillColorSecondaryBrush"),
                "ConnectionPage_NodePillState_Off",
                null),
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var capabilityIcon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 12,
            Foreground = iconBrush,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextScaleFactorEnabled = false,
        };
        AutomationProperties.SetAccessibilityView(capabilityIcon, AccessibilityView.Raw);
        content.Children.Add(capabilityIcon);

        var stateText = LocalizationHelper.GetString(stateKey);
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = textBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(labelText, $"{label} - {stateText}");
        content.Children.Add(labelText);

        if (stateGlyph != null)
        {
            var stateIcon = new FontIcon
            {
                Glyph = stateGlyph,
                FontSize = 10,
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center,
                IsTextScaleFactorEnabled = false,
            };
            AutomationProperties.SetAccessibilityView(stateIcon, AccessibilityView.Raw);
            content.Children.Add(stateIcon);
        }

        var pill = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 3, 11, 3),
            Background = fillBrush,
            Child = content,
        };
        ToolTipService.SetToolTip(pill, stateText);
        return pill;
    }

    private Brush TintBrush(string colorKey, string highContrastBrushKey, double opacity, bool isHighContrast)
    {
        if (isHighContrast)
            return ResolveBrush(highContrastBrushKey);

        var color = ResolveBrush(colorKey) is SolidColorBrush scb
            ? scb.Color
            : ((SolidColorBrush)ResolveBrush("TextFillColorPrimaryBrush")).Color;
        return new SolidColorBrush(color) { Opacity = opacity };
    }

    private bool IsHighContrastEnabled()
    {
        if (_accessibilitySettings is null)
            return false;

        try { return _accessibilitySettings.HighContrast; }
        catch (Exception ex)
        {
            Services.Logger.Warn($"[ConnectionPage] Could not read High Contrast state: {ex.Message}");
            return false;
        }
    }

    private static string BuildCapabilityPillFingerprint(
        NodeCardState state,
        IReadOnlyList<string> effective,
        IReadOnlyList<string> pendingDeclared,
        SettingsManager settings)
    {
        var eff = string.Join(
            ",",
            effective.Where(c => !string.IsNullOrWhiteSpace(c))
                     .OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
        var pend = string.Join(
            ",",
            pendingDeclared.Where(c => !string.IsNullOrWhiteSpace(c))
                           .OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
        var toggles = string.Concat(
            settings.NodeBrowserProxyEnabled ? '1' : '0',
            settings.NodeCameraEnabled ? '1' : '0',
            settings.NodeCanvasEnabled ? '1' : '0',
            settings.NodeScreenEnabled ? '1' : '0',
            settings.NodeLocationEnabled ? '1' : '0',
            settings.NodeTtsEnabled ? '1' : '0',
            settings.NodeSttEnabled ? '1' : '0');
        return $"{state}|{eff}|{pend}|{toggles}";
    }

    /// <summary>
    /// Formats one gateway-reported node surface while preserving the
    /// approved/effective versus pending-declared label chosen by the caller.
    /// </summary>
    private static string BuildNodeSurfaceListString(
        string resourceKey,
        IReadOnlyList<string> values)
        => LocalizationHelper.Format(resourceKey, FormatSurfaceList(values));

    private static string FormatSurfaceList(IReadOnlyList<string> values)
        => values.Count == 0
            ? LocalizationHelper.GetString("ConnectionPage_NodeSurfaceNone")
            : string.Join(", ", values);

    private static string BuildNodePermissionListString(
        string resourceKey,
        IReadOnlyDictionary<string, bool> permissions)
        => LocalizationHelper.Format(resourceKey, FormatPermissionList(permissions));

    private static string FormatPermissionList(IReadOnlyDictionary<string, bool> permissions)
        => permissions.Count == 0
            ? LocalizationHelper.GetString("ConnectionPage_NodeSurfaceNone")
            : string.Join(", ", permissions
                .OrderBy(permission => permission.Key, StringComparer.OrdinalIgnoreCase)
                .Select(permission =>
                    $"{permission.Key}={permission.Value.ToString().ToLowerInvariant()}"));

    private static void SetSurfaceInlines(TextBlock target, string resourceKey, string value)
    {
        var format = LocalizationHelper.GetString(resourceKey);
        var idx = format.IndexOf("{0}", StringComparison.Ordinal);
        var label = idx >= 0 ? format[..idx] : format;
        var suffix = idx >= 0 ? format[(idx + 3)..] : string.Empty;

        target.Inlines.Clear();
        target.Inlines.Add(new Run { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        target.Inlines.Add(new Run { Text = value + suffix });
    }

    private static string HumanizeNodeToken(string token)
    {
        var spaced = token.Trim().Replace('.', ' ').Replace('_', ' ');
        if (spaced.Length == 0) return spaced;
        return char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    private Brush ResolveBrush(string themeKey)
    {
        if (Application.Current.Resources.TryGetValue(themeKey, out var v) && v is Brush b)
            return b;
        return (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
    }

    // ─── Saved gateways list (Lobby + Recovery) ───────────────────────

    private void LoadSavedGateways()
    {
        var items = new List<SavedGatewayRow>();
        var emptyVisible = Visibility.Visible;

        // Auth mode for the ACTIVE gateway - taken from the latest hello-ok
        // response (server-reported, the source of truth for what we're
        // actually using). For inactive saved gateways we fall back to whatever
        // credential is stored on the record.
        var activeAuthMode = CurrentApp.AppState?.GatewaySelf?.AuthMode;

        if (_gatewayRegistry != null)
        {
            var all = _gatewayRegistry.GetAll();
            var active = _gatewayRegistry.GetActive();
            foreach (var gw in all)
            {
                var isActive = active != null && active.Id == gw.Id;
                var hostAccess = GatewayHostAccessClassifier.Classify(gw);
                items.Add(new SavedGatewayRow
                {
                    Id = gw.Id,
                    DisplayName = !string.IsNullOrEmpty(gw.FriendlyName) ? gw.FriendlyName! : gw.Url,
                    Url = gw.Url,
                    IsActive = isActive,
                    LastConnectedRelative = FormatRelative(gw.LastConnected),
                    HasSshTunnel = gw.SshTunnel != null,
                    HasWslGateway = hostAccess.IsWslManaged,
                    HasHostTerminal = hostAccess.CanOpenTerminal,
                    HostTerminalLabel = hostAccess.TerminalLabel,
                    AuthModeLabel = BuildAuthModeLabel(gw, isActive, _lastSnapshot, activeAuthMode),
                });
            }
            if (all.Count > 0) emptyVisible = Visibility.Collapsed;
        }

        SavedGatewaysEmptyText.Visibility = emptyVisible;

        // Fingerprint to skip ItemsSource swap when nothing observable
        // changed - see field comment. Includes overall connection state
        // because that drives the per-row "Connected" badge, and Url
        // because the row's sub-line shows it.
        var sb = new System.Text.StringBuilder(items.Count * 64);
        sb.Append(_lastSnapshot.OverallState).Append('|');
        foreach (var r in items)
        {
            sb.Append(r.Id).Append('/').Append(r.IsActive ? '1' : '0').Append('/')
              .Append(r.DisplayName).Append('/').Append(r.Url ?? "").Append('/')
              .Append(r.LastConnectedRelative ?? "")
              .Append('/').Append(r.HasSshTunnel ? '1' : '0').Append('/')
              .Append(r.HasWslGateway ? '1' : '0').Append('/')
              .Append(r.HasHostTerminal ? '1' : '0').Append('/')
              .Append(r.HostTerminalLabel ?? "").Append('/')
              .Append(r.AuthModeLabel ?? "").Append(';');
        }
        var fp = sb.ToString();
        if (_savedGatewaysFingerprint == fp) return;
        _savedGatewaysFingerprint = fp;

        SavedGatewaysList.ItemsSource = BuildSavedGatewayRowControls(items);
        RecoverySavedList.ItemsSource = BuildSavedGatewayRowControls(items);
        RecoverySavedHeaderText.Text = items.Count == 1
            ? LocalizationHelper.GetString("ConnectionPage_SavedGatewaysSingular")
            : string.Format(LocalizationHelper.GetString("ConnectionPage_SavedGatewaysPlural"), items.Count);
    }

    private static string BuildAuthModeLabel(
        GatewayRecord rec,
        bool isActive,
        GatewayConnectionSnapshot snapshot,
        string? activeAuthMode)
    {
        if (isActive)
        {
            var credentialLabel = ConnectionPagePlan.FormatCredentialSummary(snapshot);
            if (!string.IsNullOrEmpty(credentialLabel))
                return credentialLabel;

            if (!string.IsNullOrEmpty(activeAuthMode))
                return NormalizeGatewayAuthMode(activeAuthMode!);
        }

        return InferAuthModeLabel(rec);
    }

    private static string InferAuthModeLabel(GatewayRecord rec)
    {
        if (!string.IsNullOrEmpty(rec.BootstrapToken)) return LocalizationHelper.GetString("ConnectionPage_AuthModeBootstrap");
        if (!string.IsNullOrEmpty(rec.SharedGatewayToken)) return LocalizationHelper.GetString("ConnectionPage_AuthModeSharedToken");
        // No credential on the record itself → likely paired (device token
        // stored in the DeviceIdentityStore for this gateway's identity dir).
        return LocalizationHelper.GetString("ConnectionPage_AuthModeDeviceToken");
    }

    private static string NormalizeGatewayAuthMode(string authMode) =>
        string.Equals(authMode, "device-token", StringComparison.OrdinalIgnoreCase)
            ? "paired via device token"
            : authMode;

    private List<Border> BuildSavedGatewayRowControls(IEnumerable<SavedGatewayRow> rows)
    {
        var list = new List<Border>();
        foreach (var row in rows)
        {
            list.Add(BuildSavedGatewayRowControl(row));
        }
        return list;
    }

    /// <summary>
    /// Returns true when the active gateway row is in a state where
    /// "Disconnect" is a meaningful action. Delegates to
    /// <see cref="ConnectionPageRowState.CanDisconnectFromBadge"/>, kept
    /// pure so the contract can be unit-tested.
    /// </summary>
    private static bool CanDisconnectFromBadge(OverallConnectionState state) =>
        ConnectionPageRowState.CanDisconnectFromBadge(state);

    /// <summary>
    /// Returns the inline status badge to render on the active gateway row
    /// in the saved-gateways list, or <c>null</c> when the row should fall
    /// back to a [Connect] button. The badge tracks the *real* overall
    /// state - historically Connecting and PairingRequired were collapsed
    /// into "Connected", which told users they were connected while the
    /// page was actually mid-handshake or awaiting an approval.
    /// </summary>
    private FrameworkElement? BuildActiveRowBadge(OverallConnectionState state)
    {
        // (glyph, brushKey, label) per state. Connected/Ready/Degraded all
        // mean "operator is online" - the only case that warrants the
        // affirmative "Connected" badge.
        (string glyph, string brushKey, string label)? badge = state switch
        {
            OverallConnectionState.Connected or
            OverallConnectionState.Ready or
            OverallConnectionState.Degraded =>
                (Helpers.FluentIconCatalog.StatusOk, "SystemFillColorSuccessBrush", LocalizationHelper.GetString("ConnectionPage_BadgeConnected")),

            OverallConnectionState.Connecting =>
                (Helpers.FluentIconCatalog.Sync, "SystemFillColorCautionBrush", LocalizationHelper.GetString("ConnectionPage_BadgeConnecting")),

            OverallConnectionState.PairingRequired =>
                (Helpers.FluentIconCatalog.Lock, "SystemFillColorCautionBrush", LocalizationHelper.GetString("ConnectionPage_BadgeAwaitingApproval")),

            OverallConnectionState.Disconnecting =>
                (Helpers.FluentIconCatalog.Sync, "TextFillColorSecondaryBrush", LocalizationHelper.GetString("ConnectionPage_BadgeDisconnecting")),

            // Idle and Error → no badge; caller renders [Connect].
            // The status strip up top already carries the "broken / can't
            // reach gateway" signal (with the URL and category), and the
            // Recovery card right beneath it offers Disconnect. Adding an
            // "Error" badge here would duplicate that signal and remove the
            // [Connect] retry affordance - which is the one actionable
            // thing the row should offer in an Error state.
            _ => null,
        };

        if (badge is null) return null;
        var (glyph, brushKey, label) = badge.Value;

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 12,
            Foreground = ResolveBrush(brushKey),
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ResolveBrush(brushKey),
            VerticalAlignment = VerticalAlignment.Center,
        });
        // Accessibility: the badge label is the screen-reader narration for
        // the active row's status. Marking the container as a polite live
        // region triggers a re-announcement when the page rebuilds the row
        // with a new badge (e.g. Connecting… → Awaiting approval → Connected).
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(stack, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(
            stack, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        return stack;
    }

    private Border BuildSavedGatewayRowControl(SavedGatewayRow row)
    {
        // All rows use neutral card chrome. The status badge alone communicates
        // which row is the active/live gateway; tinting the whole row was visually noisy.
        var card = new Border
        {
            Background = ResolveBrush("CardBackgroundFillColorSecondaryBrush"),
            BorderBrush = ResolveBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 8, 8),
            Margin = new Thickness(0, 0, 0, 6),
        };

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Info column
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        info.Children.Add(new TextBlock
        {
            Text = row.DisplayName,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var sub = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        sub.Children.Add(new TextBlock
        {
            Text = ConnectionCardPlanSanitizer.SanitizeGatewayUrl(row.Url),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
        });
        if (!string.IsNullOrEmpty(row.AuthModeLabel))
        {
            sub.Children.Add(new TextBlock
            {
                Text = $"• {row.AuthModeLabel}",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = ResolveBrush("TextFillColorTertiaryBrush"),
            });
        }
        if (row.HasSshTunnel)
        {
            sub.Children.Add(new TextBlock
            {
                Text = "• " + LocalizationHelper.GetString("ConnectionPage_ViaSSH"),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
            });
        }
        if (row.HasWslGateway)
        {
            sub.Children.Add(new TextBlock
            {
                Text = "• WSL",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
            });
        }
        if (!string.IsNullOrEmpty(row.LastConnectedRelative) && !row.IsActive)
        {
            sub.Children.Add(new TextBlock
            {
                Text = $"• {row.LastConnectedRelative}",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = ResolveBrush("TextFillColorTertiaryBrush"),
            });
        }
        info.Children.Add(sub);
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        // Per-row right-hand affordance: a status badge when the row is the
        // *active* gateway and the snapshot has something live-ish to report,
        // otherwise a [Connect] button to (re)activate the row.
        //
        // The badge label must follow real state - previously this branch
        // collapsed Connecting and PairingRequired into "Connected", which
        // told users they were connected while the page was actually
        // mid-handshake or waiting for the gateway operator to approve a
        // pairing request.
        var badge = row.IsActive ? BuildActiveRowBadge(_lastSnapshot.OverallState) : null;
        bool hasLiveAffordance = badge != null;
        if (hasLiveAffordance)
        {
            Grid.SetColumn(badge!, 1);
            grid.Children.Add(badge!);
        }
        else
        {
            var connectBtn = new Button
            {
                Content = LocalizationHelper.GetString("ConnectionPage_ConnectAction"),
                Tag = row.Id,
                VerticalAlignment = VerticalAlignment.Center,
            };
            connectBtn.Click += OnConnectSavedGateway;
            Grid.SetColumn(connectBtn, 1);
            grid.Children.Add(connectBtn);
        }

        // Overflow menu - actions depend on whether this row is currently the
        // live connection. Disconnect only makes sense when actually connected.
        //   Connected (active + live) : Open dashboard · Disconnect · Edit
        //   Active but disconnected   : Open dashboard · Edit · Remove
        //   Inactive                  : Open dashboard · Edit · Remove
        var overflowBtn = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = row.Id,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(overflowBtn,
            string.Format(LocalizationHelper.GetString("ConnectionPage_GatewayOptionsA11y"), row.DisplayName));
        overflowBtn.Content = new FontIcon
        {
            Glyph = Helpers.FluentIconCatalog.MoreOverflow,
            FontSize = 12,
            Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
        };
        var flyout = new MenuFlyout();
        var openDashboard = new MenuFlyoutItem { Text = LocalizationHelper.GetString("ConnectionPage_OpenDashboard"), Tag = row.Id };
        openDashboard.Click += OnSavedRowOpenDashboard;
        flyout.Items.Add(openDashboard);
        if (row.HasHostTerminal)
        {
            var openTerminal = new MenuFlyoutItem { Text = row.HostTerminalLabel, Tag = row.Id };
            openTerminal.Click += OnSavedRowOpenTerminal;
            flyout.Items.Add(openTerminal);
        }
        if (hasLiveAffordance)
        {
            // Whenever the row is in a state where Disconnect makes sense
            // (Connected / Connecting… / Awaiting approval / Error) we offer
            // it as the corresponding teardown / cancel action. Disconnecting
            // is intentionally excluded - teardown is already in flight and
            // re-entering would race the connection manager.
            if (CanDisconnectFromBadge(_lastSnapshot.OverallState))
            {
                var disconnect = new MenuFlyoutItem { Text = LocalizationHelper.GetString("ConnectionPage_DisconnectAction"), Tag = row.Id };
                disconnect.Click += OnDisconnect;
                flyout.Items.Add(disconnect);
            }
            var editActive = new MenuFlyoutItem { Text = LocalizationHelper.GetString("ConnectionPage_Edit"), Tag = row.Id };
            editActive.Click += OnSavedRowEdit;
            flyout.Items.Add(editActive);
        }
        else
        {
            var edit = new MenuFlyoutItem { Text = LocalizationHelper.GetString("ConnectionPage_Edit"), Tag = row.Id };
            edit.Click += OnSavedRowEdit;
            flyout.Items.Add(edit);
            // Removing the active-but-disconnected row just clears the active
            // pointer - safe.
            flyout.Items.Add(new MenuFlyoutSeparator());
            var remove = new MenuFlyoutItem { Text = LocalizationHelper.GetString("ConnectionPage_Remove"), Tag = row.Id };
            remove.Click += OnSavedRowRemove;
            flyout.Items.Add(remove);
        }
        overflowBtn.Flyout = flyout;
        Grid.SetColumn(overflowBtn, 2);
        grid.Children.Add(overflowBtn);

        card.Child = grid;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, row.DisplayName);
        return card;
    }

    private static string FormatRelative(DateTime? when)
    {
        if (when == null) return "";
        var span = DateTime.UtcNow - when.Value.ToUniversalTime();
        if (span.TotalMinutes < 1) return LocalizationHelper.GetString("ConnectionPage_JustNow");
        if (span.TotalMinutes < 60) return string.Format(LocalizationHelper.GetString("ConnectionPage_MinutesAgo"), (int)span.TotalMinutes);
        if (span.TotalHours < 24) return string.Format(LocalizationHelper.GetString("ConnectionPage_HoursAgo"), (int)span.TotalHours);
        if (span.TotalDays < 30) return string.Format(LocalizationHelper.GetString("ConnectionPage_DaysAgo"), (int)span.TotalDays);
        if (span.TotalDays < 365) return string.Format(LocalizationHelper.GetString("ConnectionPage_MonthsAgo"), (int)(span.TotalDays / 30));
        return string.Format(LocalizationHelper.GetString("ConnectionPage_YearsAgo"), (int)(span.TotalDays / 365));
    }

    private sealed class SavedGatewayRow
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Url { get; init; } = "";
        public bool IsActive { get; init; }
        public string LastConnectedRelative { get; init; } = "";
        public bool HasSshTunnel { get; init; }
        public bool HasWslGateway { get; init; }
        public bool HasHostTerminal { get; init; }
        public string HostTerminalLabel { get; init; } = "";
        public string AuthModeLabel { get; init; } = "";
    }

    // ─── User intent transitions ──────────────────────────────────────

    private void OnEnterAddGateway(object sender, RoutedEventArgs e)
    {
        _editingGatewayId = null;
        _userIntent = UserIntent.AddingGateway;
        ClearAddGatewaySshFields();
        // Direct is default - make sure the selector is on Direct.
        // Pre-fill the most common local gateway URL.
        DirectUrlBox.Text = "ws://127.0.0.1:18789";
        DirectTokenBox.Text = "";
        DirectNameBox.Text = "";
        AutoFillTokenForUrl(DirectUrlBox.Text);
        ShowAddPane("direct");
        AddDirectItem.IsSelected = true;
        RefreshFromSnapshot(_lastSnapshot);
    }

    private void OnEnterAddGatewayDirect(object sender, RoutedEventArgs e)
    {
        _editingGatewayId = null;
        _userIntent = UserIntent.AddingGateway;
        ClearAddGatewaySshFields();
        ShowAddPane("direct");
        AddDirectItem.IsSelected = true;
        RefreshFromSnapshot(_lastSnapshot);
    }

    private void OnEnterAddGatewaySetupCode(object sender, RoutedEventArgs e)
    {
        _editingGatewayId = null;
        _userIntent = UserIntent.AddingGateway;
        ClearAddGatewaySshFields();
        ShowAddPane("setup");
        AddSetupCodeItem.IsSelected = true;
        RefreshFromSnapshot(_lastSnapshot);
    }

    private void OnEnterAddGatewayScan(object sender, RoutedEventArgs e)
    {
        // Legacy entry point - Scan no longer lives in the Add form.
        // Route to the canonical scan-from-gateways flow instead.
        OnScanGatewaysClicked(sender, e);
    }

    private void OnAddBack(object sender, RoutedEventArgs e)
    {
        _editingGatewayId = null;
        _userIntent = UserIntent.None;
        // Clear transient form state so re-entering is fresh
        AddResultText.Text = "";
        AddSetupCodeBox.Text = "";
        AddSetupCodePreviewPanel.Visibility = Visibility.Collapsed;
        ClearAddGatewaySshFields();
        AddScanStatusText.Text = LocalizationHelper.GetString("ConnectionPage_PressScan");
        AddScanProgressBar.Visibility = Visibility.Collapsed;
        AddScanResultsPanel.Children.Clear();
        RefreshFromSnapshot(_lastSnapshot);
    }

    private void OnAddMethodChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is not string tag) return;
        ShowAddPane(tag);
    }

    private void ShowAddPane(string tag)
    {
        AddDirectPane.Visibility = (tag == "direct") ? Visibility.Visible : Visibility.Collapsed;
        AddSetupCodePane.Visibility = (tag == "setup") ? Visibility.Visible : Visibility.Collapsed;
        AddLocalWslPane.Visibility = (tag == "local") ? Visibility.Visible : Visibility.Collapsed;
        // Scan pane is dead code now (kept for back-compat); always hidden.
        AddScanPane.Visibility = Visibility.Collapsed;

        // SSH tunnel + Save/Cancel row only apply to Direct and Setup-code
        // methods. The Local-WSL pane drives the install via its own
        // button and doesn't need a per-gateway form to submit.
        bool isFormMethod = (tag == "direct") || (tag == "setup");
        AddSshExpander.Visibility = isFormMethod ? Visibility.Visible : Visibility.Collapsed;
        AddSaveButton.Visibility = isFormMethod ? Visibility.Visible : Visibility.Collapsed;
        AddRemoteHelpLink.Visibility = isFormMethod ? Visibility.Visible : Visibility.Collapsed;
        if (!isFormMethod)
            AddRemoteHelpTip.IsOpen = false;

        UpdateRemoteSetupAdvice();
    }

    private void ClearAddGatewaySshFields()
    {
        AddSshExpander.IsExpanded = false;
        AddSshUserBox.Text = "";
        AddSshHostBox.Text = "";
        AddSshServerPortBox.Text = "";
        AddSshRemotePortBox.Text = "";
        AddSshLocalPortBox.Text = "";
    }

    private void OnRemoteHelpClick(object sender, RoutedEventArgs e)
    {
        AddRemoteHelpTip.IsOpen = !AddRemoteHelpTip.IsOpen;
    }

    // ─── Remote setup transport-security advisory ─────────────────────
    // Offline (no network) classification driven by RemoteGatewayClassifier.
    // Steers users to TLS / SSH tunnel / trusted proxy before they save a
    // gateway that would send the token in cleartext over the network.

    private void OnAddSshExpanding(Expander sender, ExpanderExpandingEventArgs args) =>
        UpdateRemoteSetupAdvice(sshExpandedOverride: true);

    private void OnAddSshCollapsed(Expander sender, ExpanderCollapsedEventArgs args) =>
        UpdateRemoteSetupAdvice(sshExpandedOverride: false);

    private void OnAddSshFieldChanged(object sender, TextChangedEventArgs e) =>
        UpdateRemoteSetupAdvice();

    private void UpdateRemoteSetupAdvice(bool? sshExpandedOverride = null)
    {
        if (AddSecurityAdviceBar == null) return;

        // The advice applies to the two form methods (Direct + Setup code).
        // Pick the URL from whichever is active: the typed Direct URL, or the
        // URL decoded from a pasted setup code.
        string? url;
        var tag = ActiveAddPaneTag();
        if (tag == "direct")
            url = DirectUrlBox.Text?.Trim();
        else if (tag == "setup")
            url = _lastDecodedSetupUrl;
        else
        {
            AddSecurityAdviceBar.IsOpen = false;
            return;
        }

        // The Expander.Expanding/Collapsed events fire before IsExpanded flips,
        // so callers pass the post-transition state to avoid a one-frame flash
        // of the cleartext warning.
        bool sshExpanded = sshExpandedOverride ?? AddSshExpander.IsExpanded;
        bool hasSshTunnel = sshExpanded
                         && !string.IsNullOrWhiteSpace(AddSshUserBox.Text)
                         && !string.IsNullOrWhiteSpace(AddSshHostBox.Text);

        var profile = RemoteGatewayClassifier.Classify(url, hasSshTunnel);

        InfoBarSeverity severity;
        string title;
        string message;
        bool open = true;

        switch (profile.Topology)
        {
            case GatewayConnectionTopology.DirectInsecure:
                severity = InfoBarSeverity.Warning;
                title = LocalizationHelper.GetString("ConnectionPage_AdviceCleartextTitle");
                message = LocalizationHelper.GetString("ConnectionPage_AdviceCleartextMessage");
                break;
            case GatewayConnectionTopology.DirectSecure:
                severity = InfoBarSeverity.Success;
                title = LocalizationHelper.GetString("ConnectionPage_AdviceSecureTitle");
                message = LocalizationHelper.GetString("ConnectionPage_AdviceSecureMessage");
                break;
            case GatewayConnectionTopology.SshTunnel:
                severity = InfoBarSeverity.Success;
                title = LocalizationHelper.GetString("ConnectionPage_AdviceTunnelTitle");
                message = LocalizationHelper.GetString("ConnectionPage_AdviceTunnelMessage");
                break;
            default:
                // Local or unparseable - no transport warning needed.
                AddSecurityAdviceBar.IsOpen = false;
                return;
        }

        // InfoBar does not reliably repaint its severity icon/accent when
        // Severity changes while IsOpen stays true. When the severity actually
        // changes (e.g. cleartext ws:// → TLS wss://), force a close before
        // re-applying so the bar re-renders cleanly. Guard on change so we
        // don't flicker on every keystroke within the same severity.
        if (AddSecurityAdviceBar.IsOpen && AddSecurityAdviceBar.Severity != severity)
            AddSecurityAdviceBar.IsOpen = false;

        AddSecurityAdviceBar.Severity = severity;
        AddSecurityAdviceBar.Title = title;
        AddSecurityAdviceBar.Message = message;
        AddSecurityAdviceBar.IsOpen = open;
    }

    private string ActiveAddPaneTag()
    {
        if (AddSetupCodeItem.IsSelected) return "setup";
        if (AddLocalWslItem.IsSelected) return "local";
        return "direct"; // default
    }

    // ─── Status strip actions ────────────────────────────────────────

    private void OnStripPrimaryClicked(object sender, RoutedEventArgs e)
    {
        var plan = _currentPlan;
        switch (plan.StripPrimaryAction)
        {
            case ConnectionPrimaryAction.Connect:
            case ConnectionPrimaryAction.Reconnect:
            case ConnectionPrimaryAction.Retry:
                ((IAppCommands)CurrentApp).Reconnect();
                break;
            case ConnectionPrimaryAction.Cancel:
                _ = _connectionManager?.DisconnectAsync();
                break;
            case ConnectionPrimaryAction.RestartTunnel:
                OnRestartTunnel(sender, e);
                break;
            // CopyApproveCommand and Rep arms were removed: the inline Copy
            // button (RecoveryApproveCmdBlock / NodeApproveCmdBox) and the
            // paste-setup-code block (RecoveryAuthPasteBlock) own those
            // flows now. Both enum values are retired in ConnectionPagePlan.
        }
    }

    private void OnOpenDashboard(object sender, RoutedEventArgs e)
    {
        ((IAppCommands)CurrentApp).OpenDashboard();
    }

    private void OnOpenGatewayTerminal(object sender, RoutedEventArgs e)
    {
        OpenGatewayTerminal(_activeHostAccessPlan, showInlineStatus: true);
    }

    private void OnSavedRowOpenTerminal(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string gwId) return;
        var record = _gatewayRegistry?.GetById(gwId);
        var accessPlan = GatewayHostAccessClassifier.Classify(record);
        OpenGatewayTerminal(accessPlan, showInlineStatus: record?.Id == _gatewayRegistry?.GetActive()?.Id);
    }

    private void OpenGatewayTerminal(GatewayHostAccessPlan accessPlan, bool showInlineStatus)
    {
        if (!accessPlan.CanOpenTerminal)
        {
            ShowGatewayHostFailure(
                "Terminal unavailable",
                accessPlan.DisabledReason ?? "This gateway does not have terminal access.",
                showInlineStatus);
            return;
        }

        try
        {
            TerminalLauncher.Open(accessPlan);
        }
        catch (Exception ex)
        {
            Services.Logger.Warn($"[ConnectionPage] Failed to open gateway terminal: {ex.Message}");
            ShowGatewayHostFailure("Terminal failed", ex.Message, showInlineStatus);
        }
    }

    private void OnStartGatewayClicked(object sender, RoutedEventArgs e)
    {
        _ = RunWslGatewayControlAsync(WslGatewayControlAction.Start);
    }

    private void OnStopGatewayClicked(object sender, RoutedEventArgs e)
    {
        _ = RunWslGatewayControlAsync(WslGatewayControlAction.Stop);
    }

    private void OnRestartGatewayClicked(object sender, RoutedEventArgs e)
    {
        _ = RunWslGatewayControlAsync(WslGatewayControlAction.Restart);
    }

    private async Task RunWslGatewayControlAsync(WslGatewayControlAction action)
    {
        if (_gatewayHostActionInProgress)
        {
            return;
        }

        var activeRecord = _gatewayRegistry?.GetActive();
        var accessPlan = GatewayHostAccessClassifier.Classify(activeRecord);
        if (!accessPlan.CanControlWslGateway || string.IsNullOrWhiteSpace(accessPlan.DistroName))
        {
            SetGatewayHostActionStatus("This gateway is not an app-managed WSL gateway.", isError: true);
            return;
        }

        var cts = new CancellationTokenSource();
        _gatewayHostActionCts = cts;
        var cancellationToken = cts.Token;
        _gatewayHostActionInProgress = true;
        ApplyGatewayHostAccess(_currentPlan);
        var verb = WslGatewayControlCommandBuilder.ToVerb(action);
        SetGatewayHostActionStatus($"{ActionInProgressLabel(action)} gateway in {accessPlan.DistroName}…");

        try
        {
            if (action == WslGatewayControlAction.Stop && _connectionManager != null)
            {
                try
                {
                    await _connectionManager.DisconnectAsync();
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    Services.Logger.Warn($"[ConnectionPage] Disconnect before WSL gateway stop failed; continuing stop: {ex.Message}");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = await WslGatewayController.RunAsync(accessPlan.DistroName!, action, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                var details = string.IsNullOrWhiteSpace(result.OutputSummary)
                    ? $"wsl.exe exited with code {result.ExitCode}."
                    : result.OutputSummary;
                SetGatewayHostActionStatus($"{UppercaseFirst(verb)} failed: {details}", isError: true);
                return;
            }

            if (action == WslGatewayControlAction.Stop)
            {
                SetGatewayHostActionStatus("Gateway stopped.");
                RefreshFromSnapshot(_connectionManager?.CurrentSnapshot ?? _lastSnapshot);
                return;
            }

            SetGatewayHostActionStatus($"Gateway {PastTense(action)}. Reconnecting…");
            BeginReconnectMask();
            ((IAppCommands)CurrentApp).Reconnect();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Services.Logger.Info($"[ConnectionPage] WSL gateway {verb} cancelled.");
        }
        catch (Exception ex)
        {
            Services.Logger.Warn($"[ConnectionPage] WSL gateway {verb} failed: {ex.Message}");
            SetGatewayHostActionStatus($"{UppercaseFirst(verb)} failed: {ex.Message}", isError: true);
        }
        finally
        {
            _gatewayHostActionInProgress = false;
            if (ReferenceEquals(_gatewayHostActionCts, cts))
            {
                _gatewayHostActionCts = null;
            }
            cts.Dispose();
            if (IsLoaded)
            {
                ApplyGatewayHostAccess(_currentPlan);
            }
        }
    }

    private void ShowGatewayHostFailure(string title, string message, bool preferInlineStatus)
    {
        if (preferInlineStatus && GatewayHostControlsSection.Visibility == Visibility.Visible)
        {
            SetGatewayHostActionStatus(message, isError: true);
            return;
        }

        // No inline WSL-controls surface is available here (e.g. launching a
        // terminal for a non-active saved gateway, where that card isn't shown).
        // The in-page Connection Error bar was removed, so surface the failure as
        // a transient top-bar notification rather than dropping it silently. This
        // is a one-off action failure, not the persistent connection-issue banner,
        // so it carries no "Open Connection" action.
        AppNotificationPublisher.Show(
            CurrentApp.AppNotifications,
            title,
            message,
            "connection",
            "gateway-host",
            AppNotificationSeverity.Error,
            $"gateway-host-action:{title}",
            actionRoute: string.Empty,
            actionLabel: string.Empty,
            id: $"gateway-host-action:{title}");
    }

    private static string UppercaseFirst(string value)
    {
        return string.IsNullOrEmpty(value)
            ? value
            : char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static string PastTense(WslGatewayControlAction action)
    {
        return action switch
        {
            WslGatewayControlAction.Start => "started",
            WslGatewayControlAction.Restart => "restarted",
            WslGatewayControlAction.Stop => "stopped",
            _ => "updated"
        };
    }

    private static string ActionInProgressLabel(WslGatewayControlAction action)
    {
        return action switch
        {
            WslGatewayControlAction.Start => "Starting",
            WslGatewayControlAction.Stop => "Stopping",
            WslGatewayControlAction.Restart => "Restarting",
            _ => "Updating"
        };
    }

    /// <summary>
    /// Handler for both the Welcome and Cockpit "Install local WSL gateway"
    /// buttons. Hands off to the hub's OpenSetupAction (which the App wires
    /// to the V2 onboarding flow) - same wiring master added on the legacy
    /// ConnectionPage; the cards live in different slots in the rebuilt
    /// page but the user-facing behavior is identical.
    /// </summary>
    private void OnInstallLocalWslGateway(object sender, RoutedEventArgs e)
    {
        ((IAppCommands)CurrentApp).ShowOnboarding();
    }

    // ─── Operator card navigation ────────────────────────────────────

    private void OnOpenSessions(object sender, RoutedEventArgs e) => ((IAppCommands)CurrentApp).Navigate("sessions");
    private void OnOpenInstances(object sender, RoutedEventArgs e) => ((IAppCommands)CurrentApp).Navigate("instances");

    // ─── Node card navigation ────────────────────────────────────────

    private void OnOpenPermissions(object sender, RoutedEventArgs e) => ((IAppCommands)CurrentApp).Navigate("permissions");

    private void OnCopyNodeApproveCommand(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(NodeApproveCmdText.Text))
            ClipboardHelper.CopyText(NodeApproveCmdText.Text);
    }

    private void OnCopyNodeTrustApproveCommand(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(NodeTrustApproveCmdText.Text))
            ClipboardHelper.CopyText(NodeTrustApproveCmdText.Text);
    }

    // ─── Recovery actions ────────────────────────────────────────────

    private void OnCopyApproveCommand(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(RecoveryApproveCmdText.Text))
            ClipboardHelper.CopyText(RecoveryApproveCmdText.Text);
    }

    private void OnRestartTunnel(object sender, RoutedEventArgs e)
    {
        try
        {
            var app = (App)Microsoft.UI.Xaml.Application.Current;
            app.EnsureSshTunnelStarted();
            AddResultText.Text = LocalizationHelper.GetString("ConnectionPage_TunnelRestartTriggered");
        }
        catch (Exception ex)
        {
            AddResultText.Text = string.Format(LocalizationHelper.GetString("ConnectionPage_TunnelRestartFailed"), ex.Message);
        }
    }

    private void OnEditTunnelSettings(object sender, RoutedEventArgs e)
    {
        // The active gateway's SSH tunnel is edited via the per-gateway Edit
        // action (saved-gateway row overflow → Edit), which routes into the
        // Add Gateway form pre-filled.
        var active = _gatewayRegistry?.GetActive();
        if (active == null) return;
        var rec = active;
        _editingGatewayId = rec.Id;
        _userIntent = UserIntent.AddingGateway;
        DirectUrlBox.Text = rec.Url;
        DirectTokenBox.Text = rec.SharedGatewayToken ?? "";
        DirectNameBox.Text = rec.FriendlyName ?? "";
        if (rec.SshTunnel != null)
        {
            AddSshExpander.IsExpanded = true;
            AddSshUserBox.Text = rec.SshTunnel.User;
            AddSshHostBox.Text = rec.SshTunnel.Host;
            AddSshServerPortBox.Text = rec.SshTunnel.SshPort.ToString();
            AddSshRemotePortBox.Text = rec.SshTunnel.RemotePort.ToString();
            AddSshLocalPortBox.Text = rec.SshTunnel.LocalPort.ToString();
        }
        ShowAddPane("direct");
        AddDirectItem.IsSelected = true;
        RefreshFromSnapshot(_lastSnapshot);
    }

    private void OnApplyRepairCode(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            OnApplyRepairCodeAsync,
            new OpenClawTray.AppLogger(),
            nameof(OnApplyRepairCode));

    private async Task OnApplyRepairCodeAsync()
    {
        var code = RecoveryRepairCodeBox.Text?.Trim();
        if (string.IsNullOrEmpty(code) || _connectionManager == null) return;
        void ShowResult(string text, bool error)
        {
            RecoveryRepairResultText.Text = text;
            RecoveryRepairResultText.Foreground = (Brush)Application.Current.Resources[
                error ? "SystemFillColorCriticalBrush" : "SystemFillColorSuccessBrush"];
            RecoveryRepairResultText.Visibility = Visibility.Visible;
        }
        try
        {
            var result = await _connectionManager.ApplySetupCodeAsync(code);
            if (result.Outcome == SetupCodeOutcome.Success)
                ShowResult(LocalizationHelper.GetString("ConnectionPage_RepairedReconnecting"), error: false);
            else
                ShowResult($"✗ {result.ErrorMessage ?? LocalizationHelper.GetString("ConnectionPage_CouldNotApplyCode")}", error: true);
        }
        catch (Exception ex)
        {
            ShowResult($"✗ {ex.Message}", error: true);
        }
    }

    // ─── Saved-gateway row actions ───────────────────────────────────

    private void OnConnectSavedGateway(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => OnConnectSavedGatewayAsync(sender),
            new OpenClawTray.AppLogger(),
            nameof(OnConnectSavedGateway));

    private async Task OnConnectSavedGatewayAsync(object sender)
    {
        if (sender is not Button btn || btn.Tag is not string gwId) return;
        if (_gatewayRegistry == null || _connectionManager == null) return;
        btn.IsEnabled = false;
        try
        {
            _gatewayRegistry.SetActive(gwId);
            _userIntent = UserIntent.None;
            LoadSavedGateways();
            RefreshFromSnapshot(_lastSnapshot);
            // Await the switch so any failure surfaces in the strip via the
            // catch below rather than becoming a silent unobserved task
            // exception. The state-change events that drive the rest of the
            // UI continue to fire while this awaits.
            await _connectionManager.SwitchGatewayAsync(gwId);
        }
        catch (Exception ex)
        {
            // Strip status will read the snapshot's terminal state next tick;
            // surface the immediate error in the single top connection banner so
            // the user gets feedback (and an "Open Connection" action) even if
            // the snapshot is briefly silent.
            try
            {
                CurrentApp.ShowTransientConnectionError(ex.Message);
            }
            catch (Exception uiEx)
            {
                Logger.Warn($"ConnectionPage: Failed to surface connect failure in connection banner: {uiEx.Message}");
            }
        }
        finally
        {
            try { btn.IsEnabled = true; }
            catch (Exception uiEx) { Logger.Debug($"ConnectionPage: Failed to re-enable connect button; control may be detached: {uiEx.Message}"); }
        }
    }

    private void OnSavedRowOpenDashboard(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string gwId) return;
        var rec = _gatewayRegistry?.GetById(gwId);
        if (rec == null) return;
        try
        {
            var url = GatewayDashboardUrlBuilder.Build(
                rec.Url,
                path: null,
                rec.SharedGatewayToken,
                appendSharedGatewayToken: !string.IsNullOrWhiteSpace(rec.SharedGatewayToken));
            _ = global::Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception ex)
        {
            Services.Logger.Warn($"[ConnectionPage] Failed to open saved gateway dashboard: {ex.Message}");
        }
    }

    private void OnSavedRowEdit(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string gwId) return;
        var rec = _gatewayRegistry?.GetById(gwId);
        if (rec == null) return;
        // Pre-fill the Direct pane for editing
        _editingGatewayId = gwId;
        _userIntent = UserIntent.AddingGateway;
        DirectUrlBox.Text = rec.Url;
        DirectTokenBox.Text = rec.SharedGatewayToken ?? "";
        DirectNameBox.Text = rec.FriendlyName ?? "";
        if (rec.SshTunnel != null)
        {
            AddSshExpander.IsExpanded = true;
            AddSshUserBox.Text = rec.SshTunnel.User;
            AddSshHostBox.Text = rec.SshTunnel.Host;
            AddSshServerPortBox.Text = rec.SshTunnel.SshPort.ToString();
            AddSshRemotePortBox.Text = rec.SshTunnel.RemotePort.ToString();
            AddSshLocalPortBox.Text = rec.SshTunnel.LocalPort.ToString();
        }
        ShowAddPane("direct");
        AddDirectItem.IsSelected = true;
        RefreshFromSnapshot(_lastSnapshot);
    }

    private void OnSavedRowRemove(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            () => OnSavedRowRemoveAsync(sender),
            new OpenClawTray.AppLogger(),
            nameof(OnSavedRowRemove));

    private async Task OnSavedRowRemoveAsync(object sender)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string gwId) return;
        var rec = _gatewayRegistry?.GetById(gwId);
        if (rec == null) return;
        var dialog = new ContentDialog
        {
            Title = LocalizationHelper.GetString("ConnectionPage_RemoveGatewayTitle"),
            Content = string.Format(LocalizationHelper.GetString("ConnectionPage_RemoveGatewayMessage"), rec.FriendlyName ?? rec.Url),
            PrimaryButtonText = LocalizationHelper.GetString("ConnectionPage_Remove"),
            CloseButtonText = LocalizationHelper.GetString("ConnectionPage_CancelAction"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            // If the user is removing the currently-active gateway, tear
            // down the live connection first - otherwise the connection
            // manager keeps trying to talk to a record that no longer
            // exists in the registry, which leaves the UI in a stale state.
            var wasActive = string.Equals(_gatewayRegistry?.ActiveGatewayId, gwId, StringComparison.Ordinal);
            if (wasActive && _connectionManager != null)
            {
                try { await _connectionManager.DisconnectAsync(); }
                catch (Exception ex) { Logger.Warn($"ConnectionPage: Failed to disconnect active gateway before removal: {ex.Message}"); }
            }
            _gatewayRegistry?.Remove(gwId);
            _gatewayRegistry?.Save();
            LoadSavedGateways();
            RefreshFromSnapshot(_lastSnapshot);
        }
    }

    // ─── Add gateway: Save ────────────────────────────────────────────

    // ─── Auto-test connectivity (debounced) ────────────────────────────

    private System.Threading.CancellationTokenSource? _connectivityTestCts;

    private void OnDirectInputChanged(object sender, RoutedEventArgs e)
    {
        var url = DirectUrlBox.Text?.Trim();
        ScheduleConnectivityTest(url);
        AutoFillTokenForUrl(url);
        UpdateRemoteSetupAdvice();
    }

    private void OnDirectUrlLostFocus(object sender, RoutedEventArgs e)
    {
        var url = DirectUrlBox.Text?.Trim();
        ScheduleConnectivityTest(url);
        // On focus-out, overwrite token even if already populated (user finished editing URL).
        AutoFillTokenForUrl(url, force: true);
        UpdateRemoteSetupAdvice();
    }

    private void AutoFillTokenForUrl(string? url, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(url) || (!force && !string.IsNullOrEmpty(DirectTokenBox.Text)))
            return;
        var match = _gatewayRegistry?.GetAll().FirstOrDefault(g =>
            string.Equals(g.Url?.TrimEnd('/'), url!.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(match?.SharedGatewayToken))
            DirectTokenBox.Text = match!.SharedGatewayToken;
        else if (force)
            DirectTokenBox.Text = "";
    }

    private void ScheduleConnectivityTest(string? rawUrl)
    {
        _connectivityTestCts?.Cancel();
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            AddTestResultText.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            return;
        }
        _connectivityTestCts = new System.Threading.CancellationTokenSource();
        var token = _connectivityTestCts.Token;
        // Debounce 600ms so we don't hit the network on every keystroke
        _ = RunConnectivityTestAsync(rawUrl, token, delay: 600);
    }

    private async Task RunConnectivityTestAsync(string rawUrl, System.Threading.CancellationToken ct, int delay = 0)
    {
        if (delay > 0)
        {
            await Task.Delay(delay, ct);
            if (ct.IsCancellationRequested) return;
        }

        AddTestResultText.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        AddTestResultText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        AddTestResultText.Text = LocalizationHelper.GetString("ConnectionPage_TestingConnection");

        try
        {
            var url = GatewayUrlHelper.NormalizeForWebSocket(rawUrl);
            var httpUrl = url.Replace("ws://", "http://").Replace("wss://", "https://").TrimEnd('/');

            using var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            System.Net.Http.HttpResponseMessage? response = null;
            Exception? firstProbeError = null;
            try { response = await httpClient.GetAsync(httpUrl, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                firstProbeError = ex;
                Logger.Warn($"ConnectionPage: Gateway connectivity probe failed for {GatewayUrlHelper.SanitizeForDisplay(httpUrl)}: {ex.Message}");
            }

            if (ct.IsCancellationRequested) return;

            if (response == null || !response.IsSuccessStatusCode)
            {
                var healthUrl = $"{httpUrl}/health";
                try { response = await httpClient.GetAsync(healthUrl, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Logger.Warn($"ConnectionPage: Gateway /health connectivity probe failed for {GatewayUrlHelper.SanitizeForDisplay(httpUrl)}: {ex.Message}");
                    firstProbeError ??= ex;
                }
            }

            if (ct.IsCancellationRequested) return;

            if (response != null && response.IsSuccessStatusCode)
            {
                AddTestResultText.Text = $"✓ {LocalizationHelper.GetString("ConnectionPage_GatewayReachable")}";
                AddTestResultText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
            }
            else if (response != null)
            {
                AddTestResultText.Text = $"⚠ {string.Format(LocalizationHelper.GetString("ConnectionPage_GatewayRespondedWith"), (int)response.StatusCode)}";
                AddTestResultText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
            }
            else
            {
                AddTestResultText.Text = $"✗ {LocalizationHelper.GetString("ConnectionPage_CannotReachGateway")}";
                AddTestResultText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            }
        }
        // slopwatch-ignore: SW003 Shutdown cancellation or disposal is expected and the caller already preserves the safe state.
        catch (OperationCanceledException) { /* debounce or page nav */ }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                Logger.Warn($"ConnectionPage: Gateway connectivity test failed for {GatewayUrlHelper.SanitizeForDisplay(rawUrl)}: {ex.Message}");
                AddTestResultText.Text = "✗ Unable to test gateway connection. Check the URL and try again.";
                AddTestResultText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            }
        }
    }

    private void OnAddSave(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            OnAddSaveAsync,
            new OpenClawTray.AppLogger(),
            nameof(OnAddSave));

    private async Task OnAddSaveAsync()
    {
        var tag = ActiveAddPaneTag();
        AddResultText.Text = "";
        try
        {
            switch (tag)
            {
                case "direct":  await DoDirectConnectFromAddFormAsync(); break;
                case "setup":   await DoApplySetupCodeFromAddFormAsync(); break;
                case "scan":    AddResultText.Text = LocalizationHelper.GetString("ConnectionPage_PickDiscoveredGateway"); break;
            }
        }
        catch (Exception ex)
        {
            AddResultText.Text = $"✗ {ex.Message}";
        }
    }

    /// <summary>
    /// Direct connect - adapted from the legacy OnDirectConnect handler.
    /// Identical semantics: validate, snapshot for rollback, AddOrUpdate +
    /// SetActive in the registry, ClearStoredTokens for the identity, save
    /// settings, kick the connection manager and wait for a terminal state.
    /// Per-gateway SSH is built from the AddSsh* fields (when expander expanded).
    /// </summary>
    private async Task DoDirectConnectFromAddFormAsync()
    {
        if (_connectionManager == null || _gatewayRegistry == null) return;

        var url = DirectUrlBox.Text?.Trim();
        var token = DirectTokenBox.Text?.Trim();
        var friendly = DirectNameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            AddResultText.Text = LocalizationHelper.GetString("ConnectionPage_EnterGatewayUrl");
            return;
        }

        url = GatewayUrlHelper.NormalizeForWebSocket(url);

        if (!TryBuildAddSshTunnelConfig(out var sshConfig, out var sshError))
        {
            AddResultText.Text = sshError;
            return;
        }
        bool useSsh = sshConfig != null;

        AddSaveButton.IsEnabled = false;
        AddResultText.Text = LocalizationHelper.GetString("ConnectionPage_Connecting");

        // Snapshot previous state for rollback (mirrors legacy logic exactly)
        var previousActiveId = _gatewayRegistry.ActiveGatewayId;
        var previousSettings = CurrentApp.Settings;
        var prevGatewayUrl = previousSettings?.GatewayUrl;
        var prevUseSsh = previousSettings?.UseSshTunnel ?? false;
        var prevSshUser = previousSettings?.SshTunnelUser;
        var prevSshHost = previousSettings?.SshTunnelHost;
        var prevSshPort = previousSettings?.SshTunnelSshPort ?? 22;
        var prevSshRemotePort = previousSettings?.SshTunnelRemotePort ?? 0;
        var prevSshLocalPort = previousSettings?.SshTunnelLocalPort ?? 0;

        // Resolve which record we're operating on:
        //   1. If the user opened the form via Edit on a saved row, prefer
        //      the original record id - this lets a URL change *update* the
        //      existing record instead of orphaning it as a duplicate.
        //   2. Otherwise look up by URL (typical "user typed a URL" flow).
        //   3. Otherwise it's brand new.
        var existing = _editingGatewayId != null
            ? _gatewayRegistry.GetById(_editingGatewayId) ?? _gatewayRegistry.FindByUrl(url)
            : _gatewayRegistry.FindByUrl(url);
        var isNewRecord = existing == null;
        var existingRecordSnapshot = existing;
        var recordId = existing?.Id ?? Guid.NewGuid().ToString();

        // Hoisted out of the try block so the catch handler can pass the
        // backup to RollbackDirectConnect for credential restore.
        // identityBackupSentinel = file size + last-write-time captured at
        // backup time. Rollback uses it to skip the restore if the file was
        // touched in the meantime (e.g. successful late pairing wrote a new
        // valid token while the connect attempt was still failing).
        string? identityKeyPath = null;
        string? identityBackup = null;
        long identityBackupLength = -1;
        DateTime identityBackupMtimeUtc = DateTime.MinValue;
        bool identityCleared = false;

        try
        {
            await _connectionManager.DisconnectAsync();

            var record = new GatewayRecord
            {
                Id = recordId,
                Url = url,
                FriendlyName = string.IsNullOrWhiteSpace(friendly) ? existing?.FriendlyName : friendly,
                SharedGatewayToken = string.IsNullOrWhiteSpace(token) ? null : token,
                BootstrapToken = null,
                SshTunnel = sshConfig,
                LastConnected = existing?.LastConnected,
            }.PreserveAdvancedFields(existing); // keep per-gateway BrowserControlPort across edits
            _gatewayRegistry.AddOrUpdate(record);
            _gatewayRegistry.SetActive(recordId);
            _gatewayRegistry.Save();

            // Identity-token handling.
            //   - When the user provides a NEW shared token, the previous
            //     device token is no longer trusted by the gateway, so we
            //     clear the stored device tokens to force a fresh re-pair.
            //   - When the form is left blank (user is just renaming /
            //     fixing SSH), keep the existing device tokens - clearing
            //     them would silently force a re-pair the user didn't ask
            //     for and would violate the "never downgrade a paired
            //     device" architecture rule.
            //   - When we DO clear, snapshot the identity JSON first so
            //     RollbackDirectConnect can restore the user's credentials
            //     if the connection then fails.
            var identityDir = _gatewayRegistry.GetIdentityDirectory(recordId);
            identityKeyPath = Path.Combine(identityDir, "device-key-ed25519.json");
            try
            {
                if (File.Exists(identityKeyPath))
                {
                    identityBackup = File.ReadAllText(identityKeyPath);
                    var info = new FileInfo(identityKeyPath);
                    identityBackupLength = info.Length;
                    identityBackupMtimeUtc = info.LastWriteTimeUtc;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"ConnectionPage: Failed to snapshot gateway identity before direct connect; rollback will skip restore: {ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(token))
            {
                DeviceIdentityStore.ClearStoredTokens(identityDir);
                identityCleared = true;
            }

            if (previousSettings != null)
            {
                previousSettings.GatewayUrl = url;
                previousSettings.UseSshTunnel = useSsh;
                if (useSsh && sshConfig != null)
                {
                    previousSettings.SshTunnelUser = sshConfig.User;
                    previousSettings.SshTunnelHost = sshConfig.Host;
                    previousSettings.SshTunnelSshPort = sshConfig.SshPort;
                    previousSettings.SshTunnelRemotePort = sshConfig.RemotePort;
                    previousSettings.SshTunnelLocalPort = sshConfig.LocalPort;
                }
                previousSettings.Save();
            }

            if (useSsh)
            {
                AddResultText.Text = LocalizationHelper.GetString("ConnectionPage_StartingSshTunnel");
                var app = (App)Microsoft.UI.Xaml.Application.Current;
                app.EnsureSshTunnelStarted();
            }

            var snapshot = await ConnectAndWaitForDirectConnectOutcomeAsync(recordId);
            AddResultText.Text = snapshot.OperatorState == RoleConnectionState.PairingRequired
                ? string.Format(LocalizationHelper.GetString("ConnectionPage_PairingApprovalRequired"), GatewayUrlHelper.SanitizeForDisplay(url))
                : string.Format(LocalizationHelper.GetString("ConnectionPage_ConnectedTo"), GatewayUrlHelper.SanitizeForDisplay(url));

            // Success - leave Add mode and stop tracking the edited record.
            _editingGatewayId = null;
            _userIntent = UserIntent.None;
            LoadSavedGateways();
            RefreshFromSnapshot(_lastSnapshot);
        }
        catch (Exception ex)
        {
            AddResultText.Text = $"✗ {ex.Message}";
            RollbackDirectConnect(previousActiveId, isNewRecord, recordId, existingRecordSnapshot,
                previousSettings, prevGatewayUrl, prevUseSsh, prevSshUser, prevSshHost,
                prevSshPort, prevSshRemotePort, prevSshLocalPort,
                identityCleared ? identityKeyPath : null,
                identityCleared ? identityBackup : null,
                identityCleared ? identityBackupLength : -1,
                identityCleared ? identityBackupMtimeUtc : DateTime.MinValue);
        }
        finally
        {
            AddSaveButton.IsEnabled = true;
        }
    }

    private async Task<GatewayConnectionSnapshot> ConnectAndWaitForDirectConnectOutcomeAsync(string recordId)
    {
        if (_connectionManager == null)
            throw new InvalidOperationException("Connection manager is not available.");

        var completion = new TaskCompletionSource<GatewayConnectionSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStateChanged(object? sender, GatewayConnectionSnapshot snapshot)
        {
            if (!string.Equals(snapshot.GatewayId, recordId, StringComparison.Ordinal))
                return;
            if (IsDirectConnectTerminal(snapshot))
                completion.TrySetResult(snapshot);
        }

        _connectionManager.StateChanged += OnStateChanged;
        try
        {
            await _connectionManager.ConnectAsync(recordId);

            var current = _connectionManager.CurrentSnapshot;
            if (string.Equals(current.GatewayId, recordId, StringComparison.Ordinal) &&
                IsDirectConnectTerminal(current))
            {
                return EnsureDirectConnectSucceeded(current);
            }

            var completed = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            if (completed != completion.Task)
                throw new TimeoutException(LocalizationHelper.GetString("ConnectionPage_ConnectionTimeout"));

            return EnsureDirectConnectSucceeded(await completion.Task);
        }
        finally
        {
            _connectionManager.StateChanged -= OnStateChanged;
        }
    }

    private static bool IsDirectConnectTerminal(GatewayConnectionSnapshot snapshot) =>
        snapshot.OverallState is OverallConnectionState.Connected
            or OverallConnectionState.Ready
            or OverallConnectionState.Degraded ||
        snapshot.OperatorState is RoleConnectionState.PairingRequired
            or RoleConnectionState.Error;

    private bool TryBuildAddSshTunnelConfig(out SshTunnelConfig? sshConfig, out string? error)
    {
        sshConfig = null;
        error = null;

        if (!AddSshExpander.IsExpanded ||
            string.IsNullOrWhiteSpace(AddSshUserBox.Text) ||
            string.IsNullOrWhiteSpace(AddSshHostBox.Text))
        {
            return true;
        }

        var sshUser = AddSshUserBox.Text.Trim();
        var sshHost = AddSshHostBox.Text.Trim();
        var sshPortText = string.IsNullOrWhiteSpace(AddSshServerPortBox.Text) ? "22" : AddSshServerPortBox.Text;
        if (!int.TryParse(sshPortText, out var sshPort) || sshPort is < 1 or > 65535)
        {
            error = LocalizationHelper.GetString("ConnectionPage_SshServerPortInvalid");
            return false;
        }
        if (!int.TryParse(AddSshRemotePortBox.Text, out var remotePort) || remotePort is < 1 or > 65535)
        {
            error = LocalizationHelper.GetString("ConnectionPage_SshRemotePortInvalid");
            return false;
        }
        if (!int.TryParse(AddSshLocalPortBox.Text, out var localPort) || localPort is < 1 or > 65535)
        {
            error = LocalizationHelper.GetString("ConnectionPage_SshLocalPortInvalid");
            return false;
        }

        var includeBrowserProxyForward = BrowserProxySshTunnelForwardPolicy.ShouldInclude(
            CurrentApp.Settings.NodeBrowserProxyEnabled,
            remotePort,
            localPort);
        sshConfig = new SshTunnelConfig(
            sshUser,
            sshHost,
            remotePort,
            localPort,
            IncludeBrowserProxyForward: includeBrowserProxyForward,
            SshPort: sshPort);
        return true;
    }

    private static GatewayConnectionSnapshot EnsureDirectConnectSucceeded(GatewayConnectionSnapshot snapshot)
    {
        if (snapshot.OperatorState == RoleConnectionState.Error)
        {
            var message = snapshot.OperatorError ?? snapshot.NodeError ?? LocalizationHelper.GetString("ConnectionPage_GatewayConnectionFailed");
            throw new InvalidOperationException(message);
        }
        return snapshot;
    }

    private void RollbackDirectConnect(
        string? previousActiveId, bool isNewRecord, string recordId,
        GatewayRecord? existingRecordSnapshot, SettingsManager? settings,
        string? prevGatewayUrl, bool prevUseSsh, string? prevSshUser,
        string? prevSshHost, int prevSshPort, int prevSshRemotePort, int prevSshLocalPort,
        string? identityKeyPath = null, string? identityBackup = null,
        long identityBackupLength = -1, DateTime identityBackupMtimeUtc = default)
    {
        if (_gatewayRegistry == null) return;

        if (isNewRecord)
            _gatewayRegistry.Remove(recordId);
        else if (existingRecordSnapshot != null)
            _gatewayRegistry.AddOrUpdate(existingRecordSnapshot);

        if (previousActiveId != null)
            _gatewayRegistry.SetActive(previousActiveId);
        _gatewayRegistry.Save();

        // Restore the device-token JSON we cleared at the top of
        // DoDirectConnectFromAddFormAsync. Without this, a failed direct
        // connect after the user had typed a (possibly wrong) shared token
        // would permanently destroy the device token earned during the
        // last successful pairing - forcing a full re-pair the user never
        // asked for. Skip the restore if the file changed since backup
        // (e.g. a late-arriving successful pairing wrote a fresh token in
        // the meantime - that token is more valuable than our backup).
        if (!string.IsNullOrEmpty(identityKeyPath) && identityBackup != null)
        {
            try
            {
                bool fileUnchanged = false;
                if (File.Exists(identityKeyPath))
                {
                    var info = new FileInfo(identityKeyPath);
                    fileUnchanged = info.Length == identityBackupLength
                                    && info.LastWriteTimeUtc == identityBackupMtimeUtc;
                }
                else
                {
                    // ClearStoredTokens may have rewritten the file with a
                    // smaller body - that's the expected post-clear state,
                    // so treat as unchanged-from-clear and restore.
                    fileUnchanged = true;
                }
                if (fileUnchanged)
                    DeviceIdentity.AtomicWriteKeyFileRaw(identityKeyPath, identityBackup);
                // else: another writer touched the file; preserve it.
            }
            catch (Exception ex)
            {
                Logger.Warn($"ConnectionPage: Failed to restore gateway identity after direct connect rollback: {ex.Message}");
            }
        }

        if (settings != null)
        {
            settings.GatewayUrl = prevGatewayUrl ?? string.Empty;
            settings.UseSshTunnel = prevUseSsh;
            settings.SshTunnelUser = prevSshUser ?? string.Empty;
            settings.SshTunnelHost = prevSshHost ?? string.Empty;
            settings.SshTunnelSshPort = prevSshPort;
            settings.SshTunnelRemotePort = prevSshRemotePort;
            settings.SshTunnelLocalPort = prevSshLocalPort;
            settings.Save();
        }
    }

    private async Task DoApplySetupCodeFromAddFormAsync()
    {
        var code = AddSetupCodeBox.Text?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            AddResultText.Text = LocalizationHelper.GetString("ConnectionPage_PleaseEnterSetupCode");
            return;
        }
        if (!TryBuildAddSshTunnelConfig(out var sshConfig, out var sshError))
        {
            AddResultText.Text = sshError;
            return;
        }

        AddSaveButton.IsEnabled = false;
        AddResultText.Text = LocalizationHelper.GetString("ConnectionPage_Applying");
        try
        {
            if (_connectionManager != null)
            {
                var result = await _connectionManager.ApplySetupCodeAsync(code, sshConfig);
                AddResultText.Text = result.Outcome switch
                {
                    SetupCodeOutcome.Success => $"✓ {string.Format(LocalizationHelper.GetString("ConnectionPage_AppliedGateway"), SanitizeUrl(result.GatewayUrl ?? ""))}",
                    SetupCodeOutcome.InvalidCode => $"✗ {result.ErrorMessage ?? LocalizationHelper.GetString("ConnectionPage_InvalidSetupCode")}",
                    SetupCodeOutcome.InvalidUrl => $"✗ {result.ErrorMessage ?? LocalizationHelper.GetString("ConnectionPage_InvalidUrl")}",
                    SetupCodeOutcome.ConnectionFailed => $"✗ {result.ErrorMessage ?? LocalizationHelper.GetString("ConnectionPage_ConnectionFailed")}",
                    _ => $"✗ {result.ErrorMessage ?? LocalizationHelper.GetString("ConnectionPage_UnknownError")}",
                };
                if (result.Outcome == SetupCodeOutcome.Success)
                {
                    _editingGatewayId = null;
                    _userIntent = UserIntent.None;
                    LoadSavedGateways();
                    RefreshFromSnapshot(_lastSnapshot);
                }
            }
            else
            {
                var decoded = SetupCodeDecoder.Decode(code);
                if (!decoded.Success)
                {
                    AddResultText.Text = $"✗ {decoded.Error}";
                    return;
                }
                var settings = CurrentApp.Settings;
                if (settings == null) return;
                if (!string.IsNullOrEmpty(decoded.Url))
                    settings.GatewayUrl = decoded.Url;
                settings.Save();
                AddResultText.Text = $"✓ {string.Format(LocalizationHelper.GetString("ConnectionPage_AppliedGateway"), SanitizeUrl(decoded.Url ?? settings.GatewayUrl ?? ""))}";
                ((IAppCommands)CurrentApp).NotifySettingsSaved();
            }
        }
        finally
        {
            AddSaveButton.IsEnabled = true;
        }
    }

    private void OnAddSetupCodeDecode(object sender, RoutedEventArgs e)
    {
        OnSetupCodeTextChanged(AddSetupCodeBox, null!);
    }

    private void OnSetupCodeTextChanged(object sender, TextChangedEventArgs? e)
    {
        var code = AddSetupCodeBox.Text?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length < 10)
        {
            AddSetupCodePreviewPanel.Visibility = Visibility.Collapsed;
            _lastDecodedSetupUrl = null;
            UpdateRemoteSetupAdvice();
            return;
        }
        var decoded = SetupCodeDecoder.Decode(code);
        if (decoded.Success)
        {
            AddSetupCodePreviewUrl.Text = string.Format(LocalizationHelper.GetString("ConnectionPage_PreviewGateway"), decoded.Url ?? LocalizationHelper.GetString("ConnectionPage_PreviewGatewayNotSpecified"));
            var tokenHint = string.IsNullOrEmpty(decoded.Token)
                ? LocalizationHelper.GetString("ConnectionPage_PreviewNoToken")
                : decoded.Token!.Substring(0, Math.Min(8, decoded.Token.Length)) + "…";
            AddSetupCodePreviewToken.Text = string.Format(LocalizationHelper.GetString("ConnectionPage_PreviewToken"), tokenHint);
            AddSetupCodePreviewPanel.Visibility = Visibility.Visible;
            // Auto-test connectivity with the decoded URL
            if (!string.IsNullOrEmpty(decoded.Url))
                ScheduleConnectivityTest(decoded.Url);
            // Warn if the decoded URL would send the bootstrap token in cleartext.
            _lastDecodedSetupUrl = decoded.Url;
            UpdateRemoteSetupAdvice();
        }
        else
        {
            AddSetupCodePreviewPanel.Visibility = Visibility.Collapsed;
            _lastDecodedSetupUrl = null;
            UpdateRemoteSetupAdvice();
        }
    }

    // ─── Scan flow ───────────────────────────────────────────────────

    private bool _scanInProgress;

    /// <summary>
    /// Click handler for both the Gateways-section Scan button and the
    /// Welcome inline Scan button. Toggles scan on/off; on completion
    /// populates whichever discovered-list panel is visible.
    /// </summary>
    private void OnScanGatewaysClicked(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            OnScanGatewaysClickedAsync,
            new OpenClawTray.AppLogger(),
            nameof(OnScanGatewaysClicked));

    private async Task OnScanGatewaysClickedAsync()
    {
        if (_scanInProgress)
        {
            _discoveryService?.Stop();
            return;
        }
        // Set the guard synchronously so a fast double-click cannot spawn
        // a parallel scan. RunScanForGatewaysAsync resets it in finally.
        _scanInProgress = true;
        await RunScanForGatewaysAsync();
    }

    /// <summary>
    /// Legacy entry point - kept for the now-inert Add form Scan-pane
    /// "Start scan" button. Routes to the canonical scan flow.
    /// </summary>
    private void OnAddScanStart(object sender, RoutedEventArgs e)
    {
        _ = RunScanForGatewaysAsync();
    }

    /// <summary>
    /// Run mDNS / LAN discovery once and populate the Gateways-section
    /// "Discovered" list (and the Welcome panel's mirror, if visible).
    /// Scan button toggles between Scan / Stop while in progress.
    /// </summary>
    private async Task RunScanForGatewaysAsync()
    {
        _discoveryService ??= new GatewayDiscoveryService();
        _scanInProgress = true;

        // Reflect "scanning" state on both buttons + sub-section
        GatewaysScanButtonText.Text = LocalizationHelper.GetString("ConnectionPage_Stop");
        WelcomeScanButtonText.Text = LocalizationHelper.GetString("ConnectionPage_Stop");
        GatewaysDiscoveredSection.Visibility = Visibility.Visible;
        GatewaysDiscoveredHeader.Text = LocalizationHelper.GetString("ConnectionPage_ScanningNetwork");
        GatewaysDiscoveredProgress.IsActive = true;
        GatewaysDiscoveredProgress.Visibility = Visibility.Visible;
        GatewaysDiscoveredList.Children.Clear();
        WelcomeDiscoveredSection.Visibility = Visibility.Visible;
        WelcomeDiscoveredList.Children.Clear();

        try
        {
            await _discoveryService.StartDiscoveryAsync();
            var gateways = _discoveryService.Gateways;
            GatewaysDiscoveredHeader.Text = gateways.Count == 0
                ? LocalizationHelper.GetString("ConnectionPage_NoGatewaysFound")
                : string.Format(LocalizationHelper.GetString("ConnectionPage_DiscoveredOnNetwork"), gateways.Count);
            GatewaysDiscoveredProgress.IsActive = false;
            GatewaysDiscoveredProgress.Visibility = Visibility.Collapsed;

            foreach (var gw in gateways)
            {
                GatewaysDiscoveredList.Children.Add(BuildDiscoveredRow(gw));
                WelcomeDiscoveredList.Children.Add(BuildDiscoveredRow(gw));
            }
        }
        catch (Exception ex)
        {
            GatewaysDiscoveredHeader.Text = string.Format(LocalizationHelper.GetString("ConnectionPage_ScanFailed"), ex.Message);
            GatewaysDiscoveredProgress.IsActive = false;
            GatewaysDiscoveredProgress.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _scanInProgress = false;
            GatewaysScanButtonText.Text = LocalizationHelper.GetString("ConnectionPage_ScanAction");
            WelcomeScanButtonText.Text = LocalizationHelper.GetString("ConnectionPage_ScanAction");
        }
    }

    /// <summary>
    /// Per-discovered-gateway row. Tapping [Add] pre-fills the Direct pane
    /// and switches into AddGateway mode; user types/pastes a token there.
    /// </summary>
    private Border BuildDiscoveredRow(DiscoveredGateway gw)
    {
        var card = new Border
        {
            Background = ResolveBrush("CardBackgroundFillColorSecondaryBrush"),
            BorderBrush = ResolveBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 8, 8),
            Margin = new Thickness(0, 0, 0, 0),
        };
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = Helpers.FluentIconCatalog.System, // PC1 - discovered, not yet connected
            FontSize = 14,
            Foreground = ResolveBrush("TextFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        info.Children.Add(new TextBlock
        {
            Text = !string.IsNullOrEmpty(gw.DisplayName) ? gw.DisplayName : (gw.Host ?? "gateway"),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        info.Children.Add(new TextBlock
        {
            Text = gw.ConnectionUrl,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var addBtn = new Button { Content = LocalizationHelper.GetString("ConnectionPage_Add"), Tag = gw.ConnectionUrl, VerticalAlignment = VerticalAlignment.Center };
        addBtn.Click += OnAddDiscoveredGateway;
        Grid.SetColumn(addBtn, 2);
        grid.Children.Add(addBtn);

        card.Child = grid;
        return card;
    }

    private void OnAddDiscoveredGateway(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string url) return;
        // Pre-fill the Direct pane and enter AddGateway mode. User types
        // the token there and clicks Save & connect.
        _editingGatewayId = null;
        DirectUrlBox.Text = url;
        DirectTokenBox.Text = "";
        DirectNameBox.Text = "";
        AddDirectItem.IsSelected = true;
        ShowAddPane("direct");
        _userIntent = UserIntent.AddingGateway;
        RefreshFromSnapshot(_lastSnapshot);
    }

    // Legacy [Use this] handler - replaced by [Add] above. Kept inert in
    // case any legacy XAML still binds it.
    private void OnScanResultUseThis(object sender, RoutedEventArgs e)
        => OnAddDiscoveredGateway(sender, e);

    // ─── Connection lifecycle handlers (preserved verbatim semantics) ─

    private void OnDisconnect(object sender, RoutedEventArgs e)
    {
        ((IAppCommands)CurrentApp).Disconnect();
    }

    private void OnReconnectFromRecovery(object sender, RoutedEventArgs e)
    {
        ((IAppCommands)CurrentApp).Reconnect();
    }

    /// <summary>
    /// The gateway card's connection toggle. ON ↔ OFF mirrors the active
    /// gateway's connection state. Tapping OFF disconnects; tapping ON
    /// (after a disconnect) reconnects to the still-active record.
    /// </summary>
    private void OnConnectionToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressConnectionToggle) return;
        if (ConnectionToggle.IsOn)
        {
            // User asked to reconnect.
            ((IAppCommands)CurrentApp).Reconnect();
        }
        else
        {
            // User asked to disconnect.
            ((IAppCommands)CurrentApp).Disconnect();
        }
    }

    private void OnNodeModeToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressNodeModeToggle) return;
        var settings = CurrentApp.Settings;
        if (settings == null) return;
        settings.EnableNodeMode = NodeModeToggle.IsOn;
        settings.Save();
        // Toggling Node mode forces a full reconnect of the gateway WS so
        // the role change registers; mask the brief transient window so the
        // gateway/operator visuals don't flicker through "Disconnected".
        BeginReconnectMask();
        ((IAppCommands)CurrentApp).NotifySettingsSaved();
        RefreshFromSnapshot(_lastSnapshot);
    }

    private static bool IsStableState(OverallConnectionState s) =>
        s is OverallConnectionState.Connected
          or OverallConnectionState.Ready
          or OverallConnectionState.Degraded;

    private static bool IsTransientState(OverallConnectionState s) =>
        s is OverallConnectionState.Disconnecting
          or OverallConnectionState.Idle
          or OverallConnectionState.Connecting;

    private void BeginReconnectMask()
    {
        // 8s ceiling: typical Node-toggle reconnects on a healthy WAN settle
        // in 1-3s; this gives slow networks headroom without leaving the UI
        // showing a stale "connected" state forever if the reconnect fails.
        _suppressReconnectVisualsUntilUtc = DateTime.UtcNow.AddSeconds(8);
        // Reset the "transient observed" flag for this arming cycle so the
        // stable-branch in RefreshFromSnapshot waits for a real transient
        // snapshot before dropping the mask.
        _maskHasObservedTransient = false;
        if (_reconnectMaskTimer == null)
        {
            _reconnectMaskTimer = new Microsoft.UI.Xaml.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(8),
            };
            _reconnectMaskTimer.Tick += OnReconnectMaskTimeout;
        }
        _reconnectMaskTimer.Stop();
        _reconnectMaskTimer.Start();
    }

    private void OnReconnectMaskTimeout(object? sender, object e)
    {
        _reconnectMaskTimer?.Stop();
        _suppressReconnectVisualsUntilUtc = DateTime.MinValue;
        _maskHasObservedTransient = false;
        if (_connectionManager != null)
            RefreshFromSnapshot(_connectionManager.CurrentSnapshot);
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        try
        {
            switch (e.PropertyName)
            {
                case nameof(AppState.Status):
                    var snapshot = _connectionManager?.CurrentSnapshot ?? GatewayConnectionSnapshot.Idle;
                    _lastSnapshot = snapshot;
                    RefreshFromSnapshot(snapshot);
                    break;
                case nameof(AppState.Nodes):
                    RefreshFromSnapshot(_connectionManager?.CurrentSnapshot ?? _lastSnapshot);
                    break;
                case nameof(AppState.NodePairList):
                    if (_appState?.NodePairList != null) UpdatePairingRequests(_appState.NodePairList);
                    break;
                case nameof(AppState.DevicePairList):
                    if (_appState?.DevicePairList != null) UpdateDevicePairingRequests(_appState.DevicePairList);
                    break;
                case nameof(AppState.Channels):
                case nameof(AppState.UsageCost):
                case nameof(AppState.Sessions):
                    OnGlanceDataChanged();
                    break;
                case nameof(AppState.GatewaySelf):
                    OnGlanceDataChanged();
                    break;
            }
        }
        catch (Exception ex)
        {
            Services.Logger.Warn($"[ConnectionPage] OnAppStateChanged({e.PropertyName}) failed: {ex.Message}");
            throw;
        }
    }

    // ─── Pending pairing - populate the banner with existing semantics ─

    public void UpdateDevicePairingRequests(DevicePairingListInfo data)
    {
        DevicePairingListPanel.Children.Clear();
        var hasDevicePending = data.Pending.Count > 0;

        if (hasDevicePending)
        {
            var scopes = CurrentApp.GatewayClient?.GrantedOperatorScopes ?? (IReadOnlyList<string>)Array.Empty<string>();
            var canPair = OperatorScopeHelper.CanApproveDevices(scopes);
            // Build the set of fallback ids that collide across multiple
            // pending requests. When a legacy gateway omits RequestId we
            // fall back to DeviceId; if two pending requests share the
            // same DeviceId, approving from the UI would be ambiguous and
            // could act on the wrong one - disable those rows so the user
            // is forced to approve via the gateway-host CLI instead.
            var ambiguousIds = ComputeAmbiguousFallbackIds(
                data.Pending.Select(r => (RequestId: (string?)r.RequestId, FallbackId: (string?)r.DeviceId)));
            foreach (var req in data.Pending)
            {
                bool ambiguous = req.RequestId == null
                                 && !string.IsNullOrEmpty(req.DeviceId)
                                 && ambiguousIds.Contains(req.DeviceId!);
                DevicePairingListPanel.Children.Add(BuildDevicePairingCard(req, canPair && !ambiguous));
            }
        }
        UpdatePendingApprovalsVisibility();
    }

    public void UpdatePairingRequests(PairingListInfo data)
    {
        NodePairingListPanel.Children.Clear();
        var hasNodePending = data.Pending.Count > 0;

        if (hasNodePending)
        {
            var scopes = CurrentApp.GatewayClient?.GrantedOperatorScopes ?? (IReadOnlyList<string>)Array.Empty<string>();
            var canPair = OperatorScopeHelper.CanApproveDevices(scopes);
            // Same ambiguity guard as UpdateDevicePairingRequests above:
            // if a legacy gateway sends multiple node-pair requests with
            // the same NodeId and no RequestId, disable approve/deny on
            // those rows so the user can't pick the wrong target.
            var ambiguousIds = ComputeAmbiguousFallbackIds(
                data.Pending.Select(r => (RequestId: (string?)r.RequestId, FallbackId: (string?)r.NodeId)));
            foreach (var req in data.Pending)
            {
                bool ambiguous = req.RequestId == null
                                 && !string.IsNullOrEmpty(req.NodeId)
                                 && ambiguousIds.Contains(req.NodeId!);
                NodePairingListPanel.Children.Add(BuildNodePairingCard(req, canPair && !ambiguous));
            }
        }
        UpdatePendingApprovalsVisibility();
    }

    /// <summary>
    /// Returns the set of fallback ids (DeviceId / NodeId) that appear in
    /// 2+ pending requests where RequestId is missing - i.e. the cases
    /// where approve/deny via the fallback id would be ambiguous.
    /// </summary>
    private static HashSet<string> ComputeAmbiguousFallbackIds(IEnumerable<(string? RequestId, string? FallbackId)> rows)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (r.RequestId != null) continue;
            if (string.IsNullOrEmpty(r.FallbackId)) continue;
            counts[r.FallbackId!] = counts.TryGetValue(r.FallbackId!, out var n) ? n + 1 : 1;
        }
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in counts)
            if (kv.Value > 1) ambiguous.Add(kv.Key);
        return ambiguous;
    }

    private void UpdatePendingApprovalsVisibility()
    {
        int total = DevicePairingListPanel.Children.Count + NodePairingListPanel.Children.Count;
        if (total == 0)
        {
            PendingApprovalsBanner.Visibility = Visibility.Collapsed;
            return;
        }
        PendingApprovalsBanner.Visibility = Visibility.Visible;
        PendingApprovalsHeaderText.Text = total == 1
            ? LocalizationHelper.GetString("ConnectionPage_ApprovalsSingular")
            : string.Format(LocalizationHelper.GetString("ConnectionPage_ApprovalsPlural"), total);
    }

    /// <summary>
    /// Runs an Approve or Deny decision and manages the buttons' enabled
    /// state. Both buttons are disabled while the call is in flight; on
    /// failure or a missing gateway client they're re-enabled immediately.
    /// On success they stay disabled, expecting the gateway to push a
    /// list-updated event that rebuilds the panel (and drops this card).
    /// A 8-second watchdog re-enables the buttons if that event never
    /// arrives - without it, a dropped websocket frame would leave the
    /// row permanently inert and the user would have no way to retry.
    /// </summary>
    private async Task RunPairingDecisionAsync(
        Button approveBtn, Button rejectBtn, bool isApprove,
        Func<IOperatorGatewayClient, Task<bool>> action)
    {
        approveBtn.IsEnabled = false;
        rejectBtn.IsEnabled = false;
        bool successPath = false;
        try
        {
            var client = CurrentApp.GatewayClient;
            if (client == null) return; // finally re-enables
            var ok = await action(client);
            successPath = ok;
            // !ok falls into finally below - re-enable so user can retry.
        }
        catch (Exception ex)
        {
            // Finally re-enables. The pairing list refresh has its own
            // observable surface (gateway list-updated event), so there's
            // no clean place to surface a per-row error here - but log it.
            Services.Logger.Warn($"[ConnectionPage] Pairing row action failed: {ex.Message}");
        }
        finally
        {
            if (!successPath)
            {
                approveBtn.IsEnabled = true;
                rejectBtn.IsEnabled = true;
            }
            else
            {
                ArmPairingDecisionWatchdog(approveBtn, rejectBtn);
            }
        }
    }

    private void ArmPairingDecisionWatchdog(Button approveBtn, Button rejectBtn)
    {
        // 8 s matches the existing reconnect-mask budget at the top of the
        // file - the gateway's normal list-updated round-trip is < 1 s, so
        // arming at 8 s only fires when something has genuinely gone wrong
        // (websocket dropped, gateway crashed mid-approve, ...). The card
        // will be removed by RefreshFromSnapshot in the happy path, which
        // cancels this watchdog implicitly because the targets are no
        // longer parented.
        var timer = new Microsoft.UI.Xaml.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(8),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // If the row is still on screen (Parent != null), the gateway
            // didn't push a list update - re-enable so the user can retry
            // instead of being stuck with permanently disabled buttons.
            if (approveBtn.Parent != null)
            {
                approveBtn.IsEnabled = true;
                rejectBtn.IsEnabled = true;
            }
        };
        timer.Start();
    }

    /// <summary>
    /// Builds a per-row pairing decision button (Approve / Deny). The button
    /// stays a plain <see cref="Button"/> - accent style is intentionally
    /// avoided so we comply with the Fluent "one accent per view" rule when
    /// several pending requests are stacked. The affirmative or negative
    /// cue comes from a leading glyph painted in a theme brush
    /// (success / critical), which works correctly in light, dark, and
    /// high-contrast modes.
    /// </summary>
    private Button BuildPairingDecisionButton(string glyph, string glyphBrushKey, string label,
        string automationName, string automationId)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 12,
            Foreground = ResolveBrush(glyphBrushKey),
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var btn = new Button { Content = stack };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, automationName);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(btn, automationId);
        ToolTipService.SetToolTip(btn, label);
        return btn;
    }

    private Border BuildDevicePairingCard(DevicePairingRequest req, bool canPair)
    {
        var card = new Border
        {
            Background = ResolveBrush("CardBackgroundFillColorSecondaryBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
        };
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (canPair) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Spacing = 2 };
        info.Children.Add(new TextBlock
        {
            Text = string.Format(LocalizationHelper.GetString("ConnectionPage_DeviceLabel"), req.DisplayName ?? req.DeviceId),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        var detail = req.Platform ?? LocalizationHelper.GetString("ConnectionPage_UnknownPlatform");
        if (!string.IsNullOrEmpty(req.Role)) detail += $" · {req.Role}";
        info.Children.Add(new TextBlock
        {
            Text = detail,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
        });
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        if (canPair)
        {
            // Legacy gateways may not populate RequestId for device pairing
            // requests; UpdatePairingGuidance already falls back to DeviceId
            // for the user-facing copy. Apply the same fallback here so the
            // approve/deny click can call into the gateway client even on
            // legacy snapshots; if neither id is present, leave the buttons
            // disabled so the user isn't tricked into a no-op.
            // Per-row Approve/Deny: per Fluent guidance ("at most one accent
            // button per view"), don't paint the affirmative half accent -
            // when several pending requests are stacked, multiple accent
            // buttons compete and dilute the cue. The outer
            // PendingApprovalsBanner (caution-yellow, bold count, live
            // region) already carries the page-level attention. We signal
            // affirmative/negative per row via leading glyphs in success /
            // critical brushes instead of button chrome.
            var capturedId = req.RequestId ?? req.DeviceId;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var approveBtn = BuildPairingDecisionButton(
                glyph: Helpers.FluentIconCatalog.Check,
                glyphBrushKey: "SystemFillColorSuccessBrush",
                label: LocalizationHelper.GetString("ConnectionPage_Approve"),
                automationName: LocalizationHelper.GetString("ConnectionPage_ApprovePairingRequest"),
                automationId: "DevicePairApproveAction");
            var rejectBtn = BuildPairingDecisionButton(
                glyph: Helpers.FluentIconCatalog.Exit,
                glyphBrushKey: "SystemFillColorCriticalBrush",
                label: LocalizationHelper.GetString("ConnectionPage_Deny"),
                automationName: LocalizationHelper.GetString("ConnectionPage_DenyPairingRequest"),
                automationId: "DevicePairDenyAction");
            if (string.IsNullOrEmpty(capturedId))
            {
                approveBtn.IsEnabled = false;
                rejectBtn.IsEnabled = false;
            }
            approveBtn.Click += async (_, __) =>
            {
                if (string.IsNullOrEmpty(capturedId)) return;
                await RunPairingDecisionAsync(approveBtn, rejectBtn, isApprove: true, async client =>
                {
                    var ok = await client.DevicePairApproveAsync(capturedId);
                    if (ok) await client.RequestDevicePairListAsync();
                    return ok;
                });
            };
            rejectBtn.Click += async (_, __) =>
            {
                if (string.IsNullOrEmpty(capturedId)) return;
                await RunPairingDecisionAsync(approveBtn, rejectBtn, isApprove: false, async client =>
                {
                    var ok = await client.DevicePairRejectAsync(capturedId);
                    if (ok) await client.RequestDevicePairListAsync();
                    return ok;
                });
            };
            buttons.Children.Add(approveBtn);
            buttons.Children.Add(rejectBtn);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);
        }
        card.Child = grid;
        return card;
    }

    private Border BuildNodePairingCard(PairingRequest req, bool canPair)
    {
        var card = new Border
        {
            Background = ResolveBrush("CardBackgroundFillColorSecondaryBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
        };
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (canPair) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Spacing = 2 };
        info.Children.Add(new TextBlock
        {
            Text = string.Format(LocalizationHelper.GetString("ConnectionPage_NodeLabel"), req.DisplayName ?? req.NodeId),
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        var detail = req.Platform ?? LocalizationHelper.GetString("ConnectionPage_UnknownPlatform");
        if (!string.IsNullOrEmpty(req.RemoteIp)) detail += $" · {req.RemoteIp}";
        info.Children.Add(new TextBlock
        {
            Text = detail,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
        });
        if (req.IsRepair)
        {
            info.Children.Add(new TextBlock
            {
                Text = LocalizationHelper.GetString("ConnectionPage_RepairRequest"),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = ResolveBrush("SystemFillColorCautionBrush"),
            });
        }
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        if (canPair)
        {
            // Same per-row treatment as device pairing (see BuildDevicePairingCard).
            // Affirmative/negative cues come from leading glyphs, not button
            // chrome, to keep the page within the "one accent per view" rule
            // even when several pending requests are stacked.
            var capturedId = req.RequestId ?? req.NodeId;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var approveBtn = BuildPairingDecisionButton(
                glyph: Helpers.FluentIconCatalog.Check,
                glyphBrushKey: "SystemFillColorSuccessBrush",
                label: LocalizationHelper.GetString("ConnectionPage_Approve"),
                automationName: LocalizationHelper.GetString("ConnectionPage_ApprovePairingRequest"),
                automationId: "NodePairApproveAction");
            var rejectBtn = BuildPairingDecisionButton(
                glyph: Helpers.FluentIconCatalog.Exit,
                glyphBrushKey: "SystemFillColorCriticalBrush",
                label: LocalizationHelper.GetString("ConnectionPage_Deny"),
                automationName: LocalizationHelper.GetString("ConnectionPage_DenyPairingRequest"),
                automationId: "NodePairDenyAction");
            if (string.IsNullOrEmpty(capturedId))
            {
                approveBtn.IsEnabled = false;
                rejectBtn.IsEnabled = false;
            }
            approveBtn.Click += async (_, __) =>
            {
                if (string.IsNullOrEmpty(capturedId)) return;
                await RunPairingDecisionAsync(approveBtn, rejectBtn, isApprove: true, async client =>
                {
                    var ok = await client.NodePairApproveAsync(capturedId);
                    // Symmetry with device pairing: ask for a fresh list so the
                    // panel rebuilds and this card drops out. Without this the
                    // Approve/Deny buttons stayed disabled until the gateway
                    // spontaneously pushed a list update.
                    if (ok) await client.RequestNodePairListAsync();
                    return ok;
                });
            };
            rejectBtn.Click += async (_, __) =>
            {
                if (string.IsNullOrEmpty(capturedId)) return;
                await RunPairingDecisionAsync(approveBtn, rejectBtn, isApprove: false, async client =>
                {
                    var ok = await client.NodePairRejectAsync(capturedId);
                    if (ok) await client.RequestNodePairListAsync();
                    return ok;
                });
            };
            buttons.Children.Add(approveBtn);
            buttons.Children.Add(rejectBtn);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);
        }
        card.Child = grid;
        return card;
    }

    private static string SanitizeUrl(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return uri.Port > 0 ? $"{uri.Scheme}://{uri.Host}:{uri.Port}" : $"{uri.Scheme}://{uri.Host}";
        }
        catch (Exception ex)
        {
            Logger.Debug($"ConnectionPage: Failed to sanitize gateway URL '{url}': {ex.Message}");
        }
        return url;
    }
}
