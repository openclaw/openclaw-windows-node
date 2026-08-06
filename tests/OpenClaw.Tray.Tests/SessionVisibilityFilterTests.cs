using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class SessionVisibilityFilterTests
{
    [Theory]
    [InlineData("done")]
    [InlineData("DONE")]
    [InlineData(" completed ")]
    public void IsCompleted_RecognizesSuccessfulCompletedStatuses(string status)
    {
        var session = new SessionInfo { Status = status };

        Assert.True(SessionVisibilityFilter.IsCompleted(session));
        Assert.False(SessionVisibilityFilter.IsVisibleWhenCompletedHidden(session));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("killed")]
    [InlineData("timeout")]
    [InlineData("running")]
    [InlineData("unknown")]
    public void IsCompleted_LeavesWorkingAndNonSuccessOutcomesVisible(string status)
    {
        var session = new SessionInfo { Status = status };

        Assert.False(SessionVisibilityFilter.IsCompleted(session));
        Assert.True(SessionVisibilityFilter.IsVisibleWhenCompletedHidden(session));
    }

    [Fact]
    public void IsCompleted_LeavesAbortedDoneSessionsVisibleAsStoppedWork()
    {
        var session = new SessionInfo
        {
            Status = "done",
            AbortedLastRun = true,
        };

        Assert.False(SessionVisibilityFilter.IsCompleted(session));
        Assert.True(SessionVisibilityFilter.IsVisibleWhenCompletedHidden(session));
    }

    [Fact]
    public void VisibleSessions_HidesOnlySuccessfulCompletedSessionsByDefault()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "done", Status = "done" },
            new SessionInfo { Key = "failed", Status = "failed" },
            new SessionInfo { Key = "killed", Status = "killed" },
            new SessionInfo { Key = "timeout", Status = "timeout" },
            new SessionInfo { Key = "aborted-done", Status = "done", AbortedLastRun = true },
            new SessionInfo { Key = "running", Status = "running" },
        };

        var visible = SessionVisibilityFilter.VisibleSessions(sessions, showCompleted: false)
            .Select(s => s.Key)
            .ToArray();

        Assert.Equal(new[] { "failed", "killed", "timeout", "aborted-done", "running" }, visible);
    }

    [Fact]
    public void VisibleSessions_ShowCompletedPreservesAllSessions()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "done", Status = "done" },
            new SessionInfo { Key = "failed", Status = "failed" },
        };

        var visible = SessionVisibilityFilter.VisibleSessions(sessions, showCompleted: true)
            .Select(s => s.Key)
            .ToArray();

        Assert.Equal(new[] { "done", "failed" }, visible);
    }

    [Theory]
    [InlineData("running", true, ChatThreadStatus.Running)]
    [InlineData("running", false, ChatThreadStatus.Created)]
    [InlineData("done", false, ChatThreadStatus.Created)]
    [InlineData("failed", false, ChatThreadStatus.Created)]
    [InlineData("killed", false, ChatThreadStatus.Created)]
    [InlineData("timeout", false, ChatThreadStatus.Created)]
    public void ToChatThreadStatus_UsesCanonicalRunLiveness(
        string status,
        bool hasActiveRun,
        ChatThreadStatus expected)
    {
        var session = new SessionInfo
        {
            Status = status,
            HasActiveRun = hasActiveRun,
        };

        Assert.Equal(expected, SessionVisibilityFilter.ToChatThreadStatus(session));
    }

    [Fact]
    public void VisibleChatPickerThreads_ShowsSessionsWithConversationActivity()
    {
        var threads = new[]
        {
            new ChatThread
            {
                Id = "completed-chat",
                Title = "Completed chat",
                Status = ChatThreadStatus.Ended,
                TotalTokens = 42,
            },
            new ChatThread
            {
                Id = "working",
                Title = "Working",
                Status = ChatThreadStatus.Running,
                Activity = ChatActivity.Working,
            },
            new ChatThread
            {
                Id = "input-chat",
                Title = "Input chat",
                Status = ChatThreadStatus.Ended,
                InputTokens = 1,
            },
            new ChatThread
            {
                Id = "output-chat",
                Title = "Output chat",
                Status = ChatThreadStatus.Ended,
                OutputTokens = 1,
            },
            new ChatThread
            {
                Id = "empty-placeholder",
                Title = "Empty placeholder",
                Status = ChatThreadStatus.Running,
            },
            new ChatThread
            {
                Id = "context-only-placeholder",
                Title = "Context-only placeholder",
                Status = ChatThreadStatus.Running,
                ContextTokens = 200_000,
            },
            new ChatThread
            {
                Id = "selected-empty",
                Title = "Selected empty",
                Status = ChatThreadStatus.Ended,
            },
        };

        var visible = SessionVisibilityFilter.VisibleChatPickerThreads(threads, "selected-empty")
            .Select(thread => thread.Id)
            .ToArray();

        Assert.Equal(
            new[] { "completed-chat", "working", "input-chat", "output-chat", "selected-empty" },
            visible);
    }

    [Theory]
    [InlineData("all", "all")]
    [InlineData("Slack", "Slack")]
    [InlineData("slack", "slack")]
    [InlineData("missing", "all")]
    public void ResolveActiveChannel_PreservesOnlyVisibleChannels(string activeChannel, string expected)
    {
        var visibleChannels = new[] { "Slack", "WhatsApp" };

        Assert.Equal(expected, SessionVisibilityFilter.ResolveActiveChannel(activeChannel, visibleChannels));
    }
}
