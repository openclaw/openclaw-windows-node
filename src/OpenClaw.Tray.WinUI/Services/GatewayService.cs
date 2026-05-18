using Microsoft.UI.Dispatching;
using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace OpenClawTray.Services;

/// <summary>
/// Owns all 27 operator gateway client event subscriptions.
/// Handlers dispatch model updates to the UI thread via <see cref="EnqueueModelUpdate"/>,
/// writing to <see cref="AppState"/> which fires <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/>.
/// Four events are re-raised for App-level UI side effects (toasts, tray icon, health checks).
/// </summary>
internal sealed class GatewayService
{
    private readonly AppState _state;
    private readonly DispatcherQueue _dispatcher;

    // Re-raised events — App subscribes for UI side effects that don't belong in this service.
    public event EventHandler<ConnectionStatus>? ConnectionStatusChanged;
    public event EventHandler<string>? AuthenticationFailed;
    public event EventHandler<SessionCommandResult>? SessionCommandCompleted;
    public event EventHandler<OpenClawNotification>? NotificationReceived;

    // Throttle / dedup state (moved from App)
    private DateTime _lastPreviewRequestUtc = DateTime.MinValue;
    private DateTime _lastUsageActivityLogUtc = DateTime.MinValue;
    private string? _lastChannelStatusSignature;
    private readonly Dictionary<string, AgentActivity> _sessionActivities = new();
    private string? _displayedSessionKey;
    private DateTime _lastSessionSwitch = DateTime.MinValue;
    private static readonly TimeSpan SessionSwitchDebounce = TimeSpan.FromSeconds(3);

    // Client tracking for stale-event safety
    private IOperatorGatewayClient? _currentClient;
    private int _clientGeneration;

    public GatewayService(AppState state, DispatcherQueue dispatcher)
    {
        _state = state;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Swap client subscriptions. Unsubscribes from <paramref name="oldClient"/>,
    /// subscribes to <paramref name="newClient"/>. Either may be null (MCP-only / first connect).
    /// </summary>
    public void AttachClient(IOperatorGatewayClient? newClient, IOperatorGatewayClient? oldClient)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            if (!_dispatcher.TryEnqueue(() => AttachClient(newClient, oldClient)))
            {
                Logger.Warn("[GatewayService] Failed to dispatch operator client swap to UI thread");
            }
            return;
        }

        if (oldClient != null)
            UnsubscribeAll(oldClient);

        _clientGeneration++;
        _currentClient = newClient;

        // Clear service-level caches on actual client swap
        if (!ReferenceEquals(oldClient, newClient))
        {
            _lastChannelStatusSignature = null;
            _sessionActivities.Clear();
            _displayedSessionKey = null;
            _lastSessionSwitch = DateTime.MinValue;
        }

