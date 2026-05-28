using Microsoft.Toolkit.Uwp.Notifications;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

/// <summary>
/// Seeds Windows Settings > System > Notifications for the packaged app.
/// </summary>
internal static class NotificationSettingsRegistrationService
{
    internal const string RegistrationToastTag = "openclaw-notification-settings-registration";
    internal const string RegistrationToastGroup = "openclaw-system-registration";
    private const string LocalSettingsKey = "OpenClaw.NotificationSettingsSeeded";

    public static void EnsureRegistered()
    {
        if (!OpenClawTray.Helpers.PackageHelper.IsPackaged)
            return;

        if (HasAlreadySeeded())
            return;

        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            notifier.Show(CreateSuppressedRegistrationToast());
            MarkSeeded();
            _ = RemoveRegistrationToastFromHistoryAsync();
            Logger.Info("Seeded Windows notification settings registration");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            Logger.Warn($"Failed to seed Windows notification settings registration: {ex.Message}");
        }
    }

    private static global::Windows.UI.Notifications.ToastNotification CreateSuppressedRegistrationToast()
    {
        var xml = new global::Windows.Data.Xml.Dom.XmlDocument();
        xml.LoadXml(
            "<toast>" +
            "<visual><binding template=\"ToastGeneric\">" +
            "<text>OpenClaw Companion</text>" +
            "<text>Notifications are registered for this app.</text>" +
            "</binding></visual>" +
            "</toast>");

        return new global::Windows.UI.Notifications.ToastNotification(xml)
        {
            Tag = RegistrationToastTag,
            Group = RegistrationToastGroup,
            SuppressPopup = true
        };
    }

    private static async Task RemoveRegistrationToastFromHistoryAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            ToastNotificationManagerCompat.History.Remove(RegistrationToastTag, RegistrationToastGroup);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            Logger.Warn($"Failed to remove notification settings registration toast from history: {ex.Message}");
        }
    }

    private static bool HasAlreadySeeded()
    {
        try
        {
            var values = global::Windows.Storage.ApplicationData.Current.LocalSettings.Values;
            return values.TryGetValue(LocalSettingsKey, out var value) && value is bool seeded && seeded;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            Logger.Warn($"Failed to read notification settings registration state: {ex.Message}");
            return false;
        }
    }

    private static void MarkSeeded()
    {
        try
        {
            global::Windows.Storage.ApplicationData.Current.LocalSettings.Values[LocalSettingsKey] = true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            Logger.Warn($"Failed to persist notification settings registration state: {ex.Message}");
        }
    }
}
