// Pure helper for ChatPage panel-visibility transitions. Kept free of
// WinUI/Microsoft.UI.Xaml types so it can be cross-compiled into
// OpenClaw.Tray.Tests for regression coverage. See ChatPage.xaml.cs for
// the WinUI adapter that maps ChatPanelVisibility onto Visibility on the
// real panels.
//
// All call sites mutate UI elements and MUST run on the UI thread; the
// WinUI adapter asserts this in DEBUG. These helpers themselves are pure
// data transitions and have no thread affinity of their own.

using System;

namespace OpenClawTray.Pages;

internal enum ChatPanelVisibility
{
    Visible,
    Collapsed,
}

internal interface IChatPagePanelHost
{
    ChatPanelVisibility WebView { get; set; }
    ChatPanelVisibility ErrorPanel { get; set; }
    ChatPanelVisibility WaitingPanel { get; set; }
    ChatPanelVisibility RetryChatButton { get; set; }
    ChatPanelVisibility LoadingRing { get; set; }
    ChatPanelVisibility PlaceholderPanel { get; set; }
}

internal static class ChatPagePanelStates
{
    // Apply the panel state used whenever ChatPage successfully shows the
    // WebView. Routed through by:
    //   * NavigateWebViewToCurrentChatUrl (re-show on page revisit)
    //   * _navCompletedHandler success branch (first navigate succeeds)
    //   * NavigateWhenChatReadyAsync (pre-navigate handoff once chat is reachable)
    //
    // Regression for issue #730: the WaitingPanel overlay shares Grid.Row
    // with the WebView and is centered, so a stale Visible WaitingPanel
    // floats over an already-usable chat. Every "show WebView" path must
    // collapse it -- as well as ErrorPanel (prior error display),
    // RetryChatButton (orphaned from a prior ShowChatReadinessFailure), and
    // PlaceholderPanel (defense-in-depth in case the disconnected-state
    // panel was somehow left visible).
    //
    // LoadingRing is intentionally NOT collapsed here: NavigateWhenChatReadyAsync
    // calls this BEFORE the WebView has finished loading, and the ring is
    // expected to spin over the loading WebView until the navCompleted handler
    // collapses it explicitly.
    public static void ApplyShowingWebView(IChatPagePanelHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        host.ErrorPanel = ChatPanelVisibility.Collapsed;
        host.WaitingPanel = ChatPanelVisibility.Collapsed;
        host.RetryChatButton = ChatPanelVisibility.Collapsed;
        host.PlaceholderPanel = ChatPanelVisibility.Collapsed;
        host.WebView = ChatPanelVisibility.Visible;
    }

    // Apply the panel state used whenever ChatPage shows the connection
    // ErrorPanel (the _navCompletedHandler failure branch for transport-level
    // CoreWebView2 errors such as ConnectionAborted / CannotConnect). All
    // overlapping panels are collapsed so the error message is the only
    // visible affordance.
    //
    // Same bug class as #730: previously this branch only collapsed WebView
    // and showed ErrorPanel, leaving WaitingPanel/RetryChatButton/LoadingRing
    // to potentially float on top.
    public static void ApplyShowingError(IChatPagePanelHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        host.WebView = ChatPanelVisibility.Collapsed;
        host.WaitingPanel = ChatPanelVisibility.Collapsed;
        host.RetryChatButton = ChatPanelVisibility.Collapsed;
        host.LoadingRing = ChatPanelVisibility.Collapsed;
        host.PlaceholderPanel = ChatPanelVisibility.Collapsed;
        host.ErrorPanel = ChatPanelVisibility.Visible;
    }

    // Apply the panel state used by ShowChatReadinessFailure -- the chat
    // backend is not reachable yet, so the WaitingPanel is shown with its
    // RetryChatButton so the user can retry. LoadingRing is collapsed
    // (caller also clears IsActive). Centralizing here keeps the #730 bug
    // class from re-emerging: every "show waiting" transition collapses
    // the same superset of overlapping panels.
    public static void ApplyShowingReadinessFailure(IChatPagePanelHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        host.WebView = ChatPanelVisibility.Collapsed;
        host.ErrorPanel = ChatPanelVisibility.Collapsed;
        host.PlaceholderPanel = ChatPanelVisibility.Collapsed;
        host.LoadingRing = ChatPanelVisibility.Collapsed;
        host.WaitingPanel = ChatPanelVisibility.Visible;
        host.RetryChatButton = ChatPanelVisibility.Visible;
    }

    // Apply the panel state used by OnRetryChat -- the user clicked retry,
    // so the RetryChatButton is collapsed, the LoadingRing spins, and the
    // WaitingPanel stays visible until navigation completes (caller also
    // sets LoadingRing.IsActive). Same #730-hardening rationale as the
    // other helpers.
    public static void ApplyShowingRetryInProgress(IChatPagePanelHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        host.WebView = ChatPanelVisibility.Collapsed;
        host.ErrorPanel = ChatPanelVisibility.Collapsed;
        host.PlaceholderPanel = ChatPanelVisibility.Collapsed;
        host.RetryChatButton = ChatPanelVisibility.Collapsed;
        host.WaitingPanel = ChatPanelVisibility.Visible;
        host.LoadingRing = ChatPanelVisibility.Visible;
    }
}
