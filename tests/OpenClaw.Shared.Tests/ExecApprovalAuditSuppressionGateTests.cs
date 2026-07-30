using Xunit;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Tests;

public class ExecApprovalAuditSuppressionGateTests
{
    [Fact]
    public void SetSuppressions_RequiresApproval()
        => Assert.True(ExecApprovalAuditSuppressionGate.RequiresExtraApproval(
            new[] { "openclaw", "config", "set", "security.audit.suppressions", "x" },
            "openclaw config set security.audit.suppressions x"));

    [Fact]
    public void GetSuppressions_IsReadOnly_NoApproval()
        => Assert.False(ExecApprovalAuditSuppressionGate.RequiresExtraApproval(
            new[] { "openclaw", "config", "get", "security.audit.suppressions" },
            "openclaw config get security.audit.suppressions"));

    [Theory]
    [InlineData("schema")]
    [InlineData("validate")]
    public void InspectSuppressions_IsReadOnly_NoApproval(string verb)
        => Assert.False(ExecApprovalAuditSuppressionGate.RequiresExtraApproval(
            new[] { "openclaw", "config", verb, "security.audit.suppressions" },
            $"openclaw config {verb} security.audit.suppressions"));

    [Fact]
    public void PnpmOpenclawGet_IsReadOnly_NoApproval()
        => Assert.False(ExecApprovalAuditSuppressionGate.RequiresExtraApproval(
            new[] { "pnpm", "openclaw", "config", "get", "security.audit.suppressions" },
            "pnpm openclaw config get security.audit.suppressions"));

    [Fact]
    public void UnrelatedCommand_NoApproval()
        => Assert.False(ExecApprovalAuditSuppressionGate.RequiresExtraApproval(
            new[] { "git", "status" }, "git status"));

    [Fact]
    public void ObfuscatedReference_RequiresApproval()
        => Assert.True(ExecApprovalAuditSuppressionGate.RequiresExtraApproval(
            new[] { "node", "-e", "cfg[\"security\"][\"audit\"][\"suppressions\"]=[]" },
            "node -e cfg[\"security\"][\"audit\"][\"suppressions\"]=[]"));

    [Fact]
    public void ShellWrappedSet_RequiresApproval()
        // Shell-wrapped reads are not exempted (over-requires approval, which is safe).
        => Assert.True(ExecApprovalAuditSuppressionGate.RequiresExtraApproval(
            new[] { "cmd", "/c", "openclaw config set security.audit.suppressions x" },
            "cmd /c openclaw config set security.audit.suppressions x"));

    [Fact]
    public void GlobalFlagBeforeConfig_StillReadOnly()
        => Assert.False(ExecApprovalAuditSuppressionGate.RequiresExtraApproval(
            new[] { "openclaw", "--no-color", "config", "get", "security.audit.suppressions" },
            "openclaw --no-color config get security.audit.suppressions"));

    [Fact]
    public void ValueGlobalOptionBeforeConfig_StillReadOnly()
        => Assert.False(ExecApprovalAuditSuppressionGate.RequiresExtraApproval(
            new[] { "openclaw", "--profile", "prod", "config", "get", "security.audit.suppressions" },
            "openclaw --profile prod config get security.audit.suppressions"));

    [Fact]
    public void CmdSuffixOpenclaw_IsRecognizedAsReadOnly()
        => Assert.False(ExecApprovalAuditSuppressionGate.RequiresExtraApproval(
            new[] { "openclaw.cmd", "config", "get", "security.audit.suppressions" },
            "openclaw.cmd config get security.audit.suppressions"));

    [Fact]
    public void GlobalFlagBeforeSet_StillRequiresApproval()
        => Assert.True(ExecApprovalAuditSuppressionGate.RequiresExtraApproval(
            new[] { "openclaw", "--dev", "config", "set", "security.audit.suppressions", "x" },
            "openclaw --dev config set security.audit.suppressions x"));
}
