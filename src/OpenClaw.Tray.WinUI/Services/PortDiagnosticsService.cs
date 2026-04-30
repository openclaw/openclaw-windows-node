using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace OpenClawTray.Services;

public static class PortDiagnosticsService
{
    public static List<PortDiagnosticInfo> BuildDiagnostics(
        GatewayTopologyInfo topology,
        TunnelCommandCenterInfo? tunnel)
    {
        var localTcpPorts = GetLocalTcpListeners();
        var diagnostics = new List<PortDiagnosticInfo>();

        if (TryGetPort(topology.GatewayUrl, out var gatewayPort) && topology.IsLoopback)
        {
            diagnostics.Add(Create("Gateway endpoint", gatewayPort, localTcpPorts));
        }

        if (TryGetBrowserProxyPort(topology, tunnel, out var browserProxyPort))
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

    private static PortDiagnosticInfo Create(string purpose, int port, IReadOnlyDictionary<int, TcpListenerOwner> localTcpPorts)
    {
        var isListening = localTcpPorts.ContainsKey(port);
        localTcpPorts.TryGetValue(port, out var owner);
        var ownerDetail = owner.ProcessId > 0
            ? $" Owner: {FormatProcessName(owner.ProcessName)} (PID {owner.ProcessId})."
            : "";
        return new PortDiagnosticInfo
        {
            Purpose = purpose,
            Port = port,
            IsLocal = true,
            IsListening = isListening,
            OwningProcessId = owner.ProcessId > 0 ? owner.ProcessId : null,
            OwningProcessName = owner.ProcessName,
            Detail = isListening
                ? $"Local TCP port {port} has a listener.{ownerDetail}"
                : $"Local TCP port {port} does not currently have a listener."
        };
    }

    private static IReadOnlyDictionary<int, TcpListenerOwner> GetLocalTcpListeners()
    {
        var listeners = new Dictionary<int, TcpListenerOwner>();
        try
        {
            foreach (var endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                listeners.TryAdd(endpoint.Port, new TcpListenerOwner(0, null));
            }
        }
        catch (NetworkInformationException)
        {
            return listeners;
        }

        foreach (var owner in GetWindowsTcpListenerOwners())
        {
            listeners[owner.Port] = new TcpListenerOwner(owner.ProcessId, ResolveProcessName(owner.ProcessId));
        }

        return listeners;
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

    private static bool TryGetBrowserProxyPort(GatewayTopologyInfo topology, TunnelCommandCenterInfo? tunnel, out int port)
    {
        port = 0;
        if (topology.UsesSshTunnel &&
            TryGetEndpointPort(tunnel?.LocalEndpoint, out var tunnelLocalPort) &&
            tunnelLocalPort <= 65533)
        {
            port = tunnelLocalPort + 2;
            return true;
        }

        if (topology.DetectedKind is not (GatewayKind.WindowsNative or GatewayKind.Wsl or GatewayKind.MacOverSsh) ||
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

    private static string FormatProcessName(string? processName) =>
        string.IsNullOrWhiteSpace(processName) ? "unknown process" : processName;

    private static string? ResolveProcessName(int processId)
    {
        if (processId <= 0)
            return null;

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static IEnumerable<TcpListenerProcessOwner> GetWindowsTcpListenerOwners()
    {
        if (!OperatingSystem.IsWindows())
            yield break;

        var bufferLength = 0;
        var result = GetExtendedTcpTable(
            IntPtr.Zero,
            ref bufferLength,
            sort: true,
            ipVersion: AfInet,
            tableClass: TcpTableOwnerPidListener,
            reserved: 0);
        if (result != ErrorInsufficientBuffer || bufferLength <= 0)
            yield break;

        var tablePtr = Marshal.AllocHGlobal(bufferLength);
        try
        {
            result = GetExtendedTcpTable(
                tablePtr,
                ref bufferLength,
                sort: true,
                ipVersion: AfInet,
                tableClass: TcpTableOwnerPidListener,
                reserved: 0);
            if (result != ErrorSuccess)
                yield break;

            var rowCount = Marshal.ReadInt32(tablePtr);
            var rowPtr = IntPtr.Add(tablePtr, sizeof(int));
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            for (var i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                var port = (row.LocalPort[0] << 8) + row.LocalPort[1];
                if (port is >= 1 and <= 65535)
                    yield return new TcpListenerProcessOwner(port, unchecked((int)row.OwningProcessId));
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(tablePtr);
        }
    }

    private readonly record struct TcpListenerOwner(int ProcessId, string? ProcessName);
    private readonly record struct TcpListenerProcessOwner(int Port, int ProcessId);

    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint ErrorSuccess = 0;
    private const uint ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int tcpTableLength,
        bool sort,
        int ipVersion,
        int tableClass,
        uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] LocalPort;
        public uint RemoteAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] RemotePort;
        public uint OwningProcessId;
    }
}
