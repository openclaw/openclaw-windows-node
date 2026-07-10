using OpenClaw.Connection;
using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace OpenClawTray.Services;

internal static class ConnectionDiagnosticsProjection
{
    internal static ConnectionStatusDiagnostics BuildStatus(
        GatewayConnectionSnapshot? currentSnapshot,
        GatewayRecord? activeGateway,
        bool enableNodeMode,
        bool enableMcpServer,
        bool isMcpRunning,
        string? mcpError,
        bool nodeBrowserProxyEnabled,
        IReadOnlyList<ConnectionDiagnosticEvent> recentDiagnostics,
        int diagnosticEventCount)
    {
        var snapshot = currentSnapshot ?? GatewayConnectionSnapshot.Idle;
        var legacyStatus = ConnectionStatusPresenter.ToLegacyStatus(snapshot);
        var pendingActions = BuildPendingActions(snapshot);
        var recentEvents = recentDiagnostics
            .TakeLast(12)
            .Select(ToDiagnosticEvent)
            .ToArray();

        return new ConnectionStatusDiagnostics(
            SchemaVersion: 1,
            ConnectionState: snapshot.OverallState.ToString(),
            EffectiveMode: GetEffectiveMode(enableNodeMode, enableMcpServer),
            LegacyConnectionStatus: legacyStatus.ToString(),
            Gateway: BuildGateway(activeGateway, snapshot, isActive: true, nodeBrowserProxyEnabled),
            Operator: new OperatorConnectionDiagnostics(
                State: snapshot.OperatorState.ToString(),
                Connected: snapshot.OperatorState == RoleConnectionState.Connected,
                Error: snapshot.OperatorError,
                PairingRequired: snapshot.OperatorPairingRequired || snapshot.OperatorState == RoleConnectionState.PairingRequired,
                PairingRequestId: snapshot.OperatorPairingRequestId,
                Credential: BuildCredential(
                    snapshot.OperatorCredentialSource,
                    snapshot.OperatorCredentialStatus,
                    snapshot.OperatorCredentialFallbackUsed,
                    snapshot.OperatorCredentialBootstrapRequired,
                    snapshot.OperatorCredentialDetail),
                DeviceId: snapshot.OperatorDeviceId),
            Node: new NodeConnectionDiagnostics(
                Intended: snapshot.NodeConnectionIntended || enableNodeMode,
                State: snapshot.NodeState.ToString(),
                Connected: snapshot.NodeState == RoleConnectionState.Connected,
                Paired: snapshot.NodePairingStatus == PairingStatus.Paired,
                PendingApproval: snapshot.NodeState == RoleConnectionState.PairingRequired,
                PairingStatus: snapshot.NodePairingStatus.ToString(),
                PairingApprovalKind: snapshot.NodePairingApprovalKind.ToString(),
                PairingRequestId: snapshot.NodePairingRequestId,
                ApprovalCommand: BuildNodeApprovalCommand(snapshot),
                Error: snapshot.NodeError,
                Credential: BuildCredential(
                    snapshot.NodeCredentialSource,
                    snapshot.NodeCredentialStatus,
                    snapshot.NodeCredentialFallbackUsed,
                    snapshot.NodeCredentialBootstrapRequired,
                    snapshot.NodeCredentialDetail),
                DeviceId: snapshot.NodeDeviceId),
            Mcp: new McpConnectionDiagnostics(
                Enabled: enableMcpServer,
                Running: isMcpRunning,
                Error: mcpError),
            BrowserProxy: BuildBrowserProxy(activeGateway, nodeBrowserProxyEnabled),
            PendingActions: pendingActions,
            Retry: BuildRetry(recentDiagnostics),
            Diagnostics: BuildDiagnosticSummary(recentEvents, recentDiagnostics, diagnosticEventCount));
    }

    internal static GatewayListDiagnostics BuildGateways(
        IReadOnlyList<GatewayRecord> gateways,
        string? activeGatewayId,
        bool nodeBrowserProxyEnabled)
    {
        var items = gateways
            .OrderByDescending(g => string.Equals(g.Id, activeGatewayId, StringComparison.Ordinal))
            .ThenBy(g => g.FriendlyName ?? g.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => BuildGateway(
                g,
                currentSnapshot: null,
                isActive: string.Equals(g.Id, activeGatewayId, StringComparison.Ordinal),
                nodeBrowserProxyEnabled))
            .OfType<GatewayDiagnostics>()
            .ToArray();

        return new GatewayListDiagnostics(
            ActiveGatewayId: activeGatewayId,
            Count: items.Length,
            Gateways: items);
    }

