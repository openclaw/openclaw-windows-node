using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClawTray.Services;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OpenClawTray.Presentation;

internal enum LocalAiEnginePresentationState { Running, Starting, Stopped, Error }
internal enum LocalAiModelPresentationState { Unknown, NotInstalled, Verified, Loaded }
internal enum LocalAiGatewayPresentationState { Connected, Connecting, NeedsAttention, Disconnected, Error }

internal sealed record LocalAiModelOption(
    string ModelId,
    string DisplayName,
    long DownloadBytes,
    int ContextTokens,
    KvCachePrecision KeyCachePrecision,
    bool IsRecommended,
    bool IsCurrent);

/// <summary>WinUI-free presentation and action owner for the Local AI Hub page.</summary>
internal sealed class LocalAiPageViewModel : INavigationAware, IDisposable, INotifyPropertyChanged
{
    private readonly ILocalAiRuntime _runtime;
    private readonly IPermissionsPageRuntimeSource _gatewaySource;
    private readonly IAppCommands _appCommands;
    private readonly IUiDispatcher _dispatcher;
    private readonly IHostHardwareProbe _hardwareProbe;
    private readonly ILocalAiModelSwitcher _modelSwitcher;
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
    private LocalInferenceUnavailableReason? _localAiUnavailableReason;
    private HostHardwareInfo? _hardware;
    private LocalInferencePlan? _installedPlan;
    private IReadOnlyList<LocalAiModelOption> _modelOptions = [];
    private string? _selectedModelOptionId;
    private string _customModelReference = string.Empty;
    private bool _isSwitchingModel;
    private LocalAiModelSwitchProgress? _switchProgress;
    private string? _switchError;
    private bool _switchCanceled;
    private LocalAiModelSwitchTarget? _pendingForceTarget;
    private CancellationTokenSource? _switchCancellation;

    public LocalAiPageViewModel(
        ILocalAiRuntime runtime,
        IPermissionsPageRuntimeSource gatewaySource,
        IAppCommands appCommands,
        IUiDispatcher dispatcher,
        IHostHardwareProbe hardwareProbe,
        ILocalAiModelSwitcher modelSwitcher)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _gatewaySource = gatewaySource ?? throw new ArgumentNullException(nameof(gatewaySource));
        _appCommands = appCommands ?? throw new ArgumentNullException(nameof(appCommands));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _hardwareProbe = hardwareProbe ?? throw new ArgumentNullException(nameof(hardwareProbe));
        _modelSwitcher = modelSwitcher ?? throw new ArgumentNullException(nameof(modelSwitcher));
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
    public string? ModelName => _installedPlan?.Model.DisplayName ??
        LocalModelCatalog.FindInstalled(_runtimeSnapshot.ModelId)?.DisplayName ??
        _runtimeSnapshot.ModelId;
    public string ContextLengthText => _runtimeSnapshot.ContextLength is { } tokens
        ? FormatContext(tokens)
        : "Unknown";
    public string KvCacheText => FormatKvCache(_runtimeSnapshot);

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
    /// <summary>
    /// A runtime action or model switch is running. Includes a switch started by another
    /// page instance, which can outlive navigation once it can no longer be canceled.
    /// </summary>
    public bool IsBusy => _isBusy || _isSwitchingModel || _modelSwitcher.IsSwitching;
    public bool IsAvailabilityKnown => _isAvailabilityKnown;
    public bool IsLocalAiAvailable => _isAvailabilityKnown && _isLocalAiAvailable;
    public bool HasAvailabilityProbeError => _hasAvailabilityProbeError;
    /// <summary>An availability probe (initial check or recheck) is currently in flight.</summary>
    public bool IsCheckingAvailability => _availabilityCancellation is not null;
    public bool ShowAvailabilityInfoBar =>
        (_isAvailabilityKnown && !_isLocalAiAvailable) || _hasAvailabilityProbeError || IsCheckingAvailability;
    public bool IsSetupAvailable => !_isAvailabilityKnown || _isLocalAiAvailable;
    public bool CanRecheckAvailability =>
        (_hasAvailabilityProbeError || _localAiUnavailableReason?.Kind == LocalInferenceUnavailableReasonKind.InsufficientDiskSpace) &&
        _availabilityCancellation is null && !IsBusy;
    public LocalInferenceUnavailableReason? LocalAiUnavailableReason => _localAiUnavailableReason;
    public LocalInferenceProfileRecommendation? ProfileRecommendation { get; private set; }
    public bool RequiresNativeSelection => _runtime.RequiresNativeSelection;
    public bool CanStart => !IsBusy && !RequiresNativeSelection && HasManagedInstall &&
        (_runtimeSnapshot.State == LocalAiRuntimeState.Stopped ||
            (_runtimeSnapshot.State == LocalAiRuntimeState.Failed &&
                _runtimeSnapshot.Ownership != LocalAiOwnership.CompanionManaged));
    public bool CanStop => !IsBusy &&
        (_runtimeSnapshot.GatewayRouteRequiresResolution ||
            _runtimeSnapshot.State is LocalAiRuntimeState.Conflict or LocalAiRuntimeState.Failed ||
            (HasManagedInstall &&
                _runtimeSnapshot.Ownership == LocalAiOwnership.CompanionManaged &&
                _runtimeSnapshot.State is LocalAiRuntimeState.Starting or LocalAiRuntimeState.Healthy));
    public bool CanRestart => !IsBusy && !RequiresNativeSelection &&
        _runtimeSnapshot.Ownership == LocalAiOwnership.CompanionManaged &&
        _runtimeSnapshot.State == LocalAiRuntimeState.Healthy;
    public bool ShowReleaseOwnership => _runtime.HasReleasableOwnership;
    public bool CanReleaseOwnership => !IsBusy && ShowReleaseOwnership &&
        _runtimeSnapshot.State == LocalAiRuntimeState.Stopped &&
        _runtimeSnapshot.Ownership == LocalAiOwnership.None && !_runtimeSnapshot.GatewayRouteRequiresResolution;
    public bool CanOpenLogs => !IsBusy && HasManagedInstall;
    public bool CanRetrySetup => IsSetupAvailable && !IsBusy && ModelState is
        LocalAiModelPresentationState.NotInstalled or LocalAiModelPresentationState.Unknown;
    public bool HasInstalledModel => ModelState is
        LocalAiModelPresentationState.Verified or LocalAiModelPresentationState.Loaded;
    public IReadOnlyList<LocalAiModelOption> ModelOptions => _modelOptions;

