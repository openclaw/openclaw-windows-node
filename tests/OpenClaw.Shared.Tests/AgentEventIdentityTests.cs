using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClaw.Shared.Tests;

public class AgentEventIdentityTests
{
    [Fact]
    public void TwoApprovalsWithEmptyRunIds_AreNotTheSame()
    {
        var first = Approval("apr-1", "requested");
        var second = Approval("apr-2", "requested");

        Assert.False(AgentEventIdentity.IsSame(first, second));
    }

    [Fact]
    public void SameApprovalAndPhase_IsTheSame()
    {
        Assert.True(AgentEventIdentity.IsSame(
            Approval("apr-1", "requested"),
            Approval("apr-1", "requested")));
    }

    [Fact]
    public void SameApprovalDifferentPhase_IsKept()
    {
        Assert.False(AgentEventIdentity.IsSame(
            Approval("apr-1", "requested"),
            Approval("apr-1", "resolved")));
    }

    [Fact]
    public void EmptyRunWithoutAnApprovalId_IsNotCollapsed()
    {
        Assert.False(AgentEventIdentity.IsSame(
            new AgentEventInfo { RunId = "", Seq = 0 },
            new AgentEventInfo { RunId = "", Seq = 0 }));
    }

    [Fact]
    public void RunIdAndSequence_StillDedup()
    {
        var first = new AgentEventInfo { RunId = "run-1", Seq = 4 };
        var same = new AgentEventInfo { RunId = "run-1", Seq = 4 };
        var later = new AgentEventInfo { RunId = "run-1", Seq = 5 };

        Assert.True(AgentEventIdentity.IsSame(first, same));
        Assert.False(AgentEventIdentity.IsSame(first, later));
    }

    private static AgentEventInfo Approval(string id, string phase)
    {
        using var document = JsonDocument.Parse(
            $$"""{"approvalId":"{{id}}","phase":"{{phase}}"}""");
        return new AgentEventInfo
        {
            RunId = "",
            Seq = 0,
            Stream = "approval",
            Data = document.RootElement.Clone(),
        };
    }
}
