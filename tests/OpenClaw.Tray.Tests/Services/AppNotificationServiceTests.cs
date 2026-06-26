using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests.Services;

public sealed class AppNotificationServiceTests
{
    [Fact]
    public void Show_FirstNotification_BecomesCurrent()
    {
        var service = new AppNotificationService();

        service.Show(Notification("Local command denied", "Command: dir"));

        Assert.Equal("Local command denied", service.Snapshot.Current?.Title);
        Assert.Equal(0, service.Snapshot.PendingCount);
    }

    [Fact]
    public void Show_AdditionalNotifications_AreQueued()
    {
        var service = new AppNotificationService();

        service.Show(Notification("First", "Message 1"));
        service.Show(Notification("Second", "Message 2"));
        service.Show(Notification("Third", "Message 3"));

        Assert.Equal("First", service.Snapshot.Current?.Title);
        Assert.Equal(2, service.Snapshot.PendingCount);
    }

    [Fact]
    public void Snapshot_ActiveNotifications_ReturnsCurrentThenQueued()
    {
        var service = new AppNotificationService();

        service.Show(Notification("First", "Message 1"));
        service.Show(Notification("Second", "Message 2"));
        service.Show(Notification("Third", "Message 3"));

        Assert.Equal(
            ["First", "Second", "Third"],
            service.Snapshot.ActiveNotifications.Select(n => n.Title).ToArray());
    }

    [Fact]
    public void Snapshot_HasMultipleActiveNotifications_OnlyWhenNotificationsListHasMoreThanOneItem()
    {
        var service = new AppNotificationService();

        Assert.False(service.Snapshot.HasMultipleActiveNotifications);

        service.Show(Notification("First", "Message 1", dedupeKey: "same"));

        Assert.False(service.Snapshot.HasMultipleActiveNotifications);

        service.Show(Notification("First updated", "Message 1", dedupeKey: "same"));
        service.Show(Notification("First updated again", "Message 1", dedupeKey: "same"));

        Assert.False(service.Snapshot.HasMultipleActiveNotifications);

        service.Show(Notification("Second", "Message 2"));

        Assert.True(service.Snapshot.HasMultipleActiveNotifications);
        Assert.Equal(2, service.Snapshot.ActiveNotifications.Count);
    }

    [Fact]
    public void BannerState_HideActiveNotifications_HidesExistingItemsUntilNewNotificationArrives()
    {
        var service = new AppNotificationService();
        var bannerState = new AppNotificationBannerState();

        service.Show(Notification("First", "Message 1"));
        service.Show(Notification("Second", "Message 2"));

        Assert.Equal("First", bannerState.SelectVisibleNotification(service.Snapshot)?.Title);

        bannerState.HideActiveNotifications(service.Snapshot);

        Assert.Null(bannerState.SelectVisibleNotification(service.Snapshot));
        Assert.Equal(2, service.Snapshot.ActiveNotifications.Count);

        service.Show(Notification("Third", "Message 3"));

        Assert.Equal("Third", bannerState.SelectVisibleNotification(service.Snapshot)?.Title);
        Assert.Equal(3, service.Snapshot.ActiveNotifications.Count);
    }

    [Fact]
    public void BannerState_DismissDisplayedNotification_SelectsNextListItem()
    {
        var service = new AppNotificationService();
        var bannerState = new AppNotificationBannerState();

        service.Show(Notification("First", "Message 1"));
        service.Show(Notification("Second", "Message 2"));
        var displayed = bannerState.SelectVisibleNotification(service.Snapshot);

        service.Dismiss(displayed!.Id);

        Assert.Equal("Second", bannerState.SelectVisibleNotification(service.Snapshot)?.Title);
        Assert.Equal(["Second"], service.Snapshot.ActiveNotifications.Select(n => n.Title).ToArray());
    }

    [Fact]
    public void BannerState_DismissDisplayedNotification_RevealsRemainingHiddenItemWhenRequested()
    {
        var service = new AppNotificationService();
        var bannerState = new AppNotificationBannerState();

        service.Show(Notification("First", "Message 1"));
        service.Show(Notification("Second", "Message 2"));
        bannerState.HideActiveNotifications(service.Snapshot);
        var displayed = bannerState.SelectVisibleNotification(service.Snapshot);

        Assert.Null(displayed);
        service.Dismiss(service.Snapshot.Queued[0].Id);

        Assert.Null(bannerState.SelectVisibleNotification(service.Snapshot));
        Assert.Equal("First", bannerState.SelectVisibleNotification(service.Snapshot, revealHiddenIfNeeded: true)?.Title);
        Assert.Equal(["First"], service.Snapshot.ActiveNotifications.Select(n => n.Title).ToArray());
    }

    [Fact]
    public void DismissCurrent_ShowsQueuedNotification()
    {
        var service = new AppNotificationService();
        service.Show(Notification("First", "Message 1"));
        service.Show(Notification("Second", "Message 2"));

        service.DismissCurrent();

        Assert.Equal("Second", service.Snapshot.Current?.Title);
        Assert.Equal(0, service.Snapshot.PendingCount);
    }

    [Fact]
    public void Dismiss_ByCurrentId_ShowsQueuedNotification()
    {
        var service = new AppNotificationService();
        service.Show(Notification("First", "Message 1"));
        service.Show(Notification("Second", "Message 2"));
        var firstId = service.Snapshot.Current!.Id;

        service.Dismiss(firstId);

        Assert.Equal("Second", service.Snapshot.Current?.Title);
        Assert.Equal(0, service.Snapshot.PendingCount);
    }

