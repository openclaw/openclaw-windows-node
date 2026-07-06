using OpenClaw.Shared;
using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests.Services;

public sealed class ChatNotificationSpeechTextTests
{
    [Fact]
    public void Resolve_UsesFullMessageWhenPreviewWasTruncated()
    {
        var fullMessage = new string('x', 240);

        var resolved = ChatNotificationSpeechText.Resolve(new OpenClawNotification
        {
            Message = fullMessage[..200] + "…",
            FullMessage = fullMessage
        });

        Assert.Equal(fullMessage, resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Resolve_FallsBackToPreviewWhenFullMessageIsUnavailable(string? fullMessage)
    {
        var resolved = ChatNotificationSpeechText.Resolve(new OpenClawNotification
        {
            Message = "Preview",
            FullMessage = fullMessage
        });

        Assert.Equal("Preview", resolved);
    }
}
