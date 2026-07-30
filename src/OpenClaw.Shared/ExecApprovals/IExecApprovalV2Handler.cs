using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Seam for the V2 exec approval path. Implementations must be UI-free (no WinUI types).
/// Implementations decide whether a system.run request is allowed.
/// The NullHandler is the default; production wiring installs the real coordinator.
/// </summary>
public interface IExecApprovalV2Handler
{
    /// <param name="correlationId">Short identifier propagated through logging for this request.</param>
    Task<ExecApprovalV2Result> HandleAsync(OpenClaw.Shared.NodeInvokeRequest request, string correlationId);

    /// <summary>
    /// Revalidates the authorizing policy immediately before process launch. The default
    /// fails closed so a handler cannot authorize execution without implementing currency.
    /// </summary>
    ValueTask<ExecApprovalRevalidationResult> RevalidateAsync(
        ExecApprovedExecution execution,
        string correlationId,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(
            ExecApprovalRevalidationResult.NotCurrent("policy-revalidation-unavailable"));
}

public readonly record struct ExecApprovalRevalidationResult(bool IsCurrent, string Reason)
{
    public static ExecApprovalRevalidationResult Current { get; } = new(true, "current");

    public static ExecApprovalRevalidationResult NotCurrent(string reason)
        => new(false, reason);
}