        if (newClient != null)
            SubscribeAll(newClient);
    }

    // ── Dispatcher helper ───────────────────────────────────────────────

    /// <summary>
    /// Dispatch a model update to the UI thread. Guards against stale events
    /// from a previous client by capturing and re-checking the client generation.
    /// </summary>
    private void EnqueueModelUpdate(Action update)
    {
        var gen = _clientGeneration;
        if (_dispatcher.HasThreadAccess)
        {
            if (gen == _clientGeneration) update();
        }
        else
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (gen == _clientGeneration) update();
            });
        }
    }

    // ── Subscribe / Unsubscribe ─────────────────────────────────────────

    private void SubscribeAll(IOperatorGatewayClient client)
    {
        client.StatusChanged += OnConnectionStatusChanged;
        client.AuthenticationFailed += OnAuthenticationFailed;
        client.ActivityChanged += OnActivityChanged;
        client.NotificationReceived += OnNotificationReceived;
        client.ChannelHealthUpdated += OnChannelHealthUpdated;
        client.SessionsUpdated += OnSessionsUpdated;
        client.UsageUpdated += OnUsageUpdated;
        client.UsageStatusUpdated += OnUsageStatusUpdated;
        client.UsageCostUpdated += OnUsageCostUpdated;
        client.NodesUpdated += OnNodesUpdated;
        client.SessionPreviewUpdated += OnSessionPreviewUpdated;
        client.SessionCommandCompleted += OnSessionCommandCompleted;
        client.GatewaySelfUpdated += OnGatewaySelfUpdated;
        client.CronListUpdated += OnCronListUpdated;
        client.CronStatusUpdated += OnCronStatusUpdated;
        client.CronRunsUpdated += OnCronRunsUpdated;
        client.ConfigUpdated += OnConfigUpdated;
        client.ConfigSchemaUpdated += OnConfigSchemaUpdated;
        client.SkillsStatusUpdated += OnSkillsStatusUpdated;
        client.AgentEventReceived += OnAgentEventReceived;
        client.NodePairListUpdated += OnNodePairListUpdated;
        client.DevicePairListUpdated += OnDevicePairListUpdated;
        client.ModelsListUpdated += OnModelsListUpdated;
        client.PresenceUpdated += OnPresenceUpdated;
        client.AgentsListUpdated += OnAgentsListUpdated;
        client.AgentFilesListUpdated += OnAgentFilesListUpdated;
        client.AgentFileContentUpdated += OnAgentFileContentUpdated;
    }

    private void UnsubscribeAll(IOperatorGatewayClient client)
    {
        client.StatusChanged -= OnConnectionStatusChanged;
        client.AuthenticationFailed -= OnAuthenticationFailed;
        client.ActivityChanged -= OnActivityChanged;
        client.NotificationReceived -= OnNotificationReceived;
        client.ChannelHealthUpdated -= OnChannelHealthUpdated;
        client.SessionsUpdated -= OnSessionsUpdated;
        client.UsageUpdated -= OnUsageUpdated;
        client.UsageStatusUpdated -= OnUsageStatusUpdated;
        client.UsageCostUpdated -= OnUsageCostUpdated;
        client.NodesUpdated -= OnNodesUpdated;
        client.SessionPreviewUpdated -= OnSessionPreviewUpdated;
        client.SessionCommandCompleted -= OnSessionCommandCompleted;
        client.GatewaySelfUpdated -= OnGatewaySelfUpdated;
        client.CronListUpdated -= OnCronListUpdated;
        client.CronStatusUpdated -= OnCronStatusUpdated;
        client.CronRunsUpdated -= OnCronRunsUpdated;
        client.ConfigUpdated -= OnConfigUpdated;
        client.ConfigSchemaUpdated -= OnConfigSchemaUpdated;
        client.SkillsStatusUpdated -= OnSkillsStatusUpdated;
        client.AgentEventReceived -= OnAgentEventReceived;
        client.NodePairListUpdated -= OnNodePairListUpdated;
        client.DevicePairListUpdated -= OnDevicePairListUpdated;
        client.ModelsListUpdated -= OnModelsListUpdated;
        client.PresenceUpdated -= OnPresenceUpdated;
        client.AgentsListUpdated -= OnAgentsListUpdated;
        client.AgentFilesListUpdated -= OnAgentFilesListUpdated;
        client.AgentFileContentUpdated -= OnAgentFileContentUpdated;
    }

    // ── Category C: Re-raised events (update AppState + re-raise for App) ──

    private void OnConnectionStatusChanged(object? sender, ConnectionStatus status)
    {
        if (sender != _currentClient) return;

        DiagnosticsJsonlService.Write("connection.status", new
        {
            status = status.ToString()
        });

        EnqueueModelUpdate(() =>
        {
            if (status == ConnectionStatus.Connected)
            {
                _state.AuthFailureMessage = null;
            }

            if (status == ConnectionStatus.Disconnected || status == ConnectionStatus.Error)
            {
                _state.ClearCachedData();
            }

            ConnectionStatusChanged?.Invoke(this, status);
        });
    }

    private void OnAuthenticationFailed(object? sender, string message)
    {
        if (sender != _currentClient) return;

        Logger.Error($"Authentication failed: {message}");
        DiagnosticsJsonlService.Write("connection.auth_failed", new { message });
        ActivityStreamService.Add(
            category: "error",
            title: $"Auth failed: {message}");

        EnqueueModelUpdate(() =>
        {
            _state.AuthFailureMessage = message;
            AuthenticationFailed?.Invoke(this, message);
        });
    }

    private void OnSessionCommandCompleted(object? sender, SessionCommandResult result)
    {
        if (sender != _currentClient) return;
        EnqueueModelUpdate(() => SessionCommandCompleted?.Invoke(this, result));
    }

    private void OnNotificationReceived(object? sender, OpenClawNotification notification)
    {
        if (sender != _currentClient) return;

        ActivityStreamService.Add(
            category: "notification",
            title: $"{notification.Type ?? "info"}: {notification.Title ?? "notification"}",
            details: notification.Message);

        EnqueueModelUpdate(() => NotificationReceived?.Invoke(this, notification));
    }

    // ── Category B: Complex data (state + side effects) ─────────────────

    private void OnActivityChanged(object? sender, AgentActivity? activity)
    {
        if (sender != _currentClient) return;

        EnqueueModelUpdate(() =>
        {
            if (activity == null)
            {
                if (_displayedSessionKey != null && _sessionActivities.ContainsKey(_displayedSessionKey))
                    _sessionActivities.Remove(_displayedSessionKey);
                _state.CurrentActivity = null;
            }
            else
            {
                var sessionKey = activity.SessionKey ?? "default";
                _sessionActivities[sessionKey] = activity;
                ActivityStreamService.Add(
                    category: "session",
                    title: $"{sessionKey}: {activity.Label}",
                    dashboardPath: $"sessions/{sessionKey}",
                    details: activity.Kind.ToString(),
                    sessionKey: sessionKey);

                var now = DateTime.Now;
                if (_displayedSessionKey != sessionKey &&
                    (now - _lastSessionSwitch) > SessionSwitchDebounce)
                {
                    _displayedSessionKey = sessionKey;
                    _lastSessionSwitch = now;
                }

                if (_displayedSessionKey == sessionKey)
                    _state.CurrentActivity = activity;
            }
        });
    }

    internal void OnChannelHealthUpdated(object? sender, ChannelHealth[] channels)
    {
        if (sender != _currentClient && sender is IOperatorGatewayClient) return;
        if (sender is not IOperatorGatewayClient && _state.Status != ConnectionStatus.Connected) return;

        var signature = string.Join("|", channels
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => $"{c.Name}:{c.Status}:{c.Error}"));

        if (!string.Equals(signature, _lastChannelStatusSignature, StringComparison.Ordinal))
        {
            _lastChannelStatusSignature = signature;
            var summary = channels.Length == 0
                ? "No channels reported"
                : string.Join(", ", channels.Select(c => $"{c.Name}={c.Status}"));
            DiagnosticsJsonlService.Write("gateway.health.channels", new
            {
                channelCount = channels.Length,
                healthyCount = channels.Count(c => ChannelHealth.IsHealthyStatus(c.Status)),
                errorCount = channels.Count(c => !string.IsNullOrWhiteSpace(c.Error))
            });
            ActivityStreamService.Add(
                category: "channel",
                title: "Channel health updated",
                dashboardPath: "channels",
                details: summary);
        }

        EnqueueModelUpdate(() =>
        {
            _state.LastCheckTime = DateTime.Now;
            _state.Channels = channels;
        });
    }

    private void OnSessionsUpdated(object? sender, SessionInfo[] sessions)
    {
        if (sender != _currentClient) return;

        var activeKeys = new HashSet<string>(sessions.Select(s => s.Key), StringComparer.Ordinal);

        // Throttled preview request (capture client ref for safety)
        var client = _currentClient;
        if (client != null &&
            sessions.Length > 0 &&
            DateTime.UtcNow - _lastPreviewRequestUtc > TimeSpan.FromSeconds(5))
        {
            _lastPreviewRequestUtc = DateTime.UtcNow;
            var keys = sessions.Take(5).Select(s => s.Key).ToArray();
            _ = client.RequestSessionPreviewAsync(keys, limit: 3, maxChars: 140);
        }

        EnqueueModelUpdate(() =>
        {
            _state.Sessions = sessions;
            _state.PruneSessionPreviews(activeKeys);
        });
    }

    private void OnUsageCostUpdated(object? sender, GatewayCostUsageInfo usageCost)
    {
        if (sender != _currentClient) return;

        if (DateTime.UtcNow - _lastUsageActivityLogUtc > TimeSpan.FromMinutes(1))
        {
            _lastUsageActivityLogUtc = DateTime.UtcNow;
            ActivityStreamService.Add(
                category: "usage",
                title: $"{usageCost.Days}d usage ${usageCost.Totals.TotalCost:F2}",
                dashboardPath: "usage",
                details: $"{usageCost.Totals.TotalTokens:N0} tokens");
        }

        EnqueueModelUpdate(() => _state.UsageCost = usageCost);
    }

    internal void OnGatewaySelfUpdated(object? sender, GatewaySelfInfo gatewaySelf)
    {
        if (sender != _currentClient && sender is IOperatorGatewayClient) return;
        if (sender is not IOperatorGatewayClient && _state.Status != ConnectionStatus.Connected) return;

        EnqueueModelUpdate(() =>
        {
            _state.GatewaySelf = _state.GatewaySelf?.Merge(gatewaySelf) ?? gatewaySelf;
            DiagnosticsJsonlService.Write("gateway.self", new
            {
                version = _state.GatewaySelf.ServerVersion,
                protocol = _state.GatewaySelf.Protocol,
                uptimeMs = _state.GatewaySelf.UptimeMs,
                authMode = _state.GatewaySelf.AuthMode,
                stateVersionPresence = _state.GatewaySelf.StateVersionPresence,
                stateVersionHealth = _state.GatewaySelf.StateVersionHealth,
                presenceCount = _state.GatewaySelf.PresenceCount
            });
        });
    }

    private void OnNodesUpdated(object? sender, GatewayNodeInfo[] nodes)
    {
        if (sender != _currentClient) return;

        EnqueueModelUpdate(() =>
        {
            var previousCount = _state.Nodes.Length;
            var previousOnline = _state.Nodes.Count(n => n.IsOnline);
            var online = nodes.Count(n => n.IsOnline);

            _state.Nodes = nodes;

            if (nodes.Length != previousCount || online != previousOnline)
            {
                ActivityStreamService.Add(
                    category: "node",
                    title: $"Nodes {online}/{nodes.Length} online",
                    dashboardPath: "nodes");
            }
        });
    }

    private void OnSessionPreviewUpdated(object? sender, SessionsPreviewPayloadInfo payload)
    {
        if (sender != _currentClient) return;

        foreach (var preview in payload.Previews)
            _state.SetSessionPreview(preview.Key, preview);
    }

    private void OnAgentEventReceived(object? sender, AgentEventInfo evt)
    {
        if (sender != _currentClient) return;
        EnqueueModelUpdate(() => _state.AddAgentEvent(evt));
    }

    // ── Category A: Simple data (just update AppState) ──────────────────

    private void OnUsageUpdated(object? sender, GatewayUsageInfo usage)
    {
        if (sender != _currentClient) return;
        EnqueueModelUpdate(() => _state.Usage = usage);
    }

    private void OnUsageStatusUpdated(object? sender, GatewayUsageStatusInfo usageStatus)
    {
        if (sender != _currentClient) return;
        EnqueueModelUpdate(() => _state.UsageStatus = usageStatus);
    }

    private void OnCronListUpdated(object? sender, JsonElement data)
    {
        if (sender != _currentClient) return;
        var cloned = data.Clone();
        EnqueueModelUpdate(() => _state.CronList = cloned);
    }

    private void OnCronStatusUpdated(object? sender, JsonElement data)
    {
        if (sender != _currentClient) return;
        var cloned = data.Clone();
        EnqueueModelUpdate(() => _state.CronStatus = cloned);
    }

    private void OnCronRunsUpdated(object? sender, JsonElement data)
    {
        if (sender != _currentClient) return;
        var cloned = data.Clone();
        EnqueueModelUpdate(() => _state.CronRuns = cloned);
    }

    private void OnSkillsStatusUpdated(object? sender, JsonElement data)
    {
        if (sender != _currentClient) return;
        var cloned = data.Clone();
        EnqueueModelUpdate(() => _state.SkillsData = cloned);
    }

    private void OnConfigUpdated(object? sender, JsonElement data)
    {
        if (sender != _currentClient) return;
        var cloned = data.Clone();
        EnqueueModelUpdate(() => _state.Config = cloned);
    }

    private void OnConfigSchemaUpdated(object? sender, JsonElement data)
    {
        if (sender != _currentClient) return;
        var cloned = data.Clone();
        EnqueueModelUpdate(() => _state.ConfigSchema = cloned);
    }

    private void OnAgentsListUpdated(object? sender, JsonElement data)
    {
        if (sender != _currentClient) return;
        var cloned = data.Clone();
        EnqueueModelUpdate(() => _state.AgentsList = cloned);
    }

    private void OnAgentFilesListUpdated(object? sender, JsonElement data)
    {
        if (sender != _currentClient) return;
        var cloned = data.Clone();
        EnqueueModelUpdate(() => _state.AgentFilesList = cloned);
    }

    private void OnAgentFileContentUpdated(object? sender, JsonElement data)
    {
        if (sender != _currentClient) return;
        var cloned = data.Clone();
        EnqueueModelUpdate(() => _state.AgentFileContent = cloned);
    }

    private void OnNodePairListUpdated(object? sender, PairingListInfo data)
    {
        if (sender != _currentClient) return;
        EnqueueModelUpdate(() => _state.NodePairList = data);
    }

    private void OnDevicePairListUpdated(object? sender, DevicePairingListInfo data)
    {
        if (sender != _currentClient) return;
        EnqueueModelUpdate(() => _state.DevicePairList = data);
    }

    private void OnModelsListUpdated(object? sender, ModelsListInfo data)
    {
        if (sender != _currentClient) return;
        EnqueueModelUpdate(() => _state.ModelsList = data);
    }

    private void OnPresenceUpdated(object? sender, PresenceEntry[] data)
    {
        if (sender != _currentClient) return;
        EnqueueModelUpdate(() => _state.Presence = data);
    }
}
