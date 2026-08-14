using OpenClaw.Connection;
using OpenClaw.Shared.ExecApprovals;
using OpenClawTray.Services;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace OpenClawTray.Presentation;

internal sealed class PermissionsPageViewModel : INavigationAware, IDisposable, INotifyPropertyChanged
{
    private readonly ISettingsStore _settingsStore;
    private readonly IExecApprovalsPresentationStore _execApprovalsStore;
    private readonly IAppCommands _appCommands;
    private readonly IUiDispatcher _dispatcher;
    private readonly IPermissionsPageRuntimeSource _runtimeSource;
    private readonly SettingsWriteOrigin _settingsOrigin;
    private readonly ExecApprovalsWriterOrigin _execApprovalsOrigin;
    private readonly SemaphoreSlim _execApprovalsMutationLock = new(1, 1);

    private bool _subscribed;
    private bool _loadingSettings;
    private bool _disposed;
    private string? _execApprovalsBaseHash;
    private long _execApprovalsRefreshGeneration;
    private long _lastExecApprovalsChangeSequence;
    private long _lastAppliedSettingsVersion = -1;

    private bool _nodeModeEnabled;
    private bool _mcpEnabled;
    private bool _nodeSystemRunEnabled;
    private bool _nodeBrowserProxyEnabled;
    private bool _nodeCameraEnabled;
    private bool _nodeCanvasEnabled;
    private bool _nodeScreenEnabled;
    private bool _nodeLocationEnabled;
    private bool _nodeTtsEnabled;
    private bool _nodeSttEnabled;
    private IReadOnlyList<PermissionsCapabilityState> _capabilities = Array.Empty<PermissionsCapabilityState>();
    private bool _areFeaturesEnabled;
    private string _featuresDescriptionResourceKey = "PermissionsPage_FeaturesDescription_Disabled";
    private PermissionsNodeStatusKind _nodeStatusKind = PermissionsNodeStatusKind.Disabled;
    private string _nodeStatusResourceKey = "PermissionsPage_NodeStatus_Disabled";
    private string? _nodeDetailsResourceKey = "PermissionsPage_NodeStatus_DisabledDetails";
    private string? _nodeDetailsErrorText;
    private int _mcpServedCapabilityCount;
    private IReadOnlyList<string> _localNodeCapabilities = Array.Empty<string>();
    private PermissionsMcpTokenState _mcpTokenState;
    private string _mcpEndpoint = "http://127.0.0.1:8765/mcp";
    private string _mcpStatusResourceKey = "PermissionsPage_McpStatus_TokenPending";
    private string? _mcpStatusErrorText;
    private bool _voiceSettingsVisible;
    private PermissionsVoiceSetupRequirement _voiceSetupRequirement;
    private string? _voiceSetupHelpResourceKey;
    private IReadOnlyList<string> _allowCommands = Array.Empty<string>();
    private PermissionsGatewayAllowlistState _gatewayAllowlistState;
    private string _defaultExecActionTag = "deny";
    private IReadOnlyList<PermissionsExecApprovalRule> _execApprovalRules = Array.Empty<PermissionsExecApprovalRule>();
    private ExecApprovalsSnapshotFailure? _execApprovalsFailure;
    private PermissionsExecApprovalsStatus _execApprovalsStatus;
    private long _execApprovalsStatusVersion;

    public PermissionsPageViewModel(
        ISettingsStore settingsStore,
        IExecApprovalsPresentationStore execApprovalsStore,
        IAppCommands appCommands,
        IUiDispatcher dispatcher,
        IPermissionsPageRuntimeSource runtimeSource)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _execApprovalsStore = execApprovalsStore ?? throw new ArgumentNullException(nameof(execApprovalsStore));
        _appCommands = appCommands ?? throw new ArgumentNullException(nameof(appCommands));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runtimeSource = runtimeSource ?? throw new ArgumentNullException(nameof(runtimeSource));
        _settingsOrigin = _settingsStore.CreateOrigin();
        _execApprovalsOrigin = _execApprovalsStore.CreateWriterOrigin();
        RecomputePresentation(_runtimeSource.Current);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ExternalChanged;

