using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.SetupEngine.UI.Pages;
using System.Diagnostics;

namespace OpenClaw.SetupEngine.UI.Controls;

public sealed partial class LocalAiSetupControl : UserControl
{
    private SetupConfig? _config;
    private SetupAccessDraft? _draft;
    private SetupWindow? _setupWindow;
    private bool _suppressLocalAiToggle;
    private bool _suppressLocalAiSelection;
    private bool _localAiSelectionEligible;
    private bool _localAiNetworkingConsentRequired;
    private HostHardwareInfo? _localAiHardware;
    private string? _localAiRecommendedModelId;
    private WslGlobalConfigStatus? _localAiNetworkingStatus;
    private string _localAiUnavailableReason = string.Empty;
    private readonly LocalAiSetupAvailabilityCoordinator _localAiAvailability = new();
    private bool _forceLocalAiNetworkingConsent;
    private bool _localAiRecoveryOnly;
    private bool _localAiRecoveryModelPinned;

    public event EventHandler? StateChanged;

    public LocalAiSetupControl()
    {
        InitializeComponent();
        Unloaded += (_, _) => Deactivate();
    }

    internal void Initialize(SetupAccessDraft draft, bool recovery, bool pinModel)
    {
        _draft = draft;
        _config = draft.Config;
        _setupWindow = SetupWindow.Active;
        _localAiRecoveryOnly = recovery;
        _localAiRecoveryModelPinned = pinModel;
        _forceLocalAiNetworkingConsent = SetupPreview.RequestedPage == "capabilities-review-consent";
        AsyncEventHandlerGuard.Run(
            () => InitializeLocalAiReviewAsync(_forceLocalAiNetworkingConsent),
            NullLogger.Instance, nameof(InitializeLocalAiReviewAsync));
    }

    internal void Deactivate()
    {
        _localAiAvailability.CancelCurrent();
        _setupWindow = null;
    }