    private static CredentialDiagnostics BuildCredential(
        string? source,
        GatewayCredentialResolutionStatus? status,
        bool fallbackUsed,
        bool bootstrapRequired,
        string? detail) =>
        new(
            Source: source,
            Status: status?.ToString(),
            FallbackUsed: fallbackUsed,
            BootstrapRequired: bootstrapRequired,
            Detail: detail);

    private static GatewayDiagnostics? BuildGateway(
        GatewayRecord? gateway,
        GatewayConnectionSnapshot? currentSnapshot,
        bool isActive,
        bool nodeBrowserProxyEnabled)
    {
        var id = gateway?.Id ?? currentSnapshot?.GatewayId;
        var url = GatewayUrlHelper.SanitizeForDisplay(gateway?.Url ?? currentSnapshot?.GatewayUrl);
        var name = gateway?.FriendlyName ?? currentSnapshot?.GatewayName;
        if (string.IsNullOrWhiteSpace(id) &&
            string.IsNullOrWhiteSpace(url) &&
            string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new GatewayDiagnostics(
            Id: id,
            Name: name,
            Url: url,
            IsActive: isActive,
            IsLocal: gateway?.IsLocal,
            LastConnected: gateway?.LastConnected,
            RequiresV2Signature: gateway?.RequiresV2Signature,
            HasSharedGatewayToken: !string.IsNullOrWhiteSpace(gateway?.SharedGatewayToken),
            HasBootstrapToken: !string.IsNullOrWhiteSpace(gateway?.BootstrapToken),
            BrowserControlPort: gateway?.BrowserControlPort,
            BrowserProxyCaveat: BuildBrowserProxyCaveat(gateway, nodeBrowserProxyEnabled, isActive),
            SshTunnel: gateway?.SshTunnel is null ? null : new GatewaySshTunnelDiagnostics(
                User: gateway.SshTunnel.User,
                Host: gateway.SshTunnel.Host,
                RemotePort: gateway.SshTunnel.RemotePort,
                LocalPort: gateway.SshTunnel.LocalPort,
                SshPort: gateway.SshTunnel.SshPort,
                IncludeBrowserProxyForward: gateway.SshTunnel.IncludeBrowserProxyForward));
    }

    private static BrowserProxyDiagnostics BuildBrowserProxy(GatewayRecord? gateway, bool nodeBrowserProxyEnabled)
    {
        var hasSharedToken = !string.IsNullOrWhiteSpace(gateway?.SharedGatewayToken);
        return new BrowserProxyDiagnostics(
            Enabled: nodeBrowserProxyEnabled,
            ActiveGatewayHasSharedToken: hasSharedToken,
            BrowserControlPort: gateway?.BrowserControlPort,
            SshBrowserProxyForward: gateway?.SshTunnel?.IncludeBrowserProxyForward == true,
            Caveat: BuildBrowserProxyCaveat(gateway, nodeBrowserProxyEnabled, isActive: true));
    }

    private static string? BuildBrowserProxyCaveat(GatewayRecord? gateway, bool nodeBrowserProxyEnabled, bool isActive)
    {
        if (!isActive ||
            !nodeBrowserProxyEnabled ||
            gateway is null ||
            !string.IsNullOrWhiteSpace(gateway.SharedGatewayToken))
        {
            return null;
        }

        return "browser.proxy may need a saved shared gateway token for browser-control authentication; QR/bootstrap pairing alone can leave this token absent.";
    }

    private static ConnectionPendingActionDiagnostics[] BuildPendingActions(GatewayConnectionSnapshot snapshot)
    {
        var actions = new List<ConnectionPendingActionDiagnostics>();
        if (snapshot.OperatorPairingRequired || snapshot.OperatorState == RoleConnectionState.PairingRequired)
        {
            actions.Add(new ConnectionPendingActionDiagnostics(
                Kind: "operatorDevicePairing",
                State: snapshot.OperatorState.ToString(),
                RequestId: snapshot.OperatorPairingRequestId,
                Command: CommandCenterDiagnostics.BuildDeviceApprovalRepairCommand(snapshot.OperatorPairingRequestId),
                Summary: "Approve the operator device pairing request for the active gateway."));
        }

        if (snapshot.NodeState == RoleConnectionState.PairingRequired)
        {
            var command = BuildNodeApprovalCommand(snapshot) ?? CommandCenterDiagnostics.BuildUnknownPairingDiscoveryCommands();
            actions.Add(new ConnectionPendingActionDiagnostics(
                Kind: "nodePairing",
                State: snapshot.NodeState.ToString(),
                RequestId: snapshot.NodePairingRequestId,
                Command: command,
                Summary: "Approve the Windows node pairing or command-trust request for the active gateway."));
        }

        return actions.ToArray();
    }

    private static string? BuildNodeApprovalCommand(GatewayConnectionSnapshot snapshot) =>
        snapshot.NodeState != RoleConnectionState.PairingRequired
            ? null
            : snapshot.NodePairingApprovalKind switch
            {
                PairingApprovalKind.DevicePair => CommandCenterDiagnostics.BuildDeviceApprovalRepairCommand(snapshot.NodePairingRequestId),
                PairingApprovalKind.NodePair => CommandCenterDiagnostics.BuildNodeApprovalRepairCommand(snapshot.NodePairingRequestId),
                _ => CommandCenterDiagnostics.BuildUnknownPairingDiscoveryCommands()
            };

    private static RetryDiagnostics BuildRetry(IReadOnlyList<ConnectionDiagnosticEvent> recentDiagnostics)
    {
        var retryEvent = recentDiagnostics.LastOrDefault(evt =>
            Contains(evt.Message, "retry") ||
            Contains(evt.Message, "reconnect") ||
            Contains(evt.Detail, "retry") ||
            Contains(evt.Detail, "reconnect"));

        return new RetryDiagnostics(
            HasRecentRetrySignal: retryEvent is not null,
            LastRetryAt: retryEvent?.Timestamp,
            LastRetryMessage: retryEvent?.Message,
            NextRetryAt: null,
            PausedReason: null,
            Detail: "Retry attempt and next-retry timestamps are not published as manager state; recent retry/reconnect diagnostics are surfaced when present.");
    }

    private static DiagnosticSummary BuildDiagnosticSummary(
        IReadOnlyList<DiagnosticEventDiagnostics> recentEvents,
        IReadOnlyList<ConnectionDiagnosticEvent> sourceEvents,
        int eventCount)
    {
        var lastError = sourceEvents.LastOrDefault(evt =>
            Contains(evt.Category, "error") ||
            Contains(evt.Message, "error") ||
            Contains(evt.Message, "failed") ||
            Contains(evt.Detail, "error") ||
            Contains(evt.Detail, "failed"));
        var lastStateChange = sourceEvents.LastOrDefault(evt =>
            string.Equals(evt.Category, "state", StringComparison.OrdinalIgnoreCase));

        return new DiagnosticSummary(
            EventCount: eventCount,
            LastEventAt: sourceEvents.LastOrDefault()?.Timestamp,
            LastStateChangeAt: lastStateChange?.Timestamp,
            LastStateChange: lastStateChange?.Message,
            LastErrorAt: lastError?.Timestamp,
            LastError: lastError is null ? null : FormatDiagnostic(lastError),
            RecentEvents: recentEvents);
    }

    private static DiagnosticEventDiagnostics ToDiagnosticEvent(ConnectionDiagnosticEvent evt) =>
        new(evt.Timestamp, evt.Category, evt.Message, evt.Detail);

    private static string FormatDiagnostic(ConnectionDiagnosticEvent evt) =>
        string.IsNullOrWhiteSpace(evt.Detail)
            ? $"{evt.Category}: {evt.Message}"
            : $"{evt.Category}: {evt.Message} ({evt.Detail})";

    private static string GetEffectiveMode(bool enableNodeMode, bool enableMcpServer) =>
        (enableNodeMode, enableMcpServer) switch
        {
            (true, true) => "GatewayNodeAndLocalMcp",
            (true, false) => "GatewayNode",
            (false, true) => "LocalMcpOnly",
            _ => "OperatorOnly"
        };

    private static bool Contains(string? value, string needle) =>
        !string.IsNullOrEmpty(value) &&
        value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}

internal sealed record ConnectionStatusDiagnostics(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("connectionState")] string ConnectionState,
    [property: JsonPropertyName("effectiveMode")] string EffectiveMode,
    [property: JsonPropertyName("legacyConnectionStatus")] string LegacyConnectionStatus,
    [property: JsonPropertyName("gateway")] GatewayDiagnostics? Gateway,
    [property: JsonPropertyName("operator")] OperatorConnectionDiagnostics Operator,
    [property: JsonPropertyName("node")] NodeConnectionDiagnostics Node,
    [property: JsonPropertyName("mcp")] McpConnectionDiagnostics Mcp,
    [property: JsonPropertyName("browserProxy")] BrowserProxyDiagnostics BrowserProxy,
    [property: JsonPropertyName("pendingActions")] IReadOnlyList<ConnectionPendingActionDiagnostics> PendingActions,
    [property: JsonPropertyName("retry")] RetryDiagnostics Retry,
    [property: JsonPropertyName("diagnostics")] DiagnosticSummary Diagnostics);

