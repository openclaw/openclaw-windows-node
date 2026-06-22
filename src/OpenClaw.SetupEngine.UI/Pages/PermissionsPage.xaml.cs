using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.SetupEngine.UI;
using Microsoft.Win32;
using Windows.Devices.Enumeration;
using Windows.Graphics.Capture;
using Windows.UI;

namespace OpenClaw.SetupEngine.UI.Pages;

public sealed partial class PermissionsPage : Page
{
    private SetupConfig? _config;

    private record PermDef(string Name, string Glyph, string SettingsUri, Func<Task<(string Status, bool Granted)>> Check);

    private static readonly PermDef[] Permissions =
    [
        new("Notifications", "\uEA8F", "ms-settings:notifications", CheckNotificationsAsync),
        new("Camera", "\uE722", "ms-settings:privacy-webcam", CheckCameraAsync),
        new("Microphone", "\uE720", "ms-settings:privacy-microphone", CheckMicrophoneAsync),
        new("Location (optional)", "\uE81D", "ms-settings:privacy-location", CheckLocationAsync),
        new("Screen Capture", "\uE7F4", "", CheckScreenCaptureAsync),
    ];

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
        var isDark = ActualTheme == ElementTheme.Dark;
        var cardBg = new SolidColorBrush(isDark
            ? Color.FromArgb(255, 0x2C, 0x2C, 0x2C)
            : Color.FromArgb(255, 0xF5, 0xF5, 0xF5));

        foreach (var perm in Permissions)
        {
            var (status, granted) = await perm.Check();
            PermRows.Children.Add(BuildRow(perm, status, granted, cardBg, isDark));
        }
    }

    private static FrameworkElement BuildRow(PermDef perm, string status, bool granted, Brush cardBg, bool isDark)
    {
        var statusColor = granted
            ? Color.FromArgb(255, 0x2B, 0xC3, 0x6F)  // green
            : Color.FromArgb(255, 0xF4, 0xA6, 0xB0); // pink

        // Icon badge
        var iconBadge = new Border
        {
            Width = 40, Height = 40,
            CornerRadius = new CornerRadius(20),
            Background = isDark
                ? new SolidColorBrush(Microsoft.UI.Colors.Transparent)
                : new SolidColorBrush(Color.FromArgb(255, 0x33, 0x33, 0x33)),
            Child = new TextBlock
            {
                Text = perm.Glyph,
                FontFamily = IconFonts.SymbolThemeFontFamily,
                FontSize = 20,
                Foreground = new SolidColorBrush(isDark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
        };

        // Title + status
        var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock { Text = perm.Name, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        textStack.Children.Add(new TextBlock { Text = status, FontSize = 13, Foreground = new SolidColorBrush(statusColor) });

        // Open Settings button (only if URI exists)
        FrameworkElement actionCol;
        if (!string.IsNullOrEmpty(perm.SettingsUri))
        {
            var btn = new Button
            {
                Padding = new Thickness(8, 6, 8, 6),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            var btnContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            btnContent.Children.Add(new TextBlock
            {
                Text = "\uE8A7", FontFamily = IconFonts.SymbolThemeFontFamily,
                FontSize = 14, VerticalAlignment = VerticalAlignment.Center
            });
            btnContent.Children.Add(new TextBlock { Text = "Open Settings", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            btn.Content = btnContent;
            var uri = perm.SettingsUri;
            btn.Click += async (_, _) =>
            {
                try { await Windows.System.Launcher.LaunchUriAsync(new Uri(uri)); }
                // slopwatch-ignore: SW003 UI helper action is best-effort and failure should not break the owning UI flow.
                catch { /* best effort */ }
            };
            actionCol = btn;
        }
        else
        {
            actionCol = new Border { Width = 1 };
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
        Grid.SetColumn(iconBadge, 0);
        Grid.SetColumn(textStack, 1);
        Grid.SetColumn(actionCol, 2);
        grid.Children.Add(iconBadge);
        grid.Children.Add(textStack);
        grid.Children.Add(actionCol);

        return new Border
        {
            Child = grid,
            Background = cardBg,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20, 18, 20, 18),
        };
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshPermissions();

    private void BackToWizard_Click(object sender, RoutedEventArgs e)
        => SetupWindow.Active?.NavigateToWizard();

    private void Next_Click(object sender, RoutedEventArgs e)
        => SetupWindow.Active?.NavigateToComplete(true, TimeSpan.Zero, null);

    // ── Permission checks (passive, no OS consent dialogs) ──

    private static Task<(string, bool)> CheckNotificationsAsync()
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

    private static async Task<(string, bool)> CheckCameraAsync()
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

    private static async Task<(string, bool)> CheckMicrophoneAsync()
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

    private static Task<(string, bool)> CheckLocationAsync()
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

    private static Task<(string, bool)> CheckScreenCaptureAsync()
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
