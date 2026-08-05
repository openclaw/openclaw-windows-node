using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class AppNotificationInfoBarPresenterTests
{
    [Fact]
    public void Present_FiltersInformationalAndSuccessNotifications()
    {
        var presenter = new AppNotificationInfoBarPresenter();
        var snapshot = Snapshot(
            Notification("info", AppNotificationSeverity.Informational),
            Notification("success", AppNotificationSeverity.Success));

        var presentation = presenter.Present(snapshot, null, false, null, "Show more");

        Assert.False(presentation.IsVisible);
        Assert.Equal(AppNotificationInfoBarActionKind.None, presentation.Action.Kind);
    }

    [Theory]
    [InlineData(AppNotificationSeverity.Warning, (int)AppNotificationInfoBarSeverity.Warning)]
    [InlineData(AppNotificationSeverity.Error, (int)AppNotificationInfoBarSeverity.Error)]
    public void Present_ProjectsBannerSeverity(
        AppNotificationSeverity source,
        int expected)
    {
        var presenter = new AppNotificationInfoBarPresenter();

        var presentation = presenter.Present(
            Snapshot(Notification("visible", source)),
            null,
            false,
            null,
            "Show more");

        Assert.True(presentation.IsVisible);
        Assert.Equal((AppNotificationInfoBarSeverity)expected, presentation.Severity);
    }

    [Fact]
    public void Present_ActionableNotificationWinsOverShowMore()
    {
        var presenter = new AppNotificationInfoBarPresenter();
        var actionable = Notification("connection", AppNotificationSeverity.Error) with
        {
            ActionLabel = "Open Connection",
            ActionRoute = "connection",
            Source = "connection"
        };

        var presentation = presenter.Present(
            Snapshot(Notification("other", AppNotificationSeverity.Warning), actionable),
            null,
            false,
            null,
            "Show more");

        Assert.Equal("connection", presentation.Notification?.Id);
        Assert.Equal(AppNotificationInfoBarActionKind.NotificationRoute, presentation.Action.Kind);
        Assert.Equal("Open Connection", presentation.Action.Label);
        Assert.Equal("connection", presentation.Action.Route);
        Assert.True(presentation.Action.IsEnabled);
    }

    [Theory]
    [InlineData("notifications", false)]
    [InlineData("Notifications", true)]
    [InlineData("settings", true)]
    [InlineData(null, true)]
    public void Present_ShowMoreEnabledStateUsesCurrentExactTag(string? currentTag, bool expected)
    {
        var presenter = new AppNotificationInfoBarPresenter();
        var presentation = presenter.Present(
            Snapshot(
                Notification("warning", AppNotificationSeverity.Warning),
                Notification("info", AppNotificationSeverity.Informational)),
            null,
            false,
            currentTag,
            "Show more");

        Assert.Equal(AppNotificationInfoBarActionKind.ShowMore, presentation.Action.Kind);
        Assert.Equal("Show more", presentation.Action.Label);
        Assert.Null(presentation.Action.Route);
        Assert.Equal(expected, presentation.Action.IsEnabled);
    }

    [Fact]
    public void UpdateCurrentTag_ChangesOnlyShowMoreEnabledState()
    {
        var presenter = new AppNotificationInfoBarPresenter();
        var original = presenter.Present(
            Snapshot(
                Notification("first", AppNotificationSeverity.Warning),
                Notification("second", AppNotificationSeverity.Error)),
            null,
            false,
            "settings",
            "Show more");

        var updated = presenter.UpdateCurrentTag(original, "notifications");

        Assert.Equal(original.Notification, updated.Notification);
        Assert.False(updated.Action.IsEnabled);
    }

    [Fact]
    public void HideThenRemoval_RevealsRemainingHiddenBannerWhenDisplayedItemWasRemoved()
    {
        var presenter = new AppNotificationInfoBarPresenter();
        var first = Notification("first", AppNotificationSeverity.Warning);
        var second = Notification("second", AppNotificationSeverity.Error);
        var initial = Snapshot(first, second);

        var shown = presenter.Present(initial, null, false, null, "Show more");
        presenter.HideActiveNotifications(initial);
        var hidden = presenter.Present(initial, shown.Notification?.Id, false, null, "Show more");
        var afterRemoval = Snapshot(second);
        var fallback = presenter.Present(afterRemoval, first.Id, true, null, "Show more");

        Assert.False(hidden.IsVisible);
        Assert.Equal("second", fallback.Notification?.Id);
    }

    [Fact]
    public void Present_ActionlessSingleBannerHasNoAction()
    {
        var presenter = new AppNotificationInfoBarPresenter();

        var presentation = presenter.Present(
            Snapshot(Notification("warning", AppNotificationSeverity.Warning)),
            null,
            false,
            null,
            "Show more");

        Assert.Equal(AppNotificationInfoBarActionKind.None, presentation.Action.Kind);
        Assert.Null(presentation.Action.Label);
        Assert.Null(presentation.Action.Route);
        Assert.True(presentation.Action.IsEnabled);
    }

    private static AppNotification Notification(string id, AppNotificationSeverity severity) => new()
    {
        Id = id,
        Title = $"Title {id}",
        Message = $"Message {id}",
        Severity = severity,
        Source = "test"
    };

    private static AppNotificationSnapshot Snapshot(params AppNotification[] notifications) => new(
        notifications.FirstOrDefault(),
        Math.Max(0, notifications.Length - 1),
        notifications.Skip(1).ToArray(),
        notifications);
}
