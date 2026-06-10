using System;
using OpenClawTray.Pages;
using Xunit;

namespace OpenClaw.Tray.Tests.Pages;

public class ChatPagePanelStatesTests
{
    private sealed class FakeHost : IChatPagePanelHost
    {
        public ChatPanelVisibility WebView { get; set; } = ChatPanelVisibility.Collapsed;
        public ChatPanelVisibility ErrorPanel { get; set; } = ChatPanelVisibility.Collapsed;
        public ChatPanelVisibility WaitingPanel { get; set; } = ChatPanelVisibility.Collapsed;
        public ChatPanelVisibility RetryChatButton { get; set; } = ChatPanelVisibility.Collapsed;
        public ChatPanelVisibility LoadingRing { get; set; } = ChatPanelVisibility.Collapsed;
        public ChatPanelVisibility PlaceholderPanel { get; set; } = ChatPanelVisibility.Collapsed;
    }

    // Regression for issue #730: every "show WebView" path must collapse
    // WaitingPanel, otherwise the "Waiting for chat to start..." overlay
    // (same Grid.Row as the WebView) floats over an already-usable chat.
    [Fact]
    public void ApplyShowingWebView_CollapsesWaitingPanel_Issue730()
    {
        var host = new FakeHost
        {
            WebView = ChatPanelVisibility.Collapsed,
            ErrorPanel = ChatPanelVisibility.Visible,
            WaitingPanel = ChatPanelVisibility.Visible,
        };

        ChatPagePanelStates.ApplyShowingWebView(host);

        Assert.Equal(ChatPanelVisibility.Visible, host.WebView);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.ErrorPanel);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.WaitingPanel);
    }

    // Regression for issue #730: after ShowChatReadinessFailure leaves
    // RetryChatButton=Visible, a page revisit (NavigateWebViewToCurrentChatUrl
    // -> ApplyShowingWebView) must not leave the Retry button floating over
    // the WebView.
    [Fact]
    public void ApplyShowingWebView_CollapsesOrphanRetryChatButton()
    {
        var host = new FakeHost
        {
            WebView = ChatPanelVisibility.Collapsed,
            ErrorPanel = ChatPanelVisibility.Collapsed,
            WaitingPanel = ChatPanelVisibility.Visible,
            RetryChatButton = ChatPanelVisibility.Visible,
        };

        ChatPagePanelStates.ApplyShowingWebView(host);

        Assert.Equal(ChatPanelVisibility.Collapsed, host.RetryChatButton);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.WaitingPanel);
        Assert.Equal(ChatPanelVisibility.Visible, host.WebView);
    }

    // ApplyShowingWebView is also called from NavigateWhenChatReadyAsync
    // BEFORE the WebView finishes loading, so it must leave LoadingRing
    // alone (the spinner is expected to keep spinning until navCompleted).
    [Fact]
    public void ApplyShowingWebView_DoesNotTouchLoadingRing()
    {
        var host = new FakeHost { LoadingRing = ChatPanelVisibility.Visible };
        ChatPagePanelStates.ApplyShowingWebView(host);
        Assert.Equal(ChatPanelVisibility.Visible, host.LoadingRing);
    }

    [Fact]
    public void ApplyShowingWebView_IsIdempotent()
    {
        var host = new FakeHost
        {
            WebView = ChatPanelVisibility.Visible,
            ErrorPanel = ChatPanelVisibility.Collapsed,
            WaitingPanel = ChatPanelVisibility.Collapsed,
            RetryChatButton = ChatPanelVisibility.Collapsed,
        };

        ChatPagePanelStates.ApplyShowingWebView(host);
        ChatPagePanelStates.ApplyShowingWebView(host);

        Assert.Equal(ChatPanelVisibility.Visible, host.WebView);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.ErrorPanel);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.WaitingPanel);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.RetryChatButton);
    }

    [Fact]
    public void ApplyShowingWebView_NullHost_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ChatPagePanelStates.ApplyShowingWebView(null!));
    }

    // Regression for issue #730 on the error path: the navCompleted failure
    // branch previously only collapsed WebView and showed ErrorPanel, leaving
    // WaitingPanel / RetryChatButton / LoadingRing to float over the error
    // message.
    [Fact]
    public void ApplyShowingError_CollapsesAllOverlays()
    {
        var host = new FakeHost
        {
            WebView = ChatPanelVisibility.Visible,
            WaitingPanel = ChatPanelVisibility.Visible,
            RetryChatButton = ChatPanelVisibility.Visible,
            LoadingRing = ChatPanelVisibility.Visible,
            ErrorPanel = ChatPanelVisibility.Collapsed,
        };

        ChatPagePanelStates.ApplyShowingError(host);

        Assert.Equal(ChatPanelVisibility.Visible, host.ErrorPanel);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.WebView);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.WaitingPanel);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.RetryChatButton);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.LoadingRing);
    }

    [Fact]
    public void ApplyShowingError_IsIdempotent()
    {
        var host = new FakeHost
        {
            ErrorPanel = ChatPanelVisibility.Visible,
        };

        ChatPagePanelStates.ApplyShowingError(host);
        ChatPagePanelStates.ApplyShowingError(host);

        Assert.Equal(ChatPanelVisibility.Visible, host.ErrorPanel);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.WebView);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.WaitingPanel);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.RetryChatButton);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.LoadingRing);
    }

    [Fact]
    public void ApplyShowingError_NullHost_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ChatPagePanelStates.ApplyShowingError(null!));
    }

    // ShowChatReadinessFailure used to set panel visibility inline (4 of 4
    // transitions weren't centralized), making it a #730-class drift hazard
    // if a new panel is ever added.
    [Fact]
    public void ApplyShowingReadinessFailure_ShowsWaitingAndRetryAndCollapsesOthers()
    {
        var host = new FakeHost
        {
            WebView = ChatPanelVisibility.Visible,
            ErrorPanel = ChatPanelVisibility.Visible,
            LoadingRing = ChatPanelVisibility.Visible,
            PlaceholderPanel = ChatPanelVisibility.Visible,
            WaitingPanel = ChatPanelVisibility.Collapsed,
            RetryChatButton = ChatPanelVisibility.Collapsed,
        };

        ChatPagePanelStates.ApplyShowingReadinessFailure(host);

        Assert.Equal(ChatPanelVisibility.Visible, host.WaitingPanel);
        Assert.Equal(ChatPanelVisibility.Visible, host.RetryChatButton);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.WebView);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.ErrorPanel);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.LoadingRing);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.PlaceholderPanel);
    }

    [Fact]
    public void ApplyShowingReadinessFailure_NullHost_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ChatPagePanelStates.ApplyShowingReadinessFailure(null!));
    }

    // OnRetryChat used to set panel visibility inline -- now goes through
    // the helper so the next added panel cannot drift out of sync.
    [Fact]
    public void ApplyShowingRetryInProgress_ShowsWaitingAndLoadingRingAndCollapsesOthers()
    {
        var host = new FakeHost
        {
            WebView = ChatPanelVisibility.Visible,
            ErrorPanel = ChatPanelVisibility.Visible,
            RetryChatButton = ChatPanelVisibility.Visible,
            PlaceholderPanel = ChatPanelVisibility.Visible,
            WaitingPanel = ChatPanelVisibility.Collapsed,
            LoadingRing = ChatPanelVisibility.Collapsed,
        };

        ChatPagePanelStates.ApplyShowingRetryInProgress(host);

        Assert.Equal(ChatPanelVisibility.Visible, host.WaitingPanel);
        Assert.Equal(ChatPanelVisibility.Visible, host.LoadingRing);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.WebView);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.ErrorPanel);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.RetryChatButton);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.PlaceholderPanel);
    }

    [Fact]
    public void ApplyShowingRetryInProgress_NullHost_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ChatPagePanelStates.ApplyShowingRetryInProgress(null!));
    }

    // Verify ApplyShowingWebView also collapses PlaceholderPanel for full
    // panel coverage (defense-in-depth).
    [Fact]
    public void ApplyShowingWebView_CollapsesPlaceholderPanel()
    {
        var host = new FakeHost { PlaceholderPanel = ChatPanelVisibility.Visible };
        ChatPagePanelStates.ApplyShowingWebView(host);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.PlaceholderPanel);
    }

    [Fact]
    public void ApplyShowingError_CollapsesPlaceholderPanel()
    {
        var host = new FakeHost { PlaceholderPanel = ChatPanelVisibility.Visible };
        ChatPagePanelStates.ApplyShowingError(host);
        Assert.Equal(ChatPanelVisibility.Collapsed, host.PlaceholderPanel);
    }
}
