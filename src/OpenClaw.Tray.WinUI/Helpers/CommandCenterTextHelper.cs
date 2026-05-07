using OpenClaw.Shared;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenClawTray.Helpers;

internal static class CommandCenterTextHelper
{
    internal static string BuildSupportContext(GatewayCommandCenterState state)
    {
        var builder = new StringBuilder();
        builder.AppendLine("OpenClaw Windows Tray Support Context");
        builder.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        builder.AppendLine($"Connection: {state.ConnectionStatus}");
        builder.AppendLine($"Topology: {state.Topology.DisplayName}");
        builder.AppendLine($"Transport: {state.Topology.Transport}");
        builder.AppendLine($"Gateway URL: {RedactSupportValue(state.Topology.GatewayUrl)}");
        builder.AppendLine($"Topology detail: {RedactSupportValue(state.Topology.Detail)}");
        builder.AppendLine($"Gateway runtime: {RedactSupportValue(state.Runtime.DisplayText)}");
        builder.AppendLine($"Update status: {RedactSupportValue(state.Update.DisplayText)}");
        if (state.Tunnel != null && state.Tunnel.Status != TunnelStatus.NotConfigured)
        {
            builder.AppendLine($"Tunnel: {state.Tunnel.Status}");
            builder.AppendLine($"Tunnel local endpoint: {RedactSupportValue(state.Tunnel.LocalEndpoint)}");
            builder.AppendLine($"Tunnel remote endpoint: {RedactSupportValue(state.Tunnel.RemoteEndpoint)}");
            if (!string.IsNullOrWhiteSpace(state.Tunnel.BrowserProxyLocalEndpoint) ||
                !string.IsNullOrWhiteSpace(state.Tunnel.BrowserProxyRemoteEndpoint))
            {
                builder.AppendLine($"Tunnel browser proxy local endpoint: {RedactSupportValue(state.Tunnel.BrowserProxyLocalEndpoint)}");
                builder.AppendLine($"Tunnel browser proxy remote endpoint: {RedactSupportValue(state.Tunnel.BrowserProxyRemoteEndpoint)}");
            }
            if (!string.IsNullOrWhiteSpace(state.Tunnel.LastError))
                builder.AppendLine($"Tunnel last error: {RedactSupportValue(state.Tunnel.LastError)}");
        }

        builder.AppendLine($"Gateway version: {state.GatewaySelf?.ServerVersion ?? "unknown"}");
        builder.AppendLine($"Gateway uptime ms: {state.GatewaySelf?.UptimeMs?.ToString() ?? "unknown"}");
        builder.AppendLine($"Channels: {state.Channels.Count}");
        builder.AppendLine($"Sessions: {state.Sessions.Count}");
        builder.AppendLine($"Nodes: {state.Nodes.Count}");
        builder.AppendLine($"Warnings: {state.Warnings.Count}");
        foreach (var warning in state.Warnings.Take(10))
        {
            builder.AppendLine($"- {warning.Severity}: {warning.Title}");
        }
        builder.AppendLine($"Recent activity: {state.RecentActivity.Count}");
        foreach (var item in state.RecentActivity.Take(10))
        {
            builder.AppendLine($"- {item.Timestamp:O} [{item.Category}] {item.Title}");
        }
        builder.AppendLine($"Ports: {state.PortDiagnostics.Count}");
        foreach (var port in state.PortDiagnostics)
        {
            builder.AppendLine($"- {port.Purpose}: {port.Port} {port.StatusText} ({RedactSupportValue(port.Detail)})");
        }
        builder.AppendLine($"Log file: {RedactSupportPath(Logger.LogFilePath)}");
        builder.AppendLine($"Diagnostics JSONL: {RedactSupportPath(DiagnosticsJsonlService.FilePath)}");
        builder.AppendLine($"Settings folder: {RedactSupportPath(SettingsManager.SettingsDirectoryPath)}");
        builder.AppendLine("Excluded: tokens, bootstrap tokens, command arguments, screenshots, recordings, camera data, microphone data, base64 payloads, and message payloads.");
        return builder.ToString();
    }

