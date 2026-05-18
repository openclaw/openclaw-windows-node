using System;
using System.Collections.Generic;
using Xunit;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Tests for PR5: evaluator skeleton, typed outcomes, and allowlist matcher.
/// Covers research doc 06 (state machine) and doc 03 (allowlist matcher).
/// All tests are stateless and UI-free.
/// </summary>
public class ExecApprovalV2EvaluatorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ExecCommandResolution Res(string raw, string? resolved = null)
        => new(raw, resolved, resolved is not null ? System.IO.Path.GetFileName(resolved) : raw, null);

    private static ExecAllowlistEntry Entry(string pattern)
        => new() { Id = Guid.NewGuid(), Pattern = pattern };

    private static ExecApprovalEvaluation MakeContext(
        ExecSecurity security = ExecSecurity.Full,
        ExecAsk ask = ExecAsk.Off,
        IReadOnlyList<ExecCommandResolution>? allowlistResolutions = null,
        IReadOnlyList<ExecAllowlistEntry>? allowlistMatches = null,
        bool skillAllow = false)
    {
        var resolutions = allowlistResolutions ?? [];
        var matches = allowlistMatches ?? [];
        return new ExecApprovalEvaluation(
            command: ["test"],
            displayCommand: "test",
            agentId: null,
            security: security,
            ask: ask,
            env: null,
            allowlistResolutions: resolutions,
            allowAlwaysPatterns: [],
            allowlistMatches: matches,
            skillAllow: skillAllow);
    }

    private static void AssertDeny(ExecHostPolicyDecision d, ExecApprovalV2Code expectedCode)
    {
        var deny = Assert.IsType<ExecHostPolicyDecision.DenyOutcome>(d);
        Assert.Equal(expectedCode, deny.Error.Code);
    }

    // ── Evaluator — five-step precedence ─────────────────────────────────────

    [Fact]
    public void Step1_SecurityDeny_IsAbsoluteOverride()
    {
        var ctx = MakeContext(security: ExecSecurity.Deny);
        AssertDeny(ExecApprovalEvaluator.Evaluate(ctx, null), ExecApprovalV2Code.SecurityDeny);
    }

    [Fact]
    public void Step1_SecurityDeny_BeatsApprovalDecisionAllow()
    {
        // A user cannot override security=deny by sending any approval decision.
        var ctx = MakeContext(security: ExecSecurity.Deny);
        AssertDeny(ExecApprovalEvaluator.Evaluate(ctx, ExecApprovalDecision.AllowOnce), ExecApprovalV2Code.SecurityDeny);
        AssertDeny(ExecApprovalEvaluator.Evaluate(ctx, ExecApprovalDecision.AllowAlways), ExecApprovalV2Code.SecurityDeny);
    }

    [Fact]
    public void Step2_UserDenied_WhenApprovalDecisionIsDeny()
    {
        var ctx = MakeContext(security: ExecSecurity.Full);
        AssertDeny(ExecApprovalEvaluator.Evaluate(ctx, ExecApprovalDecision.Deny), ExecApprovalV2Code.UserDenied);
    }

    [Fact]
    public void Step3_RequiresPrompt_WhenAskAlwaysAndNoDecision()
    {
        var ctx = MakeContext(security: ExecSecurity.Full, ask: ExecAsk.Always);
        Assert.IsType<ExecHostPolicyDecision.RequiresPromptOutcome>(
            ExecApprovalEvaluator.Evaluate(ctx, null));
    }

    [Fact]
    public void Step3_RequiresPrompt_WhenAskOnMissAndAllowlistMissAndNoDecision()
    {
        var res = Res("/usr/bin/rg", "/usr/bin/rg");
        var ctx = MakeContext(
            security: ExecSecurity.Allowlist,
            ask: ExecAsk.OnMiss,
            allowlistResolutions: [res],
            allowlistMatches: []); // no match
        Assert.IsType<ExecHostPolicyDecision.RequiresPromptOutcome>(
            ExecApprovalEvaluator.Evaluate(ctx, null));
    }

    [Fact]
    public void Step4_AllowlistMiss_WhenSecurityAllowlistAndAskOffAndNoMatch()
    {
        var res = Res("/usr/bin/rg", "/usr/bin/rg");
        var ctx = MakeContext(
            security: ExecSecurity.Allowlist,
            ask: ExecAsk.Off,
            allowlistResolutions: [res],
            allowlistMatches: []); // no match
        AssertDeny(ExecApprovalEvaluator.Evaluate(ctx, null), ExecApprovalV2Code.AllowlistMiss);
    }

    [Fact]
    public void Step5_Allow_WhenSecurityFull()
    {
        var ctx = MakeContext(security: ExecSecurity.Full);
        var result = Assert.IsType<ExecHostPolicyDecision.AllowOutcome>(
            ExecApprovalEvaluator.Evaluate(ctx, null));
        Assert.False(result.ApprovedByAsk);
    }

    [Fact]
    public void Step5_Allow_WhenAllowlistSatisfied()
    {
        var res = Res("rg", @"C:\tools\rg.exe");
        var entry = Entry(@"C:\tools\*");
        var ctx = MakeContext(
            security: ExecSecurity.Allowlist,
            ask: ExecAsk.Off,
            allowlistResolutions: [res],
            allowlistMatches: [entry]);
        var result = Assert.IsType<ExecHostPolicyDecision.AllowOutcome>(
            ExecApprovalEvaluator.Evaluate(ctx, null));
        Assert.False(result.ApprovedByAsk);
    }

    [Fact]
    public void Step5_Allow_ApprovedByAskTrue_WhenUserApprovedOnce()
    {
        var res = Res("/usr/bin/rg", "/usr/bin/rg");
        var ctx = MakeContext(
            security: ExecSecurity.Allowlist,
            ask: ExecAsk.OnMiss,
            allowlistResolutions: [res],
            allowlistMatches: []);
        var result = Assert.IsType<ExecHostPolicyDecision.AllowOutcome>(
            ExecApprovalEvaluator.Evaluate(ctx, ExecApprovalDecision.AllowOnce));
        Assert.True(result.ApprovedByAsk);
    }

    [Fact]
    public void Step5_Allow_ApprovedByAskTrue_WhenUserApprovedAlways()
    {
        var res = Res("/usr/bin/rg", "/usr/bin/rg");
        var ctx = MakeContext(
            security: ExecSecurity.Allowlist,
            ask: ExecAsk.OnMiss,
            allowlistResolutions: [res],
            allowlistMatches: []);
        var result = Assert.IsType<ExecHostPolicyDecision.AllowOutcome>(
            ExecApprovalEvaluator.Evaluate(ctx, ExecApprovalDecision.AllowAlways));
        Assert.True(result.ApprovedByAsk);
    }

    // ── Evaluator — two-pass mechanics ───────────────────────────────────────

    [Fact]
    public void TwoPass_FirstRequiresPrompt_SecondAllows()
    {
        var res = Res("/usr/bin/rg", "/usr/bin/rg");
        var ctx = MakeContext(
            security: ExecSecurity.Allowlist,
            ask: ExecAsk.OnMiss,
            allowlistResolutions: [res],
            allowlistMatches: []);

        // Pass 1: no decision → prompt required
        Assert.IsType<ExecHostPolicyDecision.RequiresPromptOutcome>(
            ExecApprovalEvaluator.Evaluate(ctx, null));

        // Pass 2: user approved → allow (context reused unchanged)
        var result = Assert.IsType<ExecHostPolicyDecision.AllowOutcome>(
            ExecApprovalEvaluator.Evaluate(ctx, ExecApprovalDecision.AllowOnce));
        Assert.True(result.ApprovedByAsk);
    }

    [Fact]
    public void TwoPass_FirstRequiresPrompt_SecondDenies()
    {
        var res = Res("/usr/bin/rg", "/usr/bin/rg");
        var ctx = MakeContext(
            security: ExecSecurity.Allowlist,
            ask: ExecAsk.OnMiss,
            allowlistResolutions: [res],
            allowlistMatches: []);

        Assert.IsType<ExecHostPolicyDecision.RequiresPromptOutcome>(
            ExecApprovalEvaluator.Evaluate(ctx, null));

        AssertDeny(
            ExecApprovalEvaluator.Evaluate(ctx, ExecApprovalDecision.Deny),
            ExecApprovalV2Code.UserDenied);
    }

    // ── RequiresAsk helper ────────────────────────────────────────────────────

    [Fact]
    public void RequiresAsk_Always_ReturnsTrue()
        => Assert.True(ExecApprovalEvaluator.RequiresAsk(ExecAsk.Always, ExecSecurity.Full, null, false));

    [Fact]
    public void RequiresAsk_Always_ReturnsTrueEvenWithAllowlistMatch()
    {
        var entry = Entry(@"C:\tools\rg.exe");
        Assert.True(ExecApprovalEvaluator.RequiresAsk(ExecAsk.Always, ExecSecurity.Allowlist, entry, false));
    }

    [Fact]
    public void RequiresAsk_Off_ReturnsFalse()
        => Assert.False(ExecApprovalEvaluator.RequiresAsk(ExecAsk.Off, ExecSecurity.Allowlist, null, false));

    [Fact]
    public void RequiresAsk_OnMiss_NoMatch_SecurityAllowlist_ReturnsFalse_WhenSkillAllow()
        => Assert.False(ExecApprovalEvaluator.RequiresAsk(ExecAsk.OnMiss, ExecSecurity.Allowlist, null, skillAllow: true));

    [Fact]
    public void RequiresAsk_OnMiss_WithMatch_ReturnsFalse()
    {
        var entry = Entry(@"C:\tools\rg.exe");
        Assert.False(ExecApprovalEvaluator.RequiresAsk(ExecAsk.OnMiss, ExecSecurity.Allowlist, entry, false));
    }

    [Fact]
    public void RequiresAsk_OnMiss_SecurityFull_ReturnsFalse()
        // security=full means allowlist is not in play; onMiss condition requires security=allowlist.
        => Assert.False(ExecApprovalEvaluator.RequiresAsk(ExecAsk.OnMiss, ExecSecurity.Full, null, false));

    [Fact]
    public void RequiresAsk_OnMiss_NoMatch_SecurityAllowlist_ReturnsTrue()
        => Assert.True(ExecApprovalEvaluator.RequiresAsk(ExecAsk.OnMiss, ExecSecurity.Allowlist, null, false));

    // ── ExecApprovalEvaluation — derived fields ───────────────────────────────

    [Fact]
    public void Evaluation_Resolution_IsFirstAllowlistResolution()
    {
        var r1 = Res("a", @"C:\a.exe");
        var r2 = Res("b", @"C:\b.exe");
        var ctx = MakeContext(allowlistResolutions: [r1, r2]);
        Assert.Equal(r1, ctx.Resolution);
    }

    [Fact]
    public void Evaluation_Resolution_IsNull_WhenNoAllowlistResolutions()
    {
        var ctx = MakeContext(allowlistResolutions: []);
        Assert.Null(ctx.Resolution);
    }

    [Fact]
    public void Evaluation_AllowlistSatisfied_TrueWhenAllMatch()
    {
        var r1 = Res("a", @"C:\a.exe");
        var e1 = Entry(@"C:\a.exe");
        var ctx = MakeContext(
            security: ExecSecurity.Allowlist,
            allowlistResolutions: [r1],
            allowlistMatches: [e1]);
        Assert.True(ctx.AllowlistSatisfied);
        Assert.Equal(e1, ctx.AllowlistMatch);
    }

    [Fact]
    public void Evaluation_AllowlistSatisfied_FalseWhenCountMismatch()
    {
        var r1 = Res("a", @"C:\a.exe");
        var r2 = Res("b", @"C:\b.exe");
        var e1 = Entry(@"C:\a.exe");
        var ctx = MakeContext(
            security: ExecSecurity.Allowlist,
            allowlistResolutions: [r1, r2],
            allowlistMatches: [e1]); // only one of two matched
        Assert.False(ctx.AllowlistSatisfied);
        Assert.Null(ctx.AllowlistMatch);
    }

    [Fact]
    public void Evaluation_AllowlistSatisfied_FalseWhenSecurityIsNotAllowlist()
    {
        var r1 = Res("a", @"C:\a.exe");
        var e1 = Entry(@"C:\a.exe");
        var ctx = MakeContext(
            security: ExecSecurity.Full,
            allowlistResolutions: [r1],
            allowlistMatches: [e1]);
        Assert.False(ctx.AllowlistSatisfied);
        Assert.Null(ctx.AllowlistMatch);
    }

    [Fact]
    public void Evaluation_AllowlistSatisfied_FalseWhenNoResolutions()
    {
        var ctx = MakeContext(
            security: ExecSecurity.Allowlist,
            allowlistResolutions: [],
            allowlistMatches: []);
        Assert.False(ctx.AllowlistSatisfied);
    }

    [Fact]
    public void Evaluation_SkillAllow_IsAlwaysFalseInV1()
    {
        var ctx = MakeContext(skillAllow: false);
        Assert.False(ctx.SkillAllow);
    }

    // ── ExecAllowlistMatcher ──────────────────────────────────────────────────

    [Fact]
    public void Matcher_UsesResolvedPath_WhenAvailable()
    {
        var entries = new[] { Entry(@"C:\tools\rg.exe") };
        // resolvedPath matches; rawExecutable would not
        var res = Res("rg", @"C:\tools\rg.exe");
        Assert.NotNull(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_FallsBackToRawExecutable_WhenResolvedPathIsNull()
    {
        var entries = new[] { Entry(@"C:\tools\rg.exe") };
        var res = Res(@"C:\tools\rg.exe", null); // no resolvedPath
        Assert.NotNull(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_NoMatch_ReturnsNull()
    {
        var entries = new[] { Entry(@"C:\tools\rg.exe") };
        var res = Res("curl", @"C:\tools\curl.exe");
        Assert.Null(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_CaseInsensitive()
    {
        var entries = new[] { Entry(@"C:\TOOLS\RG.EXE") };
        var res = Res("rg", @"C:\tools\rg.exe");
        Assert.NotNull(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_BackslashNormalization()
    {
        // Pattern uses forward slashes; resolvedPath uses backslashes — must still match.
        var entries = new[] { Entry(@"C:/tools/rg.exe") };
        var res = Res("rg", @"C:\tools\rg.exe");
        Assert.NotNull(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_Star_MatchesSingleSegment()
    {
        var entries = new[] { Entry(@"C:\tools\*") };
        var res = Res("rg", @"C:\tools\rg.exe");
        Assert.NotNull(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_Star_DoesNotCrossSeparator()
    {
        var entries = new[] { Entry(@"C:\*") };
        var res = Res("rg", @"C:\tools\rg.exe"); // two levels below root
        Assert.Null(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_DoubleStar_CrossesSeparators()
    {
        var entries = new[] { Entry(@"C:\**\rg.exe") };
        var res = Res("rg", @"C:\tools\bin\rg.exe"); // two intermediate segments
        Assert.NotNull(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_QuestionMark_MatchesSingleCharNoSeparator()
    {
        var entries = new[] { Entry(@"C:\tools\rg.ex?") };
        var res = Res("rg", @"C:\tools\rg.exe");
        Assert.NotNull(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_QuestionMark_DoesNotCrossSeparator()
    {
        // ? cannot substitute a slash
        var entries = new[] { Entry(@"C:\tools?rg.exe") };
        var res = Res("rg", @"C:\tools\rg.exe");
        Assert.Null(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_BasenameOnlyPattern_IsInvalidAndNeverMatches()
    {
        var entries = new[] { Entry("rg") };
        var res = Res("rg", @"C:\tools\rg.exe");
        Assert.Null(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_NullPattern_IsInvalid()
    {
        var entries = new[] { new ExecAllowlistEntry { Pattern = null } };
        var res = Res("rg", @"C:\tools\rg.exe");
        Assert.Null(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_EmptyPattern_IsInvalid()
    {
        var entries = new[] { new ExecAllowlistEntry { Pattern = "" } };
        var res = Res("rg", @"C:\tools\rg.exe");
        Assert.Null(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void MatchAll_AllMatch_ReturnsOrderedList()
    {
        var e1 = Entry(@"C:\tools\rg.exe");
        var e2 = Entry(@"C:\tools\curl.exe");
        var entries = new[] { e1, e2 };
        var resolutions = new[]
        {
            Res("rg",   @"C:\tools\rg.exe"),
            Res("curl", @"C:\tools\curl.exe"),
        };
        var result = ExecAllowlistMatcher.MatchAll(entries, resolutions);
        Assert.Equal(2, result.Count);
        Assert.Equal(e1, result[0]);
        Assert.Equal(e2, result[1]);
    }

    [Fact]
    public void MatchAll_OneMiss_ReturnsEmpty()
    {
        var e1 = Entry(@"C:\tools\rg.exe");
        var entries = new[] { e1 };
        var resolutions = new[]
        {
            Res("rg",   @"C:\tools\rg.exe"),
            Res("curl", @"C:\tools\curl.exe"), // no matching entry
        };
        Assert.Empty(ExecAllowlistMatcher.MatchAll(entries, resolutions));
    }

    [Fact]
    public void MatchAll_EmptyResolutions_ReturnsEmpty()
        => Assert.Empty(ExecAllowlistMatcher.MatchAll([], []));

    [Fact]
    public void Matcher_UncPath_MatchesWithNormalization()
    {
        // UNC paths normalize to //server/share/... after \ → / substitution.
        var entries = new[] { Entry(@"\\server\share\tools\rg.exe") };
        var res = Res("rg", @"\\server\share\tools\rg.exe");
        Assert.NotNull(ExecAllowlistMatcher.Match(entries, res));
    }

    // ── IsValidPattern ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\tools\rg.exe", true)]
    [InlineData(@"C:/tools/rg.exe", true)]
    [InlineData(@"C:\tools\*",      true)]
    [InlineData(@"/usr/bin/rg",     true)]
    [InlineData("rg",               false)] // basename only
    [InlineData("",                 false)]
    [InlineData("   ",              false)]
    public void IsValidPattern_Correct(string pattern, bool expected)
        => Assert.Equal(expected, ExecAllowlistMatcher.IsValidPattern(pattern));

    // ── Evaluator — missing precedence edge cases ─────────────────────────────

    [Fact]
    public void Step1_SecurityDeny_BeatsApprovalDecisionDeny()
    {
        // security=deny must produce SecurityDeny even when the user also sent Deny.
        // Step 1 wins over step 2 — the code must not reach step 2.
        var ctx = MakeContext(security: ExecSecurity.Deny);
        AssertDeny(ExecApprovalEvaluator.Evaluate(ctx, ExecApprovalDecision.Deny), ExecApprovalV2Code.SecurityDeny);
    }

    [Fact]
    public void Step3_DoesNotFire_WhenApprovalDecisionIsPresent_AskAlways()
    {
        // ask=always + approvalDecision present → step 3 is skipped; result is Allow at step 5.
        // This verifies the "approvalDecision is null" guard in step 3.
        var ctx = MakeContext(security: ExecSecurity.Full, ask: ExecAsk.Always);
        var result = Assert.IsType<ExecHostPolicyDecision.AllowOutcome>(
            ExecApprovalEvaluator.Evaluate(ctx, ExecApprovalDecision.AllowOnce));
        Assert.True(result.ApprovedByAsk);
    }

    [Fact]
    public void Step4_Bypassed_WhenApprovedByAsk()
    {
        // security=allowlist, ask=off, no match, but approvalDecision is present.
        // Step 4 checks !approvedByAsk, so the allowlist miss does not fire.
        // This is the correct behavior for the second pass when security=allowlist and ask=off.
        var res = Res("/usr/bin/rg", "/usr/bin/rg");
        var ctx = MakeContext(
            security: ExecSecurity.Allowlist,
            ask: ExecAsk.Off,
            allowlistResolutions: [res],
            allowlistMatches: []);
        var result = Assert.IsType<ExecHostPolicyDecision.AllowOutcome>(
            ExecApprovalEvaluator.Evaluate(ctx, ExecApprovalDecision.AllowOnce));
        Assert.True(result.ApprovedByAsk);
    }

    [Fact]
    public void Step4_Bypassed_WhenSkillAllow()
    {
        // security=allowlist, no match, skillAllow=true → step 4 skipped → allow.
        var res = Res("/usr/bin/rg", "/usr/bin/rg");
        var ctx = MakeContext(
            security: ExecSecurity.Allowlist,
            ask: ExecAsk.Off,
            allowlistResolutions: [res],
            allowlistMatches: [],
            skillAllow: true);
        var result = Assert.IsType<ExecHostPolicyDecision.AllowOutcome>(
            ExecApprovalEvaluator.Evaluate(ctx, null));
        Assert.False(result.ApprovedByAsk);
    }

    [Fact]
    public void ApprovalDecisionAllow_ProducesApprovedByAskTrue()
    {
        // The basic Allow enum value (not AllowOnce/AllowAlways) must also set approvedByAsk=true.
        var ctx = MakeContext(security: ExecSecurity.Full, ask: ExecAsk.Always);
        var result = Assert.IsType<ExecHostPolicyDecision.AllowOutcome>(
            ExecApprovalEvaluator.Evaluate(ctx, ExecApprovalDecision.Allow));
        Assert.True(result.ApprovedByAsk);
    }

    [Fact]
    public void DenyReasons_AreSemanticallyDistinct()
    {
        // The three deny reasons carry different codes for downstream distinguishability.
        // Research doc 06: "la distinción semántica importa para módulos posteriores".
        var ctxSecDeny = MakeContext(security: ExecSecurity.Deny);
        var ctxUserDeny = MakeContext(security: ExecSecurity.Full);
        var res = Res("/usr/bin/rg", "/usr/bin/rg");
        var ctxAllowlistMiss = MakeContext(
            security: ExecSecurity.Allowlist,
            ask: ExecAsk.Off,
            allowlistResolutions: [res],
            allowlistMatches: []);

        var d1 = (ExecHostPolicyDecision.DenyOutcome)ExecApprovalEvaluator.Evaluate(ctxSecDeny, null);
        var d2 = (ExecHostPolicyDecision.DenyOutcome)ExecApprovalEvaluator.Evaluate(ctxUserDeny, ExecApprovalDecision.Deny);
        var d3 = (ExecHostPolicyDecision.DenyOutcome)ExecApprovalEvaluator.Evaluate(ctxAllowlistMiss, null);

        Assert.Equal(ExecApprovalV2Code.SecurityDeny, d1.Error.Code);
        Assert.Equal(ExecApprovalV2Code.UserDenied, d2.Error.Code);
        Assert.Equal(ExecApprovalV2Code.AllowlistMiss, d3.Error.Code);

        // All three reasons must be distinct strings.
        Assert.NotEqual(d1.Error.Reason, d2.Error.Reason);
        Assert.NotEqual(d2.Error.Reason, d3.Error.Reason);
        Assert.NotEqual(d1.Error.Reason, d3.Error.Reason);
    }

    // ── Matcher — additional edge cases ──────────────────────────────────────

    [Fact]
    public void Matcher_ResolvedPath_TakesPrecedenceOverRawExecutable()
    {
        // resolvedPath does not match the pattern; rawExecutable would match it.
        // The matcher must use resolvedPath and ignore rawExecutable → no match.
        var entries = new[] { Entry(@"C:\raw\rg.exe") };
        var res = Res(@"C:\raw\rg.exe", @"C:\resolved\rg.exe"); // resolvedPath is different
        Assert.Null(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Match_ReturnsFirstEntry_WhenMultipleEntriesMatch()
    {
        var e1 = Entry(@"C:\tools\*");    // matches first
        var e2 = Entry(@"C:\tools\rg.*"); // also matches, but e1 comes first
        var entries = new[] { e1, e2 };
        var res = Res("rg", @"C:\tools\rg.exe");
        Assert.Equal(e1, ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void MatchAll_EmptyEntries_WithResolutions_ReturnsEmpty()
    {
        var resolutions = new[] { Res("rg", @"C:\tools\rg.exe") };
        Assert.Empty(ExecAllowlistMatcher.MatchAll([], resolutions));
    }

    // ── Evaluator — full precedence pairs ────────────────────────────────────

    [Fact]
    public void Evaluate_Denies_WithSecurityCode_EvenWhen_AskIsAlways()
    {
        // security=deny is checked at step 1; RequiresAsk fires at step 3.
        // Step 1 must win — the evaluator must never reach step 3 when security=deny.
        var ctx = MakeContext(security: ExecSecurity.Deny, ask: ExecAsk.Always);
        AssertDeny(ExecApprovalEvaluator.Evaluate(ctx, null), ExecApprovalV2Code.SecurityDeny);
    }

    [Fact]
    public void Evaluate_Denies_WithSecurityCode_EvenWhen_AllowlistWouldSatisfy()
    {
        // An allowlist match must never override security=deny.
        var res = Res("rg", @"C:\tools\rg.exe");
        var entry = Entry(@"C:\tools\*");
        var ctx = MakeContext(
            security: ExecSecurity.Deny,
            allowlistResolutions: [res],
            allowlistMatches: [entry]);
        AssertDeny(ExecApprovalEvaluator.Evaluate(ctx, null), ExecApprovalV2Code.SecurityDeny);
    }

    [Fact]
    public void Evaluate_Denies_WithUserDeniedCode_EvenWhen_PromptWouldBeRequired()
    {
        // ask=always makes RequiresAsk true (step 3), but approvalDecision=Deny fires at step 2.
        // Step 2 must win — result is UserDenied, not RequiresPrompt.
        var ctx = MakeContext(security: ExecSecurity.Full, ask: ExecAsk.Always);
        AssertDeny(ExecApprovalEvaluator.Evaluate(ctx, ExecApprovalDecision.Deny), ExecApprovalV2Code.UserDenied);
    }

    // ── Evaluator — "no resolutions" vs "resolutions with no matches" ─────────

    [Fact]
    public void Evaluate_Denies_WithAllowlistMiss_When_NoResolutionsExist()
    {
        // Empty AllowlistResolutions → AllowlistSatisfied=false → AllowlistMiss deny.
        var ctx = MakeContext(
            security: ExecSecurity.Allowlist,
            ask: ExecAsk.Off,
            allowlistResolutions: [],
            allowlistMatches: []);
        AssertDeny(ExecApprovalEvaluator.Evaluate(ctx, null), ExecApprovalV2Code.AllowlistMiss);
    }

    [Fact]
    public void Evaluate_Denies_WithAllowlistMiss_When_ResolutionsExistButNoneMatch()
    {
        // Resolutions present but no matching entries → same AllowlistMiss outcome.
        // Intentional: the two fail-closed cases must be semantically equivalent.
        var res = Res("rg", @"C:\tools\rg.exe");
        var ctx = MakeContext(
            security: ExecSecurity.Allowlist,
            ask: ExecAsk.Off,
            allowlistResolutions: [res],
            allowlistMatches: []);
        AssertDeny(ExecApprovalEvaluator.Evaluate(ctx, null), ExecApprovalV2Code.AllowlistMiss);
    }

    // ── RequiresAsk — full enum coverage ─────────────────────────────────────

    [Fact]
    public void RequiresAsk_ReturnsFalse_WhenAskIsDeny()
        // ExecAsk.Deny means "never ask"; it is not Ask.Always or Ask.OnMiss.
        => Assert.False(ExecApprovalEvaluator.RequiresAsk(ExecAsk.Deny, ExecSecurity.Allowlist, null, false));

    // ── Evaluator — determinism ───────────────────────────────────────────────

    [Fact]
    public void Evaluate_IsDeterministic_SameInputsAlwaysProduceSameOutput()
    {
        var res = Res("/usr/bin/rg", "/usr/bin/rg");
        var ctx = MakeContext(
            security: ExecSecurity.Allowlist,
            ask: ExecAsk.OnMiss,
            allowlistResolutions: [res],
            allowlistMatches: []);

        // Three identical calls; each must return RequiresPrompt.
        for (var i = 0; i < 3; i++)
            Assert.IsType<ExecHostPolicyDecision.RequiresPromptOutcome>(
                ExecApprovalEvaluator.Evaluate(ctx, null));
    }

    // ── Matcher — case sensitivity (both directions) ──────────────────────────

    [Fact]
    public void Matcher_Matches_WhenPatternUpperCase_AndTarget_LowerCase()
    {
        var entries = new[] { Entry(@"C:\TOOLS\GIT.EXE") };
        var res = Res("git", @"c:\tools\git.exe");
        Assert.NotNull(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_Matches_WhenPatternMixedCase_AndTarget_MixedCase()
    {
        var entries = new[] { Entry(@"C:\Tools\Git.exe") };
        var res = Res("git", @"C:\TOOLS\GIT.EXE");
        Assert.NotNull(ExecAllowlistMatcher.Match(entries, res));
    }

    // ── Matcher — separator edge cases ───────────────────────────────────────

    [Fact]
    public void Matcher_TrailingSlashInPattern_DoesNotMatchFile()
    {
        // C:\tools\ is a directory pattern — must not match C:\tools\rg.exe.
        var entries = new[] { Entry(@"C:\tools\") };
        var res = Res("rg", @"C:\tools\rg.exe");
        Assert.Null(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_DoubleBackslash_DoesNotMatchSingleBackslashPath()
    {
        // @"C:\\tools\\rg.exe" contains literal double backslashes.
        // After \ → / normalization those become //, which does not match the single-separator target.
        // This is intentional fail-closed: a malformed pattern must not produce an accidental allow.
        var entries = new[] { Entry(@"C:\\tools\\rg.exe") };
        var res = Res("rg", @"C:\tools\rg.exe");
        Assert.Null(ExecAllowlistMatcher.Match(entries, res));
    }

    // ── Matcher — glob edge cases ─────────────────────────────────────────────

    [Fact]
    public void Matcher_DoubleStar_AtStart_MatchesAnyDepth()
    {
        // **/rg.exe should match rg.exe at any directory depth.
        var entries = new[] { Entry(@"**/rg.exe") };
        var res = Res("rg", @"C:\tools\bin\rg.exe");
        Assert.NotNull(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_DoubleStar_AtStart_DoesNotMatchPartialBasename()
    {
        // **/cmd.exe must not match a path whose basename merely ends with cmd.exe.
        var entries = new[] { Entry(@"**/cmd.exe") };
        Assert.Null(ExecAllowlistMatcher.Match(entries, Res("notcmd", @"C:\Windows\notcmd.exe")));
    }

    [Fact]
    public void Matcher_DoubleStar_AtStart_MatchesRootLevel()
    {
        // **/rg.exe must also match rg.exe at root level (no leading directory).
        var entries = new[] { Entry(@"**/rg.exe") };
        Assert.NotNull(ExecAllowlistMatcher.Match(entries, Res("rg", @"rg.exe")));
    }

    [Fact]
    public void Matcher_PathWithSpaces_Matches()
    {
        // Paths like C:\Program Files\git.exe are common on Windows.
        var entries = new[] { Entry(@"C:\Program Files\git.exe") };
        var res = Res("git", @"C:\Program Files\git.exe");
        Assert.NotNull(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_StarGlob_MatchesFilenameWithExtension()
    {
        // C:\tools\*.exe must match files ending in .exe in that directory only.
        var entries = new[] { Entry(@"C:\tools\*.exe") };
        Assert.NotNull(ExecAllowlistMatcher.Match(entries, Res("rg", @"C:\tools\rg.exe")));
        Assert.Null(ExecAllowlistMatcher.Match(entries, Res("rg", @"C:\tools\bin\rg.exe"))); // deeper
    }

    // ── Matcher — fail-closed for unusual input ───────────────────────────────

    [Fact]
    public void Matcher_WhitespaceOnlyPattern_DoesNotMatch()
    {
        var entries = new[] { new ExecAllowlistEntry { Pattern = "   " } };
        var res = Res("rg", @"C:\tools\rg.exe");
        Assert.Null(ExecAllowlistMatcher.Match(entries, res));
    }

    // ── Matcher — pathological and adversarial patterns ───────────────────────

    [Fact]
    public void Matcher_PathologicalGlobPattern_CompletesWithoutHanging()
    {
        // **/**/**/**/* produces a degenerate regex without NonBacktracking.
        // With NonBacktracking the engine runs in linear time regardless of pattern shape.
        var entries = new[] { Entry(@"**/**/**/**/*") };
        var res = Res("rg", @"C:\tools\deep\nested\dir\rg.exe");
        // **/**/**/**/* matches any path — result is non-null.
        Assert.NotNull(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_DotDot_InPattern_DoesNotTraverseDirectory()
    {
        // .. in a pattern is treated as literal characters after \ → / normalization,
        // not as path traversal. It must not allow a pattern to reach outside its intended scope.
        var entries = new[] { Entry(@"C:\allowed\..\secret\rg.exe") };
        var res = Res("rg", @"C:\secret\rg.exe");
        Assert.Null(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_TrailingDot_InPattern_DoesNotMatchWithoutDot()
    {
        // Windows can strip trailing dots in some API calls, but the matcher must not.
        // A pattern with a trailing dot is distinct from one without it.
        var entries = new[] { Entry(@"C:\tools\rg.exe.") };
        var res = Res("rg", @"C:\tools\rg.exe");
        Assert.Null(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_DoubleStarTrailingSlashOnly_DoesNotMatchEverything()
    {
        // **/ alone must not generate ^.*$ (match-everything). The trailing / is only consumed
        // when more pattern content follows; otherwise the pattern stays as a literal directory
        // reference that no resolved executable path will satisfy.
        var entries = new[] { Entry(@"**/") };
        var res = Res("rg", @"C:\tools\rg.exe");
        Assert.Null(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_RegexCache_ProducesSameResult_OnRepeatedCalls()
    {
        // The cached regex must produce identical results to a fresh compilation.
        var entries = new[] { Entry(@"C:\tools\*") };
        var res = Res("rg", @"C:\tools\rg.exe");
        var first = ExecAllowlistMatcher.Match(entries, res);
        var second = ExecAllowlistMatcher.Match(entries, res);
        var third = ExecAllowlistMatcher.Match(entries, res);
        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }

    // ── Evaluator — purity ────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_DoesNotMutate_Context()
    {
        // Evaluate() must be a pure function: the context must be identical before and after.
        var res = Res("/usr/bin/rg", "/usr/bin/rg");
        var entry = Entry(@"/usr/bin/*");
        var ctx = MakeContext(
            security: ExecSecurity.Allowlist,
            ask: ExecAsk.Off,
            allowlistResolutions: [res],
            allowlistMatches: [entry]);

        var securityBefore = ctx.Security;
        var askBefore = ctx.Ask;
        var satisfiedBefore = ctx.AllowlistSatisfied;
        var matchBefore = ctx.AllowlistMatch;

        ExecApprovalEvaluator.Evaluate(ctx, null);

        Assert.Equal(securityBefore, ctx.Security);
        Assert.Equal(askBefore, ctx.Ask);
        Assert.Equal(satisfiedBefore, ctx.AllowlistSatisfied);
        Assert.Equal(matchBefore, ctx.AllowlistMatch);
    }

    // ── ExecAsk.Deny — step 2.5 (blocking fix from review) ───────────────────

    [Fact]
    public void AskDeny_SecurityFull_NoDecision_IsDenied()
    {
        // security=Full, ask=Deny, approvalDecision=null => AskDeny (policy bypass reported by Shanselman).
        var ctx = MakeContext(security: ExecSecurity.Full, ask: ExecAsk.Deny);
        AssertDeny(ExecApprovalEvaluator.Evaluate(ctx, null), ExecApprovalV2Code.AskDeny);
    }

    [Fact]
    public void AskDeny_AllowlistSatisfied_NoDecision_IsAllowed()
    {
        // security=Allowlist, ask=Deny, allowlist satisfied, approvalDecision=null => Allow.
        // The allowlist pre-authorized the tool without any prompt; AskFallback is irrelevant
        // because no ask would have been issued anyway. Step 4.5 guards on !AllowlistSatisfied.
        var res = Res("rg", @"C:\tools\rg.exe");
        var entry = Entry(@"C:\tools\*");
        var ctx = MakeContext(
            security: ExecSecurity.Allowlist,
            ask: ExecAsk.Deny,
            allowlistResolutions: [res],
            allowlistMatches: [entry]);
        var result = Assert.IsType<ExecHostPolicyDecision.AllowOutcome>(
            ExecApprovalEvaluator.Evaluate(ctx, null));
        Assert.False(result.ApprovedByAsk);
    }

    [Fact]
    public void AskDeny_SecurityDenyWins_OverAskDeny()
    {
        // security=Deny at step 1 must still win even when ask=Deny.
        var ctx = MakeContext(security: ExecSecurity.Deny, ask: ExecAsk.Deny);
        AssertDeny(ExecApprovalEvaluator.Evaluate(ctx, null), ExecApprovalV2Code.SecurityDeny);
    }

    [Fact]
    public void AskDeny_WithApprovalDecision_DoesNotFire()
    {
        // Step 2.5 only fires when approvalDecision is null.
        // An explicit approval decision bypasses ask=deny.
        var ctx = MakeContext(security: ExecSecurity.Full, ask: ExecAsk.Deny);
        var result = Assert.IsType<ExecHostPolicyDecision.AllowOutcome>(
            ExecApprovalEvaluator.Evaluate(ctx, ExecApprovalDecision.AllowOnce));
        Assert.True(result.ApprovedByAsk);
    }

    [Fact]
    public void AskDeny_UserDeniedWins_OverAskDeny()
    {
        // approvalDecision=Deny at step 2 must win before step 2.5 (ask=Deny) fires.
        var ctx = MakeContext(security: ExecSecurity.Full, ask: ExecAsk.Deny);
        AssertDeny(ExecApprovalEvaluator.Evaluate(ctx, ExecApprovalDecision.Deny), ExecApprovalV2Code.UserDenied);
    }

    [Fact]
    public void AskDeny_AllowlistNotSatisfied_ProducesAllowlistMiss_NotAskDeny()
    {
        // security=Allowlist, ask=Deny, no match, no decision.
        // Step 4 fires (allowlist miss) before step 4.5 (ask=deny) is reached.
        // The allowlist miss is the precise failure reason; AskDeny does not override it.
        var res = Res("rg", @"C:\tools\rg.exe");
        var ctx = MakeContext(
            security: ExecSecurity.Allowlist,
            ask: ExecAsk.Deny,
            allowlistResolutions: [res],
            allowlistMatches: []);
        AssertDeny(ExecApprovalEvaluator.Evaluate(ctx, null), ExecApprovalV2Code.AllowlistMiss);
    }

    // ── Matcher — malformed ** patterns (non-blocking hardening from review) ───

    [Fact]
    public void Matcher_MalformedDoubleStar_TrailingAfterLiteral_DoesNotMatch()
    {
        // C:\tools** normalizes to C:/tools** — ** not at segment boundary.
        // Must fail-closed: the pattern is invalid and no match is produced.
        var entries = new[] { Entry(@"C:\tools**") };
        var res = Res("rg", @"C:\tools\rg.exe");
        Assert.Null(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_MalformedDoubleStar_PrefixBeforeLiteral_DoesNotMatch()
    {
        // **tools/rg.exe — ** at start but followed by 't' (not '/').
        var entries = new[] { Entry(@"**tools/rg.exe") };
        var res = Res("rg", @"C:\tools\rg.exe");
        Assert.Null(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_MalformedDoubleStar_EmbeddedInSegment_DoesNotMatch()
    {
        // C:\too**ls\rg.exe — ** inside a segment name.
        var entries = new[] { Entry(@"C:\too**ls\rg.exe") };
        var res = Res("rg", @"C:\tools\rg.exe");
        Assert.Null(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void IsValidPattern_MalformedDoubleStar_ReturnsFalse()
    {
        Assert.False(ExecAllowlistMatcher.IsValidPattern(@"C:\tools**"));
        Assert.False(ExecAllowlistMatcher.IsValidPattern(@"**tools/rg.exe"));
    }

    [Fact]
    public void IsValidPattern_WellFormedDoubleStar_ReturnsTrue()
    {
        // Segment-boundary ** patterns must remain valid.
        Assert.True(ExecAllowlistMatcher.IsValidPattern(@"C:\**\rg.exe"));
        Assert.True(ExecAllowlistMatcher.IsValidPattern(@"**/rg.exe"));
        Assert.True(ExecAllowlistMatcher.IsValidPattern(@"C:\tools\**"));
    }

    [Fact]
    public void Matcher_TwoConsecutiveDoubleStars_SeparatedBySlash_IsValid()
    {
        // **/** — each ** is at a segment boundary; the pattern is valid and matches any path.
        var entries = new[] { Entry(@"**/**") };
        var res = Res("rg", @"C:\tools\rg.exe");
        Assert.NotNull(ExecAllowlistMatcher.Match(entries, res));
    }

    [Fact]
    public void Matcher_MalformedDoubleStar_InMiddleOfSegment_WithValidDoubleStarElsewhere_IsInvalid()
    {
        // C:\tools\**\bin** — the first ** is valid but the second is malformed (no trailing separator).
        // The whole pattern must be rejected fail-closed.
        var entries = new[] { Entry(@"C:\tools\**\bin**") };
        var res = Res("rg", @"C:\tools\bin\rg.exe");
        Assert.Null(ExecAllowlistMatcher.Match(entries, res));
    }
}
