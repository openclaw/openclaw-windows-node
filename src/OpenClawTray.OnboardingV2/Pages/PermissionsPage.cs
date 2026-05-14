using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Onboarding.V2.Pages;

/// <summary>
/// Grant permissions page (Dialog-5).
///
/// Layout:
///   * Centered title "Grant permissions" (28pt SemiBold).
///   * Centered intro paragraph (14pt 70% white, max width ~480).
///   * Five row cards in a vertical list, each:
///     [icon 28x28] [title + status (green when enabled)] [Open Settings link]
///     The Screen Capture row has no Open Settings link — its status
///     describes the per-capture picker instead.
///   * "Refresh status" hyperlink with circular-arrow glyph in the
///     bottom-right under the list.
///
/// Status colour and Open-Settings visibility are driven by a
/// PermissionRow record; the preview seeds them from the
/// OPENCLAW_PREVIEW_PERMS_SCENARIO env var (today: only "all-granted"
/// matches Dialog-5; the real PermissionChecker plugs in here at
/// cutover).
/// </summary>
public sealed class PermissionsPage : Component<OnboardingV2State>
{
    private sealed record PermissionRow(
        string IconAsset,
        string Title,
        string Status,
        bool ShowOpenSettings,
        bool StatusIsAccent);

    private static IReadOnlyList<PermissionRow> AllGranted = new[]
    {
        new PermissionRow("ms-appx:///Assets/Setup/PermNotifications.png", V2Strings.Get("V2_Permissions_Row_Notifications"), V2Strings.Get("V2_Permissions_Status_Enabled"), true, true),
        new PermissionRow("ms-appx:///Assets/Setup/PermCamera.png", V2Strings.Get("V2_Permissions_Row_Camera"), V2Strings.Get("V2_Permissions_Status_Enabled"), true, true),
        new PermissionRow("ms-appx:///Assets/Setup/PermMicrophone.png", V2Strings.Get("V2_Permissions_Row_Microphone"), V2Strings.Get("V2_Permissions_Status_Enabled"), true, true),
        new PermissionRow("ms-appx:///Assets/Setup/PermLocation.png", V2Strings.Get("V2_Permissions_Row_Location"), V2Strings.Get("V2_Permissions_Status_Enabled"), true, true),
        new PermissionRow("ms-appx:///Assets/Setup/PermScreenCapture.png", V2Strings.Get("V2_Permissions_Row_ScreenCapture"), V2Strings.Get("V2_Permissions_Status_Available"), false, true),
    };

    public override Element Render()
    {
        var rows = AllGranted; // env-var scenarios land in fake-services/F1 overlay later.
        var rowEls = new List<Element>();
        foreach (var row in rows)
        {
            rowEls.Add(BuildRow(row));
        }

        var refreshLink = Button(
            $"\u21BB  {V2Strings.Get("V2_Permissions_Refresh")}",
            () => { /* page-permissions wiring later */ })
            .HAlign(HorizontalAlignment.Right)
            .Set(b =>
            {
                b.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x2C, 0x2C, 0x2C));
                b.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                b.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xE0, 0xE0, 0xE0));
                b.FontSize = 14;
                b.UseSystemFocusVisuals = false;
                b.Padding = new Thickness(20, 10, 20, 10);
                b.CornerRadius = new CornerRadius(8);
                b.Resources["ButtonBackground"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x2C, 0x2C, 0x2C));
                b.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x36, 0x36, 0x36));
                b.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x42, 0x42, 0x42));
                b.Resources["ButtonForeground"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xE0, 0xE0, 0xE0));
                b.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xF0, 0xF0, 0xF0));
                b.Resources["ButtonForegroundPressed"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xF0, 0xF0, 0xF0));
                b.Resources["ButtonBorderBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            });

        return VStack(0,
            new BorderElement(null).Height(36),
            TextBlock(V2Strings.Get("V2_Permissions_Title"))
                .FontSize(28)
                .SemiBold()
                .HAlign(HorizontalAlignment.Center),
            new BorderElement(null).Height(16),
            TextBlock(V2Strings.Get("V2_Permissions_Body"))
                .FontSize(14)
                .HAlign(HorizontalAlignment.Center)
                .TextWrapping()
                .MaxWidth(480)
                .Set(t =>
                {
                    t.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xC8, 0xC8, 0xC8));
                    t.TextAlignment = TextAlignment.Center;
                }),
            new BorderElement(null).Height(28),
            VStack(8, rowEls.ToArray())
                .Margin(48, 0, 48, 0),
            new BorderElement(null).Height(16),
            refreshLink.Margin(0, 0, 48, 0)
        )
        .HAlign(HorizontalAlignment.Stretch)
        .VAlign(VerticalAlignment.Top);
    }

    private static Element BuildRow(PermissionRow row)
    {
        var statusBrush = new SolidColorBrush(row.StatusIsAccent
            ? Microsoft.UI.ColorHelper.FromArgb(255, 0x6D, 0xC8, 0x68) // accent green for Enabled / Available
            : Microsoft.UI.ColorHelper.FromArgb(255, 0xA0, 0xA0, 0xA0));

        var inner = Grid(
            new[] { "auto", "*", "auto" },
            new[] { "auto" },
            // Icon column
            Image(row.IconAsset)
                .Width(28)
                .Height(28)
                .VAlign(VerticalAlignment.Center)
                .Margin(4, 0, 16, 0)
                .Grid(row: 0, column: 0),

            // Title + status
            VStack(2,
                TextBlock(row.Title)
                    .FontSize(15)
                    .SemiBold(),
                TextBlock(row.Status)
                    .FontSize(13)
                    .Set(t => t.Foreground = statusBrush)
            )
            .VAlign(VerticalAlignment.Center)
            .Grid(row: 0, column: 1),

            // Open Settings link (omitted for Screen Capture per design)
            row.ShowOpenSettings
                ? Button(
                    $"\u2197  {V2Strings.Get("V2_Permissions_OpenSettings")}",
                    () => { /* page-permissions wiring later */ })
                  .Set(b =>
                  {
                      b.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                      b.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                      b.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xE0, 0xE0, 0xE0));
                      b.FontSize = 14;
                      b.UseSystemFocusVisuals = false;
                      b.Padding = new Thickness(8, 6, 8, 6);
                      b.Resources["ButtonBackground"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                      b.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(40, 0xFF, 0xFF, 0xFF));
                      b.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(70, 0xFF, 0xFF, 0xFF));
                      b.Resources["ButtonForeground"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xE0, 0xE0, 0xE0));
                      b.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xFF, 0xFF, 0xFF));
                      b.Resources["ButtonForegroundPressed"] = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xFF, 0xFF, 0xFF));
                      b.Resources["ButtonBorderBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                  })
                  .HAlign(HorizontalAlignment.Right)
                  .VAlign(VerticalAlignment.Center)
                  .Grid(row: 0, column: 2)
                : new BorderElement(null).Width(1).Grid(row: 0, column: 2)
        );

        return new BorderElement(inner)
            .Background("#2C2C2C")
            .Padding(20, 18, 20, 18)
            .Set(b => b.CornerRadius = new CornerRadius(8));
    }
}

