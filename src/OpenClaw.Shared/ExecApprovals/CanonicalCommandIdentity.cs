using System.Collections.Generic;

namespace OpenClaw.Shared.ExecApprovals;

// Architectural barrier produced by PR3.
// Equivalent to ExecHostValidatedRequest in the macOS reference, extended with resolution outputs.
// No module from PR4 onward may accept ValidatedRunRequest as direct input (research doc 05 line 439).
// Rail 15: a single canonical representation reused across evaluation, logging, prompting, execution.
public sealed class CanonicalCommandIdentity
{
    // ── Normalization outputs ─────────────────────────────────────────────────

    // Argv exactly as produced by PR2 (no trimming; coding contract process-argv-semantics).
    public IReadOnlyList<string> Command { get; }

    // Canonical display form generated from argv. Never rawCommand from the agent.
    // Used by logging and prompting. Research doc 05 decision 2.
    public string DisplayCommand { get; }

    // Safe rawCommand for executable resolution. Null in Windows v1 (rawCommand not in
    // system.run protocol; research doc 05 OQ-V4 / decision 10).
    public string? EvaluationRawCommand { get; }

    // ── Resolution outputs ────────────────────────────────────────────────────

    // Singular resolution for the state machine (PR5).
    // Null if the primary executable cannot be determined.
    public ExecCommandResolution? Resolution { get; }

    // Per-segment resolutions for the allowlist matcher (PR4/PR5).
    // Empty list means fail-closed — no allowlist satisfaction possible.
    public IReadOnlyList<ExecCommandResolution> AllowlistResolutions { get; }

    // Suggested allowlist patterns for prompt/UI (PR6). Not a security decision.
    public IReadOnlyList<string> AllowAlwaysPatterns { get; }

    // ── Request context (carried from ValidatedRunRequest) ────────────────────

    public string? Cwd { get; }
    public int TimeoutMs { get; }
    public IReadOnlyDictionary<string, string>? Env { get; }
    public string? AgentId { get; }
    public string? SessionKey { get; }

    internal CanonicalCommandIdentity(
        IReadOnlyList<string> command,
        string displayCommand,
        string? evaluationRawCommand,
        ExecCommandResolution? resolution,
        IReadOnlyList<ExecCommandResolution> allowlistResolutions,
        IReadOnlyList<string> allowAlwaysPatterns,
        string? cwd,
        int timeoutMs,
        IReadOnlyDictionary<string, string>? env,
        string? agentId,
        string? sessionKey)
    {
        Command = command;
        DisplayCommand = displayCommand;
        EvaluationRawCommand = evaluationRawCommand;
        Resolution = resolution;
        AllowlistResolutions = allowlistResolutions;
        AllowAlwaysPatterns = allowAlwaysPatterns;
        Cwd = cwd;
        TimeoutMs = timeoutMs;
        Env = env;
        AgentId = agentId;
        SessionKey = sessionKey;
    }
}
