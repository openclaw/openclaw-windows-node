using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;

namespace OpenClaw.Shared.ExecApprovals;

// Full coordinator pipeline: validate → normalize → buildContext → evaluate(pass1) →
// prompt/fallback → [persistAllowlistEntry stub] → evaluate(pass2) → final decision.
// Rail 10: no WinUI types. Rail 17: SemaphoreSlim serializes the prompt+pass2 block.
// Rail 19: not wired in production src in PR7 — verified by test 15.
// Must be registered as singleton when wired (PR8+): the SemaphoreSlim is per-instance.
public sealed class ExecApprovalsCoordinator : IExecApprovalV2Handler
{
    private readonly ExecApprovalsStore _store;
    private readonly ICanPresentEvaluator _canPresent;
    private readonly IExecApprovalV2PromptHandler _prompt;
    private readonly IOpenClawLogger _logger;

    // Serializes the prompt call + second-pass block (rail 17).
    // Does NOT protect validate/normalize/buildContext — those are stateless.
    private readonly SemaphoreSlim _promptLock = new(1, 1);

    public ExecApprovalsCoordinator(
        ExecApprovalsStore store,
        ICanPresentEvaluator canPresentEvaluator,
        IExecApprovalV2PromptHandler promptHandler,
        IOpenClawLogger logger)
    {
        _store = store;
        _canPresent = canPresentEvaluator;
        _prompt = promptHandler;
        _logger = logger;
    }

    public async Task<ExecApprovalV2Result> HandleAsync(NodeInvokeRequest request, string correlationId)
    {
        if (string.IsNullOrEmpty(correlationId))
            correlationId = Guid.NewGuid().ToString("N");

        // Step 1: validate
        var validation = ExecApprovalV2InputValidator.Validate(request);
        if (!validation.IsValid)
            return LogAndReturn(validation.Error!, correlationId,
                promptAttempted: false, fallbackUsed: false);

        // Step 2: normalize (unwrap shell wrappers, resolve executables, build canonical identity)
        var norm = ExecApprovalV2Normalizer.Normalize(validation.Request!);
        if (!norm.IsResolved)
            return LogAndReturn(norm.Error!, correlationId,
                promptAttempted: false, fallbackUsed: false);
        var identity = norm.Identity!;

        // Step 3: buildContext
        var resolved = _store.ResolveReadOnly(identity.AgentId);

        // Env injection guard — preserves SystemCapability.HandleRunAsync:343-351 behavior.
        // identity.Env is IReadOnlyDictionary; copy to Dictionary for Sanitize.
        var envInput = identity.Env is null
            ? null
            : new Dictionary<string, string>(identity.Env, StringComparer.OrdinalIgnoreCase);
        var envResult = ExecEnvSanitizer.Sanitize(envInput);

        if (envResult.Blocked.Length > 0)
        {
            var blockedNames = (string[])envResult.Blocked.Clone();
            Array.Sort(blockedNames, StringComparer.OrdinalIgnoreCase);
            _logger.Warn($"[EXEC-APPROVALS] [{correlationId}] env-blocked: [{string.Join(", ", blockedNames)}]");
            return LogAndReturn(ExecApprovalV2Result.ValidationFailed("env-blocked"),
                correlationId, promptAttempted: false, fallbackUsed: false);
        }

        var sanitizedEnv = envResult.Allowed as IReadOnlyDictionary<string, string>;
        IReadOnlyList<ExecAllowlistEntry> matches = resolved.Defaults.Security == ExecSecurity.Allowlist
            ? ExecAllowlistMatcher.MatchAll(resolved.Allowlist, identity.AllowlistResolutions)
            : [];

        var context = new ExecApprovalEvaluation(
            identity.Command,
            identity.DisplayCommand,
            identity.AgentId,
            resolved.Defaults.Security,
            resolved.Defaults.Ask,
            sanitizedEnv,
            identity.AllowlistResolutions,
            identity.AllowAlwaysPatterns,
            matches);

        // Step 4: first pass (approvalDecision always null in PR7 — CVE #8682, ADR-0002 Phase 2)
        var pass1 = ExecApprovalEvaluator.Evaluate(context, null);
        if (pass1 is ExecHostPolicyDecision.DenyOutcome denyPass1)
            return LogAndReturn(denyPass1.Error, correlationId,
                promptAttempted: false, fallbackUsed: false, canonical: context.DisplayCommand);
        if (pass1 is ExecHostPolicyDecision.AllowOutcome)
        {
            // Pre-approved path (security=Full, ask=Off or allowlist satisfied): skip prompt
            _logger.Info($"[EXEC-APPROVALS] [{correlationId}] path=new " +
                $"canonical=\"{SanitizeForLog(context.DisplayCommand)}\" decision=allow " +
                $"reason=approved fallbackUsed=false promptAttempted=false");
            return ExecApprovalV2Result.Allow();
        }
        // RequiresPromptOutcome → continue to prompt/fallback block

        // Steps 5-7: prompt/fallback + second pass (critical section)
        bool promptAttempted = false;
        bool fallbackUsed = false;

        await _promptLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ExecApprovalDecision followupDecision;

            if (_canPresent.CanPresent(identity.SessionKey))
            {
                promptAttempted = true;
                ExecApprovalPromptOutcome promptResult;
                try
                {
                    promptResult = await _prompt.PromptAsync(
                        BuildPromptRequest(context, identity, correlationId),
                        cancellationToken: default).ConfigureAwait(false);
                }
                catch
                {
                    // Presenter failure → fail-closed, no fallback delegation
                    return LogAndReturn(ExecApprovalV2Result.UserDenied("prompt-failed"),
                        correlationId, promptAttempted: true, fallbackUsed: false,
                        canonical: context.DisplayCommand);
                }

                // Allow (plain) from a prompt handler is an invariant violation —
                // only AllowOnce and AllowAlways are semantically valid from UI.
                if (promptResult == ExecApprovalPromptOutcome.Allow)
                {
                    _logger.Error($"[EXEC-APPROVALS] [{correlationId}] invariant: " +
                        "prompt returned Allow — treating as invariant violation deny");
                    return LogAndReturn(ExecApprovalV2Result.InternalError("prompt-returned-allow"),
                        correlationId, promptAttempted: true, fallbackUsed: false,
                        canonical: context.DisplayCommand);
                }

                // Exhaustive mapping without _ so the compiler warns if ExecApprovalPromptOutcome
                // gains a new value. Allow is unreachable here — handled by the check above.
                followupDecision = promptResult switch
                {
                    ExecApprovalPromptOutcome.Deny => ExecApprovalDecision.Deny,
                    ExecApprovalPromptOutcome.AllowOnce => ExecApprovalDecision.AllowOnce,
                    ExecApprovalPromptOutcome.AllowAlways => ExecApprovalDecision.AllowAlways,
                    ExecApprovalPromptOutcome.Allow => throw new UnreachableException("prompt-returned-allow handled above"),
                };
            }
            else
            {
                fallbackUsed = true;
                followupDecision = FallbackDecision(context, resolved.Defaults.AskFallback);
            }

            // Step 6: AddAllowlistEntry stub (PR9 implements for AllowAlways + security==Allowlist)

            // Step 7: second pass — must never return RequiresPrompt
            var pass2 = ExecApprovalEvaluator.Evaluate(context, followupDecision);
            if (pass2 is ExecHostPolicyDecision.DenyOutcome denyPass2)
                return LogAndReturn(denyPass2.Error, correlationId, promptAttempted, fallbackUsed,
                    canonical: context.DisplayCommand);
            if (pass2 is ExecHostPolicyDecision.RequiresPromptOutcome)
            {
                _logger.Error($"[EXEC-APPROVALS] [{correlationId}] invariant: " +
                    "second pass returned RequiresPrompt");
                return LogAndReturn(ExecApprovalV2Result.InternalError("second-pass-requires-prompt"),
                    correlationId, promptAttempted, fallbackUsed, canonical: context.DisplayCommand);
            }
            // AllowOutcome → fall through to steps 8-10
        }
        finally
        {
            _promptLock.Release();
        }

