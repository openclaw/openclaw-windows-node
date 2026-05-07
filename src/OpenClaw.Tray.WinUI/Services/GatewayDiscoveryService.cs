using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using Zeroconf;

namespace OpenClawTray.Services;

/// <summary>
/// Discovers OpenClaw gateways on the local network via mDNS/Bonjour.
/// Browses for _openclaw-gw._tcp services and resolves SRV records for endpoints.
/// TXT records are used for display metadata only — routing uses resolved SRV host/port.
/// </summary>
public sealed class GatewayDiscoveryService : IDisposable
{
    private const string ServiceType = "_openclaw-gw._tcp.local.";
    private CancellationTokenSource? _cts;
    private readonly List<DiscoveredGateway> _gateways = new();
    private bool _isSearching;

    public event EventHandler<IReadOnlyList<DiscoveredGateway>>? GatewaysUpdated;
    public event EventHandler<string>? StatusChanged;

    public IReadOnlyList<DiscoveredGateway> Gateways => _gateways.AsReadOnly();
    public bool IsSearching => _isSearching;

    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public async Task StartDiscoveryAsync()
    {
        if (!await _scanLock.WaitAsync(0))
            return; // Scan already in progress

        try
        {
            Stop();
            _cts = new CancellationTokenSource();
            _isSearching = true;
            StatusChanged?.Invoke(this, "Searching...");

            // Run mDNS and localhost probe in parallel
            var mdnsTask = RunMdnsDiscoveryAsync(_cts.Token);
            var probeTask = ProbeLocalhostAsync(_cts.Token);

            await Task.WhenAll(mdnsTask, probeTask);

            var mdnsResults = await mdnsTask;
            var probeResults = await probeTask;

            _gateways.Clear();
            _gateways.AddRange(mdnsResults);

            // Add probe results not already found by mDNS
            var knownEndpoints = new HashSet<string>(
                _gateways.Select(g => $"{g.Host}:{g.Port}"), StringComparer.OrdinalIgnoreCase);
            foreach (var gw in probeResults)
            {
                if (!knownEndpoints.Contains($"{gw.Host}:{gw.Port}"))
                    _gateways.Add(gw);
            }

            GatewaysUpdated?.Invoke(this, _gateways.AsReadOnly());
            StatusChanged?.Invoke(this, _gateways.Count > 0
                ? $"Found {_gateways.Count} gateway{(_gateways.Count != 1 ? "s" : "")}"
                : "No gateways found");
        }
        catch (OperationCanceledException)
        {
            StatusChanged?.Invoke(this, "Cancelled");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Error: {ex.Message}");
        }
        finally
        {
            _isSearching = false;
            _scanLock.Release();
        }
    }

    private async Task<List<DiscoveredGateway>> RunMdnsDiscoveryAsync(CancellationToken ct)
    {
        try
        {
            var results = await ZeroconfResolver.ResolveAsync(
                ServiceType,
                scanTime: TimeSpan.FromSeconds(5),
                cancellationToken: ct);

            return results
                .Select(ParseHost)
                .Where(g => g != null)
                .Cast<DiscoveredGateway>()
                .GroupBy(g => $"{g.Host}:{g.Port}")
                .Select(g => g.First())
                .ToList();
        }
        catch (OperationCanceledException) { return new(); }
        catch { return new(); }
    }

    /// <summary>
    /// Discovers OpenClaw gateways on localhost by enumerating listening TCP ports
    /// and probing them for the gateway HTML signature.
    /// </summary>
    private static async Task<List<DiscoveredGateway>> ProbeLocalhostAsync(CancellationToken ct)
    {
        var results = new List<DiscoveredGateway>();
        try
        {
            // Get all listening TCP ports on localhost (instant, no network I/O)
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            // Exclude: system ports (<1024), MCP server port (8765)
            var excludePorts = new HashSet<int> { 8765 };
            var ports = listeners
                .Where(ep => IPAddress.IsLoopback(ep.Address) || ep.Address.Equals(IPAddress.Any))
                .Select(ep => ep.Port)
                .Where(p => p >= 1024 && !excludePorts.Contains(p))
                .Distinct()
                .ToList();

            if (ports.Count == 0) return results;

            // Probe each port in parallel with short timeout
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
            var tasks = ports.Select(async port =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var response = await http.GetStringAsync($"http://127.0.0.1:{port}/", ct);
                    // Match the gateway's specific HTML title — not generic "OpenClaw" which matches MCP too
                    if (response.Contains("<title>OpenClaw Control</title>", StringComparison.OrdinalIgnoreCase))
                    {
                        return new DiscoveredGateway
                        {
                            Id = $"local-{port}",
                            DisplayName = $"Local Gateway (:{port})",
                            Host = "127.0.0.1",
                            Port = port,
                            TlsEnabled = false
                        };
                    }
                }
                catch { /* port doesn't respond or isn't a gateway */ }
                return null;
            });

            var probed = await Task.WhenAll(tasks);
            results.AddRange(probed.Where(g => g != null).Cast<DiscoveredGateway>());
        }
        catch { /* best effort */ }
        return results;
    }

    internal static DiscoveredGateway? ParseHost(IZeroconfHost host)
    {
        // Use resolved SRV record for routing (not TXT hints)
        var service = host.Services.Values.FirstOrDefault();
        if (service == null) return null;

        var resolvedHost = !string.IsNullOrWhiteSpace(host.IPAddress) ? host.IPAddress : host.DisplayName;
        if (string.IsNullOrWhiteSpace(resolvedHost)) return null;
        var resolvedPort = service.Port;

        // Parse TXT records for display metadata only
        var txt = service.Properties
            .SelectMany(p => p)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        txt.TryGetValue("displayName", out var displayName);
        txt.TryGetValue("lanHost", out var lanHost);
        txt.TryGetValue("tailnetDns", out var tailnetDns);
        txt.TryGetValue("gatewayTls", out var tlsFlag);
        txt.TryGetValue("gatewayTlsSha256", out var tlsFingerprint);

        // Routing uses resolved SRV data only — TXT is for display metadata

        return new DiscoveredGateway
        {
            Id = $"{resolvedHost}:{resolvedPort}",
            DisplayName = PrettifyName(displayName ?? host.DisplayName ?? resolvedHost),
            Host = resolvedHost,
            Port = resolvedPort,
            LanHost = lanHost,
            TailnetDns = tailnetDns,
            TlsEnabled = tlsFlag == "1",
            TlsFingerprint = tlsFingerprint
        };
    }

    private static string PrettifyName(string name)
    {
        // Strip .local suffix and capitalize
        if (name.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            name = name[..^6];
        if (name.EndsWith(".local.", StringComparison.OrdinalIgnoreCase))
            name = name[..^7];
        return name;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _isSearching = false;
    }

    public void Dispose()
    {
        Stop();
        _scanLock?.Dispose();
    }
}
