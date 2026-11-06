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

    [Fact]
    public void RenderReceiptIdentifiesAFreshHistoryRevisionWithoutChangingLoadedKeys()
    {
        var before = Snapshot() with { HistoryRevisions = new Dictionary<string, long> { ["a"] = 4, ["b"] = 9, ["unloaded"] = 99 } };
        var after = before with { HistoryRevisions = new Dictionary<string, long> { ["a"] = 4, ["b"] = 10, ["unloaded"] = 99 } };
        using var oldReceipt = JsonDocument.Parse(GatewayFixtureRenderObservation.Create(before, "b", true));
        using var newReceipt = JsonDocument.Parse(GatewayFixtureRenderObservation.Create(after, "b", true));
        Assert.Equal(9, oldReceipt.RootElement.GetProperty("historyRevisions").GetProperty("b").GetInt64());
        Assert.Equal(10, newReceipt.RootElement.GetProperty("historyRevisions").GetProperty("b").GetInt64());
        Assert.Equal(4, newReceipt.RootElement.GetProperty("historyRevisions").GetProperty("a").GetInt64());
        Assert.False(newReceipt.RootElement.GetProperty("historyRevisions").TryGetProperty("unloaded", out _));
    }

    [Fact]
    public void MissingRevisionIsExplicitlyUnqualifiedInsteadOfFabricatingFreshness()
    {
        using var receipt = JsonDocument.Parse(GatewayFixtureRenderObservation.Create(Snapshot(), "b", true));
        Assert.Equal(JsonValueKind.Null, receipt.RootElement.GetProperty("historyRevisions").GetProperty("b").ValueKind);
    }

    [Fact]
    public void PositioningReceiptJoinsFreshRequestNativeCompletionAndRenderedGeometry()
    {
        var state = new GatewayFixturePositioningState();
        state.Begin("old", 9);
        state.RenderedFrame(ownerPending: false, nativeIdle: true);
        Assert.False(Settled());
        state.RenderedFrame(ownerPending: false, nativeIdle: true);
        Assert.True(Settled());
        state.Begin("fresh", 10);
        Assert.False(Settled());
        state.RenderedFrame(ownerPending: true, nativeIdle: true);
        state.ScrollStarted(41);
        state.RenderedFrame(ownerPending: false, nativeIdle: true);
        Assert.False(Settled());
        state.ScrollCompleted(99); // An unrelated completion cannot settle41.
        state.RenderedFrame(ownerPending: false, nativeIdle: true);
        Assert.False(Settled());
        state.ScrollCompleted(41);
        state.RenderedFrame(ownerPending: false, nativeIdle: false);
        Assert.False(Settled());
        state.RenderedFrame(ownerPending: false, nativeIdle: true);
        Assert.False(Settled());
        state.RenderedFrame(ownerPending: false, nativeIdle: true);
        using var receipt = JsonDocument.Parse(state.Receipt());
        Assert.True(Settled());
        Assert.Equal(10, receipt.RootElement.GetProperty("historyRevision").GetInt64());
        Assert.Equal("fresh", receipt.RootElement.GetProperty("completedRequestKey").GetString());

        bool Settled()
        {
            using var receipt = JsonDocument.Parse(state.Receipt());
            return receipt.RootElement.GetProperty("settled").GetBoolean();
        }
    }

    [Fact]
    public void NativePositioningObservationIsFixtureScopedAndCannotDriveNavigation()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Chat", "GatewayFixturePositioningObservation.cs"));
        Assert.Contains("GatewayFixtureIsolation.IsEnabled ? new(view, ownerPending) : null", source);
        Assert.Contains("CompositionTarget.Rendering += OnRendering", source);
        Assert.Contains("CompositionTarget.Rendering -= OnRendering", source);
        Assert.Contains(".BringingIntoView += OnBringingIntoView", source);
        Assert.Contains(".ScrollCompleted += OnScrollCompleted", source);
        Assert.DoesNotContain("StartBringItemIntoView(", source);
        Assert.DoesNotContain(".ScrollTo(", source);
        Assert.DoesNotContain(".ChangeView(", source);
        Assert.DoesNotContain("DispatcherTimer", source);
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
