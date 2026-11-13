using OpenClaw.Chat;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatSubagentPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private static ChatThread Child(string id, ChatActivity activity = ChatActivity.Idle, string? runStatus = null,
        bool waitingForSubagents = false, DateTimeOffset? started = null, DateTimeOffset? updated = null,
        double? runtimeMs = null, long? endedAt = null) => new()
    {
        Id = id, Title = id, ParentSessionKey = "parent", IsSubagent = true,
        Activity = activity, RunStatus = runStatus, WaitingForSubagents = waitingForSubagents,
        CreatedAt = started, UpdatedAt = updated, RuntimeMs = runtimeMs, EndedAt = endedAt,
    };

    [Theory]
    [InlineData(ChatActivity.Working, false, "failed", "Working")]
    [InlineData(ChatActivity.AwaitingPermission, false, null, "AwaitingApproval")]
    [InlineData(ChatActivity.AwaitingInput, false, null, "AwaitingAnswer")]
    [InlineData(ChatActivity.Idle, true, "completed", "AwaitingSubagents")]
    [InlineData(ChatActivity.Idle, false, "queued", "Queued")]
    [InlineData(ChatActivity.Idle, false, "timeout", "Failed")]
    [InlineData(ChatActivity.Error, false, null, "Failed")]
    [InlineData(ChatActivity.Idle, false, "killed", "Stopped")]
    [InlineData(ChatActivity.Idle, false, "completed", "Done")]
    [InlineData(ChatActivity.Idle, false, null, "Ended")]
    [InlineData(ChatActivity.Idle, false, "unknown", "Ended")]
    [InlineData(ChatActivity.Idle, false, "running", "Ended")]
    public void LiveActivityOutranksTheLatestRecordedOutcome(
        ChatActivity activity, bool waitingForSubagents, string? runStatus, string expected)
    {
        var status = ChatSubagentPresentation.Classify(Child("child", activity, runStatus, waitingForSubagents));

        Assert.Equal(expected, status.ToString());
    }

    [Fact]
    public void GroupsRunningBeforeFinishedWithNewestWorkFirst()
    {
        var summary = ChatSubagentPresentation.Project([
            Child("old-running", ChatActivity.Working, started: Now.AddMinutes(-10)),
            Child("done-early", runStatus: "completed", updated: Now.AddMinutes(-9)),
            Child("waiting", waitingForSubagents: true, started: Now.AddMinutes(-2)),
            Child("failed-late", runStatus: "failed", updated: Now.AddMinutes(-1)),
            Child("queued", runStatus: "queued"),
        ], Now);

        Assert.Equal(["waiting", "old-running", "queued"], summary.Running.Select(row => row.Thread.Id));
        Assert.Equal(["failed-late", "done-early"], summary.Finished.Select(row => row.Thread.Id));
        Assert.Equal(3, summary.RunningCount);
        Assert.Equal(5, summary.Total);
    }

    [Fact]
    public void ElapsedCountsFromStartWhileLiveAndUsesTheRecordedRuntimeOnceFinished()
    {
        var started = Now.AddMinutes(-3);
        var summary = ChatSubagentPresentation.Project([
            Child("live", ChatActivity.Working, started: started, runtimeMs: 1_000),
            Child("queued", runStatus: "queued", started: started),
            Child("runtime", runStatus: "completed", started: started, runtimeMs: 42_000),
            Child("ended", runStatus: "failed", started: started,
                endedAt: started.AddSeconds(75).ToUnixTimeMilliseconds()),
            Child("unknown", runStatus: "completed", started: started),
        ], Now);
        TimeSpan? Elapsed(string id) => summary.Running.Concat(summary.Finished)
            .Single(row => row.Thread.Id == id).Elapsed;

        Assert.Equal(TimeSpan.FromMinutes(3), Elapsed("live"));
        Assert.Null(Elapsed("queued"));
        Assert.Equal(TimeSpan.FromSeconds(42), Elapsed("runtime"));
        Assert.Equal(TimeSpan.FromSeconds(75), Elapsed("ended"));
        Assert.Null(Elapsed("unknown"));
    }
}
