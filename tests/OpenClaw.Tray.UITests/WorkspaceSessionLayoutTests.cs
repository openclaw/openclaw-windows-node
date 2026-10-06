using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Presentation;
using OpenClawTray.Windows;
using Windows.Foundation;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class WorkspaceSessionLayoutTests(UIThreadFixture ui)
{
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