    internal static string BuildDebugBundle(GatewayCommandCenterState state)
    {
        var builder = new StringBuilder();
        builder.AppendLine("OpenClaw Windows Tray Debug Bundle");
        builder.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        builder.AppendLine();
        AppendSection(builder, "Support Context", BuildSupportContext(state));
        AppendSection(builder, "Port Diagnostics", BuildPortDiagnosticsSummary(state.PortDiagnostics));
        AppendSection(builder, "Capability Diagnostics", BuildCapabilityDiagnosticsSummary(state));
        AppendSection(builder, "Node Inventory", BuildNodeInventorySummary(state.Nodes));
        AppendSection(builder, "Channel Summary", BuildChannelSummaryText(state.Channels));
        AppendSection(builder, "Activity Summary", BuildActivitySummary(state.RecentActivity));
        AppendSection(builder, "Extensibility Summary", BuildExtensibilitySummary(state.Channels));
        return builder.ToString();
    }

    internal static string BuildBrowserSetupGuidance(GatewayCommandCenterState state)
    {
        var browserProxyPort = state.PortDiagnostics
            .FirstOrDefault(p => p.Purpose.Equals("Browser proxy host", StringComparison.OrdinalIgnoreCase))
            ?.Port ?? 0;

        return BuildBrowserSetupGuidance(browserProxyPort, state.Topology, state.Tunnel);
    }

