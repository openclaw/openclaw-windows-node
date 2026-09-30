using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OpenClawTray.Helpers;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class CapabilitiesPage : Page
{
    private SetupAccessDraft? _draft;
    private bool _applying;
    private readonly Dictionary<SetupCapability, ToggleSwitch> _toggles = new();
    private SettingsCard? _browserCard;

    public CapabilitiesPage()
    {
        InitializeComponent();
        AutomationProperties.SetName(FineTuneExpander, SetupLocalization.GetString("Onboarding_V3_FineTune.Text"));
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _draft = e.Parameter as SetupAccessDraft ?? SetupWindow.Active?.AccessDraft;
        if (_draft is null) return;
        var nativeInstall = _draft.Route == SetupGatewayRoute.Native;
        NativeInstallConsent.Visibility = nativeInstall ? Visibility.Visible : Visibility.Collapsed;
        ContinueButton.Content = SetupLocalization.GetString(nativeInstall
            ? "Onboarding_Native_Start.Content" : "Onboarding_V2_Next.Content");
        _applying = true;
        NodeModeToggle.IsOn = _draft.Config.Settings.EnableNodeMode;
        McpToggle.IsOn = _draft.Config.Settings.EnableMcpServer == true;
        OllamaToggle.IsOn = _draft.Config.Settings.NodeOllamaInferenceEnabled == true;
        var window = SetupWindow.Active;
        StartupPreferenceRow.Visibility = window?.ShowStartupPreference == true &&
            _draft.Route != SetupGatewayRoute.ManagedWsl
            ? Visibility.Visible : Visibility.Collapsed;
        StartupPreferenceToggle.IsOn = window?.AutoStartAfterSetup == true;
        BuildCapabilityRows();
        ApplyPresentation();
        _applying = false;
    }

    private void BuildCapabilityRows()
    {
        FineTuneExpander.Items.Clear();
        _toggles.Clear();
        foreach (var capability in SetupCapabilityProfiles.Ordered)
        {
            var resourceName = capability == SetupCapability.System ? "SystemRun" : capability.ToString();
            var label = SetupLocalization.GetString($"PermissionsPage_Cap_{resourceName}_Label");
            var toggle = new ToggleSwitch { IsOn = _draft!.GetCapability(capability), OnContent = "", OffContent = "", MinWidth = 0 };
            AutomationProperties.SetName(toggle, label);
            AutomationProperties.SetAutomationId(toggle, $"SetupCapability{capability}");
            toggle.Toggled += (_, _) =>
            {
                if (_applying) return;
                _draft.SetCapability(capability, toggle.IsOn);
                ApplyPresentation();
            };
            _toggles[capability] = toggle;
            var icon = FluentIconCatalog.Build(Glyph(capability), 20);
            icon.IsTextScaleFactorEnabled = false;
            var card = new SettingsCard
            {
                Header = label,
                Description = SetupLocalization.GetString($"PermissionsPage_Cap_{resourceName}_Description"),
                HeaderIcon = icon,
                Content = toggle,
            };
            FineTuneExpander.Items.Add(card);
            if (capability == SetupCapability.Browser) _browserCard = card;
        }
    }

    private static string Glyph(SetupCapability capability) => capability switch
    {
        SetupCapability.System => FluentIconCatalog.Terminal,
        SetupCapability.Canvas => FluentIconCatalog.Canvas,
        SetupCapability.Screen => FluentIconCatalog.Screen,
        SetupCapability.Camera => FluentIconCatalog.Camera,
        SetupCapability.Location => FluentIconCatalog.Location,
        SetupCapability.Browser => FluentIconCatalog.Browser,
        SetupCapability.Tts => FluentIconCatalog.Voice,
        _ => FluentIconCatalog.Speech,
    };

    private void ApplyPresentation()
    {
        if (_draft is null) return;
        var previous = _applying;
        _applying = true;
        ProfileSelector.SelectedIndex = _draft.Profile == SetupCapabilityProfile.Custom ? -1 : (int)_draft.Profile;
        CustomProfileText.Visibility = _draft.Profile == SetupCapabilityProfile.Custom ? Visibility.Visible : Visibility.Collapsed;
        FineTuneExpander.IsExpanded = _draft.FineTuneExpanded;
        var selected = SetupCapabilityProfiles.Ordered.Where(_draft.GetCapability).Select(capability =>
            SetupLocalization.GetString($"PermissionsPage_Cap_{(capability == SetupCapability.System ? "SystemRun" : capability.ToString())}_Label")).ToArray();
        SelectedSummary.Text = selected.Length > 0
            ? string.Join(", ", selected)
            : SetupLocalization.GetString("Onboarding_V2_NoCapabilities");
        foreach (var (capability, toggle) in _toggles)
        {
            toggle.IsOn = _draft.GetCapability(capability);
            toggle.IsEnabled = _draft.CapabilityControlsEnabled &&
                (capability != SetupCapability.Browser || _draft.BrowserAvailable);
        }
        ProfileSelector.IsEnabled = _draft.CapabilityControlsEnabled;
        CapabilityControls.Opacity = _draft.CapabilityControlsEnabled ? 1 : 0.4;
        TransportRequired.Visibility = _draft.CapabilityControlsEnabled ? Visibility.Collapsed : Visibility.Visible;
        if (_browserCard is not null)
        {
            _browserCard.Description = SetupLocalization.GetString("PermissionsPage_Cap_Browser_Description") +
                (_draft.BrowserAvailable ? "" : "\n" + SetupLocalization.GetString("Onboarding_V2_BrowserPrerequisite"));
        }
        _applying = previous;
    }

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_applying || _draft is null) return;
        if (ProfileSelector.SelectedIndex >= 0)
            _draft.ApplyProfile((SetupCapabilityProfile)ProfileSelector.SelectedIndex);
        ApplyPresentation();
    }

    private void FineTune_Changed(object sender, EventArgs e)
    {
        if (!_applying && _draft is not null)
            _draft.FineTuneExpanded = FineTuneExpander.IsExpanded;
    }

    private void Transport_Toggled(object sender, RoutedEventArgs e)
    {
        if (_applying || _draft is null) return;
        _draft.SetNodeMode(NodeModeToggle.IsOn);
        _draft.SetMcpServer(McpToggle.IsOn);
        ApplyPresentation();
    }

    private void Ollama_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_applying) _draft?.SetOllamaSharing(OllamaToggle.IsOn);
    }

    private void Startup_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_applying && SetupWindow.Active is { } window)
            window.AutoStartAfterSetup = StartupPreferenceToggle.IsOn;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => SetupWindow.Active?.NavigateToWelcome(back: true);
    private void Next_Click(object sender, RoutedEventArgs e) => SetupWindow.Active?.NavigateAfterCapabilities();
}
