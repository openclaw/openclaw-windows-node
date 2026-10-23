using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.ExecApprovals;

public interface IExecApprovalV2PromptHandler
{
    // Implementations must never throw. Infrastructure errors fail closed as Failed,
    // not as a user or policy refusal.
    Task<ExecApprovalPromptOutcome> PromptAsync(ExecApprovalV2PromptRequest request, CancellationToken cancellationToken = default);
}
