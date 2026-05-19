using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;
using Windows.UI.Notifications;
using OpenClawTray.Helpers;

namespace OpenClawTray.Onboarding.Services;

/// <summary>
/// Checks real Windows permission status for onboarding.
/// Uses lightweight passive checks — never triggers OS consent dialogs.
///
/// Branches on <see cref="PackageHelper.IsPackaged"/>:
/// <list type="bullet">
/// <item><description>Packaged (MSIX, the shipping channel): uses
/// <c>Windows.Security.Authorization.AppCapabilityAccess.AppCapability</c> for
/// webcam / microphone / location. This is the only API that reports the
/// per-package consent state surfaced in Settings → Privacy → &lt;Capability&gt;
/// under our package name (vs. the catch-all "Desktop apps" bucket).</description></item>
/// <item><description>Unpackaged (dev / debug builds): keeps the legacy
/// <c>DeviceAccessInformation</c> + registry probes which are what Windows
/// reports for arbitrary Win32 EXEs.</description></item>
/// </list>
/// Both branches are passive (no <c>RequestAccessAsync</c>) so they can run
/// during onboarding without interrupting the user. The actual capability
/// request happens later when the consuming service (e.g. <c>CameraCaptureService</c>)
/// first calls <c>MediaCapture.InitializeAsync</c>.
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
    /// Maps an <see cref="AppCapabilityAccessStatus"/> (packaged-app capability state)
    /// to our internal <see cref="PermissionStatus"/>. Pulled out so unit tests can
    /// pin the mapping without instantiating an <see cref="AppCapability"/> (which
    /// only resolves inside an MSIX-launched process).
    /// </summary>
    internal static (PermissionStatus Status, string LabelKey) MapAppCapabilityAccessStatus(
        AppCapabilityAccessStatus status) => status switch
    {
        AppCapabilityAccessStatus.Allowed             => (PermissionStatus.Granted, "Onboarding_Perm_Allowed"),
        AppCapabilityAccessStatus.UserPromptRequired  => (PermissionStatus.Unknown, "Onboarding_Perm_NotDetermined"),
        AppCapabilityAccessStatus.DeniedByUser        => (PermissionStatus.Denied,  "Onboarding_Perm_DeniedUser"),
        AppCapabilityAccessStatus.DeniedBySystem      => (PermissionStatus.Denied,  "Onboarding_Perm_DeniedSystem"),
        _                                             => (PermissionStatus.Unknown, "Onboarding_Perm_NotDetermined")
    };

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
        if (PackageHelper.IsPackaged)
        {
            return SubscribeToAccessChangesPackaged(onChanged);
        }

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

    private static Action SubscribeToAccessChangesPackaged(Action onChanged)
    {
        // AppCapability.AccessChanged fires whenever the user toggles the per-package
        // consent in Settings → Privacy. We subscribe to webcam, microphone, and
        // location together because the onboarding row strip surfaces all three.
        AppCapability? webcam = null, microphone = null, location = null;
        TypedEventHandler<AppCapability, AppCapabilityAccessChangedEventArgs>? handler = null;
        try
        {
            webcam = AppCapability.Create("webcam");
            microphone = AppCapability.Create("microphone");
            location = AppCapability.Create("location");
            handler = (_, _) => onChanged();
            webcam.AccessChanged += handler;
            microphone.AccessChanged += handler;
            location.AccessChanged += handler;
        }
        catch (Exception)
        {
            // If any one of these failed, unwind any subscriptions we did make and
            // hand back a no-op disposer; callers must not crash if the packaged
            // capability surface is unavailable on an older OS build.
            if (handler != null)
            {
                if (webcam != null) webcam.AccessChanged -= handler;
                if (microphone != null) microphone.AccessChanged -= handler;
                if (location != null) location.AccessChanged -= handler;
            }
            return () => { };
        }

        return () =>
        {
            try
            {
                webcam.AccessChanged -= handler;
                microphone.AccessChanged -= handler;
                location.AccessChanged -= handler;
            }
            catch
            {
                // Best-effort unsubscription; nothing to do if the AppCapability
                // objects have already been GC'd.
            }
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

            if (PackageHelper.IsPackaged)
            {
                return CheckAppCapability("webcam", LocalizationHelper.GetString("Onboarding_Perm_Camera"), "📷",
                    "ms-settings:privacy-webcam");
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

            if (PackageHelper.IsPackaged)
            {
                return CheckAppCapability("microphone", LocalizationHelper.GetString("Onboarding_Perm_Microphone"), "🎤",
                    "ms-settings:privacy-microphone");
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
    /// Checks location permission passively.
    ///
    /// Packaged: uses <c>AppCapability.Create("location").CheckAccess()</c> which
    /// returns the per-package consent state — the same answer the user sees in
    /// Settings → Privacy → Location under our package name.
    ///
    /// Unpackaged: reads <c>HKLM\…\ConsentStore\location</c> (system-wide kill
    /// switch) and <c>HKCU\…\ConsentStore\location</c> (per-user). Both are
    /// passive reads; we deliberately never call <c>Geolocator.RequestAccessAsync()</c>
    /// here because that surfaces a consent dialog mid-onboarding.
    /// </summary>
    private static PermissionResult CheckLocation()
    {
        if (PackageHelper.IsPackaged)
        {
            return CheckAppCapability("location", LocalizationHelper.GetString("Onboarding_Perm_Location"), "📍",
                "ms-settings:privacy-location");
        }

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

    /// <summary>
    /// Packaged-app capability probe. Builds the <see cref="PermissionResult"/>
    /// for a declared MSIX capability (webcam / microphone / location) using
    /// <see cref="AppCapability"/>. Falls back to Unknown if the API throws —
    /// older Windows builds or non-declared capabilities both surface as a
    /// throw and we don't want to crash onboarding for either.
    /// </summary>
    private static PermissionResult CheckAppCapability(
        string capabilityName,
        string displayName,
        string icon,
        string settingsUri)
    {
        try
        {
            var capability = AppCapability.Create(capabilityName);
            var (status, labelKey) = MapAppCapabilityAccessStatus(capability.CheckAccess());
            return new PermissionResult(displayName, icon, status, settingsUri,
                LocalizationHelper.GetString(labelKey));
        }
        catch (Exception)
        {
            return new PermissionResult(displayName, icon, PermissionStatus.Unknown, settingsUri,
                LocalizationHelper.GetString("Onboarding_Perm_UnableToCheck"));
        }
    }
}
