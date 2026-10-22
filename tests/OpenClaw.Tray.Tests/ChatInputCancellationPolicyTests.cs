using OpenClaw.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatInputCancellationPolicyTests
{
    [Theory]
    [InlineData(ChatQueuedMessageSendState.Queued, true)]
    [InlineData(ChatQueuedMessageSendState.Failed, true)]
    [InlineData(ChatQueuedMessageSendState.Sending, false)]
    [InlineData(ChatQueuedMessageSendState.Accepted, false)]
    [InlineData(ChatQueuedMessageSendState.Unconfirmed, false)]
    [InlineData((ChatQueuedMessageSendState)999, false)]
    public void OnlyUnsentOrRejectedInputCanBeLocallyCancelled(ChatQueuedMessageSendState state, bool expected)
    {
        var message = new ChatQueuedMessage("id", "text", DateTimeOffset.UtcNow, "nonce", state);
        Assert.Equal(expected, message.CanCancel);
    }

    [Fact]
    public void McpAdvertisementAndNativeFooterUseTheSameLocalCancellationPolicy()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "App.CapabilityHandlers.cs"))
            .Replace("\r\n", "\n");
        Assert.Contains("private static bool CanCancelQueuedMessage(ChatQueuedMessage message) =>\n        message.CanCancel;", app);
        Assert.Contains("canCancel = CanCancelQueuedMessage(message)", app);
        var timeline = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Chat", "ReactorChatTimeline.cs"));
        var start = timeline.IndexOf("private static Element BuildQueuedMessageAction(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = timeline.IndexOf("\n    private static", start + 1, StringComparison.Ordinal);
        var action = timeline[start..(end < 0 ? timeline.Length : end)];
        Assert.Contains("|| !message.CanCancel", action);
        Assert.DoesNotContain("message.SendState == ChatQueuedMessageSendState.Sending", action);
    }
}