    internal static string BuildBrowserSetupGuidance(
        int browserProxyPort,
        GatewayTopologyInfo? topology,
        TunnelCommandCenterInfo? tunnel)
    {
        var portText = browserProxyPort is >= 1 and <= 65535
            ? browserProxyPort.ToString(CultureInfo.InvariantCulture)
            : "<gateway-port+2>";
        var gatewayHost = string.IsNullOrWhiteSpace(topology?.Host) ? "<gateway-host>" : topology.Host;
        var gatewayPort = ResolveGatewayPort(topology?.GatewayUrl);
        var gatewayPortText = gatewayPort is >= 1 and <= 65535
            ? gatewayPort.Value.ToString(CultureInfo.InvariantCulture)
            : "<gateway-port>";

        var lines = new List<string>
        {
            "OpenClaw browser proxy setup",
            $"Expected local browser-control endpoint: http://127.0.0.1:{portText}/",
            "",
            "If the Gateway and browser are on this Windows machine:",
            "1. Ensure the upstream browser plugin is enabled in the Gateway config.",
            "2. Verify the browser control plane:",
            "   openclaw browser --browser-profile openclaw doctor",
            "   openclaw browser --browser-profile openclaw start",
            "   openclaw browser --browser-profile openclaw tabs",
            "",
            "If the browser is on this Windows machine but the Gateway is remote:",
            "1. Run a browser-capable OpenClaw node host on this machine:",
            $"   openclaw node run --host {gatewayHost} --port {gatewayPortText}",
            "2. Or install it as a user service:",
            $"   openclaw node install --host {gatewayHost} --port {gatewayPortText}",
            "   openclaw node start",
            "3. Keep nodeHost.browserProxy.enabled=true, and configure nodeHost.browserProxy.allowProfiles only if you want to restrict profile access.",
            "",
            "Gateway policy and auth checks:",
            "- The Gateway allowlist must permit browser.proxy for this node.",
            "- Browser-control auth must match the saved Gateway token/password in Settings.",
            "- Do not paste QR bootstrap tokens into the normal Gateway Token field."
        };

        if (topology?.UsesSshTunnel == true)
        {
            lines.Add("");
            lines.Add("SSH tunnel mode:");
            lines.Add("- Prefer the tray-managed SSH tunnel with Browser proxy bridge enabled; it forwards local-port+2 to remote-port+2 automatically.");
            lines.Add($"- Manual forward shape: {BuildBrowserProxySshForwardHint(browserProxyPort, tunnel)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal static string BuildChannelSummaryText(IReadOnlyCollection<ChannelCommandCenterInfo> channels)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Channels: {BuildChannelSummary(channels)}");
        foreach (var channel in channels.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {channel.Name}: {channel.Status ?? "unknown"} ({BuildChannelDetail(channel)})");
        }

        return builder.ToString();
    }

    internal static string BuildExtensibilitySummary(IReadOnlyCollection<ChannelCommandCenterInfo> channels)
    {
        var builder = new StringBuilder();
        builder.AppendLine("OpenClaw extensibility surfaces");
        builder.AppendLine("Channels dashboard: channels");
        builder.AppendLine("Skills dashboard: skills");
        builder.AppendLine("Cron / schedules dashboard: cron");
        builder.AppendLine();
        builder.AppendLine("Channel health currently reported to Windows:");
        foreach (var channel in channels.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {channel.Name}: {channel.Status} ({BuildChannelDetail(channel)})");
        }

        return builder.ToString();
    }

    internal static string BuildCapabilityDiagnosticsSummary(GatewayCommandCenterState state)
    {
        var builder = new StringBuilder();
        builder.AppendLine("OpenClaw capability diagnostics");
        builder.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        builder.AppendLine();
        builder.AppendLine("Windows permission surfaces:");
        foreach (var permission in state.Permissions.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {permission.Name}: {permission.Status} - {permission.Detail}");
        }

        builder.AppendLine();
        builder.AppendLine("Node command allowlist status:");
        if (state.Nodes.Count == 0)
        {
            builder.AppendLine("- No nodes reported by gateway.");
        }

        foreach (var node in state.Nodes.OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var displayName = string.IsNullOrWhiteSpace(node.DisplayName) ? node.NodeId : node.DisplayName;
            builder.AppendLine($"- {displayName} ({node.Platform ?? "unknown"}, {(node.IsOnline ? "online" : "offline")})");
            builder.AppendLine($"  declared commands: {FormatCommandList(node.Commands)}");
            builder.AppendLine($"  safe companion commands: {FormatCommandList(node.SafeDeclaredCommands)}");
            builder.AppendLine($"  privacy-sensitive opt-ins: {FormatCommandList(node.DangerousDeclaredCommands)}");
            builder.AppendLine($"  browser proxy commands: {FormatCommandList(node.BrowserDeclaredCommands)}");
            builder.AppendLine($"  Windows-specific commands: {FormatCommandList(node.WindowsSpecificDeclaredCommands)}");
            builder.AppendLine($"  filtered by gateway policy: {FormatCommandList(node.BlockedDeclaredCommands)}");
            builder.AppendLine($"  disabled in Settings: {FormatCommandList(node.DisabledBySettingsCommands)}");
            builder.AppendLine($"  missing safe allowlist: {FormatCommandList(node.MissingSafeAllowlistCommands)}");
            builder.AppendLine($"  missing privacy-sensitive allowlist: {FormatCommandList(node.MissingDangerousAllowlistCommands)}");
            builder.AppendLine($"  missing browser proxy allowlist: {FormatCommandList(node.MissingBrowserAllowlistCommands)}");
            builder.AppendLine($"  missing Mac parity: {FormatCommandList(node.MissingMacParityCommands)}");
        }

        builder.AppendLine();
        builder.AppendLine("Rule: safe companion commands can be allowlisted for parity; privacy-sensitive commands such as camera.snap, camera.clip, and screen.record should stay explicit opt-ins.");
        return builder.ToString();
    }

    internal static string BuildPortDiagnosticsSummary(IReadOnlyCollection<PortDiagnosticInfo> ports)
    {
        if (ports.Count == 0)
            return "No local port diagnostics available for the current topology.";

        var builder = new StringBuilder();
        builder.AppendLine("OpenClaw port diagnostics");
        builder.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        foreach (var port in ports.OrderBy(p => p.Port).ThenBy(p => p.Purpose, StringComparer.OrdinalIgnoreCase))
        {
            var owner = port.OwningProcessId is > 0
                ? $" · owner {port.OwningProcessName ?? "unknown"} (PID {port.OwningProcessId})"
                : "";
            builder.AppendLine($"- {port.Purpose}: {port.Port} {port.StatusText}{owner} - {RedactSupportValue(port.Detail)}");
            if (port.OwningProcessId is > 0)
            {
                builder.AppendLine($"  stop hint: Stop-Process -Id {port.OwningProcessId.Value}");
            }
        }

        return builder.ToString();
    }

    internal static string BuildActivitySummary(IReadOnlyCollection<CommandCenterActivityInfo> activity)
    {
        if (activity.Count == 0)
            return "No recent OpenClaw tray activity.";

        var builder = new StringBuilder();
        builder.AppendLine("Recent OpenClaw tray activity");
        foreach (var item in activity)
        {
            var details = BuildActivityDetail(item);
            builder.AppendLine($"{item.Timestamp:O} [{item.Category}] {item.Title} - {details}");
        }

        return builder.ToString();
    }

    internal static string BuildNodeInventorySummary(IReadOnlyCollection<NodeCapabilityHealthInfo> nodes)
    {
        if (nodes.Count == 0)
            return "No nodes reported by gateway.";

        var builder = new StringBuilder();
        builder.AppendLine("OpenClaw node inventory");
        builder.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        builder.AppendLine();
        foreach (var node in nodes.OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(BuildNodeSummary(node).TrimEnd());
            builder.AppendLine($"Safe companion commands: {FormatCommandList(node.SafeDeclaredCommands)}");
            builder.AppendLine($"Privacy-sensitive commands: {FormatCommandList(node.DangerousDeclaredCommands)}");
            builder.AppendLine($"Browser proxy commands: {FormatCommandList(node.BrowserDeclaredCommands)}");
            builder.AppendLine($"Windows-specific commands: {FormatCommandList(node.WindowsSpecificDeclaredCommands)}");
            builder.AppendLine($"Filtered by gateway policy: {FormatCommandList(node.BlockedDeclaredCommands)}");
            builder.AppendLine($"Missing browser proxy allowlist: {FormatCommandList(node.MissingBrowserAllowlistCommands)}");
            builder.AppendLine($"Disabled in Settings: {FormatCommandList(node.DisabledBySettingsCommands)}");
            builder.AppendLine($"Missing Mac parity: {FormatCommandList(node.MissingMacParityCommands)}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static void AppendSection(StringBuilder builder, string title, string content)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine(content.TrimEnd());
        builder.AppendLine();
    }

    private static string BuildBrowserProxySshForwardHint(int browserProxyPort, TunnelCommandCenterInfo? tunnel)
    {
        if (browserProxyPort is < 1 or > 65535)
            return "ssh -N -L <local-browser-port>:127.0.0.1:<remote-browser-port> <user>@<host>";

        var target = string.IsNullOrWhiteSpace(tunnel?.User) || string.IsNullOrWhiteSpace(tunnel.Host)
            ? "<user>@<host>"
            : $"{tunnel.User}@{tunnel.Host}";
        var remoteBrowserPort = TryParseEndpointPort(tunnel?.BrowserProxyRemoteEndpoint) ?? browserProxyPort;
        return $"ssh -N -L {browserProxyPort}:127.0.0.1:{remoteBrowserPort} {target}";
    }

    private static int? TryParseEndpointPort(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        if (Uri.TryCreate($"tcp://{endpoint}", UriKind.Absolute, out var uri) &&
            uri.Port is >= 1 and <= 65535)
        {
            return uri.Port;
        }

        var portDelimiter = endpoint.LastIndexOf(':');
        return portDelimiter >= 0 &&
               int.TryParse(endpoint[(portDelimiter + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
               port is >= 1 and <= 65535
            ? port
            : null;
    }

    private static int? ResolveGatewayPort(string? gatewayUrl)
    {
        return Uri.TryCreate(gatewayUrl, UriKind.Absolute, out var uri) && uri.Port is >= 1 and <= 65535
            ? uri.Port
            : null;
    }

    private static string RedactSupportPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "not configured";

        var redacted = path;
        var knownFolders = new Dictionary<string, string>
        {
            [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)] = "%USERPROFILE%",
            [Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)] = "%APPDATA%",
            [Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)] = "%LOCALAPPDATA%",
            [Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)] = "%USERPROFILE%\\Documents"
        };

        foreach (var (folder, replacement) in knownFolders
                     .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                     .OrderByDescending(pair => pair.Key.Length))
        {
            if (redacted.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                redacted = replacement + redacted[folder.Length..];
                break;
            }
        }

        redacted = Regex.Replace(
            redacted,
            @"\b[A-Za-z]:\\Users\\[^\\]+",
            "%USERPROFILE%",
            RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));

        redacted = Regex.Replace(
            redacted,
            @"/Users/[^/]+",
            "$HOME",
            RegexOptions.None,
            TimeSpan.FromMilliseconds(100));

        return redacted;
    }

