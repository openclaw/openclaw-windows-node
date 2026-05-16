using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace OpenClaw.App.Pages.Settings;

public sealed partial class NotificationSettingsPage : Page
{
    public NotificationSettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var s = App.Current.Settings;
        if (s == null) return;

        NotificationsToggle.IsOn = s.ShowNotifications;

        var sound = s.NotificationSound ?? "Default";
        foreach (ComboBoxItem item in SoundCombo.Items)
        {
            if (item.Tag is string tag && tag == sound)
            {
                SoundCombo.SelectedItem = item;
                break;
            }
        }
        if (SoundCombo.SelectedIndex < 0) SoundCombo.SelectedIndex = 0;

        NotifyHealthCb.IsChecked = s.NotifyHealth;
        NotifyUrgentCb.IsChecked = s.NotifyUrgent;
        NotifyReminderCb.IsChecked = s.NotifyReminder;
        NotifyEmailCb.IsChecked = s.NotifyEmail;
        NotifyCalendarCb.IsChecked = s.NotifyCalendar;
        NotifyBuildCb.IsChecked = s.NotifyBuild;
        NotifyStockCb.IsChecked = s.NotifyStock;
        NotifyInfoCb.IsChecked = s.NotifyInfo;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var s = App.Current.Settings;
        if (s == null) return;

        s.ShowNotifications = NotificationsToggle.IsOn;
        s.NotificationSound = (SoundCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Default";
        s.NotifyHealth = NotifyHealthCb.IsChecked == true;
        s.NotifyUrgent = NotifyUrgentCb.IsChecked == true;
        s.NotifyReminder = NotifyReminderCb.IsChecked == true;
        s.NotifyEmail = NotifyEmailCb.IsChecked == true;
        s.NotifyCalendar = NotifyCalendarCb.IsChecked == true;
        s.NotifyBuild = NotifyBuildCb.IsChecked == true;
        s.NotifyStock = NotifyStockCb.IsChecked == true;
        s.NotifyInfo = NotifyInfoCb.IsChecked == true;
        s.Save();
    }
}
