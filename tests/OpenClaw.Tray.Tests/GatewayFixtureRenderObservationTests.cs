using System.Text.Json;
using OpenClaw.Chat;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class GatewayFixtureRenderObservationTests
{
    [Fact]
    public void OrdinaryRenderingExposesNoFixtureObservation()
    {
        Assert.Empty(GatewayFixtureRenderObservation.Create(Snapshot(), "selected", fixtureEnabled: false));
    }

    [Fact]
    public void RenderAcknowledgesLoadedHistoriesNotOnlySelectedThread()
    {
        using var observation = JsonDocument.Parse(GatewayFixtureRenderObservation.Create(Snapshot(), "b", fixtureEnabled: true));
        Assert.Equal("b", observation.RootElement.GetProperty("selectedThreadId").GetString());
        Assert.Collection(observation.RootElement.GetProperty("loadedThreadIds").EnumerateArray(),
            key => Assert.Equal("a", key.GetString()),
            key => Assert.Equal("b", key.GetString()));
    }

    [Fact]
    public void ObservationNeverIncludesMessageContentOrUnloadedThreads()
    {
        var observation = GatewayFixtureRenderObservation.Create(Snapshot(), "b", fixtureEnabled: true);
        Assert.DoesNotContain("unloaded", observation);
        Assert.DoesNotContain("private message content", observation);
    }

    private static ChatDataSnapshot Snapshot() => new(
        [],
        new Dictionary<string, ChatTimelineState>
        {
            ["b"] = ChatTimelineState.Initial() with
            {
                HistoryLoaded = true,
                Entries = [new ChatTimelineItem("entry", ChatTimelineItemKind.User, "private message content")]
            },
            ["unloaded"] = ChatTimelineState.Initial(),
            ["a"] = ChatTimelineState.Initial() with { HistoryLoaded = true }
        },
        "a", "Connected", [], ChatComposeTarget.NotReady);
}