    private static string RedactSupportValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var redacted = Regex.Replace(
            value,
            @"\b[a-z][a-z0-9+.-]*://(?:[^@\s/]+@)?([^:/\s]+)",
            match => match.Value.Replace(match.Groups[1].Value, "<host>"),
            RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));

        redacted = Regex.Replace(
            redacted,
            @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
            "<ip>",
            RegexOptions.None,
            TimeSpan.FromMilliseconds(100));

        redacted = Regex.Replace(
            redacted,
            @"\b[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}\b",
            "<email>",
            RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));

        redacted = Regex.Replace(
            redacted,
            @"\b(?<user>[A-Za-z0-9._-]+)@(?<host>[A-Za-z0-9._-]+)(?=[:\s]|$)",
            "<user>@<host>",
            RegexOptions.None,
            TimeSpan.FromMilliseconds(100));

        redacted = Regex.Replace(
            redacted,
            @"(?<=\bto\s)[A-Za-z0-9._-]+(?=:\d{1,5}\b)",
            "<host>",
            RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));

        redacted = Regex.Replace(
            redacted,
            @"^\s*[A-Za-z0-9._-]+(?=:\d{1,5}\b)",
            "<host>",
            RegexOptions.None,
            TimeSpan.FromMilliseconds(100));

        return redacted;
    }

    private static string BuildChannelDetail(ChannelCommandCenterInfo channel)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(channel.Type))
            parts.Add(channel.Type!);
        if (channel.IsLinked)
            parts.Add(string.IsNullOrWhiteSpace(channel.AuthAge) ? "linked" : $"linked · {channel.AuthAge}");
        if (!string.IsNullOrWhiteSpace(channel.Error))
            parts.Add(channel.Error!);
        if (channel.CanStart)
            parts.Add("start available");
        if (channel.CanStop)
            parts.Add("stop available");
        return parts.Count == 0 ? "no details" : string.Join(" · ", parts);
    }

    private static string BuildChannelSummary(IReadOnlyCollection<ChannelCommandCenterInfo> channels)
    {
        if (channels.Count == 0)
            return "No channels reported by gateway health.";

        var running = channels.Count(c => c.CanStop);
        var startable = channels.Count(c => c.CanStart);
        var errors = channels.Count(c => string.Equals(c.Status, "error", StringComparison.OrdinalIgnoreCase));
        return $"{running}/{channels.Count} running · {startable} startable · {errors} error";
    }

    private static string FormatCommandList(IEnumerable<string> commands)
    {
        var ordered = commands
            .Where(command => !string.IsNullOrWhiteSpace(command))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ordered.Count == 0 ? "none" : string.Join(", ", ordered);
    }

    private static string BuildActivityDetail(CommandCenterActivityInfo activity)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(activity.Details))
            details.Add(activity.Details);
        if (!string.IsNullOrWhiteSpace(activity.SessionKey))
            details.Add($"session: {activity.SessionKey}");
        if (!string.IsNullOrWhiteSpace(activity.NodeId))
            details.Add($"node: {ShortId(activity.NodeId)}");
        if (!string.IsNullOrWhiteSpace(activity.DashboardPath))
            details.Add($"dashboard: {activity.DashboardPath}");

        return details.Count == 0 ? activity.Category : string.Join(" · ", details);
    }

    private static string ShortId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return value.Length <= 12 ? value : value[..12] + "...";
    }

    private static string BuildNodeSummary(NodeCapabilityHealthInfo node)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.IsNullOrWhiteSpace(node.DisplayName) ? node.NodeId : node.DisplayName);
        builder.AppendLine($"Node ID: {node.NodeId}");
        builder.AppendLine($"Platform: {node.Platform ?? "unknown"}");
        builder.AppendLine($"Status: {(node.IsOnline ? "online" : "offline")}");
        builder.AppendLine($"Capabilities: {string.Join(", ", node.Capabilities.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))}");
        builder.AppendLine($"Commands: {string.Join(", ", node.Commands.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))}");
        if (node.DisabledBySettingsCommands.Count > 0)
            builder.AppendLine($"Disabled in Settings: {string.Join(", ", node.DisabledBySettingsCommands)}");
        if (node.Warnings.Count > 0)
        {
            builder.AppendLine("Warnings:");
            foreach (var warning in node.Warnings)
            {
                builder.AppendLine($"- {warning.Title}: {warning.Detail}");
            }
        }

        return builder.ToString();
    }
}
