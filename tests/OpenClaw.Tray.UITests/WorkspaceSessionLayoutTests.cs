using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using OpenClawTray.Presentation;
using OpenClawTray.Windows;
using Windows.Foundation;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class WorkspaceSessionLayoutTests(UIThreadFixture ui)
{
    [Theory]
    [InlineData("WorkspaceWindow.xaml", "CollapsePaneButton")]
    [InlineData("WorkspaceWindow.xaml", "ReopenPaneButton")]
    [InlineData("HubWindow.xaml", "NavPaneToggleButton")]
    public async Task PaneToggle_LeavesRoundingRoomAroundUnconstrainedGlyph(string file, string name)
    {
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT")
            ?? throw new InvalidOperationException("OPENCLAW_REPO_ROOT is required for production markup proof.");
        var document = XDocument.Load(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Windows", file));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var markup = new XElement(document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name));
        // Mount the production control without its window event handler or localized tooltip.
        markup.Attribute("Click")!.Remove();
        markup.Attribute(x + "Uid")!.Remove();
        markup.SetAttributeValue("Visibility", "Visible");
        markup.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName);

        await ui.RunOnUIAsync(async () =>
        {
            var button = Assert.IsType<Button>(XamlReader.Load(markup.ToString()));
            var icon = Assert.IsType<FontIcon>(button.Content);
            var reference = new FontIcon
            {
                Glyph = icon.Glyph,
                FontFamily = icon.FontFamily,
                FontSize = icon.FontSize,
                IsTextScaleFactorEnabled = icon.IsTextScaleFactorEnabled
            };
            ui.Container.Children.Add(button);
            ui.Container.Children.Add(reference);
            try
            {
                await ui.YieldToRenderAsync();
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                {
                    button.RequestedTheme = theme;
                    button.UpdateLayout();
                    reference.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var size = reference.DesiredSize;
                    Assert.True(size.Width > 0 && size.Height > 0);
                    Assert.InRange(button.ActualWidth, 39, 41);
                    Assert.InRange(button.ActualHeight, 39, 41);
                    var contentWidth = button.ActualWidth - button.Padding.Left - button.Padding.Right
                        - button.BorderThickness.Left - button.BorderThickness.Right;
                    // Leave a physical pixel on each side for fractional-DPI layout rounding.
                    var roundingRoom = 2 / button.XamlRoot.RasterizationScale;
                    Assert.True(contentWidth >= size.Width + roundingRoom,
                        $"Glyph width {size.Width} has no rounding room in content width {contentWidth} at raster scale {button.XamlRoot.RasterizationScale}.");
                    Assert.True(icon.ActualWidth >= size.Width,
                        $"Glyph width {size.Width} was clipped to {icon.ActualWidth} at raster scale {button.XamlRoot.RasterizationScale}.");
                    Assert.True(icon.ActualHeight >= size.Height,
                        $"Glyph height {size.Height} was clipped to {icon.ActualHeight}.");
                    var bounds = icon.TransformToVisual(button).TransformBounds(new Rect(0, 0, size.Width, size.Height));
                    Assert.True(bounds.Left >= 0 && bounds.Top >= 0 &&
                        bounds.Right <= button.ActualWidth && bounds.Bottom <= button.ActualHeight);
                }
            }
            finally
            {
                ui.Container.Children.Remove(button);
                ui.Container.Children.Remove(reference);
            }
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LongTitlesLeaveRoomForBusyOrUnreadStatus(bool pinned, bool working)
    {
        await ui.RunOnUIAsync(() =>
        {
            var session = new WorkspaceSession("agent:test:long", new string('W', 240), "test",
                IsPinned: pinned, IsUnread: true, IsWorking: working);
            var grid = WorkspaceWindow.BuildSessionContent(session);
            grid.Width = 200;
            ui.Container.Children.Add(grid);
            try
            {
                grid.Measure(new Size(200, 40));
                grid.Arrange(new Rect(0, 0, 200, 40));
                grid.UpdateLayout();
                var title = Assert.Single(grid.Children.OfType<TextBlock>());
                Assert.Equal(1, Grid.GetColumn(title));
                Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
                var status = Assert.Single(grid.Children.OfType<FrameworkElement>(), child => Grid.GetColumn(child) == 2);
                var titleLeft = title.TransformToVisual(grid).TransformPoint(new Point()).X;
                var statusLeft = status.TransformToVisual(grid).TransformPoint(new Point()).X;
                Assert.InRange(title.ActualWidth, 1, 185);
                Assert.True(titleLeft + title.ActualWidth <= statusLeft);
                Assert.InRange(statusLeft + status.ActualWidth, 1, 200);
                Assert.True(status.ActualWidth > 0);
            }
            finally
            {
                ui.Container.Children.Remove(grid);
            }
        });
    }
}
