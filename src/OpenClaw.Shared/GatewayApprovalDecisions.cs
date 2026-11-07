// Decision rules for Gateway approvals (exec, plugin and system-agent), shared by every
// operator surface that decides them. Mirrors the web Control UI: the request's
// allowedDecisions win, then an "ask always" exec request offers Allow once and Deny,
// otherwise all three decisions. Like the native chat banner, an exec command that cannot
// be shown in full (missing, truncated, oversized or concealing text) only offers Deny.
//
// Usage:
//   var decisions = GatewayApprovalDecisions.Effective(approval);   // ordered permit-first
//   if (decisions.Contains(decision)) await client.ResolveApprovalAsync(approval.Kind, approval.Id, decision, epoch);
//   catch (GatewayApprovalStaleException) { /* decided or expired elsewhere */ }

using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared;

public static class GatewayApprovalDecisions
{
    public const string AllowOnce = "allow-once";
    public const string AllowAlways = "allow-always";
    public const string Deny = "deny";

    private const string AskAlways = "always";
    private const string ApprovalNotFound = "APPROVAL_NOT_FOUND";
    private const string ApprovalAlreadyResolved = "APPROVAL_ALREADY_RESOLVED";
    // Message-only errors from Gateways that predate structured approval codes.
    private static readonly string[] LegacyStaleMessages =
    [
        "unknown or expired approval id",
        "approval expired or not found",
        "approval already resolved",
    ];

    /// <summary>Every decision, in the Inbox's permit-first order.</summary>
    public static IReadOnlyList<string> All { get; } = [AllowOnce, AllowAlways, Deny];

    public static bool IsKnown(string? decision) => decision is AllowOnce or AllowAlways or Deny;

    /// <summary>Gateway RPC that resolves an approval of <paramref name="kind"/>.</summary>
    public static string ResolveMethod(GatewayApprovalKind kind) => kind switch
    {
        GatewayApprovalKind.Plugin => "plugin.approval.resolve",
        GatewayApprovalKind.SystemAgent => "approval.resolve",
        _ => "exec.approval.resolve",
    };

    /// <summary>Decisions the operator may send for <paramref name="approval"/>, permit-first.</summary>
    public static IReadOnlyList<string> Effective(GatewayPendingApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        IEnumerable<string> decisions = approval.AllowedDecisionsSpecified
            ? approval.AllowedDecisions
            : approval.Kind == GatewayApprovalKind.Exec && string.Equals(approval.Ask, AskAlways, StringComparison.Ordinal)
                ? [AllowOnce, Deny]
                : All;
        if (!CanReviewInFull(approval))
            decisions = decisions.Where(decision => decision == Deny);
        return All.Where(decisions.Contains).ToArray();
    }

    /// <summary>
    /// False when an exec command is missing or cannot be displayed exactly as it would run,
    /// so a person could not review what they are allowing.
    /// </summary>
    public static bool CanReviewInFull(GatewayPendingApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (approval.Kind != GatewayApprovalKind.Exec)
            return true;
        if (string.IsNullOrWhiteSpace(approval.Command))
            return false;
        var status = ExecApprovalCommandDisplaySanitizer.SanitizeWithStatus(approval.Command);
        return !status.Truncated && !status.Oversized && !status.UnsafeConcealment;
    }

    /// <summary>True when a rejected resolve means the approval was already decided or expired.</summary>
    public static bool IsStaleRejection(string? code, string? detailsReason, string? message) =>
        code is ApprovalNotFound or ApprovalAlreadyResolved ||
        detailsReason is ApprovalNotFound or ApprovalAlreadyResolved ||
        (message is not null && LegacyStaleMessages.Any(text => message.Contains(text, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>
/// A resolve the Gateway rejected because the approval is no longer pending (decided elsewhere,
/// expired or unknown). Callers treat the approval as gone rather than retrying.
/// </summary>
public sealed class GatewayApprovalStaleException(string message) : InvalidOperationException(message);
