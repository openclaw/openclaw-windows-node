using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.UI.Notifications;
using OpenClawTray.Helpers;

namespace OpenClawTray.Onboarding.Services;

/// <summary>
/// Checks real Windows permission status for onboarding.
/// Uses lightweight passive checks — never triggers OS consent dialogs.
/// Designed for unpackaged apps (WindowsPackageType=None).
/// </summary>
public static class PermissionChecker
{
    public enum PermissionStatus
    {
        /// <summary>Permission is granted and ready to use.</summary>
        Granted,
        /// <summary>Permission is denied by user or system policy.</summary>
        Denied,
        /// <summary>No hardware device available (camera/mic).</summary>
        NoDevice,
        /// <summary>Feature is supported but uses on-demand consent (screen capture picker).</summary>
        Supported,
        /// <summary>Feature is not supported on this device/OS version.</summary>
        NotSupported,
        /// <summary>Status could not be determined.</summary>
        Unknown
    }

    public record PermissionResult(
        string Name,
        string Icon,
        PermissionStatus Status,
        string SettingsUri,
        string StatusLabel);

    /// <summary>
    /// Checks all 5 permissions and returns current status for each.
    /// All checks are passive — no OS consent dialogs are triggered.
    /// </summary>
    public static async Task<List<PermissionResult>> CheckAllAsync()
    {
        var results = new List<PermissionResult>();

        results.Add(CheckNotifications());
        results.Add(await CheckCameraAsync());
        results.Add(await CheckMicrophoneAsync());
        results.Add(CheckLocation());
        results.Add(CheckScreenCapture());

        return results;
    }

    /// <summary>
    /// Subscribes to camera and microphone access changes for auto-refresh.
    /// Returns an Action that unsubscribes when called.
    /// </summary>
    public static Action SubscribeToAccessChanges(Action onChanged)
    {
        var cameraAccess = DeviceAccessInformation.CreateFromDeviceClass(DeviceClass.VideoCapture);
        var micAccess = DeviceAccessInformation.CreateFromDeviceClass(DeviceClass.AudioCapture);

        TypedEventHandler<DeviceAccessInformation, DeviceAccessChangedEventArgs> handler =
            (_, _) => onChanged();

        cameraAccess.AccessChanged += handler;
        micAccess.AccessChanged += handler;

        return () =>
        {
            cameraAccess.AccessChanged -= handler;
            micAccess.AccessChanged -= handler;
        };
    }

    private static PermissionResult CheckNotifications()
    {
        try
        {
            // Use the Compat API which handles unpackaged app identity correctly
            var notifier = Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat.CreateToastNotifier();
            var setting = notifier.Setting;

            var (status, label) = setting switch
            {
                NotificationSetting.Enabled => (PermissionStatus.Granted, LocalizationHelper.GetString("Onboarding_Perm_Enabled")),
                NotificationSetting.DisabledByManifest => (PermissionStatus.Denied, LocalizationHelper.GetString("Onboarding_Perm_DisabledManifest")),
                NotificationSetting.DisabledByGroupPolicy => (PermissionStatus.Denied, LocalizationHelper.GetString("Onboarding_Perm_DisabledPolicy")),
                NotificationSetting.DisabledForUser => (PermissionStatus.Denied, LocalizationHelper.GetString("Onboarding_Perm_DisabledUser")),
                _ => (PermissionStatus.Denied, LocalizationHelper.GetString("Onboarding_Perm_Disabled"))
            };

            return new PermissionResult(LocalizationHelper.GetString("Onboarding_Perm_Notifications"), "🔔", status,
                "ms-settings:notifications", label);
        }
        catch (Exception)
        {
            // Fallback: check global notification setting via registry
            return CheckNotificationsViaRegistry();
        }
    }

    private static PermissionResult CheckNotificationsViaRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\PushNotifications");
            if (key != null)
            {
                var val = key.GetValue("ToastEnabled");
                if (val is int intVal)
                {
                    return new PermissionResult(LocalizationHelper.GetString("Onboarding_Perm_Notifications"), "🔔",
                        intVal == 1 ? PermissionStatus.Granted : PermissionStatus.Denied,
                        "ms-settings:notifications",
                        intVal == 1 ? LocalizationHelper.GetString("Onboarding_Perm_Enabled") : LocalizationHelper.GetString("Onboarding_Perm_DisabledUser"));
                }
            }

