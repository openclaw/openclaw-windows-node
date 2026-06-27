using Microsoft.Toolkit.Uwp.Notifications;
using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests.Services;

public sealed class AppNotificationPublisherTests
{
    [Fact]
    public void Publish_AppNotificationFailure_DoesNotSuppressToast()
    {
        var service = new AppNotificationService();
        var expected = new InvalidOperationException("app notification subscriber failed");
        var toastShown = false;
        service.Changed += (_, _) => throw expected;

        var actual = Assert.Throws<InvalidOperationException>(() =>
            AppNotificationPublisher.Publish(
                service,
                (_, _, _) => toastShown = true,
                new AppNotificationPublishRequest(Notification(), new ToastContentBuilder())));

        Assert.Same(expected, actual);
        Assert.True(toastShown);
    }

    [Fact]
    public void Publish_ToastFailure_DoesNotSuppressAppNotification()
    {
        var service = new AppNotificationService();
        var expected = new InvalidOperationException("toast failed");

        var actual = Assert.Throws<InvalidOperationException>(() =>
            AppNotificationPublisher.Publish(
                service,
                (_, _, _) => throw expected,
                new AppNotificationPublishRequest(Notification(), new ToastContentBuilder())));

        Assert.Same(expected, actual);
        Assert.Equal("Title", service.Snapshot.Current?.Title);
    }

    [Fact]
    public void Publish_EmptyRequest_Throws()
    {
        var service = new AppNotificationService();

        Assert.Throws<ArgumentException>(() =>
            AppNotificationPublisher.Publish(
                service,
                (_, _, _) => { },
                new AppNotificationPublishRequest()));
    }

    private static AppNotification Notification() => new()
    {
        Title = "Title",
        Message = "Message",
        Source = "test"
    };
}
