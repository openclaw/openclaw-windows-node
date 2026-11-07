// "This PC" Inbox alerts: Windows privacy settings that block a device an enabled node
// capability needs (camera for camera.*, microphone for stt.transcribe and camera clips,
// location for location.get). Pure rules over the settings snapshot and already-read device access, so
// nothing here can raise a Windows consent prompt. ThisPcAlertSource publishes the result
// as System-tab Inbox rows whose action opens the matching Windows privacy settings page.
// WinUI-free.
//
// Usage:
//   var alerts = ThisPcAlerts.Build(ThisPcCapabilities.From(settings.Current), reader.Read);
//   foreach (var alert in alerts) notifications.Show(ThisPcAlerts.ToNotification(alert, getString));
//   if (ThisPcAlerts.IsPrivacySettingsRoute(route)) { /* launch the ms-settings URI */ }

using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

internal enum ThisPcDevice
{
    Camera,
    Microphone,
    Location,
}

/// <summary>What Windows reports for a device without prompting; <see cref="Unknown"/> never alerts.</summary>
internal enum ThisPcDeviceAccess
{
    Unknown,
    Allowed,
    DeniedByUser,
    DeniedBySystem,
}

/// <summary>Which privacy-sensitive node capabilities are on while this PC serves node commands.</summary>
internal sealed record ThisPcCapabilities(bool NodeActive, bool Camera, bool Microphone, bool Location)
{
    /// <summary>
    /// Node commands run through the Gateway node connection or local MCP. The microphone is
    /// needed for speech-to-text and for camera clips, which record sound by default.
    /// </summary>
    public static ThisPcCapabilities From(SettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new(
            settings.EnableNodeMode || settings.EnableMcpServer,
            settings.NodeCameraEnabled,
            settings.NodeSttEnabled || settings.NodeCameraEnabled,
            settings.NodeLocationEnabled);
    }

    public bool Needs(ThisPcDevice device) => NodeActive && device switch
    {
        ThisPcDevice.Camera => Camera,
        ThisPcDevice.Microphone => Microphone,
        ThisPcDevice.Location => Location,
        _ => false,
    };
}

internal sealed record ThisPcAlert(ThisPcDevice Device, ThisPcDeviceAccess Access);

internal static class ThisPcAlerts
{
    public const string Source = "this-pc";
    public const string Category = "privacy";

    /// <summary>Fixture-only device access override, for example <c>camera=denied-by-user</c>.</summary>
    public const string FixtureAccessEnvironmentVariable = "OPENCLAW_FIXTURE_DEVICE_ACCESS";

    private static readonly ThisPcDevice[] Devices = [ThisPcDevice.Camera, ThisPcDevice.Microphone, ThisPcDevice.Location];

    /// <summary>Blocked devices that an enabled capability needs, in a fixed device order.</summary>
    public static IReadOnlyList<ThisPcAlert> Build(ThisPcCapabilities capabilities, Func<ThisPcDevice, ThisPcDeviceAccess> readAccess)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(readAccess);
        var alerts = new List<ThisPcAlert>();
        foreach (var device in Devices)
        {
            // Only devices an enabled capability needs are read at all.
            if (!capabilities.Needs(device))
                continue;
            var access = readAccess(device);
            if (access is ThisPcDeviceAccess.DeniedByUser or ThisPcDeviceAccess.DeniedBySystem)
                alerts.Add(new(device, access));
        }
        return alerts;
    }

    public static string DedupeKey(ThisPcDevice device) => device switch
    {
        ThisPcDevice.Camera => "this-pc:camera",
        ThisPcDevice.Microphone => "this-pc:microphone",
        _ => "this-pc:location",
    };

    public static string PrivacySettingsRoute(ThisPcDevice device) => device switch
    {
        ThisPcDevice.Camera => "ms-settings:privacy-webcam",
        ThisPcDevice.Microphone => "ms-settings:privacy-microphone",
        _ => "ms-settings:privacy-location",
    };

    /// <summary>True only for the exact privacy pages this source links to; other ms-settings URIs are not routes.</summary>
    public static bool IsPrivacySettingsRoute(string? route) =>
        route is not null && Devices.Any(device => string.Equals(route, PrivacySettingsRoute(device), StringComparison.OrdinalIgnoreCase));

    public static AppNotification ToNotification(ThisPcAlert alert, Func<string, string> getString)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(getString);
        var (titleKey, messageKey) = alert.Device switch
        {
            ThisPcDevice.Camera => ("ThisPc_CameraBlockedTitle", "ThisPc_CameraBlockedMessage"),
            ThisPcDevice.Microphone => ("ThisPc_MicrophoneBlockedTitle", "ThisPc_MicrophoneBlockedMessage"),
            _ => ("ThisPc_LocationBlockedTitle", "ThisPc_LocationBlockedMessage"),
        };
        var message = getString(messageKey);
        if (alert.Access == ThisPcDeviceAccess.DeniedBySystem)
            message = $"{message} {getString("ThisPc_ManagedBySystem")}";
        return new AppNotification
        {
            Id = DedupeKey(alert.Device),
            Title = getString(titleKey),
            Message = message,
            Severity = AppNotificationSeverity.Warning,
            Source = Source,
            Category = Category,
            DedupeKey = DedupeKey(alert.Device),
            ActionLabel = getString("ThisPc_OpenPrivacySettings"),
            ActionRoute = PrivacySettingsRoute(alert.Device),
        };
    }

    /// <summary>
    /// Parses the fixture override (<c>camera=denied-by-user;location=denied-by-system</c>).
    /// Unlisted devices and unrecognized values read as <see cref="ThisPcDeviceAccess.Unknown"/>.
    /// </summary>
    public static IReadOnlyDictionary<ThisPcDevice, ThisPcDeviceAccess> ParseFixtureAccess(string? value)
    {
        var result = new Dictionary<ThisPcDevice, ThisPcDeviceAccess>();
        foreach (var pair in (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            ThisPcDevice? device = parts.Length != 2 ? null : parts[0].ToLowerInvariant() switch
            {
                "camera" => ThisPcDevice.Camera,
                "microphone" => ThisPcDevice.Microphone,
                "location" => ThisPcDevice.Location,
                _ => null,
            };
            if (device is null)
                continue;
            result[device.Value] = parts[1].ToLowerInvariant() switch
            {
                "allowed" => ThisPcDeviceAccess.Allowed,
                "denied-by-user" => ThisPcDeviceAccess.DeniedByUser,
                "denied-by-system" => ThisPcDeviceAccess.DeniedBySystem,
                _ => ThisPcDeviceAccess.Unknown,
            };
        }
        return result;
    }
}
