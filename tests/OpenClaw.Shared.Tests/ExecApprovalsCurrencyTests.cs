using System.Collections.Generic;
using System.Linq;
using Xunit;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Tests;

public class ExecApprovalsCurrencyTests
{
    private static ExecApprovalsResolved Resolved(
        ExecSecurity security,
        ExecAsk ask,
        ExecSecurity askFallback = ExecSecurity.Deny,
        params string[] patterns)
        => new()
        {
            AgentId = "agent-1",
            Defaults = new ExecApprovalsResolvedDefaults
            {
                Security = security,
                Ask = ask,
                AskFallback = askFallback,
            },
            Allowlist = patterns.Select(p => new ExecAllowlistEntry { Pattern = p }).ToList(),
        };

    private static ExecApprovalsResolved ResolvedWithEntries(params ExecAllowlistEntry[] entries)
        => new()
        {
            AgentId = "agent-1",
            Defaults = new ExecApprovalsResolvedDefaults
            {
                Security = ExecSecurity.Allowlist,
                Ask = ExecAsk.OnMiss,
                AskFallback = ExecSecurity.Deny,
            },
            Allowlist = entries.ToList(),
        };

    // A grant is (pattern, argPattern, source), not the pattern alone. Tightening the
    // entry an approval relied on keeps its pattern present, so fingerprinting only the
    // pattern would report the policy unchanged and let the stale approval execute.
    [Fact]
    public void AddingAnArgPatternToTheReliedOnEntry_IsNotCurrent()
    {
        var snap = ExecApprovalsCurrency.Capture(
            ResolvedWithEntries(new ExecAllowlistEntry { Pattern = "**/tool.exe" }));

        Assert.False(snap.IsStillCurrent(
            ResolvedWithEntries(new ExecAllowlistEntry
            {
                Pattern = "**/tool.exe",
                ArgPattern = "^--safe\u0000$",
            })));
    }

    [Fact]
    public void NarrowingAnExistingArgPattern_IsNotCurrent()
    {
        var snap = ExecApprovalsCurrency.Capture(
            ResolvedWithEntries(new ExecAllowlistEntry
            {
                Pattern = "**/tool.exe",
                ArgPattern = "^.*\u0000$",
            }));

        Assert.False(snap.IsStillCurrent(
            ResolvedWithEntries(new ExecAllowlistEntry
            {
                Pattern = "**/tool.exe",
                ArgPattern = "^--safe\u0000$",
            })));
    }

    // Marking a hand-written path-only entry as generated makes ExecAllowlistMatcher skip
    // it (a generated entry without an argPattern is inert), so it is a revocation.
    [Fact]
    public void MarkingAPathOnlyEntryAsGenerated_IsNotCurrent()
    {
        var snap = ExecApprovalsCurrency.Capture(
            ResolvedWithEntries(new ExecAllowlistEntry { Pattern = "**/tool.exe" }));

        Assert.False(snap.IsStillCurrent(
            ResolvedWithEntries(new ExecAllowlistEntry
            {
                Pattern = "**/tool.exe",
                Source = "allow-always",
            })));
    }

    [Fact]
    public void AnUnchangedBoundEntry_IsCurrent()
    {
        ExecAllowlistEntry Entry() => new()
        {
            Pattern = "**/tool.exe",
            ArgPattern = "^--safe\u0000$",
            Source = "allow-always",
        };

        var snap = ExecApprovalsCurrency.Capture(ResolvedWithEntries(Entry()));
        Assert.True(snap.IsStillCurrent(ResolvedWithEntries(Entry())));
    }

    // Patterns name Windows paths, so they stay case-insensitive.
    [Fact]
    public void ThePatternComparisonStaysCaseInsensitive()
    {
        var snap = ExecApprovalsCurrency.Capture(
            ResolvedWithEntries(new ExecAllowlistEntry { Pattern = @"C:\Tools\Tool.exe" }));

        Assert.True(snap.IsStillCurrent(
            ResolvedWithEntries(new ExecAllowlistEntry { Pattern = @"c:\tools\tool.exe" })));
    }

    [Fact]
    public void UnchangedPolicy_IsCurrent()
    {
        var snap = ExecApprovalsCurrency.Capture(
            Resolved(ExecSecurity.Allowlist, ExecAsk.OnMiss, patterns: ["git*"]));
        Assert.True(snap.IsStillCurrent(
            Resolved(ExecSecurity.Allowlist, ExecAsk.OnMiss, patterns: ["git*"])));
    }

    [Fact]
    public void SecurityTightened_IsNotCurrent()
    {
        var snap = ExecApprovalsCurrency.Capture(Resolved(ExecSecurity.Allowlist, ExecAsk.OnMiss));
        Assert.False(snap.IsStillCurrent(Resolved(ExecSecurity.Deny, ExecAsk.OnMiss)));
    }

    [Fact]
    public void SecurityLoosened_StaysCurrent()
    {
        var snap = ExecApprovalsCurrency.Capture(Resolved(ExecSecurity.Allowlist, ExecAsk.OnMiss));
        Assert.True(snap.IsStillCurrent(Resolved(ExecSecurity.Full, ExecAsk.OnMiss)));
    }

    [Fact]
    public void AskRaised_IsNotCurrent()
    {
        var snap = ExecApprovalsCurrency.Capture(Resolved(ExecSecurity.Allowlist, ExecAsk.OnMiss));
        Assert.False(snap.IsStillCurrent(Resolved(ExecSecurity.Allowlist, ExecAsk.Always)));
    }

    [Fact]
    public void AllowlistEntryRevoked_IsNotCurrent()
    {
        var snap = ExecApprovalsCurrency.Capture(
            Resolved(ExecSecurity.Allowlist, ExecAsk.OnMiss, patterns: ["git*", "npm*"]));
        Assert.False(snap.IsStillCurrent(
            Resolved(ExecSecurity.Allowlist, ExecAsk.OnMiss, patterns: ["git*"])));
    }

    [Fact]
    public void AllowlistEntryAdded_StaysCurrent()
    {
        var snap = ExecApprovalsCurrency.Capture(
            Resolved(ExecSecurity.Allowlist, ExecAsk.OnMiss, patterns: ["git*"]));
        Assert.True(snap.IsStillCurrent(
            Resolved(ExecSecurity.Allowlist, ExecAsk.OnMiss, patterns: ["git*", "npm*"])));
    }

    [Fact]
    public void AskFallbackTightened_IsNotCurrent()
    {
        var snap = ExecApprovalsCurrency.Capture(
            Resolved(ExecSecurity.Full, ExecAsk.Always, ExecSecurity.Full));

        Assert.False(snap.IsStillCurrent(
            Resolved(ExecSecurity.Full, ExecAsk.Always, ExecSecurity.Deny)));
    }
}
