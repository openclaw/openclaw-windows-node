using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Windows;
using Xunit;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class A2UICanvasSurfaceLayoutTests
{
    private readonly UIThreadFixture _ui;

    public A2UICanvasSurfaceLayoutTests(UIThreadFixture ui) => _ui = ui;

    [Fact]
    public async Task DroppingTheSecondSurface_KeepsTheRemainingScrollerInTheSingleHost()
    {
        await _ui.ResetContainerAsync();
        await _ui.RunOnUIAsync(() =>
        {
            var keptText = new TextBlock { Text = "kept surface" };
            var kept = new ScrollViewer { Content = keptText };
            var dropped = new ScrollViewer { Content = new TextBlock { Text = "dropped surface" } };
            var tabs = new Dictionary<string, TabViewItem>(StringComparer.Ordinal);
            var tabView = new TabView { IsAddTabButtonVisible = false };
            var keptTab = new TabViewItem { Header = "kept", Content = kept, Tag = "kept" };
            var droppedTab = new TabViewItem { Header = "dropped", Content = dropped, Tag = "dropped" };
            tabs["kept"] = keptTab;
            tabs["dropped"] = droppedTab;
            tabView.TabItems.Add(keptTab);
            tabView.TabItems.Add(droppedTab);
            var host = new ContentControl();
            _ui.Container.Children.Add(tabView);
            _ui.Container.Children.Add(host);

            CanvasSurfacePlacement.MoveToSingleHost(host, tabView, tabs, kept);
            CanvasSurfacePlacement.MoveToSingleHost(host, tabView, tabs, kept);

            Assert.Same(kept, host.Content);
            Assert.Same(keptText, kept.Content);
            Assert.Empty(tabView.TabItems);
            Assert.Empty(tabs);
        });
    }
}