internal sealed record GatewayListDiagnostics(
    [property: JsonPropertyName("activeGatewayId")] string? ActiveGatewayId,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("gateways")] IReadOnlyList<GatewayDiagnostics> Gateways);

internal sealed record GatewayDiagnostics(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("isActive")] bool IsActive,
    [property: JsonPropertyName("isLocal")] bool? IsLocal,
    [property: JsonPropertyName("lastConnected")] DateTime? LastConnected,
    [property: JsonPropertyName("requiresV2Signature")] bool? RequiresV2Signature,
    [property: JsonPropertyName("hasSharedGatewayToken")] bool HasSharedGatewayToken,
    [property: JsonPropertyName("hasBootstrapToken")] bool HasBootstrapToken,
    [property: JsonPropertyName("browserControlPort")] int? BrowserControlPort,
    [property: JsonPropertyName("browserProxyCaveat")] string? BrowserProxyCaveat,
    [property: JsonPropertyName("sshTunnel")] GatewaySshTunnelDiagnostics? SshTunnel);

internal sealed record GatewaySshTunnelDiagnostics(
    [property: JsonPropertyName("user")] string User,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("remotePort")] int RemotePort,
    [property: JsonPropertyName("localPort")] int LocalPort,
    [property: JsonPropertyName("sshPort")] int SshPort,
    [property: JsonPropertyName("includeBrowserProxyForward")] bool IncludeBrowserProxyForward);

