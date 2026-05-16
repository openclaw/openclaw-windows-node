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
    CopyApproveCommand,
    RestartTunnel,
    Rep,
    BackToCockpit,
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
    OnHealthy,
    OnPermissionsIncomplete,
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
    public static ConnectionPagePlan Build(
        GatewayConnectionSnapshot snap,
        GatewayRecord? activeRecord,
        GatewaySelfInfo? self,
        SettingsManager? settings,
        int savedGatewayCount,
        UserIntent userIntent = UserIntent.None)
    {
        var hasActive = activeRecord != null;
        var displayName = activeRecord?.FriendlyName
            ?? activeRecord?.Url
            ?? snap.GatewayName
            ?? "gateway";

        // ─── User-intent override: AddGateway sub-view ───
        // Keeps Operator/Node cards visible in the strip+roles area while the
        // bottom section is swapped to the Add form.
        if (userIntent == UserIntent.AddingGateway)
        {
            // Inherit the snap-derived status strip so the user keeps context,
            // but force the bottom section to the Add form.
            var inner = BuildDerived(snap, activeRecord, self, settings, savedGatewayCount, displayName);
            return inner with { Mode = ConnectionPageMode.AddGateway };
        }

        return BuildDerived(snap, activeRecord, self, settings, savedGatewayCount, displayName);
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
            OverallConnectionState.Idle => BuildIdle(savedGatewayCount, activeRecord),

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

            _ => BuildIdle(savedGatewayCount, activeRecord),
        };
    }

    // ───────────────────────────────────────────────────────────────────
    // Mode builders
    // ───────────────────────────────────────────────────────────────────

    private static ConnectionPagePlan BuildIdle(int savedCount, GatewayRecord? activeRecord)
    {
        if (savedCount == 0)
        {
            return new ConnectionPagePlan
            {
                Mode = ConnectionPageMode.Welcome,
                StripGlyph = OpenClawTray.Helpers.FluentIconCatalog.System,
                StripAccent = ConnectionAccent.Neutral,
                StripHeadline = "No gateway yet",
                StripSub = "Add a gateway to get started.",
            };
        }

        // Saved gateways exist but none active — drop straight into Cockpit
        // (Operator/Node panels hide themselves because OperatorCardState=Hidden).
        return new ConnectionPagePlan
        {
            Mode = ConnectionPageMode.Cockpit,
            StripGlyph = OpenClawTray.Helpers.FluentIconCatalog.System,
            StripAccent = ConnectionAccent.Neutral,
            StripHeadline = "Not connected",
            StripSub = "Pick a gateway below, or add a new one.",
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
        var sub = BuildConnectedDetailLine(rec, self);

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
            ActiveGatewayDetailLine = BuildConnectedDetailLine(rec, self),
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
                StripPrimaryLabel = cmd != null ? "Copy command" : null,
                StripPrimaryAction = cmd != null ? ConnectionPrimaryAction.CopyApproveCommand : ConnectionPrimaryAction.None,
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
                StripPrimaryLabel = "Re-pair",
                StripPrimaryAction = ConnectionPrimaryAction.Rep,
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

    private static NodeCardState BuildNodeCardState(GatewayConnectionSnapshot snap, SettingsManager? settings)
    {
        if (settings == null) return NodeCardState.Hidden;
        if (!settings.EnableNodeMode) return NodeCardState.Off;

        // Operator must be connected for the node card to be meaningful.
        if (snap.OperatorState != RoleConnectionState.Connected)
            return NodeCardState.Off;

        return snap.NodeState switch
        {
            RoleConnectionState.PairingRequired => NodeCardState.OnNodePairingRequired,
            RoleConnectionState.PairingRejected => NodeCardState.OnNodeRejected,
            RoleConnectionState.RateLimited => NodeCardState.OnNodeRateLimited,
            RoleConnectionState.Error => NodeCardState.OnNodeError,
            _ when CountEnabledCapabilities(settings) == 0 => NodeCardState.OnPermissionsIncomplete,
            _ => NodeCardState.OnHealthy,
        };
    }

    private static string? BuildNodeApproveCommand(GatewayConnectionSnapshot snap)
    {
        if (snap.NodeState != RoleConnectionState.PairingRequired) return null;
        var reqId = !string.IsNullOrEmpty(snap.NodePairingRequestId)
            ? ConnectionCardPlanSanitizer.Sanitize(snap.NodePairingRequestId!, maxLen: 64)
            : null;
        return reqId != null
            ? $"openclaw approve node {reqId}"
            : "openclaw approve node";
    }

    private static string? BuildDevicePairingApproveCommand(GatewayConnectionSnapshot snap)
    {
        if (!snap.OperatorPairingRequired && snap.OperatorState != RoleConnectionState.PairingRequired)
            return null;
        var reqId = !string.IsNullOrEmpty(snap.OperatorPairingRequestId)
            ? ConnectionCardPlanSanitizer.Sanitize(snap.OperatorPairingRequestId!, maxLen: 64)
            : null;
        return reqId != null
            ? $"openclaw approve device {reqId}"
            : "openclaw approve device";
    }

    private static string? ExtractNodeErrorDetail(GatewayConnectionSnapshot snap)
    {
        if (string.IsNullOrWhiteSpace(snap.NodeError)) return null;
        return ConnectionCardPlanSanitizer.Sanitize(snap.NodeError!);
    }

    private static string BuildConnectedDetailLine(GatewayRecord? rec, GatewaySelfInfo? self)
    {
        var bits = new List<string>(4);
        var url = ConnectionCardPlanSanitizer.SanitizeGatewayUrl(rec?.Url);
        if (!string.IsNullOrEmpty(url)) bits.Add(url);
        if (rec?.SshTunnel != null) bits.Add("via SSH tunnel");
        if (!string.IsNullOrWhiteSpace(self?.ServerVersion)) bits.Add($"v{self!.ServerVersion}");
        if (self?.UptimeMs is long uptime && uptime > 0)
            bits.Add($"up {FormatUptime(uptime)}");
        return string.Join(" • ", bits);
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
        if (string.IsNullOrEmpty(err)) return RecoveryCategory.Network;
        var e = err.ToLowerInvariant();

        if (e.Contains("auth") || e.Contains("token") || e.Contains("unauthor") || e.Contains("forbid"))
            return RecoveryCategory.Auth;

        if (e.Contains("ssh") || e.Contains("tunnel"))
            return RecoveryCategory.Tunnel;

        if (e.Contains("500") || e.Contains("502") || e.Contains("503") ||
            e.Contains("internal") || e.Contains("server"))
            return RecoveryCategory.Server;

        return RecoveryCategory.Network;
    }

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
