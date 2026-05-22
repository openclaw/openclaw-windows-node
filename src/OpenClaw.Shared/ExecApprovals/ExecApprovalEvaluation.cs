using System;
using System.Collections.Generic;

namespace OpenClaw.Shared.ExecApprovals;

// Aggregated evaluation context passed to the stateless evaluator.
// Shape mirrors macOS ExecApprovalEvaluation struct.
// Derived fields are computed once in the constructor and must not be recomputed by callers.
// Construction belongs to the coordinator, not the evaluator.
public sealed class ExecApprovalEvaluation
{
    public IReadOnlyList<string> Command { get; }
    public string DisplayCommand { get; }
    public string? AgentId { get; }
    public ExecSecurity Security { get; }
    public ExecAsk Ask { get; }
    public IReadOnlyDictionary<string, string>? Env { get; }

    // Singular resolution — AllowlistResolutions[0], or null if the list is empty.
    // Always the first element of AllowlistResolutions, not an independent resolver call.
    public ExecCommandResolution? Resolution { get; }

    public IReadOnlyList<ExecCommandResolution> AllowlistResolutions { get; }
    public IReadOnlyList<string> AllowAlwaysPatterns { get; }
    public IReadOnlyList<ExecAllowlistEntry> AllowlistMatches { get; }

    // true iff security==allowlist && resolutions.Count>0 && matches.Count==resolutions.Count.
    // Derived once in the constructor — must not be re-derived outside it.
    public bool AllowlistSatisfied { get; }

    // First match when AllowlistSatisfied; null otherwise.
    // Must be null when AllowlistSatisfied is false.
    public ExecAllowlistEntry? AllowlistMatch { get; }

    // Always false in v1. Kept as part of the conceptual model; activation deferred.
    public bool SkillAllow { get; }

    public ExecApprovalEvaluation(
        IReadOnlyList<string> command,
        string displayCommand,
        string? agentId,
        ExecSecurity security,
        ExecAsk ask,
        IReadOnlyDictionary<string, string>? env,
        IReadOnlyList<ExecCommandResolution> allowlistResolutions,
        IReadOnlyList<string> allowAlwaysPatterns,
        IReadOnlyList<ExecAllowlistEntry> allowlistMatches,
        bool skillAllow = false)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(displayCommand);
        ArgumentNullException.ThrowIfNull(allowlistResolutions);
        ArgumentNullException.ThrowIfNull(allowAlwaysPatterns);
        ArgumentNullException.ThrowIfNull(allowlistMatches);
        if (allowlistMatches.Count > allowlistResolutions.Count)
            throw new ArgumentException(
                "allowlistMatches cannot have more entries than allowlistResolutions.",
                nameof(allowlistMatches));

        Command = command;
        DisplayCommand = displayCommand;
        AgentId = agentId;
        Security = security;
        Ask = ask;
        Env = env;
        AllowlistResolutions = allowlistResolutions;
        AllowAlwaysPatterns = allowAlwaysPatterns;
        AllowlistMatches = allowlistMatches;
        SkillAllow = skillAllow;

        Resolution = allowlistResolutions.Count > 0 ? allowlistResolutions[0] : (ExecCommandResolution?)null;

        AllowlistSatisfied = security == ExecSecurity.Allowlist
            && allowlistResolutions.Count > 0
            && allowlistMatches.Count == allowlistResolutions.Count;

        AllowlistMatch = AllowlistSatisfied ? allowlistMatches[0] : null;
    }
}