    private async Task InitializeLocalAiReviewAsync(
        bool forceNetworkingConsent,
        bool refreshHardwareProbe = false,
        LocalAiSetupAvailabilitySnapshot? startedAvailability = null)
    {
        LocalAiSetupAvailabilitySnapshot checking =
            startedAvailability ?? _localAiAvailability.StartProbe();
        ShowLocalAiAvailabilityChecking(checking);
        SetupWindow? setupWindow = _setupWindow;
        Task<HostHardwareInfo> hardwareTask = setupWindow is not null
            ? setupWindow.GetLocalAiHardwareAsync(forceRefresh: refreshHardwareProbe)
            : Task.Run(() => new CudaHostHardwareProbe().Probe());

        string? hardwareReason = null;
        LocalInferenceEligibilityResult? eligibility = null;
        try
        {
            HostHardwareInfo hardware = await hardwareTask;
            if (!CanApplyLocalAiAvailability(checking.Generation, setupWindow))
                return;
            _localAiHardware = hardware;

            // Gate on device-level eligibility (the best catalog model this hardware can run),
            // not the currently configured model. A stale/removed SelectedModelId must not make
            // an otherwise-capable device look unavailable and hide the badge/option; it only
            // means the configured model needs to fall back to a valid one below.
            LocalInferenceEligibilityResult deviceEligibility = LocalInferenceEligibility.Evaluate(_localAiHardware);
            if (deviceEligibility.FailureCode == LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete)
            {
                // Incomplete facts (a partial/transient CUDA read) are inconclusive, not a
                // definitive "this device cannot run Local AI". Report it the same way as a
                // thrown probe failure so recheck stays available instead of the option being
                // permanently disabled.
                if (_localAiAvailability.TryApplyProbeFailure(
                        checking.Generation,
                        LocalAiProbeFailureReason,
                        out var incompleteSnapshot))
                {
                    ShowLocalAiProbeUnknown(incompleteSnapshot);
                }
                return;
            }
            _localAiRecommendedModelId = deviceEligibility.CanInstall
                ? deviceEligibility.Plan?.Model.Id
                : null;

            // A SKU with no recommended default (RTX Spark 32 GB) still runs an already
            // configured model. Gate availability on that configured selection when there is
            // one, so rerunning setup does not switch Local AI off on a working machine.
            // _localAiRecommendedModelId stays null so nothing is labelled Recommended.
            LocalInferenceEligibilityResult availability =
                LocalInferenceEligibility.EvaluateForConfiguredAvailability(
                    _localAiHardware,
                    _config!.LocalAi.SelectedModelId);

            if (!availability.CanInstall || availability.Plan is null || availability.SelectedGpu is null)
            {
                hardwareReason = DescribeLocalAiUnavailable(availability);
            }
            else
            {
                // The device can run Local AI. Reconcile the configured model selection: a
                // model that no longer exists in the catalog, or exists but this specific
                // hardware cannot run at all (e.g. the config was moved to a machine with a
                // smaller GPU), falls back to the recommended (or the device-eligible default)
                // model instead of leaving setup stuck on a known-incompatible selection. A
                // merely busy GPU (EligibleButBusy) is not reconciled away: the same model would
                // still work once the GPU frees up, and CanInstall already covers that case.
                if (_config.LocalAi.SelectedModelId is { } selectedModelId)
                {
                    LocalInferenceEligibilityResult selectedEligibility =
                        LocalInferenceEligibility.Evaluate(_localAiHardware, selectedModelId);
                    if (_localAiRecoveryModelPinned)
                    {
                        eligibility = selectedEligibility;
                    }
                    else if (!selectedEligibility.CanInstall)
                    {
                        _config.LocalAi.SelectedModelId = null;
                    }
                }
                _config.LocalAi.SelectedModelId ??= _localAiRecommendedModelId ?? availability.Plan.Model.Id;

                eligibility ??= LocalInferenceEligibility.Evaluate(
                        _localAiHardware,
                        _config.LocalAi.SelectedModelId);
                if (_localAiRecoveryModelPinned && !eligibility.CanInstall)
                    hardwareReason = DescribeLocalAiUnavailable(eligibility);
            }
        }
        catch (Exception ex)
        {
            // Trace.TraceWarning is not compiled out in Release (unlike Debug.WriteLine) and its
            // default listener forwards to OutputDebugString, so this stays visible via
            // DebugView/ETW instead of silently disappearing in a packaged build.
            Trace.TraceWarning($"Local AI hardware probe failed: {ex}");
            if (_localAiAvailability.TryApplyProbeFailure(
                    checking.Generation,
                    LocalAiProbeFailureReason,
                    out var unavailableSnapshot))
            {
                ShowLocalAiProbeUnknown(unavailableSnapshot);
            }
            return;
        }

        string? wslNetworkingReason = null;
        try
        {
            WslGlobalConfigStatus networkingStatus = forceNetworkingConsent
                ? new(false, false)
                : CreateWslGlobalConfigManager().Inspect();
            if (!CanApplyLocalAiAvailability(checking.Generation, setupWindow))
                return;
            _localAiNetworkingStatus = networkingStatus;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Trace.TraceWarning($"WSL networking inspection failed: {ex}");
            wslNetworkingReason = SetupLocalization.GetString("Onboarding_LocalAi_WslConfigReadFailureReason");
        }

        if (!CanApplyLocalAiAvailability(checking.Generation, setupWindow))
            return;

        string? unavailableReason = LocalAiAvailabilityReasons.Build(
            hardwareReason,
            wslNetworkingReason);
        if (unavailableReason is not null)
        {
            if (_localAiAvailability.TryApplyUnsupported(
                    checking.Generation,
                    unavailableReason,
                    out var unavailableSnapshot))
            {
                ShowLocalAiUnavailable(unavailableSnapshot);
            }
            return;
        }

        if (!_localAiAvailability.TryApplyAvailable(checking.Generation, out var availableSnapshot))
            return;
        Debug.Assert(eligibility is not null);
        ApplyLocalAiAvailabilityChrome(availableSnapshot);
        LocalAiInstallReviewCard.Visibility = Visibility.Visible;
        LocalAiToggle.Visibility = Visibility.Visible;
        SetLocalAiOptionAvailability(isAvailable: true);
        _localAiSelectionEligible = eligibility.CanInstall;
        _config!.LocalAi.SelectedModelId ??= eligibility.Plan!.Model.Id;
        _config.LocalAi.SelectedProfileId = eligibility.Plan!.Profile.Id;
        PopulateLocalAiModels();
        _suppressLocalAiToggle = true;
        LocalAiToggle.IsOn = _config!.LocalAi.Enabled;
        _suppressLocalAiToggle = false;
        UpdateLocalAiOptions(forceNetworkingConsent);
        PublishState();
    }

