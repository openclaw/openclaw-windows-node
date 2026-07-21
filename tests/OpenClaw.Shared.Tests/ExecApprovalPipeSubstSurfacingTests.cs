using System;
using System.IO;
using System.Linq;
using OpenClaw.Shared;
using OpenClaw.Shared.ExecApprovals;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Regression guard for the exec-approval unwrapper fix: the shell-wrapper decomposer must surface
/// commands reached through the pipeline operator "|" and command substitution / subexpression
/// ($(...), @(...), `...`), not only ";"/"&&"/"||"-chained commands. Because system.run executes
/// the approved command through a real shell (powershell -Command / cmd /C), those operators run a
/// smuggled sub-command; before the fix it was never surfaced as an evaluation target, so an
/// anchored deny rule was silently bypassed under a benign allow rule. These tests assert the
/// smuggled command is now surfaced AND blocked, that legitimate fully-approved pipes still pass,
/// and that the split stays quote/paren/operator-correct.
/// </summary>
public class ExecApprovalPipeSubstSurfacingTests
{
    private const string Victim = "C:/Windows/Temp/victim";

    // ── Part 1: the decomposer now surfaces the smuggled command as a target ──

    [Fact]
    public void Pipe_SurfacesDownstreamStage()
    {
        var targets = ExecShellWrapperParser.Expand($"echo x | Remove-Item -Recurse -Force {Victim}", "powershell").Targets;
        Assert.Contains(targets, t => t.Command.StartsWith("Remove-Item", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Substitution_Dollar_SurfacesInner()
    {
        var targets = ExecShellWrapperParser.Expand($"echo $(Remove-Item -Recurse -Force {Victim})", "powershell").Targets;
        Assert.Contains(targets, t => t.Command.StartsWith("Remove-Item", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Subexpression_At_SurfacesInner()
    {
        // PowerShell @(...) array subexpression also evaluates its contents.
        var targets = ExecShellWrapperParser.Expand($"echo @(Remove-Item -Recurse -Force {Victim})", "powershell").Targets;
        Assert.Contains(targets, t => t.Command.StartsWith("Remove-Item", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Substitution_Backtick_FailsClosed()
    {
        // POSIX backtick substitution triggers the fail-closed guard (HasUndecomposableExec) because
        // the parser cannot safely decompose it without a full shell parser. This is the correct
        // security posture — deny rather than risk missing the inner command.
        var result = ExecShellWrapperParser.Expand($"echo `Remove-Item -Recurse -Force {Victim}`", "sh");
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Pipe_InsideSubstitution_SurfacesInnerStage()
    {
        // The '|' lives inside $(...), so it must NOT split at the top level, but the inner
        // pipeline must decompose when the substitution body is re-expanded.
        var targets = ExecShellWrapperParser.Expand($"echo $(Get-ChildItem {Victim} | Remove-Item -Recurse -Force)", "powershell").Targets;
        Assert.Contains(targets, t => t.Command.StartsWith("Remove-Item", StringComparison.OrdinalIgnoreCase));
    }

    // ── Part 2: the split stays correct for operators, quotes, and parens ──

    [Fact]
    public void DoublePipe_StillSplitsIntoExactlyTwo()
    {
        // "||" is one operator, not two single pipes — must yield 2 segments, not 3.
        var result = ExecShellWrapperParser.Expand("echo a || echo b");
        Assert.Equal(2, result.Targets.Count);
    }

    [Fact]
    public void Pipe_InsideSingleQuotes_NotSplit()
    {
        // A '|' inside single quotes is literal; the whole thing is one non-wrapped segment.
        var result = ExecShellWrapperParser.Expand("echo 'a | b'");
        Assert.Empty(result.Targets);
    }

    [Fact]
    public void SinglePipe_SplitsIntoStages()
    {
        var result = ExecShellWrapperParser.Expand("echo a | rev | tac");
        Assert.Equal(3, result.Targets.Count);
    }

    // ── Part 3: end-to-end — the anchored deny now fires; legit pipes still pass ──

    private static ExecApprovalPolicy BuildPolicy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "execpol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var policy = new ExecApprovalPolicy(dir, NullLogger.Instance);
        policy.SetRules(new[]
        {
            new ExecApprovalRule { Pattern = "Remove-Item *", Action = ExecApprovalAction.Deny, Description = "Block destructive deletes" },
            new ExecApprovalRule { Pattern = "echo *", Action = ExecApprovalAction.Allow },
            new ExecApprovalRule { Pattern = "Get-ChildItem *", Action = ExecApprovalAction.Allow },
            new ExecApprovalRule { Pattern = "Select-Object *", Action = ExecApprovalAction.Allow },
        }, ExecApprovalAction.Deny);
        return policy;
    }

    private static bool IsExplicitDeny(ExecApprovalResult r) =>
        !r.Allowed && r.Action == ExecApprovalAction.Deny && !string.IsNullOrWhiteSpace(r.MatchedPattern);

    private static bool DenyBlocks(ExecApprovalPolicy policy, string command, string shell)
    {
        if (IsExplicitDeny(policy.Evaluate(command, shell))) return true;
        foreach (var t in ExecShellWrapperParser.Expand(command, shell).Targets)
            if (IsExplicitDeny(policy.Evaluate(t.Command, t.Shell))) return true;
        return false;
    }

    [Fact]
    public void Semicolon_ChainedRemoveItem_IsBlocked_Control()
    {
        Assert.True(DenyBlocks(BuildPolicy(), $"echo a; Remove-Item -Recurse -Force {Victim}", "powershell"));
    }

    [Fact]
    public void Pipe_SmuggledRemoveItem_IsNowBlocked()
    {
        Assert.True(DenyBlocks(BuildPolicy(), $"echo a | Remove-Item -Recurse -Force {Victim}", "powershell"));
    }

    [Fact]
    public void Substitution_SmuggledRemoveItem_IsNowBlocked()
    {
        Assert.True(DenyBlocks(BuildPolicy(), $"echo $(Remove-Item -Recurse -Force {Victim})", "powershell"));
    }

    [Fact]
    public void NestedPipeInSubstitution_SmuggledRemoveItem_IsNowBlocked()
    {
        Assert.True(DenyBlocks(BuildPolicy(), $"echo $(Get-ChildItem {Victim} | Remove-Item -Recurse -Force)", "powershell"));
    }

    [Fact]
    public void LegitFullyApprovedPipe_IsNotBlocked()
    {
        // Both stages match explicit allow rules → the fix does not over-block legitimate pipes.
        Assert.False(DenyBlocks(BuildPolicy(), $"Get-ChildItem {Victim} | Select-Object -First 1", "powershell"));
    }
}
