using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClawTray.Services;
using System.ComponentModel;
using System.Diagnostics;
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
    private readonly IHostHardwareProbe _hardwareProbe;
    private LocalAiRuntimeSnapshot _runtimeSnapshot;
    private GatewayConnectionSnapshot _gatewaySnapshot;
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _availabilityCancellation;
    private bool _subscribed;
    private bool _disposed;
    private bool _isBusy;
    private string? _actionError;
    private bool _isAvailabilityKnown;
    private bool _isLocalAiAvailable;
    private bool _hasAvailabilityProbeError;
    private string? _localAiUnavailableReason;

    public LocalAiPageViewModel(
        ILocalAiRuntime runtime,
        IPermissionsPageRuntimeSource gatewaySource,
        IAppCommands appCommands,
        IUiDispatcher dispatcher,
        IHostHardwareProbe hardwareProbe)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _gatewaySource = gatewaySource ?? throw new ArgumentNullException(nameof(gatewaySource));
        _appCommands = appCommands ?? throw new ArgumentNullException(nameof(appCommands));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _hardwareProbe = hardwareProbe ?? throw new ArgumentNullException(nameof(hardwareProbe));
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
    public bool IsAvailabilityKnown => _isAvailabilityKnown;
    public bool IsLocalAiAvailable => _isAvailabilityKnown && _isLocalAiAvailable;
    public bool HasAvailabilityProbeError => _hasAvailabilityProbeError;
    public bool ShowAvailabilityInfoBar => (_isAvailabilityKnown && !_isLocalAiAvailable) || _hasAvailabilityProbeError;
    public bool IsSetupAvailable => !_isAvailabilityKnown || _isLocalAiAvailable;
    public bool CanRecheckAvailability => _hasAvailabilityProbeError && _availabilityCancellation is null && !IsBusy;
    public string? LocalAiUnavailableReason => _localAiUnavailableReason;
    public bool CanStart => !IsBusy && HasManagedInstall &&
        _runtimeSnapshot.State is LocalAiRuntimeState.Stopped or LocalAiRuntimeState.Failed;
    public bool CanStop => !IsBusy && _runtimeSnapshot.Ownership == LocalAiOwnership.CompanionManaged &&
        _runtimeSnapshot.State is LocalAiRuntimeState.Starting or LocalAiRuntimeState.Healthy;
    public bool CanRestart => !IsBusy && _runtimeSnapshot.Ownership == LocalAiOwnership.CompanionManaged &&
        _runtimeSnapshot.State == LocalAiRuntimeState.Healthy;
    public bool CanOpenLogs => !IsBusy && HasManagedInstall;
    public bool CanRetrySetup => IsSetupAvailable && !IsBusy && ModelState is
        LocalAiModelPresentationState.NotInstalled or LocalAiModelPresentationState.Unknown;
    public bool HasInstalledModel => ModelState is
        LocalAiModelPresentationState.Verified or LocalAiModelPresentationState.Loaded;
    public bool CanChangeModel => IsSetupAvailable && !IsBusy && HasInstalledModel;
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
        StartAvailabilityRefresh();
    }

    public void Deactivate()
    {
        CancelRuntimeRefresh();
        CancelAvailabilityRefresh();
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
    public bool ChangeModel() => RunCommand(CanChangeModel, _appCommands.ShowOnboarding);
    public bool RepairConnection() => RunCommand(CanRepairConnection, _appCommands.Reconnect);
    public bool OpenChat() => RunCommand(CanOpenChat, _appCommands.ShowChat);
    public bool RecheckAvailability()
    {
        ThrowIfDisposed();
        if (!IsActive || _availabilityCancellation is not null)
            return false;
        StartAvailabilityRefresh();
        return true;
    }

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

    private void StartAvailabilityRefresh()
    {
        CancelAvailabilityRefresh();
        _isAvailabilityKnown = false;
        _isLocalAiAvailable = false;
        _hasAvailabilityProbeError = false;
        _localAiUnavailableReason = null;
        OnPropertyChanged(null);
        var cancellation = new CancellationTokenSource();
        _availabilityCancellation = cancellation;
        _ = RefreshAvailabilityAsync(cancellation);
    }

    private void CancelAvailabilityRefresh()
    {
        CancellationTokenSource? cancellation = _availabilityCancellation;
        _availabilityCancellation = null;
        cancellation?.Cancel();
    }

    private const string LocalAiAvailabilityProbeFailureReason =
        "OpenClaw could not read the NVIDIA GPU, driver, CUDA, or memory information. " +
        "Check the NVIDIA driver installation and try setup again.";

    private async Task RefreshAvailabilityAsync(CancellationTokenSource cancellation)
    {
        // The probe result is applied and the cancellation is released together, inside the
        // single dispatched callback below. Clearing _availabilityCancellation here (before the
        // dispatched callback runs) would make a queued-but-not-yet-run callback's own
        // IsCurrentAvailabilityProbe guard fail against itself, silently dropping a real
        // asynchronous DispatcherQueue completion.
        try
        {
            HostHardwareInfo hardware = await Task.Run(
                _hardwareProbe.Probe,
                cancellation.Token).ConfigureAwait(false);
            // Evaluate device-level eligibility (the best catalog model this hardware can run),
            // not the currently selected/installed model. A selection-specific failure (unknown,
            // deprecated, or oversized model) must not report the device itself as unavailable
            // and block retry-setup from switching to a compatible catalog model.
            LocalInferenceEligibilityResult eligibility = LocalInferenceEligibility.Evaluate(hardware);
            if (eligibility.FailureCode == LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete)
            {
                // Incomplete facts (a driver/NVML read that came back partial or transient) are
                // inconclusive, not a definitive "this device cannot run Local AI". Report it the
                // same way as a thrown probe failure below so recheck stays available instead of
                // permanently disabling Local AI on this device.
                ApplyOnUiThread(() => ApplyAvailabilityResult(
                    cancellation,
                    isAvailabilityKnown: false,
                    isLocalAiAvailable: false,
                    hasAvailabilityProbeError: true,
                    LocalAiAvailabilityProbeFailureReason));
                return;
            }
            bool isAvailable = eligibility.CanInstall;
            string? unavailableReason = isAvailable
                ? null
                : LocalInferenceEligibilityDiagnostics.DescribeUnavailable(eligibility);
            ApplyOnUiThread(() => ApplyAvailabilityResult(
                cancellation,
                isAvailabilityKnown: true,
                isLocalAiAvailable: isAvailable,
                hasAvailabilityProbeError: false,
                unavailableReason));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer probe already replaced (or cleared) _availabilityCancellation, so this
            // probe is stale by definition. Just release the token; do not touch shared state.
            cancellation.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Local AI availability probe failed: {ex}");
            ApplyOnUiThread(() => ApplyAvailabilityResult(
                cancellation,
                isAvailabilityKnown: false,
                isLocalAiAvailable: false,
                hasAvailabilityProbeError: true,
                LocalAiAvailabilityProbeFailureReason));
        }
    }

    /// <summary>
    /// Applies a completed availability probe's result and releases its cancellation together,
    /// on the UI thread, so the currency check and the state clear cannot race a real
    /// asynchronous DispatcherQueue callback.
    /// </summary>
    private void ApplyAvailabilityResult(
        CancellationTokenSource cancellation,
        bool isAvailabilityKnown,
        bool isLocalAiAvailable,
        bool hasAvailabilityProbeError,
        string? unavailableReason)
    {
        if (!IsCurrentAvailabilityProbe(cancellation))
            return;
        _availabilityCancellation = null;
        _isAvailabilityKnown = isAvailabilityKnown;
        _isLocalAiAvailable = isLocalAiAvailable;
        _hasAvailabilityProbeError = hasAvailabilityProbeError;
        _localAiUnavailableReason = unavailableReason;
        OnPropertyChanged(null);
        cancellation.Dispose();
    }

    private bool IsCurrentAvailabilityProbe(CancellationTokenSource cancellation) =>
        ReferenceEquals(_availabilityCancellation, cancellation);

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
