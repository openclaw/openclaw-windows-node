using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests.Services;

public sealed class AppNotificationMapperTests
{
    [Fact]
    public void FromGatewayNotification_MapsChatActionAndUsesHashedStableDedupeKey()
    {
        var notification = new OpenClawNotification
        {
            Title = "Build complete",
            Message = "The build passed",
            Type = "info",
            IsChat = true,
            SessionKey = "agent:main:default"
        };

        var mapped = AppNotificationMapper.FromGatewayNotification(notification, "Open Chat");
        var mappedAgain = AppNotificationMapper.FromGatewayNotification(notification, "Open Chat");

        Assert.Equal("gateway", mapped.Source);
        Assert.Equal("info", mapped.Category);
        Assert.Equal(AppNotificationSeverity.Informational, mapped.Severity);
        Assert.Equal("Open Chat", mapped.ActionLabel);
        Assert.True(AppNotificationActionRoutes.TryGetChatSessionKey(mapped.ActionRoute, out var sessionKey));
        Assert.Equal("agent:main:default", sessionKey);
        Assert.Equal(mapped.DedupeKey, mappedAgain.DedupeKey);
        Assert.DoesNotContain(notification.Title, mapped.DedupeKey);
        Assert.DoesNotContain(notification.Message, mapped.DedupeKey);
    }

    [Fact]
    public void FromGatewayNotification_ChatWithoutSessionKey_FallsBackToChatPageAction()
    {
        var mapped = AppNotificationMapper.FromGatewayNotification(new OpenClawNotification
        {
            Title = "Chat response",
            Message = "A response is ready",
            Type = "info",
            IsChat = true
        }, "Open Chat");

        Assert.Equal("Open Chat", mapped.ActionLabel);
        Assert.Equal("chat", mapped.ActionRoute);
    }

    [Theory]
    [InlineData("error", AppNotificationSeverity.Error)]
    [InlineData("urgent", AppNotificationSeverity.Warning)]
    [InlineData("health", AppNotificationSeverity.Warning)]
    [InlineData("reminder", AppNotificationSeverity.Informational)]
    public void FromGatewayNotification_MapsSeverityFromType(string type, AppNotificationSeverity expected)
    {
        var mapped = AppNotificationMapper.FromGatewayNotification(new OpenClawNotification
        {
            Title = "Notification",
            Message = "Message",
            Type = type
        });

        Assert.Equal(expected, mapped.Severity);
    }

    [Fact]
    public void FromNodeSystemNotification_UsesNodeSourceAndSystemNotifyCategory()
    {
        var mapped = AppNotificationMapper.FromNodeSystemNotification(new SystemNotifyArgs
        {
            Title = "Agent notice",
            Body = "Something happened"
        });

        Assert.Equal("node", mapped.Source);
        Assert.Equal("system.notify", mapped.Category);
        Assert.Equal("Agent notice", mapped.Title);
        Assert.Equal("Something happened", mapped.Message);
        Assert.Equal(AppNotificationSeverity.Informational, mapped.Severity);
    }

    [Fact]
    public void FromNodeActivity_UsesExplicitMetadata()
    {
        var mapped = AppNotificationMapper.FromNodeActivity(
            "Camera blocked",
            "Enable camera access.",
            "node.invoke",
            AppNotificationSeverity.Error,
            "node:camera-blocked");

        Assert.Equal("node", mapped.Source);
        Assert.Equal("node.invoke", mapped.Category);
        Assert.Equal(AppNotificationSeverity.Error, mapped.Severity);
        Assert.Equal("node:camera-blocked", mapped.DedupeKey);
    }
}
