using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.SetupEngine.UI;
using System.Diagnostics;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class CapabilitiesPage : Page
{
    private SetupConfig? _config;
    private readonly Dictionary<string, ToggleSwitch> _toggles = new();
    private readonly Dictionary<string, FrameworkElement> _permRows = new();
    private readonly Dictionary<string, bool> _permGranted = new();
    private SetupWindow? _setupWindow;
    private Task? _permissionsTask;
    private bool _suppressProfile;
    private bool _suppressLocalAiToggle;
    private bool _suppressLocalAiSelection;
    private bool _suppressLocalAiConsent;
    private bool _skipPermissions;
    private bool _skipWizardWithoutLocalAi;
    private bool _localAiSelectionEligible;
    private bool _localAiNetworkingConsentRequired;
    private HostHardwareInfo? _localAiHardware;
    private string? _localAiRecommendedModelId;
    private long? _localAiSelectedGpuCapacityBytes;
    private WslGlobalConfigStatus? _localAiNetworkingStatus;
    private string _localAiUnavailableReason = string.Empty;
    private bool _treatBundledAllOnAsPlaceholder;
    private int _step = 1;

    // Capability profiles preset only runtime-gated settings. Device info/status
    // stays available whenever Node Mode is enabled, so it is disclosed but not selectable.
    private static readonly string[] ProfileReadOnly = ["Canvas", "Screen"];
    private static readonly string[] ProfileStandard = ["System", "Canvas", "Screen", "Tts", "Stt"];

    // (config property, display name, description, fluent icon glyph)
    private static readonly (string Key, string Name, string Desc, string Glyph)[] Capabilities =
    [
        ("System", "System", "Shell commands, files, clipboard", "\uE756"),
        ("Canvas", "Canvas", "Whiteboard and annotations", "\uE790"),
        ("Screen", "Screen capture", "Screenshots and recording", "\uE7F4"),
        ("Camera", "Camera", "Webcam photos and video", "\uE722"),
        ("Location", "Location", "Share device location", "\uE81D"),
        ("Browser", "Browser", "Web navigation and automation", "\uE774"),
        ("Tts", "Text-to-speech", "Speak text aloud", "\uE767"),
        ("Stt", "Speech-to-text", "Transcribe spoken audio", "\uE720"),
    ];

    // Which capability requires which Windows permission (for the inline step-2 rows).
    private static readonly (string CapKey, string PermId)[] CapPermMap =
    [
        ("Camera", "Camera"),
        ("Stt", "Microphone"),
        ("Location", "Location"),
        ("Screen", "Screen"),
    ];

    public CapabilitiesPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
        // The tray always registers device.info/status with Node Mode. Keep the
        // setup declaration and gateway allowlist aligned with that runtime contract.
        _config.Capabilities.Device = true;
        _skipPermissions = _config.SkipPermissions;
        _skipWizardWithoutLocalAi = _config.SkipWizard;
        _treatBundledAllOnAsPlaceholder = _config.UsesBundledDefaultConfig;
        BuildToggles();
        _suppressProfile = true;
        var profileIndex = DetectProfileIndex();
        ProfileRadio.SelectedIndex = profileIndex;
        UpdateCapabilityProfilePresentation(profileIndex);
        // BuildToggles() seeded the toggles from the config. The bundled
        // default-config.json still ships with every capability on as a
        // placeholder, so default that implicit case to Standard. Explicit
        // custom configs are preserved even when they do not match a preset.
        if (_config.UsesBundledDefaultConfig && profileIndex == 1 && !MatchesProfile(ProfileStandard))
            ApplyProfile(1);
        _suppressProfile = false;
        _treatBundledAllOnAsPlaceholder = false;
        // Only probe OS permissions when the permissions step will actually be shown.
        if (!_skipPermissions)
            _permissionsTask = BuildPermissionRows();
        _setupWindow = SetupWindow.Active;
        if (_setupWindow is not null)
            _setupWindow.Activated += SetupWindow_Activated;
        TailscaleToggle.IsOn = _config.Tailscale.Enabled;
        TailscaleTrustAuthToggle.IsOn = _config.Tailscale.TrustTailscaleAuth;
        TailscaleAuthModeSelector.SelectedIndex = _config.Tailscale.AuthMode == TailscaleAuthMode.AuthKey ? 1 : 0;
        UpdateTailscaleOptions();
        var previewPage = SetupPreview.RequestedPage;
        var localAiReviewPreview = previewPage is "capabilities-review" or "capabilities-review-consent";
        if (localAiReviewPreview)
            _config.LocalAi.Enabled = true;
        AsyncEventHandlerGuard.Run(
            () => InitializeLocalAiReviewAsync(
                forceNetworkingConsent: previewPage == "capabilities-review-consent"),
            NullLogger.Instance,
            nameof(InitializeLocalAiReviewAsync));
        ApplySetupReviewSummary(_config);
        GoToStep(localAiReviewPreview ? 3 : 1);
        if (localAiReviewPreview)
            DispatcherQueue.TryEnqueue(() => Scroller.ChangeView(null, 0, null, disableAnimation: true));
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_setupWindow is not null)
        {
            _setupWindow.Activated -= SetupWindow_Activated;
            _setupWindow = null;
        }
        base.OnNavigatedFrom(e);
    }

    private void SetupWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        if (_skipPermissions || e.WindowActivationState == WindowActivationState.Deactivated)
            return;

        // Settings opens outside the setup window. Refresh when focus returns so the
        // status rows and completion summary immediately reflect the user's changes.
        _permissionsTask = RefreshPermissionRowsAsync(_permissionsTask);
    }

    private async Task RefreshPermissionRowsAsync(Task? previousRefresh)
    {
        if (previousRefresh is not null)
            await previousRefresh;
        await BuildPermissionRows();
    }

    // ── Stepped flow (mirrors the gateway onboard transcript) ──

    // The permissions step (internal step 2) is hidden when SetupConfig.SkipPermissions
    // is set, so the flow is 2 visible steps instead of 3. Internal step ids stay 1/2/3;
    // navigation routes around step 2 when it is hidden.

    private void GoToStep(int step)
    {
        _step = step;
        Step1Content.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Content.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Content.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;

        StepTitle.Text = step switch
        {
            1 => "What should your agent be able to do?",
            2 => "Windows permissions",
            _ => "What setup will install on this PC",
        };
        PrimaryButton.Content = step == 3 ? "Install & set up" : "Next";
        // Back is always available — from step 1 it returns to the Welcome screen.
        BackButton.Visibility = Visibility.Visible;
        UpdatePrimaryButtonState();

        ScrollActiveIntoView();
    }

    private void Primary_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            PrimaryClickAsync,
            NullLogger.Instance,
            nameof(Primary_Click));

    private async Task PrimaryClickAsync()
    {
        // The Windows-permission checks run on entry as a background task. They are fast
        // local reads (registry / device enumeration), but make sure they have finished
        // before any step that reads their results — step 2's rows and step 3's summary —
        // so a fast click-through can't render empty rows or an undercounted summary.
        if (_permissionsTask is { } permissionsTask && !permissionsTask.IsCompletedSuccessfully)
        {
            PrimaryButton.IsEnabled = false;
            try { await permissionsTask; }
            finally { PrimaryButton.IsEnabled = true; }
        }

        switch (_step)
        {
            case 1:
                AppendTranscript("What your agent can do", ProfileSummary());
                GoToStep(_skipPermissions ? 3 : 2);
                break;
            case 2:
                AppendTranscript("Windows permissions", PermissionSummary());
                GoToStep(3);
                break;
            default:
                WriteCapabilities();
                SetupWindow.Active?.NavigateToProgress();
                break;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step <= 1)
        {
            // First capability step — step back to the Welcome screen.
            SetupWindow.Active?.NavigateToWelcome(back: true);
            return;
        }
        if (Transcript.Children.Count > 0)
            Transcript.Children.RemoveAt(Transcript.Children.Count - 1);
        // Skip back over the hidden permissions step when permissions are skipped.
        var previous = _step == 3 && _skipPermissions ? 1 : _step - 1;
        GoToStep(previous);
    }

    private void WriteCapabilities()
    {
        var config = _config!;
        var caps = config.Capabilities;
        foreach (var (key, _, _, _) in Capabilities)
        {
            if (_toggles.TryGetValue(key, out var toggle))
            {
                var prop = typeof(CapabilitiesConfig).GetProperty(key);
                prop?.SetValue(caps, toggle.IsOn);
            }
        }
        config.Settings.ApplyCapabilities(caps);
        config.Tailscale.Enabled = TailscaleToggle.IsOn == true;
        config.Tailscale.TrustTailscaleAuth = TailscaleTrustAuthToggle.IsOn == true;
        config.Tailscale.AuthMode = TailscaleAuthModeSelector.SelectedIndex == 1
            ? TailscaleAuthMode.AuthKey
            : TailscaleAuthMode.Browser;
        config.Tailscale.AuthKey = config.Tailscale.AuthMode == TailscaleAuthMode.AuthKey
            ? TailscaleAuthKeyBox.Password
            : null;
        config.LocalAi.Enabled = LocalAiToggle.IsOn == true;
        config.SkipWizard = config.LocalAi.Enabled || _skipWizardWithoutLocalAi;
        config.LocalAi.WslMirroredNetworkingConsent =
            config.LocalAi.Enabled &&
            _localAiNetworkingConsentRequired &&
            LocalAiNetworkingConsentCheckBox.IsChecked == true;
    }

    private void ApplySetupReviewSummary(SetupConfig config)
    {
        var summary = SetupReviewSummaryBuilder.Build(
            config,
            SetupWindow.Active?.DataDir,
            SetupWindow.Active?.LocalDataDir);
        InstallDistroTitleText.Text = summary.DistroTitle;
        InstallDistroDetailText.Text = summary.DistroDescription;
        InstallCliDetailText.Text = summary.InstallerDescription;
        InstallCliBadgeText.Text = summary.InstallerBadge;
        GatewayServiceDetailText.Text = summary.GatewayDescription;
        GatewayEndpointText.Text = summary.GatewayEndpoint;
        ExactCommandsText.Text = summary.ExactCommands;
    }

    private async Task InitializeLocalAiReviewAsync(bool forceNetworkingConsent)
    {
        SetupWindow? setupWindow = _setupWindow;
        Task<HostHardwareInfo> hardwareTask = setupWindow is not null
            ? setupWindow.GetLocalAiHardwareAsync()
            : Task.Run(() => new NvmlHostHardwareProbe().Probe());
        Task<WslViabilityResult> wslTask = setupWindow is not null
            ? setupWindow.GetWslViabilityAsync()
            : InspectWslViabilityAsync();

        string? hardwareReason = null;
        LocalInferenceEligibilityResult? eligibility = null;
        try
        {
            _localAiHardware = await hardwareTask;
            eligibility = LocalInferenceEligibility.Evaluate(
                _localAiHardware,
                _config!.LocalAi.SelectedModelId);
            _localAiSelectedGpuCapacityBytes = eligibility.DetectedTotalMemoryBytes;
            _localAiRecommendedModelId = LocalModelCatalog.Models
                .OrderByDescending(model => model.Weights.SizeBytes)
                .FirstOrDefault(model =>
                    _localAiSelectedGpuCapacityBytes is { } capacityBytes &&
                    LocalInferenceEligibility.GetRequiredMemoryBytes(model) <= capacityBytes)?.Id;
            if (!eligibility.CanInstall || eligibility.Plan is null || eligibility.SelectedGpu is null)
                hardwareReason = DescribeLocalAiUnavailable(eligibility);
        }
        catch
        {
            hardwareReason =
                "OpenClaw could not read the NVIDIA GPU, driver, CUDA, or memory information. " +
                "Check the NVIDIA driver installation and try setup again.";
        }

        WslViabilityResult wslViability;
        try
        {
            wslViability = await wslTask;
        }
        catch
        {
            wslViability = new(
                WslViabilityKind.InspectionFailed,
                "OpenClaw could not safely verify the WSL2 environment.",
                "Run wsl --status in PowerShell, resolve the reported problem, and try setup again.");
        }

        string? wslNetworkingReason = null;
        try
        {
            _localAiNetworkingStatus = forceNetworkingConsent
                ? new(false, false)
                : CreateWslGlobalConfigManager().Inspect();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Debug.WriteLine($"WSL networking inspection failed: {ex}");
            wslNetworkingReason =
                "OpenClaw cannot safely read the global .wslconfig file. " +
                "Check that the file is valid and readable, then try setup again.";
        }

        if (_setupWindow is null && setupWindow is not null)
            return;

        string? unavailableReason = LocalAiAvailabilityReasons.Build(
            hardwareReason,
            wslViability,
            wslNetworkingReason);
        if (unavailableReason is not null)
        {
            ShowLocalAiUnavailable(unavailableReason);
            return;
        }

        Debug.Assert(eligibility is not null);
        LocalAiInstallReviewCard.Visibility = Visibility.Visible;
        LocalAiUnavailablePanel.Visibility = Visibility.Collapsed;
        LocalAiToggle.Visibility = Visibility.Visible;
        _localAiSelectionEligible = eligibility.Status == LocalInferenceEligibilityStatus.Eligible;
        _config!.LocalAi.SelectedModelId ??= eligibility.Plan!.Model.Id;
        PopulateLocalAiModels();
        _suppressLocalAiToggle = true;
        LocalAiToggle.IsOn = _config!.LocalAi.Enabled;
        _suppressLocalAiToggle = false;
        UpdateLocalAiOptions(forceNetworkingConsent);
        ApplySetupReviewSummary(_config);
    }

    private static async Task<WslViabilityResult> InspectWslViabilityAsync()
    {
        using var logger = new SetupLogger(filePath: null);
        return await WslViabilityInspector.InspectAsync(
            new CommandRunner(logger),
            logger,
            CancellationToken.None);
    }

    private static WslGlobalConfigManager CreateWslGlobalConfigManager()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configPath = Path.Combine(profile, ".wslconfig");
        var localDataDir = SetupWindow.Active?.LocalDataDir ?? SetupContext.ResolveLocalDataDir();
        return new WslGlobalConfigManager(
            configPath,
            Path.Combine(localDataDir, "LocalAI", "network-backup"));
    }

    private void ShowLocalAiUnavailable(string reason)
    {
        _localAiSelectionEligible = false;
        _suppressLocalAiToggle = true;
        LocalAiToggle.IsOn = false;
        _suppressLocalAiToggle = false;
        LocalAiToggle.Visibility = Visibility.Collapsed;
        LocalAiDetailsPanel.Visibility = Visibility.Collapsed;
        _localAiUnavailableReason = reason;
        LocalAiUnavailablePanel.Visibility = Visibility.Visible;
        LocalAiInstallReviewCard.Visibility = Visibility.Visible;
        _config!.LocalAi.Enabled = false;
        _config.SkipWizard = _skipWizardWithoutLocalAi;
        ApplySetupReviewSummary(_config);
    }

    private static string DescribeLocalAiUnavailable(LocalInferenceEligibilityResult eligibility) =>
        eligibility.SelectionFailureCode switch
        {
            LocalInferenceSelectionFailureCode.RuntimeUnavailable =>
                "This Local AI release does not include a native llama-server runtime for the detected Windows architecture.",
            LocalInferenceSelectionFailureCode.NoNvidiaGpu =>
                "No NVIDIA GPU was reported by the NVIDIA driver. Install or repair the NVIDIA driver, then try setup again.",
            LocalInferenceSelectionFailureCode.UnknownModel =>
                "The selected model is not available in this Local AI release.",
            _ => eligibility.FailureCode switch
            {
                LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete =>
                    "OpenClaw could not read a stable NVIDIA GPU identifier, memory, driver, or CUDA capability.",
                LocalInferenceEligibilityFailureCode.InsufficientGpuMemory =>
                    $"{eligibility.Plan?.Model.DisplayName ?? "The selected model"} requires " +
                    $"{FormatSize(eligibility.RequiredTotalMemoryBytes)} of GPU memory for model weights, KV cache, and runtime workspace. " +
                    $"OpenClaw detected {FormatOptionalSize(eligibility.DetectedTotalMemoryBytes)}.",
                LocalInferenceEligibilityFailureCode.DriverTooOld =>
                    $"NVIDIA driver {eligibility.SelectedGpu?.DriverVersion ?? "unknown"} was detected. " +
                    $"Local AI requires version {LocalInferenceEligibility.MinimumNvidiaDriverVersion} or newer.",
                LocalInferenceEligibilityFailureCode.CudaCapabilityTooLow =>
                    "The NVIDIA driver does not provide CUDA 13 support. A separate CUDA Toolkit is not required.",
                _ => "OpenClaw could not verify the Local AI requirements on this system.",
            },
        };

    private void LocalAiUnavailableDetails_Click(object sender, RoutedEventArgs e) =>
        AsyncEventHandlerGuard.Run(
            ShowLocalAiUnavailableDetailsAsync,
            NullLogger.Instance,
            nameof(LocalAiUnavailableDetails_Click));

    private async Task ShowLocalAiUnavailableDetailsAsync()
    {
        var xamlRoot = LocalAiInstallReviewCard.XamlRoot;
        if (xamlRoot is null)
            return;

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Why Local AI is unavailable",
            Content = new TextBlock
            {
                Text = _localAiUnavailableReason,
                TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = "Close",
        };
        await dialog.ShowAsync();
    }

    private void PopulateLocalAiModels()
    {
        _suppressLocalAiSelection = true;
        LocalAiModelSelector.Items.Clear();
        int selectedIndex = 0;
        LocalModelInfo[] fittingModels = LocalModelCatalog.Models
            .Where(model =>
                _localAiSelectedGpuCapacityBytes is { } capacityBytes &&
                LocalInferenceEligibility.GetRequiredMemoryBytes(model) <= capacityBytes)
            .ToArray();
        for (int index = 0; index < fittingModels.Length; index++)
        {
            LocalModelInfo model = fittingModels[index];
            bool isRecommended = string.Equals(
                _localAiRecommendedModelId,
                model.Id,
                StringComparison.OrdinalIgnoreCase);
            LocalAiModelSelector.Items.Add(new ComboBoxItem
            {
                Content = $"{model.DisplayName} ({FormatSize(model.Weights.SizeBytes)})" +
                    (isRecommended ? " - Recommended" : string.Empty),
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
        ApplySetupReviewSummary(_config);
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
        ApplySetupReviewSummary(_config);
    }

    private void LocalAiNetworkingConsent_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressLocalAiConsent || _config is null)
            return;
        _config.LocalAi.WslMirroredNetworkingConsent =
            LocalAiToggle.IsOn == true &&
            _localAiNetworkingConsentRequired &&
            LocalAiNetworkingConsentCheckBox.IsChecked == true;
        UpdatePrimaryButtonState();
    }

    private void UpdateLocalAiOptions(bool forceNetworkingConsent = false)
    {
        var config = _config!;
        bool enabled = LocalAiToggle.IsOn == true;
        config.LocalAi.Enabled = enabled;
        config.SkipWizard = enabled || _skipWizardWithoutLocalAi;
        LocalAiDetailsPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        LocalAiNetworkingInspectionError.Visibility = Visibility.Collapsed;
        _localAiNetworkingConsentRequired = false;

        if (!enabled)
        {
            LocalAiNetworkingConsentPanel.Visibility = Visibility.Collapsed;
            SetLocalAiNetworkingConsent(false);
            config.LocalAi.WslMirroredNetworkingConsent = false;
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
        SetLocalAiNetworkingConsent(false);
        config.LocalAi.WslMirroredNetworkingConsent = false;
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
            LocalAiHardwareStatusText.Text = "This model is not qualified for the detected hardware.";
            UpdatePrimaryButtonState();
            return;
        }

        _localAiSelectionEligible = eligibility.Status == LocalInferenceEligibilityStatus.Eligible;
        LocalAiHardwareStatusText.Text = eligibility.Status switch
        {
            LocalInferenceEligibilityStatus.Eligible =>
                $"Detected {gpu.Name} with {FormatOptionalSize(eligibility.DetectedTotalMemoryBytes)}. " +
                $"The selected model requires {FormatSize(eligibility.RequiredTotalMemoryBytes)}.",
            LocalInferenceEligibilityStatus.EligibleButBusy =>
                $"Detected {gpu.Name}, but only {FormatOptionalSize(eligibility.AvailableFreeMemoryBytes)} of " +
                $"{FormatSize(eligibility.RequiredFreeMemoryBytes)} required GPU memory is currently free. " +
                "Close GPU applications and retry setup.",
            _ => DescribeLocalAiUnavailable(eligibility),
        };
        LocalAiEngineDetailText.Text =
            "llama-server for Windows; " +
            $"{FormatSize(plan.Runtime.Artifacts.Sum(artifact => artifact.SizeBytes))} verified download";
        LocalAiModelDetailText.Text =
            $"{plan.Model.DisplayName}, {FormatSize(plan.Model.Weights.SizeBytes)} from Hugging Face";
        LocalAiSettingsDetailText.Text =
            $"{plan.Model.Recipe.ContextTokens / 1024}K context, FP16 KV cache, full CUDA offload, loads on first request";
        UpdatePrimaryButtonState();
    }

    private void SetLocalAiNetworkingConsent(bool value)
    {
        _suppressLocalAiConsent = true;
        LocalAiNetworkingConsentCheckBox.IsChecked = value;
        _suppressLocalAiConsent = false;
    }

    private void UpdatePrimaryButtonState()
    {
        PrimaryButton.IsEnabled =
            _step != 3 ||
            LocalAiToggle.IsOn != true ||
            (_localAiSelectionEligible &&
             (!_localAiNetworkingConsentRequired || LocalAiNetworkingConsentCheckBox.IsChecked == true));
    }

    private static string FormatSize(long bytes) =>
        $"{bytes / 1_000_000_000d:0.#} GB";

    private static string FormatOptionalSize(long? bytes) =>
        bytes is { } value ? FormatSize(value) : "an unknown amount";

    private void TailscaleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        UpdateTailscaleOptions();
        if (_config is not null)
        {
            _config.Tailscale.Enabled = TailscaleToggle.IsOn == true;
            ApplySetupReviewSummary(_config);
        }
    }

    private void TailscaleAuthMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        TailscaleAuthKeyBox.Visibility = TailscaleAuthModeSelector.SelectedIndex == 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void TailscaleTrustAuthToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_config is null)
            return;

        _config.Tailscale.TrustTailscaleAuth = TailscaleTrustAuthToggle.IsOn == true;
        ApplySetupReviewSummary(_config);
    }

    private void UpdateTailscaleOptions()
    {
        var enabled = TailscaleToggle.IsOn == true;
        TailscaleOptions.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        TailscaleAuthKeyBox.Visibility = enabled && TailscaleAuthModeSelector.SelectedIndex == 1
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (enabled)
            _ = RefreshWindowsTailscaleStatusAsync();
    }

    private async Task RefreshWindowsTailscaleStatusAsync()
    {
        TailscaleStatusText.Text = "Checking Windows Tailscale…";
        try
        {
            var path = PreflightWindowsTailscaleStep.ResolveWindowsTailscaleCliPath();
            var result = await Task.Run(() =>
            {
                var psi = new ProcessStartInfo(path, "status --json")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi);
                if (process is null) return (ExitCode: -1, Output: string.Empty);
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                return (ExitCode: process.ExitCode, Output: output);
            });
            string? dnsName = null;
            string? tailnetDnsSuffix = null;
            if (result.ExitCode == 0 &&
                TailscaleSetupPolicy.TryParseStatus(result.Output, out var status) &&
                status.IsRunning)
            {
                dnsName = status.DnsName;
                tailnetDnsSuffix = TailscaleSetupPolicy.GetTailnetDnsSuffix(dnsName);
            }
            TailscaleStatusText.Text = tailnetDnsSuffix is not null
                ? $"Windows Tailscale connected as {dnsName}."
                : "Windows Tailscale must be installed and signed in before setup can continue.";
            if (_config is not null && TailscaleToggle.IsOn == true)
            {
                _config.Tailscale.TailnetDnsSuffix = tailnetDnsSuffix;
                ApplySetupReviewSummary(_config);
            }
        }
        catch
        {
            TailscaleStatusText.Text = "Windows Tailscale must be installed and signed in before setup can continue.";
            if (_config is not null && TailscaleToggle.IsOn == true)
            {
                _config.Tailscale.TailnetDnsSuffix = null;
                ApplySetupReviewSummary(_config);
            }
        }
    }

    private string ProfileSummary()
    {
        if (MatchesProfile(ProfileReadOnly)) return "Read-only";
        if (MatchesProfile(ProfileStandard)) return "Standard";
        if (MatchesProfile(Capabilities.Select(c => c.Key).ToArray())) return "Full access";
        var n = _toggles.Values.Count(t => t.IsOn);
        return $"{n} of {Capabilities.Length} capabilities";
    }

    private string PermissionSummary()
    {
        var visible = 1; // Notifications always shown
        var granted = _permGranted.TryGetValue("Notifications", out var ng) && ng ? 1 : 0;
        foreach (var (capKey, permId) in CapPermMap)
        {
            if (!IsCapOn(capKey))
                continue;
            visible++;
            if (_permGranted.TryGetValue(permId, out var g) && g)
                granted++;
        }
        return granted == visible ? $"All {visible} granted" : $"{granted} of {visible} granted";
    }

    private void AppendTranscript(string question, string? answer)
    {
        var grid = new Grid { Padding = new Thickness(2, 6, 2, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = SetupPermissionHelper.Res("SystemFillColorSuccessBrush"),
            Margin = new Thickness(0, 1, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 11,
                Foreground = new SolidColorBrush(Colors.White),
            },
        };

        var stack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = question,
            FontSize = 14,
            Foreground = SetupPermissionHelper.Res("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(answer))
        {
            stack.Children.Add(new TextBlock
            {
                Text = answer,
                FontSize = 13,
                Foreground = SetupPermissionHelper.Res("TextFillColorPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        Grid.SetColumn(dot, 0);
        Grid.SetColumn(stack, 1);
        grid.Children.Add(dot);
        grid.Children.Add(stack);
        Transcript.Children.Add(grid);
    }

    private void ScrollActiveIntoView()
    {
        Scroller.UpdateLayout();
        Scroller.ChangeView(null, Scroller.ScrollableHeight, null);
    }

    // ── Capability toggles ──

    private void BuildToggles()
    {
        var caps = _config!.Capabilities;
        var totalRows = (Capabilities.Length + 1) / 2; // ceiling division for 2 columns

        for (int i = 0; i < totalRows; i++)
            CapGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < Capabilities.Length; i++)
        {
            var (key, name, desc, glyph) = Capabilities[i];
            var prop = typeof(CapabilitiesConfig).GetProperty(key);
            var isEnabled = (bool)(prop?.GetValue(caps) ?? true);

            var toggle = new ToggleSwitch
            {
                IsOn = isEnabled,
                OnContent = "",
                OffContent = "",
                MinWidth = 0,
            };
            _toggles[key] = toggle;
            toggle.Toggled += Capability_Toggled;

            var item = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
                Padding = new Thickness(10, 12, 6, 12),
            };

            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = IconFonts.SymbolThemeFontFamily,
                FontSize = 20,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                Opacity = 0.85,
            };

            var textStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            textStack.Children.Add(new TextBlock { Text = desc, FontSize = 11, Opacity = 0.55 });

            Grid.SetColumn(icon, 0);
            Grid.SetColumn(textStack, 1);
            Grid.SetColumn(toggle, 2);
            item.Children.Add(icon);
            item.Children.Add(textStack);
            item.Children.Add(toggle);

            int row = i / 2;
            int col = i % 2;
            Grid.SetRow(item, row);
            Grid.SetColumn(item, col);
            CapGrid.Children.Add(item);
        }
    }

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProfile || _toggles.Count == 0)
            return;

        _suppressProfile = true;
        try
        {
            ApplyProfile(ProfileRadio.SelectedIndex);
            UpdateCapabilityProfilePresentation(ProfileRadio.SelectedIndex);
        }
        finally
        {
            _suppressProfile = false;
        }
    }

    private void Capability_Toggled(object sender, RoutedEventArgs e)
    {
        UpdatePermissionVisibility();
        if (_suppressProfile)
            return;

        var profileIndex = DetectProfileIndex();
        _suppressProfile = true;
        try
        {
            ProfileRadio.SelectedIndex = profileIndex;
            UpdateCapabilityProfilePresentation(profileIndex);
        }
        finally
        {
            _suppressProfile = false;
        }
    }

    private void UpdateCapabilityProfilePresentation(int profileIndex)
    {
        CapabilityExpander.Header = profileIndex < 0
            ? "Custom capabilities (review)"
            : "Fine-tune individual capabilities (optional)";
        if (profileIndex < 0)
            CapabilityExpander.IsExpanded = true;
    }

    // Turns the capability toggles on/off to match a profile index (0=Read-only,
    // 1=Standard, 2=Full access). Shared by the radio handler and the default-on-entry path.
    private void ApplyProfile(int index)
    {
        var on = index switch
        {
            0 => ProfileReadOnly,
            1 => ProfileStandard,
            _ => Capabilities.Select(c => c.Key).ToArray(), // Full access
        };
        var onSet = new HashSet<string>(on);
        foreach (var (key, _, _, _) in Capabilities)
            if (_toggles.TryGetValue(key, out var toggle))
                toggle.IsOn = onSet.Contains(key);
    }

    private int DetectProfileIndex()
    {
        if (MatchesProfile(ProfileReadOnly)) return 0;
        if (MatchesProfile(ProfileStandard)) return 1;
        if (MatchesProfile(Capabilities.Select(c => c.Key).ToArray()))
            return _treatBundledAllOnAsPlaceholder ? 1 : 2;

        // An "all capabilities on" bundled config is the shipped placeholder
        // default, not a deliberate Full-access choice, so new users default to
        // Standard (recommended). Every other non-preset set is explicit and must
        // remain visibly custom, including edits made during bundled setup.
        return -1;
    }

    private bool MatchesProfile(string[] onKeys)
    {
        var onSet = new HashSet<string>(onKeys);
        foreach (var (key, _, _, _) in Capabilities)
        {
            if (!_toggles.TryGetValue(key, out var toggle) || toggle.IsOn != onSet.Contains(key))
                return false;
        }
        return true;
    }

    // ── Windows permissions (merged inline from the old standalone step) ──

    private async Task BuildPermissionRows()
    {
        try
        {
            PermRows.Children.Clear();
            _permRows.Clear();
            _permGranted.Clear();
            foreach (var perm in SetupPermissionHelper.All)
            {
                var (status, granted) = await perm.Check();
                _permGranted[perm.Id] = granted;
                var row = SetupPermissionHelper.BuildRow(perm, status, granted);
                _permRows[perm.Id] = row;
                PermRows.Children.Add(row);
            }
            UpdatePermissionVisibility();
        }
        catch (Exception ex)
        {
            PermRows.Children.Clear();
            _permRows.Clear();
            _permGranted.Clear();
            PermRows.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Warning,
                IsOpen = true,
                IsClosable = false,
                Title = "Couldn't read Windows permission status",
                Message = $"You can continue setup. Review permissions later in Settings. Details: {ex.Message}",
            });
        }
    }

    private void UpdatePermissionVisibility()
    {
        if (_permRows.Count == 0)
            return;
        foreach (var (capKey, permId) in CapPermMap)
            SetPermVisible(permId, IsCapOn(capKey));
        // Notifications is always visible (app-level, not tied to a capability toggle).
    }

    private bool IsCapOn(string key) => _toggles.TryGetValue(key, out var t) && t.IsOn;

    private void SetPermVisible(string id, bool visible)
    {
        if (_permRows.TryGetValue(id, out var row))
            row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
}
