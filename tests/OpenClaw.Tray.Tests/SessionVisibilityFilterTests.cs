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
    [InlineData("failed")]
    [InlineData("killed")]
    [InlineData("timeout")]
    public void IsEnded_RecognizesTerminalStatuses(string status)
    {
        var session = new SessionInfo { Status = status };

        Assert.True(SessionVisibilityFilter.IsEnded(session));
        Assert.False(SessionVisibilityFilter.IsVisibleWhenEndedHidden(session));
    }

    [Theory]
    [InlineData("running")]
    [InlineData("unknown")]
    public void IsEnded_LeavesActiveAndUnknownStatusesVisible(string status)
    {
        var session = new SessionInfo { Status = status };

        Assert.False(SessionVisibilityFilter.IsEnded(session));
        Assert.True(SessionVisibilityFilter.IsVisibleWhenEndedHidden(session));
    }

    [Fact]
    public void IsEnded_KeepsAbortedDoneSessionsVisible()
    {
        var session = new SessionInfo
        {
            Status = "done",
            AbortedLastRun = true,
        };

        Assert.False(SessionVisibilityFilter.IsEnded(session));
        Assert.True(SessionVisibilityFilter.IsVisibleWhenEndedHidden(session));
    }

    [Fact]
    public void VisibleSessions_HidesEndedSessionsByDefault()
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

        var visible = SessionVisibilityFilter.VisibleSessions(sessions, showEnded: false)
            .Select(s => s.Key)
            .ToArray();

        Assert.Equal(new[] { "aborted-done", "running" }, visible);
    }

    [Fact]
    public void VisibleSessions_ShowEndedPreservesAllSessions()
    {
        var sessions = new[]
        {
            new SessionInfo { Key = "done", Status = "done" },
            new SessionInfo { Key = "failed", Status = "failed" },
        };

        var visible = SessionVisibilityFilter.VisibleSessions(sessions, showEnded: true)
            .Select(s => s.Key)
            .ToArray();

        Assert.Equal(new[] { "done", "failed" }, visible);
    }

    [Theory]
    [InlineData("done", false, ChatThreadStatus.Ended)]
    [InlineData("completed", false, ChatThreadStatus.Ended)]
    [InlineData("failed", false, ChatThreadStatus.Ended)]
    [InlineData("killed", false, ChatThreadStatus.Ended)]
    [InlineData("timeout", false, ChatThreadStatus.Ended)]
    [InlineData("done", true, ChatThreadStatus.Running)]
    [InlineData("unknown", false, ChatThreadStatus.Running)]
    public void ToChatThreadStatus_ReusesEndedSemantics(
        string status,
        bool abortedLastRun,
        ChatThreadStatus expected)
    {
        var session = new SessionInfo
        {
            Status = status,
            AbortedLastRun = abortedLastRun,
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
