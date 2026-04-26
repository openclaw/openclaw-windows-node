using System.Threading.Tasks;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Default V2 handler: always returns <see cref="ExecApprovalV2Code.Unavailable"/>.
/// Keeps the V2 path inert until a real handler is installed (rail 19).
/// Never throws, never falls through to legacy.
/// </summary>
public sealed class ExecApprovalV2NullHandler : IExecApprovalV2Handler
{
    public static readonly ExecApprovalV2NullHandler Instance = new();

    public Task<ExecApprovalV2Result> HandleAsync(OpenClaw.Shared.NodeInvokeRequest request, string correlationId)
        => Task.FromResult(ExecApprovalV2Result.Unavailable());
}
