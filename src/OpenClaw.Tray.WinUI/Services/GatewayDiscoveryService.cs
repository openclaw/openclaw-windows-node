using System;
using System.Collections.Generic;
using System.Linq;
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

            var results = await ZeroconfResolver.ResolveAsync(
                ServiceType,
                scanTime: TimeSpan.FromSeconds(5),
                cancellationToken: _cts.Token);

            _gateways.Clear();
            foreach (var host in results)
            {
                var gw = ParseHost(host);
                if (gw != null)
                    _gateways.Add(gw);
            }

            // Deduplicate by endpoint
            var deduped = _gateways
                .GroupBy(g => $"{g.Host}:{g.Port}")
                .Select(g => g.First())
                .ToList();
            _gateways.Clear();
            _gateways.AddRange(deduped);

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

    private static DiscoveredGateway? ParseHost(IZeroconfHost host)
    {
        // Use resolved SRV record for routing (not TXT hints)
        var service = host.Services.Values.FirstOrDefault();
        if (service == null) return null;

        var resolvedHost = !string.IsNullOrEmpty(host.DisplayName) ? host.DisplayName : host.IPAddress;
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
    }
}