    internal ISettingsStore SettingsStore => _settingsStore;
    internal IExecApprovalsPresentationStore ExecApprovalsStore => _execApprovalsStore;
    internal IAppCommands AppCommands => _appCommands;
    internal IUiDispatcher Dispatcher => _dispatcher;
    internal IPermissionsPageRuntimeSource RuntimeSource => _runtimeSource;
    internal bool IsActive { get; private set; }
    internal bool IsDisposed => _disposed;

    public bool NodeModeEnabled
    {
        get => _nodeModeEnabled;
        set
        {
            if (SetField(ref _nodeModeEnabled, value) && !_loadingSettings)
            {
                PersistSetting(edit => edit.EnableNodeMode = value);
            }
        }
    }

    public bool McpEnabled
    {
        get => _mcpEnabled;
        set
        {
            if (SetField(ref _mcpEnabled, value) && !_loadingSettings)
            {
                PersistSetting(edit => edit.EnableMcpServer = value);
            }
        }
    }

    public IReadOnlyList<PermissionsCapabilityState> Capabilities => _capabilities;
    public bool AreFeaturesEnabled => _areFeaturesEnabled;
    public string FeaturesDescriptionResourceKey => _featuresDescriptionResourceKey;
    public PermissionsNodeStatusKind NodeStatusKind => _nodeStatusKind;
    public string NodeStatusResourceKey => _nodeStatusResourceKey;
    public string? NodeDetailsResourceKey => _nodeDetailsResourceKey;
    public string? NodeDetailsErrorText => _nodeDetailsErrorText;
    public int McpServedCapabilityCount => _mcpServedCapabilityCount;
    public IReadOnlyList<string> LocalNodeCapabilities => _localNodeCapabilities;
    public int LocalNodeCapabilityCount => _localNodeCapabilities.Count;
    public PermissionsMcpTokenState McpTokenState => _mcpTokenState;
    public string McpEndpoint => _mcpEndpoint;
    public string McpStatusResourceKey => _mcpStatusResourceKey;
    public string? McpStatusErrorText => _mcpStatusErrorText;
    public bool VoiceSettingsVisible => _voiceSettingsVisible;
    public PermissionsVoiceSetupRequirement VoiceSetupRequirement => _voiceSetupRequirement;
    public string? VoiceSetupHelpResourceKey => _voiceSetupHelpResourceKey;
    public IReadOnlyList<string> AllowCommands => _allowCommands;
    public PermissionsGatewayAllowlistState GatewayAllowlistState => _gatewayAllowlistState;

    public string DefaultExecActionTag => _defaultExecActionTag;

    public IReadOnlyList<PermissionsExecApprovalRule> ExecApprovalRules => _execApprovalRules;
    public ExecApprovalsSnapshotFailure? ExecApprovalsFailure => _execApprovalsFailure;
    public PermissionsExecApprovalsStatus ExecApprovalsStatus => _execApprovalsStatus;
    public long ExecApprovalsStatusVersion => _execApprovalsStatusVersion;

    public void Activate(object? parameter)
    {
        ThrowIfDisposed();
        IsActive = true;
        if (!_subscribed)
        {
            _settingsStore.Changed += OnSettingsChanged;
            _execApprovalsStore.Changed += OnExecApprovalsChanged;
            _runtimeSource.Changed += OnRuntimeChanged;
            _subscribed = true;
        }

        LoadCurrentSettings();
        var refreshGeneration = Interlocked.Increment(ref _execApprovalsRefreshGeneration);
        _ = RefreshExecApprovalsAsync(refreshGeneration);
        RecomputePresentation(_runtimeSource.Current);
    }

    public void Deactivate()
    {
        Interlocked.Increment(ref _execApprovalsRefreshGeneration);
        if (_subscribed)
        {
            _settingsStore.Changed -= OnSettingsChanged;
            _execApprovalsStore.Changed -= OnExecApprovalsChanged;
            _runtimeSource.Changed -= OnRuntimeChanged;
            _subscribed = false;
        }

        IsActive = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Deactivate();
        _disposed = true;
    }

