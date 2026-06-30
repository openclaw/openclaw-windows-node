using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.Shared;
using OpenClaw.SetupEngine.UI;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class CapabilitiesPage : Page
{
    private SetupConfig? _config;
    private readonly Dictionary<string, ToggleSwitch> _toggles = new();
    private readonly Dictionary<string, FrameworkElement> _permRows = new();
    private readonly Dictionary<string, bool> _permGranted = new();
    private Task? _permissionsTask;
    private bool _suppressProfile;
    private bool _skipPermissions;
    private int _step = 1;

    // Capability profiles just preset the granular toggles below. Keys map 1:1 to
    // CapabilitiesConfig — Full = every key on.
    private static readonly string[] ProfileReadOnly = ["Canvas", "Screen", "Device"];
    private static readonly string[] ProfileStandard = ["System", "Canvas", "Screen", "Device", "Tts", "Stt"];

    // (config property, display name, description, fluent icon glyph)
    private static readonly (string Key, string Name, string Desc, string Glyph)[] Capabilities =
    [
        ("System", "System", "Shell commands, files, clipboard", "\uE756"),
        ("Canvas", "Canvas", "Whiteboard and annotations", "\uE790"),
        ("Screen", "Screen capture", "Screenshots and recording", "\uE7F4"),
        ("Camera", "Camera", "Webcam photos and video", "\uE722"),
        ("Location", "Location", "Share device location", "\uE81D"),
        ("Browser", "Browser", "Web navigation and automation", "\uE774"),
        ("Device", "Device", "Volume, brightness, system info", "\uE772"),
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
        _skipPermissions = _config.SkipPermissions;
        BuildToggles();
        _suppressProfile = true;
        var profileIndex = DetectProfileIndex();
        ProfileRadio.SelectedIndex = profileIndex;
        _suppressProfile = false;
        // BuildToggles() seeded the toggles from the config (the shipped default has every
        // capability on). When we fall back to the recommended Standard default — i.e. the
        // config didn't exactly match Read-only or Standard — apply Standard so the toggles
        // (and the capabilities we install) match the selected radio instead of staying Full.
        if (profileIndex == 1 && !MatchesProfile(ProfileStandard))
            ApplyProfile(1);
        // Only probe OS permissions when the permissions step will actually be shown.
        if (!_skipPermissions)
            _permissionsTask = BuildPermissionRows();
        GoToStep(1);
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
        if (_permissionsTask is { IsCompleted: false })
        {
            PrimaryButton.IsEnabled = false;
            try { await _permissionsTask; }
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
        var caps = _config!.Capabilities;
        foreach (var (key, _, _, _) in Capabilities)
        {
            if (_toggles.TryGetValue(key, out var toggle))
            {
                var prop = typeof(CapabilitiesConfig).GetProperty(key);
                prop?.SetValue(caps, toggle.IsOn);
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
            toggle.Toggled += (_, _) => UpdatePermissionVisibility();

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

        ApplyProfile(ProfileRadio.SelectedIndex);
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
        // An "all capabilities on" config is the shipped placeholder default, not a
        // deliberate Full-access choice, so we don't auto-select Full here. New users
        // default to Standard (recommended) — the least-surprising, safer starting point.
        return 1;
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
