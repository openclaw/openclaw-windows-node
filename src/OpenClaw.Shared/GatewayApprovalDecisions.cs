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

    private static readonly HashSet<string> LoggableErrorCodes = new(StringComparer.Ordinal)
    {
        "INVALID_REQUEST", "INVALID_PARAMS", "UNAVAILABLE", "FORBIDDEN", "UNAUTHORIZED", "MISSING_SCOPE",
        "NOT_LINKED", "NOT_PAIRED", ApprovalNotFound, ApprovalAlreadyResolved,
    };

    /// <summary>A Gateway error code safe to log, from a fixed allowlist; anything else is "UNKNOWN", absent is "none".</summary>
    public static string SafeErrorCode(string? code) =>
        code is null ? "none" : LoggableErrorCodes.Contains(code) ? code : "UNKNOWN";

    /// <summary>Every decision, in the Inbox's permit-first order.</summary>
    public static IReadOnlyList<string> All { get; } = [AllowOnce, AllowAlways, Deny];

    public static bool IsKnown(string? decision) => decision is AllowOnce or AllowAlways or Deny;

    /// <summary>
    /// True when the connection's granted scopes explicitly include <c>operator.approvals</c> or
    /// <c>operator.admin</c>. Deciding fails closed: an empty or missing scope list is no grant.
    /// </summary>
    public static bool HasDecisionGrant(IEnumerable<string>? grantedScopes) =>
        grantedScopes?.Any(scope => scope is "operator.approvals" or "operator.admin") == true;

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
    /// False when the command or required reviewer text cannot be displayed completely,
    /// so a person could not review what they are allowing.
    /// </summary>
    public static bool CanReviewInFull(GatewayPendingApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (!CanReviewText(approval.WarningText) || !CanReviewText(approval.Title) ||
            !CanReviewText(approval.Description) || !CanReviewText(approval.Detail))
            return false;
        if (approval.Kind != GatewayApprovalKind.Exec && string.IsNullOrWhiteSpace(approval.Title))
            return false;
        if (approval.Kind == GatewayApprovalKind.Plugin)
            return true;
        if (string.IsNullOrWhiteSpace(approval.Command))
            return false;
        var status = ExecApprovalCommandDisplaySanitizer.SanitizeWithStatus(approval.Command);
        return !status.Truncated && !status.Oversized && !status.UnsafeConcealment;
    }

    private static bool CanReviewText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return true;
        var status = ExecApprovalCommandDisplaySanitizer.SanitizeWarningTextWithStatus(text);
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