    private static string LocalAiProbeFailureReason =>
        SetupLocalization.GetString("Onboarding_LocalAi_ProbeFailureReason");

    private bool CanApplyLocalAiAvailability(int generation, SetupWindow? setupWindow) =>
        _localAiAvailability.IsCurrent(generation) &&
        (_setupWindow is not null || setupWindow is null);

    private static WslGlobalConfigManager CreateWslGlobalConfigManager()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configPath = Path.Combine(profile, ".wslconfig");
        var localDataDir = SetupWindow.Active?.LocalDataDir ?? SetupContext.ResolveLocalDataDir();
        return new WslGlobalConfigManager(
            configPath,
            Path.Combine(localDataDir, "LocalAI", "network-backup"));
    }

    private void ShowLocalAiAvailabilityChecking(LocalAiSetupAvailabilitySnapshot snapshot)
    {
        ApplyLocalAiAvailabilityChrome(snapshot);
        _localAiSelectionEligible = false;
        _suppressLocalAiToggle = true;
        LocalAiToggle.IsOn = _config!.LocalAi.Enabled;
        _suppressLocalAiToggle = false;
        LocalAiToggle.Visibility = Visibility.Visible;
        LocalAiDetailsPanel.Visibility = Visibility.Collapsed;
        LocalAiInstallReviewCard.Visibility = Visibility.Visible;
        SetLocalAiOptionAvailability(
            isAvailable: false,
            SetupLocalization.GetString("Onboarding_LocalAi_CheckingHelpText"));
        RestoreLocalAiToggleAsPendingStateEscapeHatch();
        PublishState();
        UpdatePrimaryButtonState();
    }

    private void ShowLocalAiProbeUnknown(LocalAiSetupAvailabilitySnapshot snapshot)
    {
        ApplyLocalAiAvailabilityChrome(snapshot);
        _localAiSelectionEligible = false;
        _suppressLocalAiToggle = true;
        LocalAiToggle.IsOn = _config!.LocalAi.Enabled;
        _suppressLocalAiToggle = false;
        LocalAiToggle.Visibility = Visibility.Visible;
        LocalAiDetailsPanel.Visibility = Visibility.Collapsed;
        LocalAiInstallReviewCard.Visibility = Visibility.Visible;
        SetLocalAiOptionAvailability(
            isAvailable: false,
            SetupLocalization.GetString("Onboarding_LocalAi_ProbeUnknownHelpText"));
        RestoreLocalAiToggleAsPendingStateEscapeHatch();
        PublishState();
        UpdatePrimaryButtonState();
    }

    /// <summary>
    /// SetLocalAiOptionAvailability(isAvailable: false) sets LocalAiOptionContent.IsHitTestVisible
    /// to false, which suppresses pointer input for its entire subtree regardless of any
    /// descendant's own IsEnabled value; setting LocalAiToggle.IsEnabled back to true alone would
    /// not make it clickable. Availability being merely pending (Checking/ProbeUnknown), not yet a
    /// definitive result, must not remove the user's only way out, so this restores hit-testing on
    /// the shared container and re-enables just the toggle outside recovery: turning Local AI off
    /// unblocks Continue via the existing LocalAiToggle.IsOn != true branch, instead of ever
    /// letting Continue itself bypass an as-yet-undetermined WSL networking-consent requirement.
    /// Recovery requires Local AI to remain selected, so its toggle stays disabled. The other
    /// Local AI controls (model selector, consent checkbox) stay genuinely non-interactive because
    /// their own IsEnabled is still false, independent of the container's hit-testability.
    /// </summary>
    private void RestoreLocalAiToggleAsPendingStateEscapeHatch()
    {
        LocalAiOptionContent.IsHitTestVisible = true;
        LocalAiToggle.IsEnabled = !_localAiRecoveryOnly;
    }

    private void ShowLocalAiUnavailable(LocalAiSetupAvailabilitySnapshot snapshot)
    {
        _localAiSelectionEligible = false;
        _suppressLocalAiToggle = true;
        LocalAiToggle.IsOn = _config!.LocalAi.Enabled;
        _suppressLocalAiToggle = false;
        LocalAiToggle.Visibility = Visibility.Visible;
        LocalAiDetailsPanel.Visibility = Visibility.Collapsed;
        ApplyLocalAiAvailabilityChrome(snapshot);
        LocalAiInstallReviewCard.Visibility = Visibility.Visible;
        SetLocalAiOptionAvailability(isAvailable: false);
        RestoreLocalAiToggleAsPendingStateEscapeHatch();
        PublishState();
        UpdatePrimaryButtonState();
    }

    internal static string DescribeLocalAiUnavailable(LocalInferenceEligibilityResult eligibility)
    {
        LocalInferenceUnavailableReason reason = LocalInferenceEligibilityDiagnostics.GetUnavailableReason(eligibility);
        return reason.Kind switch
        {
            LocalInferenceUnavailableReasonKind.RuntimeUnavailable =>
                SetupLocalization.GetString("LocalAi_Reason_RuntimeUnavailable"),
            LocalInferenceUnavailableReasonKind.NoNvidiaGpu =>
                SetupLocalization.GetString("LocalAi_Reason_NoNvidiaGpu"),
            LocalInferenceUnavailableReasonKind.UnknownModel =>
                SetupLocalization.GetString("LocalAi_Reason_UnknownModel"),
            LocalInferenceUnavailableReasonKind.HardwareFactsIncomplete =>
                SetupLocalization.GetString("LocalAi_Reason_HardwareFactsIncomplete"),
            LocalInferenceUnavailableReasonKind.InsufficientGpuMemory =>
                SetupLocalization.Format(
                    "LocalAi_Reason_InsufficientGpuMemory",
                    reason.ModelDisplayName ?? SetupLocalization.GetString("LocalAi_Reason_UnknownModelName"),
                    FormatGigabytes(reason.RequiredGigabytes),
                    reason.DetectedGigabytes is { } detected
                        ? FormatGigabytes(detected)
                        : SetupLocalization.GetString("LocalAi_Reason_UnknownMemoryAmount")),
            LocalInferenceUnavailableReasonKind.DriverTooOld =>
                SetupLocalization.Format(
                    "LocalAi_Reason_DriverTooOld",
                    reason.DetectedDriverVersion ?? SetupLocalization.GetString("LocalAi_Reason_UnknownDriverVersion"),
                    reason.MinimumDriverVersion),
            LocalInferenceUnavailableReasonKind.CudaCapabilityTooLow =>
                SetupLocalization.GetString("LocalAi_Reason_CudaCapabilityTooLow"),
            _ => SetupLocalization.GetString("LocalAi_Reason_Generic"),
        };
    }

    private static string FormatGigabytes(double gigabytes) =>
        SetupLocalization.Format("LocalAi_Reason_GigabytesFormat", gigabytes);

    private void ApplyLocalAiAvailabilityChrome(LocalAiSetupAvailabilitySnapshot snapshot)
    {
        _localAiUnavailableReason = snapshot.Reason ?? string.Empty;
        LocalAiUnavailablePanel.Visibility =
            snapshot.IsAvailable ? Visibility.Collapsed : Visibility.Visible;
        LocalAiUnavailablePanel.Title = snapshot.Status switch
        {
            LocalAiSetupAvailabilityStatus.Checking =>
                SetupLocalization.GetString("Onboarding_LocalAi_CheckingTitle"),
            LocalAiSetupAvailabilityStatus.Unknown =>
                SetupLocalization.GetString("Onboarding_LocalAi_ProbeUnknownTitle"),
            _ => SetupLocalization.GetString("Onboarding_LocalAi_UnavailableTitle"),
        };
        LocalAiUnavailablePanel.Message = snapshot.Status switch
        {
            LocalAiSetupAvailabilityStatus.Checking =>
                SetupLocalization.GetString("Onboarding_LocalAi_CheckingMessage"),
            LocalAiSetupAvailabilityStatus.Unknown =>
                SetupLocalization.GetString("Onboarding_LocalAi_ProbeUnknownMessage"),
            _ => SetupLocalization.GetString("Onboarding_LocalAi_UnavailableMessage"),
        };
        LocalAiUnavailableDetailsButton.Visibility =
            string.IsNullOrWhiteSpace(_localAiUnavailableReason) ? Visibility.Collapsed : Visibility.Visible;
        LocalAiAvailabilityRecoveryPanel.Visibility =
            snapshot.IsChecking || snapshot.IsUnknown ? Visibility.Visible : Visibility.Collapsed;
        LocalAiAvailabilityProgressRing.IsActive = snapshot.IsChecking;
        LocalAiAvailabilityProgressRing.Visibility =
            snapshot.IsChecking ? Visibility.Visible : Visibility.Collapsed;
        LocalAiRecheckAvailabilityButton.Visibility =
            snapshot.IsUnknown ? Visibility.Visible : Visibility.Collapsed;
        LocalAiRecheckAvailabilityButton.IsEnabled = snapshot.CanRecheck;
    }

    private void SetLocalAiOptionAvailability(bool isAvailable, string? helpText = null)
    {
        LocalAiOptionContent.IsHitTestVisible = isAvailable;
        LocalAiOptionContent.Opacity = isAvailable ? 1 : 0.55;
        LocalAiToggle.IsEnabled = isAvailable && !_localAiRecoveryOnly;
        LocalAiModelSelector.IsEnabled = isAvailable && !_localAiRecoveryModelPinned;
        AutomationProperties.SetHelpText(
            LocalAiOptionContent,
            isAvailable
                ? string.Empty
                : helpText ?? SetupLocalization.GetString("Onboarding_LocalAi_UnavailableHelpText"));
    }

    private void LocalAiRecheckAvailability_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            RecheckLocalAiAvailabilityAsync,
            NullLogger.Instance,
            nameof(LocalAiRecheckAvailability_Click));

    private Task RecheckLocalAiAvailabilityAsync()
    {
        if (!_localAiAvailability.TryStartRecheck(out var snapshot))
        {
            ApplyLocalAiAvailabilityChrome(snapshot);
            return Task.CompletedTask;
        }

        return InitializeLocalAiReviewAsync(
            forceNetworkingConsent: _forceLocalAiNetworkingConsent,
            refreshHardwareProbe: true,
            startedAvailability: snapshot);
    }

    private void LocalAiUnavailableDetails_Click(object sender, RoutedEventArgs e)
    {
        LocalAiUnavailableReasonText.Text = _localAiUnavailableReason;
        LocalAiUnavailableReasonText.Visibility = Visibility.Visible;
    }

    private void Networking_Click(object sender, RoutedEventArgs e) =>
        _setupWindow?.NavigateToWslNetworking();

    private void PopulateLocalAiModels()
    {
        _suppressLocalAiSelection = true;
        LocalAiModelSelector.Items.Clear();
        int selectedIndex = 0;
        (LocalModelInfo Model, LocalInferencePlan Plan)[] fittingModels = LocalModelCatalog.Models
            .Select(model => (Model: model, Eligibility: LocalInferenceEligibility.Evaluate(_localAiHardware!, model.Id)))
            .Where(candidate => candidate.Eligibility.CanInstall && candidate.Eligibility.Plan is not null)
            .Select(candidate => (candidate.Model, candidate.Eligibility.Plan!))
            .ToArray();
        for (int index = 0; index < fittingModels.Length; index++)
        {
            (LocalModelInfo model, LocalInferencePlan plan) = fittingModels[index];
            bool isRecommended = string.Equals(
                _localAiRecommendedModelId,
                model.Id,
                StringComparison.OrdinalIgnoreCase);
            LocalAiModelSelector.Items.Add(new ComboBoxItem
            {
                Content = $"{SetupReviewSummaryBuilder.DisplayModelName(model)} " +
                    $"({FormatSize(LocalModelCatalog.TotalDownloadSizeBytes(model))}, " +
                    $"{FormatContext(plan.Profile.ContextTokens)}, " +
                    $"{LocalModelCatalog.ToDisplayCacheType(plan.Profile.KeyCachePrecision)} KV)" +
                    (isRecommended ? $" ({SetupLocalization.GetString("Onboarding_V2_Recommended")})" : string.Empty),
                Tag = model.Id,
            });
            string? selectedModelId = _config!.LocalAi.SelectedModelId ?? _localAiRecommendedModelId;
            if (string.Equals(selectedModelId, model.Id, StringComparison.OrdinalIgnoreCase))
                selectedIndex = index;
        }
        LocalAiModelSelector.SelectedIndex = selectedIndex;
        _suppressLocalAiSelection = false;
    }

    private void LocalAiToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressLocalAiToggle || _config is null)
            return;
        UpdateLocalAiOptions();
        PublishState();
    }

    private void LocalAiModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLocalAiSelection || _config is null ||
            LocalAiModelSelector.SelectedItem is not ComboBoxItem { Tag: string modelId })
        {
            return;
        }
        _config.LocalAi.SelectedModelId = modelId;
        UpdateLocalAiModelDetails();
        PublishState();
    }

    private void UpdateLocalAiOptions(bool forceNetworkingConsent = false)
    {
        var config = _config!;
        bool enabled = LocalAiToggle.IsOn == true;
        _draft!.SetLocalAiEnabled(enabled);
        LocalAiDetailsPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        LocalAiNetworkingInspectionError.Visibility = Visibility.Collapsed;
        _localAiNetworkingConsentRequired = false;

        if (!enabled)
        {
            LocalAiNetworkingConsentPanel.Visibility = Visibility.Collapsed;
            UpdatePrimaryButtonState();
            return;
        }

        UpdateLocalAiModelDetails();
        WslGlobalConfigStatus status = forceNetworkingConsent
            ? new(false, false)
            : _localAiNetworkingStatus ?? new(false, false);
        _localAiNetworkingConsentRequired = !status.IsMirrored;
        LocalAiNetworkingConsentPanel.Visibility = _localAiNetworkingConsentRequired
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdatePrimaryButtonState();
    }

    private void UpdateLocalAiModelDetails()
    {
        if (_localAiHardware is null ||
            LocalAiModelSelector.SelectedItem is not ComboBoxItem { Tag: string modelId })
        {
            return;
        }

        LocalInferenceEligibilityResult eligibility = LocalInferenceEligibility.Evaluate(_localAiHardware, modelId);
        if (eligibility.Plan is not { } plan || eligibility.SelectedGpu is not { } gpu)
        {
            _localAiSelectionEligible = false;
            _config!.LocalAi.SelectedProfileId = null;
            LocalAiHardwareStatusText.Text = SetupLocalization.GetString("Onboarding_V2_ModelNotQualified");
            UpdatePrimaryButtonState();
            return;
        }

        _localAiSelectionEligible = eligibility.CanInstall;
        _config!.LocalAi.SelectedProfileId = plan.Profile.Id;
        LocalAiHardwareStatusText.Text = eligibility.Status switch
        {
            LocalInferenceEligibilityStatus.Eligible or LocalInferenceEligibilityStatus.EligibleButBusy =>
                SetupLocalization.Format("Onboarding_V2_HardwareMemory",
                    FormatMemorySize(eligibility.RequiredTotalMemoryBytes),
                    FormatOptionalMemorySize(eligibility.DetectedTotalMemoryBytes), gpu.Name),
            _ => DescribeLocalAiUnavailable(eligibility),
        };
        LocalAiEngineDetailText.Text = SetupLocalization.Format("Onboarding_V2_EngineDownload",
            FormatSize(plan.Runtime.Artifacts.Sum(artifact => artifact.SizeBytes)));
        LocalAiModelDetailText.Text = SetupLocalization.Format("Onboarding_V2_ModelDownload",
            SetupReviewSummaryBuilder.DisplayModelName(plan.Model), FormatSize(LocalModelCatalog.TotalDownloadSizeBytes(plan.Model)));
        UpdatePrimaryButtonState();
    }

    private void UpdatePrimaryButtonState() => PublishState();

    private void PublishState()
    {
        if (_draft is null)
            return;
        _draft.LocalAiReady = _localAiSelectionEligible;
        _draft.LocalAiNetworkingConsentRequired = _localAiNetworkingConsentRequired;
        NetworkingButton.Content = SetupLocalization.GetString(
            _draft.Config.LocalAi.WslMirroredNetworkingConsent
                ? "Onboarding_V2_NetworkConsentRecorded"
                : "Onboarding_V2_ReviewNetworking");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatSize(long bytes) =>
        $"{bytes / 1_000_000_000d:0.#} GB";

    private static string FormatMemorySize(long bytes) =>
        $"{bytes / (1024d * 1024d * 1024d):0.#} GiB";

    private static string FormatOptionalMemorySize(long? bytes) =>
        bytes is { } value ? FormatMemorySize(value) : SetupLocalization.GetString("LocalAi_Reason_UnknownMemoryAmount");

    private static string FormatContext(int tokens) =>
        tokens % 1024 == 0
            ? $"{tokens / 1024}K"
            : tokens % 1000 == 0
                ? $"{tokens / 1000}K"
                : SetupLocalization.Format("Onboarding_V2_Tokens", tokens);

}
