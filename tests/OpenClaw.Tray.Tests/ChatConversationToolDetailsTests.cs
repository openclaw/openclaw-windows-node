using System.Reflection;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatConversationToolDetailsTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void NativeDetailsWaitForHistoryCommittedInTheCurrentConnection(bool native, bool current, bool expected) =>
        Assert.Equal(expected, ChatConversationToolDetails.HasCurrentHistory(
            native, "one", current ? new HashSet<string> { "one" } : null));

    private static ChatTimelineItem Tool(string output = "initial", string? run = "run-one") =>
        new("tool-entry", ChatTimelineItemKind.ToolCall, "input",
            ToolName: "read", ToolOutput: output, ToolCallId: "call-one", ToolRunId: run);

    [Fact]
    public void LiveOutputComesFromCurrentSnapshotNotClickTime()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, ChatConversationPanelStateTests.GatewayProxy>();
        var context = ChatConversationContext.Capture("gateway-one", client,
            new() { Key = "one", SessionId = "session-one", AgentId = "writing" }, "profile-a")!;
        using var panels = new ChatConversationPanelState(_ => context);
        panels.Bind("one");
        var details = new ChatConversationToolDetails();
        var request = new ChatToolDetailRequest("one", 3, "tool-entry", "run-one", 5, "call-one", "read");
        Assert.True(details.Open(panels, context, request, "one", 3, 5, [Tool()]));
        var updated = Tool("latest streamed output");
        Assert.True(details.TryResolve(panels, "one", 3, 5, [updated], out var actual));
        Assert.Same(updated, actual);
        Assert.Equal("latest streamed output", actual.ToolOutput);
    }

    [Fact]
    public void HistoryCommitMakesDetailsUnavailableAndCurrentCardCanReopen()
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, ChatConversationPanelStateTests.GatewayProxy>();
        var context = ChatConversationContext.Capture("gateway-one", client,
            new() { Key = "one", SessionId = "session-one", AgentId = "writing" }, "profile-a")!;
        using var panels = new ChatConversationPanelState(_ => context);
        panels.Bind("one");
        var details = new ChatConversationToolDetails();
        var request = new ChatToolDetailRequest("one", 3, "tool-entry", "run-one", 5, "call-one", "read");
        Assert.True(details.Open(panels, context, request, "one", 3, 5, [Tool()]));
        Assert.False(details.TryResolve(panels, "one", 3, 6, [Tool("history")], out _));
        Assert.Equal("details", panels.Layout.SelectedPanel);
        Assert.True(details.Open(panels, context, request with { HistoryRevision = 6 },
            "one", 3, 6, [Tool("history")]));
        Assert.True(details.TryResolve(panels, "one", 3, 6, [Tool("history")], out var actual));
        Assert.Equal("history", actual.ToolOutput);
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData("principal")]
    [InlineData("epoch")]
    [InlineData("session")]
    [InlineData("reset")]
    [InlineData("client")]
    [InlineData("account")]
    public void AnotherAuthenticatedContextCannotAdoptSelectedTool(string change)
    {
        var client = DispatchProxy.Create<IOperatorGatewayClient, ChatConversationPanelStateTests.GatewayProxy>();
        var proxy = (ChatConversationPanelStateTests.GatewayProxy)client;
        var original = ChatConversationContext.Capture("gateway-one", client,
            new() { Key = "one", SessionId = "session-one", AgentId = "writing" }, "profile-a")!;
        var context = original;
        using var panels = new ChatConversationPanelState(key => key == context.Identity.SessionKey ? context : null);
        panels.Bind("one");
        var details = new ChatConversationToolDetails();
        var request = new ChatToolDetailRequest("one", 3, "tool-entry", "run-one", 5, "call-one", "read");
        Assert.True(details.Open(panels, original, request, "one", 3, 5, [Tool()]));
        if (change == "gateway") context = original with { Identity = original.Identity with { GatewayId = "gateway-two" } };
        if (change == "principal")
        {
            proxy.SigningDeviceId = "operator-b";
            context = original with { Identity = original.Identity with { SigningDeviceId = "operator-b" } };
        }
        if (change == "epoch")
        {
            proxy.Epoch++;
            context = original with { ConnectionEpoch = proxy.Epoch!.Value };
        }
        if (change == "session") context = original with { Identity = original.Identity with { SessionKey = "two" } };
        if (change == "reset") context = original with { Identity = original.Identity with { SessionId = "session-reset" } };
        if (change == "account") context = original with { Identity = original.Identity with { UserId = "profile-b" } };
        if (change == "client") context = original with
        {
            Client = DispatchProxy.Create<IOperatorGatewayClient, ChatConversationPanelStateTests.GatewayProxy>()
        };
        panels.CommitSelection(context.Identity.SessionKey);
        Assert.False(details.TryResolve(panels, context.Identity.SessionKey, 3, 5, [Tool()], out _));
        Assert.False(details.Open(panels, original, request, "one", 3, 5, [Tool()]));
    }
}