            // Key absent = notifications enabled by default
            return new PermissionResult(LocalizationHelper.GetString("Onboarding_Perm_Notifications"), "🔔", PermissionStatus.Granted,
                "ms-settings:notifications", LocalizationHelper.GetString("Onboarding_Perm_EnabledDefault"));
        }
        catch (Exception)
        {
            return new PermissionResult(LocalizationHelper.GetString("Onboarding_Perm_Notifications"), "🔔", PermissionStatus.Unknown,
                "ms-settings:notifications", LocalizationHelper.GetString("Onboarding_Perm_UnableToCheck"));
        }
    }

    private static async Task<PermissionResult> CheckCameraAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            if (devices.Count == 0)
            {
                return new PermissionResult(LocalizationHelper.GetString("Onboarding_Perm_Camera"), "📷", PermissionStatus.NoDevice,
                    "ms-settings:privacy-webcam", LocalizationHelper.GetString("Onboarding_Perm_NoCameraDetected"));
            }

            var access = DeviceAccessInformation.CreateFromDeviceClass(DeviceClass.VideoCapture);
            var (status, label) = access.CurrentStatus switch
            {
                DeviceAccessStatus.Allowed => (PermissionStatus.Granted, LocalizationHelper.GetString("Onboarding_Perm_Allowed")),
                DeviceAccessStatus.DeniedByUser => (PermissionStatus.Denied, LocalizationHelper.GetString("Onboarding_Perm_DeniedUser")),
                DeviceAccessStatus.DeniedBySystem => (PermissionStatus.Denied, LocalizationHelper.GetString("Onboarding_Perm_DeniedSystem")),
                _ => (PermissionStatus.Unknown, LocalizationHelper.GetString("Onboarding_Perm_NotDetermined"))
            };

            return new PermissionResult(LocalizationHelper.GetString("Onboarding_Perm_Camera"), "📷", status,
                "ms-settings:privacy-webcam", label);
        }
        catch (Exception)
        {
            return new PermissionResult(LocalizationHelper.GetString("Onboarding_Perm_Camera"), "📷", PermissionStatus.Unknown,
                "ms-settings:privacy-webcam", LocalizationHelper.GetString("Onboarding_Perm_UnableToCheck"));
        }
    }

    private static async Task<PermissionResult> CheckMicrophoneAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
            if (devices.Count == 0)
            {
                return new PermissionResult(LocalizationHelper.GetString("Onboarding_Perm_Microphone"), "🎤", PermissionStatus.NoDevice,
                    "ms-settings:privacy-microphone", LocalizationHelper.GetString("Onboarding_Perm_NoMicDetected"));
            }

            var access = DeviceAccessInformation.CreateFromDeviceClass(DeviceClass.AudioCapture);
            var (status, label) = access.CurrentStatus switch
            {
                DeviceAccessStatus.Allowed => (PermissionStatus.Granted, LocalizationHelper.GetString("Onboarding_Perm_Allowed")),
                DeviceAccessStatus.DeniedByUser => (PermissionStatus.Denied, LocalizationHelper.GetString("Onboarding_Perm_DeniedUser")),
                DeviceAccessStatus.DeniedBySystem => (PermissionStatus.Denied, LocalizationHelper.GetString("Onboarding_Perm_DeniedSystem")),
                _ => (PermissionStatus.Unknown, LocalizationHelper.GetString("Onboarding_Perm_NotDetermined"))
            };

            return new PermissionResult(LocalizationHelper.GetString("Onboarding_Perm_Microphone"), "🎤", status,
                "ms-settings:privacy-microphone", label);
        }
        catch (Exception)
        {
            return new PermissionResult(LocalizationHelper.GetString("Onboarding_Perm_Microphone"), "🎤", PermissionStatus.Unknown,
                "ms-settings:privacy-microphone", LocalizationHelper.GetString("Onboarding_Perm_UnableToCheck"));
        }
    }

    private static PermissionResult CheckScreenCapture()
    {
        try
        {
            bool supported = GraphicsCaptureSession.IsSupported();
            return new PermissionResult(LocalizationHelper.GetString("Onboarding_Perm_ScreenCapture"), "🖥️",
                supported ? PermissionStatus.Supported : PermissionStatus.NotSupported,
                "", // No persistent settings URI — uses picker at capture time
                supported ? LocalizationHelper.GetString("Onboarding_Perm_ScreenCaptureAvailable") : LocalizationHelper.GetString("Onboarding_Perm_NotSupported"));
        }
        catch (Exception)
        {
            return new PermissionResult(LocalizationHelper.GetString("Onboarding_Perm_ScreenCapture"), "🖥️", PermissionStatus.Unknown,
                "", LocalizationHelper.GetString("Onboarding_Perm_UnableToCheck"));
        }
    }

    /// <summary>
    /// Checks location permission passively via registry.
    /// NEVER calls Geolocator.RequestAccessAsync() which triggers an OS consent dialog.
    /// </summary>
    private static PermissionResult CheckLocation()
    {
        try
        {
            // Check system-wide location service status
            using var sysKey = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location");
            var sysValue = sysKey?.GetValue("Value") as string;

            if (string.Equals(sysValue, "Deny", StringComparison.OrdinalIgnoreCase))
            {
                return new PermissionResult(LocalizationHelper.GetString("Onboarding_Perm_Location"), "📍", PermissionStatus.Denied,
                    "ms-settings:privacy-location", LocalizationHelper.GetString("Onboarding_Perm_LocationDisabledSystem"));
            }

            // Check per-user location setting
            using var userKey = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location");
            var userValue = userKey?.GetValue("Value") as string;

            if (string.Equals(userValue, "Deny", StringComparison.OrdinalIgnoreCase))
            {
                return new PermissionResult(LocalizationHelper.GetString("Onboarding_Perm_Location"), "📍", PermissionStatus.Denied,
                    "ms-settings:privacy-location", LocalizationHelper.GetString("Onboarding_Perm_LocationDisabledUser"));
            }

            if (string.Equals(userValue, "Allow", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sysValue, "Allow", StringComparison.OrdinalIgnoreCase))
            {
                return new PermissionResult(LocalizationHelper.GetString("Onboarding_Perm_Location"), "📍", PermissionStatus.Granted,
                    "ms-settings:privacy-location", LocalizationHelper.GetString("Onboarding_Perm_LocationEnabled"));
            }

            return new PermissionResult(LocalizationHelper.GetString("Onboarding_Perm_Location"), "📍", PermissionStatus.Unknown,
                "ms-settings:privacy-location", LocalizationHelper.GetString("Onboarding_Perm_NotDetermined"));
        }
        catch (Exception)
        {
            return new PermissionResult(LocalizationHelper.GetString("Onboarding_Perm_Location"), "📍", PermissionStatus.Unknown,
                "ms-settings:privacy-location", LocalizationHelper.GetString("Onboarding_Perm_UnableToCheck"));
        }
    }
}
