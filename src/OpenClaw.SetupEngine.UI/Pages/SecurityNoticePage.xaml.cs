using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI.Controls;
using OpenClawTray.Helpers;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class SecurityNoticePage : Page
{
    private SetupConfig? _config;

    public SecurityNoticePage()
    {
        InitializeComponent();
        WelcomeDescription.Text = SetupLocalization.GetString("Onboarding_Flow_WelcomeDescription.Text");
        ChatFeature.HeaderIcon = FluentIconCatalog.Build(FluentIconCatalog.Chat, 18);
        PcFeature.HeaderIcon = FluentIconCatalog.Build(FluentIconCatalog.System, 18);
        GatewayFeature.HeaderIcon = FluentIconCatalog.Build(FluentIconCatalog.ServerEnvironment, 18);
        SetupRowPresentation.StyleHeaders(PcFeature, GatewayFeature, ChatFeature);
        SafetyTitle.Text = SetupLocalization.GetString("Onboarding_V2_WelcomeTrust.Title");
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        SetupWindow.Active?.NavigateToWelcome();
    }
}
