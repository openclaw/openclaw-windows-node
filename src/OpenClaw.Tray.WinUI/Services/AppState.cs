using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;
#if !OPENCLAW_TRAY_TESTS
using Microsoft.UI.Dispatching;
#endif

namespace OpenClawTray.Services;

/// <summary>
/// Single source of truth for all gateway-cached state. All property writes
/// must happen on the UI thread (enforced by <see cref="SetField{T}"/>).
/// Pages and HubWindow observe changes via <see cref="INotifyPropertyChanged"/>.
/// </summary>
internal sealed class AppState : INotifyPropertyChanged
{
#if !OPENCLAW_TRAY_TESTS
    private readonly DispatcherQueue? _dispatcher;
    public AppState(DispatcherQueue? dispatcher = null) => _dispatcher = dispatcher;
#else
    public AppState() { }
#endif

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
#if !OPENCLAW_TRAY_TESTS
        if (_dispatcher != null && !_dispatcher.HasThreadAccess)
        {
            throw new InvalidOperationException($"AppState.{name} must be written on UI thread");
        }
#endif
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    // ── Connection ──────────────────────────────────────────────────────

    private ConnectionStatus _status = ConnectionStatus.Disconnected;
    public ConnectionStatus Status { get => _status; set => SetField(ref _status, value); }

    private AgentActivity? _currentActivity;
    public AgentActivity? CurrentActivity { get => _currentActivity; set => SetField(ref _currentActivity, value); }

    private string? _authFailureMessage;
    public string? AuthFailureMessage { get => _authFailureMessage; set => SetField(ref _authFailureMessage, value); }

    // ── Channels / Sessions / Nodes ─────────────────────────────────────

    private ChannelHealth[] _channels = Array.Empty<ChannelHealth>();
    public ChannelHealth[] Channels { get => _channels; set => SetField(ref _channels, value); }

    /// <summary>
    /// Rich channels.status snapshot from the gateway (channelOrder, channelLabels,
    /// channelMeta, channelAccounts, per-channel typed status). Distinct from
    /// <see cref="Channels"/> which is the slim per-event health array pushed on
    /// the gateway's <c>ChannelHealthUpdated</c> events. <see cref="ChannelsSnapshot"/>
    /// is fetched on demand via <c>OpenClawGatewayClient.GetChannelsStatusAsync</c>.
    /// </summary>
    private ChannelsStatusSnapshot? _channelsSnapshot;
    public ChannelsStatusSnapshot? ChannelsSnapshot { get => _channelsSnapshot; set => SetField(ref _channelsSnapshot, value); }

    private SessionInfo[] _sessions = Array.Empty<SessionInfo>();
    public SessionInfo[] Sessions { get => _sessions; set => SetField(ref _sessions, value); }

    private GatewayNodeInfo[] _nodes = Array.Empty<GatewayNodeInfo>();
    public GatewayNodeInfo[] Nodes { get => _nodes; set => SetField(ref _nodes, value); }

    // ── Usage ───────────────────────────────────────────────────────────

    private GatewayUsageInfo? _usage;
    public GatewayUsageInfo? Usage { get => _usage; set => SetField(ref _usage, value); }

    private GatewayUsageStatusInfo? _usageStatus;
    public GatewayUsageStatusInfo? UsageStatus { get => _usageStatus; set => SetField(ref _usageStatus, value); }

    private GatewayCostUsageInfo? _usageCost;
    public GatewayCostUsageInfo? UsageCost { get => _usageCost; set => SetField(ref _usageCost, value); }

    // ── Gateway identity ────────────────────────────────────────────────

    private GatewaySelfInfo? _gatewaySelf;
    public GatewaySelfInfo? GatewaySelf { get => _gatewaySelf; set => SetField(ref _gatewaySelf, value); }

    // ── Pairing ─────────────────────────────────────────────────────────

    private PairingListInfo? _nodePairList;
    public PairingListInfo? NodePairList { get => _nodePairList; set => SetField(ref _nodePairList, value); }

    private DevicePairingListInfo? _devicePairList;
    public DevicePairingListInfo? DevicePairList { get => _devicePairList; set => SetField(ref _devicePairList, value); }

    // ── Models ──────────────────────────────────────────────────────────

    private ModelsListInfo? _modelsList;
    public ModelsListInfo? ModelsList { get => _modelsList; set => SetField(ref _modelsList, value); }

    // ── Presence ────────────────────────────────────────────────────────

    private PresenceEntry[]? _presence;
    public PresenceEntry[]? Presence { get => _presence; set => SetField(ref _presence, value); }

    // ── JSON-typed data ─────────────────────────────────────────────────

    private JsonElement? _agentsList;
    public JsonElement? AgentsList { get => _agentsList; set => SetField(ref _agentsList, value); }

    private JsonElement? _config;
    public JsonElement? Config { get => _config; set => SetField(ref _config, value); }

    private JsonElement? _configSchema;
    public JsonElement? ConfigSchema { get => _configSchema; set => SetField(ref _configSchema, value); }

    private JsonElement? _skillsData;
    public JsonElement? SkillsData { get => _skillsData; set => SetField(ref _skillsData, value); }

    private string? _skillsAgentId;
    public string? SkillsAgentId { get => _skillsAgentId; set => SetField(ref _skillsAgentId, value); }

