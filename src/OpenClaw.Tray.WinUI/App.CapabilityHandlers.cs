using OpenClaw.Connection;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenClawTray;

public partial class App
{
    // MCP-only app capabilities — local testing/control, not exposed to gateway.
    private AppCapability? _appCapability;
    private AppConnectionCapability? _appConnectionCapability;

    private void WireAppCapabilityHandlers()
    {
        if (_nodeService == null) return;
        if (_appCapability != null) return; // already wired

        _appCapability = new AppCapability(new AppLogger());
        _nodeService.RegisterMcpOnlyCapability(_appCapability);
        _appConnectionCapability = new AppConnectionCapability(new AppLogger());
        _nodeService.RegisterMcpOnlyCapability(_appConnectionCapability);
        var app = _appCapability;
        var connection = _appConnectionCapability;

        app.NavigateHandler = async (page) =>
        {
            var tcs = new TaskCompletionSource<object?>();
            var queued = _dispatcherQueue?.TryEnqueue(() =>
            {
                try { ShowHub(page); tcs.SetResult(new { navigated = true, page }); }
                catch (Exception ex)
                {
                    Logger.Warn($"App: NavigationHandler ShowHub('{page}') failed: {ex.Message}");
                    tcs.SetResult(new { navigated = false, error = ex.Message });
                }
            }) ?? false;
            if (!queued) tcs.TrySetResult(new { navigated = false, error = "UI thread unavailable" });
            return await tcs.Task;
        };

        app.StatusHandler = () => new
        {
            connectionStatus = _appState!.Status.ToString(),
            nodeConnected = _nodeService?.IsConnected ?? false,
            nodePaired = _nodeService?.IsPaired ?? false,
            nodePendingApproval = _nodeService?.IsPendingApproval ?? false,
            gatewayVersion = _appState!.GatewaySelf?.ServerVersion,
            sessionCount = _appState!.Sessions?.Length ?? 0,
            nodeCount = _appState!.Nodes?.Length ?? 0,
            operatorScopes = _connectionManager?.OperatorClient?.GrantedOperatorScopes.ToArray() ?? Array.Empty<string>(),
            operatorDeviceId = _connectionManager?.CurrentSnapshot.OperatorDeviceId,
        };

        app.SessionsHandler = async (agentId) =>
        {
            var sessions = _appState!.Sessions ?? Array.Empty<SessionInfo>();
            if (!string.IsNullOrEmpty(agentId))
                sessions = sessions.Where(s => s.Key != null &&
                    s.Key.StartsWith($"agent:{agentId}:", StringComparison.OrdinalIgnoreCase)).ToArray();
            return sessions.Select(s => new { s.Key, s.Status, s.Model, s.AgeText, tokens = s.InputTokens + s.OutputTokens }).ToArray();
        };

        app.AgentsHandler = async () =>
        {
            if (_appState!.AgentsList.HasValue &&
                _appState!.AgentsList.Value.TryGetProperty("agents", out var agentsArr) &&
                agentsArr.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<object>(agentsArr.GetRawText());
            }
            return Array.Empty<object>();
        };

        app.NodesHandler = () =>
        {
            return _appState!.Nodes?.Select(n => new
            {
                n.DisplayName,
                n.NodeId,
                n.IsOnline,
                n.Platform,
                n.CapabilityCount,
                n.CommandCount,
                n.Capabilities,
                n.Commands,
                n.DisabledCommands,
                n.Permissions
            }).ToArray()
                ?? Array.Empty<object>();
        };

        app.ConfigGetHandler = async (path) =>
        {
            if (_appState?.Config == null) return new { error = "Config not loaded" };
            // Config is already redacted by the gateway's redactConfigSnapshot
            var raw = _appState.Config.Value;
            var config = raw.TryGetProperty("parsed", out var parsed) ? parsed
                : (raw.TryGetProperty("config", out var cfg) ? cfg : raw);
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var segment in path.Split('.'))
                {
                    if (config.TryGetProperty(segment, out var child)) config = child;
                    else return (object)new { error = $"Path not found: {path}" };
                }
            }
            return JsonSerializer.Deserialize<object>(config.GetRawText());
        };

        // Allowlist of safe settings (no secrets like Token, BootstrapToken, API keys)
        var safeSettings = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AutoStart", "GlobalHotkeyEnabled", "ShowNotifications", "NotificationSound",
            "NotifyHealth", "NotifyUrgent", "NotifyReminder", "NotifyEmail", "NotifyCalendar",
            "NotifyBuild", "NotifyStock", "NotifyInfo", "NotifyChatResponses",
            "EnableNodeMode", "EnableMcpServer", "PreferStructuredCategories",
            "NodeCanvasEnabled", "NodeScreenEnabled", "NodeCameraEnabled",
            "NodeLocationEnabled", "NodeBrowserProxyEnabled", "NodeTtsEnabled",
            "HasSeenActivityStreamTip", "TtsProvider"
        };

        app.SettingsGetHandler = (name) =>
        {
            if (_settings == null) return null;
            if (!safeSettings.Contains(name)) return new { error = $"Setting '{name}' is not accessible" };
            var prop = typeof(SettingsManager).GetProperty(name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            return prop?.GetValue(_settings);
        };

        app.SettingsSetHandler = (name, value) =>
        {
            if (_settings == null) return new { error = "Settings not loaded" };
            if (!safeSettings.Contains(name)) return new { error = $"Setting '{name}' is not accessible" };
            var prop = typeof(SettingsManager).GetProperty(name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            if (prop == null) return new { error = $"Unknown setting: {name}" };
            try
            {
                var converted = Convert.ChangeType(value, prop.PropertyType);
                prop.SetValue(_settings, converted);
                _settings.Save();
                return new { name, value = prop.GetValue(_settings) };
            }
            catch (Exception ex)
            {
                Logger.Warn($"App: SettingsHandler set '{name}' failed: {ex.Message}");
                return new { error = ex.Message };
            }
        };

        app.MenuHandler = () =>
        {
            var items = new List<object>
            {
                new { type = "status", status = _appState!.Status.ToString() },
                new { type = "sessions", count = _appState!.Sessions?.Length ?? 0 },
                new { type = "nodes", count = _appState!.Nodes?.Length ?? 0 },
            };
            return items;
        };

        app.SearchHandler = (query) =>
        {
            if (_hubWindow == null) return Array.Empty<object>();
            var commands = _hubWindow.BuildCommandList();
            var matches = commands
                .Where(c => c.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || (c.Subtitle?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .Take(10)
                .Select(c => new { c.Title, c.Subtitle, c.Icon })
                .ToArray();
            return matches;
        };

        app.DashboardUrlHandler = (path) =>
        {
            if (!TryResolveChatCredentials(out var gatewayUrl, out var token, out var credentialSource, out var isBootstrapToken))
                return new { error = "Gateway URL or credential is not configured" };

            var url = GatewayDashboardUrlBuilder.Build(
                gatewayUrl,
                path,
                token,
                !isBootstrapToken && credentialSource == CredentialResolver.SourceSharedGatewayToken);

            return new
            {
                url,
                credentialSource,
                usesSharedGatewayToken = !isBootstrapToken && credentialSource == CredentialResolver.SourceSharedGatewayToken,
                hasTokenQuery = url.Contains("?token=", StringComparison.Ordinal) || url.Contains("&token=", StringComparison.Ordinal)
            };
        };

        app.ChatSnapshotHandler = BuildChatSnapshotForMcpAsync;
        app.ChatSendHandler = SendChatMessageForMcpAsync;
        app.ChatResetHandler = ResetChatSessionForMcpAsync;

        connection.ApplySetupCodeHandler = async setupCode =>
        {
            if (_connectionManager == null)
                return new { outcome = "ConnectionFailed", error = "Connection manager is not initialized", connected = false };

            var result = await _connectionManager.ApplySetupCodeAsync(setupCode);
            return new
            {
                outcome = result.Outcome.ToString(),
                error = result.ErrorMessage,
                gatewayUrl = result.GatewayUrl,
                connected = result.Outcome == SetupCodeOutcome.Success
            };
        };

        connection.ConnectSharedTokenHandler = async (gatewayUrl, token) =>
        {
            if (_connectionManager == null)
                return new { outcome = "ConnectionFailed", error = "Connection manager is not initialized", connected = false };

            var result = await _connectionManager.ConnectWithSharedTokenAsync(gatewayUrl, token);
            return new
            {
                outcome = result.Outcome.ToString(),
                error = result.ErrorMessage,
                gatewayUrl = result.GatewayUrl,
                connected = result.Outcome == SetupCodeOutcome.Success
            };
        };

        connection.PendingApprovalsHandler = GetPendingApprovalsForMcpAsync;

        connection.ApproveDevicePairingHandler = async requestId =>
        {
            var client = GatewayClient;
            if (client == null || !client.IsConnectedToGateway)
                return new { succeeded = false, error = "Gateway client is not connected" };

            var approved = await client.DevicePairApproveAsync(requestId);
            if (approved)
                await WaitForAppStateUpdateAsync(nameof(AppState.DevicePairList), client.RequestDevicePairListAsync);

            return BuildPendingApprovalsPayload(
                connected: client.IsConnectedToGateway,
                decisionKind: "device",
                decisionAction: "approve",
                requestId: requestId,
                succeeded: approved,
                error: approved ? null : "Device pairing approval was rejected or unavailable");
        };

        connection.RejectDevicePairingHandler = async requestId =>
        {
            var client = GatewayClient;
            if (client == null || !client.IsConnectedToGateway)
                return new { succeeded = false, error = "Gateway client is not connected" };

            var rejected = await client.DevicePairRejectAsync(requestId);
            if (rejected)
                await WaitForAppStateUpdateAsync(nameof(AppState.DevicePairList), client.RequestDevicePairListAsync);

            return BuildPendingApprovalsPayload(
                connected: client.IsConnectedToGateway,
                decisionKind: "device",
                decisionAction: "reject",
                requestId: requestId,
                succeeded: rejected,
                error: rejected ? null : "Device pairing rejection was rejected or unavailable");
        };

        connection.ApproveNodePairingHandler = async requestId =>
        {
            var client = GatewayClient;
            if (client == null || !client.IsConnectedToGateway)
                return new { succeeded = false, error = "Gateway client is not connected" };

            var approved = await client.NodePairApproveAsync(requestId);
            if (approved)
                await WaitForAppStateUpdateAsync(nameof(AppState.NodePairList), client.RequestNodePairListAsync);

            return BuildPendingApprovalsPayload(
                connected: client.IsConnectedToGateway,
                decisionKind: "node",
                decisionAction: "approve",
                requestId: requestId,
                succeeded: approved,
                error: approved ? null : "Node pairing approval was rejected or unavailable");
        };

        connection.RejectNodePairingHandler = async requestId =>
        {
            var client = GatewayClient;
            if (client == null || !client.IsConnectedToGateway)
                return new { succeeded = false, error = "Gateway client is not connected" };

            var rejected = await client.NodePairRejectAsync(requestId);
            if (rejected)
                await WaitForAppStateUpdateAsync(nameof(AppState.NodePairList), client.RequestNodePairListAsync);

            return BuildPendingApprovalsPayload(
                connected: client.IsConnectedToGateway,
                decisionKind: "node",
                decisionAction: "reject",
                requestId: requestId,
                succeeded: rejected,
                error: rejected ? null : "Node pairing rejection was rejected or unavailable");
        };

        connection.ReconnectHandler = async () =>
        {
            if (_connectionManager == null)
                return new { reconnected = false, error = "Connection manager is not initialized" };

            await _connectionManager.ReconnectAsync();
            return new { reconnected = true };
        };

        connection.ReconnectNodeHandler = async () =>
        {
            if (_connectionManager == null)
                return new { reconnected = false, error = "Connection manager is not initialized" };

            await _connectionManager.ConnectNodeOnlyAsync();
            var client = GatewayClient;
            if (client?.IsConnectedToGateway == true)
                await WaitForAppStateUpdateAsync(nameof(AppState.Nodes), client.RequestNodesAsync);

            return new { reconnected = true };
        };
    }

    private async Task<object?> GetPendingApprovalsForMcpAsync()
    {
        var client = GatewayClient;
        if (client == null || !client.IsConnectedToGateway)
            return BuildPendingApprovalsPayload(connected: false, error: "Gateway client is not connected");

        await WaitForAppStateUpdateAsync(nameof(AppState.DevicePairList), client.RequestDevicePairListAsync);
        await WaitForAppStateUpdateAsync(nameof(AppState.NodePairList), client.RequestNodePairListAsync);

        return BuildPendingApprovalsPayload(connected: client.IsConnectedToGateway);
    }

    private object BuildPendingApprovalsPayload(
        bool connected,
        string? decisionKind = null,
        string? decisionAction = null,
        string? requestId = null,
        bool? succeeded = null,
        string? error = null)
    {
        var devicePending = _appState?.DevicePairList?.Pending
            .Select(req => (object)new Dictionary<string, object?>
            {
                ["requestId"] = req.RequestId,
                ["deviceId"] = req.DeviceId,
                ["displayName"] = req.DisplayName,
                ["platform"] = req.Platform,
                ["clientId"] = req.ClientId,
                ["clientMode"] = req.ClientMode,
                ["role"] = req.Role,
                ["scopes"] = req.Scopes,
                ["remoteIp"] = req.RemoteIp,
                ["isRepair"] = req.IsRepair
            })
            .ToArray() ?? Array.Empty<object>();

        var nodePending = _appState?.NodePairList?.Pending
            .Select(req => (object)new Dictionary<string, object?>
            {
                ["requestId"] = req.RequestId,
                ["nodeId"] = req.NodeId,
                ["displayName"] = req.DisplayName,
                ["platform"] = req.Platform,
                ["version"] = req.Version,
                ["remoteIp"] = req.RemoteIp,
                ["isRepair"] = req.IsRepair
            })
            .ToArray() ?? Array.Empty<object>();

        var payload = new Dictionary<string, object?>
        {
            ["connected"] = connected,
            ["error"] = error,
            ["totalPending"] = devicePending.Length + nodePending.Length,
            ["devicePending"] = devicePending,
            ["nodePending"] = nodePending
        };
        if (decisionKind != null)
        {
            payload["decision"] = new Dictionary<string, object?>
            {
                ["kind"] = decisionKind,
                ["action"] = decisionAction,
                ["requestId"] = requestId,
                ["succeeded"] = succeeded ?? false
            };
        }

        return payload;
    }

    private async Task<object?> BuildChatSnapshotForMcpAsync(string? threadId)
    {
        var provider = _chatCoordinator?.Provider;
        if (provider == null)
            return new { error = "Chat provider is not initialized" };

        var snapshot = await provider.LoadAsync();
        var resolvedThreadId = ResolveChatThreadId(snapshot, threadId);
        return BuildChatSnapshotPayload(snapshot, resolvedThreadId);
    }

    private async Task<object?> SendChatMessageForMcpAsync(string? threadId, string message)
    {
        var provider = _chatCoordinator?.Provider;
        if (provider == null)
            return new { sent = false, error = "Chat provider is not initialized" };

        var snapshot = await provider.LoadAsync();
        var resolvedThreadId = ResolveChatThreadId(snapshot, threadId);
        if (string.IsNullOrWhiteSpace(resolvedThreadId))
            return new { sent = false, error = "Chat compose target is not ready" };

        try
        {
            await provider.SendMessageAsync(resolvedThreadId, message);
        }
        catch (Exception ex)
        {
            Logger.Warn($"App: Chat send for '{resolvedThreadId}' failed: {ex.Message}");
            return new { sent = false, threadId = resolvedThreadId, error = ex.Message };
        }

        var updated = await provider.LoadAsync();
        var timeline = updated.Timelines.TryGetValue(resolvedThreadId, out var tl)
            ? tl
            : ChatTimelineState.Initial();

        return new
        {
            sent = true,
            threadId = resolvedThreadId,
            entryCount = timeline.Entries.Count,
            turnActive = timeline.TurnActive
        };
    }

    private async Task<object?> ResetChatSessionForMcpAsync(string? threadId)
    {
        var client = GatewayClient;
        if (client == null || !client.IsConnectedToGateway)
            return new { reset = false, error = "Gateway client is not connected" };

        var provider = _chatCoordinator?.Provider;
        if (provider == null)
            return new { reset = false, error = "Chat provider is not initialized" };

        var snapshot = await provider.LoadAsync();
        var resolvedThreadId = ResolveChatThreadId(snapshot, threadId);
        if (string.IsNullOrWhiteSpace(resolvedThreadId))
            return new { reset = false, error = "Chat compose target is not ready" };

        var reset = await client.ResetSessionAsync(resolvedThreadId);
        if (reset)
            await WaitForAppStateUpdateAsync(nameof(AppState.Sessions), () => client.RequestSessionsAsync());

        return new
        {
            reset,
            threadId = resolvedThreadId,
            error = reset ? null : "sessions.reset was rejected or unavailable"
        };
    }

    private static string? ResolveChatThreadId(ChatDataSnapshot snapshot, string? requestedThreadId)
    {
        if (!string.IsNullOrWhiteSpace(requestedThreadId))
            return requestedThreadId;

        if (snapshot.ComposeTarget.IsReady && !string.IsNullOrWhiteSpace(snapshot.ComposeTarget.SessionKey))
            return snapshot.ComposeTarget.SessionKey;

        return snapshot.DefaultThreadId;
    }

    private static object BuildChatSnapshotPayload(ChatDataSnapshot snapshot, string? resolvedThreadId)
    {
        var selectedTimeline = resolvedThreadId is not null
            && snapshot.Timelines.TryGetValue(resolvedThreadId, out var timeline)
                ? timeline
                : null;

        return new
        {
            connectionStatus = snapshot.ConnectionStatus,
            defaultThreadId = snapshot.DefaultThreadId,
            requestedThreadId = resolvedThreadId,
            composeTarget = new
            {
                sessionKey = snapshot.ComposeTarget.SessionKey,
                isReady = snapshot.ComposeTarget.IsReady
            },
            threads = snapshot.Threads.Select(t => new
            {
                t.Id,
                t.Title,
                status = t.Status.ToString(),
                activity = t.Activity.ToString(),
                t.Model,
                t.ModelProvider,
                t.ThinkingLevel,
                t.InputTokens,
                t.OutputTokens,
                t.TotalTokens,
                t.ContextTokens
            }).ToArray(),
            selectedTimeline = selectedTimeline is null ? null : new
            {
                turnActive = selectedTimeline.TurnActive,
                historyLoaded = selectedTimeline.HistoryLoaded,
                pendingPermission = selectedTimeline.PendingPermission is null ? null : new
                {
                    selectedTimeline.PendingPermission.RequestId,
                    selectedTimeline.PendingPermission.PermissionKind,
                    selectedTimeline.PendingPermission.ToolName,
                    selectedTimeline.PendingPermission.Detail,
                    selectedTimeline.PendingPermission.Actions
                },
                entries = selectedTimeline.Entries
                    .TakeLast(30)
                    .Select(e => new
                    {
                        e.Id,
                        kind = e.Kind.ToString(),
                        e.Text,
                        e.IsStreaming,
                        e.ToolName,
                        toolResult = e.ToolResult?.ToString(),
                        e.IntentSummary,
                        e.ToolCallId,
                        e.PermissionRequestId,
                        permissionDecision = e.PermissionDecision.ToString()
                    })
                    .ToArray()
            }
        };
    }

    private async Task WaitForAppStateUpdateAsync(string propertyName, Func<Task> requestAsync)
    {
        if (_appState == null)
        {
            await requestAsync();
            return;
        }

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (string.Equals(args.PropertyName, propertyName, StringComparison.Ordinal))
                tcs.TrySetResult(null);
        };

        _appState.PropertyChanged += handler;
        try
        {
            await requestAsync();
            await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            _appState.PropertyChanged -= handler;
        }
    }
}