    public string? SelectedModelOptionId
    {
        get => _selectedModelOptionId;
        set
        {
            if (string.Equals(_selectedModelOptionId, value, StringComparison.Ordinal))
                return;
            _selectedModelOptionId = value;
            ClearSwitchFeedback();
        }
    }

    public string CustomModelReference
    {
        get => _customModelReference;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_customModelReference, value, StringComparison.Ordinal))
                return;
            _customModelReference = value;
            ClearSwitchFeedback();
        }
    }

    public bool IsCustomModelActive => !string.IsNullOrWhiteSpace(_customModelReference);
    public bool CanApplyModel => !IsBusy && HasInstalledModel && _hardware is not null &&
        (IsCustomModelActive ||
         (_selectedModelOptionId is { } id &&
          !string.Equals(id, _installedPlan?.Model.Id ?? _runtimeSnapshot.ModelId, StringComparison.OrdinalIgnoreCase)));
    public bool IsSwitchingModel => _isSwitchingModel;
    /// <summary>A switch started by another page instance is still running.</summary>
    public bool IsSwitchInProgressElsewhere => !_isSwitchingModel && _modelSwitcher.IsSwitching;
    public LocalAiModelSwitchProgress? SwitchProgress => _switchProgress;
    public bool CanCancelModelSwitch => _isSwitchingModel && _switchProgress?.Phase is null
        or LocalAiModelSwitchPhase.Resolving or LocalAiModelSwitchPhase.Downloading or LocalAiModelSwitchPhase.Verifying;
    public string? SwitchError => _switchError;
    public bool SwitchCanceled => _switchCanceled;
    public LocalAiModelSwitchTarget? FitWarning => _pendingForceTarget;
    public bool CanRepairConnection => !IsBusy && GatewayState is not
        (LocalAiGatewayPresentationState.Connected or LocalAiGatewayPresentationState.Connecting);
    public bool CanOpenChat => !IsBusy &&
        GatewayState == LocalAiGatewayPresentationState.Connected &&
        _runtimeSnapshot.State == LocalAiRuntimeState.Healthy &&
        ModelState is LocalAiModelPresentationState.Verified or LocalAiModelPresentationState.Loaded;

    private bool HasManagedInstall => LocalAiRuntimePresentation.HasManagedInstall(_runtimeSnapshot);

    public void Activate(object? parameter)
    {
        ThrowIfDisposed();
        IsActive = true;
        if (!_subscribed)
        {
            _runtime.StateChanged += OnRuntimeStateChanged;
            _runtime.SelectionChanged += OnSelectionChanged;
            _gatewaySource.Changed += OnGatewayChanged;
            _modelSwitcher.IsSwitchingChanged += OnModelSwitchingChanged;
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
        if (CanCancelModelSwitch)
            _switchCancellation?.Cancel();
        if (_subscribed)
        {
            _runtime.StateChanged -= OnRuntimeStateChanged;
            _runtime.SelectionChanged -= OnSelectionChanged;
            _gatewaySource.Changed -= OnGatewayChanged;
            _modelSwitcher.IsSwitchingChanged -= OnModelSwitchingChanged;
            _subscribed = false;
        }
        IsActive = false;
    }

    public Task<bool> StartAsync() => RunRuntimeActionAsync(CanStart, _runtime.EnsureStartedAsync);
    public Task<bool> StopAsync() => RunRuntimeActionAsync(CanStop, _runtime.StopAsync);
    public Task<bool> ReleaseOwnershipAsync() => RunRuntimeActionAsync(CanReleaseOwnership, _runtime.ReleaseOwnershipAsync);
    public Task<bool> RestartAsync() => RunRuntimeActionAsync(CanRestart, _runtime.RestartAsync);
    public bool OpenLogs() => RunCommand(CanOpenLogs, _appCommands.OpenLocalAiLogs);
    public bool RetrySetup() => RunCommand(CanRetrySetup, _appCommands.ShowLocalAiSetup);
    public Task<bool> ApplyModelAsync() => RunModelSwitchAsync(force: false);
    public Task<bool> ForceApplyModelAsync() => RunModelSwitchAsync(force: true);

    public void CancelModelSwitch()
    {
        if (CanCancelModelSwitch)
            _switchCancellation?.Cancel();
    }

    private void ClearSwitchFeedback()
    {
        _pendingForceTarget = null;
        _switchError = null;
        _switchCanceled = false;
        OnPropertyChanged(null);
    }

    private async Task<bool> RunModelSwitchAsync(bool force)
    {
        ThrowIfDisposed();
        if (IsBusy || (!force && !CanApplyModel) || (force && _pendingForceTarget is null))
            return false;
        var cancellation = new CancellationTokenSource();
        _switchCancellation = cancellation;
        _isSwitchingModel = true;
        _switchError = null;
        _switchCanceled = false;
        OnPropertyChanged(null);
        try
        {
            CancellationToken ct = cancellation.Token;
            LocalAiModelSwitchTarget target = force
                ? _pendingForceTarget!
                : IsCustomModelActive
                    ? await _modelSwitcher.PrepareCustomAsync(_hardware!, _customModelReference.Trim(), ct)
                    : await _modelSwitcher.PrepareCatalogAsync(_hardware!, _selectedModelOptionId!, ct);
            if (target.RequiresForce && !force)
            {
                _pendingForceTarget = target;
                return false;
            }

            _pendingForceTarget = null;
            LocalAiRuntimeSnapshot snapshot = await _modelSwitcher.SwitchAsync(
                target,
                force,
                new CoalescingSwitchProgress(_dispatcher, progress =>
                {
                    if (_disposed || !_isSwitchingModel)
                        return;
                    bool phaseChanged = progress.Phase != _switchProgress?.Phase;
                    _switchProgress = progress;
                    // Byte-only updates refresh just the progress area; a phase change can
                    // change which actions are available (cancel is only allowed early).
                    OnPropertyChanged(phaseChanged ? null : nameof(SwitchProgress));
                }),
                ct);
            _isSwitchingModel = false;
            _customModelReference = string.Empty;
            _selectedModelOptionId = null;
            ApplyRuntimeSnapshot(snapshot);
            if (IsActive)
                StartAvailabilityRefresh();
            return true;
        }
        catch (OperationCanceledException)
        {
            _switchCanceled = true;
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or HttpRequestException)
        {
            _switchError = ex.Message;
            return false;
        }
        finally
        {
            _isSwitchingModel = false;
            _switchProgress = null;
            if (ReferenceEquals(_switchCancellation, cancellation))
                _switchCancellation = null;
            cancellation.Dispose();
            if (!_disposed)
                OnPropertyChanged(null);
        }
    }

    public bool RepairConnection() => RunCommand(CanRepairConnection, _appCommands.Reconnect);
    public bool OpenChat() => RunCommand(CanOpenChat, () => _appCommands.Navigate("chat"));
    public bool RecheckAvailability()
    {
        ThrowIfDisposed();
        if (!IsActive || !CanRecheckAvailability)
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
        // Same atomic grab-and-clear as CancelAvailabilityRefresh(): RefreshRuntimeSnapshotAsync's
        // finally block clears/disposes this token from a worker thread after ConfigureAwait(false),
        // so a plain read-then-write here could race it the same way.
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _refreshCancellation, null);
        cancellation?.Cancel();
    }

    private void StartAvailabilityRefresh()
    {
        CancelAvailabilityRefresh();
        _isAvailabilityKnown = false;
        _isLocalAiAvailable = false;
        _hasAvailabilityProbeError = false;
        _localAiUnavailableReason = null;
        ProfileRecommendation = null;
        _hardware = null;
        var cancellation = new CancellationTokenSource();
        // Assign the new probe slot before notifying, so IsCheckingAvailability (which reads
        // _availabilityCancellation) already reports true in this notification. Notifying first
        // would report the pre-refresh "not checking" state, so a UI bound to PropertyChanged
        // (like the Hub page) would not reliably show checking/recheck progress until the probe
        // had already completed.
        _availabilityCancellation = cancellation;
        OnPropertyChanged(null);
        _ = RefreshAvailabilityAsync(cancellation);
    }

    private void CancelAvailabilityRefresh()
    {
        // Interlocked.Exchange atomically grabs and clears the field together, so this can
        // never race the worker-thread rejected-enqueue path in
        // ApplyAvailabilityResultOnUiThread: whichever side's atomic operation the CPU
        // actually applies first "wins" the token, and the other side observes the field
        // already null and does nothing further with it. A plain read-then-null-then-Cancel()
        // sequence would let this method capture the token into a local variable, lose the
        // field-ownership race to the worker thread's CompareExchange, and then still call
        // Cancel() on the copy the worker already disposed, throwing ObjectDisposedException.
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _availabilityCancellation, null);
        cancellation?.Cancel();
    }

    /// <summary>
    /// Locale-neutral placeholder used when the probe itself failed to produce facts (thrown
    /// exception, or a successful read that came back incomplete). The View resolves this kind
    /// into localized text; no language-specific text lives on the ViewModel.
    /// </summary>
    private static readonly LocalInferenceUnavailableReason ProbeFailureReason = new(
        LocalInferenceUnavailableReasonKind.HardwareFactsIncomplete,
        ModelDisplayName: null,
        RequiredGigabytes: 0,
        DetectedGigabytes: null,
        DetectedDriverVersion: null,
        MinimumDriverVersion: string.Empty);

    private async Task RefreshAvailabilityAsync(CancellationTokenSource cancellation)
    {
        // The probe result is applied and the cancellation is released together, inside the
        // single dispatched callback below. Clearing _availabilityCancellation here (before the
        // dispatched callback runs) would make a queued-but-not-yet-run callback's own
        // IsCurrentAvailabilityProbe guard fail against itself, silently dropping a real
        // asynchronous DispatcherQueue completion.
        // Captured before the probe so the managed-install receipt is read on the caller's
        // thread rather than on whatever thread resumes after the awaited probe.
        string? installedModelId = _runtimeSnapshot.ModelId;
        try
        {
            LocalInferencePlan? installedPlan = await _modelSwitcher
                .LoadInstalledPlanAsync(cancellation.Token).ConfigureAwait(false);
            HostHardwareInfo hardware = await Task.Run(
                _hardwareProbe.Probe,
                cancellation.Token).ConfigureAwait(false);
            // Evaluate device-level eligibility (the best catalog model this hardware can run),
            // not the currently selected/installed model. A selection-specific failure (unknown,
            // deprecated, or oversized model) must not report the device itself as unavailable
            // and block retry-setup from switching to a compatible catalog model.
            //
            // The one exception is a SKU that has no recommended default at all (RTX Spark
            // 32 GB). That says nothing about whether this device can run what is already
            // installed, so the managed-install receipt is taken into account: an installed
            // model that still qualifies keeps this entry point available, which is what lets
            // Retry Setup reach the manifest-aware recovery path and model switching stay enabled.
            // A machine with no managed receipt still reports unavailable, and a receipt whose
            // model is unknown or no longer fits reports that model's own reason.
            LocalInferenceEligibilityResult eligibility =
                LocalInferenceEligibility.EvaluateForConfiguredAvailability(hardware, installedModelId,
                    hasInstalledReceipt: !string.IsNullOrWhiteSpace(installedModelId), installedPlan);
            ModelSelection selection = BuildModelSelection(hardware, installedPlan);
            if (eligibility.FailureCode == LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete)
            {
                // Incomplete facts (a CUDA read that came back partial or transient) are
                // inconclusive, not a definitive "this device cannot run Local AI". Report it the
                // same way as a thrown probe failure below so recheck stays available instead of
                // permanently disabling Local AI on this device.
                ApplyAvailabilityResultOnUiThread(
                    cancellation,
                    isAvailabilityKnown: false,
                    isLocalAiAvailable: false,
                    hasAvailabilityProbeError: true,
                    ProbeFailureReason);
                return;
            }
            bool isAvailable = eligibility.CanInstall;
            LocalInferenceUnavailableReason? unavailableReason = isAvailable
                ? null
                : LocalInferenceEligibilityDiagnostics.GetUnavailableReason(eligibility);
            ApplyAvailabilityResultOnUiThread(
                cancellation,
                isAvailabilityKnown: true,
                isLocalAiAvailable: isAvailable,
                hasAvailabilityProbeError: false,
                unavailableReason, LocalInferenceProfileRecommendation.From(eligibility), selection);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer probe already replaced (or cleared) _availabilityCancellation, so this
            // probe is stale by definition. Just release the token; do not touch shared state.
            cancellation.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Local AI availability probe failed: {ex}");
            ApplyAvailabilityResultOnUiThread(
                cancellation,
                isAvailabilityKnown: false,
                isLocalAiAvailable: false,
                hasAvailabilityProbeError: true,
                ProbeFailureReason);
        }
    }

    private sealed record ModelSelection(
        HostHardwareInfo Hardware,
        LocalInferencePlan? InstalledPlan,
        IReadOnlyList<LocalAiModelOption> Options);

    /// <summary>The installed model first, then every catalog model this PC can be offered.</summary>
    private static ModelSelection BuildModelSelection(HostHardwareInfo hardware, LocalInferencePlan? installedPlan)
    {
        string? recommendedId = LocalInferenceEligibility.Evaluate(hardware) is { CanInstall: true, Plan: { } recommended }
            ? recommended.Model.Id
            : null;
        var options = new List<LocalAiModelOption>();
        if (installedPlan is not null)
            options.Add(ToOption(installedPlan, recommendedId, isCurrent: true));
        foreach (LocalInferencePlan plan in LocalInferenceEligibility.OfferedPlans(id => LocalInferenceEligibility.Evaluate(hardware, id)))
        {
            if (!string.Equals(plan.Model.Id, installedPlan?.Model.Id, StringComparison.OrdinalIgnoreCase))
                options.Add(ToOption(plan, recommendedId, isCurrent: false));
        }
        return new ModelSelection(hardware, installedPlan, options);
    }

    private static LocalAiModelOption ToOption(LocalInferencePlan plan, string? recommendedId, bool isCurrent) =>
        new(
            plan.Model.Id,
            plan.Model.DisplayName,
            LocalModelCatalog.TotalDownloadSizeBytes(plan.Model),
            plan.Profile.ContextTokens,
            plan.Profile.KeyCachePrecision,
            string.Equals(plan.Model.Id, recommendedId, StringComparison.OrdinalIgnoreCase),
            isCurrent);

    /// <summary>
    /// Dispatches a completed availability probe's result to the UI thread, guaranteeing
    /// <paramref name="cancellation"/> is disposed exactly once no matter which path runs: applied
    /// inline, applied from a genuinely deferred dispatcher callback, or never applied at all
    /// because the ViewModel is already disposed/inactive or the dispatcher refused the enqueue.
    /// A plain <see cref="ApplyOnUiThread"/> call would silently leak the token on those last two
    /// paths, since its own no-op guard runs before (or instead of) the wrapped action.
    /// </summary>
    private void ApplyAvailabilityResultOnUiThread(
        CancellationTokenSource cancellation,
        bool isAvailabilityKnown,
        bool isLocalAiAvailable,
        bool hasAvailabilityProbeError,
        LocalInferenceUnavailableReason? unavailableReason,
        LocalInferenceProfileRecommendation? recommendation = null,
        ModelSelection? selection = null)
    {
        if (_disposed || !IsActive)
        {
            // This check and the dispose below run on the probing worker thread (this whole
            // method is reached from RefreshAvailabilityAsync after ConfigureAwait(false)), so
            // it must not assume Deactivate()/Dispose() having flipped these plain, unsynchronized
            // flags means CancelAvailabilityRefresh() has already fully claimed and released this
            // exact token; that assumption depends on cross-thread visibility ordering these plain
            // fields don't guarantee. Route through the same atomic release path as the
            // rejected-enqueue branch below instead of disposing unconditionally.
            ReleaseAbandonedAvailabilityProbe(cancellation);
            return;
        }

        if (_dispatcher.HasThreadAccess)
        {
            ApplyAvailabilityResult(
                cancellation, isAvailabilityKnown, isLocalAiAvailable, hasAvailabilityProbeError, unavailableReason, recommendation, selection);
            return;
        }

        bool enqueued = _dispatcher.TryEnqueue(() =>
        {
            // This callback runs on the UI thread (that is what TryEnqueue guarantees when it
            // returns true), the same thread CancelAvailabilityRefresh() runs on, so the two are
            // already serialized here; no atomic coordination is needed for this plain check.
            if (_disposed || !IsActive)
            {
                cancellation.Dispose();
                return;
            }
            ApplyAvailabilityResult(
                cancellation, isAvailabilityKnown, isLocalAiAvailable, hasAvailabilityProbeError, unavailableReason, recommendation, selection);
        });
        if (!enqueued)
        {
            // The dispatcher refused the enqueue (e.g. it is shutting down). This runs on the
            // probing worker thread, not the UI thread, so it must not call OnPropertyChanged
            // (WinUI's page code-behind touches controls from that notification and would throw
            // on the wrong thread) either.
            ReleaseAbandonedAvailabilityProbe(cancellation);
        }
    }

    /// <summary>
    /// Releases an availability-probe token from a worker thread when it will never reach
    /// <see cref="ApplyAvailabilityResult"/> (the ViewModel is disposed/inactive, or the
    /// dispatcher refused the enqueue). Interlocked.CompareExchange atomically claims the token:
    /// only the side that actually swaps it out for null may dispose it, so this can never
    /// dispose a token CancelAvailabilityRefresh() is concurrently (or has already) called
    /// Cancel() on. CancelAvailabilityRefresh() uses the matching Interlocked.Exchange to
    /// grab-and-clear the field atomically, so exactly one of the two atomic operations "wins"
    /// any given token; the loser observes the field already null and leaves its local copy of
    /// the token untouched. A rare undisposed CancellationTokenSource when the loser is this
    /// method is an acceptable trade-off for never risking ObjectDisposedException, since this
    /// class never registers a timeout that would give the token an unmanaged resource to leak.
    /// </summary>
    private void ReleaseAbandonedAvailabilityProbe(CancellationTokenSource cancellation)
    {
        if (Interlocked.CompareExchange(ref _availabilityCancellation, null, cancellation) == cancellation)
            cancellation.Dispose();
    }

    /// <summary>
    /// Applies a completed availability probe's result and releases its cancellation together,
    /// so the currency check and the state clear cannot race a real asynchronous DispatcherQueue
    /// callback. Always disposes <paramref name="cancellation"/>: a stale probe (superseded by a
    /// newer one) is no longer referenced by <see cref="_availabilityCancellation"/> by
    /// definition, so nothing else can still cancel or dispose this exact instance.
    /// </summary>
    private void ApplyAvailabilityResult(
        CancellationTokenSource cancellation,
        bool isAvailabilityKnown,
        bool isLocalAiAvailable,
        bool hasAvailabilityProbeError,
        LocalInferenceUnavailableReason? unavailableReason,
        LocalInferenceProfileRecommendation? recommendation = null,
        ModelSelection? selection = null)
    {
        if (!IsCurrentAvailabilityProbe(cancellation))
        {
            cancellation.Dispose();
            return;
        }
        _availabilityCancellation = null;
        _isAvailabilityKnown = isAvailabilityKnown;
        _isLocalAiAvailable = isLocalAiAvailable;
        _hasAvailabilityProbeError = hasAvailabilityProbeError;
        _localAiUnavailableReason = unavailableReason;
        ProfileRecommendation = recommendation;
        if (selection is not null)
        {
            _hardware = selection.Hardware;
            _installedPlan = selection.InstalledPlan;
            _modelOptions = selection.Options;
            if (_selectedModelOptionId is null ||
                !_modelOptions.Any(option => string.Equals(option.ModelId, _selectedModelOptionId, StringComparison.OrdinalIgnoreCase)))
                _selectedModelOptionId = _installedPlan?.Model.Id ?? _modelOptions.FirstOrDefault()?.ModelId;
        }
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
            // Interlocked.CompareExchange pairs with CancelRuntimeRefresh()'s
            // Interlocked.Exchange: only the side that atomically wins clearing the field may
            // dispose this token, so a concurrent CancelRuntimeRefresh() on the UI thread can
            // never call Cancel() on an instance this worker thread already disposed. If
            // CancelRuntimeRefresh() already claimed the field first, this side leaves the
            // token undisposed rather than risk disposing it out from under an in-flight
            // Cancel() call; CancellationTokenSource has no unmanaged resource to leak unless a
            // timeout was registered, which this class never does.
            if (Interlocked.CompareExchange(ref _refreshCancellation, null, cancellation) == cancellation)
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
    private void OnSelectionChanged(object? sender, EventArgs e) =>
        ApplyOnUiThread(() => OnPropertyChanged(null));
    private void OnModelSwitchingChanged(object? sender, EventArgs e) => ApplyOnUiThread(() =>
    {
        // A switch that ended (ours or another page's) may have committed a new receipt;
        // reload the installed plan so options and "current" are not stale.
        if (!_modelSwitcher.IsSwitching && !_isSwitchingModel)
            StartAvailabilityRefresh();
        else
            OnPropertyChanged(null);
    });
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
        string? previousModelId = _runtimeSnapshot.ModelId;
        _runtimeSnapshot = snapshot;
        OnPropertyChanged(null);
        // Availability reads the managed-install receipt (see RefreshAvailabilityAsync), which the
        // runtime refresh may only publish after that read has already happened. Recomputing when
        // the model id changes is what keeps a first visit correct: otherwise a 32 GB Spark whose
        // receipt arrives late stays pinned at NotRecommendedForSku, with Retry Setup and Change
        // Model disabled and Recheck unavailable, until the page is left and reopened.
        if (IsActive && !string.Equals(previousModelId, snapshot.ModelId, StringComparison.Ordinal))
            StartAvailabilityRefresh();
    }
    private void ApplyGatewaySnapshot(GatewayConnectionSnapshot snapshot)
    {
        _gatewaySnapshot = snapshot;
        OnPropertyChanged(null);
    }

    private static string FormatContext(int tokens) =>
        tokens % 1024 == 0
            ? $"{tokens / 1024}K"
            : tokens % 1000 == 0
                ? $"{tokens / 1000}K"
                : $"{tokens:N0} tokens";

    private static string FormatKvCache(LocalAiRuntimeSnapshot snapshot)
    {
        if (snapshot.KeyCachePrecision is not { } targetPrecision ||
            snapshot.DraftKeyCachePrecision is not { } draftPrecision)
        {
            return "Unknown";
        }

        string target = LocalModelCatalog.ToDisplayCacheType(targetPrecision);
        string draft = LocalModelCatalog.ToDisplayCacheType(draftPrecision);
        return target == draft ? $"{target} target + MTP draft" : $"{target} target + {draft} MTP draft";
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
