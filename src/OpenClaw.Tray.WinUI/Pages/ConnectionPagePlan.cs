using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OpenClawTray.Pages;

// ───────────────────────────────────────────────────────────────────────────
// Pure projection of (settings + connection snapshot + active gateway record
// + gateway self-info + saved-gateway count + pending-approval count)
// into the visual state of every region on the new ConnectionPage:
//
//   • Page mode (Lobby / Cockpit / Recovery)
//   • Status strip (glyph, accent, headline, sub, primary CTA, progress)
//   • Operator card state
//   • Node card state + optional approve command
//   • Recovery details (auth / server / network / pairing)
//   • Active gateway display strings
//
// No I/O, no UI types, no settings mutation. Drives all VisualStateManager
// state changes from a single deterministic function. Trivially unit-testable.
//
// IMPORTANT: This file lives in the Tray.WinUI layer for proximity to the
// page, but it MUST NOT call into GatewayConnectionManager/Registry — that
// would couple a pure projection to live services. Code-behind is responsible
// for collecting the inputs and applying the outputs.
// ───────────────────────────────────────────────────────────────────────────

/// <summary>High-level layout mode of the page.</summary>
internal enum ConnectionPageMode
{
    /// <summary>Registry is empty. Welcome card with Add tiles.</summary>
    Welcome,

    /// <summary>Default mode: status + (Operator+Node when connected) + always-visible gateways list.</summary>
    Cockpit,

    /// <summary>Active gateway is failing; focused recovery help block above the gateways list.</summary>
    Recovery,

    /// <summary>User is in the "Add a gateway" sub-view; bottom section swapped to the form.</summary>
    AddGateway,
}

/// <summary>Status severity that maps to a ThemeResource brush via <see cref="ConnectionPagePlan.AccentToBrushKey"/>.</summary>
internal enum ConnectionAccent
{
    Neutral,
    Success,
    Caution,
    Critical,
}

/// <summary>Which lifecycle action the status strip's primary CTA invokes.</summary>
internal enum ConnectionPrimaryAction
{
    None,
    Connect,
    Reconnect,
    Retry,
    Cancel,
    RestartTunnel,
    BackToCockpit,
    // CopyApproveCommand and Rep retired: the inline RecoveryApproveCmdBlock
    // Copy button and the RecoveryAuthPasteBlock paste-and-Apply affordance
    // own those flows; surfacing them in the strip header on top of a
    // critical/caution status read as a loud red CTA duplicating the inline
    // controls below it.
}

/// <summary>Visual state of the Operator card in Cockpit mode.</summary>
internal enum OperatorCardState
{
    Hidden,
    Active,
    Idle,
    Connecting,
    Paused,
}

/// <summary>Visual state of the Node card in Cockpit mode.</summary>
internal enum NodeCardState
{
    Hidden,
    Off,
    /// <summary>Gateway node is off, local MCP server is enabled.</summary>
    OffMcpOnly,
    OnHealthy,
    /// <summary>Node role is connecting / starting up (not yet ready).</summary>
    OnNodeConnecting,
    OnPermissionsIncomplete,
    OnNodeApprovalRequired,
    OnNodeReapprovalRequired,
    OnNodePairingRequired,
    OnNodeRejected,
    OnNodeRateLimited,
    OnNodeError,
}

/// <summary>Error sub-category used to pick the Recovery body content.</summary>
internal enum RecoveryCategory
{
    None,
    Auth,
    Pairing,
    Network,
    Server,
    Tunnel,
    /// <summary>Authenticated but missing a required scope — re-pair for higher scopes.</summary>
    Scope,
    /// <summary>Stored device token rotated/revoked — re-pair to repair.</summary>
    TokenDrift,
    /// <summary>TLS/cleartext transport problem — switch to wss:// or a tunnel.</summary>
    Tls,
    /// <summary>Gateway is temporarily rate-limiting this client.</summary>
    RateLimited,
    /// <summary>A managed Tailscale endpoint cannot be reached from this Companion.</summary>
    Tailscale,
}

/// <summary>
/// Final projection consumed by ConnectionPage.xaml.cs. Apply to UI via
/// VisualStateManager + simple property setters.
/// </summary>
internal sealed record ConnectionPagePlan
{
    public ConnectionPageMode Mode { get; init; } = ConnectionPageMode.Welcome;

    // ─── Status strip ───
    public string StripGlyph { get; init; } = OpenClawTray.Helpers.FluentIconCatalog.System; // PC1 default — "no gateway yet"
    public ConnectionAccent StripAccent { get; init; } = ConnectionAccent.Neutral;
    public string StripHeadline { get; init; } = "Not connected";
    public string StripSub { get; init; } = "";
    public bool StripShowProgress { get; init; }
    public string? StripPrimaryLabel { get; init; }
    public ConnectionPrimaryAction StripPrimaryAction { get; init; } = ConnectionPrimaryAction.None;

    // ─── Operator card ───
    public OperatorCardState OperatorCard { get; init; } = OperatorCardState.Hidden;

