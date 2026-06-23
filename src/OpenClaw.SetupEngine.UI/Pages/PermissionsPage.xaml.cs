using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine.UI;

namespace OpenClaw.SetupEngine.UI.Pages;

// Legacy standalone permissions step. Its content now lives inline on the merged
// CapabilitiesPage ("Windows permissions" section), so the main setup flow no longer
// navigates here — it is retained for the dev preview route and as a fallback.
public sealed partial class PermissionsPage : Page
{
    private SetupConfig? _config;

    public PermissionsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _config = e.Parameter as SetupConfig ?? new SetupConfig();
        _ = RefreshPermissions();
    }

    private async Task RefreshPermissions()
    {
        PermRows.Children.Clear();
        foreach (var perm in SetupPermissionHelper.All)
        {
            var (status, granted) = await perm.Check();
            PermRows.Children.Add(SetupPermissionHelper.BuildRow(perm, status, granted));
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshPermissions();

    private void BackToWizard_Click(object sender, RoutedEventArgs e)
        => SetupWindow.Active?.NavigateToWizard();

    private void Next_Click(object sender, RoutedEventArgs e)
        => SetupWindow.Active?.NavigateToComplete(true, TimeSpan.Zero, null);
}
