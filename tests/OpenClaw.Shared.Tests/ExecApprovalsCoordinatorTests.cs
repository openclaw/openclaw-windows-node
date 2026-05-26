using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Tests for PR7: ExecApprovalsCoordinator full pipeline.
/// Covers rail 8 (observability), rail 10 (UI-free), rail 17 (concurrency),
/// rail 19 (production wiring inert), env injection guard, and log injection prevention.
/// </summary>
public class ExecApprovalsCoordinatorTests : IDisposable
{
    private readonly string _dir;

    public ExecApprovalsCoordinatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"oca-coord-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ["cmd","/c","echo","hello"] reliably resolves cmd.exe on Windows via WellKnownPaths.
    // Shell wrapper form: singular resolution succeeds; allowlistResolutions=[] (echo is a builtin).
    private static NodeInvokeRequest Req(string argsJson)
        => new() { Id = "r1", Command = "system.run", Args = Parse(argsJson) };

    private static NodeInvokeRequest DefaultReq()
        => Req("""{"command":["cmd","/c","echo","hello"]}""");

    private void WriteStoreFile(string json)
        => File.WriteAllText(Path.Combine(_dir, "exec-approvals.json"), json);

    private ExecApprovalsCoordinator MakeCoordinator(
        ICanPresentEvaluator? canPresent = null,
        IExecApprovalV2PromptHandler? prompt = null,
        IOpenClawLogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        return new(
            new ExecApprovalsStore(_dir, log),
            canPresent ?? AlwaysCannotPresentEvaluator.Instance,
            prompt ?? ExecApprovalV2NullPromptHandler.Instance,
            log);
    }

    // ── 1. No file → SecurityDeny (default-deny on first activation) ──────────

    [Fact]
    public async Task NoFile_ReturnsSecurityDeny()
    {
        var result = await MakeCoordinator().HandleAsync(DefaultReq(), "c1");
        Assert.Equal(ExecApprovalV2Code.SecurityDeny, result.Code);
    }

    // ── 2. security=full → Allow ──────────────────────────────────────────────

