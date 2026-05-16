using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Pages;

// ════════════════════════════════════════════════════════════════════════════
// ConnectionPage — Lobby & Cockpit & Recovery
// ────────────────────────────────────────────────────────────────────────────
// Visual policy:
//   • Page mode (Lobby / Cockpit / Recovery / Add) computed by
//     ConnectionPagePlan.Build(...) (pure projection).
//   • Every status surface uses a 4-px colored LEFT border — accent brush
//     resolved via ConnectionPagePlan.AccentToBrushKey.
//   • This code-behind never mutates connection state directly; every
//     connection-touching action goes through GatewayConnectionManager,
//     GatewayRegistry, SettingsManager (untouched here).
// ════════════════════════════════════════════════════════════════════════════
public sealed partial class ConnectionPage : Page
{
    // ─── DI / services ───
    private HubWindow? _hub;
    private IGatewayConnectionManager? _connectionManager;
    private GatewayRegistry? _gatewayRegistry;
    private GatewayDiscoveryService? _discoveryService;

    // ─── UI state ───
    private UserIntent _userIntent = UserIntent.None;
    private GatewayConnectionSnapshot _lastSnapshot = GatewayConnectionSnapshot.Idle;
    private bool _suppressNodeModeToggle;
    private bool _suppressConnectionToggle;
    private ConnectionPagePlan _currentPlan = new();

    // Tracks which gateway record the Add Gateway form is currently editing
    // (set by OnSavedRowEdit / OnEditTunnelSettings; null = creating a brand
    // new record). Used by DoDirectConnectFromAddFormAsync so a URL change
    // updates the original record instead of orphaning it as a duplicate.
    private string? _editingGatewayId;

    // ─── Reconnect-mask state ───
    // Toggling Node mode forces the connection manager to tear down the
    // WS and rebuild it (so the gateway sees the role change). That brief
    // Disconnecting → Disconnected → Connecting → Connected transition was
    // showing through every visual surface (strip headline, operator card,
    // active-row badge) as a "you're disconnected!" flicker, which is wrong
    // — the user's *intent* is still to be connected, the connection layer
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
    // ItemsSource swaps re-template every item even when the content is
    // identical, which causes a visible flash on every snapshot tick.
    // We stash a string fingerprint of the inputs and skip the swap when
    // the rendered output would be identical. Keeps the page calm during
    // the rapid-fire snapshot transitions a Node-mode toggle produces.
    private string? _savedGatewaysFingerprint;
    private string? _glanceChipsFingerprint;
    private string? _capabilityChipsFingerprint;

    public ConnectionPage()
    {
        InitializeComponent();
    }

    // ─── Initialization ───────────────────────────────────────────────

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        _connectionManager = hub.ConnectionManager;
        _gatewayRegistry = hub.GatewayRegistry;
        var settings = hub.Settings;
        if (settings == null) return;

        // Local-WSL install entry points. The hub only exposes
        // OpenSetupAction on platforms where WSL tooling is wired up; if
        // it's null we hide the entry points so the user isn't offered a
        // button that does nothing.
        //   • WelcomeLocalWslSetupCard — get-started CTA on the empty-state
        //     Welcome screen for first-run users.
        //   • AddLocalWslItem — third method tab inside the Add Gateway
        //     form, alongside Direct and Setup code.
        var localSetupAvailable = hub.OpenSetupAction is not null;
        var localSetupVisibility = localSetupAvailable
            ? Visibility.Visible
            : Visibility.Collapsed;
        WelcomeLocalWslSetupCard.Visibility = localSetupVisibility;
        AddLocalWslItem.Visibility = localSetupVisibility;

        if (_connectionManager != null)
            _connectionManager.StateChanged += OnManagerStateChanged;
        if (_gatewayRegistry != null)
            _gatewayRegistry.Changed += OnRegistryChanged;

        Unloaded += OnPageUnloaded;

        // Initialize Node mode toggle from settings (suppressed event)
        _suppressNodeModeToggle = true;
        NodeModeToggle.IsOn = settings.EnableNodeMode;
        _suppressNodeModeToggle = false;

