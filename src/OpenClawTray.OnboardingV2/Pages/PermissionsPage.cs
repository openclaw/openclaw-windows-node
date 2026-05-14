using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Core;
using static OpenClawTray.FunctionalUI.Factories;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace OpenClawTray.Onboarding.V2.Pages;

/// <summary>
/// Helper: build a button content panel with a Segoe Fluent Icons glyph
/// followed by a label. Used by the Permissions page's "Open Settings"
/// and "Refresh status" affordances so the icons are crisp and properly
/// sized (matches Dialog-5 — Unicode-symbol fallbacks render too small).
/// </summary>
internal static class GlyphButtonContent
{
    public static StackPanel Build(string glyph, string label, double glyphSize = 16, double spacing = 8)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var glyphTb = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = glyphSize,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, spacing, 0),
        };
        var labelTb = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(glyphTb);
        stack.Children.Add(labelTb);
        return stack;
    }
}

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
        var theme = Props.EffectiveTheme;
        // If the host has populated real PermissionRowSnapshot data, render those;
        // otherwise fall back to the all-granted preview rows so the page still
        // looks right in the standalone preview (no real PermissionChecker).
        var rowEls = new List<Element>();
        if (Props.Permissions is { Count: > 0 } realRows)
        {
            foreach (var snapshot in realRows)
            {
                rowEls.Add(BuildRowFromSnapshot(theme, snapshot));
            }
        }
        else
        {
            foreach (var row in AllGranted)
            {
                rowEls.Add(BuildRow(theme, row));
            }
        }

        var refreshLink = Button(
            V2Strings.Get("V2_Permissions_Refresh"),
            () => Props.RequestPermissionsRefresh())
            .HAlign(HorizontalAlignment.Right)
            .Set(b =>
            {
                // Segoe Fluent Icons "Refresh" (\uE72C) — circular arrow at
                // 16pt, paired with the label, so the glyph reads at the
                // same size as the comp instead of the tiny U+21BB.
                b.Content = GlyphButtonContent.Build("\uE72C", V2Strings.Get("V2_Permissions_Refresh"), glyphSize: 16);
                b.Background = V2Theme.CardBackground(theme);
                b.BorderBrush = V2Theme.Transparent();
                b.Foreground = V2Theme.TextPrimary(theme);
                b.FontSize = 14;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "V2_Permissions_Refresh");
                b.Padding = new Thickness(20, 10, 20, 10);
                b.CornerRadius = new CornerRadius(8);
                b.Resources["ButtonBackground"] = V2Theme.CardBackground(theme);
                b.Resources["ButtonBackgroundPointerOver"] = V2Theme.CardBackgroundHover(theme);
                b.Resources["ButtonBackgroundPressed"] = V2Theme.CardBackgroundPressed(theme);
                b.Resources["ButtonForeground"] = V2Theme.TextPrimary(theme);
                b.Resources["ButtonForegroundPointerOver"] = V2Theme.TextStrong(theme);
                b.Resources["ButtonForegroundPressed"] = V2Theme.TextStrong(theme);
                b.Resources["ButtonBorderBrush"] = V2Theme.Transparent();
            });

        return VStack(0,
            new BorderElement(null).Height(36),
            TextBlock(V2Strings.Get("V2_Permissions_Title"))
                .FontSize(28)
                .SemiBold()
                .HAlign(HorizontalAlignment.Center)
                .Set(t => t.Foreground = V2Theme.TextStrong(theme)),
            new BorderElement(null).Height(16),
            TextBlock(V2Strings.Get("V2_Permissions_Body"))
                .FontSize(14)
                .HAlign(HorizontalAlignment.Center)
                .TextWrapping()
                .MaxWidth(480)
                .Set(t =>
                {
                    t.Foreground = V2Theme.TextSecondary(theme);
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

    /// <summary>
    /// Render a permission row from real <see cref="OnboardingV2State.PermissionRowSnapshot"/>
    /// data. Identical visual treatment to <see cref="BuildRow"/> but the
    /// status colour reflects severity, the icon comes from the snapshot,
    /// and the Open-Settings button actually launches the settings URI.
    /// </summary>
    private static Element BuildRowFromSnapshot(ElementTheme theme, OnboardingV2State.PermissionRowSnapshot snapshot)
    {
        var statusBrush = snapshot.Severity switch
        {
            OnboardingV2State.PermissionSeverity.Granted => V2Theme.AccentGreen(),
            OnboardingV2State.PermissionSeverity.Denied => V2Theme.BadgeErrorPink(),
            _ => V2Theme.TextSubtle(theme),
        };

        var inner = Grid(
            new[] { "auto", "*", "auto" },
            new[] { "auto" },
            // Constant-colour dark badge so the white-on-dark icons read on light cards too.
            new BorderElement(
                Image(snapshot.IconAsset)
                    .Width(24)
                    .Height(24)
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
            )
            .Width(40)
            .Height(40)
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 0, 16, 0)
            .Background(theme == ElementTheme.Light
                ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x33, 0x33, 0x33))
                : V2Theme.Transparent())
            .Set(b => b.CornerRadius = new CornerRadius(20))
            .Grid(row: 0, column: 0),

            VStack(2,
                TextBlock(snapshot.Label)
                    .FontSize(15)
                    .SemiBold()
                    .Set(t => t.Foreground = V2Theme.TextPrimary(theme)),
                TextBlock(snapshot.StatusLabel)
                    .FontSize(13)
                    .Set(t => t.Foreground = statusBrush)
            )
            .VAlign(VerticalAlignment.Center)
            .Grid(row: 0, column: 1),

            snapshot.ShowOpenSettings && snapshot.SettingsUri is { } uri
                ? Button(
                    V2Strings.Get("V2_Permissions_OpenSettings"),
                    () => _ = LaunchSettingsUriAsync(uri))
                  .Set(b =>
                  {
                      b.Content = GlyphButtonContent.Build("\uE8A7", V2Strings.Get("V2_Permissions_OpenSettings"), glyphSize: 16);
                      b.Background = V2Theme.Transparent();
                      b.BorderBrush = V2Theme.Transparent();
                      b.Foreground = V2Theme.TextPrimary(theme);
                      b.FontSize = 14;
                      Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "V2_Permissions_OpenSettings");
                      b.Padding = new Thickness(8, 6, 8, 6);
                      b.Resources["ButtonBackground"] = V2Theme.Transparent();
                      b.Resources["ButtonBackgroundPointerOver"] = V2Theme.ButtonOverlayHover(theme);
                      b.Resources["ButtonBackgroundPressed"] = V2Theme.ButtonOverlayPressed(theme);
                      b.Resources["ButtonForeground"] = V2Theme.TextPrimary(theme);
                      b.Resources["ButtonForegroundPointerOver"] = V2Theme.TextStrong(theme);
                      b.Resources["ButtonForegroundPressed"] = V2Theme.TextStrong(theme);
                      b.Resources["ButtonBorderBrush"] = V2Theme.Transparent();
                  })
                  .HAlign(HorizontalAlignment.Right)
                  .VAlign(VerticalAlignment.Center)
                  .Grid(row: 0, column: 2)
                : new BorderElement(null).Width(1).Grid(row: 0, column: 2)
        );

        return new BorderElement(inner)
            .Background(V2Theme.CardBackground(theme))
            .Padding(20, 18, 20, 18)
            .Set(b => b.CornerRadius = new CornerRadius(8));
    }

    /// <summary>
    /// Launch a permission's settings URI via Windows Launcher. Restricted
    /// to ms-settings:// URIs to avoid arbitrary protocol launches (matches
    /// the legacy <c>OnboardingTray.Onboarding.Pages.PermissionsPage</c>
    /// security gate).
    /// </summary>
    private static async System.Threading.Tasks.Task LaunchSettingsUriAsync(Uri uri)
    {
        if (!uri.Scheme.Equals("ms-settings", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
        catch
        {
            // Non-fatal — user can navigate to Settings manually; refresh
            // status will pick up the new state when they come back.
        }
    }

    private static Element BuildRow(ElementTheme theme, PermissionRow row)
    {
        var statusBrush = row.StatusIsAccent
            ? V2Theme.AccentGreen()
            : V2Theme.TextSubtle(theme);

        var inner = Grid(
            new[] { "auto", "*", "auto" },
            new[] { "auto" },
            // Icon column — the designer PNGs are white/light line art that
            // disappears on a light card, so wrap them in a dark constant-
            // colour badge that works in both themes. The badge size matches
            // the design (40dp circle with 24dp icon).
            new BorderElement(
                Image(row.IconAsset)
                    .Width(24)
                    .Height(24)
                    .HAlign(HorizontalAlignment.Center)
                    .VAlign(VerticalAlignment.Center)
            )
            .Width(40)
            .Height(40)
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 0, 16, 0)
            .Background(theme == ElementTheme.Light
                ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x33, 0x33, 0x33))
                : V2Theme.Transparent())
            .Set(b => b.CornerRadius = new CornerRadius(20))
            .Grid(row: 0, column: 0),

            // Title + status
            VStack(2,
                TextBlock(row.Title)
                    .FontSize(15)
                    .SemiBold()
                    .Set(t => t.Foreground = V2Theme.TextPrimary(theme)),
                TextBlock(row.Status)
                    .FontSize(13)
                    .Set(t => t.Foreground = statusBrush)
            )
            .VAlign(VerticalAlignment.Center)
            .Grid(row: 0, column: 1),

            // Open Settings link (omitted for Screen Capture per design)
            row.ShowOpenSettings
                ? Button(
                    V2Strings.Get("V2_Permissions_OpenSettings"),
                    () => { /* page-permissions wiring later */ })
                  .Set(b =>
                  {
                      // Segoe Fluent Icons "OpenInNewWindow" (\uE8A7) —
                      // square-with-arrow icon that matches Dialog-5.
                      b.Content = GlyphButtonContent.Build("\uE8A7", V2Strings.Get("V2_Permissions_OpenSettings"), glyphSize: 16);
                      b.Background = V2Theme.Transparent();
                      b.BorderBrush = V2Theme.Transparent();
                      b.Foreground = V2Theme.TextPrimary(theme);
                      b.FontSize = 14;
                      Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "V2_Permissions_OpenSettings");
                      b.Padding = new Thickness(8, 6, 8, 6);
                      b.Resources["ButtonBackground"] = V2Theme.Transparent();
                      b.Resources["ButtonBackgroundPointerOver"] = V2Theme.ButtonOverlayHover(theme);
                      b.Resources["ButtonBackgroundPressed"] = V2Theme.ButtonOverlayPressed(theme);
                      b.Resources["ButtonForeground"] = V2Theme.TextPrimary(theme);
                      b.Resources["ButtonForegroundPointerOver"] = V2Theme.TextStrong(theme);
                      b.Resources["ButtonForegroundPressed"] = V2Theme.TextStrong(theme);
                      b.Resources["ButtonBorderBrush"] = V2Theme.Transparent();
                  })
                  .HAlign(HorizontalAlignment.Right)
                  .VAlign(VerticalAlignment.Center)
                  .Grid(row: 0, column: 2)
                : new BorderElement(null).Width(1).Grid(row: 0, column: 2)
        );

        return new BorderElement(inner)
            .Background(V2Theme.CardBackground(theme))
            .Padding(20, 18, 20, 18)
            .Set(b => b.CornerRadius = new CornerRadius(8));
    }
}
