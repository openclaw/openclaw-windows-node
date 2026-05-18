using System.Threading.Tasks;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>Default prompt handler stub: always denies. Never throws.</summary>
public sealed class ExecApprovalV2NullPromptHandler : IExecApprovalV2PromptHandler
{
    public static readonly ExecApprovalV2NullPromptHandler Instance = new();

    public Task<ExecApprovalDecision> PromptAsync(ExecApprovalV2PromptRequest request)
        => Task.FromResult(ExecApprovalDecision.Deny);
}
