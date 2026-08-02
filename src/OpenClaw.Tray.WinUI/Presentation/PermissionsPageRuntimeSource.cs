using OpenClawTray.Services;
using System.Text.Json;

namespace OpenClawTray.Presentation;

internal sealed class PermissionsPageRuntimeSource : IPermissionsPageRuntimeSource, IDisposable
{
    private readonly IPermissionsPageRuntimeHost _host;
    private bool _disposed;

    public PermissionsPageRuntimeSource(IPermissionsPageRuntimeHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _host.Changed += OnHostChanged;
    }

    public event EventHandler<PermissionsRuntimeSourceChangedEventArgs>? Changed;

    public PermissionsRuntimeSourceSnapshot Current
    {
        get
        {
            ThrowIfDisposed();
            return BuildSnapshot();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _host.Changed -= OnHostChanged;
        _disposed = true;
    }

    private void OnHostChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Changed?.Invoke(this, new PermissionsRuntimeSourceChangedEventArgs(BuildSnapshot()));
    }

    private PermissionsRuntimeSourceSnapshot BuildSnapshot()
    {
        var capabilities = NodeCapabilityGating.GetLocalNodeCapabilities(_host.Nodes, _host.LocalNodeDeviceId)
            ?? Array.Empty<string>();
        var allowlist = GetGatewayAllowCommands(_host.GatewayConfig);

        return new PermissionsRuntimeSourceSnapshot(
            _host.ConnectionSnapshot,
            _host.McpStartupError,
            _host.McpEndpoint,
            _host.IsMcpTokenReady,
            _host.McpServedCapabilityCount,
            capabilities,
            allowlist.Commands,
            allowlist.State,
            _host.VoiceSetupRequirement);
    }

    private static GatewayAllowlistProjection GetGatewayAllowCommands(JsonElement? config)
    {
        if (!config.HasValue)
        {
            return new GatewayAllowlistProjection(
                PermissionsGatewayAllowlistState.NoConfig,
                Array.Empty<string>());
        }

        try
        {
            var commands = new List<string>();
            var root = config.Value;
            if (root.TryGetProperty("gateway", out var gateway)
                && gateway.TryGetProperty("nodes", out var nodes)
                && nodes.TryGetProperty("allowCommands", out var allowCommands)
                && allowCommands.ValueKind == JsonValueKind.Array)
            {
                foreach (var command in allowCommands.EnumerateArray())
                {
                    if (command.ValueKind != JsonValueKind.String)
                    {
                        return new GatewayAllowlistProjection(
                            PermissionsGatewayAllowlistState.ParseFailed,
                            Array.Empty<string>());
                    }

                    var value = command.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        commands.Add(value);
                    }
                }
            }

            return new GatewayAllowlistProjection(
                commands.Count == 0
                    ? PermissionsGatewayAllowlistState.NoCommands
                    : PermissionsGatewayAllowlistState.Commands,
                commands);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new GatewayAllowlistProjection(
                PermissionsGatewayAllowlistState.ParseFailed,
                Array.Empty<string>());
        }
    }

    private sealed record GatewayAllowlistProjection(
        PermissionsGatewayAllowlistState State,
        IReadOnlyList<string> Commands);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
