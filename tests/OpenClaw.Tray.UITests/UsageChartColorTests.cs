using Microsoft.UI;
using Microsoft.UI.Reactor;
using System.Xml.Linq;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.Helpers;
using OpenClawTray.Pages.Usage;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class UsageChartColorTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData(ElementTheme.Light)]
    [InlineData(ElementTheme.Dark)]
    public async Task DailyBars_UseSolidNativeAccentForReportedAndCurrentDays(ElementTheme theme)
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                UsageThemeProofResources.Ensure();
                ui.Container.RequestedTheme = theme;
                host = new ReactorHostControl { Width = 400, RequestedTheme = theme };
                // This shared test Application starts Light. Scope the production brush to the mounted theme;
                // separate fresh-process Light/Dark fixture captures prove application theme selection.
                var resources = Assert.IsType<ResourceDictionary>(
                    Application.Current.Resources.ThemeDictionaries[theme == ElementTheme.Dark ? "Default" : "Light"]);
                host.Resources["UsageChartBarBrush"] = resources["UsageChartBarBrush"];
                host.Mount(_ => UsageCharts.Columns(
                    [new("complete", 50, "Complete", "Complete day", false, false),
                     new("today", 25, "Today", "Current day", false, false, Today: true)],
                    100, value => value.ToString(), "UsageGradientProof").RequestedTheme(theme));
                ui.Container.Children.Add(host);
            });
            await ui.RunOnUIAsync(() => WaitForRenderedConditionAsync(() =>
                FindDescendants<Border>(host!).Any(element =>
                    AutomationProperties.GetAutomationId(element) == "UsageGradientProof_mark_today" && element.ActualHeight > 0),
                "Usage gradient data marks mounted"));
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                var complete = Assert.Single(FindDescendants<Border>(host!), element =>
                    AutomationProperties.GetAutomationId(element) == "UsageGradientProof_mark_complete");
                var today = Assert.Single(FindDescendants<Border>(host!), element =>
                    AutomationProperties.GetAutomationId(element) == "UsageGradientProof_mark_today");
                var fill = Assert.IsType<SolidColorBrush>(complete.Background);
                Windows.UI.Color AccentColor(string key)
                {
                    var reference = (Border)XamlReader.Load(
                        $$"""<Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" RequestedTheme="{{theme}}"><Border.Background><SolidColorBrush Color="{ThemeResource {{key}}}"/></Border.Background></Border>""");
                    return Assert.IsType<SolidColorBrush>(reference.Background).Color;
                }
                Assert.Equal(AccentColor("SystemAccentColor"), fill.Color);
                Assert.Equal(1, complete.Opacity);
                Assert.Equal(1, today.Opacity);
                Assert.Equal(fill.Color, Assert.IsType<SolidColorBrush>(today.Background).Color);
                Assert.InRange(Math.Abs(complete.ActualHeight - 90), 0, 1);
                Assert.InRange(Math.Abs(today.ActualHeight - 45), 0, 1);
            });
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                if (host is not null) { ui.Container.Children.Remove(host); host.Dispose(); }
                ui.Container.RequestedTheme = ElementTheme.Default;
            });
        }
    }

    [Fact]
    public async Task HighContrastBarResource_IsSolidSystemHighlightRatherThanAGradient()
    {
        await ui.RunOnUIAsync(() =>
        {
            var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")!;
            var app = XElement.Load(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "App.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var highContrast = app.Descendants().Single(element => (string?)element.Attribute(x + "Key") == "HighContrast");
            var brush = new XElement(highContrast.Elements().Single(element => (string?)element.Attribute(x + "Key") == "UsageChartBarBrush"));
            Assert.Equal("SolidColorBrush", brush.Name.LocalName);
            Assert.Equal("{ThemeResource SystemColorHighlightColor}", (string?)brush.Attribute("Color"));
            brush.Attribute(x + "Key")!.Remove();
            Assert.IsType<SolidColorBrush>(XamlReader.Load(brush.ToString()));
        });
    }

    [Fact]
    public async Task DataMarksRemainContrastingWhenTheButtonAccentMatchesThePlot()
    {
        await ui.ResetContainerAsync();
        ReactorHostControl? host = null;
        UIElement? root = null;
        Border? buttonFill = null;
        var overridden = new List<(ResourceDictionary Dictionary, string Key, bool Existed, object? Previous)>();
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                UsageThemeProofResources.Ensure();
                // Supply the native HC collision (button accent equals the plot) as theme input, without changing the
                // desktop's theme. Keys are overridden in place and restored: Reactor reuses compiled styles bound to
                // the application's dictionaries, so swapping the whole resource set would leave earlier brushes live.
                foreach (var dictionary in Application.Current.Resources.ThemeDictionaries.Values
                    .OfType<ResourceDictionary>().Prepend(Application.Current.Resources))
                {
                    foreach (var (key, color) in new[] { ("AccentFillColorDefaultBrush", Colors.Black), ("UsageChartBarBrush", Colors.White) })
                    {
                        var existed = dictionary.TryGetValue(key, out var previous);
                        overridden.Add((dictionary, key, existed, previous));
                        dictionary[key] = new SolidColorBrush(color);
                    }
                }
                buttonFill = (Border)XamlReader.Load(
                    """<Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Background="{ThemeResource AccentFillColorDefaultBrush}" Width="20" Height="20"/>""");
                ui.Container.Children.Add(buttonFill);
                host = new ReactorHostControl { Width = 320, RequestedTheme = ElementTheme.Light };
                var chart = UsageCharts.Columns(
                    [new("day", 50, "Day", "Sample day", Pending: false, LowerBound: false)],
                    100, value => value.ToString(),
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
                    border => AutomationProperties.GetAutomationId(border) == "UsageColorProof_mark_day");
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
                foreach (var (dictionary, key, existed, previous) in Enumerable.Reverse(overridden))
                {
                    if (existed) dictionary[key] = previous;
                    else dictionary.Remove(key);
                }
            });
        }
    }
}
