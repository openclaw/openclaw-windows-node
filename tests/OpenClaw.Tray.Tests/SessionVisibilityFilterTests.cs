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
    public void IsCleanCompleted_RecognizesSuccessfulCompletedStatuses(string status)
    {
        var session = new SessionInfo { Status = status };

        Assert.True(SessionVisibilityFilter.IsCleanCompleted(session));
        Assert.False(SessionVisibilityFilter.IsVisibleWhenCompletedHidden(session));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("killed")]
    [InlineData("timeout")]
    [InlineData("running")]
    [InlineData("unknown")]
    public void IsCleanCompleted_LeavesNonSuccessfulTerminalAndActiveStatusesVisible(string status)
    {
        var session = new SessionInfo { Status = status };

        Assert.False(SessionVisibilityFilter.IsCleanCompleted(session));
        Assert.True(SessionVisibilityFilter.IsVisibleWhenCompletedHidden(session));
    }

    [Fact]
    public void IsCleanCompleted_KeepsAbortedDoneSessionsVisible()
    {
        var session = new SessionInfo
        {
            Status = "done",
            AbortedLastRun = true,
        };

        Assert.False(SessionVisibilityFilter.IsCleanCompleted(session));
        Assert.True(SessionVisibilityFilter.IsVisibleWhenCompletedHidden(session));
    }

    [Fact]
    public void VisibleSessions_HidesOnlyCleanCompletedSessionsByDefault()
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
    [InlineData("done", false, ChatThreadStatus.Completed)]
    [InlineData("completed", false, ChatThreadStatus.Completed)]
    [InlineData("done", true, ChatThreadStatus.Running)]
    [InlineData("unknown", false, ChatThreadStatus.Running)]
    public void ToChatThreadStatus_ReusesCleanCompletionSemantics(
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
    public void VisibleChatPickerThreads_HidesCompletedThreads()
    {
        var threads = new[]
        {
            new ChatThread { Id = "running", Title = "Running", Status = ChatThreadStatus.Running },
            new ChatThread { Id = "completed", Title = "Completed", Status = ChatThreadStatus.Completed },
            new ChatThread { Id = "suspended", Title = "Suspended", Status = ChatThreadStatus.Suspended },
        };

        var visible = SessionVisibilityFilter.VisibleChatPickerThreads(threads)
            .Select(thread => thread.Id)
            .ToArray();

        Assert.Equal(new[] { "running", "suspended" }, visible);
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
