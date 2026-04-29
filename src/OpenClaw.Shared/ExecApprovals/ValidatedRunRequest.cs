using System.Collections.Generic;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Structurally-valid system.run input produced by ExecApprovalV2InputValidator.
/// Argv is guaranteed non-empty with a non-blank first element.
/// </summary>
public sealed class ValidatedRunRequest
{
    public string[] Argv { get; }
    public string? Shell { get; }
    public string? Cwd { get; }
    public int TimeoutMs { get; }
    public IReadOnlyDictionary<string, string>? Env { get; }
    public string? AgentId { get; }
    public string? SessionKey { get; }

    internal ValidatedRunRequest(
        string[] argv,
        string? shell,
        string? cwd,
        int timeoutMs,
        IReadOnlyDictionary<string, string>? env,
        string? agentId,
        string? sessionKey)
    {
        Argv = argv;
        Shell = shell;
        Cwd = cwd;
        TimeoutMs = timeoutMs;
        Env = env;
        AgentId = agentId;
        SessionKey = sessionKey;
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