    public void SetCapabilityEnabled(PermissionsCapabilityKey key, bool value)
    {
        switch (key)
        {
            case PermissionsCapabilityKey.SystemRun:
                PersistCapabilitySetting(ref _nodeSystemRunEnabled, value, edit => edit.NodeSystemRunEnabled = value);
                break;
            case PermissionsCapabilityKey.BrowserProxy:
                PersistCapabilitySetting(ref _nodeBrowserProxyEnabled, value, edit => edit.NodeBrowserProxyEnabled = value);
                break;
            case PermissionsCapabilityKey.Camera:
                PersistCapabilitySetting(ref _nodeCameraEnabled, value, edit => edit.NodeCameraEnabled = value);
                break;
            case PermissionsCapabilityKey.Canvas:
                PersistCapabilitySetting(ref _nodeCanvasEnabled, value, edit => edit.NodeCanvasEnabled = value);
                break;
            case PermissionsCapabilityKey.Screen:
                PersistCapabilitySetting(ref _nodeScreenEnabled, value, edit => edit.NodeScreenEnabled = value);
                break;
            case PermissionsCapabilityKey.Location:
                PersistCapabilitySetting(ref _nodeLocationEnabled, value, edit => edit.NodeLocationEnabled = value);
                break;
            case PermissionsCapabilityKey.TextToSpeech:
                PersistCapabilitySetting(ref _nodeTtsEnabled, value, edit => edit.NodeTtsEnabled = value);
                break;
            case PermissionsCapabilityKey.SpeechToText:
                PersistCapabilitySetting(ref _nodeSttEnabled, value, edit => edit.NodeSttEnabled = value);
                break;
        }
    }

    public Task<bool> SetDefaultExecActionAsync(string? action)
    {
        var normalized = NormalizeAction(action);
        return string.Equals(_defaultExecActionTag, normalized, StringComparison.Ordinal)
            ? Task.FromResult(true)
            : SaveExecApprovalsAsync(new ExecApprovalsMutation(ExecApprovalsMutationKind.DefaultAction, normalized, null));
    }

    public Task<bool> TryAddExecApprovalRuleAsync(string pattern)
    {
        var trimmed = pattern?.Trim();
        if (!OpenClaw.Shared.ExecApprovals.ExecApprovalsStore.IsValidAllowlistPattern(trimmed))
        {
            return Task.FromResult(false);
        }

        return SaveExecApprovalsAsync(new ExecApprovalsMutation(ExecApprovalsMutationKind.AddRule, null, trimmed));
    }

    public Task<bool> RemoveExecApprovalRuleAsync(PermissionsExecApprovalRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return SaveExecApprovalsAsync(new ExecApprovalsMutation(
            ExecApprovalsMutationKind.RemoveRule,
            null,
            rule.Pattern,
            rule.Id,
            rule.ArgPattern));
    }

    private void PersistSetting(Action<ISettingsEditor> edit)
    {
        _settingsStore.Update(_settingsOrigin, edit);
        _appCommands.NotifySettingsSaved();
        RecomputePresentation(_runtimeSource.Current);
    }

