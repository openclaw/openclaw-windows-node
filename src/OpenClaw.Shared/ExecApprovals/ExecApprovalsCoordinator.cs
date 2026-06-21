using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;

namespace OpenClaw.Shared.ExecApprovals;

// Full coordinator pipeline: validate → normalize → buildContext → evaluate(pass1) →
// prompt/fallback → evaluate(pass2) → side effects → final decision.
// UI-free: no WinUI types. A SemaphoreSlim serializes the prompt+pass2 block.
// Not wired in production src — verified by ProductionWiring_CoordinatorNotReferencedInSrc test.
// Must be registered as singleton when wired: the SemaphoreSlim is per-instance.
public sealed class ExecApprovalsCoordinator : IExecApprovalV2Handler
{
    private readonly ExecApprovalsStore _store;
    private readonly ICanPresentEvaluator _canPresent;
    private readonly IExecApprovalV2PromptHandler _prompt;
    private readonly IOpenClawLogger _logger;

    // Serializes the prompt call + second-pass block.
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

        try
        {
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
        var needsAllowlistMatches = resolved.Defaults.Security == ExecSecurity.Allowlist
            || resolved.Defaults.AskFallback == ExecSecurity.Allowlist;
        IReadOnlyList<ExecAllowlistEntry> matches = needsAllowlistMatches
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

        // Step 4: first pass (approvalDecision always null — pass2 decides based on user response)
        var pass1 = ExecApprovalEvaluator.Evaluate(context, null);
        if (pass1 is ExecHostPolicyDecision.DenyOutcome denyPass1)
            return LogAndReturn(denyPass1.Error, correlationId,
                promptAttempted: false, fallbackUsed: false, canonical: context.DisplayCommand);
        if (pass1 is ExecHostPolicyDecision.AllowOutcome)
        {
            // Pre-approved path (security=Full, ask=Off or allowlist satisfied): skip prompt.
            // Fail closed if the approved executable cannot be pinned to a resolved path.
            var preApprovedExecution = BuildApprovedExecution(identity, sanitizedEnv);
            if (preApprovedExecution is null)
                return LogAndReturn(ExecApprovalV2Result.InternalError("unresolved-executable-on-allow"),
                    correlationId, promptAttempted: false, fallbackUsed: false, canonical: context.DisplayCommand);

            // Side effects are best-effort: a metadata write failure must not flip an allow to a deny.
            try { await RecordAllowlistUsageAsync(context).ConfigureAwait(false); }
            catch (Exception ex) { _logger.Warn($"[EXEC-APPROVALS] [{correlationId}] side-effect: record-usage failed (non-fatal): {ex.Message}"); }
            _logger.Info($"[EXEC-APPROVALS] [{correlationId}] path=new " +
                $"canonical=\"{SanitizeForLog(context.DisplayCommand)}\" decision=allow " +
                $"reason=approved fallbackUsed=false promptAttempted=false");
            return ExecApprovalV2Result.Allow(preApprovedExecution);
        }
        // RequiresPromptOutcome → continue to prompt/fallback block

        // Steps 5-8: prompt/fallback + second pass (critical section) + side effect flag
        bool promptAttempted = false;
        bool fallbackUsed = false;
        bool persistAllowlistEntry = false;

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

                // Allow is unreachable here — handled by the check above. The fallback arm
                // fails closed for invalid enum values that can still be cast at runtime.
                followupDecision = promptResult switch
                {
                    ExecApprovalPromptOutcome.Deny => ExecApprovalDecision.Deny,
                    ExecApprovalPromptOutcome.AllowOnce => ExecApprovalDecision.AllowOnce,
                    ExecApprovalPromptOutcome.AllowAlways => ExecApprovalDecision.AllowAlways,
                    ExecApprovalPromptOutcome.Allow => throw new UnreachableException("prompt-returned-allow handled above"),
                    _ => throw new UnreachableException($"unknown prompt outcome: {promptResult}"),
                };
            }
            else
            {
                fallbackUsed = true;
                followupDecision = FallbackDecision(context, resolved.Defaults.AskFallback);
            }

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
            // pass2 is AllowOutcome — record whether AllowAlways was the prompt decision.
            persistAllowlistEntry = followupDecision == ExecApprovalDecision.AllowAlways;
        }
        finally
        {
            _promptLock.Release();
        }

        // Step 8: side effects — strictly after the final allow decision.
        // Each side effect is independently best-effort so a failure in one does not skip the other.
        if (persistAllowlistEntry && context.Security == ExecSecurity.Allowlist)
        {
            try { await PersistAllowlistEntriesAsync(context).ConfigureAwait(false); }
            catch (Exception ex) { _logger.Warn($"[EXEC-APPROVALS] [{correlationId}] side-effect: persist-entry failed (non-fatal): {ex.Message}"); }
        }
        try { await RecordAllowlistUsageAsync(context).ConfigureAwait(false); }
        catch (Exception ex) { _logger.Warn($"[EXEC-APPROVALS] [{correlationId}] side-effect: record-usage failed (non-fatal): {ex.Message}"); }

        // Step 9: build payload from the resolved path; fail closed if unresolved.
        var execution = BuildApprovedExecution(identity, sanitizedEnv);
        if (execution is null)
            return LogAndReturn(ExecApprovalV2Result.InternalError("unresolved-executable-on-allow"),
                correlationId, promptAttempted, fallbackUsed, canonical: context.DisplayCommand);

        // Step 9b: final allow log
        _logger.Info($"[EXEC-APPROVALS] [{correlationId}] path=new " +
            $"canonical=\"{SanitizeForLog(context.DisplayCommand)}\" decision=allow " +
            $"reason=approved fallbackUsed={fallbackUsed} promptAttempted={promptAttempted}");

        // Step 10: return Allow
        return ExecApprovalV2Result.Allow(execution);
        }
        catch (Exception ex)
        {
            // Outer safety net: any unhandled exception in buildContext, CanPresent, FallbackDecision,
            // or an out-of-range prompt outcome produces a typed deny instead of escaping HandleAsync.
            // Failures must never be silent or untyped.
            var msg = $"[EXEC-APPROVALS] [{correlationId}] path=new " +
                $"canonical=\"\" decision=deny reason=unexpected-exception " +
                $"fallbackUsed=false promptAttempted=false";
            _logger.Error(msg, ex);
            return ExecApprovalV2Result.InternalError("unexpected-exception");
        }
    }

    // Builds the approved execution payload from the RESOLVED executable path, never
    // the raw argv[0]. The command must execute with the same canonical identity it
    // was evaluated under: a relative argv[0] in the payload would let Windows
    // re-resolve it against PATH/cwd at execution time (a hijack), and the
    // direct-argv runner rejects non-absolute executables anyway. Returns null when
    // the executable could not be resolved to a path — the caller fails closed
    // rather than execute a command whose identity we cannot pin.
    internal static ExecApprovedExecution? BuildApprovedExecution(
        CanonicalCommandIdentity identity,
        IReadOnlyDictionary<string, string>? sanitizedEnv)
    {
        var resolvedPath = identity.Resolution?.ResolvedPath;
        if (string.IsNullOrEmpty(resolvedPath))
            return null;

        // argv[0] = resolved absolute path; remaining args preserved verbatim.
        var argv = new string[identity.Command.Count];
        argv[0] = resolvedPath;
        for (var i = 1; i < identity.Command.Count; i++)
            argv[i] = identity.Command[i];

        return new ExecApprovedExecution(argv, identity.Cwd, identity.TimeoutMs, sanitizedEnv);
    }

    // Persists allowAlways patterns after an AllowAlways prompt decision (non-empty only).
    // Caller guarantees Security == Allowlist (guard is in HandleAsync step 8).
    private async Task PersistAllowlistEntriesAsync(ExecApprovalEvaluation context)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in context.AllowAlwaysPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern) || !seen.Add(pattern)) continue;
            await _store.AddAllowlistEntryAsync(context.AgentId, pattern).ConfigureAwait(false);
        }
    }

    // Updates lastUsed* metadata for every matched allowlist entry after a final allow.
    // Guard mirrors macOS recordAllowlistMatches: no-op unless security=allowlist and satisfied.
    private async Task RecordAllowlistUsageAsync(ExecApprovalEvaluation context)
    {
        if (context.Security != ExecSecurity.Allowlist || !context.AllowlistSatisfied) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < context.AllowlistMatches.Count; i++)
        {
            var pattern = context.AllowlistMatches[i].Pattern;
            if (string.IsNullOrEmpty(pattern) || !seen.Add(pattern)) continue;
            var resolvedPath = i < context.AllowlistResolutions.Count
                ? context.AllowlistResolutions[i].ResolvedPath
                : null;
            await _store.RecordAllowlistUseAsync(
                context.AgentId, pattern, resolvedPath)
                .ConfigureAwait(false);
        }
    }

    // Fail-safe defaults when no UI is available (Saltzer/Schroeder fail-safe defaults, OWASP ASVS 4.1.4).
    // ask=Always → Deny: human approval is a precondition; without UI the only safe outcome is deny.
    private static ExecApprovalDecision FallbackDecision(
        ExecApprovalEvaluation context,
        ExecSecurity askFallback)
    {
        var effectiveFallback = (ExecSecurity)Math.Min((int)context.Security, (int)askFallback);
        return effectiveFallback switch
        {
            ExecSecurity.Full => ExecApprovalDecision.AllowOnce,
            ExecSecurity.Allowlist => context.AllAllowlistResolutionsMatched
                ? ExecApprovalDecision.AllowOnce
                : ExecApprovalDecision.Deny,
            ExecSecurity.Deny => ExecApprovalDecision.Deny,
            _ => ExecApprovalDecision.Deny,  // defensive
        };
    }

    private static ExecApprovalV2PromptRequest BuildPromptRequest(
        ExecApprovalEvaluation context,
        CanonicalCommandIdentity identity,
        string correlationId)
        => new()
        {
            DisplayCommand = context.DisplayCommand,  // NOT sanitized — presenter's responsibility
            Cwd = identity.Cwd,
            Security = context.Security,
            Ask = context.Ask,
            AgentId = context.AgentId ?? "main",
            ResolvedPath = context.Resolution?.ResolvedPath,
            SessionKey = identity.SessionKey,
            CorrelationId = correlationId,
            // Host omitted (no gateway wiring yet)
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
