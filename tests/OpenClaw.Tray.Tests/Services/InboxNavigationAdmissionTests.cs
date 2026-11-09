using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Services;

public sealed class InboxNavigationAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Feed_ForwardsOnlyAppReminderAdmissionAndKeepsReplacement(bool admitted)
    {
        var notifications = new AppNotificationService();
        var feed = new InboxFeed(notifications, gateway: null);
        notifications.Show(new AppNotification
        {
            Id = "mention", DedupeKey = "mention", Title = "Reply", Message = "A reply is ready.", Source = "chat", Severity = AppNotificationSeverity.Warning,
            ActionRoute = "chat:agent%3Aresearch%3Amain", ActionLabel = "Open chat",
        });
        var completion = new TaskCompletionSource<bool>();
        (string Route, string? Key)? observed = null;
        var pending = feed.NavigateNotificationAsync("mention", (route, key) =>
        {
            observed = (route, key);
            return completion.Task;
        });
        Assert.Equal(("chat", (string?)"agent:research:main"), observed);
        var original = Assert.Single(notifications.Snapshot.ActiveNotifications);
        Assert.False(pending.IsCompleted);
        notifications.Show(original with { Title = "New reply" });
        completion.SetResult(admitted);
        Assert.Equal(admitted, await pending);
        Assert.Equal("New reply", Assert.Single(notifications.Snapshot.ActiveNotifications).Title);
        Assert.Equal(admitted, await feed.NavigateNotificationAsync("mention", (_, _) => Task.FromResult(admitted)));
        if (admitted)
            Assert.Empty(notifications.Snapshot.ActiveNotifications);
        else
            Assert.Single(notifications.Snapshot.ActiveNotifications);
    }

    [Theory]
    [InlineData("gateway:approval:exec:fixture")]
    [InlineData("gateway:cronFailed:fixture")]
    [InlineData("gateway:modelAuth:fixture")]
    public async Task Feed_RefusesGatewayIdentityWithoutNavigationOrDismissal(string id)
    {
        var notifications = new AppNotificationService();
        var feed = new InboxFeed(notifications, gateway: null);
        // Even a colliding app record cannot turn a Gateway row into a dismiss-on-open reminder.
        notifications.Show(new AppNotification { Id = id, Title = "Collision", Message = "A local reminder.", Source = "app", ActionRoute = "connection" });
        var calls = 0;
        Assert.False(await feed.NavigateNotificationAsync(id, (_, _) =>
        {
            calls++;
            return Task.FromResult(true);
        }));
        Assert.Equal(0, calls);
        Assert.Equal(id, Assert.Single(notifications.Snapshot.ActiveNotifications).Id);
    }
}
