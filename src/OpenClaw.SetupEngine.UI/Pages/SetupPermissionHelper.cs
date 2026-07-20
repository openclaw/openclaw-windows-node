using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.Devices.Enumeration;
using Windows.Graphics.Capture;

namespace OpenClaw.SetupEngine.UI.Pages;

/// <summary>
/// Shared definition + passive status checks for the Windows OS permissions surfaced
/// during setup. Used by CapabilitiesPage so each capability shows its matching
/// Windows permission inline.
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
        new("Location", "Location", "\uE81D", "ms-settings:privacy-location", CheckLocationAsync);
    public static readonly PermDef ScreenCapture =
        new("Screen", "Screen capture", "\uE7F4", "", CheckScreenCaptureAsync);

    public static readonly PermDef[] All = [Notifications, Camera, Microphone, Location, ScreenCapture];

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
        var iconFg = Res("TextFillColorPrimaryBrush");
        var subFg = Res("TextFillColorSecondaryBrush");

        var icon = new FontIcon
        {
            Glyph = perm.Glyph,
            FontSize = 20,
            Foreground = iconFg,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
            IsTextScaleFactorEnabled = false,
        };

        var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = perm.Name,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        textStack.Children.Add(new TextBlock { Text = status, FontSize = 12, Foreground = subFg });

        FrameworkElement actionCol;
        if (string.IsNullOrEmpty(perm.SettingsUri))
        {
            // No standing OS grant (screen capture) — the user picks what to share each time
            // via the Windows Graphics Capture picker, so there's nothing to pre-allow here.
            var pill = Pill(granted ? "Ask every time" : "Unavailable", subFg);
            if (granted)
                ToolTipService.SetToolTip(pill,
                    "Windows has no on/off setting for screen capture. Each time the agent captures your screen, Windows shows a picker so you choose exactly what to share - there's nothing to allow in advance.");
            actionCol = pill;
        }
        else if (granted)
        {
            actionCol = Pill("Allowed", ok);
        }
        else
        {
            var btn = new Button
            {
                CornerRadius = new CornerRadius(4),
                MinWidth = 140,
                Padding = new Thickness(12, 5, 12, 5),
                VerticalAlignment = VerticalAlignment.Center,
                Content = new TextBlock { Text = "Open Settings", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center },
            };
            AutomationProperties.SetName(btn, $"Open {perm.Name} settings");
            var uri = perm.SettingsUri;
            // Sync handler (no async-void lambda): launching a settings deep link is
            // best-effort and fire-and-forget, so we don't await the operation.
            btn.Click += (_, _) =>
            {
                try { _ = Windows.System.Launcher.LaunchUriAsync(new Uri(uri)); }
                catch { /* best effort */ }
            };
            actionCol = btn;
        }

        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(152) },
            },
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Right-align the status/action in a fixed-width rail so chips and buttons line up.
        if (actionCol is FrameworkElement fe)
            fe.HorizontalAlignment = HorizontalAlignment.Right;
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

    public static Task<(string, bool)> CheckNotificationsAsync()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\PushNotifications");
            if (key?.GetValue("ToastEnabled") is int val && val == 0)
                return Task.FromResult(("Turn on in Settings to get alerts", false));
            return Task.FromResult(("Windows notifications are on", true));
        }
        catch
        {
            return Task.FromResult(("Couldn't check status", false));
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
                ? ("Windows camera access is on", true)
                : ("Turn on in Settings to use the camera", false);
        }
        catch { return ("Couldn't check status", false); }
    }

    public static async Task<(string, bool)> CheckMicrophoneAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
            if (devices.Count == 0) return ("No microphone detected", false);
            var access = DeviceAccessInformation.CreateFromDeviceClass(DeviceClass.AudioCapture);
            return access.CurrentStatus == DeviceAccessStatus.Allowed
                ? ("Windows microphone access is on", true)
                : ("Turn on in Settings to use the microphone", false);
        }
        catch { return ("Couldn't check status", false); }
    }

    public static Task<(string, bool)> CheckLocationAsync()
    {
        try
        {
            using var sysKey = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location");
            if (sysKey?.GetValue("Value") is string sv && sv.Equals("Deny", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(("Turn on in Settings to share location", false));

            using var userKey = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location");
            var uv = userKey?.GetValue("Value") as string;
            if (uv != null && uv.Equals("Deny", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(("Turn on in Settings to share location", false));

            return Task.FromResult(("Windows location is on", true));
        }
        catch { return Task.FromResult(("Couldn't check status", false)); }
    }

    public static Task<(string, bool)> CheckScreenCaptureAsync()
    {
        try
        {
            return Task.FromResult(GraphicsCaptureSession.IsSupported()
                ? ("You choose what to share each capture", true)
                : ("Not supported on this device", false));
        }
        catch { return Task.FromResult(("Couldn't check status", false)); }
    }
}
