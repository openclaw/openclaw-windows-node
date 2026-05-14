using OpenClaw.Shared;
using OpenClawTray.Helpers;
using System.Linq;

namespace OpenClawTray.Services;

internal sealed class TrayTooltipBuilder
{
    private readonly TrayStateSnapshot _snapshot;

    internal TrayTooltipBuilder(TrayStateSnapshot snapshot)
    {
        _snapshot = snapshot;
    }

    internal string Build()
    {
        var topology = GatewayTopologyClassifier.Classify(
            _snapshot.Settings?.GatewayUrl,
            _snapshot.Settings?.UseSshTunnel == true,
            _snapshot.Settings?.SshTunnelHost,
            _snapshot.Settings?.SshTunnelLocalPort ?? 0,
            _snapshot.Settings?.SshTunnelRemotePort ?? 0);

        var channelReady = _snapshot.Channels.Count(c => ChannelHealth.IsHealthyStatus(c.Status));
        var nodeOnline = _snapshot.Nodes.Count(n => n.IsOnline);
        var nodeTotal = _snapshot.Nodes.Length;
        if (nodeTotal == 0 && _snapshot.LocalNodeFallback is { } localNode)
        {
            nodeTotal = 1;
            nodeOnline = localNode.IsOnline ? 1 : 0;
        }

        var warningCount = 0;
        if (_snapshot.Status != ConnectionStatus.Connected) warningCount++;
        if (_snapshot.AuthFailureMessage != null) warningCount++;
        if (_snapshot.Channels.Length == 0 && _snapshot.Status == ConnectionStatus.Connected) warningCount++;

        var tooltip = $"OpenClaw Tray - {_snapshot.Status}; " +
            $"{topology.DisplayName}; " +
            $"Channels {channelReady}/{_snapshot.Channels.Length}; " +
            $"Nodes {nodeOnline}/{nodeTotal}; " +
            $"Warnings {warningCount}; " +
            $"Last {_snapshot.LastCheckTime:HH:mm:ss}";

        if (_snapshot.CurrentActivity != null && !string.IsNullOrEmpty(_snapshot.CurrentActivity.DisplayText))
        {
            tooltip = $"OpenClaw Tray - {_snapshot.CurrentActivity.DisplayText}; {_snapshot.Status}";
        }

        return TrayTooltipFormatter.FitShellTooltip(tooltip);
    }
}
