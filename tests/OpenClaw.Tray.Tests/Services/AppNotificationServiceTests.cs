using OpenClawTray.Services;
using OpenClawTray.Presentation;
using Xunit;

namespace OpenClaw.Tray.Tests.Services;

public sealed class AppNotificationServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NavigateAction_DismissesOnlyTheAdmittedReminder(bool admitted)
    {
        var service = new AppNotificationService();
        service.Show(Notification("First", "Open connection") with { Id = "first", ActionRoute = "connection" });
        service.Show(Notification("Second", "Open permissions") with { Id = "second", ActionRoute = "permissions" });
        (string Route, string? Key, int Count)? observed = null;

        var result = service.NavigateAction("second", (route, key) =>
        {
            observed = (route, key, service.Snapshot.ActiveNotifications.Count);
            return admitted;
        });

        Assert.Equal(("permissions", (string?)null, 2), observed);
        Assert.Equal(admitted, result);
        Assert.Equal(admitted ? ["first"] : ["first", "second"],
            service.Snapshot.ActiveNotifications.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void NavigateAction_ThrowingNavigationRetainsReminder()
    {
        var service = new AppNotificationService();
        service.Show(Notification("Reminder", "Open connection") with { Id = "reminder", ActionRoute = "connection" });

        Assert.False(service.NavigateAction("reminder", (_, _) =>
            throw new InvalidOperationException("Navigation failed")));

        Assert.Equal("reminder", Assert.Single(service.Snapshot.ActiveNotifications).Id);
    }

    [Fact]
    public void NavigateAction_ChatKeyIsPassedExactlyAndRefusalDoesNotConsumeReminder()
    {
        const string key = "agent:Mixed Case:thread/a?x=1&literal=%2F";
        var service = new AppNotificationService();
        service.Show(Notification("Chat reminder", "Open chat") with
        {
            Id = "chat-reminder",
            ActionRoute = "chat:agent%3AMixed%20Case%3Athread%2Fa%3Fx%3D1%26literal%3D%252F"
        });
        (string Route, string? Key)? observed = null;

        Assert.False(service.NavigateAction("chat-reminder", (route, session) =>
        {
            observed = (route, session);
            return false;
        }));
        Assert.Equal(("chat", key), observed);
        Assert.Equal("chat-reminder", Assert.Single(service.Snapshot.ActiveNotifications).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NavigateAction_DeferredIntentNeverDismissesReminder(bool replaceIntent)
    {
        var service = new AppNotificationService();
        var deferred = new DeferredNavigation();
        var opened = new List<string>();
        service.Show(Notification("Reminder", "Open connection") with { Id = "reminder", ActionRoute = "connection" });

        Assert.False(service.NavigateAction("reminder", (route, _) =>
        {
            deferred.Defer(() => opened.Add(route));
            return false;
        }));
        if (replaceIntent)
            deferred.Defer(() => opened.Add("permissions"));

        Assert.False(deferred.Run(() => true, () => { }));
        Assert.Empty(opened);
        Assert.Single(service.Snapshot.ActiveNotifications);
        Assert.True(deferred.Run(() => false, () => { }));
        Assert.Equal(replaceIntent ? ["permissions"] : ["connection"], opened);
        Assert.Single(service.Snapshot.ActiveNotifications);
    }

    [Fact]
    public void NavigateAction_UsesCurrentReminderRatherThanStalePresentation()
    {
        var service = new AppNotificationService();
        service.Show(Notification("Reminder", "Open connection") with { Id = "reminder", ActionRoute = "connection" });
        service.Dismiss("reminder");
        var navigationCount = 0;

        Assert.False(service.NavigateAction("reminder", (_, _) =>
        {
            navigationCount++;
            return true;
        }));
        Assert.Equal(0, navigationCount);
        Assert.Empty(service.Snapshot.ActiveNotifications);
    }

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
    public void BannerState_PrioritizesConnectionIssueOverEarlierActionableNotification()
    {
        var service = new AppNotificationService();
        var bannerState = new AppNotificationBannerState();

        // An unrelated, actionable notification is current...
        service.Show(Notification("Sandbox risk", "Review sandbox", source: "sandbox"));
        // ...then a connection failure arrives and is queued behind it.
        service.Show(Notification("Gateway connection failed", "unauthorized", source: "connection"));

        // The connection issue must be the visible banner so the user can reach
        // the Connection page, even though it wasn't the first/current item.
        Assert.Equal(
            "Gateway connection failed",
            bannerState.SelectVisibleNotification(service.Snapshot)?.Title);
    }

    [Fact]
    public void BannerState_HidingConnectionIssue_RevealsRemainingNotification()
    {
        var service = new AppNotificationService();
        var bannerState = new AppNotificationBannerState();

        service.Show(Notification("Sandbox risk", "Review sandbox", source: "sandbox"));
        service.Show(Notification("Gateway connection failed", "unauthorized", source: "connection"));

        // Dismissing the banner hides all active items; once the connection
        // issue is removed, the remaining notification can surface again.
        bannerState.HideActiveNotifications(service.Snapshot);
        Assert.Null(bannerState.SelectVisibleNotification(service.Snapshot));

        var connectionId = service.Snapshot.ActiveNotifications
            .First(n => n.Source == "connection").Id;
        service.Dismiss(connectionId);

        Assert.Equal(
            "Sandbox risk",
            bannerState.SelectVisibleNotification(service.Snapshot, revealHiddenIfNeeded: true)?.Title);
    }

    [Fact]
    public void BannerState_PrioritizesActionableConnectionOverActionlessConnection()
    {
        var service = new AppNotificationService();
        var bannerState = new AppNotificationBannerState();

        // An action-less connection notification (e.g. a transient gateway-host
        // failure) is current...
        service.Show(new AppNotification
        {
            Id = "gateway-host-action:Terminal failed",
            Title = "Terminal failed",
            Message = "Could not open the gateway terminal.",
            Source = "connection",
            Severity = AppNotificationSeverity.Error
        });
        // ...then a real connection error arrives with an "Open Connection" action.
        service.Show(new AppNotification
        {
            Id = "connection:issue",
            Title = "Gateway connection failed",
            Message = "Transport error",
            Source = "connection",
            Severity = AppNotificationSeverity.Error,
            ActionRoute = "connection",
            ActionLabel = "Open Connection"
        });

        // The actionable connection error must win so the banner offers
        // "Open Connection" rather than degrading to "Show more".
        var visible = bannerState.SelectVisibleNotification(service.Snapshot);
        Assert.Equal("Gateway connection failed", visible?.Title);
        Assert.Equal("Open Connection", visible?.ActionLabel);
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
    public void Show_CapsActiveNotificationsAndKeepsNewestQueuedItems()
    {
        var service = new AppNotificationService();

        for (var index = 1; index <= 105; index++)
            service.Show(Notification($"Notification {index}", "Message"));

        Assert.Equal(100, service.Snapshot.ActiveNotifications.Count);
        Assert.Equal("Notification 1", service.Snapshot.Current?.Title);
        Assert.DoesNotContain(service.Snapshot.ActiveNotifications, notification => notification.Title == "Notification 2");
        Assert.Contains(service.Snapshot.ActiveNotifications, notification => notification.Title == "Notification 105");
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
