using System.Text.Json;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public class ChatEventMapperThroughputTests
{
    private static AgentEventInfo Event(string stream, string json) => new()
    {
        Stream = stream,
        Data = JsonDocument.Parse(json).RootElement.Clone(),
        SessionKey = "main",
    };

    [Fact]
    public void ThinkingDeltaLength_CountsDeltaNotCumulativeText()
        => Assert.Equal(6, ChatEventMapper.ThinkingDeltaLength(
            Event("thinking", """{"text":"Let me think","delta":" think"}""")));

    [Fact]
    public void ThinkingDeltaLength_IgnoresPersistenceReceiptsAndOtherStreams()
    {
        Assert.Null(ChatEventMapper.ThinkingDeltaLength(
            Event("thinking", """{"phase":"persisted","itemId":"i1","messageId":"m1"}""")));
        Assert.Null(ChatEventMapper.ThinkingDeltaLength(
            Event("assistant", """{"delta":"hello"}""")));
    }

    [Fact]
    public void RunOutputTokens_ReadsUsageTotalOnly()
    {
        Assert.Equal(1234L, ChatEventMapper.RunOutputTokens(
            Event("usage", """{"outputTokens":1234,"usageReport":{"state":"available"}}""")));
        Assert.Null(ChatEventMapper.RunOutputTokens(Event("usage", """{"usageReport":{"state":"unavailable"}}""")));
        Assert.Null(ChatEventMapper.RunOutputTokens(Event("lifecycle", """{"outputTokens":5}""")));
    }
}
