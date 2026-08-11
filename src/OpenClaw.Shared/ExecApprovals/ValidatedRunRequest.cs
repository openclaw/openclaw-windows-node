using System.Collections.Generic;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Structurally-valid system.run input produced by ExecApprovalV2InputValidator.
/// Argv is guaranteed non-empty with a non-blank first element.
/// </summary>
public sealed class ValidatedRunRequest
{
    public string[] Argv { get; }
    public string? Cwd { get; }
    public int TimeoutMs { get; }
    public IReadOnlyDictionary<string, string>? Env { get; }
    public string? AgentId { get; }
    public string? SessionKey { get; }

    /// <summary>
    /// The request's human-readable command text, when supplied. Validated against
    /// Argv so the text an operator is shown cannot describe a different command
    /// than the one that runs. Never an input to resolution or execution.
    /// </summary>
    public string? RawCommand { get; }

    internal ValidatedRunRequest(
        string[] argv,
        string? cwd,
        int timeoutMs,
        IReadOnlyDictionary<string, string>? env,
        string? agentId,
        string? sessionKey,
        string? rawCommand = null)
    {
        Argv = argv;
        Cwd = cwd;
        TimeoutMs = timeoutMs;
        Env = env;
        AgentId = agentId;
        SessionKey = sessionKey;
        RawCommand = rawCommand;
    }
}

/// <summary>
/// Either a ValidatedRunRequest (IsValid=true) or a typed denial (IsValid=false).
/// Produced by ExecApprovalV2InputValidator; consumed by the coordinator pipeline.
/// </summary>
public sealed class ExecApprovalV2ValidationOutcome
{
    public bool IsValid { get; }
    public ValidatedRunRequest? Request { get; }
    public ExecApprovalV2Result? Error { get; }

    private ExecApprovalV2ValidationOutcome(ValidatedRunRequest request)
    {
        IsValid = true;
        Request = request;
    }

    private ExecApprovalV2ValidationOutcome(ExecApprovalV2Result error)
    {
        IsValid = false;
        Error = error;
    }

    public static ExecApprovalV2ValidationOutcome Ok(ValidatedRunRequest r) => new(r);
    public static ExecApprovalV2ValidationOutcome Fail(ExecApprovalV2Result e) => new(e);
}