    // ─── Node card ───
    public NodeCardState NodeCard { get; init; } = NodeCardState.Hidden;
    /// <summary>For OnNodePairingRequired — the exact CLI command to copy/paste.</summary>
    public string? NodeApproveCommand { get; init; }
    /// <summary>Copy-only command for node-list command-trust approval or reapproval.</summary>
    public string? NodeTrustApproveCommand { get; init; }
    /// <summary>True only when the trust command approves the reported request; false for discovery commands.</summary>
    public bool NodeTrustCommandApprovesRequest { get; init; }
    public GatewayNodeApprovalState NodeApprovalState { get; init; }
    public IReadOnlyList<string> NodeEffectiveCapabilities { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> NodeEffectiveCommands { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, bool> NodeEffectivePermissions { get; init; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> NodePendingDeclaredCapabilities { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> NodePendingDeclaredCommands { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, bool> NodePendingDeclaredPermissions { get; init; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    /// <summary>For OnNodeError — sanitized error string.</summary>
    public string? NodeErrorDetail { get; init; }

    // ─── Recovery sub-screen ───
    public RecoveryCategory Recovery { get; init; } = RecoveryCategory.None;
    public string? RecoveryDetail { get; init; }
    /// <summary>For RecoveryCategory.Pairing — the CLI command the user should run.</summary>
    public string? RecoveryApproveCommand { get; init; }

    // ─── Active gateway display ───
    public string? ActiveGatewayDisplayName { get; init; }
    public string? ActiveGatewayDetailLine { get; init; }
    public bool ActiveGatewayHasSshTunnel { get; init; }

    // ─── Inputs the projection chose to keep around for code-behind ───
    /// <summary>The gateway record the strip is reporting on (active or "the one we were on").</summary>
    public string? RelevantGatewayId { get; init; }

    /// <summary>
    /// Pure builder. Given current state, returns the visual plan.
    /// </summary>
    /// <param name="snap">Live snapshot from GatewayConnectionManager.</param>
    /// <param name="activeRecord">The currently active gateway record (null if none).</param>
    /// <param name="self">Hello-ok response from the gateway (null until connected).</param>
    /// <param name="settings">App settings (capability flags, etc.).</param>
    /// <param name="savedGatewayCount">Total saved gateways (governs Welcome vs Cockpit).</param>
    /// <param name="userIntent">User-driven mode override ("adding"); pass <c>UserIntent.None</c> for default.</param>
    /// <param name="localNode">Gateway-reported local node record, including effective and pending approval surfaces.</param>
    public static ConnectionPagePlan Build(
        GatewayConnectionSnapshot snap,
        GatewayRecord? activeRecord,
        GatewaySelfInfo? self,
        SettingsManager? settings,
        int savedGatewayCount,
        UserIntent userIntent = UserIntent.None,
        GatewayNodeInfo? localNode = null)
    {
        var displayName = activeRecord?.FriendlyName
            ?? activeRecord?.Url
            ?? snap.GatewayName
            ?? "gateway";

        var plan = BuildDerived(snap, activeRecord, self, settings, savedGatewayCount, displayName);

        // ─── User-intent override: AddGateway sub-view ───
        // Keeps Operator/Node cards visible in the strip+roles area while the
        // bottom section is swapped to the Add form.
        if (userIntent == UserIntent.AddingGateway)
            plan = plan with { Mode = ConnectionPageMode.AddGateway };

        return ApplyNodeListApproval(
            plan,
            localNode,
            snap,
            settings);
    }

    private static ConnectionPagePlan BuildDerived(
        GatewayConnectionSnapshot snap,
        GatewayRecord? activeRecord,
        GatewaySelfInfo? self,
        SettingsManager? settings,
        int savedGatewayCount,
        string displayName)
    {
        // ─── Derived layout ───
        return snap.OverallState switch
        {
            OverallConnectionState.Idle => BuildIdle(savedGatewayCount, activeRecord, settings),

            OverallConnectionState.Connecting => BuildCockpitConnecting(snap, activeRecord, displayName),

            OverallConnectionState.Connected or OverallConnectionState.Ready =>
                BuildCockpitConnected(snap, activeRecord, self, settings, displayName),

            OverallConnectionState.Degraded =>
                BuildCockpitDegraded(snap, activeRecord, self, settings, displayName),

            OverallConnectionState.PairingRequired =>
                BuildPairingRequired(snap, activeRecord, settings, displayName),

            OverallConnectionState.Error =>
                BuildRecoveryFromError(snap, activeRecord, displayName),

            OverallConnectionState.Disconnecting => new ConnectionPagePlan
            {
                Mode = ConnectionPageMode.Cockpit,
                StripGlyph = OpenClawTray.Helpers.FluentIconCatalog.Sync,
                StripAccent = ConnectionAccent.Neutral,
                StripHeadline = "Disconnecting…",
                StripSub = displayName,
                StripShowProgress = true,
                ActiveGatewayDisplayName = displayName,
                RelevantGatewayId = activeRecord?.Id,
                ActiveGatewayHasSshTunnel = activeRecord?.SshTunnel != null,
            },

            _ => BuildIdle(savedGatewayCount, activeRecord, settings),
        };
    }

    // ───────────────────────────────────────────────────────────────────
    // Mode builders
    // ───────────────────────────────────────────────────────────────────

    private static ConnectionPagePlan BuildIdle(
        int savedCount,
        GatewayRecord? activeRecord,
        SettingsManager? settings)
    {
        var idleNodeCard = BuildIdleNodeCardState(settings);
        if (savedCount == 0)
        {
            return new ConnectionPagePlan
            {
                Mode = ConnectionPageMode.Welcome,
                StripGlyph = OpenClawTray.Helpers.FluentIconCatalog.System,
                StripAccent = ConnectionAccent.Neutral,
                StripHeadline = "No gateway yet",
                StripSub = "Add a gateway to get started.",
                NodeCard = idleNodeCard,
            };
        }

        // Saved gateways exist but none active — drop straight into Cockpit
        // (role panels hide themselves unless local MCP-only status is visible).
        return new ConnectionPagePlan
        {
            Mode = ConnectionPageMode.Cockpit,
            StripGlyph = OpenClawTray.Helpers.FluentIconCatalog.System,
            StripAccent = ConnectionAccent.Neutral,
            StripHeadline = "Not connected",
            StripSub = "Pick a gateway below, or add a new one.",
            NodeCard = idleNodeCard,
            RelevantGatewayId = activeRecord?.Id,
        };
    }

    private static ConnectionPagePlan BuildCockpitConnecting(
        GatewayConnectionSnapshot snap, GatewayRecord? rec, string name)
    {
        var url = ConnectionCardPlanSanitizer.SanitizeGatewayUrl(rec?.Url ?? snap.GatewayUrl);
        return new ConnectionPagePlan
        {
            Mode = ConnectionPageMode.Cockpit,
            StripGlyph = OpenClawTray.Helpers.FluentIconCatalog.Sync, // replaced visually by ProgressRing
            StripAccent = ConnectionAccent.Caution,
            StripHeadline = "Connecting…",
            StripSub = !string.IsNullOrEmpty(url) ? url : "Reaching gateway",
            StripShowProgress = true,
            StripPrimaryLabel = "Cancel",
            StripPrimaryAction = ConnectionPrimaryAction.Cancel,
            OperatorCard = OperatorCardState.Connecting,
            NodeCard = NodeCardState.Hidden, // hidden until operator connects
            ActiveGatewayDisplayName = name,
            ActiveGatewayDetailLine = url,
            ActiveGatewayHasSshTunnel = rec?.SshTunnel != null,
            RelevantGatewayId = rec?.Id,
        };
    }

    private static ConnectionPagePlan BuildCockpitConnected(
        GatewayConnectionSnapshot snap,
        GatewayRecord? rec,
        GatewaySelfInfo? self,
        SettingsManager? settings,
        string name)
    {
        var url = ConnectionCardPlanSanitizer.SanitizeGatewayUrl(rec?.Url ?? snap.GatewayUrl);
        var sub = BuildConnectedDetailLine(rec, self, snap);

        return new ConnectionPagePlan
        {
            Mode = ConnectionPageMode.Cockpit,
            StripGlyph = OpenClawTray.Helpers.FluentIconCatalog.StatusOk,
            StripAccent = ConnectionAccent.Success,
            StripHeadline = "Connected",
            StripSub = sub,
            OperatorCard = OperatorCardState.Active,
            NodeCard = BuildNodeCardState(snap, settings),
            NodeApproveCommand = BuildNodeApproveCommand(snap),
            NodeErrorDetail = ExtractNodeErrorDetail(snap),
            ActiveGatewayDisplayName = name,
            ActiveGatewayDetailLine = sub,
            ActiveGatewayHasSshTunnel = rec?.SshTunnel != null,
            RelevantGatewayId = rec?.Id,
        };
    }

    private static ConnectionPagePlan BuildCockpitDegraded(
        GatewayConnectionSnapshot snap,
        GatewayRecord? rec,
        GatewaySelfInfo? self,
        SettingsManager? settings,
        string name)
    {
        var reason = !string.IsNullOrWhiteSpace(snap.NodeError)
            ? ConnectionCardPlanSanitizer.Sanitize(snap.NodeError!)
            : snap.NodeState switch
            {
                RoleConnectionState.PairingRejected => "Node pairing was rejected.",
                RoleConnectionState.RateLimited => "Node is rate-limited by the gateway.",
                RoleConnectionState.Error => "Node reported an error.",
                RoleConnectionState.Idle when snap.NodeConnectionIntended => "Node mode is enabled, but the node has not connected.",
                _ => "Connection is impaired.",
            };

        // SSH-tunnel-specific framing if the tunnel is the likely cause
        bool tunnelLikely = rec?.SshTunnel != null &&
                            (reason.Contains("tunnel", StringComparison.OrdinalIgnoreCase) ||
                             reason.Contains("ssh", StringComparison.OrdinalIgnoreCase));

        return new ConnectionPagePlan
        {
            Mode = ConnectionPageMode.Cockpit,
            StripGlyph = OpenClawTray.Helpers.FluentIconCatalog.StatusWarn,
            StripAccent = ConnectionAccent.Caution,
            StripHeadline = tunnelLikely ? "Connection degraded" : "Connection degraded",
            StripSub = reason,
            StripPrimaryLabel = tunnelLikely ? "Restart tunnel" : "Reconnect",
            StripPrimaryAction = tunnelLikely ? ConnectionPrimaryAction.RestartTunnel : ConnectionPrimaryAction.Reconnect,
            OperatorCard = OperatorCardState.Paused,
            NodeCard = BuildNodeCardState(snap, settings),
            NodeApproveCommand = BuildNodeApproveCommand(snap),
            NodeErrorDetail = ExtractNodeErrorDetail(snap),
            ActiveGatewayDisplayName = name,
            ActiveGatewayDetailLine = BuildConnectedDetailLine(rec, self, snap),
            ActiveGatewayHasSshTunnel = rec?.SshTunnel != null,
            RelevantGatewayId = rec?.Id,
        };
    }

    private static ConnectionPagePlan BuildPairingRequired(
        GatewayConnectionSnapshot snap,
        GatewayRecord? rec,
        SettingsManager? settings,
        string name)
    {
        // Pairing can be either operator-level (device pairing — full Recovery) or
        // node-level (operator is fine, just Node toggle awaits approval — Cockpit
        // with the Node card in OnNodePairingRequired).
        var operatorPairing = snap.OperatorState == RoleConnectionState.PairingRequired ||
                              snap.OperatorPairingRequired;

        if (operatorPairing)
        {
            var cmd = BuildDevicePairingApproveCommand(snap);
            return new ConnectionPagePlan
            {
                Mode = ConnectionPageMode.Recovery,
                Recovery = RecoveryCategory.Pairing,
                StripGlyph = OpenClawTray.Helpers.FluentIconCatalog.Lock,
                StripAccent = ConnectionAccent.Caution,
                StripHeadline = "Awaiting approval",
                StripSub = "Approve this client on the gateway host. Connection will resume automatically.",
                // No strip-level CTA here: the inline Copy button in
                // RecoveryApproveCmdBlock owns the copy action, and surfacing
                // an accent "Copy command" CTA in the strip header on top of
                // a caution status read as a loud red button next to the
                // pairing instructions. ConnectionToggle still allows the
                // user to disconnect; reconnection auto-resumes once approved.
                StripPrimaryLabel = null,
                StripPrimaryAction = ConnectionPrimaryAction.None,
                RecoveryApproveCommand = cmd,
                RecoveryDetail = "Run on the gateway host:",
                ActiveGatewayDisplayName = name,
                RelevantGatewayId = rec?.Id,
                ActiveGatewayHasSshTunnel = rec?.SshTunnel != null,
            };
        }

        // Otherwise node-level pairing → stay in Cockpit, surface in Node card.
        return BuildCockpitConnected(snap, rec, null, settings, name) with
        {
            NodeCard = NodeCardState.OnNodePairingRequired,
            NodeApproveCommand = BuildNodeApproveCommand(snap),
        };
    }

    private static ConnectionPagePlan BuildRecoveryFromError(
        GatewayConnectionSnapshot snap, GatewayRecord? rec, string name)
    {
        var errRaw = snap.OperatorError ?? snap.NodeError ?? "";
        var err = ConnectionCardPlanSanitizer.Sanitize(errRaw);
        var category = ClassifyError(err);
        var url = ConnectionCardPlanSanitizer.SanitizeGatewayUrl(rec?.Url ?? snap.GatewayUrl);

        if (category == RecoveryCategory.Network &&
            IsManagedTailscaleGateway(rec, rec?.Url ?? snap.GatewayUrl))
        {
            return new ConnectionPagePlan
            {
                Mode = ConnectionPageMode.Recovery,
                Recovery = RecoveryCategory.Tailscale,
                StripGlyph = OpenClawTray.Helpers.FluentIconCatalog.StatusErr,
                StripAccent = ConnectionAccent.Critical,
                StripHeadline = "Tailscale gateway unavailable",
                StripSub = string.IsNullOrEmpty(err)
                    ? $"Can't reach the private tailnet endpoint {url}."
                    : err,
                StripPrimaryLabel = "Retry",
                StripPrimaryAction = ConnectionPrimaryAction.Retry,
                RecoveryDetail = err,
                ActiveGatewayDisplayName = name,
                ActiveGatewayDetailLine = url,
                ActiveGatewayHasSshTunnel = false,
                RelevantGatewayId = rec?.Id,
            };
        }

        return category switch
        {
            RecoveryCategory.Auth => new ConnectionPagePlan
            {
                Mode = ConnectionPageMode.Recovery,
                Recovery = RecoveryCategory.Auth,
                StripGlyph = OpenClawTray.Helpers.FluentIconCatalog.StatusErr,
                StripAccent = ConnectionAccent.Critical,
                StripHeadline = "Authentication failed",
                StripSub = string.IsNullOrEmpty(err)
                    ? $"Token for {name} is no longer valid."
                    : err,
                // No strip-level "Re-pair" CTA: the RecoveryAuthPasteBlock
                // below the strip already exposes a paste-setup-code + Apply
                // affordance, which IS the re-pair flow. A strip CTA on top
                // of a critical error read as a loud red button duplicating
                // an action the user can already see beneath it.
                StripPrimaryLabel = null,
                StripPrimaryAction = ConnectionPrimaryAction.None,
                ActiveGatewayDisplayName = name,
                ActiveGatewayDetailLine = url,
                ActiveGatewayHasSshTunnel = rec?.SshTunnel != null,
                RelevantGatewayId = rec?.Id,
            },

            // Stored device token rotated/revoked — the fix is to re-pair, not
            // retry. Same paste-setup-code affordance as Auth, clearer copy.
            RecoveryCategory.TokenDrift => new ConnectionPagePlan
            {
                Mode = ConnectionPageMode.Recovery,
                Recovery = RecoveryCategory.TokenDrift,
                StripGlyph = OpenClawTray.Helpers.FluentIconCatalog.StatusErr,
                StripAccent = ConnectionAccent.Critical,
                StripHeadline = "Device needs re-pairing",
                StripSub = string.IsNullOrEmpty(err)
                    ? $"The saved device token for {name} is no longer trusted by the gateway."
                    : err,
                StripPrimaryLabel = null,
                StripPrimaryAction = ConnectionPrimaryAction.None,
                ActiveGatewayDisplayName = name,
                ActiveGatewayDetailLine = url,
                ActiveGatewayHasSshTunnel = rec?.SshTunnel != null,
                RelevantGatewayId = rec?.Id,
            },

            // Authenticated but under-privileged — re-pair to request the scopes
            // this device needs (e.g. operator.admin / operator.pairing).
            RecoveryCategory.Scope => new ConnectionPagePlan
            {
                Mode = ConnectionPageMode.Recovery,
                Recovery = RecoveryCategory.Scope,
                StripGlyph = OpenClawTray.Helpers.FluentIconCatalog.Lock,
                StripAccent = ConnectionAccent.Critical,
                StripHeadline = "Not enough access",
                StripSub = string.IsNullOrEmpty(err)
                    ? $"This device is connected but lacks the scopes it needs on {name}."
                    : err,
                StripPrimaryLabel = null,
                StripPrimaryAction = ConnectionPrimaryAction.None,
                ActiveGatewayDisplayName = name,
                ActiveGatewayDetailLine = url,
                ActiveGatewayHasSshTunnel = rec?.SshTunnel != null,
                RelevantGatewayId = rec?.Id,
            },

            // TLS/cleartext transport problem — steer toward wss:// or a tunnel.
            RecoveryCategory.Tls => new ConnectionPagePlan
            {
                Mode = ConnectionPageMode.Recovery,
                Recovery = RecoveryCategory.Tls,
                StripGlyph = OpenClawTray.Helpers.FluentIconCatalog.StatusErr,
                StripAccent = ConnectionAccent.Critical,
                StripHeadline = "Secure connection failed",
                StripSub = string.IsNullOrEmpty(err)
                    ? "The gateway's transport could not be secured."
                    : err,
                StripPrimaryLabel = "Retry",
                StripPrimaryAction = ConnectionPrimaryAction.Retry,
                RecoveryDetail = err,
                ActiveGatewayDisplayName = name,
                ActiveGatewayDetailLine = url,
                ActiveGatewayHasSshTunnel = rec?.SshTunnel != null,
                RelevantGatewayId = rec?.Id,
            },

            RecoveryCategory.RateLimited => new ConnectionPagePlan
            {
                Mode = ConnectionPageMode.Recovery,
                Recovery = RecoveryCategory.RateLimited,
                StripGlyph = OpenClawTray.Helpers.FluentIconCatalog.StatusWarn,
                StripAccent = ConnectionAccent.Caution,
                StripHeadline = "Too many failed attempts",
                StripSub = string.IsNullOrEmpty(err)
                    ? "The gateway is temporarily limiting connection attempts from this client."
                    : err,
                StripPrimaryLabel = null,
                StripPrimaryAction = ConnectionPrimaryAction.None,
                RecoveryDetail = err,
                ActiveGatewayDisplayName = name,
                ActiveGatewayDetailLine = url,
                ActiveGatewayHasSshTunnel = rec?.SshTunnel != null,
                RelevantGatewayId = rec?.Id,
            },

            RecoveryCategory.Tunnel => new ConnectionPagePlan
            {
                Mode = ConnectionPageMode.Recovery,
                Recovery = RecoveryCategory.Tunnel,
                StripGlyph = OpenClawTray.Helpers.FluentIconCatalog.StatusErr,
                StripAccent = ConnectionAccent.Critical,
                StripHeadline = "Can't reach gateway",
                StripSub = "SSH tunnel is down — " + (err.Length > 0 ? err : "last attempt failed."),
                StripPrimaryLabel = "Restart tunnel",
                StripPrimaryAction = ConnectionPrimaryAction.RestartTunnel,
                RecoveryDetail = err,
                ActiveGatewayDisplayName = name,
                ActiveGatewayDetailLine = url,
                ActiveGatewayHasSshTunnel = true,
                RelevantGatewayId = rec?.Id,
            },

            RecoveryCategory.Server => new ConnectionPagePlan
            {
                Mode = ConnectionPageMode.Recovery,
                Recovery = RecoveryCategory.Server,
                StripGlyph = OpenClawTray.Helpers.FluentIconCatalog.StatusErr,
                StripAccent = ConnectionAccent.Critical,
                StripHeadline = "Can't reach gateway",
                StripSub = string.IsNullOrEmpty(err) ? "Gateway returned an error." : err,
                StripPrimaryLabel = "Retry",
                StripPrimaryAction = ConnectionPrimaryAction.Retry,
                RecoveryDetail = err,
                ActiveGatewayDisplayName = name,
                ActiveGatewayDetailLine = url,
                ActiveGatewayHasSshTunnel = rec?.SshTunnel != null,
                RelevantGatewayId = rec?.Id,
            },

            // Default: Network
            _ => new ConnectionPagePlan
            {
                Mode = ConnectionPageMode.Recovery,
                Recovery = RecoveryCategory.Network,
                StripGlyph = OpenClawTray.Helpers.FluentIconCatalog.StatusErr,
                StripAccent = ConnectionAccent.Critical,
                StripHeadline = "Can't reach gateway",
                StripSub = string.IsNullOrEmpty(err)
                    ? (string.IsNullOrEmpty(url)
                        ? "Connection refused."
                        : $"Connection refused at {url}.")
                    : err,
                StripPrimaryLabel = "Retry",
                StripPrimaryAction = ConnectionPrimaryAction.Retry,
                RecoveryDetail = err,
                ActiveGatewayDisplayName = name,
                ActiveGatewayDetailLine = url,
                ActiveGatewayHasSshTunnel = rec?.SshTunnel != null,
                RelevantGatewayId = rec?.Id,
            },
        };
    }

    // ───────────────────────────────────────────────────────────────────
    // Card state helpers
    // ───────────────────────────────────────────────────────────────────

    private static ConnectionPagePlan ApplyNodeListApproval(
        ConnectionPagePlan plan,
        GatewayNodeInfo? localNode,
        GatewayConnectionSnapshot snap,
        SettingsManager? settings)
    {
        var pairingApprovalKind = snap.NodePairingApprovalKind;
        var pairingRequestId = snap.NodePairingRequestId;
        var isPendingTrustApproval = localNode?.ApprovalState is
            GatewayNodeApprovalState.PendingApproval or
            GatewayNodeApprovalState.PendingReapproval;
        var nodeConnectingAllowsTrustOverride =
            plan.NodeCard == NodeCardState.Hidden &&
            settings?.EnableNodeMode == true &&
            snap.OperatorState == RoleConnectionState.Connected &&
            snap.NodeState == RoleConnectionState.Connecting;
        var nodeCardAllowsTrustOverride = plan.NodeCard is
            NodeCardState.OnHealthy or
            NodeCardState.OnPermissionsIncomplete or
            NodeCardState.OnNodePairingRequired or
            NodeCardState.OnNodeConnecting ||
            nodeConnectingAllowsTrustOverride;
        // Authoritative node-list trust can override any non-device-pair card.
        // Snapshot fallback is narrower: Unknown stays on discovery-only pairing UI.
        var nodeListTrustOwnsApprovalUx =
            isPendingTrustApproval &&
            nodeCardAllowsTrustOverride &&
            pairingApprovalKind != PairingApprovalKind.DevicePair;
        var snapshotTrustOwnsApprovalUx =
            plan.NodeCard == NodeCardState.OnNodePairingRequired &&
            pairingApprovalKind == PairingApprovalKind.NodePair;
        var nodeTrustOwnsApprovalUx =
            nodeListTrustOwnsApprovalUx ||
            snapshotTrustOwnsApprovalUx;
        var nodeCard = plan.NodeCard;
        if (nodeTrustOwnsApprovalUx)
        {
            nodeCard = localNode?.ApprovalState switch
            {
                GatewayNodeApprovalState.PendingReapproval => NodeCardState.OnNodeReapprovalRequired,
                _ => NodeCardState.OnNodeApprovalRequired
            };
        }

        var trustRequestId = isPendingTrustApproval
            ? localNode!.PendingRequestId
            : pairingRequestId;
        var approvalCommand = "";
        var hasApprovalCommand = nodeTrustOwnsApprovalUx &&
            CommandCenterDiagnostics.TryBuildNodeApprovalCommand(
                trustRequestId,
                out approvalCommand);

        return plan with
        {
            NodeCard = nodeCard,
            NodeApproveCommand = nodeTrustOwnsApprovalUx ? null : plan.NodeApproveCommand,
            NodeApprovalState = localNode?.ApprovalState ?? plan.NodeApprovalState,
            NodeTrustApproveCommand = nodeTrustOwnsApprovalUx
                ? hasApprovalCommand
                    ? approvalCommand
                    : "openclaw nodes pending"
                : null,
            NodeTrustCommandApprovesRequest = hasApprovalCommand,
            NodeEffectiveCapabilities = localNode != null
                ? localNode.Capabilities.ToArray()
                : plan.NodeEffectiveCapabilities,
            NodeEffectiveCommands = localNode != null
                ? localNode.Commands.ToArray()
                : plan.NodeEffectiveCommands,
            NodeEffectivePermissions = localNode != null
                ? new Dictionary<string, bool>(localNode.Permissions, StringComparer.OrdinalIgnoreCase)
                : plan.NodeEffectivePermissions,
            NodePendingDeclaredCapabilities = localNode != null
                ? localNode.PendingDeclaredCapabilities.ToArray()
                : plan.NodePendingDeclaredCapabilities,
            NodePendingDeclaredCommands = localNode != null
                ? localNode.PendingDeclaredCommands.ToArray()
                : plan.NodePendingDeclaredCommands,
            NodePendingDeclaredPermissions = localNode != null
                ? new Dictionary<string, bool>(localNode.PendingDeclaredPermissions, StringComparer.OrdinalIgnoreCase)
                : plan.NodePendingDeclaredPermissions
        };
    }

    private static NodeCardState BuildNodeCardState(GatewayConnectionSnapshot snap, SettingsManager? settings)
    {
        if (settings == null) return NodeCardState.Hidden;

        if (!settings.EnableNodeMode)
            return settings.EnableMcpServer ? NodeCardState.OffMcpOnly : NodeCardState.Off;

        if (snap.OperatorState != RoleConnectionState.Connected)
            return NodeCardState.Off;

        return snap.NodeState switch
        {
            RoleConnectionState.Connecting => NodeCardState.OnNodeConnecting,
            RoleConnectionState.PairingRequired => NodeCardState.OnNodePairingRequired,
            RoleConnectionState.PairingRejected => NodeCardState.OnNodeRejected,
            RoleConnectionState.RateLimited => NodeCardState.OnNodeRateLimited,
            RoleConnectionState.Error => NodeCardState.OnNodeError,
            RoleConnectionState.Idle when snap.NodeConnectionIntended => NodeCardState.OnNodeError,
            _ when CountEnabledCapabilities(settings) == 0 => NodeCardState.OnPermissionsIncomplete,
            _ => NodeCardState.OnHealthy,
        };
    }

    private static NodeCardState BuildIdleNodeCardState(SettingsManager? settings)
    {
        if (settings == null) return NodeCardState.Hidden;

        return !settings.EnableNodeMode && settings.EnableMcpServer
            ? NodeCardState.OffMcpOnly
            : NodeCardState.Hidden;
    }

    private static string? BuildNodeApproveCommand(GatewayConnectionSnapshot snap)
    {
        if (snap.NodeState != RoleConnectionState.PairingRequired) return null;
        var reqId = !string.IsNullOrEmpty(snap.NodePairingRequestId)
            ? ConnectionCardPlanSanitizer.Sanitize(snap.NodePairingRequestId!, maxLen: 64)
            : null;
        // Exact approval commands require an explicit kind. Unknown legacy
        // events stay discovery-only so operators can classify the request.
        // Missing requestId is a real-world case on older gateway builds:
        // emit a single discovery command the
        // user can paste verbatim into any shell — they then pick a
        // requestId from its output and run approve manually. We avoid
        // embedding a "# then:" or "<requestId>" follow-up in the clipboard
        // text because `#` is treated as a literal arg by cmd.exe and `<`
        // is parsed as input redirection — pasting either breaks for
        // Windows-cmd users.
        if (snap.NodePairingApprovalKind == PairingApprovalKind.DevicePair)
        {
            return CommandCenterDiagnostics.BuildDeviceApprovalRepairCommand(reqId);
        }

        if (snap.NodePairingApprovalKind == PairingApprovalKind.NodePair)
        {
            return reqId != null
                ? $"openclaw nodes approve {reqId}"
                : "openclaw nodes pending";
        }

        return CommandCenterDiagnostics.BuildUnknownPairingDiscoveryCommands();
    }

    private static string? BuildDevicePairingApproveCommand(GatewayConnectionSnapshot snap)
    {
        if (!snap.OperatorPairingRequired && snap.OperatorState != RoleConnectionState.PairingRequired)
            return null;
        var reqId = !string.IsNullOrEmpty(snap.OperatorPairingRequestId)
            ? ConnectionCardPlanSanitizer.Sanitize(snap.OperatorPairingRequestId!, maxLen: 64)
            : null;
        // Noun-first per openclaw/src/cli/devices-cli.ts:
        // `openclaw devices approve <requestId>`. Mirror BuildNodeApproveCommand:
        // single discovery command in the clipboard, no shell-hostile suffix.
        return reqId != null
            ? $"openclaw devices approve {reqId}"
            : "openclaw devices list";
    }

    private static string? ExtractNodeErrorDetail(GatewayConnectionSnapshot snap)
    {
        if (string.IsNullOrWhiteSpace(snap.NodeError)) return null;
        return ConnectionCardPlanSanitizer.Sanitize(snap.NodeError!);
    }

    private static string BuildConnectedDetailLine(GatewayRecord? rec, GatewaySelfInfo? self, GatewayConnectionSnapshot snap)
    {
        var bits = new List<string>(4);
        var url = ConnectionCardPlanSanitizer.SanitizeGatewayUrl(rec?.Url);
        if (!string.IsNullOrEmpty(url)) bits.Add(url);
        if (rec?.SshTunnel != null) bits.Add("via SSH tunnel");
        var credential = FormatCredentialSummary(snap);
        if (!string.IsNullOrEmpty(credential)) bits.Add(credential);
        if (!string.IsNullOrWhiteSpace(self?.ServerVersion)) bits.Add($"v{self!.ServerVersion}");
        if (self?.UptimeMs is long uptime && uptime > 0)
            bits.Add($"up {FormatUptime(uptime)}");
        return string.Join(" • ", bits);
    }

    internal static string FormatCredentialSource(string? source)
    {
        return source switch
        {
            CredentialResolver.SourceNodeDeviceToken => "paired via node device token",
            CredentialResolver.SourceDeviceToken => "paired via device token",
            CredentialResolver.SourceSharedGatewayToken => "shared token",
            CredentialResolver.SourceBootstrapToken => "bootstrap token",
            _ => "",
        };
    }

    internal static string FormatCredentialSummary(GatewayConnectionSnapshot snap)
    {
        var useOperator = !string.IsNullOrEmpty(snap.OperatorCredentialSource);
        var source = useOperator ? snap.OperatorCredentialSource : snap.NodeCredentialSource;
        var label = FormatCredentialSource(source);
        if (string.IsNullOrEmpty(label))
            return "";

        var status = useOperator ? snap.OperatorCredentialStatus : snap.NodeCredentialStatus;
        var fallbackUsed = useOperator ? snap.OperatorCredentialFallbackUsed : snap.NodeCredentialFallbackUsed;
        if (fallbackUsed || status == GatewayCredentialResolutionStatus.FallbackUsed)
            return $"{label} (fallback)";
        if (status == GatewayCredentialResolutionStatus.BootstrapRequired ||
            (useOperator ? snap.OperatorCredentialBootstrapRequired : snap.NodeCredentialBootstrapRequired))
            return $"{label} (pairing required)";

        return label;
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

    private static RecoveryCategory ClassifyError(string err)
    {
        // Delegate the heuristic matching to the pure, unit-tested Shared
        // classifier so the same kinds drive both setup and recovery copy.
        return OpenClaw.Shared.GatewayErrorClassifier.Classify(err) switch
        {
            OpenClaw.Shared.GatewayErrorKind.ScopeMismatch => RecoveryCategory.Scope,
            OpenClaw.Shared.GatewayErrorKind.TokenDrift => RecoveryCategory.TokenDrift,
            OpenClaw.Shared.GatewayErrorKind.Auth => RecoveryCategory.Auth,
            OpenClaw.Shared.GatewayErrorKind.Tls => RecoveryCategory.Tls,
            OpenClaw.Shared.GatewayErrorKind.Tunnel => RecoveryCategory.Tunnel,
            OpenClaw.Shared.GatewayErrorKind.Server => RecoveryCategory.Server,
            OpenClaw.Shared.GatewayErrorKind.RateLimited => RecoveryCategory.RateLimited,
            OpenClaw.Shared.GatewayErrorKind.PairingRejected => RecoveryCategory.Auth,
            // PairingRequired is normally driven by snapshot state, not the
            // error string; if it surfaces here, the Auth re-pair path is the
            // closest actionable fit. Network / Unknown → Network.
            OpenClaw.Shared.GatewayErrorKind.PairingRequired => RecoveryCategory.Auth,
            _ => RecoveryCategory.Network,
        };
    }

    private static bool IsTailscaleEndpoint(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host;
        if (host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase))
            return true;

        var parts = host.Split('.');
        return parts.Length == 4 &&
               int.TryParse(parts[0], out var first) && first == 100 &&
               int.TryParse(parts[1], out var second) && second is >= 64 and <= 127;
    }

    private static bool IsManagedTailscaleGateway(GatewayRecord? rec, string? endpoint) =>
        rec?.IsLocal == true &&
        !string.IsNullOrWhiteSpace(rec.SetupManagedDistroName) &&
        IsTailscaleEndpoint(endpoint);

    private static string FormatUptime(long uptimeMs)
    {
        var span = TimeSpan.FromMilliseconds(uptimeMs);
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m";
        return $"{(int)span.TotalSeconds}s";
    }

    /// <summary>Maps an accent enum to the ThemeResource brush key used in XAML.</summary>
    public static string AccentToBrushKey(ConnectionAccent accent) => accent switch
    {
        ConnectionAccent.Success   => "SystemFillColorSuccessBrush",
        ConnectionAccent.Caution   => "SystemFillColorCautionBrush",
        ConnectionAccent.Critical  => "SystemFillColorCriticalBrush",
        // Neutral default — using ControlStrokeColorDefaultBrush instead of
        // SystemFillColorNeutralBrush so the page accent matches the standard
        // card stroke colour at rest (per tokens.md "Neutral / off").
        _                          => "ControlStrokeColorDefaultBrush",
    };
}

/// <summary>User-driven mode override set by code-behind in response to user actions.</summary>
internal enum UserIntent
{
    None,
    AddingGateway,
}

/// <summary>
/// Shared sanitizers for free-form text or URLs sourced from the gateway/snapshot
/// before they're rendered. Strips control chars, collapses whitespace,
/// truncates, drops userinfo from URLs.
/// </summary>
internal static class ConnectionCardPlanSanitizer
{
    public static string Sanitize(string raw, int maxLen = 120)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsControl(c)) { sb.Append(' '); continue; }
            sb.Append(c);
        }
        var s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        return s.Length > maxLen ? s.Substring(0, maxLen - 1) + "…" : s;
    }

    public static string SanitizeGatewayUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        try
        {
            var uri = new Uri(raw);
            var safe = uri.GetComponents(
                UriComponents.Scheme | UriComponents.Host | UriComponents.Port | UriComponents.Path,
                UriFormat.UriEscaped);
            return Sanitize(string.IsNullOrEmpty(safe) ? raw : safe, maxLen: 80);
        }
        catch
        {
            return Sanitize(raw, maxLen: 80);
        }
    }
}
