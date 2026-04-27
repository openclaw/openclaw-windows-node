using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;

namespace OpenClawTray.Services;

public static class PortDiagnosticsService
{
    public static List<PortDiagnosticInfo> BuildDiagnostics(
        GatewayTopologyInfo topology,
        TunnelCommandCenterInfo? tunnel)
    {
        var localTcpPorts = GetLocalTcpListenerPorts();
        var diagnostics = new List<PortDiagnosticInfo>();

        if (TryGetPort(topology.GatewayUrl, out var gatewayPort) && topology.IsLoopback)
        {
            diagnostics.Add(Create("Gateway endpoint", gatewayPort, localTcpPorts));
        }

        if (TryGetBrowserProxyPort(topology, out var browserProxyPort))
        {
            diagnostics.Add(Create("Browser proxy host", browserProxyPort, localTcpPorts));
        }

        if (tunnel != null && TryGetEndpointPort(tunnel.LocalEndpoint, out var tunnelPort))
        {
            diagnostics.Add(Create("SSH tunnel local forward", tunnelPort, localTcpPorts));
        }

        return diagnostics
            .GroupBy(d => $"{d.Purpose}|{d.Port}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static PortDiagnosticInfo Create(string purpose, int port, IReadOnlySet<int> localTcpPorts)
    {
        var isListening = localTcpPorts.Contains(port);
        return new PortDiagnosticInfo
        {
            Purpose = purpose,
            Port = port,
            IsLocal = true,
            IsListening = isListening,
            Detail = isListening
                ? $"Local TCP port {port} has a listener."
                : $"Local TCP port {port} does not currently have a listener."
        };
    }

    private static IReadOnlySet<int> GetLocalTcpListenerPorts()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(endpoint => endpoint.Port)
                .ToHashSet();
        }
        catch (NetworkInformationException)
        {
            return new HashSet<int>();
        }
    }

    private static bool TryGetPort(string? url, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Port <= 0)
        {
            return false;
        }

        port = uri.Port;
        return true;
    }

    private static bool TryGetBrowserProxyPort(GatewayTopologyInfo topology, out int port)
    {
        port = 0;
        if (topology.DetectedKind is not (GatewayKind.WindowsNative or GatewayKind.Wsl) ||
            !TryGetPort(topology.GatewayUrl, out var gatewayPort) ||
            gatewayPort > 65533)
        {
            return false;
        }

        port = gatewayPort + 2;
        return true;
    }

    private static bool TryGetEndpointPort(string? endpoint, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        var separator = endpoint.LastIndexOf(':');
        return separator >= 0 &&
            int.TryParse(endpoint.AsSpan(separator + 1), out port) &&
            port is >= 1 and <= 65535;
    }
}