    private JsonElement? _agentFilesList;
    public JsonElement? AgentFilesList { get => _agentFilesList; set => SetField(ref _agentFilesList, value); }

    private string? _agentFilesListAgentId;
    public string? AgentFilesListAgentId { get => _agentFilesListAgentId; set => SetField(ref _agentFilesListAgentId, value); }

    private JsonElement? _cronList;
    public JsonElement? CronList { get => _cronList; set => SetField(ref _cronList, value); }

    private JsonElement? _cronStatus;
    public JsonElement? CronStatus { get => _cronStatus; set => SetField(ref _cronStatus, value); }

    private JsonElement? _cronRuns;
    public JsonElement? CronRuns { get => _cronRuns; set => SetField(ref _cronRuns, value); }

    private JsonElement? _agentFileContent;
    public JsonElement? AgentFileContent { get => _agentFileContent; set => SetField(ref _agentFileContent, value); }

    // ── Update info ─────────────────────────────────────────────────────

    private UpdateCommandCenterInfo _updateInfo = new();
    public UpdateCommandCenterInfo UpdateInfo { get => _updateInfo; set => SetField(ref _updateInfo, value); }

    private DateTime _lastCheckTime = DateTime.Now;
    public DateTime LastCheckTime { get => _lastCheckTime; set => SetField(ref _lastCheckTime, value); }

    // ── Agent events (ring buffer, newest-first) ────────────────────────

    private readonly List<AgentEventInfo> _agentEvents = new();
    public IReadOnlyList<AgentEventInfo> AgentEvents => _agentEvents;

    /// <summary>Fires on UI thread when a new event is added.</summary>
    public event Action<AgentEventInfo>? AgentEventAdded;

    public void AddAgentEvent(AgentEventInfo evt)
    {
#if !OPENCLAW_TRAY_TESTS
        if (_dispatcher != null && !_dispatcher.HasThreadAccess)
        {
            throw new InvalidOperationException("AppState.AddAgentEvent must be called on UI thread");
        }
#endif
        _agentEvents.Insert(0, evt);
        if (_agentEvents.Count > MaxAgentEvents)
            _agentEvents.RemoveRange(MaxAgentEvents, _agentEvents.Count - MaxAgentEvents);
        AgentEventAdded?.Invoke(evt);
    }

    public void ClearAgentEvents()
    {
        _agentEvents.Clear();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AgentEvents)));
    }

    private const int MaxAgentEvents = 400;

    // ── Session previews (thread-safe, not observable) ───────────────────

    private readonly Dictionary<string, SessionPreviewInfo> _sessionPreviews = new();
    private readonly object _sessionPreviewsLock = new();

    public SessionPreviewInfo? GetSessionPreview(string key)
    {
        lock (_sessionPreviewsLock)
            return _sessionPreviews.TryGetValue(key, out var p) ? p : null;
    }

    public void SetSessionPreview(string key, SessionPreviewInfo preview)
    {
        lock (_sessionPreviewsLock)
            _sessionPreviews[key] = preview;
    }

    public void PruneSessionPreviews(HashSet<string> validKeys)
    {
        lock (_sessionPreviewsLock)
        {
            var stale = _sessionPreviews.Keys.Where(key => !validKeys.Contains(key)).ToArray();
            foreach (var key in stale)
                _sessionPreviews.Remove(key);
        }
    }

    // ── Computed helpers ────────────────────────────────────────────────

    /// <summary>Extract agent IDs from cached <see cref="AgentsList"/> JSON.</summary>
    public List<string> GetAgentIds()
    {
        var ids = new List<string>();
        if (_agentsList.HasValue &&
            _agentsList.Value.TryGetProperty("agents", out var agentsEl) &&
            agentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var agent in agentsEl.EnumerateArray())
            {
                var id = agent.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }
        }
        if (ids.Count == 0) ids.Add("main");
        return ids;
    }

    // ── Lifecycle ───────────────────────────────────────────────────────

    /// <summary>
    /// Resets ALL gateway data fields to defaults. Called on disconnect and client swap.
    /// Does NOT reset <see cref="Status"/> — that is managed by OnManagerStateChanged.
    /// Fires <see cref="PropertyChanged"/> for every property so observers refresh.
    /// </summary>
    public void ClearCachedData()
    {
        // Status is NOT reset — it is managed by OnManagerStateChanged.
        // AuthFailureMessage is NOT reset — it's set by OnAuthenticationFailed
        // and cleared explicitly on Connected (not on disconnect/error).
        CurrentActivity = null;
        Channels = Array.Empty<ChannelHealth>();
        ChannelsSnapshot = null;
        Sessions = Array.Empty<SessionInfo>();
        Nodes = Array.Empty<GatewayNodeInfo>();
        Usage = null;
        UsageStatus = null;
        UsageCost = null;
        GatewaySelf = null;
        NodePairList = null;
        DevicePairList = null;
        ModelsList = null;
        Presence = null;
        AgentsList = null;
        Config = null;
        ConfigSchema = null;
        SkillsData = null;
        SkillsAgentId = null;
        AgentFilesList = null;
        AgentFilesListAgentId = null;
        AgentFileContent = null;
        CronList = null;
        CronStatus = null;
        CronRuns = null;
        ClearAgentEvents();
        lock (_sessionPreviewsLock) _sessionPreviews.Clear();
    }
}