    private void PersistCapabilitySetting(
        ref bool field,
        bool value,
        Action<ISettingsEditor> edit)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        if (!_loadingSettings)
        {
            PersistSetting(edit);
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (!IsActive || !TryAdvanceSettingsVersion(e.Version))
        {
            return;
        }

        var isOwnChange = ReferenceEquals(e.Origin, _settingsOrigin);
        HandleOnUiThread(() =>
        {
            if (!IsActive)
            {
                return;
            }

            if (e.Version != Volatile.Read(ref _lastAppliedSettingsVersion))
            {
                return;
            }

            LoadSettings(e.Snapshot);
            if (e.Version != Volatile.Read(ref _lastAppliedSettingsVersion))
            {
                return;
            }

            if (isOwnChange)
            {
                return;
            }

            RecomputePresentation(_runtimeSource.Current);
            ExternalChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnRuntimeChanged(object? sender, PermissionsRuntimeSourceChangedEventArgs e)
    {
        HandleOnUiThread(() =>
        {
            if (!IsActive)
            {
                return;
            }

            RecomputePresentation(e.Snapshot);
        });
    }

    private void OnExecApprovalsChanged(object? sender, ExecApprovalsChangedEventArgs e)
    {
        if (!TryAcceptExecApprovalsChangeSequence(e.Sequence))
        {
            return;
        }

        if (ReferenceEquals(e.Origin, _execApprovalsOrigin))
        {
            return;
        }

        Interlocked.Increment(ref _execApprovalsRefreshGeneration);
        HandleOnUiThread(() =>
        {
            if (!IsActive
                || e.Sequence != Volatile.Read(ref _lastExecApprovalsChangeSequence))
            {
                return;
            }

            if (e.Snapshot is not null)
            {
                ApplyExecSnapshot(e.Snapshot);
                SetExecApprovalsFailure(null, PermissionsExecApprovalsStatus.None, bumpVersion: false);
                return;
            }

            if (e.LastValidSnapshot is not null)
            {
                ApplyExecSnapshot(e.LastValidSnapshot);
            }

            SetExecApprovalsFailure(e.Failure, PermissionsExecApprovalsStatus.ExternalInvalid, bumpVersion: true);
        });
    }

    private bool TryAcceptExecApprovalsChangeSequence(long sequence)
    {
        while (true)
        {
            var current = Volatile.Read(ref _lastExecApprovalsChangeSequence);
            if (sequence <= current)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _lastExecApprovalsChangeSequence, sequence, current) == current)
            {
                return true;
            }
        }
    }

    private void LoadCurrentSettings()
    {
        var snapshot = _settingsStore.Current;
        TryAdvanceSettingsVersion(snapshot.Version);
        if (snapshot.Version == Volatile.Read(ref _lastAppliedSettingsVersion))
        {
            LoadSettings(snapshot);
        }
    }

    private bool TryAdvanceSettingsVersion(long version)
    {
        while (true)
        {
            var current = Volatile.Read(ref _lastAppliedSettingsVersion);
            if (version <= current)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _lastAppliedSettingsVersion, version, current) == current)
            {
                return true;
            }
        }
    }

    private void LoadSettings(SettingsSnapshot snapshot)
    {
        _loadingSettings = true;
        try
        {
            SetField(ref _nodeModeEnabled, snapshot.EnableNodeMode, nameof(NodeModeEnabled));
            SetField(ref _mcpEnabled, snapshot.EnableMcpServer, nameof(McpEnabled));
            _nodeSystemRunEnabled = snapshot.NodeSystemRunEnabled;
            _nodeBrowserProxyEnabled = snapshot.NodeBrowserProxyEnabled;
            _nodeCameraEnabled = snapshot.NodeCameraEnabled;
            _nodeCanvasEnabled = snapshot.NodeCanvasEnabled;
            _nodeScreenEnabled = snapshot.NodeScreenEnabled;
            _nodeLocationEnabled = snapshot.NodeLocationEnabled;
            _nodeTtsEnabled = snapshot.NodeTtsEnabled;
            _nodeSttEnabled = snapshot.NodeSttEnabled;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void RecomputePresentation(PermissionsRuntimeSourceSnapshot runtime)
    {
        var featuresEnabled = _nodeModeEnabled || _mcpEnabled;
        SetField(ref _areFeaturesEnabled, featuresEnabled, nameof(AreFeaturesEnabled));
        SetField(
            ref _featuresDescriptionResourceKey,
            featuresEnabled ? "PermissionsPage_FeaturesDescription_Enabled" : "PermissionsPage_FeaturesDescription_Disabled",
            nameof(FeaturesDescriptionResourceKey));
        SetField(ref _capabilities, BuildCapabilities(featuresEnabled), nameof(Capabilities));
        SetField(ref _allowCommands, runtime.GatewayAllowCommands, nameof(AllowCommands));
        SetField(ref _gatewayAllowlistState, runtime.GatewayAllowlistState, nameof(GatewayAllowlistState));
        SetField(ref _mcpEndpoint, runtime.McpEndpoint, nameof(McpEndpoint));
        SetField(ref _mcpServedCapabilityCount, runtime.McpServedCapabilityCount, nameof(McpServedCapabilityCount));
        SetField(ref _localNodeCapabilities, runtime.LocalNodeCapabilities, nameof(LocalNodeCapabilities));
        OnPropertyChanged(nameof(LocalNodeCapabilityCount));

        var mcpTokenState = !_mcpEnabled
            ? PermissionsMcpTokenState.None
            : runtime.IsMcpTokenReady
                ? PermissionsMcpTokenState.Ready
                : PermissionsMcpTokenState.Pending;
        SetField(ref _mcpTokenState, mcpTokenState, nameof(McpTokenState));
        SetField(
            ref _mcpStatusResourceKey,
            runtime.IsMcpTokenReady ? "PermissionsPage_McpStatus_TokenReady" : "PermissionsPage_McpStatus_TokenPending",
            nameof(McpStatusResourceKey));
        SetField(ref _mcpStatusErrorText, runtime.McpStartupError, nameof(McpStatusErrorText));

        var voiceVisible = _nodeSttEnabled || _nodeTtsEnabled;
        SetField(ref _voiceSettingsVisible, voiceVisible, nameof(VoiceSettingsVisible));
        var voiceRequirement = runtime.VoiceSetupRequirement;
        SetField(ref _voiceSetupRequirement, voiceRequirement, nameof(VoiceSetupRequirement));
        SetField(ref _voiceSetupHelpResourceKey, ToVoiceHelpResourceKey(voiceRequirement), nameof(VoiceSetupHelpResourceKey));

        ApplyNodeStatus(runtime);
    }

    private IReadOnlyList<PermissionsCapabilityState> BuildCapabilities(bool featuresEnabled) =>
        new[]
        {
            new PermissionsCapabilityState(PermissionsCapabilityKey.SystemRun, _nodeSystemRunEnabled, featuresEnabled),
            new PermissionsCapabilityState(PermissionsCapabilityKey.BrowserProxy, _nodeBrowserProxyEnabled, featuresEnabled && _nodeModeEnabled),
            new PermissionsCapabilityState(PermissionsCapabilityKey.Camera, _nodeCameraEnabled, featuresEnabled),
            new PermissionsCapabilityState(PermissionsCapabilityKey.Canvas, _nodeCanvasEnabled, featuresEnabled),
            new PermissionsCapabilityState(PermissionsCapabilityKey.Screen, _nodeScreenEnabled, featuresEnabled),
            new PermissionsCapabilityState(PermissionsCapabilityKey.Location, _nodeLocationEnabled, featuresEnabled),
            new PermissionsCapabilityState(PermissionsCapabilityKey.TextToSpeech, _nodeTtsEnabled, featuresEnabled),
            new PermissionsCapabilityState(PermissionsCapabilityKey.SpeechToText, _nodeSttEnabled, featuresEnabled),
        };

    private void ApplyNodeStatus(PermissionsRuntimeSourceSnapshot runtime)
    {
        var snapshot = runtime.ConnectionSnapshot;
        var nodeState = snapshot.NodeState;
        var operatorConnected = snapshot.OperatorState == RoleConnectionState.Connected;
        var mcpError = runtime.McpStartupError;

        if (!_nodeModeEnabled)
        {
            if (_mcpEnabled)
            {
                if (!string.IsNullOrWhiteSpace(mcpError))
                {
                    SetNodeStatus(PermissionsNodeStatusKind.McpError, "PermissionsPage_NodeStatus_McpError", detailsKey: null, errorText: mcpError);
                }
                else
                {
                    SetNodeStatus(PermissionsNodeStatusKind.McpOnly, "PermissionsPage_NodeStatus_McpOnly", "PermissionsPage_NodeStatus_McpOnlyDetailsFormat", errorText: null);
                }
            }
            else
            {
                SetNodeStatus(PermissionsNodeStatusKind.Disabled, "PermissionsPage_NodeStatus_Disabled", "PermissionsPage_NodeStatus_DisabledDetails", errorText: null);
            }

            return;
        }

        if (_mcpEnabled && !string.IsNullOrWhiteSpace(mcpError))
        {
            SetNodeStatus(PermissionsNodeStatusKind.McpError, "PermissionsPage_NodeStatus_McpError", detailsKey: null, errorText: mcpError);
        }
        else if (nodeState == RoleConnectionState.Connected && operatorConnected)
        {
            SetNodeStatus(
                PermissionsNodeStatusKind.Active,
                "PermissionsPage_NodeStatus_Active",
                runtime.LocalNodeCapabilities.Count > 0
                    ? "PermissionsPage_NodeStatus_ActiveDetailsFormat"
                    : "PermissionsPage_NodeStatus_NoCapabilities",
                errorText: null);
        }
        else if (nodeState == RoleConnectionState.Connecting)
        {
            SetNodeStatus(PermissionsNodeStatusKind.Starting, "PermissionsPage_NodeStatus_Starting", "PermissionsPage_NodeStatus_NotConnectedDetails", errorText: null);
        }
        else
        {
            SetNodeStatus(
                PermissionsNodeStatusKind.NotConnected,
                "PermissionsPage_NodeStatus_NotConnected",
                _mcpEnabled && string.IsNullOrWhiteSpace(mcpError)
                    ? "PermissionsPage_NodeStatus_McpOnlyDetailsFormat"
                    : "PermissionsPage_NodeStatus_NotConnectedDetails",
                errorText: null);
        }
    }

    private void SetNodeStatus(PermissionsNodeStatusKind kind, string statusKey, string? detailsKey, string? errorText)
    {
        SetField(ref _nodeStatusKind, kind, nameof(NodeStatusKind));
        SetField(ref _nodeStatusResourceKey, statusKey, nameof(NodeStatusResourceKey));
        SetField(ref _nodeDetailsResourceKey, detailsKey, nameof(NodeDetailsResourceKey));
        SetField(ref _nodeDetailsErrorText, errorText, nameof(NodeDetailsErrorText));
    }

    private async Task RefreshExecApprovalsAsync(long refreshGeneration)
    {
        var result = await _execApprovalsStore.GetSnapshotReadOnlyAsync();
        HandleOnUiThread(() =>
        {
            if (IsActive
                && refreshGeneration == Volatile.Read(ref _execApprovalsRefreshGeneration))
            {
                LoadExecApprovals(result);
            }
        });
    }

    private async Task<bool> SaveExecApprovalsAsync(ExecApprovalsMutation mutation)
    {
        await _execApprovalsMutationLock.WaitAsync();
        try
        {
            var mutationGeneration = Interlocked.Increment(ref _execApprovalsRefreshGeneration);
            try
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var snapshotResult = await _execApprovalsStore.GetSnapshotReadOnlyAsync();
                    var currentSnapshot = snapshotResult.Snapshot ?? snapshotResult.LastValidSnapshot;
                    var baseHash = attempt == 0 && !string.IsNullOrWhiteSpace(_execApprovalsBaseHash)
                        ? _execApprovalsBaseHash!
                        : currentSnapshot?.Hash ?? snapshotResult.Failure?.Hash;
                    if (string.IsNullOrWhiteSpace(baseHash))
                    {
                        break;
                    }

                    var workingFile = CloneFile(currentSnapshot?.File ?? new ExecApprovalsFile());
                    ApplyMutation(workingFile, mutation);
                    var updated = await _execApprovalsStore.ReplaceAsync(baseHash, workingFile, _execApprovalsOrigin);
                    if (updated is null)
                    {
                        _execApprovalsBaseHash = currentSnapshot?.Hash ?? baseHash;
                        continue;
                    }

                    HandleOnUiThread(() => CompleteExecApprovalsMutation(updated, mutationGeneration));
                    return true;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            SetExecApprovalsFailure(_execApprovalsFailure, PermissionsExecApprovalsStatus.SaveFailed, bumpVersion: true);
            return false;
        }
        finally
        {
            _execApprovalsMutationLock.Release();
        }
    }

    private void CompleteExecApprovalsMutation(ExecApprovalsSnapshot updated, long mutationGeneration)
    {
        if (!IsActive)
        {
            return;
        }

        if (mutationGeneration != Volatile.Read(ref _execApprovalsRefreshGeneration))
        {
            var refreshGeneration = Interlocked.Increment(ref _execApprovalsRefreshGeneration);
            _ = RefreshExecApprovalsAsync(refreshGeneration);
            return;
        }

        ApplyExecSnapshot(updated);
        SetExecApprovalsFailure(null, PermissionsExecApprovalsStatus.Saved, bumpVersion: true);
    }

    private void ApplyMutation(ExecApprovalsFile file, ExecApprovalsMutation mutation)
    {
        file.Version = 1;
        file.Defaults ??= new ExecApprovalsDefaults();
        file.Agents ??= new Dictionary<string, ExecApprovalsAgent>(StringComparer.Ordinal);
        if (!file.Agents.TryGetValue("main", out var main) || main is null)
        {
            main = new ExecApprovalsAgent();
            file.Agents["main"] = main;
        }

        if (mutation.Kind == ExecApprovalsMutationKind.DefaultAction)
        {
            var (security, ask) = mutation.Action switch
            {
                "allow" => (ExecSecurity.Full, ExecAsk.Off),
                "prompt" => (ExecSecurity.Allowlist, ExecAsk.OnMiss),
                _ => (ExecSecurity.Allowlist, ExecAsk.Off),
            };

            file.Defaults.Security = security;
            file.Defaults.Ask = ask;
            file.Defaults.AskFallback = ExecSecurity.Deny;
            file.Defaults.AutoAllowSkills ??= false;
            main.Security = security;
            main.Ask = ask;
            main.AskFallback = ExecSecurity.Deny;
            return;
        }

        var allowlist = main.Allowlist ??= new List<ExecAllowlistEntry>();
        if (mutation.Kind == ExecApprovalsMutationKind.AddRule)
        {
            if ((main.Security ?? file.Defaults.Security ?? ExecSecurity.Deny) == ExecSecurity.Deny)
            {
                main.Security = ExecSecurity.Allowlist;
                main.Ask = ExecAsk.Off;
                main.AskFallback = ExecSecurity.Deny;
            }

            if (!allowlist.Any(entry =>
                    string.Equals(entry.Pattern?.Trim(), mutation.Pattern, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.ArgPattern, mutation.ArgPattern, StringComparison.Ordinal)))
            {
                allowlist.Add(new ExecAllowlistEntry
                {
                    Id = Guid.NewGuid(),
                    Pattern = mutation.Pattern,
                    ArgPattern = mutation.ArgPattern,
                });
            }

            return;
        }

        allowlist.RemoveAll(entry =>
            mutation.RuleId.HasValue
                ? entry.Id == mutation.RuleId
                : entry.Id is null
                    && string.Equals(entry.Pattern?.Trim(), mutation.Pattern, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.ArgPattern, mutation.ArgPattern, StringComparison.Ordinal));
    }

    private void LoadExecApprovals(ExecApprovalsReadOnlySnapshotResult result)
    {
        if (result.Snapshot is not null)
        {
            ApplyExecSnapshot(result.Snapshot);
            SetExecApprovalsFailure(null, PermissionsExecApprovalsStatus.None, bumpVersion: false);
        }
        else if (result.LastValidSnapshot is not null)
        {
            ApplyExecSnapshot(result.LastValidSnapshot);
            SetExecApprovalsFailure(result.Failure, PermissionsExecApprovalsStatus.ExternalInvalid, bumpVersion: false);
        }
        else
        {
            _execApprovalsBaseHash = result.Failure?.Hash;
            SetField(ref _defaultExecActionTag, "deny", nameof(DefaultExecActionTag));
            SetField(ref _execApprovalRules, Array.Empty<PermissionsExecApprovalRule>(), nameof(ExecApprovalRules));
            SetExecApprovalsFailure(result.Failure, PermissionsExecApprovalsStatus.None, bumpVersion: false);
        }
    }

    private void ApplyExecSnapshot(ExecApprovalsSnapshot snapshot)
    {
        _execApprovalsBaseHash = snapshot.Hash;
        SetField(ref _defaultExecActionTag, MapDefaultAction(snapshot.File), nameof(DefaultExecActionTag));
        var rules = ((IEnumerable<ExecAllowlistEntry>)(snapshot.File.Agents is not null
                && snapshot.File.Agents.TryGetValue("main", out var main)
                && main?.Allowlist is not null
                ? main.Allowlist
                : Array.Empty<ExecAllowlistEntry>()))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Pattern))
            .Select(entry => new PermissionsExecApprovalRule(
                entry.Id,
                entry.Pattern!,
                entry.ArgPattern,
                entry.LastUsedAt,
                entry.LastResolvedPath))
            .ToArray();
        SetField(ref _execApprovalRules, rules, nameof(ExecApprovalRules));
    }

    private void SetExecApprovalsFailure(
        ExecApprovalsSnapshotFailure? failure,
        PermissionsExecApprovalsStatus status,
        bool bumpVersion)
    {
        SetField(ref _execApprovalsFailure, failure, nameof(ExecApprovalsFailure));
        SetField(ref _execApprovalsStatus, status, nameof(ExecApprovalsStatus));
        if (bumpVersion)
        {
            _execApprovalsStatusVersion++;
            OnPropertyChanged(nameof(ExecApprovalsStatusVersion));
        }
    }

    private void HandleOnUiThread(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(action);
    }

    private static string? ToVoiceHelpResourceKey(PermissionsVoiceSetupRequirement requirement) => requirement switch
    {
        PermissionsVoiceSetupRequirement.SpeechModel => "PermissionsPage_VoiceSettingsHelp_SpeechModel",
        PermissionsVoiceSetupRequirement.VoiceSetup => "PermissionsPage_VoiceSettingsHelp_VoiceSetup",
        PermissionsVoiceSetupRequirement.SpeechModelAndVoiceSetup => "PermissionsPage_VoiceSettingsHelp_Both",
        _ => null,
    };

    private static string MapDefaultAction(ExecApprovalsFile file)
    {
        file.Defaults ??= new ExecApprovalsDefaults();
        file.Agents ??= new Dictionary<string, ExecApprovalsAgent>(StringComparer.Ordinal);
        file.Agents.TryGetValue("main", out var main);
        var security = main?.Security ?? file.Defaults.Security ?? ExecSecurity.Deny;
        var ask = main?.Ask ?? file.Defaults.Ask ?? ExecAsk.OnMiss;
        return security switch
        {
            ExecSecurity.Full => "allow",
            ExecSecurity.Allowlist when ask is ExecAsk.OnMiss or ExecAsk.Always => "prompt",
            _ => "deny",
        };
    }

    private static string NormalizeAction(string? action)
    {
        if (string.Equals(action, "allow", StringComparison.OrdinalIgnoreCase))
        {
            return "allow";
        }

        if (string.Equals(action, "prompt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "ask", StringComparison.OrdinalIgnoreCase))
        {
            return "prompt";
        }

        return "deny";
    }

    private static ExecApprovalsFile CloneFile(ExecApprovalsFile file) =>
        new()
        {
            Version = file.Version,
            Socket = file.Socket is null ? null : new ExecApprovalsSocketConfig
            {
                Path = file.Socket.Path,
                Token = file.Socket.Token,
            },
            Defaults = file.Defaults is null ? null : new ExecApprovalsDefaults
            {
                Security = file.Defaults.Security,
                Ask = file.Defaults.Ask,
                AskFallback = file.Defaults.AskFallback,
                AutoAllowSkills = file.Defaults.AutoAllowSkills,
            },
            Agents = file.Agents?.ToDictionary(
                pair => pair.Key,
                pair => new ExecApprovalsAgent
                {
                    Security = pair.Value.Security,
                    Ask = pair.Value.Ask,
                    AskFallback = pair.Value.AskFallback,
                    AutoAllowSkills = pair.Value.AutoAllowSkills,
                    Allowlist = pair.Value.Allowlist?.Select(entry => new ExecAllowlistEntry
                    {
                        Id = entry.Id,
                        Pattern = entry.Pattern,
                        ArgPattern = entry.ArgPattern,
                        CommandText = entry.CommandText,
                        Source = entry.Source,
                        LastUsedAt = entry.LastUsedAt,
                        LastResolvedPath = entry.LastResolvedPath,
                        LastUsedCommand = entry.LastUsedCommand,
                    }).ToList(),
                },
                StringComparer.Ordinal),
        };

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private enum ExecApprovalsMutationKind
    {
        DefaultAction,
        AddRule,
        RemoveRule,
    }

    private sealed record ExecApprovalsMutation(
        ExecApprovalsMutationKind Kind,
        string? Action,
        string? Pattern,
        Guid? RuleId = null,
        string? ArgPattern = null);
}
