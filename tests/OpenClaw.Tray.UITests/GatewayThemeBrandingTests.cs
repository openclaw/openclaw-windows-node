// Proves the chat working indicator and brand marks follow the applied theme's branding: the claw under the Windows
// default, dots for themes without the mascot (Phosphor, CRT), the brand icon, or no glyph with the status words
// visible, switching live without remounting. The dots and brand pulse only while a run is active and stay fully
// opaque under High Contrast.
// With OPENCLAW_UI_PROOF_DIR set, it also saves a rendering of each state for visual proof.
// Run with dotnet test tests\OpenClaw.Tray.UITests -r win-x64 --filter FullyQualifiedName~GatewayThemeBrandingTests.
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared.Themes;
using OpenClawTray.Chat;
using OpenClawTray.Controls;
using OpenClawTray.Theming;
using Xunit.Abstractions;
using static Microsoft.UI.Reactor.Factories;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class GatewayThemeBrandingTests(UIThreadFixture ui, ITestOutputHelper output)
{
    private static readonly string[] IndicatorIds = ["ChatRunProgressClaw", "ChatRunProgressDots", "ChatRunProgressBrand"];

    [Fact]
    public async Task WorkingIndicatorAndBrandMarkFollowTheThemeBranding()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            using var resources = new ChatThemeProofScope("Dark");
            var highContrast = ChatThemeChanges.IsHighContrast;
            var applicator = new AppThemeApplicator();
            using var host = new ReactorHostControl();
            var mark = new BrandMark { MarkSize = 24 };
            try
            {
                host.Mount(_ => Component<ReactorRunProgress, ReactorRunProgressProps>(new(new(
                    DateTimeOffset.UtcNow.AddSeconds(-5), null, "Working", true, false))));
                mark.VerticalAlignment = VerticalAlignment.Center;
                // The proof frame shows the applied theme's dark canvas behind the mark and indicator.
                var panel = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                    "<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' RequestedTheme='Dark' " +
                    "Background='{ThemeResource SolidBackgroundFillColorBaseBrush}' Padding='16' Width='280' " +
                    "HorizontalAlignment='Left' VerticalAlignment='Top'/>");
                panel.Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { mark, host } };
                ui.Container.Children.Add(panel);

                await WaitForIndicatorAsync(host, "ChatRunProgressClaw");
                Assert.False(mark.ShowsThemeMark);

                Assert.True(applicator.Apply(BuiltinGatewayThemes.All.Single(theme => theme.Id == "crt")));
                await WaitForIndicatorAsync(host, "ChatRunProgressDots");
                Assert.True(mark.ShowsThemeMark);
                await OnboardingArtworkRenderingTests.SaveProofAsync(panel, "branding-crt", output);
                var dots = Assert.Single(TestSupport.FindDescendants<NativeWorkingDots>(host));
                // The pulse starts when the indicator loads, which WinUI raises after it appears.
                await TestSupport.WaitForRenderedConditionAsync(() => dots.IsPulsing == AnimationsEnabled, "dots pulse during the run");
                Assert.Equal(3, dots.Parts.Count);

                // Dimmed dots would fall below the system contrast; High Contrast keeps them opaque and still moving.
                ChatThemeChanges.IsHighContrast = static () => true;
                ChatThemeChanges.Refresh();
                await TestSupport.WaitForRenderedConditionAsync(
                    () => dots.Parts.All(part => part.Opacity == 1) && dots.IsPulsing == AnimationsEnabled,
                    "dots opaque and still moving under High Contrast");
                ChatThemeChanges.IsHighContrast = static () => false;
                ChatThemeChanges.Refresh();
                // Leaving High Contrast restores the resting dim; the local value is what a running pulse animates from.
                // WinUI may hand back the stored opacity at single precision, so compare with a tolerance.
                await TestSupport.WaitForRenderedConditionAsync(
                    () => dots.IsPulsing == AnimationsEnabled &&
                        dots.Parts.All(part => part.ReadLocalValue(UIElement.OpacityProperty) is double opacity &&
                            Math.Abs(opacity - 0.45) < 0.001),
                    "dots dim again after High Contrast",
                    () => $"pulsing={dots.IsPulsing}, animations={AnimationsEnabled}, local opacity=" +
                        string.Join(", ", dots.Parts.Select(part => part.ReadLocalValue(UIElement.OpacityProperty))));

                Assert.True(applicator.Apply(Branded("brand")));
                await WaitForIndicatorAsync(host, "ChatRunProgressBrand");
                var brand = Assert.Single(TestSupport.FindDescendants<NativeWorkingBrand>(host));
                await TestSupport.WaitForRenderedConditionAsync(() => brand.IsPulsing == AnimationsEnabled, "brand icon pulses during the run");
                // The brand icon is the claw image here (this plugin theme keeps the mascot); it must actually decode.
                await TestSupport.WaitForRenderedConditionAsync(
                    () => TestSupport.FindDescendants<Image>(brand).Any(image =>
                        image.Visibility == Visibility.Visible && image.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapImage { PixelWidth: > 0 }),
                    "brand icon decoded");
                await OnboardingArtworkRenderingTests.SaveProofAsync(panel, "branding-plugin-brand-indicator", output);
                // The unmounted dots stopped their storyboard.
                await TestSupport.WaitForRenderedConditionAsync(() => !dots.IsPulsing, "dots stop when replaced");

                Assert.True(applicator.Apply(Branded("none")));
                await WaitForIndicatorAsync(host, null);
                // Without a glyph the status words stay visible instead of only the elapsed time.
                Assert.StartsWith("Working", StatusText(host).Text);
                await OnboardingArtworkRenderingTests.SaveProofAsync(panel, "branding-plugin-no-indicator", output);

                Assert.True(applicator.Apply(null));
                await WaitForIndicatorAsync(host, "ChatRunProgressClaw");
                Assert.False(mark.ShowsThemeMark);
                Assert.DoesNotContain("Working", StatusText(host).Text);
                await OnboardingArtworkRenderingTests.SaveProofAsync(panel, "branding-windows-default", output);
            }
            finally
            {
                ChatThemeChanges.IsHighContrast = highContrast;
                applicator.Apply(null);
                ui.Container.Children.Clear();
            }
        });
    }

    private static bool AnimationsEnabled => new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    // A plugin theme that keeps the claw mascot but asks for another working indicator.
    private static ResolvedTheme Branded(string indicator) =>
        BuiltinGatewayThemes.All.Single(theme => theme.Id == "tide") with
        {
            Id = "acme/" + indicator,
            Source = GatewayThemeSource.Plugin,
            Branding = ThemeBranding.Resolve("claw", null, indicator),
        };

    private static async Task WaitForIndicatorAsync(FrameworkElement host, string? expected)
    {
        await TestSupport.WaitForRenderedConditionAsync(
            () => IndicatorIds.Where(id => Present(host, id)).SequenceEqual(expected is null ? [] : [expected]),
            $"working indicator is {expected ?? "absent"}");
    }

    private static bool Present(FrameworkElement host, string automationId) =>
        TestSupport.FindDescendants<FrameworkElement>(host).Any(element => AutomationProperties.GetAutomationId(element) == automationId);

    private static TextBlock StatusText(FrameworkElement host) =>
        Assert.Single(TestSupport.FindDescendants<TextBlock>(host),
            text => AutomationProperties.GetAutomationId(text) == "ChatRunProgressText");
}
