using Microsoft.UI;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Helpers;
using OpenClawTray.Pages.Usage;
using OpenClawTray.Presentation.Usage;
using System.Globalization;
using System.Xml.Linq;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class UsageChartColorTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData("Light", 0.18)]
    [InlineData("Dark", 0.18)]
    [InlineData("HighContrast", 1)]
    public async Task ActivityResources_ResolveTheProductionTextPairAndOpacity(string theme, double expectedOpacity)
    {
        var repo = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")
            ?? throw new InvalidOperationException("OPENCLAW_REPO_ROOT is required for production resource proof.");
        var app = XElement.Load(Path.Combine(repo, "src", "OpenClaw.Tray.WinUI", "App.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dictionary = app.Descendants().Single(element => element.Name.LocalName == "ResourceDictionary.ThemeDictionaries")
            .Elements().Single(element => (string?)element.Attribute(x + "Key") == (theme == "Dark" ? "Default" : theme));
        var opacity = dictionary.Elements().Single(element => (string?)element.Attribute(x + "Key") == "UsageActivityMinimumOpacity");
        Assert.Equal(expectedOpacity, double.Parse(opacity.Value, CultureInfo.InvariantCulture));
        var textColor = theme == "HighContrast" ? "SystemColorHighlightTextColor" : "TextOnAccentFillColorPrimary";
        var brush = dictionary.Elements().Single(element => (string?)element.Attribute(x + "Key") == "UsageActivityTextBrush");
        // Native theme colors can coincide on the current desktop; also guard the semantic resource pairing.
        Assert.Equal("{ThemeResource " + textColor + "}", (string?)brush.Attribute("Color"));

        await ui.ResetContainerAsync();
        ChatThemeProofScope? resources = null;
        Grid? root = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                resources = new ChatThemeProofScope(theme);
                Assert.Equal(expectedOpacity, Microsoft.UI.Reactor.ThemeResource.Double("UsageActivityMinimumOpacity"), 6);
                root = (Grid)XamlReader.Load($$"""
                    <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                          RequestedTheme="{{resources.ElementTheme}}">
                        <Border x:Name="Actual" Background="{ThemeResource UsageActivityTextBrush}" />
                        <Border x:Name="Expected">
                            <Border.Background>
                                <SolidColorBrush Color="{ThemeResource {{textColor}}}" />
                            </Border.Background>
                        </Border>
                    </Grid>
                    """);
                ui.Container.Children.Add(root);
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                var actual = Assert.IsType<Border>(root!.FindName("Actual"));
                var expected = Assert.IsType<Border>(root.FindName("Expected"));
                Assert.Equal(Assert.IsType<SolidColorBrush>(expected.Background).Color,
                    Assert.IsType<SolidColorBrush>(actual.Background).Color);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (root is not null) ui.Container.Children.Remove(root);
                resources?.Dispose();
            });
        }
    }

    [Theory]
    [InlineData(0.4, true, 1)]
    [InlineData(1.0, true, 1)]
    [InlineData(0.4, false, 0.508)]
    public async Task ActivityCells_KeepTextContrastingWithoutChangingUnlabelledIntensity(
        double intensity, bool valuesInside, double expectedOpacity)
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        ChatThemeProofScope? resources = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                resources = new ChatThemeProofScope("HighContrast");
                foreach (var dictionary in Application.Current.Resources.ThemeDictionaries.Values
                    .OfType<ResourceDictionary>().Prepend(Application.Current.Resources))
                {
                    dictionary["UsagePalette1Brush"] = new SolidColorBrush(Colors.White);
                    dictionary["TextOnAccentFillColorPrimaryBrush"] = new SolidColorBrush(Colors.White);
                    dictionary["TextFillColorPrimaryBrush"] = new SolidColorBrush(Colors.White);
                    dictionary["UsageActivityTextBrush"] = new SolidColorBrush(Colors.Black);
                    dictionary["UsageActivityMinimumOpacity"] = 1d;
                }
                host = new ReactorHostControl { Width = 320, RequestedTheme = ElementTheme.Light };
                var chart = UsageCharts.ActivityStrip([new(0, 100, intensity, false)],
                    _ => "Monday", _ => true, null, "WeekdayColorProof", 64, valuesInside);
                var root = host.Reconciler.Mount(chart, static () => { });
                host.Content = root;
                ui.Container.Children.Add(host);
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(async () =>
            {
                var tile = Assert.Single(FindLogical<FrameworkElement>(host!),
                    element => AutomationProperties.GetAutomationId(element) == "WeekdayColorProof_0");
                var mark = Assert.Single(FindLogical<Border>(tile), border => border.Child is null);
                await WaitForRenderedConditionAsync(() => mark.ActualWidth > 0 && mark.ActualHeight > 0,
                    "Weekday tile layout", () => $"Width={mark.ActualWidth}, Height={mark.ActualHeight}");
                Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(mark.Background).Color);
                Assert.Equal(expectedOpacity, mark.Opacity, 5);
                var labels = FindLogical<TextBlock>(tile).ToList();
                Assert.Equal(valuesInside ? 2 : 0, labels.Count);
                await WaitForRenderedConditionAsync(
                    () => labels.All(text => text.ActualWidth > 0 && text.ActualHeight > 0),
                    "Activity label layout",
                    () => string.Join("; ", labels.Select(text => $"{text.Text}: {text.ActualWidth}x{text.ActualHeight}")));
                Assert.All(labels, text =>
                {
                    Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(text.Foreground).Color);
                    Assert.Equal(1, text.Opacity);
                });
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() => VisualTestCapture.CaptureAsync(host!, !valuesInside ? "UsageHourControlledHCIntensity"
                : intensity == 1 ? "UsageWeekdayControlledHCStrong" : "UsageWeekdayControlledHCWeak"));
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (host is not null)
                {
                    ui.Container.Children.Remove(host);
                    host.Content = null;
                    host.Dispose();
                }
                resources?.Dispose();
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DataMarksRemainContrastingWhenTheButtonAccentMatchesThePlot(bool interactive)
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        UIElement? root = null;
        Border? buttonFill = null;
        ChatThemeProofScope? resources = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                resources = new ChatThemeProofScope("HighContrast");
                // Supply the native HC collision as theme input, without changing the desktop's theme.
                foreach (var dictionary in Application.Current.Resources.ThemeDictionaries.Values
                    .OfType<ResourceDictionary>().Prepend(Application.Current.Resources))
                {
                    dictionary["AccentFillColorDefaultBrush"] = new SolidColorBrush(Colors.Black);
                    dictionary["UsagePalette1Brush"] = new SolidColorBrush(Colors.White);
                }
                buttonFill = (Border)XamlReader.Load(
                    """<Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Background="{ThemeResource AccentFillColorDefaultBrush}" Width="20" Height="20"/>""");
                ui.Container.Children.Add(buttonFill);
                host = new ReactorHostControl { Width = 320, RequestedTheme = ElementTheme.Light };
                var chart = UsageCharts.Columns(
                    [new("day", 50, [new("total", 50)], "Day", "Sample day", false, false, false)],
                    [new("total", UsageSeriesKind.Total, null, 0)],
                    100, value => value.ToString(),
                    interactive ? static (_, _) => { } : null,
                    "UsageColorProof");
                root = host.Reconciler.Mount(chart, static () => { });
                host.Content = root;
                ui.Container.Children.Add(host);
            });
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(async () =>
            {
                Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(buttonFill!.Background).Color);
                var bar = Assert.Single(FindLogical<FrameworkElement>(root!),
                    element => AutomationProperties.GetName(element) == "Sample day");
                var mark = Assert.Single(FindLogical<Border>(bar),
                    border => border.Child is null);
                await WaitForRenderedConditionAsync(
                    () => mark.ActualWidth > 0 && mark.ActualHeight > 0,
                    "Usage chart data mark layout",
                    () => $"Width={mark.ActualWidth}, Height={mark.ActualHeight}");
                Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(mark.Background).Color);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (host is not null)
                {
                    ui.Container.Children.Remove(host);
                    host.Content = null;
                    host.Dispose();
                }
                if (buttonFill is not null) ui.Container.Children.Remove(buttonFill);
                resources?.Dispose();
            });
        }
    }
}
