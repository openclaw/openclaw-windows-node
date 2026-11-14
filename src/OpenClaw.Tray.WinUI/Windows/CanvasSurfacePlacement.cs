using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Windows;

/// <summary>
/// Moves the remaining canvas scroller from its tab into the single-surface host.
/// WinUI gives an element one parent. The tab must release the scroller before
/// the host adopts it, or the move throws and the remaining surface stays blank.
/// </summary>
internal static class CanvasSurfacePlacement
{
    internal static void MoveToSingleHost(
        ContentControl singleHost,
        TabView tabs,
        IDictionary<string, TabViewItem> surfaceTabs,
        ScrollViewer scroller)
    {
        foreach (var tab in surfaceTabs.Values)
        {
            if (ReferenceEquals(tab.Content, scroller))
                tab.Content = null;
        }

        if (!ReferenceEquals(singleHost.Content, scroller))
            singleHost.Content = scroller;

        tabs.TabItems.Clear();
        surfaceTabs.Clear();
    }
}
