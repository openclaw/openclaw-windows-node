using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Services;

public sealed class SessionsAttentionSnapshotTests
{
    [Fact]
    public void InitialAndUnverifiedArraysAreUnavailable_FreshEmptyIsZero()
    {
        var state = new AppState { Status = ConnectionStatus.Connected };
        Assert.Null(state.SessionsAttentionCount);
        state.Sessions = [new() { Status = "failed" }];
        Assert.Null(state.SessionsAttentionCount);
        Assert.True(state.TryPublishSessions([], 1, 1, 7, 7, connected: true));
        Assert.Equal(0, state.SessionsAttentionCount);
        state.Status = ConnectionStatus.Disconnected;
        Assert.Null(state.SessionsAttentionCount);
    }

    [Fact]
    public void AggregateExcludesArchived_PreservesQueuedAndActiveRunPrecedence()
    {
        var state = new AppState();
        SessionInfo[] rows =
        [
            new() { Status = "queued", HasActiveRun = true },
            new() { Status = "failed", HasActiveRun = false },
            new() { Status = "timeout", Archived = true },
            new() { Status = "failed", HasActiveRun = true },
            new() { Status = "completed" }
        ];
        Assert.True(state.TryPublishSessions(rows, 1, 1, 7, 7, connected: true));
        Assert.Equal(2, state.SessionsAttentionCount);
        Assert.Same(rows, state.Sessions);
    }

    [Theory]
    [InlineData(1, 2, 7L, 7L, true)]
    [InlineData(1, 1, 7L, 8L, true)]
    [InlineData(1, 1, null, 8L, true)]
    [InlineData(1, 1, 7L, null, false)]
    public void StaleClientOrSameClientReconnectCannotReplaceFreshSnapshot(
        int capturedGeneration, int liveGeneration, long? capturedEpoch, long? liveEpoch, bool connected)
    {
        var state = new AppState();
        SessionInfo[] fresh = [new() { Status = "queued" }];
        Assert.True(state.TryPublishSessions(fresh, liveGeneration, liveGeneration, 8, 8, true));
        Assert.False(state.TryPublishSessions([], capturedGeneration, liveGeneration,
            capturedEpoch, liveEpoch, connected));
        Assert.Same(fresh, state.Sessions);
        Assert.Equal(1, state.SessionsAttentionCount);
        Assert.Equal(8L, state.SessionsConnectionEpoch);
    }

    [Fact]
    public void ObserversSeeRowsCountAndFreshnessCoherently()
    {
        var state = new AppState();
        SessionInfo[] rows = [new() { Status = "queued" }];
        var notifications = new List<string?>();
        state.PropertyChanged += (_, args) =>
        {
            notifications.Add(args.PropertyName);
            Assert.Same(rows, state.Sessions);
            Assert.Equal(1, state.SessionsAttentionCount);
            Assert.Equal(7L, state.SessionsConnectionEpoch);
        };
        Assert.True(state.TryPublishSessions(rows, 1, 1, 7, 7, true));
        Assert.Equal(new[] { nameof(AppState.Sessions), nameof(AppState.SessionsConnectionEpoch),
            nameof(AppState.SessionsAttentionCount) }, notifications);
    }

    [Fact]
    public void InvalidationRetainsCachedRowsWithoutCallingThemFresh()
    {
        var state = new AppState();
        SessionInfo[] rows = [new() { Status = "queued" }];
        state.TryPublishSessions(rows, 1, 1, 7, 7, true);
        state.InvalidateSessionsSnapshot();
        Assert.Same(rows, state.Sessions);
        Assert.Null(state.SessionsAttentionCount);
        Assert.Null(state.SessionsConnectionEpoch);
    }
}
