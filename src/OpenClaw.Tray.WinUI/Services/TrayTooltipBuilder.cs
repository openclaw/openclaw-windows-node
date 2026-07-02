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

        var statusText = BuildStatusText();
        var overallState = _snapshot.OverallState;
        var isHealthy = ConnectionStatusPresenter.IsHealthy(overallState, _snapshot.Status);
        var warningCount = 0;
        if (!isHealthy) warningCount++;
        if (_snapshot.AuthFailureMessage != null) warningCount++;
        if (HasRelevantMcpStartupError()) warningCount++;
        if (_snapshot.Channels.Length == 0 && isHealthy) warningCount++;

        var tooltip = $"OpenClaw Tray - {statusText}; " +
            $"{topology.DisplayName}; " +
            $"Channels {channelReady}/{_snapshot.Channels.Length}; " +
            $"Nodes {nodeOnline}/{nodeTotal}; " +
            $"Warnings {warningCount}; " +
            $"Last {_snapshot.LastCheckTime:HH:mm:ss}";

        if (_snapshot.CurrentActivity != null && !string.IsNullOrEmpty(_snapshot.CurrentActivity.DisplayText))
        {
            tooltip = $"OpenClaw Tray - {_snapshot.CurrentActivity.DisplayText}; {statusText}";
        }

        return TrayTooltipFormatter.FitShellTooltip(tooltip);
    }

    private string BuildStatusText()
    {
        if (HasRelevantMcpStartupError())
            return "Local MCP failed";

        if (IsStandaloneMcpOnly())
        {
            return "Local MCP only";
        }

        return ConnectionStatusPresenter.PlainText(_snapshot.OverallState, _snapshot.Status);
    }

    private bool IsStandaloneMcpOnly() =>
        _snapshot.Settings?.EnableMcpServer == true &&
        _snapshot.Settings?.EnableNodeMode == false &&
        _snapshot.IsMcpRunning &&
        (_snapshot.OverallState is null or OpenClaw.Connection.OverallConnectionState.Idle) &&
        _snapshot.Status != ConnectionStatus.Connected;

    private bool HasRelevantMcpStartupError() =>
        _snapshot.Settings?.EnableMcpServer == true &&
        !string.IsNullOrWhiteSpace(_snapshot.McpStartupError);
}
