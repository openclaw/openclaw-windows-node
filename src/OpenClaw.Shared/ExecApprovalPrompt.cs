using System;
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
    public string? SessionKey { get; init; }
    public string? CorrelationId { get; init; }
}

public sealed class ExecApprovalPromptDecision
{
    public const string TimedOutReason = "Approval prompt timed out";

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
    public static ExecApprovalPromptDecision TimedOut() => Deny(TimedOutReason);
}

public interface IExecApprovalPromptHandler
{
    Task<ExecApprovalPromptDecision> RequestAsync(ExecApprovalPromptRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Why the prompt resolved. Distinguishes a user's explicit click from
/// non-interactive terminations (token cancellation, prompt failure)
/// — all of which collapse to a Deny <see cref="ExecApprovalPromptDecisionKind"/>
/// for safety but mean very different things to UI surfaces.
/// </summary>
public enum ExecApprovalPromptDecisionSource
{
    UserDeny,
    UserAllowOnce,
    UserAlwaysAllow,
    Cancelled,
    TimedOut,
    Failed,
    /// <summary>
    /// Policy denied the command non-interactively (e.g. default action is
    /// Deny and no allow rule matched). No native prompt was ever shown.
    /// Surfaces in chat so users see why their request didn't execute.
    /// </summary>
    PolicyAutoDeny
}

public sealed class ExecApprovalPromptDecidedEventArgs : EventArgs
{
    public ExecApprovalPromptDecidedEventArgs(
        ExecApprovalPromptRequest request,
        ExecApprovalPromptDecision decision,
        ExecApprovalPromptDecisionSource source)
    {
        Request = request;
        Decision = decision;
        Source = source;
    }

    public ExecApprovalPromptRequest Request { get; }
    public ExecApprovalPromptDecision Decision { get; }
    public ExecApprovalPromptDecisionSource Source { get; }
}

public sealed class ExecApprovalPromptRequestedEventArgs : EventArgs
{
    public ExecApprovalPromptRequestedEventArgs(ExecApprovalPromptRequest request)
    {
        Request = request;
    }

    public ExecApprovalPromptRequest Request { get; }
}
