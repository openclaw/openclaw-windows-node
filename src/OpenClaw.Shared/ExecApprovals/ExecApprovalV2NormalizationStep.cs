using System.Collections.Generic;

namespace OpenClaw.Shared.ExecApprovals;

// Either a CanonicalCommandIdentity (IsResolved=true) or a typed denial (IsResolved=false).
// Produced by ExecApprovalV2Normalizer; consumed by the coordinator pipeline.
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

// Steps 2-4 of the approval pipeline: normalize command form → resolve executable → build canonical identity.
// Stateless — safe to call concurrently.
public static class ExecApprovalV2Normalizer
{
    public static ExecApprovalV2NormalizationOutcome Normalize(ValidatedRunRequest request)
    {
        var argv = request.Argv;
        var cwd = request.Cwd;
        var env = request.Env as IReadOnlyDictionary<string, string>;

        // displayCommand is always derived from argv, never from rawCommand.
        var displayCommand = ShellQuoting.FormatExecCommand(argv);

        // rawCommand is display/consistency metadata, never executable input.
        // Evaluation stays argv-only so approval and execution share one canonical command.
        string? evaluationRawCommand = null;

        // Singular resolution for state machine.
        var resolution = ExecCommandResolver.Resolve(argv, cwd, env);

        // Durable authorization has one source of truth. Shell inspection may identify
        // inner commands for diagnostics, but only a safely bound reusable command may
        // satisfy an allowlist or produce an Allow Always pattern.
        //
        // The failure reason is carried forward rather than discarded. Without it, a
        // command that is offered as one-time only looks indistinguishable from one
        // that simply missed the allowlist, which is the hardest class of exec
        // approval problem to diagnose from a log.
        var reusableCommand = ExecReusableCommandBinder.TryBind(argv, cwd, env, out var bindFailure);
        IReadOnlyList<ExecCommandResolution> allowlistResolutions =
            reusableCommand is null ? [] : [reusableCommand.Resolution];
        IReadOnlyList<string> allowAlwaysPatterns =
            reusableCommand is null ? [] : [reusableCommand.Pattern];

        // If argv is non-empty but resolution is entirely impossible, deny.
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
            request.SessionKey,
            reusableCommand,
            request.RawCommand,
            reusableCommand is null ? ExecReusableCommandBinder.DescribeFailure(bindFailure) : null);

        return ExecApprovalV2NormalizationOutcome.Ok(identity);
    }

    private static ExecApprovalV2NormalizationOutcome Fail(string reason)
        => ExecApprovalV2NormalizationOutcome.Fail(
            ExecApprovalV2Result.ResolutionFailed(reason));
}
