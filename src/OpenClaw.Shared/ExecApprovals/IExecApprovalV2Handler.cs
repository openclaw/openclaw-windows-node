using System.Threading.Tasks;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Seam for the V2 exec approval path (rail 10: UI-free, no WinUI types).
/// Implementations decide whether a system.run request is allowed.
/// In PR1 only the NullHandler exists; real evaluation arrives in later PRs.
/// </summary>
public interface IExecApprovalV2Handler
{
    /// <param name="correlationId">Short identifier propagated through logging for this request.</param>
    Task<ExecApprovalV2Result> HandleAsync(OpenClaw.Shared.NodeInvokeRequest request, string correlationId);
}
