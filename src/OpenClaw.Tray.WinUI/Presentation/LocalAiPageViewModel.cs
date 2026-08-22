using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference.Catalog;
using OpenClawTray.Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OpenClawTray.Presentation;

internal enum LocalAiEnginePresentationState { Running, Starting, Stopped, Error }
internal enum LocalAiModelPresentationState { Unknown, NotInstalled, Verified, Loaded }
internal enum LocalAiGatewayPresentationState { Connected, Connecting, NeedsAttention, Disconnected, Error }

/// <summary>WinUI-free presentation and action owner for the Local AI Hub page.</summary>
internal sealed class LocalAiPageViewModel : INavigationAware, IDisposable, INotifyPropertyChanged
{
    private readonly ILocalAiRuntime _runtime;
    private readonly IPermissionsPageRuntimeSource _gatewaySource;
    private readonly IAppCommands _appCommands;
    private readonly IUiDispatcher _dispatcher;
    private LocalAiRuntimeSnapshot _runtimeSnapshot;
    private GatewayConnectionSnapshot _gatewaySnapshot;
    private CancellationTokenSource? _refreshCancellation;
    private bool _subscribed;
    private bool _disposed;
    private bool _isBusy;
    private string? _actionError;

    public LocalAiPageViewModel(
        ILocalAiRuntime runtime,
        IPermissionsPageRuntimeSource gatewaySource,
        IAppCommands appCommands,
        IUiDispatcher dispatcher)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _gatewaySource = gatewaySource ?? throw new ArgumentNullException(nameof(gatewaySource));
        _appCommands = appCommands ?? throw new ArgumentNullException(nameof(appCommands));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runtimeSnapshot = runtime.Snapshot;
        _gatewaySnapshot = gatewaySource.Current.ConnectionSnapshot;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal bool IsActive { get; private set; }
    internal bool IsDisposed => _disposed;

    public LocalAiEnginePresentationState EngineState => _runtimeSnapshot.State switch
    {
        LocalAiRuntimeState.Healthy => LocalAiEnginePresentationState.Running,
        LocalAiRuntimeState.Starting or LocalAiRuntimeState.Stopping => LocalAiEnginePresentationState.Starting,
        LocalAiRuntimeState.Conflict or LocalAiRuntimeState.Failed => LocalAiEnginePresentationState.Error,
        _ => LocalAiEnginePresentationState.Stopped,
    };

    public string EngineStatusResourceKey => EngineState switch
    {
        LocalAiEnginePresentationState.Running => "LocalAiPage_Engine_Running",
        LocalAiEnginePresentationState.Starting => "LocalAiPage_Engine_Starting",
        LocalAiEnginePresentationState.Error => "LocalAiPage_Engine_Error",
        _ => "LocalAiPage_Engine_Stopped",
    };

    public string EngineOwnershipResourceKey => HasManagedInstall
        ? "LocalAiPage_Engine_Managed"
        : "LocalAiPage_Engine_NotInstalled";
    public string? EngineVersion => _runtimeSnapshot.EngineVersion;
    public string Endpoint => _runtimeSnapshot.Endpoint.ToString();
    public string? ProcessId => _runtimeSnapshot.ProcessId?.ToString();
    public string? EngineDetail => _runtimeSnapshot.Detail;
    public string? ModelName => LocalModelCatalog.Find(_runtimeSnapshot.ModelId)?.DisplayName ??
        _runtimeSnapshot.ModelId;
    public const string ContextLengthText = "256K";
    public const string KvCacheText = "FP16";

    public LocalAiModelPresentationState ModelState => _runtimeSnapshot.ModelEvidence.State switch
    {
        LocalAiModelAvailabilityState.NotInstalled => LocalAiModelPresentationState.NotInstalled,
        LocalAiModelAvailabilityState.Verified => LocalAiModelPresentationState.Verified,
        LocalAiModelAvailabilityState.Loaded => LocalAiModelPresentationState.Loaded,
        _ => LocalAiModelPresentationState.Unknown,
    };

