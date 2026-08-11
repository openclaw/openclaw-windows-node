using System.Collections.Generic;

namespace OpenClaw.Shared.ExecApprovals;

// Architectural barrier between raw requests and the evaluation pipeline.
// Equivalent to ExecHostValidatedRequest in the macOS reference, extended with resolution outputs.
// No evaluation module may accept ValidatedRunRequest as direct input — this is the canonical handoff type.
// A single canonical representation reused across evaluation, logging, prompting, and execution.
public sealed class CanonicalCommandIdentity
{
    // ── Normalization outputs ─────────────────────────────────────────────────

    // Argv as received from the normalizer (no trimming; callers must not modify).
    public IReadOnlyList<string> Command { get; }

    // Canonical display form generated from argv. Never rawCommand from the agent.
    // Used by logging and prompting.
    public string DisplayCommand { get; }

    // Safe rawCommand for executable resolution. Null in Windows v1 (rawCommand is
    // never an input to resolution; see RawCommand for the validated display text).
    public string? EvaluationRawCommand { get; }

    // The request's rawCommand, already validated against Command by the input
    // validator. Audit and display only. Deliberately distinct from
    // EvaluationRawCommand so it can never become executable input.
    public string? RawCommand { get; }

    // Why no durable identity was produced, when ReusableCommand is null. Diagnostic
    // only: it explains a prompt-only outcome instead of leaving it unexplained.
    public string? ReusableBindFailure { get; }

    // ── Resolution outputs ────────────────────────────────────────────────────

    // Singular resolution for the state machine.
    // Null if the primary executable cannot be determined.
    public ExecCommandResolution? Resolution { get; }

    // Per-segment resolutions for the allowlist matcher.
    // Empty list means fail-closed — no allowlist satisfaction possible.
    public IReadOnlyList<ExecCommandResolution> AllowlistResolutions { get; }

    // Suggested allowlist patterns for prompt/UI. Not a security decision.
    public IReadOnlyList<string> AllowAlwaysPatterns { get; }

    // The only identity eligible for durable allowlist authorization and execution.
    // Null means the request can be approved only as an exact one-time/full-policy operation.
    public ExecReusableCommand? ReusableCommand { get; }

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
        string? sessionKey,
        ExecReusableCommand? reusableCommand = null,
        string? rawCommand = null,
        string? reusableBindFailure = null)
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
        ReusableCommand = reusableCommand;
        RawCommand = rawCommand;
        ReusableBindFailure = reusableBindFailure;
    }
}