    [Fact]
    public void Dismiss_ByQueuedId_RemovesQueuedNotification()
    {
        var service = new AppNotificationService();
        service.Show(Notification("First", "Message 1"));
        service.Show(Notification("Second", "Message 2"));
        service.Show(Notification("Third", "Message 3"));
        var secondId = service.Snapshot.Queued[0].Id;

        service.Dismiss(secondId);

        Assert.Equal("First", service.Snapshot.Current?.Title);
        Assert.Equal(["Third"], service.Snapshot.Queued.Select(n => n.Title).ToArray());
    }

    [Fact]
    public void DismissByDedupeKey_RemovesCurrentAndQueuedMatches()
    {
        var service = new AppNotificationService();
        service.Show(Notification("First", "Message", dedupeKey: "same"));
        service.Show(Notification("Second", "Message", dedupeKey: "other"));
        service.Show(Notification("Third", "Message", dedupeKey: "same"));

        service.DismissByDedupeKey("same");

        Assert.Equal("Second", service.Snapshot.Current?.Title);
        Assert.Empty(service.Snapshot.Queued);
    }

    [Fact]
    public void ClearAll_RemovesCurrentAndQueuedNotifications()
    {
        var service = new AppNotificationService();
        service.Show(Notification("First", "Message 1"));
        service.Show(Notification("Second", "Message 2"));

        service.ClearAll();

        Assert.Null(service.Snapshot.Current);
        Assert.Equal(0, service.Snapshot.PendingCount);
        Assert.Empty(service.Snapshot.ActiveNotifications);
    }

    [Fact]
    public void ShowNext_RotatesCurrentToBackOfQueue()
    {
        var service = new AppNotificationService();
        service.Show(Notification("First", "Message 1"));
        service.Show(Notification("Second", "Message 2"));

        service.ShowNext();

        Assert.Equal("Second", service.Snapshot.Current?.Title);
        Assert.Equal(1, service.Snapshot.PendingCount);
        Assert.Equal("First", service.Snapshot.Queued[0].Title);
    }

    [Fact]
    public void Show_DedupeKey_CoalescesWithoutDroppingDistinctNotifications()
    {
        var service = new AppNotificationService();

        service.Show(Notification("Denied", "Command: one", dedupeKey: "exec:one"));
        service.Show(Notification("Denied again", "Command: one", dedupeKey: "exec:one"));
        service.Show(Notification("Different", "Command: two", dedupeKey: "exec:two"));

        Assert.Equal("Denied again", service.Snapshot.Current?.Title);
        Assert.Equal(2, service.Snapshot.Current?.OccurrenceCount);
        Assert.Equal(1, service.Snapshot.PendingCount);
        Assert.Equal("Different", service.Snapshot.Queued[0].Title);
    }

    [Fact]
    public void ClearSource_RemovesCurrentAndQueuedNotifications()
    {
        var service = new AppNotificationService();
        service.Show(Notification("Node 1", "Message", source: "node"));
        service.Show(Notification("Gateway", "Message", source: "gateway"));
        service.Show(Notification("Node 2", "Message", source: "node"));

        service.ClearSource("node");

        Assert.Equal("Gateway", service.Snapshot.Current?.Title);
        Assert.Equal(0, service.Snapshot.PendingCount);
    }

    [Fact]
    public void ClearSource_RemovesQueuedSameSourceBeforePromotingNextCurrent()
    {
        var service = new AppNotificationService();
        service.Show(Notification("Node 1", "Message", source: "node"));
        service.Show(Notification("Node 2", "Message", source: "node"));
        service.Show(Notification("Gateway", "Message", source: "gateway"));

        service.ClearSource("node");

        Assert.Equal("Gateway", service.Snapshot.Current?.Title);
        Assert.Equal(0, service.Snapshot.PendingCount);
        Assert.Equal(["Gateway"], service.Snapshot.ActiveNotifications.Select(n => n.Title).ToArray());
    }

    [Fact]
    public void AppNotificationActionRoutes_Chat_RoundTripsSessionKey()
    {
        var route = AppNotificationActionRoutes.Chat("agent:main:scratch session");

        Assert.True(AppNotificationActionRoutes.TryGetChatSessionKey(route, out var sessionKey));
        Assert.Equal("agent:main:scratch session", sessionKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("chat:")]
    [InlineData("settings")]
    public void AppNotificationActionRoutes_TryGetChatSessionKey_RejectsNonChatRoutes(string? route)
    {
        Assert.False(AppNotificationActionRoutes.TryGetChatSessionKey(route, out var sessionKey));
        Assert.Null(sessionKey);
    }

    [Theory]
    [InlineData("", "message", "source")]
    [InlineData("title", "", "source")]
    [InlineData("title", "message", "")]
    public void Show_RequiresSelfContainedCopy(string title, string message, string source)
    {
        var service = new AppNotificationService();

        Assert.Throws<ArgumentException>(() => service.Show(Notification(title, message, source)));
    }

    private static AppNotification Notification(
        string title,
        string message,
        string source = "test",
        string? dedupeKey = null) =>
        new()
        {
            Title = title,
            Message = message,
            Source = source,
            DedupeKey = dedupeKey,
            Severity = AppNotificationSeverity.Warning
        };
}
