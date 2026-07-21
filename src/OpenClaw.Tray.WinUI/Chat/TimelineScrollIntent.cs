using System;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Chat;

internal static class TimelineScrollIntent
{
    private static readonly ConditionalWeakTable<ScrollViewer, Action> s_handlers = new();

    public static void Register(ScrollViewer scrollViewer, Action handler)
    {
        s_handlers.Remove(scrollViewer);
        s_handlers.Add(scrollViewer, handler);
    }

    public static bool NotifyUserIntent(ScrollViewer scrollViewer)
    {
        if (!s_handlers.TryGetValue(scrollViewer, out var handler))
            return false;

        handler();
        return true;
    }
}
