using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.ExecApprovals;

public interface IExecApprovalV2PromptHandler
{
    // Implementations must never throw. On any unhandled error, fail-closed to Deny.
    Task<ExecApprovalPromptOutcome> PromptAsync(ExecApprovalV2PromptRequest request, CancellationToken cancellationToken = default);
}