        // RefreshFromSnapshot below already calls LoadSavedGateways with the
        // up-to-date snapshot — calling it standalone here would render the
        // active row as "disconnected" for one frame before the snapshot pass
        // flips it to "Connected".
        RefreshFromSnapshot(_connectionManager?.CurrentSnapshot ?? GatewayConnectionSnapshot.Idle);
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_connectionManager != null)
            _connectionManager.StateChanged -= OnManagerStateChanged;
        if (_gatewayRegistry != null)
            _gatewayRegistry.Changed -= OnRegistryChanged;
        _discoveryService?.Dispose();
        _discoveryService = null;
        if (_reconnectMaskTimer != null)
        {
            _reconnectMaskTimer.Stop();
            _reconnectMaskTimer.Tick -= OnReconnectMaskTimeout;
            _reconnectMaskTimer = null;
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
            LoadSavedGateways();
            RefreshFromSnapshot(_lastSnapshot);
        });
    }

    /// <summary>Legacy bridge from HubWindow's ConnectionStatus-based pumps.</summary>
    public void UpdateStatus(ConnectionStatus status)
    {
        var snapshot = _connectionManager?.CurrentSnapshot ?? GatewayConnectionSnapshot.Idle;
        _lastSnapshot = snapshot;
        RefreshFromSnapshot(snapshot);
    }

    // ─── Plan apply ───────────────────────────────────────────────────

    private void RefreshFromSnapshot(GatewayConnectionSnapshot snapshot)
    {
        if (_hub == null) return;

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
            // arrives — defeating the entire fix.
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
        var self = _hub.LastGatewaySelf;
        var settings = _hub.Settings;

        var plan = ConnectionPagePlan.Build(
            effective, activeRecord, self, settings, savedCount, _userIntent);

        _currentPlan = plan;
        ApplyPlan(plan);

        // Rebuild saved-gateway rows so the active row's "Connected" badge /
        // background highlight reflects the live snapshot. Cheap — list is
        // typically < 10 entries and only re-runs on real state transitions.
        LoadSavedGateways();

        // Bridge auth error (lives outside the plan as a transient modifier)
        var authError = _hub.LastAuthError;
        if (!string.IsNullOrEmpty(authError))
        {
            AuthErrorBar.Message = GetAuthErrorGuidance(authError!);
            AuthErrorBar.IsOpen = true;
        }
        else
        {
            AuthErrorBar.IsOpen = false;
        }
    }

    private void ApplyPlan(ConnectionPagePlan plan)
    {
        // ─── Page mode visibility ───
        bool isWelcome  = plan.Mode == ConnectionPageMode.Welcome;
        bool isCockpit  = plan.Mode == ConnectionPageMode.Cockpit;
        bool isRecovery = plan.Mode == ConnectionPageMode.Recovery;
        bool isAdding   = plan.Mode == ConnectionPageMode.AddGateway;

        // Operator + Node cards only when we actually have an active operator
        // connection AND we're not in a focused sub-view (Welcome / Recovery /
        // AddGateway). Recovery's help block carries the action; the role
        // cards would just compete with it.
        bool hasOperatorSession = _lastSnapshot.OverallState is
            OverallConnectionState.Connected
            or OverallConnectionState.Ready
            or OverallConnectionState.Degraded
            or OverallConnectionState.Connecting
            or OverallConnectionState.PairingRequired
            or OverallConnectionState.Disconnecting;
        bool showRoles = hasOperatorSession && !isWelcome && !isAdding && !isRecovery;
        CockpitPanel.Visibility = showRoles ? Visibility.Visible : Visibility.Collapsed;
        OperatorSection.Visibility = showRoles ? Visibility.Visible : Visibility.Collapsed;

        // Bottom section: exactly one of these is visible
        //   • SavedGatewaysCard  — Cockpit / Recovery (always present when registry has items)
        //   • WelcomeAddTilesCard — Welcome (empty registry)
        //   • AddGatewayPanel    — AddGateway sub-view
        SavedGatewaysCard.Visibility    = (isCockpit || isRecovery) ? Visibility.Visible : Visibility.Collapsed;
        WelcomeAddTilesCard.Visibility  = isWelcome ? Visibility.Visible : Visibility.Collapsed;
        AddGatewayPanel.Visibility      = isAdding ? Visibility.Visible : Visibility.Collapsed;

        // Recovery's help block sits above the always-visible gateways list.
        RecoveryPanel.Visibility = isRecovery ? Visibility.Visible : Visibility.Collapsed;

        // ─── Status strip ───
        ApplyStripVisuals(plan);

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

        // Primary action button — show only for actions the connection
        // toggle can't already do (CopyApproveCommand, Rep, RestartTunnel).
        // Connect / Reconnect / Retry / Cancel are all reachable by
        // tapping the toggle, so surfacing a redundant button next to it
        // was noisy. Also suppress in AddGateway since the form has its
        // own Save & Connect CTA.
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

        // Connection toggle — visible when there's an active gateway record.
        // ON = currently connected (or attempting); tap OFF to disconnect.
        // OFF = idle/disconnecting; tap ON to reconnect.
        bool hasActive = plan.RelevantGatewayId != null;
        // "ON" = the gateway is or is trying to be connected. Error state
        // is a terminal failure — toggle should read OFF so the affordance
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

        // Dashboard icon — top-right of the gateway header. Only when there's
        // a healthy connection (no point opening the dashboard of a broken one)
        // and we're not in the focused Add Gateway sub-view.
        bool showDashboard = plan.Mode == ConnectionPageMode.Cockpit
                          && plan.StripAccent == ConnectionAccent.Success
                          && plan.ActiveGatewayDisplayName != null;
        StripDashboardButton.Visibility = showDashboard ? Visibility.Visible : Visibility.Collapsed;

        // Glance chips (presence • channels • $today • topology) — when connected.
        ApplyGlanceChips(plan, hasActive && toggleOn);
    }

    /// <summary>
    /// Public entry point invoked by HubWindow when sessions / channels /
    /// usage data refresh. Cheaper than re-running the full snapshot pipeline.
    /// </summary>
    public void OnGlanceDataChanged()
    {
        // Re-run the strip/operator pass with the cached snapshot so chips
        // pick up the new data without bouncing the rest of the page. Derive
        // visibility from _lastSnapshot directly — ConnectionToggle.IsOn is
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

        var self = _hub?.LastGatewaySelf;
        var channels = _hub?.LastChannels;
        var cost = _hub?.LastUsageCost;
        var activeRec = _gatewayRegistry?.GetActive();

        // Compute the fingerprint from the same inputs the chips render so
        // we can short-circuit when nothing observable changed. Cheaper than
        // re-templating ~5 chip Borders every snapshot tick.
        var topology = ClassifyTopology(activeRec);
        var hostname = Environment.MachineName;
        int? presence = self?.PresenceCount;
        int channelTotal = channels?.Length ?? 0;
        int channelOk = channels is { Length: > 0 }
            ? channels.Count(c => c.IsLinked && (
                string.Equals(c.Status, "ready", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Status, "ok", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Status, "connected", StringComparison.OrdinalIgnoreCase)))
            : 0;
        double todayAmount = 0d;
        if (cost?.Daily is { Count: > 0 })
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var entry = cost.Daily.FirstOrDefault(d => d.Date == today);
            todayAmount = entry?.TotalCost ?? 0d;
        }
        // Include cost-data presence (daily count) separately from the
        // amount so the chip correctly appears/disappears on $0.00 days
        // — without this the fingerprint can be identical when daily list
        // appears/empties at $0.00, leaving a stale chip state.
        int dailyCount = cost?.Daily?.Count ?? 0;
        var fp = $"{topology}|{hostname}|{presence}|{channelOk}/{channelTotal}|{dailyCount}|${todayAmount:0.00}";
        if (_glanceChipsFingerprint == fp) return;
        _glanceChipsFingerprint = fp;

        var chips = new List<Border>(5);

        // 1. Topology — Local / via SSH / LAN / Tailscale / Remote.
        //    Tells the user what kind of gateway they're talking to.
        if (topology != null)
        {
            chips.Add(BuildGlanceChip(Helpers.FluentIconCatalog.ServerEnvironment, topology, neutral: true));
        }

        // 2. Local hostname — "on PERSEID". Tells the user what device the
        //    tray (and therefore Operator + Node) runs on. Free signal.
        if (!string.IsNullOrWhiteSpace(hostname))
        {
            chips.Add(BuildGlanceChip(Helpers.FluentIconCatalog.Hostname, $"on {hostname}", neutral: true));
        }

        // 3. Presence count — "1 client" / "3 clients" if the gateway has
        //    multiple operators connected.
        if (presence is int n && n > 0)
        {
            var label = n == 1 ? "1 client" : $"{n} clients";
            chips.Add(BuildGlanceChip(Helpers.FluentIconCatalog.People, label, neutral: true));
        }

        // 4. Channels glance — "2/2 channels ready" when at least one channel
        //    is configured. Counts linked-and-ok channels.
        if (channelTotal > 0)
        {
            var label = $"{channelOk}/{channelTotal} channel{(channelTotal == 1 ? "" : "s")}";
            chips.Add(BuildGlanceChip(Helpers.FluentIconCatalog.Channels, label, neutral: channelOk == channelTotal));
        }

        // 5. Today's $ — falls back to "$0.00 today" so the chip stays in a
        //    consistent slot even on a fresh day with no usage yet.
        if (cost?.Daily is { Count: > 0 })
        {
            var label = todayAmount > 0
                ? $"${todayAmount:0.00} today"
                : "$0.00 today";
            chips.Add(BuildGlanceChip(Helpers.FluentIconCatalog.Money, label, neutral: true));
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
        if (rec.SshTunnel != null) return "via SSH tunnel";
        if (string.IsNullOrEmpty(rec.Url)) return null;
        try
        {
            var uri = new Uri(rec.Url);
            var host = uri.Host;
            if (host == "localhost" || host == "127.0.0.1" || host == "::1") return "Local";
            if (host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)) return "Tailscale";
            if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return "LAN";
            // RFC1918 private ranges
            if (host.StartsWith("10.") || host.StartsWith("192.168.") ||
                (host.StartsWith("172.") && IsPrivate172(host)))
                return "LAN";
            // Tailnet CGNAT range 100.64.0.0/10
            if (host.StartsWith("100."))
            {
                var parts = host.Split('.');
                if (parts.Length >= 2 && int.TryParse(parts[1], out var second) &&
                    second >= 64 && second <= 127)
                    return "Tailscale";
            }
            return "Remote";
        }
        catch
        {
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
        // No prose body — the card's identity ("Operator") + the active
        // sessions caption + the deep links carry meaning. Dim transient states.
        bool linksEnabled = plan.OperatorCard == OperatorCardState.Active
                         || plan.OperatorCard == OperatorCardState.Idle;
        OperatorSessionsLink.IsEnabled = linksEnabled;
        OperatorInstancesLink.IsEnabled = linksEnabled;
        OperatorSection.Opacity = linksEnabled ? 1.0 : 0.65;

        // Status sub-row (mirrors PermissionsPage NodeStatusDot pattern):
        // colored dot + descriptive label that reflects the live state.
        var sessions = _hub?.LastSessions;
        int activeSessions = sessions?.Count(s =>
            string.Equals(s.Status, "active", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Status, "running", StringComparison.OrdinalIgnoreCase)) ?? 0;

        var (statusGlyph, statusBrushKey, statusText) = plan.OperatorCard switch
        {
            OperatorCardState.Active when activeSessions > 0 => (
                Helpers.FluentIconCatalog.StatusOk,
                "SystemFillColorSuccessBrush",
                activeSessions == 1
                    ? "Active · 1 session"
                    : $"Active · {activeSessions} sessions"),
            OperatorCardState.Active => (
                Helpers.FluentIconCatalog.StatusOk,
                "SystemFillColorSuccessBrush",
                "Active · no current sessions"),
            OperatorCardState.Idle => (
                Helpers.FluentIconCatalog.CapabilityOff,
                "SystemFillColorNeutralBrush",
                "Disconnected"),
            OperatorCardState.Connecting => (
                Helpers.FluentIconCatalog.Sync,
                "SystemFillColorCautionBrush",
                "Connecting…"),
            OperatorCardState.Paused => (
                Helpers.FluentIconCatalog.Sync,
                "SystemFillColorCautionBrush",
                "Reconnecting…"),
            _ => (Helpers.FluentIconCatalog.CapabilityOff, "SystemFillColorNeutralBrush", "Disconnected"),
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

        var settings = _hub?.Settings;
        var enabledCaps = settings != null ? CountEnabledCapabilities(settings) : 0;

        // Body text + chips (defined below) cover every Node state. The
        // legacy "var (_, body) = …" block was dropped — its prose is no
        // longer used (showBody handles the warning/error subset).
        _ = settings; // settings still consumed by chip builder + toggle sync

        // Body text (warning/error detail under the status text) only surfaces
        // for warning/error/pairing states.
        bool showBody = plan.NodeCard is NodeCardState.OnPermissionsIncomplete
                                       or NodeCardState.OnNodePairingRequired
                                       or NodeCardState.OnNodeRejected
                                       or NodeCardState.OnNodeRateLimited
                                       or NodeCardState.OnNodeError;
        var bodyText = plan.NodeCard switch
        {
            NodeCardState.OnPermissionsIncomplete => "No capabilities enabled. Pick what to share in Permissions.",
            NodeCardState.OnNodePairingRequired   => "Awaiting approval on the gateway host.",
            NodeCardState.OnNodeRejected          => "Pairing was rejected. Re-pair from Add Gateway.",
            NodeCardState.OnNodeRateLimited       => "Rate-limited by the gateway. Will retry after cooldown.",
            NodeCardState.OnNodeError             => plan.NodeErrorDetail ?? "Node reported an error.",
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
        var caps = settings != null ? CountEnabledCapabilities(settings) : 0;
        var (nodeGlyph, nodeBrushKey, nodeStatusText) = plan.NodeCard switch
        {
            NodeCardState.OnHealthy => (
                Helpers.FluentIconCatalog.StatusOk,
                "SystemFillColorSuccessBrush",
                caps == 1 ? "Node active · 1 capability" : $"Node active · {caps} capabilities"),
            NodeCardState.OnPermissionsIncomplete => (
                Helpers.FluentIconCatalog.StatusWarn,
                "SystemFillColorCautionBrush",
                "Node active · no capabilities enabled"),
            NodeCardState.OnNodePairingRequired => (
                Helpers.FluentIconCatalog.Lock,
                "SystemFillColorCautionBrush",
                "Awaiting pairing approval"),
            NodeCardState.OnNodeRejected => (
                Helpers.FluentIconCatalog.StatusErr,
                "SystemFillColorCriticalBrush",
                "Pairing rejected"),
            NodeCardState.OnNodeRateLimited => (
                Helpers.FluentIconCatalog.StatusWarn,
                "SystemFillColorCautionBrush",
                "Rate-limited"),
            NodeCardState.OnNodeError => (
                Helpers.FluentIconCatalog.StatusErr,
                "SystemFillColorCriticalBrush",
                "Node error"),
            NodeCardState.Off => (
                Helpers.FluentIconCatalog.CapabilityOff,
                "SystemFillColorCriticalBrush",
                "Node mode disabled"),
            _ => (Helpers.FluentIconCatalog.CapabilityOff, "SystemFillColorCriticalBrush", "Node mode disabled"),
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

        // Canonical capability list per design naming.md:
        //   "Providing N capabilities: browser, camera, canvas, screen, location, tts, stt"
        //   Empty list renders as "Providing no capabilities".
        // Hidden when Node mode is off (no concept of a "list" then).
        bool showCaps = settings != null && plan.NodeCard != NodeCardState.Off
                                         && plan.NodeCard != NodeCardState.Hidden;
        NodeCapabilityText.Visibility = showCaps ? Visibility.Visible : Visibility.Collapsed;
        if (showCaps)
        {
            NodeCapabilityText.Text = BuildCapabilityListString(settings!);
        }

        // Sync toggle from current settings (suppress event)
        _suppressNodeModeToggle = true;
        if (settings != null) NodeModeToggle.IsOn = settings.EnableNodeMode;
        _suppressNodeModeToggle = false;

        // Approve command box (only when pairing required)
        if (plan.NodeCard == NodeCardState.OnNodePairingRequired && plan.NodeApproveCommand != null)
        {
            NodeApproveCmdBox.Visibility = Visibility.Visible;
            NodeApproveCmdText.Text = plan.NodeApproveCommand;
        }
        else
        {
            NodeApproveCmdBox.Visibility = Visibility.Collapsed;
        }

        // Capability chips — skip the rebuild if the rendered output would
        // be identical (e.g. mid-reconnect snapshot ticks where settings and
        // node state haven't actually changed). Includes ALL 7 capabilities
        // because BuildCapabilityChips renders all of them — missing any
        // here would silently swallow that capability's toggle.
        var capFp = $"{plan.NodeCard}|{settings?.NodeBrowserProxyEnabled}|{settings?.NodeCameraEnabled}|{settings?.NodeCanvasEnabled}|{settings?.NodeScreenEnabled}|{settings?.NodeLocationEnabled}|{settings?.NodeTtsEnabled}|{settings?.NodeSttEnabled}";
        if (_capabilityChipsFingerprint != capFp)
        {
            _capabilityChipsFingerprint = capFp;
            NodeCapabilityChipsHost.ItemsSource = BuildCapabilityChips(settings, plan.NodeCard);
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

        RecoveryHelpHeaderText.Text = plan.Recovery switch
        {
            RecoveryCategory.Auth => "Re-pair this gateway:",
            RecoveryCategory.Pairing => "Awaiting approval:",
            RecoveryCategory.Tunnel => "Help us fix the SSH tunnel:",
            RecoveryCategory.Server => "Help us fix this connection:",
            _ => "Help us fix this connection:",
        };

        var bullets = plan.Recovery switch
        {
            RecoveryCategory.Auth => new[]
            {
                "Get a fresh setup code from the gateway host.",
                "Or paste a new direct token below.",
            },
            RecoveryCategory.Pairing => new[]
            {
                "Approve this client on the gateway host using the command below.",
                "Once approved, this client will connect automatically.",
            },
            RecoveryCategory.Tunnel => new[]
            {
                "Confirm SSH is reachable on the host.",
                "Restart the tunnel below if it stopped.",
            },
            RecoveryCategory.Server => new[]
            {
                "Check the gateway logs on the host.",
                "If the gateway is partially up, open its dashboard.",
                "Edit gateway settings to change URL or token.",
            },
            _ => new[]
            {
                "Check that the gateway is running on the host.",
                "Check that you're on the same network or VPN.",
                "If using SSH tunnel: confirm it's up.",
            },
        };

        foreach (var b in bullets)
            RecoveryBulletsPanel.Children.Add(BuildBulletRow(b));

        // Sub-blocks
        if (plan.Recovery == RecoveryCategory.Tunnel)
        {
            RecoveryTunnelBlock.Visibility = Visibility.Visible;
            RecoveryTunnelDetailText.Text = plan.RecoveryDetail ?? "SSH tunnel is down.";
        }
        if (plan.Recovery == RecoveryCategory.Auth)
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

    private List<Border> BuildCapabilityChips(SettingsManager? s, NodeCardState state)
    {
        var chips = new List<Border>();
        if (s == null) return chips;
        if (state == NodeCardState.Off || state == NodeCardState.Hidden) return chips;

        void Add(string label, bool enabled, bool warn = false, bool error = false)
        {
            string bgKey;
            string fgKey;
            string glyph;
            if (error)
            {
                bgKey = "SystemFillColorCriticalBackgroundBrush";
                fgKey = "SystemFillColorCriticalBrush";
                glyph = Helpers.FluentIconCatalog.StatusErr;
            }
            else if (warn)
            {
                bgKey = "SystemFillColorCautionBackgroundBrush";
                fgKey = "SystemFillColorCautionBrush";
                glyph = Helpers.FluentIconCatalog.StatusWarn;
            }
            else if (enabled)
            {
                bgKey = "SystemFillColorSuccessBackgroundBrush";
                fgKey = "SystemFillColorSuccessBrush";
                glyph = Helpers.FluentIconCatalog.StatusOk;
            }
            else
            {
                bgKey = "SubtleFillColorSecondaryBrush";
                fgKey = "TextFillColorSecondaryBrush";
                glyph = Helpers.FluentIconCatalog.CapabilityOff;
            }

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
            chips.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Background = ResolveBrush(bgKey),
                Child = stack,
            });
        }

        bool permsWarn = state == NodeCardState.OnPermissionsIncomplete;
        // Order matches the canonical capability list (naming.md):
        //   browser, camera, canvas, screen, location, tts, stt
        Add("Browser",  s.NodeBrowserProxyEnabled);
        Add("Camera",   s.NodeCameraEnabled);
        Add("Canvas",   s.NodeCanvasEnabled);
        Add("Screen",   s.NodeScreenEnabled);
        Add("Location", s.NodeLocationEnabled);
        Add("TTS",      s.NodeTtsEnabled);
        Add("STT",      s.NodeSttEnabled);
        // Trailing summary chip — surfaces when permissions are incomplete.
        if (permsWarn)
        {
            int caps = CountEnabledCapabilities(s);
            Add($"{caps}/7 enabled", false, warn: true);
        }
        return chips;
    }

    /// <summary>
    /// Canonical "Providing N capabilities: …" line per design naming.md.
    /// Order matches the canonical capability list (browser, camera, canvas,
    /// screen, location, tts, stt). Empty → "Providing no capabilities".
    /// </summary>
    private static string BuildCapabilityListString(SettingsManager s)
    {
        var caps = new List<string>(7);
        if (s.NodeBrowserProxyEnabled) caps.Add("browser");
        if (s.NodeCameraEnabled)       caps.Add("camera");
        if (s.NodeCanvasEnabled)       caps.Add("canvas");
        if (s.NodeScreenEnabled)       caps.Add("screen");
        if (s.NodeLocationEnabled)     caps.Add("location");
        if (s.NodeTtsEnabled)          caps.Add("tts");
        if (s.NodeSttEnabled)          caps.Add("stt");
        if (caps.Count == 0) return "Providing no capabilities";
        return $"Providing {caps.Count} capabilit{(caps.Count == 1 ? "y" : "ies")}: {string.Join(", ", caps)}";
    }

    private static int CountEnabledCapabilities(SettingsManager s)
    {
        int n = 0;
        if (s.NodeBrowserProxyEnabled) n++;
        if (s.NodeCameraEnabled) n++;
        if (s.NodeCanvasEnabled) n++;
        if (s.NodeScreenEnabled) n++;
        if (s.NodeLocationEnabled) n++;
        if (s.NodeTtsEnabled) n++;
        if (s.NodeSttEnabled) n++;
        return n;
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

        // Auth mode for the ACTIVE gateway — taken from the latest hello-ok
        // response (server-reported, the source of truth for what we're
        // actually using). For inactive saved gateways we fall back to whatever
        // credential is stored on the record.
        var activeAuthMode = _hub?.LastGatewaySelf?.AuthMode;

        if (_gatewayRegistry != null)
        {
            var all = _gatewayRegistry.GetAll();
            var active = _gatewayRegistry.GetActive();
            foreach (var gw in all)
            {
                var isActive = active != null && active.Id == gw.Id;
                items.Add(new SavedGatewayRow
                {
                    Id = gw.Id,
                    DisplayName = !string.IsNullOrEmpty(gw.FriendlyName) ? gw.FriendlyName! : gw.Url,
                    Url = gw.Url,
                    IsActive = isActive,
                    LastConnectedRelative = FormatRelative(gw.LastConnected),
                    HasSshTunnel = gw.SshTunnel != null,
                    AuthModeLabel = isActive && !string.IsNullOrEmpty(activeAuthMode)
                        ? activeAuthMode!
                        : InferAuthModeLabel(gw),
                });
            }
            if (all.Count > 0) emptyVisible = Visibility.Collapsed;
        }

        SavedGatewaysEmptyText.Visibility = emptyVisible;

        // Fingerprint to skip ItemsSource swap when nothing observable
        // changed — see field comment. Includes overall connection state
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
              .Append(r.AuthModeLabel ?? "").Append(';');
        }
        var fp = sb.ToString();
        if (_savedGatewaysFingerprint == fp) return;
        _savedGatewaysFingerprint = fp;

        SavedGatewaysList.ItemsSource = BuildSavedGatewayRowControls(items);
        RecoverySavedList.ItemsSource = BuildSavedGatewayRowControls(items);
        RecoverySavedHeaderText.Text = items.Count == 1
            ? "Saved gateways (1)"
            : $"Saved gateways ({items.Count})";
    }

    private static string InferAuthModeLabel(GatewayRecord rec)
    {
        if (!string.IsNullOrEmpty(rec.BootstrapToken)) return "bootstrap";
        if (!string.IsNullOrEmpty(rec.SharedGatewayToken)) return "shared token";
        // No credential on the record itself → likely paired (device token
        // stored in the DeviceIdentityStore for this gateway's identity dir).
        return "device token";
    }

    private List<Border> BuildSavedGatewayRowControls(IEnumerable<SavedGatewayRow> rows)
    {
        var list = new List<Border>();
        foreach (var row in rows)
        {
            list.Add(BuildSavedGatewayRowControl(row));
        }
        return list;
    }

    private Border BuildSavedGatewayRowControl(SavedGatewayRow row)
    {
        // All rows use neutral card chrome. The "Connected" badge alone communicates
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
                Text = "• via SSH",
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

        // Active badge OR Connect button.
        //   Connected (active + live)       → "Connected" green badge (no clickable Connect)
        //   Active but currently disconnected → [Connect] button
        //   Inactive                         → [Connect] button
        bool isCurrentlyConnected = row.IsActive && _lastSnapshot.OverallState is
            OverallConnectionState.Connected
            or OverallConnectionState.Ready
            or OverallConnectionState.Degraded
            or OverallConnectionState.Connecting
            or OverallConnectionState.PairingRequired;

        if (isCurrentlyConnected)
        {
            // Inline "✓ Connected" — checkmark glyph + text, no boxed badge,
            // no second green dot. The active row no longer needs a heavy badge
            // since the page already carries a top-level status indicator.
            var badgeStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };
            badgeStack.Children.Add(new FontIcon
            {
                Glyph = Helpers.FluentIconCatalog.StatusOk,
                FontSize = 12,
                Foreground = ResolveBrush("SystemFillColorSuccessBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            badgeStack.Children.Add(new TextBlock
            {
                Text = "Connected",
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ResolveBrush("SystemFillColorSuccessBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(badgeStack, 1);
            grid.Children.Add(badgeStack);
        }
        else
        {
            var connectBtn = new Button
            {
                Content = "Connect",
                Tag = row.Id,
                VerticalAlignment = VerticalAlignment.Center,
            };
            connectBtn.Click += OnConnectSavedGateway;
            Grid.SetColumn(connectBtn, 1);
            grid.Children.Add(connectBtn);
        }

        // Overflow menu — actions depend on whether this row is currently the
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
        overflowBtn.Content = new FontIcon
        {
            Glyph = Helpers.FluentIconCatalog.MoreOverflow,
            FontSize = 12,
            Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
        };
        var flyout = new MenuFlyout();
        var openDashboard = new MenuFlyoutItem { Text = "Open dashboard", Tag = row.Id };
        openDashboard.Click += OnSavedRowOpenDashboard;
        flyout.Items.Add(openDashboard);
        if (isCurrentlyConnected)
        {
            var disconnect = new MenuFlyoutItem { Text = "Disconnect", Tag = row.Id };
            disconnect.Click += OnDisconnect;
            flyout.Items.Add(disconnect);
            var editActive = new MenuFlyoutItem { Text = "Edit", Tag = row.Id };
            editActive.Click += OnSavedRowEdit;
            flyout.Items.Add(editActive);
        }
        else
        {
            var edit = new MenuFlyoutItem { Text = "Edit", Tag = row.Id };
            edit.Click += OnSavedRowEdit;
            flyout.Items.Add(edit);
            // Removing the active-but-disconnected row just clears the active
            // pointer — safe.
            flyout.Items.Add(new MenuFlyoutSeparator());
            var remove = new MenuFlyoutItem { Text = "Remove", Tag = row.Id };
            remove.Click += OnSavedRowRemove;
            flyout.Items.Add(remove);
        }
        overflowBtn.Flyout = flyout;
        Grid.SetColumn(overflowBtn, 2);
        grid.Children.Add(overflowBtn);

        card.Child = grid;
        return card;
    }

    private static string FormatRelative(DateTime? when)
    {
        if (when == null) return "";
        var span = DateTime.UtcNow - when.Value.ToUniversalTime();
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)}mo ago";
        return $"{(int)(span.TotalDays / 365)}y ago";
    }

    private sealed class SavedGatewayRow
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Url { get; init; } = "";
        public bool IsActive { get; init; }
        public string LastConnectedRelative { get; init; } = "";
        public bool HasSshTunnel { get; init; }
        public string AuthModeLabel { get; init; } = "";
    }

    // ─── User intent transitions ──────────────────────────────────────

    private void OnEnterAddGateway(object sender, RoutedEventArgs e)
    {
        _editingGatewayId = null;
        _userIntent = UserIntent.AddingGateway;
        // Direct is default — make sure the selector is on Direct.
        ShowAddPane("direct");
        AddDirectItem.IsSelected = true;
        RefreshFromSnapshot(_lastSnapshot);
    }

    private void OnEnterAddGatewayDirect(object sender, RoutedEventArgs e)
    {
        _editingGatewayId = null;
        _userIntent = UserIntent.AddingGateway;
        ShowAddPane("direct");
        AddDirectItem.IsSelected = true;
        RefreshFromSnapshot(_lastSnapshot);
    }

    private void OnEnterAddGatewaySetupCode(object sender, RoutedEventArgs e)
    {
        _editingGatewayId = null;
        _userIntent = UserIntent.AddingGateway;
        ShowAddPane("setup");
        AddSetupCodeItem.IsSelected = true;
        RefreshFromSnapshot(_lastSnapshot);
    }

    private void OnEnterAddGatewayScan(object sender, RoutedEventArgs e)
    {
        // Legacy entry point — Scan no longer lives in the Add form.
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
        AddScanStatusText.Text = "Press Start scan to look for gateways on your network.";
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
                _hub?.ReconnectAction?.Invoke();
                break;
            case ConnectionPrimaryAction.Cancel:
                _ = _connectionManager?.DisconnectAsync();
                break;
            case ConnectionPrimaryAction.CopyApproveCommand:
                if (!string.IsNullOrEmpty(plan.RecoveryApproveCommand))
                    ClipboardHelper.CopyText(plan.RecoveryApproveCommand);
                else if (!string.IsNullOrEmpty(plan.NodeApproveCommand))
                    ClipboardHelper.CopyText(plan.NodeApproveCommand);
                break;
            case ConnectionPrimaryAction.RestartTunnel:
                OnRestartTunnel(sender, e);
                break;
            case ConnectionPrimaryAction.Rep:
                _editingGatewayId = null;
                _userIntent = UserIntent.None;
                // Surface Recovery's paste textbox is already on screen for Auth recovery.
                // For Pairing-type Rep, take the user to Add → Setup code as a fast path.
                _userIntent = UserIntent.AddingGateway;
                ShowAddPane("setup");
                AddSetupCodeItem.IsSelected = true;
                RefreshFromSnapshot(_lastSnapshot);
                break;
        }
    }

    private void OnOpenDashboard(object sender, RoutedEventArgs e)
    {
        var active = _gatewayRegistry?.GetActive();
        if (active == null) return;
        try
        {
            var http = active.Url
                .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
                .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase);
            _ = global::Windows.System.Launcher.LaunchUriAsync(new Uri(http));
        }
        catch { }
    }

    /// <summary>
    /// Handler for both the Welcome and Cockpit "Install local WSL gateway"
    /// buttons. Hands off to the hub's OpenSetupAction (which the App wires
    /// to the V2 onboarding flow) — same wiring master added on the legacy
    /// ConnectionPage; the cards live in different slots in the rebuilt
    /// page but the user-facing behavior is identical.
    /// </summary>
    private void OnInstallLocalWslGateway(object sender, RoutedEventArgs e)
    {
        _hub?.OpenSetupAction?.Invoke();
    }

    // ─── Operator card navigation ────────────────────────────────────

    private void OnOpenSessions(object sender, RoutedEventArgs e) => _hub?.NavigateTo("sessions");
    private void OnOpenInstances(object sender, RoutedEventArgs e) => _hub?.NavigateTo("instances");

    // ─── Node card navigation ────────────────────────────────────────

    private void OnOpenPermissions(object sender, RoutedEventArgs e) => _hub?.NavigateTo("permissions");

    private void OnCopyNodeApproveCommand(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(NodeApproveCmdText.Text))
            ClipboardHelper.CopyText(NodeApproveCmdText.Text);
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
            AddResultText.Text = "Tunnel restart triggered.";
        }
        catch (Exception ex)
        {
            AddResultText.Text = $"Tunnel restart failed: {ex.Message}";
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
        DirectTokenBox.Password = rec.SharedGatewayToken ?? "";
        DirectNameBox.Text = rec.FriendlyName ?? "";
        if (rec.SshTunnel != null)
        {
            AddSshExpander.IsExpanded = true;
            AddSshUserBox.Text = rec.SshTunnel.User;
            AddSshHostBox.Text = rec.SshTunnel.Host;
            AddSshRemotePortBox.Text = rec.SshTunnel.RemotePort.ToString();
            AddSshLocalPortBox.Text = rec.SshTunnel.LocalPort.ToString();
        }
        ShowAddPane("direct");
        AddDirectItem.IsSelected = true;
        RefreshFromSnapshot(_lastSnapshot);
    }

    private async void OnApplyRepairCode(object sender, RoutedEventArgs e)
    {
        var code = RecoveryRepairCodeBox.Text?.Trim();
        if (string.IsNullOrEmpty(code) || _connectionManager == null) return;
        try
        {
            var result = await _connectionManager.ApplySetupCodeAsync(code);
            AddResultText.Text = result.Outcome == SetupCodeOutcome.Success
                ? "Re-paired. Reconnecting…"
                : $"✗ {result.ErrorMessage ?? "Could not apply code."}";
        }
        catch (Exception ex)
        {
            AddResultText.Text = $"✗ {ex.Message}";
        }
    }

    // ─── Saved-gateway row actions ───────────────────────────────────

    private async void OnConnectSavedGateway(object sender, RoutedEventArgs e)
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
            // surface the immediate error in the auth-error bar so the user
            // gets feedback even if the snapshot is briefly silent.
            try
            {
                AuthErrorBar.Title = "Connect failed";
                AuthErrorBar.Message = ex.Message;
                AuthErrorBar.Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
                AuthErrorBar.IsOpen = true;
            }
            catch { /* last-ditch */ }
        }
        finally
        {
            try { btn.IsEnabled = true; } catch { /* control may be detached */ }
        }
    }

    private void OnSavedRowOpenDashboard(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string gwId) return;
        var rec = _gatewayRegistry?.GetById(gwId);
        if (rec == null) return;
        try
        {
            var http = rec.Url
                .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
                .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase);
            _ = global::Windows.System.Launcher.LaunchUriAsync(new Uri(http));
        }
        catch { }
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
        DirectTokenBox.Password = rec.SharedGatewayToken ?? "";
        DirectNameBox.Text = rec.FriendlyName ?? "";
        if (rec.SshTunnel != null)
        {
            AddSshExpander.IsExpanded = true;
            AddSshUserBox.Text = rec.SshTunnel.User;
            AddSshHostBox.Text = rec.SshTunnel.Host;
            AddSshRemotePortBox.Text = rec.SshTunnel.RemotePort.ToString();
            AddSshLocalPortBox.Text = rec.SshTunnel.LocalPort.ToString();
        }
        ShowAddPane("direct");
        AddDirectItem.IsSelected = true;
        RefreshFromSnapshot(_lastSnapshot);
    }

    private async void OnSavedRowRemove(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string gwId) return;
        var rec = _gatewayRegistry?.GetById(gwId);
        if (rec == null) return;
        var dialog = new ContentDialog
        {
            Title = "Remove gateway?",
            Content = $"This will forget {rec.FriendlyName ?? rec.Url} and its credentials on this machine.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            // If the user is removing the currently-active gateway, tear
            // down the live connection first — otherwise the connection
            // manager keeps trying to talk to a record that no longer
            // exists in the registry, which leaves the UI in a stale state.
            var wasActive = string.Equals(_gatewayRegistry?.ActiveGatewayId, gwId, StringComparison.Ordinal);
            if (wasActive && _connectionManager != null)
            {
                try { await _connectionManager.DisconnectAsync(); } catch { }
            }
            _gatewayRegistry?.Remove(gwId);
            _gatewayRegistry?.Save();
            LoadSavedGateways();
            RefreshFromSnapshot(_lastSnapshot);
        }
    }

    // ─── Add gateway: Save ────────────────────────────────────────────

    private async void OnAddSave(object sender, RoutedEventArgs e)
    {
        var tag = ActiveAddPaneTag();
        AddResultText.Text = "";
        try
        {
            switch (tag)
            {
                case "direct":  await DoDirectConnectFromAddFormAsync(); break;
                case "setup":   await DoApplySetupCodeFromAddFormAsync(); break;
                case "scan":    AddResultText.Text = "Pick a discovered gateway above to connect."; break;
            }
        }
        catch (Exception ex)
        {
            AddResultText.Text = $"✗ {ex.Message}";
        }
    }

    /// <summary>
    /// Direct connect — adapted from the legacy OnDirectConnect handler.
    /// Identical semantics: validate, snapshot for rollback, AddOrUpdate +
    /// SetActive in the registry, ClearStoredTokens for the identity, save
    /// settings, kick the connection manager and wait for a terminal state.
    /// Per-gateway SSH is built from the AddSsh* fields (when expander expanded).
    /// </summary>
    private async Task DoDirectConnectFromAddFormAsync()
    {
        if (_connectionManager == null || _gatewayRegistry == null) return;

        var url = DirectUrlBox.Text?.Trim();
        var token = DirectTokenBox.Password?.Trim();
        var friendly = DirectNameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            AddResultText.Text = "Enter a gateway URL";
            return;
        }

        url = GatewayUrlHelper.NormalizeForWebSocket(url);

        // SSH tunnel — read from the Add form (per-gateway) if the expander is open
        SshTunnelConfig? sshConfig = null;
        bool useSsh = AddSshExpander.IsExpanded
                   && !string.IsNullOrWhiteSpace(AddSshUserBox.Text)
                   && !string.IsNullOrWhiteSpace(AddSshHostBox.Text);
        if (useSsh)
        {
            var sshUser = AddSshUserBox.Text.Trim();
            var sshHost = AddSshHostBox.Text.Trim();
            if (!int.TryParse(AddSshRemotePortBox.Text, out var remotePort) || remotePort is < 1 or > 65535)
            {
                AddResultText.Text = "SSH remote port must be 1–65535";
                return;
            }
            if (!int.TryParse(AddSshLocalPortBox.Text, out var localPort) || localPort is < 1 or > 65535)
            {
                AddResultText.Text = "SSH local port must be 1–65535";
                return;
            }
            sshConfig = new SshTunnelConfig(sshUser, sshHost, remotePort, localPort);
        }

        AddSaveButton.IsEnabled = false;
        AddResultText.Text = "Connecting…";

        // Snapshot previous state for rollback (mirrors legacy logic exactly)
        var previousActiveId = _gatewayRegistry.ActiveGatewayId;
        var previousSettings = _hub?.Settings;
        var prevGatewayUrl = previousSettings?.GatewayUrl;
        var prevUseSsh = previousSettings?.UseSshTunnel ?? false;
        var prevSshUser = previousSettings?.SshTunnelUser;
        var prevSshHost = previousSettings?.SshTunnelHost;
        var prevSshRemotePort = previousSettings?.SshTunnelRemotePort ?? 0;
        var prevSshLocalPort = previousSettings?.SshTunnelLocalPort ?? 0;

        // Resolve which record we're operating on:
        //   1. If the user opened the form via Edit on a saved row, prefer
        //      the original record id — this lets a URL change *update* the
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
            };
            _gatewayRegistry.AddOrUpdate(record);
            _gatewayRegistry.SetActive(recordId);
            _gatewayRegistry.Save();

            // Identity-token handling.
            //   - When the user provides a NEW shared token, the previous
            //     device token is no longer trusted by the gateway, so we
            //     clear the stored device tokens to force a fresh re-pair.
            //   - When the form is left blank (user is just renaming /
            //     fixing SSH), keep the existing device tokens — clearing
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
            catch { /* backup is best-effort; rollback simply skips restore */ }

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
                    previousSettings.SshTunnelRemotePort = sshConfig.RemotePort;
                    previousSettings.SshTunnelLocalPort = sshConfig.LocalPort;
                }
                previousSettings.Save();
            }

            if (useSsh)
            {
                AddResultText.Text = "Starting SSH tunnel…";
                var app = (App)Microsoft.UI.Xaml.Application.Current;
                app.EnsureSshTunnelStarted();
            }

            var snapshot = await ConnectAndWaitForDirectConnectOutcomeAsync(recordId);
            AddResultText.Text = snapshot.OperatorState == RoleConnectionState.PairingRequired
                ? $"Pairing approval required for {GatewayUrlHelper.SanitizeForDisplay(url)}."
                : $"Connected to {GatewayUrlHelper.SanitizeForDisplay(url)}.";

            // Success — leave Add mode and stop tracking the edited record.
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
                prevSshRemotePort, prevSshLocalPort,
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
                throw new TimeoutException("Timed out waiting for the gateway connection to complete.");

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

    private static GatewayConnectionSnapshot EnsureDirectConnectSucceeded(GatewayConnectionSnapshot snapshot)
    {
        if (snapshot.OperatorState == RoleConnectionState.Error)
        {
            var message = snapshot.OperatorError ?? snapshot.NodeError ?? "Gateway connection failed.";
            throw new InvalidOperationException(message);
        }
        return snapshot;
    }

    private void RollbackDirectConnect(
        string? previousActiveId, bool isNewRecord, string recordId,
        GatewayRecord? existingRecordSnapshot, SettingsManager? settings,
        string? prevGatewayUrl, bool prevUseSsh, string? prevSshUser,
        string? prevSshHost, int prevSshRemotePort, int prevSshLocalPort,
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
        // last successful pairing — forcing a full re-pair the user never
        // asked for. Skip the restore if the file changed since backup
        // (e.g. a late-arriving successful pairing wrote a fresh token in
        // the meantime — that token is more valuable than our backup).
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
                    // smaller body — that's the expected post-clear state,
                    // so treat as unchanged-from-clear and restore.
                    fileUnchanged = true;
                }
                if (fileUnchanged)
                    File.WriteAllText(identityKeyPath, identityBackup);
                // else: another writer touched the file; preserve it.
            }
            catch { /* best-effort restore; failure cannot regress further */ }
        }

        if (settings != null)
        {
            settings.GatewayUrl = prevGatewayUrl;
            settings.UseSshTunnel = prevUseSsh;
            settings.SshTunnelUser = prevSshUser;
            settings.SshTunnelHost = prevSshHost;
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
            AddResultText.Text = "Please paste a setup code.";
            return;
        }

        AddSaveButton.IsEnabled = false;
        AddResultText.Text = "Applying…";
        try
        {
            if (_connectionManager != null)
            {
                var result = await _connectionManager.ApplySetupCodeAsync(code);
                AddResultText.Text = result.Outcome switch
                {
                    SetupCodeOutcome.Success => $"✓ Applied — gateway: {SanitizeUrl(result.GatewayUrl ?? "")}",
                    SetupCodeOutcome.InvalidCode => $"✗ {result.ErrorMessage ?? "Invalid setup code"}",
                    SetupCodeOutcome.InvalidUrl => $"✗ {result.ErrorMessage ?? "Invalid URL"}",
                    SetupCodeOutcome.ConnectionFailed => $"✗ {result.ErrorMessage ?? "Connection failed"}",
                    _ => $"✗ {result.ErrorMessage ?? "Unknown error"}",
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
                var settings = _hub?.Settings;
                if (settings == null) return;
                if (!string.IsNullOrEmpty(decoded.Url))
                    settings.GatewayUrl = decoded.Url;
                settings.Save();
                AddResultText.Text = $"✓ Applied — gateway: {SanitizeUrl(decoded.Url ?? settings.GatewayUrl ?? "")}";
                _hub?.RaiseSettingsSaved();
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
            return;
        }
        var decoded = SetupCodeDecoder.Decode(code);
        if (decoded.Success)
        {
            AddSetupCodePreviewUrl.Text = $"Gateway: {decoded.Url ?? "(not specified)"}";
            var tokenHint = string.IsNullOrEmpty(decoded.Token)
                ? "(no token)"
                : decoded.Token!.Substring(0, Math.Min(8, decoded.Token.Length)) + "…";
            AddSetupCodePreviewToken.Text = $"Token: {tokenHint}";
            AddSetupCodePreviewPanel.Visibility = Visibility.Visible;
        }
        else
        {
            AddSetupCodePreviewPanel.Visibility = Visibility.Collapsed;
        }
    }

    // ─── Scan flow ───────────────────────────────────────────────────

    private bool _scanInProgress;

    /// <summary>
    /// Click handler for both the Gateways-section Scan button and the
    /// Welcome inline Scan button. Toggles scan on/off; on completion
    /// populates whichever discovered-list panel is visible.
    /// </summary>
    private async void OnScanGatewaysClicked(object sender, RoutedEventArgs e)
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
    /// Legacy entry point — kept for the now-inert Add form Scan-pane
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
        GatewaysScanButtonText.Text = "Stop";
        WelcomeScanButtonText.Text = "Stop";
        GatewaysDiscoveredSection.Visibility = Visibility.Visible;
        GatewaysDiscoveredHeader.Text = "Scanning your network…";
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
                ? "No gateways found on your network."
                : $"Discovered on your network ({gateways.Count})";
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
            GatewaysDiscoveredHeader.Text = $"Scan failed: {ex.Message}";
            GatewaysDiscoveredProgress.IsActive = false;
            GatewaysDiscoveredProgress.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _scanInProgress = false;
            GatewaysScanButtonText.Text = "Scan";
            WelcomeScanButtonText.Text = "Scan";
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
            Glyph = Helpers.FluentIconCatalog.System, // PC1 — discovered, not yet connected
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

        var addBtn = new Button { Content = "Add", Tag = gw.ConnectionUrl, VerticalAlignment = VerticalAlignment.Center };
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
        DirectTokenBox.Password = "";
        DirectNameBox.Text = "";
        AddDirectItem.IsSelected = true;
        ShowAddPane("direct");
        _userIntent = UserIntent.AddingGateway;
        RefreshFromSnapshot(_lastSnapshot);
    }

    // Legacy [Use this] handler — replaced by [Add] above. Kept inert in
    // case any legacy XAML still binds it.
    private void OnScanResultUseThis(object sender, RoutedEventArgs e)
        => OnAddDiscoveredGateway(sender, e);

    // ─── Connection lifecycle handlers (preserved verbatim semantics) ─

    private void OnDisconnect(object sender, RoutedEventArgs e)
    {
        _hub?.DisconnectAction?.Invoke();
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
            _hub?.ReconnectAction?.Invoke();
        }
        else
        {
            // User asked to disconnect.
            _hub?.DisconnectAction?.Invoke();
        }
    }

    private void OnNodeModeToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressNodeModeToggle) return;
        var settings = _hub?.Settings;
        if (settings == null) return;
        settings.EnableNodeMode = NodeModeToggle.IsOn;
        settings.Save();
        // Toggling Node mode forces a full reconnect of the gateway WS so
        // the role change registers; mask the brief transient window so the
        // gateway/operator visuals don't flicker through "Disconnected".
        BeginReconnectMask();
        _hub?.RaiseSettingsSaved();
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

    // ─── Pending pairing — populate the banner with existing semantics ─

    public void UpdateDevicePairingRequests(DevicePairingListInfo data)
    {
        DevicePairingListPanel.Children.Clear();
        var hasDevicePending = data.Pending.Count > 0;

        if (hasDevicePending)
        {
            var scopes = _hub?.GatewayClient?.GrantedOperatorScopes ?? (IReadOnlyList<string>)Array.Empty<string>();
            var canPair = OperatorScopeHelper.CanApproveDevices(scopes);
            // Build the set of fallback ids that collide across multiple
            // pending requests. When a legacy gateway omits RequestId we
            // fall back to DeviceId; if two pending requests share the
            // same DeviceId, approving from the UI would be ambiguous and
            // could act on the wrong one — disable those rows so the user
            // is forced to approve via the gateway-host CLI instead.
            var ambiguousIds = ComputeAmbiguousFallbackIds(
                data.Pending.Select(r => (r.RequestId, r.DeviceId)));
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
            var scopes = _hub?.GatewayClient?.GrantedOperatorScopes ?? (IReadOnlyList<string>)Array.Empty<string>();
            var canPair = OperatorScopeHelper.CanApproveDevices(scopes);
            // Same ambiguity guard as UpdateDevicePairingRequests above:
            // if a legacy gateway sends multiple node-pair requests with
            // the same NodeId and no RequestId, disable approve/deny on
            // those rows so the user can't pick the wrong target.
            var ambiguousIds = ComputeAmbiguousFallbackIds(
                data.Pending.Select(r => (r.RequestId, NodeId: r.NodeId)));
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
    /// 2+ pending requests where RequestId is missing — i.e. the cases
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
            ? "1 approval waiting on you"
            : $"{total} approvals waiting on you";
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
            Text = $"Device · {req.DisplayName ?? req.DeviceId}",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        var detail = req.Platform ?? "unknown";
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
            var capturedId = req.RequestId ?? req.DeviceId;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var approveBtn = new Button { Content = "Approve", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            var rejectBtn = new Button { Content = "Deny" };
            if (string.IsNullOrEmpty(capturedId))
            {
                approveBtn.IsEnabled = false;
                rejectBtn.IsEnabled = false;
            }
            approveBtn.Click += async (_, __) =>
            {
                if (string.IsNullOrEmpty(capturedId)) return;
                approveBtn.IsEnabled = false; rejectBtn.IsEnabled = false;
                try
                {
                    var client = _hub?.GatewayClient;
                    if (client != null)
                    {
                        var ok = await client.DevicePairApproveAsync(capturedId);
                        if (ok) await client.RequestDevicePairListAsync();
                        else { approveBtn.IsEnabled = true; rejectBtn.IsEnabled = true; }
                    }
                    else { approveBtn.IsEnabled = true; rejectBtn.IsEnabled = true; }
                }
                catch { approveBtn.IsEnabled = true; rejectBtn.IsEnabled = true; }
            };
            rejectBtn.Click += async (_, __) =>
            {
                if (string.IsNullOrEmpty(capturedId)) return;
                approveBtn.IsEnabled = false; rejectBtn.IsEnabled = false;
                try
                {
                    var client = _hub?.GatewayClient;
                    if (client != null)
                    {
                        var ok = await client.DevicePairRejectAsync(capturedId);
                        if (ok) await client.RequestDevicePairListAsync();
                        else { approveBtn.IsEnabled = true; rejectBtn.IsEnabled = true; }
                    }
                    else { approveBtn.IsEnabled = true; rejectBtn.IsEnabled = true; }
                }
                catch { approveBtn.IsEnabled = true; rejectBtn.IsEnabled = true; }
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
            Text = $"Node · {req.DisplayName ?? req.NodeId}",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        var detail = req.Platform ?? "unknown";
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
                Text = "⚠️ Repair request",
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = ResolveBrush("SystemFillColorCautionBrush"),
            });
        }
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        if (canPair)
        {
            // Same fallback as device pairing: legacy gateways may omit
            // RequestId, so prefer NodeId. If neither is present, disable
            // the buttons so the user can't trigger a no-op call.
            var capturedId = req.RequestId ?? req.NodeId;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var approveBtn = new Button { Content = "Approve", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            var rejectBtn = new Button { Content = "Deny" };
            if (string.IsNullOrEmpty(capturedId))
            {
                approveBtn.IsEnabled = false;
                rejectBtn.IsEnabled = false;
            }
            approveBtn.Click += async (_, __) =>
            {
                if (string.IsNullOrEmpty(capturedId)) return;
                approveBtn.IsEnabled = false; rejectBtn.IsEnabled = false;
                try
                {
                    var client = _hub?.GatewayClient;
                    if (client != null)
                    {
                        var ok = await client.NodePairApproveAsync(capturedId);
                        if (!ok) { approveBtn.IsEnabled = true; rejectBtn.IsEnabled = true; }
                    }
                    else { approveBtn.IsEnabled = true; rejectBtn.IsEnabled = true; }
                }
                catch { approveBtn.IsEnabled = true; rejectBtn.IsEnabled = true; }
            };
            rejectBtn.Click += async (_, __) =>
            {
                if (string.IsNullOrEmpty(capturedId)) return;
                approveBtn.IsEnabled = false; rejectBtn.IsEnabled = false;
                try
                {
                    var client = _hub?.GatewayClient;
                    if (client != null)
                    {
                        var ok = await client.NodePairRejectAsync(capturedId);
                        if (!ok) { approveBtn.IsEnabled = true; rejectBtn.IsEnabled = true; }
                    }
                    else { approveBtn.IsEnabled = true; rejectBtn.IsEnabled = true; }
                }
                catch { approveBtn.IsEnabled = true; rejectBtn.IsEnabled = true; }
            };
            buttons.Children.Add(approveBtn);
            buttons.Children.Add(rejectBtn);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);
        }
        card.Child = grid;
        return card;
    }

    // ─── Auth error guidance (preserved) ─────────────────────────────

    private static string GetAuthErrorGuidance(string error)
    {
        if (error.Contains("token", StringComparison.OrdinalIgnoreCase))
            return $"{error}\n\nPaste a new setup code from the Add Gateway flow.";
        if (error.Contains("pairing", StringComparison.OrdinalIgnoreCase))
            return $"{error}\n\nYour device needs approval on the gateway host.";
        if (error.Contains("password", StringComparison.OrdinalIgnoreCase))
            return $"{error}\n\nThis gateway requires password authentication.";
        if (error.Contains("signature", StringComparison.OrdinalIgnoreCase))
            return $"{error}\n\nThe gateway may require a different auth protocol version.";
        return $"{error}\n\nCheck your connection settings and try again.";
    }

    private static string SanitizeUrl(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return uri.Port > 0 ? $"{uri.Scheme}://{uri.Host}:{uri.Port}" : $"{uri.Scheme}://{uri.Host}";
        }
        catch { }
        return url;
    }
}
