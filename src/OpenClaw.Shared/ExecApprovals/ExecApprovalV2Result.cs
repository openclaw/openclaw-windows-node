using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using OpenClaw.Shared;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// What the caller must execute after an allow. <see cref="Argv"/> is the validated
/// argument vector and <see cref="Env"/> is the sanitized environment built during
/// evaluation. Neither may be re-derived from the raw request, and the argv must
/// reach the process without a shell re-parsing it. The constructor takes defensive
/// copies so the approved argv/env cannot be mutated through an aliased reference
/// between approval and execution.
/// </summary>
public sealed record ExecApprovedExecution
{
    public const int MaxTimeoutMs = 600_000;

    public IReadOnlyList<string> Argv { get; }
    public string? Cwd { get; }
    public int TimeoutMs { get; }
    public IReadOnlyDictionary<string, string>? Env { get; }

    public ExecApprovedExecution(
        IReadOnlyList<string> argv,
        string? cwd,
        int timeoutMs,
        IReadOnlyDictionary<string, string>? env)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count == 0)
            throw new ArgumentException("Approved execution requires a non-empty argv.", nameof(argv));
        if (timeoutMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), timeoutMs, "Approved execution timeout must be positive.");

        Argv = Array.AsReadOnly(argv.ToArray());
        Cwd = cwd;
        TimeoutMs = Math.Min(timeoutMs, MaxTimeoutMs);
        Env = env is null
            ? null
            : new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(env, StringComparer.OrdinalIgnoreCase));
    }

    public CommandRequest ToCommandRequest() => new()
    {
        Argv = Argv,
        Cwd = Cwd,
        TimeoutMs = TimeoutMs,
        Env = Env is null
            ? null
            : new Dictionary<string, string>(Env, StringComparer.OrdinalIgnoreCase),
    };
}

/// <summary>
/// Stable result codes for the V2 exec approval path.
/// </summary>
public enum ExecApprovalV2Code
{
    Unavailable,
    SecurityDeny,
    AskDeny,
    AllowlistMiss,
    UserDenied,
    ValidationFailed,
    ResolutionFailed,
    InternalError,  // invariant violations and unexpected internal bugs detected at runtime
    Allow,          // coordinator approved; caller may execute the command
}

/// <summary>
/// Typed result returned by the V2 exec approval path.
/// Every outcome carries a stable code and a human-readable reason.
/// </summary>
public sealed class ExecApprovalV2Result
{
    public ExecApprovalV2Code Code { get; }
    public string Reason { get; }

    /// <summary>
    /// The command to execute. Non-null only on <see cref="ExecApprovalV2Code.Allow"/>.
    /// Carries the validated argv and sanitized env so the caller never re-derives
    /// them from the raw request.
    /// </summary>
    public ExecApprovedExecution? Execution { get; }

    private ExecApprovalV2Result(ExecApprovalV2Code code, string reason, ExecApprovedExecution? execution = null)
    {
        // Invariant: Allow must carry a payload; non-Allow must not. A null payload
        // on Allow (or a payload on a deny) is a bug, not a representable state.
        if (code == ExecApprovalV2Code.Allow && execution is null)
            throw new ArgumentNullException(nameof(execution), "Allow result requires an execution payload.");
        if (code != ExecApprovalV2Code.Allow && execution is not null)
            throw new ArgumentException("Non-allow result must not carry an execution payload.", nameof(execution));

        Code = code;
        Reason = reason;
        Execution = execution;
    }

    public static ExecApprovalV2Result Unavailable(string reason = "Handler not available")
        => new(ExecApprovalV2Code.Unavailable, reason);

    public static ExecApprovalV2Result SecurityDeny(string reason)
        => new(ExecApprovalV2Code.SecurityDeny, reason);

    public static ExecApprovalV2Result AskDeny(string reason)
        => new(ExecApprovalV2Code.AskDeny, reason);

    public static ExecApprovalV2Result AllowlistMiss(string reason)
        => new(ExecApprovalV2Code.AllowlistMiss, reason);

    public static ExecApprovalV2Result UserDenied(string reason)
        => new(ExecApprovalV2Code.UserDenied, reason);

    public static ExecApprovalV2Result ValidationFailed(string reason)
        => new(ExecApprovalV2Code.ValidationFailed, reason);

    public static ExecApprovalV2Result ResolutionFailed(string reason)
        => new(ExecApprovalV2Code.ResolutionFailed, reason);

    public static ExecApprovalV2Result InternalError(string reason)
        => new(ExecApprovalV2Code.InternalError, reason);

    /// <summary>
    /// Approve the command and carry the execution payload the caller must run.
    /// An allow without a payload is not a valid state - there is intentionally
    /// no parameterless allow: the approved argv must reach the process verbatim.
    /// </summary>
    public static ExecApprovalV2Result Allow(ExecApprovedExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return new(ExecApprovalV2Code.Allow, "approved", execution);
    }

    public bool IsAllow => Code == ExecApprovalV2Code.Allow;

    public override string ToString() => $"{Code}: {Reason}";
}