    public string ModelStatusResourceKey => ModelState switch
    {
        LocalAiModelPresentationState.NotInstalled => "LocalAiPage_Model_NotInstalled",
        LocalAiModelPresentationState.Verified => "LocalAiPage_Model_Verified",
        LocalAiModelPresentationState.Loaded => "LocalAiPage_Model_Loaded",
        _ => "LocalAiPage_Model_Unknown",
    };

    public LocalAiGatewayPresentationState GatewayState => _gatewaySnapshot.OperatorState switch
    {
        RoleConnectionState.Connected => LocalAiGatewayPresentationState.Connected,
        RoleConnectionState.Connecting => LocalAiGatewayPresentationState.Connecting,
        RoleConnectionState.PairingRequired => LocalAiGatewayPresentationState.NeedsAttention,
        RoleConnectionState.Error or RoleConnectionState.PairingRejected or RoleConnectionState.RateLimited =>
            LocalAiGatewayPresentationState.Error,
        _ => LocalAiGatewayPresentationState.Disconnected,
    };

    public string GatewayStatusResourceKey => GatewayState switch
    {
        LocalAiGatewayPresentationState.Connected => "LocalAiPage_Gateway_Connected",
        LocalAiGatewayPresentationState.Connecting => "LocalAiPage_Gateway_Connecting",
        LocalAiGatewayPresentationState.NeedsAttention => "LocalAiPage_Gateway_NeedsAttention",
        LocalAiGatewayPresentationState.Error => "LocalAiPage_Gateway_Error",
        _ => "LocalAiPage_Gateway_Disconnected",
    };

    public string? GatewayDetail => _gatewaySnapshot.GatewayName ?? _gatewaySnapshot.GatewayUrl;
    public string? ActionError => _actionError;
    public bool IsBusy => _isBusy;
    public bool CanStart => !IsBusy && HasManagedInstall &&
        _runtimeSnapshot.State is LocalAiRuntimeState.Stopped or LocalAiRuntimeState.Failed;
    public bool CanStop => !IsBusy && _runtimeSnapshot.Ownership == LocalAiOwnership.CompanionManaged &&
        _runtimeSnapshot.State is LocalAiRuntimeState.Starting or LocalAiRuntimeState.Healthy;
    public bool CanRestart => !IsBusy && _runtimeSnapshot.Ownership == LocalAiOwnership.CompanionManaged &&
        _runtimeSnapshot.State == LocalAiRuntimeState.Healthy;
    public bool CanOpenLogs => !IsBusy && HasManagedInstall;
    public bool CanRetrySetup => !IsBusy && ModelState is
        LocalAiModelPresentationState.NotInstalled or LocalAiModelPresentationState.Unknown;
    public bool CanRepairConnection => !IsBusy && GatewayState is not
        (LocalAiGatewayPresentationState.Connected or LocalAiGatewayPresentationState.Connecting);
    public bool CanOpenChat => !IsBusy &&
        GatewayState == LocalAiGatewayPresentationState.Connected &&
        _runtimeSnapshot.State == LocalAiRuntimeState.Healthy &&
        ModelState is LocalAiModelPresentationState.Verified or LocalAiModelPresentationState.Loaded;

    private bool HasManagedInstall =>
        _runtimeSnapshot.ModelEvidence.State is LocalAiModelAvailabilityState.Verified or LocalAiModelAvailabilityState.Loaded ||
        (_runtimeSnapshot.Ownership == LocalAiOwnership.CompanionManaged &&
         _runtimeSnapshot.State != LocalAiRuntimeState.NotInstalled);

    public void Activate(object? parameter)
    {
        ThrowIfDisposed();
        IsActive = true;
        if (!_subscribed)
        {
            _runtime.StateChanged += OnRuntimeStateChanged;
            _gatewaySource.Changed += OnGatewayChanged;
            _subscribed = true;
        }
        ApplyRuntimeSnapshot(_runtime.Snapshot);
        ApplyGatewaySnapshot(_gatewaySource.Current.ConnectionSnapshot);
        StartRuntimeRefresh();
    }

