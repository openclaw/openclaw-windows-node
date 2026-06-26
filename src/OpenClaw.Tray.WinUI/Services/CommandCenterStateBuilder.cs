using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Services;

internal sealed class CommandCenterStateBuilder
{
    private readonly AppStateSnapshot _snapshot;

    internal CommandCenterStateBuilder(AppStateSnapshot snapshot)
    {
        _snapshot = snapshot;
    }

    internal GatewayCommandCenterState Build()
    {
        var nodes = _snapshot.Nodes.Select(NodeCapabilityHealthInfo.FromNode).ToList();
        if (nodes.Count == 0 && _snapshot.NodeService?.GetLocalNodeInfo() is { } localNode)
        {
            nodes.Add(NodeCapabilityHealthInfo.FromLocalDeclarations(localNode));
        }

        var tunnelInputs = CommandCenterTopologyTunnelResolver.Derive(
            _snapshot.HasActiveGatewayRecord,
            _snapshot.ActiveGatewaySshTunnel,
            _snapshot.Settings?.UseSshTunnel == true,
            _snapshot.Settings?.SshTunnelHost,
            _snapshot.Settings?.SshTunnelLocalPort ?? 0,
            _snapshot.Settings?.SshTunnelRemotePort ?? 0);
        var topology = GatewayTopologyClassifier.Classify(
            _snapshot.EffectiveGatewayUrl,
            tunnelInputs.UsesSshTunnel,
            tunnelInputs.SshHost,
            tunnelInputs.LocalPort,
            tunnelInputs.RemotePort);
        var tunnel = BuildTunnelInfo(tunnelInputs);
        var browserProxyTunnelState = BrowserProxyTunnelState.Resolve(
            activeResolverSupplied: _snapshot.HasActiveGatewayRecord,
            activeTunnel: _snapshot.ActiveGatewaySshTunnel,
            activeGatewayUrl: _snapshot.EffectiveGatewayUrl,
            settingsUseSshTunnel: _snapshot.Settings?.UseSshTunnel == true,
            settingsLocalPort: _snapshot.Settings?.SshTunnelLocalPort,
            settingsRemotePort: _snapshot.Settings?.SshTunnelRemotePort,
            settingsGatewayUrl: _snapshot.Settings?.GatewayUrl);
        var portDiagnostics = PortDiagnosticsService.BuildDiagnostics(
            topology,
            tunnel,
            _snapshot.EffectiveBrowserControlPort,
            useSshTunnelForBrowserProxy: browserProxyTunnelState.Enabled,
            allowGatewayPortFallback: browserProxyTunnelState.AllowGatewayPortFallback);
        ApplyDetectedSshForwardTopology(topology, portDiagnostics);
        var runtime = BuildGatewayRuntimeInfo(portDiagnostics);
        var warnings = nodes.SelectMany(n => n.Warnings).ToList();
        var localNodeId = _snapshot.NodeService?.FullDeviceId;
        var hasAuthoritativePendingLocalNodeTrust =
            !string.IsNullOrWhiteSpace(localNodeId) &&
            nodes.Any(node =>
                string.Equals(node.NodeId, localNodeId, StringComparison.OrdinalIgnoreCase) &&
                node.ApprovalState is GatewayNodeApprovalState.PendingApproval or
                    GatewayNodeApprovalState.PendingReapproval);
        var shouldShowPendingLocalNodeApproval =
            _snapshot.NodePairingApprovalKind == PairingApprovalKind.DevicePair ||
            !hasAuthoritativePendingLocalNodeTrust;
        warnings.AddRange(CommandCenterDiagnostics.BuildTopologyWarnings(topology, tunnel));
        warnings.AddRange(BuildPortDiagnosticWarnings(portDiagnostics, topology, tunnel, _snapshot.EffectiveBrowserControlPort));
        warnings.AddRange(BuildBrowserProxyAuthWarnings(nodes));

        if (!string.IsNullOrWhiteSpace(_snapshot.AuthFailureMessage))
        {
            warnings.Insert(0, new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Critical,
                Category = "auth",
                Title = LocalizationHelper.GetString("CommandCenter_AuthFailed"),
                Detail = _snapshot.AuthFailureMessage
            });
        }

