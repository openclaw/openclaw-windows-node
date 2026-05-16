using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace OpenClaw.App.Pages.Settings;

public sealed partial class GeneralSettingsPage : Page
{
    public GeneralSettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var s = App.Current.Settings;
        if (s == null) return;

        AutoStartToggle.IsOn = s.AutoStart;
        HotkeyToggle.IsOn = s.GlobalHotkeyEnabled;

        var theme = s.AppTheme ?? "Default";
        foreach (ComboBoxItem item in ThemeCombo.Items)
        {
            if (item.Tag is string tag && tag == theme)
            {
                ThemeCombo.SelectedItem = item;
                break;
            }
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var s = App.Current.Settings;
        if (s == null) return;

        s.AutoStart = AutoStartToggle.IsOn;
        s.GlobalHotkeyEnabled = HotkeyToggle.IsOn;
        s.AppTheme = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Default";
        s.Save();

        App.Current.ApplyTheme();
    }
}