    public void Deactivate()
    {
        CancelRuntimeRefresh();
        if (_subscribed)
        {
            _runtime.StateChanged -= OnRuntimeStateChanged;
            _gatewaySource.Changed -= OnGatewayChanged;
            _subscribed = false;
        }
        IsActive = false;
    }

    public Task<bool> StartAsync() => RunRuntimeActionAsync(CanStart, _runtime.EnsureStartedAsync);
    public Task<bool> StopAsync() => RunRuntimeActionAsync(CanStop, _runtime.StopAsync);
    public Task<bool> RestartAsync() => RunRuntimeActionAsync(CanRestart, _runtime.RestartAsync);
    public bool OpenLogs() => RunCommand(CanOpenLogs, _appCommands.OpenLocalAiLogs);
    public bool RetrySetup() => RunCommand(CanRetrySetup, _appCommands.ShowOnboarding);
    public bool RepairConnection() => RunCommand(CanRepairConnection, _appCommands.Reconnect);
    public bool OpenChat() => RunCommand(CanOpenChat, _appCommands.ShowChat);

    private static bool RunCommand(bool allowed, Action command)
    {
        if (!allowed)
            return false;
        command();
        return true;
    }

    private void StartRuntimeRefresh()
    {
        CancelRuntimeRefresh();
        var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        _ = RefreshRuntimeSnapshotAsync(cancellation);
    }

    private void CancelRuntimeRefresh()
    {
        CancellationTokenSource? cancellation = _refreshCancellation;
        _refreshCancellation = null;
        cancellation?.Cancel();
    }

    private async Task RefreshRuntimeSnapshotAsync(CancellationTokenSource cancellation)
    {
        try
        {
            LocalAiRuntimeSnapshot snapshot = await _runtime.RefreshAsync(cancellation.Token).ConfigureAwait(false);
            ApplyOnUiThread(() => ApplyRuntimeSnapshot(snapshot));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ApplyOnUiThread(() =>
            {
                _actionError = ex.Message;
                OnPropertyChanged(null);
            });
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
                _refreshCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task<bool> RunRuntimeActionAsync(
        bool allowed,
        Func<CancellationToken, Task<LocalAiRuntimeSnapshot>> action)
    {
        ThrowIfDisposed();
        if (!allowed || _isBusy)
            return false;
        _isBusy = true;
        _actionError = null;
        OnPropertyChanged(null);
        try
        {
            ApplyRuntimeSnapshot(await action(CancellationToken.None));
            return _runtimeSnapshot.State is not (LocalAiRuntimeState.Conflict or LocalAiRuntimeState.Failed);
        }
        catch (Exception ex)
        {
            _actionError = ex.Message;
            OnPropertyChanged(null);
            return false;
        }
        finally
        {
            _isBusy = false;
            OnPropertyChanged(null);
        }
    }

    private void OnRuntimeStateChanged(object? sender, LocalAiRuntimeSnapshotChangedEventArgs e) =>
        ApplyOnUiThread(() => ApplyRuntimeSnapshot(e.Snapshot));
    private void OnGatewayChanged(object? sender, PermissionsRuntimeSourceChangedEventArgs e) =>
        ApplyOnUiThread(() => ApplyGatewaySnapshot(e.Snapshot.ConnectionSnapshot));

    private void ApplyOnUiThread(Action action)
    {
        if (_disposed || !IsActive)
            return;
        if (_dispatcher.HasThreadAccess)
            action();
        else
            _dispatcher.TryEnqueue(() => { if (!_disposed && IsActive) action(); });
    }

    private void ApplyRuntimeSnapshot(LocalAiRuntimeSnapshot snapshot)
    {
        _runtimeSnapshot = snapshot;
        OnPropertyChanged(null);
    }
    private void ApplyGatewaySnapshot(GatewayConnectionSnapshot snapshot)
    {
        _gatewaySnapshot = snapshot;
        OnPropertyChanged(null);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Deactivate();
        _disposed = true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
