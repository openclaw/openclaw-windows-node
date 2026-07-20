using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.Shared;
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
    private bool _skipPermissions;
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
        ApplySetupReviewSummary(_config);
        TailscaleToggle.IsOn = _config.Tailscale.Enabled;
        TailscaleTrustAuthToggle.IsOn = _config.Tailscale.TrustTailscaleAuth;
        TailscaleAuthModeSelector.SelectedIndex = _config.Tailscale.AuthMode == TailscaleAuthMode.AuthKey ? 1 : 0;
        UpdateTailscaleOptions();
        GoToStep(1);
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
        // Back is always available - from step 1 it returns to the Welcome screen.
        BackButton.Visibility = Visibility.Visible;

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
        // before any step that reads their results - step 2's rows and step 3's summary -
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
            // First capability step - step back to the Welcome screen.
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