        if (shouldShowPendingLocalNodeApproval &&
            _snapshot.NodeService?.IsPendingApproval == true &&
            !string.IsNullOrWhiteSpace(_snapshot.NodeService.FullDeviceId))
        {
            var approvalCommand = _snapshot.NodePairingApprovalKind switch
            {
                PairingApprovalKind.DevicePair => CommandCenterDiagnostics.BuildDeviceApprovalRepairCommand(
                    _snapshot.NodePairingRequestId),
                PairingApprovalKind.NodePair => CommandCenterDiagnostics.BuildNodeApprovalRepairCommand(_snapshot.NodePairingRequestId),
                _ => CommandCenterDiagnostics.BuildUnknownPairingDiscoveryCommands()
            };
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Warning,
                Category = "pairing",
                Title = LocalizationHelper.GetString("CommandCenter_NodePendingApproval"),
                Detail = $"Resolve the pending node approval for {_snapshot.NodeService.ShortDeviceId} from the gateway CLI, then re-open the command center after reconnect.",
                RepairAction = "Copy approval command",
                CopyText = approvalCommand
            });
        }

        if (_snapshot.Status == ConnectionStatus.Error)
        {
            warnings.Insert(0, new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Critical,
                Category = "gateway",
                Title = LocalizationHelper.GetString("CommandCenter_GatewayConnectionError"),
                Detail = "The tray is not currently connected to the gateway."
            });
        }
        else if (_snapshot.Status != ConnectionStatus.Connected)
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Warning,
                Category = "gateway",
                Title = LocalizationHelper.GetString("CommandCenter_GatewayNotConnected"),
                Detail = $"Current connection state is {_snapshot.Status}."
            });
        }

        if (_snapshot.Status == ConnectionStatus.Connected &&
            DateTime.Now - _snapshot.LastCheckTime > TimeSpan.FromMinutes(2))
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Warning,
                Category = "gateway",
                Title = LocalizationHelper.GetString("CommandCenter_GatewayHealthStale"),
                Detail = $"Last health check was {_snapshot.LastCheckTime:t}. Run a health check or verify the localhost tunnel."
            });
        }

        if (_snapshot.Channels.Length == 0 && _snapshot.Status == ConnectionStatus.Connected && _snapshot.HasGatewayClient)
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Info,
                Category = "channel",
                Title = LocalizationHelper.GetString("CommandCenter_NoChannelsReported"),
                Detail = "The gateway health payload did not report any channels."
            });
        }
        else if (_snapshot.Channels.Length == 0 && _snapshot.Status == ConnectionStatus.Connected && _snapshot.Settings?.EnableNodeMode == true)
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Info,
                Category = "gateway",
                Title = LocalizationHelper.GetString("CommandCenter_WaitingForGatewayHealth"),
                Detail = "Node mode is connected. Channel/session inventories are filled from gateway health events when available."
            });
        }
        else if (_snapshot.Channels.Length > 0 && _snapshot.Channels.All(c => !ChannelHealth.IsHealthyStatus(c.Status)))
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Warning,
                Category = "channel",
                Title = LocalizationHelper.GetString("CommandCenter_NoChannelsRunning"),
                Detail = "Channels are configured but none are reporting a running/ready state."
            });
        }

        if (_snapshot.Status == ConnectionStatus.Connected && nodes.Count == 0 && _snapshot.HasGatewayClient)
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Info,
                Category = "node",
                Title = LocalizationHelper.GetString("CommandCenter_NoNodesReported"),
                Detail = "node.list did not report any connected nodes. Pair a Windows node or verify the operator token has node inventory access."
            });
        }

        if (_snapshot.UsageCost?.Totals.MissingCostEntries > 0)
        {
            warnings.Add(new GatewayDiagnosticWarning
            {
                Severity = GatewayDiagnosticSeverity.Info,
                Category = "usage",
                Title = LocalizationHelper.GetString("CommandCenter_UsageCostsMissing"),
                Detail = $"{_snapshot.UsageCost.Totals.MissingCostEntries} usage entr{(_snapshot.UsageCost.Totals.MissingCostEntries == 1 ? "y is" : "ies are")} missing cost data."
            });
        }

        return new GatewayCommandCenterState
        {
            ConnectionStatus = _snapshot.Status,
            LastRefresh = _snapshot.LastCheckTime.ToUniversalTime(),
            Topology = topology,
            Runtime = runtime,
            Update = _snapshot.LastUpdateInfo,
            Tunnel = tunnel,
            GatewaySelf = _snapshot.GatewaySelf,
            PortDiagnostics = portDiagnostics,
            Permissions = PermissionDiagnostics.BuildDefaultWindowsMatrix(),
            Channels = _snapshot.Channels.Select(ChannelCommandCenterInfo.FromHealth).ToList(),
            Sessions = _snapshot.Sessions.ToList(),
            Usage = _snapshot.Usage,
            UsageStatus = _snapshot.UsageStatus,
            UsageCost = _snapshot.UsageCost,
            Nodes = nodes,
            Warnings = CommandCenterDiagnostics.SortAndDedupeWarnings(warnings),
            RecentActivity = ActivityStreamService.GetItems(12)
                .Select(item => new CommandCenterActivityInfo
                {
                    Timestamp = item.Timestamp,
                    Category = item.Category,
                    Title = item.Title,
                    Details = item.Details,
                    DashboardPath = item.DashboardPath,
                    SessionKey = item.SessionKey,
                    NodeId = item.NodeId
                })
                .ToList()
        };
    }

    private IEnumerable<GatewayDiagnosticWarning> BuildBrowserProxyAuthWarnings(IReadOnlyList<NodeCapabilityHealthInfo> nodes)
    {
        if (_snapshot.Settings?.NodeBrowserProxyEnabled == false ||
            !nodes.Any(node =>
                node.BrowserApprovedCommands.Contains("browser.proxy", StringComparer.OrdinalIgnoreCase) ||
                node.UnverifiedDeclaredCommands.Contains("browser.proxy", StringComparer.OrdinalIgnoreCase) ||
                node.LocalDeclaredCommands.Contains("browser.proxy", StringComparer.OrdinalIgnoreCase)))
        {
            yield break;
        }

        yield return new GatewayDiagnosticWarning
        {
            Severity = GatewayDiagnosticSeverity.Info,
            Category = "browser",
            Title = LocalizationHelper.GetString("CommandCenter_BrowserProxyAuthMayNeed"),
            Detail = "This Windows node reports or declares browser.proxy without a saved gateway shared token. QR/bootstrap pairing can connect the node, but an authenticated browser-control host may still require the same gateway token in Settings.",
            RepairAction = "Copy browser proxy auth guidance",
            CopyText = "If browser.proxy returns an auth error, enter the gateway shared token in Settings > Gateway Token, or configure the browser-control host to use auth compatible with the Windows node. Do not paste QR bootstrap tokens into the normal gateway token field."
        };
    }

    private static IEnumerable<GatewayDiagnosticWarning> BuildPortDiagnosticWarnings(
        IReadOnlyList<PortDiagnosticInfo> ports,
        GatewayTopologyInfo topology,
        TunnelCommandCenterInfo? tunnel,
        int? browserControlPort)
    {
        foreach (var port in ports)
        {
            if (tunnel?.Status == TunnelStatus.Up &&
                port.Purpose.Equals("SSH tunnel local forward", StringComparison.OrdinalIgnoreCase) &&
                !port.IsListening)
            {
                yield return new GatewayDiagnosticWarning
                {
                    Severity = GatewayDiagnosticSeverity.Warning,
                    Category = "port",
                    Title = LocalizationHelper.GetString("CommandCenter_SshTunnelPortNotListening"),
                    Detail = port.Detail
                };
            }

            if (topology.DetectedKind == GatewayKind.WindowsNative &&
                port.Purpose.Equals("Gateway endpoint", StringComparison.OrdinalIgnoreCase) &&
                !port.IsListening)
            {
                yield return new GatewayDiagnosticWarning
                {
                    Severity = GatewayDiagnosticSeverity.Info,
                    Category = "port",
                    Title = LocalizationHelper.GetString("CommandCenter_NoLocalGatewayListener"),
                    Detail = port.Detail
                };
            }

            if (port.Purpose.Equals("Browser proxy host", StringComparison.OrdinalIgnoreCase) &&
                !port.IsListening)
            {
                if (topology.UsesSshTunnel)
                {
                    yield return new GatewayDiagnosticWarning
                    {
                        Severity = GatewayDiagnosticSeverity.Info,
                        Category = "browser",
                        Title = LocalizationHelper.GetString("CommandCenter_BrowserProxySshForwardNotListening"),
                        Detail = $"browser.proxy over SSH needs a companion local forward for port {port.Port}. Add the browser-control forward to the same tunnel, or enable the managed SSH tunnel so Windows starts both forwards.",
                        RepairAction = "Copy browser proxy SSH forward",
                        CopyText = BuildBrowserProxySshForwardHint(port.Port, tunnel, browserControlPort)
                    };
                    continue;
                }

                yield return new GatewayDiagnosticWarning
                {
                    Severity = GatewayDiagnosticSeverity.Info,
                    Category = "browser",
                    Title = LocalizationHelper.GetString("CommandCenter_BrowserProxyHostNotDetected"),
                    Detail = "browser.proxy needs a compatible browser-control host listening on the gateway port + 2.",
                    RepairAction = "Copy browser setup guidance",
                    // string formatter — no UI
                    CopyText = CommandCenterTextHelper.BuildBrowserSetupGuidance(port.Port, topology, tunnel, browserControlPort)
                };
            }
        }
    }

    private static string BuildBrowserProxySshForwardHint(int browserProxyPort, TunnelCommandCenterInfo? tunnel, int? browserControlPort)
    {
        if (browserProxyPort is < 1 or > 65535)
            return "ssh -N -L <local-browser-port>:127.0.0.1:<remote-browser-port> <user>@<host>";

        var localBrowserPort = ResolveLocalBrowserProxyPort(browserProxyPort, tunnel, browserControlPort);
        var target = BuildSshTarget(tunnel);
        var remoteBrowserPort = ResolveRemoteBrowserProxyPort(localBrowserPort, tunnel);
        return remoteBrowserPort is >= 1 and <= 65535
            ? $"ssh -N -L {localBrowserPort}:127.0.0.1:{remoteBrowserPort} {target}"
            : $"ssh -N -L {localBrowserPort}:127.0.0.1:<remote-gateway-port+2> {target}";
    }

    private static string BuildSshTarget(TunnelCommandCenterInfo? tunnel)
    {
        var host = tunnel?.Host?.Trim();
        var user = tunnel?.User?.Trim();
        if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(user))
            return $"{user}@{host}";
        if (!string.IsNullOrWhiteSpace(host))
            return $"<user>@{host}";
        return "<user>@<host>";
    }

    private static int ResolveLocalBrowserProxyPort(int fallbackBrowserProxyPort, TunnelCommandCenterInfo? tunnel, int? browserControlPort)
    {
        // Honour the explicit BrowserControlPort override first so diagnostics + setup guidance
        // resolve the same effective endpoint browser.proxy dials (BrowserControlEndpoint priority 1).
        if (browserControlPort is { } overridePort && overridePort is >= 1 and <= 65535)
            return overridePort;

        if (TryGetEndpointPort(tunnel?.BrowserProxyLocalEndpoint, out var browserLocalPort))
            return browserLocalPort;

        if (TryGetEndpointPort(tunnel?.LocalEndpoint, out var localGatewayPort) &&
            localGatewayPort <= 65533)
        {
            return localGatewayPort + 2;
        }

        return fallbackBrowserProxyPort;
    }

    private static int? ResolveRemoteBrowserProxyPort(int localBrowserProxyPort, TunnelCommandCenterInfo? tunnel)
    {
        if (TryGetEndpointPort(tunnel?.BrowserProxyRemoteEndpoint, out var browserRemotePort))
            return browserRemotePort;

        if (!TryGetEndpointPort(tunnel?.RemoteEndpoint, out var remoteGatewayPort) ||
            remoteGatewayPort > 65533)
        {
            return null;
        }

        if (TryGetEndpointPort(tunnel?.LocalEndpoint, out var localGatewayPort) &&
            localBrowserProxyPort != localGatewayPort + 2)
        {
            return null;
        }

        return remoteGatewayPort + 2;
    }

    private static bool TryGetEndpointPort(string? endpoint, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        var separator = endpoint.LastIndexOf(':');
        return separator >= 0 &&
            int.TryParse(endpoint[(separator + 1)..], out port) &&
            port is >= 1 and <= 65535;
    }

    private static void ApplyDetectedSshForwardTopology(
        GatewayTopologyInfo topology,
        IReadOnlyList<PortDiagnosticInfo> ports)
    {
        if (topology.UsesSshTunnel ||
            topology.DetectedKind != GatewayKind.WindowsNative ||
            !topology.IsLoopback)
        {
            return;
        }

        var gatewayPort = ports.FirstOrDefault(port =>
            port.Purpose.Equals("Gateway endpoint", StringComparison.OrdinalIgnoreCase));
        if (gatewayPort is null ||
            !gatewayPort.IsListening ||
            !string.Equals(gatewayPort.OwningProcessName, "ssh", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        topology.DetectedKind = GatewayKind.MacOverSsh;
        topology.DisplayName = "SSH tunnel (detected)";
        topology.Transport = "ssh tunnel";
        topology.UsesSshTunnel = true;
        topology.Detail = $"Local gateway port {gatewayPort.Port} is owned by ssh, so Command Center treats it as a manually managed SSH local forward.";
    }

    private static GatewayRuntimeInfo BuildGatewayRuntimeInfo(IReadOnlyList<PortDiagnosticInfo> ports)
    {
        var gatewayPort = ports.FirstOrDefault(port =>
            port.Purpose.Equals("Gateway endpoint", StringComparison.OrdinalIgnoreCase));
        if (gatewayPort is null || !gatewayPort.IsListening)
            return new GatewayRuntimeInfo();

        return new GatewayRuntimeInfo
        {
            ProcessName = gatewayPort.OwningProcessName ?? "",
            ProcessId = gatewayPort.OwningProcessId,
            Port = gatewayPort.Port,
            IsSshForward = string.Equals(gatewayPort.OwningProcessName, "ssh", StringComparison.OrdinalIgnoreCase)
        };
    }

    // Resolve tunnel diagnostics from the active-GatewayRecord-first inputs (the same priority
    // browser.proxy dialing and the topology classifier use) rather than the raw global
    // SettingsManager. Otherwise a stale SettingsManager.UseSshTunnel could hide an active
    // gateway's tunnel (or surface a tunnel a now-direct gateway no longer uses), so the Command
    // Center diagnostics and the copied SSH guidance would not match the endpoint browser.proxy
    // actually dials. The active-record-first SSH user is resolved the same way Derive resolves
    // host/ports; CommandCenterTunnelInfoBuilder then layers live SshTunnelSnapshot values on top.
    private TunnelCommandCenterInfo? BuildTunnelInfo(CommandCenterTopologyTunnelResolver.TunnelInputs tunnelInputs)
    {
        var baseUser = _snapshot.HasActiveGatewayRecord
            ? _snapshot.ActiveGatewaySshTunnel?.User
            : _snapshot.Settings?.SshTunnelUser;
        return CommandCenterTunnelInfoBuilder.Build(tunnelInputs, baseUser, _snapshot.SshTunnelSnapshot);
    }
}
