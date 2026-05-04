using System.Collections.Generic;

namespace OpenClaw.Shared.ExecApprovals;

// Either a CanonicalCommandIdentity (IsResolved=true) or a typed denial (IsResolved=false).
// Produced by ExecApprovalV2Normalizer; consumed by the coordinator pipeline (PR7).
public sealed class ExecApprovalV2NormalizationOutcome
{
    public bool IsResolved { get; }
    public CanonicalCommandIdentity? Identity { get; }
    public ExecApprovalV2Result? Error { get; }

    private ExecApprovalV2NormalizationOutcome(CanonicalCommandIdentity identity)
    {
        IsResolved = true;
        Identity = identity;
    }

    private ExecApprovalV2NormalizationOutcome(ExecApprovalV2Result error)
    {
        IsResolved = false;
        Error = error;
    }

    public static ExecApprovalV2NormalizationOutcome Ok(CanonicalCommandIdentity identity)
        => new(identity);

    public static ExecApprovalV2NormalizationOutcome Fail(ExecApprovalV2Result error)
        => new(error);
}

// Rail 18 steps 2-4: normalize command form → resolve executable → build canonical identity.
// Stateless — safe to call concurrently.
public static class ExecApprovalV2Normalizer
{
    public static ExecApprovalV2NormalizationOutcome Normalize(ValidatedRunRequest request)
    {
        var argv = request.Argv;
        var cwd = request.Cwd;
        var env = request.Env as IReadOnlyDictionary<string, string>;

        // displayCommand is always derived from argv, never from rawCommand (research doc 05 decision 2).
        var displayCommand = ShellQuoting.FormatExecCommand(argv);

        // rawCommand is null in Windows v1 (system.run does not carry it; research doc 05 OQ-V4).
        // EvaluationRawCommand stays null — correct and documented conservative output.
        string? evaluationRawCommand = null;

        // Singular resolution for state machine.
        var resolution = ExecCommandResolver.Resolve(argv, cwd, env);

        // Multi-segment resolution for allowlist.
        // Empty list is fail-closed: no allowlist satisfaction possible (research doc 04 R2).
        // An empty list is NOT itself a denial at this step — the evaluator decides.
        var allowlistResolutions = ExecCommandResolver.ResolveForAllowlist(
            argv, evaluationRawCommand, cwd, env);

        // UX patterns for prompting.
        var allowAlwaysPatterns = ExecCommandResolver.ResolveAllowAlwaysPatterns(argv, cwd, env);

        // Rail 6: if argv is non-empty but resolution is entirely impossible, deny.
        // "Ambiguous or inconsistent" → typed deny, not silent allow.
        if (resolution is null && allowlistResolutions.Count == 0)
            return Fail("executable-resolution-failed");

        var identity = new CanonicalCommandIdentity(
            argv,
            displayCommand,
            evaluationRawCommand,
            resolution,
            allowlistResolutions,
            allowAlwaysPatterns,
            cwd,
            request.TimeoutMs,
            env,
            request.AgentId,
            request.SessionKey);

        return ExecApprovalV2NormalizationOutcome.Ok(identity);
    }

    private static ExecApprovalV2NormalizationOutcome Fail(string reason)
        => ExecApprovalV2NormalizationOutcome.Fail(
            ExecApprovalV2Result.ResolutionFailed(reason));
}