internal sealed record OperatorConnectionDiagnostics(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("connected")] bool Connected,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("pairingRequired")] bool PairingRequired,
    [property: JsonPropertyName("pairingRequestId")] string? PairingRequestId,
    [property: JsonPropertyName("credential")] CredentialDiagnostics Credential,
    [property: JsonPropertyName("deviceId")] string? DeviceId);

internal sealed record NodeConnectionDiagnostics(
    [property: JsonPropertyName("intended")] bool Intended,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("connected")] bool Connected,
    [property: JsonPropertyName("paired")] bool Paired,
    [property: JsonPropertyName("pendingApproval")] bool PendingApproval,
    [property: JsonPropertyName("pairingStatus")] string PairingStatus,
    [property: JsonPropertyName("pairingApprovalKind")] string PairingApprovalKind,
    [property: JsonPropertyName("pairingRequestId")] string? PairingRequestId,
    [property: JsonPropertyName("approvalCommand")] string? ApprovalCommand,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("credential")] CredentialDiagnostics Credential,
    [property: JsonPropertyName("deviceId")] string? DeviceId);

internal sealed record CredentialDiagnostics(
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("fallbackUsed")] bool FallbackUsed,
    [property: JsonPropertyName("bootstrapRequired")] bool BootstrapRequired,
    [property: JsonPropertyName("detail")] string? Detail);

internal sealed record McpConnectionDiagnostics(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("running")] bool Running,
    [property: JsonPropertyName("error")] string? Error);

internal sealed record BrowserProxyDiagnostics(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("activeGatewayHasSharedToken")] bool ActiveGatewayHasSharedToken,
    [property: JsonPropertyName("browserControlPort")] int? BrowserControlPort,
    [property: JsonPropertyName("sshBrowserProxyForward")] bool SshBrowserProxyForward,
    [property: JsonPropertyName("caveat")] string? Caveat);

internal sealed record ConnectionPendingActionDiagnostics(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("requestId")] string? RequestId,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("summary")] string Summary);

internal sealed record RetryDiagnostics(
    [property: JsonPropertyName("hasRecentRetrySignal")] bool HasRecentRetrySignal,
    [property: JsonPropertyName("lastRetryAt")] DateTime? LastRetryAt,
    [property: JsonPropertyName("lastRetryMessage")] string? LastRetryMessage,
    [property: JsonPropertyName("nextRetryAt")] DateTime? NextRetryAt,
    [property: JsonPropertyName("pausedReason")] string? PausedReason,
    [property: JsonPropertyName("detail")] string Detail);

internal sealed record DiagnosticSummary(
    [property: JsonPropertyName("eventCount")] int EventCount,
    [property: JsonPropertyName("lastEventAt")] DateTime? LastEventAt,
    [property: JsonPropertyName("lastStateChangeAt")] DateTime? LastStateChangeAt,
    [property: JsonPropertyName("lastStateChange")] string? LastStateChange,
    [property: JsonPropertyName("lastErrorAt")] DateTime? LastErrorAt,
    [property: JsonPropertyName("lastError")] string? LastError,
    [property: JsonPropertyName("recentEvents")] IReadOnlyList<DiagnosticEventDiagnostics> RecentEvents);

internal sealed record DiagnosticEventDiagnostics(
    [property: JsonPropertyName("timestamp")] DateTime Timestamp,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("detail")] string? Detail);
