using OpenClaw.Shared;
using OpenClaw.Shared.Sessions;

namespace OpenClaw.Shared.Tests;

public sealed class SessionRunStateTests
{
    [Theory]
    [InlineData("running", null, true)]
    [InlineData("running", false, false)]
    [InlineData("running", true, true)]
    [InlineData("done", true, true)]
    [InlineData("failed", true, true)]
    [InlineData("killed", true, true)]
    [InlineData("timeout", true, true)]
    [InlineData("unknown", true, true)]
    public void IsWorking_PrefersGatewayCurrentRunLiveness(
        string status,
        bool? hasActiveRun,
        bool expected)
    {
        var session = new SessionInfo { Status = status, HasActiveRun = hasActiveRun };

        Assert.Equal(expected, SessionRunState.IsWorking(session));
    }

    [Theory]
    [InlineData("running", true, SessionDisplayState.Working)]
    [InlineData("running", false, SessionDisplayState.Ready)]
    [InlineData("done", false, SessionDisplayState.Ready)]
    [InlineData("killed", false, SessionDisplayState.Ready)]
    [InlineData("failed", false, SessionDisplayState.NeedsAttention)]
    [InlineData("timeout", false, SessionDisplayState.NeedsAttention)]
    public void GetDisplayState_UsesOnlyThreeUserFacingStates(
        string status,
        bool hasActiveRun,
        SessionDisplayState expected)
    {
        var session = new SessionInfo { Status = status, HasActiveRun = hasActiveRun };

        Assert.Equal(expected, SessionRunState.GetDisplayState(session));
    }

    [Fact]
    public void IsCompleted_RequiresACompletedRunThatIsNotWorking()
    {
        Assert.True(SessionRunState.IsCompleted(new SessionInfo { Status = "done", HasActiveRun = false }));
        Assert.False(SessionRunState.IsCompleted(new SessionInfo { Status = "failed", HasActiveRun = false }));
        Assert.False(SessionRunState.IsCompleted(new SessionInfo { Status = "running", HasActiveRun = false }));
        Assert.False(SessionRunState.IsCompleted(new SessionInfo { Status = "done", AbortedLastRun = true }));
    }

    [Fact]
    public void HasStoppedLastRun_SeparatesRunContextFromTheReadyState()
    {
        var session = new SessionInfo { Status = "killed", HasActiveRun = false };

        Assert.Equal(SessionDisplayState.Ready, SessionRunState.GetDisplayState(session));
        Assert.True(SessionRunState.HasStoppedLastRun(session));
    }
}
