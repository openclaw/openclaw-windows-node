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
}
