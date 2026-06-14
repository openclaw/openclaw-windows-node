namespace OpenClaw.Shared.ExecApprovals;

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

    private ExecApprovalV2Result(ExecApprovalV2Code code, string reason)
    {
        Code = code;
        Reason = reason;
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

    public static ExecApprovalV2Result Allow()
        => new(ExecApprovalV2Code.Allow, "approved");

    public bool IsAllow => Code == ExecApprovalV2Code.Allow;

    public override string ToString() => $"{Code}: {Reason}";
}
