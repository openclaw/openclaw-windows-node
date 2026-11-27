using OpenClaw.Chat;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public class ChatQueueReconnectTests
{
    [Fact]
    public void Reconnect_KeepsSendingMessagesAsUncertainAndQueuedMessagesAsNotAccepted()
    {
        var queue = new ChatQueueState();
        var created = DateTimeOffset.Parse("2026-10-07T00:00:00Z");
        queue.AddMessage("main", new ChatQueuedMessage(
            "q-sending", "still sending", created, "n1", ChatQueuedMessageSendState.Sending));
        queue.AddMessage("main", new ChatQueuedMessage(
            "q-queued", "not sent", created, "n2"));
        queue.AddMessage("main", new ChatQueuedMessage(
            "q-failed", "already failed", created, "n3",
            ChatQueuedMessageSendState.Failed, "earlier"));
        queue.AddRequest(new ChatQueuedSendRequest(
            "q-sending", "run-1", "main", "still sending", "still sending", "n1", null));
        queue.AddRequest(new ChatQueuedSendRequest(
            "gone", "run-2", "main", "acked", "acked", "n9", null));

        queue.ClearForReconnect();

        var messages = queue.SnapshotMessages()["main"];
        Assert.Equal(3, messages.Count);
        Assert.All(messages, message =>
            Assert.Equal(ChatQueuedMessageSendState.Failed, message.SendState));
        Assert.Equal(
            "The connection dropped after the send started. The message may have been sent. Check the conversation before trying again.",
            messages.Single(message => message.Id == "q-sending").ErrorText);
        Assert.Equal(
            "The connection dropped before the gateway accepted this message.",
            messages.Single(message => message.Id == "q-queued").ErrorText);
        Assert.Equal("earlier", messages.Single(message => message.Id == "q-failed").ErrorText);
        Assert.NotNull(queue.FindRequest("main", "q-sending"));
        Assert.Null(queue.FindRequest("main", "gone"));
    }

    [Fact]
    public void Reconnect_ReportsUncertainWhenQueuedMessagePreviouslyReceivedInFlight()
    {
        var queue = new ChatQueueState();
        queue.AddMessage("main", new ChatQueuedMessage(
            "q-in-flight", "deferred", DateTimeOffset.UtcNow, "n1",
            ChatQueuedMessageSendState.Sending));
        queue.AddRequest(new ChatQueuedSendRequest(
            "q-in-flight", "run-1", "main", "deferred", "deferred", "n1", null));

        Assert.True(queue.RequeueDeferredAdmission(
            "main",
            "q-in-flight",
            hasActiveRun: true).Requeued);
        queue.ClearForReconnect();

        var message = Assert.Single(queue.SnapshotMessages()["main"]);
        Assert.Equal(ChatQueuedMessageSendState.Failed, message.SendState);
        Assert.Contains("may have been sent", message.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconnect_PreservesUncertainDirectDraftRequest()
    {
        var queue = new ChatQueueState();
        var request = new ChatQueuedSendRequest(
            "draft-1",
            "draft-1",
            "main",
            "draft text",
            "draft text",
            "nonce-1",
            null,
            GatewayIdempotencyKey: "draft-1",
            AdmissionOutcomeUncertain: true,
            AdmissionUncertainSessionId: "session-1");
        queue.AddRequest(request);

        queue.ClearForReconnect();

        Assert.Equal(
            request,
            queue.FindUncertainDirectRequest("main", "draft-1"));
    }
}