        // Step 8: RecordAllowlistUse stub (PR9)

        // Step 9: final allow log
        _logger.Info($"[EXEC-APPROVALS] [{correlationId}] path=new " +
            $"canonical=\"{SanitizeForLog(context.DisplayCommand)}\" decision=allow " +
            $"reason=approved fallbackUsed={fallbackUsed} promptAttempted={promptAttempted}");

        // Step 10: return Allow
        return ExecApprovalV2Result.Allow();
    }

    // Fail-safe defaults when no UI is available (Saltzer/Schroeder fail-safe defaults, OWASP ASVS 4.1.4).
    // ask=Always → Deny: human approval is a precondition; without UI the only safe outcome is deny.
    private static ExecApprovalDecision FallbackDecision(
        ExecApprovalEvaluation context,
        ExecAsk askFallback)
    {
        return askFallback switch
        {
            ExecAsk.Off => ExecApprovalDecision.AllowOnce,
            ExecAsk.OnMiss => context.AllowlistSatisfied
                ? ExecApprovalDecision.AllowOnce
                : ExecApprovalDecision.Deny,
            ExecAsk.Always => ExecApprovalDecision.Deny,
            ExecAsk.Deny => ExecApprovalDecision.Deny,
            _ => ExecApprovalDecision.Deny,  // defensive
        };
    }

    private static ExecApprovalV2PromptRequest BuildPromptRequest(
        ExecApprovalEvaluation context,
        CanonicalCommandIdentity identity,
        string correlationId)
        => new()
        {
            DisplayCommand = context.DisplayCommand,  // NOT sanitized — presenter's responsibility (rail 11)
            Cwd = identity.Cwd,
            Security = context.Security,
            Ask = context.Ask,
            AgentId = context.AgentId ?? "main",
            ResolvedPath = context.Resolution?.ResolvedPath,
            SessionKey = identity.SessionKey,
            CorrelationId = correlationId,
            // Host omitted in PR7 (no gateway wiring yet)
        };

    // Anti log-injection: replaces control characters in DisplayCommand before writing to logs.
    // Truncates to 200 chars — sufficient for triage, bounded for disk-bound logs.
    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        Span<char> buffer = stackalloc char[Math.Min(value.Length, 200)];
        var count = 0;
        foreach (var ch in value)
        {
            if (count == buffer.Length) break;
            buffer[count++] = char.IsControl(ch) ? ' ' : ch;
        }
        var sanitized = new string(buffer[..count]);
        return value.Length > count ? sanitized + "..." : sanitized;
    }

    private ExecApprovalV2Result LogAndReturn(
        ExecApprovalV2Result result,
        string correlationId,
        bool promptAttempted,
        bool fallbackUsed,
        string? canonical = null)
    {
        var safeCanonical = SanitizeForLog(canonical);
        var msg = $"[EXEC-APPROVALS] [{correlationId}] path=new " +
            $"canonical=\"{safeCanonical}\" decision=deny reason={result.Reason} " +
            $"fallbackUsed={fallbackUsed} promptAttempted={promptAttempted}";
        if (result.Code == ExecApprovalV2Code.InternalError)
            _logger.Error(msg);
        else
            _logger.Warn(msg);
        return result;
    }
}
