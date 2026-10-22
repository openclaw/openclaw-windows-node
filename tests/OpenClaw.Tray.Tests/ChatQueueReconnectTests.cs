using OpenClaw.Chat;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public class ChatQueueReconnectTests
{
    [Fact]
    public void Reconnect_RetainsSendingPayloadForUncertainDeliveryReview()
    {
        var queue = new ChatQueueState();
        var attachment = new OpenClaw.Shared.ChatAttachment { FileName = "draft.txt", Content = "aGVsbG8=" };
        queue.AddMessage("source", new ChatQueuedMessage(
            "q1", "keep this", DateTimeOffset.UtcNow, "nonce", ChatQueuedMessageSendState.Sending));
        queue.AddRequest(new ChatQueuedSendRequest(
            "q1", "original-input", "source", "keep this", "keep this", "nonce", [attachment]));

        queue.ClearForReconnect();

        var row = Assert.Single(queue.SnapshotMessages()["source"]);
        Assert.Equal("q1", row.Id);
        var retained = Assert.IsType<ChatQueuedSendRequest>(queue.FindRequest("source", row.Id));
        Assert.Equal("original-input", retained.SendRunId);
        Assert.Same(attachment, Assert.Single(retained.Attachments!));
        Assert.False(queue.HasSendingMessages("source"));
    }

    [Fact]
    public void Reconnect_KeepsLocalFailuresAndUnconfirmedSendingPayloadsWithoutAutomaticReplay()
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
        Assert.All(messages.Where(message => message.Id != "q-sending"), message =>
            Assert.Equal(ChatQueuedMessageSendState.Failed, message.SendState));
        Assert.Equal(ChatQueuedMessageSendState.Unconfirmed, messages.Single(message => message.Id == "q-sending").SendState);
        Assert.Equal(
            "The connection dropped before the gateway accepted this message.",
            messages.Single(message => message.Id == "q-queued").ErrorText);
        Assert.Equal("earlier", messages.Single(message => message.Id == "q-failed").ErrorText);
        Assert.Equal("run-1", queue.FindRequest("main", "q-sending")!.SendRunId);
        Assert.Null(queue.FindRequest("main", "gone"));
    }
}
