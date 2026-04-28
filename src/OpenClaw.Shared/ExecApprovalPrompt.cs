using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared;

public enum ExecApprovalPromptDecisionKind
{
    Deny,
    AllowOnce,
    AlwaysAllow
}

public sealed class ExecApprovalPromptRequest
{
    public string Command { get; init; } = "";
    public string? Shell { get; init; }
    public string? MatchedPattern { get; init; }
    public string Reason { get; init; } = "";
}

public sealed class ExecApprovalPromptDecision
{
    private ExecApprovalPromptDecision(ExecApprovalPromptDecisionKind kind, string reason)
    {
        Kind = kind;
        Reason = reason;
    }

    public ExecApprovalPromptDecisionKind Kind { get; }
    public string Reason { get; }

    public static ExecApprovalPromptDecision Deny(string reason = "Denied by user") => new(ExecApprovalPromptDecisionKind.Deny, reason);
    public static ExecApprovalPromptDecision AllowOnce(string reason = "Allowed once by user") => new(ExecApprovalPromptDecisionKind.AllowOnce, reason);
    public static ExecApprovalPromptDecision AlwaysAllow(string reason = "Always allowed by user") => new(ExecApprovalPromptDecisionKind.AlwaysAllow, reason);
}

public interface IExecApprovalPromptHandler
{
    Task<ExecApprovalPromptDecision> RequestAsync(ExecApprovalPromptRequest request, CancellationToken cancellationToken = default);
}