    [Fact]
    public async Task SecurityFull_AskOff_ReturnsAllow()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"off"}}""");
        var result = await MakeCoordinator().HandleAsync(DefaultReq(), "c2");
        Assert.True(result.IsAllow);
    }

    // ── 3. security=deny → SecurityDeny ──────────────────────────────────────

    [Fact]
    public async Task SecurityDeny_ReturnsSecurityDeny()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"deny"}}""");
        var result = await MakeCoordinator().HandleAsync(DefaultReq(), "c3");
        Assert.Equal(ExecApprovalV2Code.SecurityDeny, result.Code);
    }

    // ── 4. ask=always, canPresent=false, askFallback=deny → UserDenied ────────

    [Fact]
    public async Task AskAlways_CannotPresent_FallbackDeny_ReturnsUserDenied()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"always","askFallback":"deny"}}""");
        var result = await MakeCoordinator().HandleAsync(DefaultReq(), "c4");
        // FallbackDecision(ExecAsk.Deny) → ExecApprovalDecision.Deny → pass2 step2 → UserDenied
        Assert.Equal(ExecApprovalV2Code.UserDenied, result.Code);
        Assert.Equal("user-denied", result.Reason);
    }

    // ── 5. ask=always, canPresent=false, askFallback=off → Allow ─────────────

    [Fact]
    public async Task AskAlways_CannotPresent_FallbackOff_ReturnsAllow()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"always","askFallback":"off"}}""");
        var log = new CapturingLogger();
        var result = await MakeCoordinator(logger: log).HandleAsync(DefaultReq(), "c5");
        Assert.True(result.IsAllow);
        Assert.NotNull(log.LastInfo);
        Assert.Contains("fallbackUsed=True", log.LastInfo, StringComparison.Ordinal);
    }

    // ── 6. canPresent=true, NullPromptHandler → UserDenied ───────────────────

    [Fact]
    public async Task CanPresent_NullPrompt_ReturnsUserDenied()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"always"}}""");
        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: ExecApprovalV2NullPromptHandler.Instance).HandleAsync(DefaultReq(), "c6");
        Assert.Equal(ExecApprovalV2Code.UserDenied, result.Code);
    }

    // ── 7. canPresent=true, AllowOnce → Allow ────────────────────────────────

    [Fact]
    public async Task CanPresent_AllowOnce_ReturnsAllow()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"always"}}""");
        var log = new CapturingLogger();
        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.AllowOnce),
            logger: log).HandleAsync(DefaultReq(), "c7");
        Assert.True(result.IsAllow);
        Assert.Contains("promptAttempted=True", log.LastInfo!, StringComparison.Ordinal);
        Assert.DoesNotContain("fallbackUsed=True", log.LastInfo!, StringComparison.Ordinal);
    }

    // ── 8. canPresent=true, AllowAlways → Allow ───────────────────────────────

    [Fact]
    public async Task CanPresent_AllowAlways_ReturnsAllow()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"always"}}""");
        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.AllowAlways))
            .HandleAsync(DefaultReq(), "c8");
        Assert.True(result.IsAllow);
    }

    // ── 9. Invariant: prompt returns Allow → InternalError ────────────────────

    [Fact]
    public async Task PromptReturnsAllowPlain_ReturnsInternalError()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"always"}}""");
        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.Allow))
            .HandleAsync(DefaultReq(), "c9");
        Assert.Equal(ExecApprovalV2Code.InternalError, result.Code);
        Assert.Equal("prompt-returned-allow", result.Reason);
    }

    // ── 10. Prompt throws → UserDenied, no fallback ───────────────────────────

    [Fact]
    public async Task PromptThrows_ReturnsUserDenied_FallbackNotUsed()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"always"}}""");
        var log = new CapturingLogger();
        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new ThrowingPromptHandler(),
            logger: log).HandleAsync(DefaultReq(), "c10");
        Assert.Equal(ExecApprovalV2Code.UserDenied, result.Code);
        Assert.Equal("prompt-failed", result.Reason);
        // Must not delegate to fallback after presenter failure
        Assert.Contains("fallbackUsed=False", log.LastWarn!, StringComparison.Ordinal);
    }

    // ── 11. Input invalid → ValidationFailed ─────────────────────────────────

    [Fact]
    public async Task InvalidInput_ReturnsValidationFailed()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full"}}""");
        var result = await MakeCoordinator().HandleAsync(
            Req("""{}"""), "c11");
        Assert.Equal(ExecApprovalV2Code.ValidationFailed, result.Code);
    }

    // ── 12. security=allowlist, allowlist empty, ask=off → AllowlistMiss ──────

    [Fact]
    public async Task SecurityAllowlist_EmptyList_ReturnsAllowlistMiss()
    {
        // ["cmd","/c","echo","hello"] → shell wrapper → allowlistResolutions=[] → AllowlistSatisfied=false
        WriteStoreFile("""{"version":1,"defaults":{"security":"allowlist","ask":"off"}}""");
        var result = await MakeCoordinator().HandleAsync(DefaultReq(), "c12");
        Assert.Equal(ExecApprovalV2Code.AllowlistMiss, result.Code);
    }

    // ── 13. FallbackDecision(ask=Always) → Deny, not AllowOnce ───────────────

    [Fact]
    public async Task FallbackDecision_AskFallbackAlways_ReturnsDeny()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"always","askFallback":"always"}}""");
        var result = await MakeCoordinator().HandleAsync(DefaultReq(), "c13");
        // ExecAsk.Always → ExecApprovalDecision.Deny → pass2 → UserDenied (fail-safe)
        Assert.False(result.IsAllow);
        Assert.NotEqual(ExecApprovalV2Code.Allow, result.Code);
    }

    // ── 14. Rail 8 — 7 log fields present ────────────────────────────────────

    [Fact]
    public async Task Rail8_AllSevenLogFieldsPresent()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"deny"}}""");
        var log = new CapturingLogger();
        await MakeCoordinator(logger: log).HandleAsync(DefaultReq(), "corr-14");

        // security=deny → LogAndReturn → Warn; check all 7 rail-8 fields
        Assert.NotNull(log.LastWarn);
        var msg = log.LastWarn!;
        Assert.Contains("corr-14", msg, StringComparison.Ordinal);
        Assert.Contains("path=new", msg, StringComparison.Ordinal);
        Assert.Contains("canonical=", msg, StringComparison.Ordinal);
        Assert.Contains("decision=deny", msg, StringComparison.Ordinal);
        Assert.Contains("reason=", msg, StringComparison.Ordinal);
        Assert.Contains("fallbackUsed=", msg, StringComparison.Ordinal);
        Assert.Contains("promptAttempted=", msg, StringComparison.Ordinal);
    }

    // ── 15. Coordinator not wired in production src ───────────────────────────

    [Fact]
    public void ProductionWiring_CoordinatorNotReferencedInSrc()
    {
        var violations = ProductionSourceFiles.All
            .Where(f => !f.Path.EndsWith("ExecApprovalsCoordinator.cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.Text.Contains("ExecApprovalsCoordinator", StringComparison.Ordinal))
            .Select(f => f.Path)
            .ToList();
        Assert.Empty(violations);
    }

    // ── 16. Rail 10 — coordinator in OpenClaw.Shared, not Tray ───────────────

    [Fact]
    public void Rail10_CoordinatorAssemblyIsOpenClawShared()
    {
        var asm = typeof(ExecApprovalsCoordinator).Assembly.GetName().Name;
        Assert.Equal("OpenClaw.Shared", asm);
    }

    // ── 17. Concurrency — 5 simultaneous requests don't corrupt state ─────────

    [Fact]
    public async Task Concurrency_FiveConcurrentRequests_AllReturnValidResults()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"off"}}""");
        var coordinator = MakeCoordinator();
        var tasks = Enumerable.Range(0, 5)
            .Select(i => coordinator.HandleAsync(DefaultReq(), $"conc-{i}"))
            .ToList();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.NotNull(r));
        Assert.All(results, r => Assert.True(r.IsAllow));
    }

    // ── 18. Env injection → ValidationFailed("env-blocked") ──────────────────

    [Fact]
    public async Task EnvInjection_BlockedEnvVar_ReturnsValidationFailed()
    {
        // security=full,ask=off rules out other denies; env PATH is always blocked
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"off"}}""");
        var log = new CapturingLogger();
        var result = await MakeCoordinator(logger: log)
            .HandleAsync(Req("""{"command":["cmd","/c","echo","hello"],"env":{"PATH":"C:\\evil"}}"""), "c18");

        Assert.Equal(ExecApprovalV2Code.ValidationFailed, result.Code);
        Assert.Equal("env-blocked", result.Reason);
        // Separate Warn with blocked names (emitted before LogAndReturn)
        Assert.Contains(log.Warns, w =>
            w.Contains("env-blocked", StringComparison.Ordinal) &&
            w.Contains("PATH", StringComparison.Ordinal));
    }

    // ── 19. Log injection — DisplayCommand control chars replaced in log ───────

    [Fact]
    public async Task LogInjection_ControlCharsInCommand_SanitizedInLog()
    {
        // \r\n in JSON string → actual CR+LF in the parsed command argument
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"off"}}""");
        var log = new CapturingLogger();
        await MakeCoordinator(logger: log)
            .HandleAsync(Req("""{"command":["cmd","/c","x\r\n[EXEC-APPROVALS] [fake] FAKE"]}"""), "c19");

        // Should allow (security=full, ask=off)
        Assert.NotNull(log.LastInfo);
        // CR+LF must not appear literally in the log line
        Assert.DoesNotContain("\r\n", log.LastInfo!, StringComparison.Ordinal);
    }

    // ── 20. Lock released after prompt throws — second call must not deadlock ────

    [Fact]
    public async Task PromptThrows_LockReleasedForSubsequentCall()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"always"}}""");
        var coordinator = MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new ThrowingPromptHandler());

        var first = await coordinator.HandleAsync(DefaultReq(), "lock-1");
        Assert.Equal(ExecApprovalV2Code.UserDenied, first.Code);

        // Second call must complete — if lock was not released this would deadlock
        var second = await coordinator.HandleAsync(DefaultReq(), "lock-2");
        Assert.Equal(ExecApprovalV2Code.UserDenied, second.Code);
    }

    // ── 21a. Concurrency with actual lock contention ───────────────────────────

    [Fact]
    public async Task Concurrency_PromptPathWithLockContention_AllReturnValidResults()
    {
        // ask=always + canPresent=true → all requests enter the locked block
        // NullPromptHandler returns Deny → all should be UserDenied (no deadlock, no corruption)
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"always"}}""");
        var coordinator = MakeCoordinator(canPresent: AlwaysCanPresentEvaluator.Instance);
        var tasks = Enumerable.Range(0, 5)
            .Select(i => coordinator.HandleAsync(DefaultReq(), $"cont-{i}"))
            .ToList();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.NotNull(r));
        // NullPromptHandler returns Deny → UserDenied for all
        Assert.All(results, r => Assert.Equal(ExecApprovalV2Code.UserDenied, r.Code));
    }

    // ── 22a. ExecApprovalV2Result — new codes constructible (InternalError, Allow) ──

    [Fact]
    public void V2Result_InternalError_CodeAndReason()
    {
        var r = ExecApprovalV2Result.InternalError("invariant-violation");
        Assert.Equal(ExecApprovalV2Code.InternalError, r.Code);
        Assert.Equal("invariant-violation", r.Reason);
        Assert.False(r.IsAllow);
    }

    [Fact]
    public void V2Result_Allow_IsAllowTrueAndReasonApproved()
    {
        var r = ExecApprovalV2Result.Allow();
        Assert.Equal(ExecApprovalV2Code.Allow, r.Code);
        Assert.Equal("approved", r.Reason);
        Assert.True(r.IsAllow);
    }

    [Fact]
    public void V2Result_IsAllow_FalseForAllDenyCodes()
    {
        Assert.False(ExecApprovalV2Result.SecurityDeny("x").IsAllow);
        Assert.False(ExecApprovalV2Result.UserDenied("x").IsAllow);
        Assert.False(ExecApprovalV2Result.ValidationFailed("x").IsAllow);
        Assert.False(ExecApprovalV2Result.InternalError("x").IsAllow);
    }

    // ── 21. ICanPresentEvaluator stubs ────────────────────────────────────────

    [Fact]
    public void AlwaysCannotPresent_AlwaysReturnsFalse()
    {
        Assert.False(AlwaysCannotPresentEvaluator.Instance.CanPresent(null));
        Assert.False(AlwaysCannotPresentEvaluator.Instance.CanPresent("session-key"));
    }

    [Fact]
    public void AlwaysCanPresent_AlwaysReturnsTrue()
    {
        Assert.True(AlwaysCanPresentEvaluator.Instance.CanPresent(null));
        Assert.True(AlwaysCanPresentEvaluator.Instance.CanPresent("session-key"));
    }

    // ── 22. Empty correlationId → auto-generated 32-char hex ─────────────────

    [Fact]
    public async Task EmptyCorrelationId_AutoGeneratedInLog()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"deny"}}""");
        var log = new CapturingLogger();
        await MakeCoordinator(logger: log).HandleAsync(DefaultReq(), "");

        Assert.NotNull(log.LastWarn);
        // log format: "[EXEC-APPROVALS] [<correlationId>] path=new ..."
        // auto-generated correlationId: Guid.NewGuid().ToString("N") → 32 hex chars
        var msg = log.LastWarn!;
        var second = msg.IndexOf('[', msg.IndexOf(']') + 1) + 1;
        var end = msg.IndexOf(']', second);
        Assert.True(end > second);
        var id = msg[second..end];
        Assert.Equal(32, id.Length);
        Assert.True(id.All(c => char.IsAsciiHexDigit(c)), $"Expected 32 hex chars, got: {id}");
    }

    // ── 23. FallbackDecision(OnMiss, AllowlistSatisfied=false) → Deny ─────────

    [Fact]
    public async Task FallbackDecision_AskFallbackOnMiss_NotSatisfied_ReturnsDeny()
    {
        // security=full, ask=always → RequiresPrompt in pass1
        // canPresent=false → FallbackDecision(context, ExecAsk.OnMiss)
        // AllowlistSatisfied=false (security=Full, not Allowlist) → Deny
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"always","askFallback":"on-miss"}}""");
        var result = await MakeCoordinator().HandleAsync(DefaultReq(), "c23");
        Assert.False(result.IsAllow);
    }

    // ── 24. Outer safety net — CanPresent throws → InternalError, not exception ───

    [Fact]
    public async Task CanPresent_Throws_ReturnsInternalError_NotException()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"always"}}""");
        var log = new CapturingLogger();
        var result = await MakeCoordinator(
            canPresent: new ThrowingCanPresentEvaluator(),
            logger: log).HandleAsync(DefaultReq(), "outer-1");

        Assert.Equal(ExecApprovalV2Code.InternalError, result.Code);
        Assert.Equal("unexpected-exception", result.Reason);
        Assert.Contains(log.Errors, e => e.Contains("unexpected-exception"));
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class FixedDecisionPromptHandler : IExecApprovalV2PromptHandler
    {
        private readonly ExecApprovalPromptOutcome _outcome;
        public FixedDecisionPromptHandler(ExecApprovalPromptOutcome o) => _outcome = o;
        public Task<ExecApprovalPromptOutcome> PromptAsync(
            ExecApprovalV2PromptRequest _,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_outcome);
    }

    private sealed class ThrowingCanPresentEvaluator : ICanPresentEvaluator
    {
        public bool CanPresent(string? requestSessionKey)
            => throw new InvalidOperationException("simulated canPresent crash");
    }

    private sealed class ThrowingPromptHandler : IExecApprovalV2PromptHandler
    {
        public Task<ExecApprovalPromptOutcome> PromptAsync(
            ExecApprovalV2PromptRequest _,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated presenter crash");
    }

    private sealed class CapturingLogger : IOpenClawLogger
    {
        public List<string> Infos { get; } = [];
        public List<string> Warns { get; } = [];
        public List<string> Errors { get; } = [];
        public string? LastInfo => Infos.Count > 0 ? Infos[^1] : null;
        public string? LastWarn => Warns.Count > 0 ? Warns[^1] : null;
        public string? LastError => Errors.Count > 0 ? Errors[^1] : null;
        public void Info(string m) => Infos.Add(m);
        public void Debug(string m) { }
        public void Warn(string m) => Warns.Add(m);
        public void Error(string m, Exception? _ = null) => Errors.Add(m);
    }

}
