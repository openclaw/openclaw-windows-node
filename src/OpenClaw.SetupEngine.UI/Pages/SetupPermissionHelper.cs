using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.Devices.Enumeration;
using Windows.Graphics.Capture;

namespace OpenClaw.SetupEngine.UI.Pages;

/// <summary>
/// Shared definition + passive status checks for the Windows OS permissions surfaced
/// during setup. Used by both the merged CapabilitiesPage (each capability shows its
/// matching Windows permission inline) and the legacy standalone PermissionsPage.
/// All checks are passive — they read current OS state and never trigger a consent dialog.
/// </summary>
internal sealed record PermDef(
    string Id,
    string Name,
    string Glyph,
    string SettingsUri,
    Func<Task<(string Status, bool Granted)>> Check);

internal static class SetupPermissionHelper
{
    public static readonly PermDef Notifications =
        new("Notifications", "Notifications", "\uEA8F", "ms-settings:notifications", CheckNotificationsAsync);
    public static readonly PermDef Camera =
        new("Camera", "Camera", "\uE722", "ms-settings:privacy-webcam", CheckCameraAsync);
    public static readonly PermDef Microphone =
        new("Microphone", "Microphone", "\uE720", "ms-settings:privacy-microphone", CheckMicrophoneAsync);
    public static readonly PermDef Location =
        new("Location", "Location (optional)", "\uE81D", "ms-settings:privacy-location", CheckLocationAsync);
    public static readonly PermDef ScreenCapture =
        new("Screen", "Screen capture", "\uE7F4", "", CheckScreenCaptureAsync);

    public static readonly PermDef[] All = [Notifications, Camera, Microphone, Location, ScreenCapture];

    // ── Row rendering (themed SettingsCard-style row + status pill) ──

    public static Brush Res(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b
            ? b
            : new SolidColorBrush(Colors.Gray);

    private static Border Pill(string text, Brush color) => new()
    {
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(10, 3, 10, 3),
        BorderBrush = color,
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 12, Foreground = color },
    };

    public static FrameworkElement BuildRow(PermDef perm, string status, bool granted)
    {
        var cardBg = Res("CardBackgroundFillColorDefaultBrush");
        var cardStroke = Res("CardStrokeColorDefaultBrush");
        var ok = Res("SystemFillColorSuccessBrush");
        var caution = Res("SystemFillColorCautionBrush");
        var iconFg = Res("TextFillColorPrimaryBrush");
        var subFg = Res("TextFillColorSecondaryBrush");

        var icon = new FontIcon
        {
            Glyph = perm.Glyph,
            FontSize = 20,
            Foreground = iconFg,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };

        var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = perm.Name,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        textStack.Children.Add(new TextBlock { Text = status, FontSize = 12, Foreground = subFg });

        FrameworkElement actionCol;
        if (granted)
        {
            actionCol = Pill("Allowed", ok);
        }
        else if (!string.IsNullOrEmpty(perm.SettingsUri))
        {
            var btn = new Button
            {
                Background = new SolidColorBrush(Colors.Transparent),
                BorderBrush = caution,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 3, 10, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Content = new TextBlock { Text = "Open Settings", FontSize = 12, Foreground = caution },
            };
            var uri = perm.SettingsUri;
            btn.Click += async (_, _) =>
            {
                try { await Windows.System.Launcher.LaunchUriAsync(new Uri(uri)); }
                catch { /* best effort */ }
            };
            actionCol = btn;
        }
        else
        {
            actionCol = Pill("Per-capture", caution);
        }

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(textStack, 1);
        Grid.SetColumn(actionCol, 2);
        grid.Children.Add(icon);
        grid.Children.Add(textStack);
        grid.Children.Add(actionCol);

        return new Border
        {
            Child = grid,
            Background = cardBg,
            BorderBrush = cardStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14, 12, 14, 12),
        };
    }

    // ── Passive permission checks (no OS consent dialogs) ──

    public static Task<(string, bool)> CheckNotificationsAsync()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\PushNotifications");
            if (key?.GetValue("ToastEnabled") is int val && val == 0)
                return Task.FromResult(("Disabled", false));
            return Task.FromResult(("Enabled", true));
        }
        catch
        {
            return Task.FromResult(("Unable to check", false));
        }
    }

    public static async Task<(string, bool)> CheckCameraAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            if (devices.Count == 0) return ("No camera detected", false);
            var access = DeviceAccessInformation.CreateFromDeviceClass(DeviceClass.VideoCapture);
            return access.CurrentStatus == DeviceAccessStatus.Allowed
                ? ("Allowed", true)
                : ("Denied — open Settings to allow", false);
        }
        catch { return ("Unable to check", false); }
    }

    public static async Task<(string, bool)> CheckMicrophoneAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
            if (devices.Count == 0) return ("No microphone detected", false);
            var access = DeviceAccessInformation.CreateFromDeviceClass(DeviceClass.AudioCapture);
            return access.CurrentStatus == DeviceAccessStatus.Allowed
                ? ("Allowed", true)
                : ("Denied — open Settings to allow", false);
        }
        catch { return ("Unable to check", false); }
    }

    public static Task<(string, bool)> CheckLocationAsync()
    {
        try
        {
            using var sysKey = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location");
            if (sysKey?.GetValue("Value") is string sv && sv.Equals("Deny", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(("Location services disabled", false));

            using var userKey = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location");
            var uv = userKey?.GetValue("Value") as string;
            if (uv != null && uv.Equals("Deny", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(("Disabled for this user", false));

            return Task.FromResult(("Location services enabled", true));
        }
        catch { return Task.FromResult(("Unable to check", false)); }
    }

    public static Task<(string, bool)> CheckScreenCaptureAsync()
    {
        try
        {
            return Task.FromResult(GraphicsCaptureSession.IsSupported()
                ? ("Available — uses picker per capture", true)
                : ("Not supported on this device", false));
        }
        catch { return Task.FromResult(("Unable to check", false)); }
    }
}
