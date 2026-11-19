// What an actionable approval toast shows and which arguments its buttons carry. Pure and
// WinUI-free so the security rules are unit-testable:
//  - Arguments carry only the action, the approval id, the decision and a one-shot token
//    issued by OperatorApprovalDecisions; never command, title or scope text.
//  - "Allow once" is offered only for an exec command the toast shows in full (single line,
//    reviewable, not shortened), with no Gateway warning, on a host confirmed to be this
//    computer (the toast says so); plugin and system-agent approvals, warned or remote
//    commands, and longer commands must be reviewed in the Inbox. "Always allow" is never
//    offered from a toast.
//  - "Deny" is offered whenever the approval allows it; the toast body and "Review" open the Inbox.
//
// Usage:
//   var offered = ApprovalToastPlan.Decisions(approval, hostConfirmedLocal);
//   if (ApprovalToastPlan.Build(approval, token, LocalizationHelper.GetString, hostConfirmedLocal, deadline) is { } plan)
//       presenter.Show(plan);

using OpenClaw.Shared;

namespace OpenClawTray.Presentation;

internal sealed record ApprovalToastButton(string Label, IReadOnlyDictionary<string, string> Arguments);

internal sealed record ApprovalToastPlan(
    string Title,
    IReadOnlyList<string> Lines,
    IReadOnlyList<ApprovalToastButton> Buttons,
    IReadOnlyDictionary<string, string> LaunchArguments,
    DateTimeOffset ExpiresAt)
{
    public const string ActionKey = "action";
    public const string DecideAction = "approval_decision";
    public const string OpenInboxAction = "open_inbox";
    public const string ApprovalIdKey = "approvalId";
    public const string DecisionKey = "decision";
    public const string TokenKey = "token";

    /// <summary>
    /// Decisions a toast may offer and an activation may send: Deny when offered, and Allow once
    /// only for an exec command the toast shows in full, with no Gateway warning, that runs on a
    /// host confirmed to be this computer (the toast shows that host). Never Always allow.
    /// </summary>
    public static IReadOnlyList<string> Decisions(GatewayPendingApproval approval, bool hostConfirmedLocal)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var effective = GatewayApprovalDecisions.Effective(approval);
        // A toast has room for the command and host, not additional consent context.
        // Keep scoped or directory-bound operations on the full Inbox review surface.
        var canAllow = hostConfirmedLocal &&
            approval.Scope is null &&
            string.IsNullOrWhiteSpace(approval.Cwd) &&
            string.IsNullOrWhiteSpace(approval.WarningText) &&
            approval.Kind == GatewayApprovalKind.Exec &&
            !string.IsNullOrWhiteSpace(approval.Command) &&
            GatewayApprovalDecisions.CanReviewInFull(approval) &&
            InboxGatewayItems.DisplayCommand(approval.Command) is var command &&
            string.Equals(InboxGatewayItems.CompactCommand(approval.Command), command, StringComparison.Ordinal);
        return effective
            .Where(decision => decision == GatewayApprovalDecisions.Deny ||
                (decision == GatewayApprovalDecisions.AllowOnce && canAllow))
            .ToArray();
    }

    /// <summary>
    /// Builds the toast for a decidable approval, or null when the toast would offer no decision.
    /// <paramref name="deadline"/> is the approval's local deadline; Windows drops the toast then.
    /// </summary>
    public static ApprovalToastPlan? Build(
        GatewayPendingApproval approval, string token, Func<string, string> getString, bool hostConfirmedLocal, DateTimeOffset deadline)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentNullException.ThrowIfNull(getString);

        var decisions = Decisions(approval, hostConfirmedLocal);
        if (decisions.Count == 0)
            return null;

        var isExec = approval.Kind == GatewayApprovalKind.Exec;
        var subject = isExec
            ? string.IsNullOrWhiteSpace(approval.Command) ? getString("Inbox_ApprovalNoCommand") : InboxGatewayItems.CompactCommand(approval.Command)
            : InboxGatewayItems.CompactCommand(approval.Title ?? approval.Command);

        var buttons = new List<ApprovalToastButton>();
        if (decisions.Contains(GatewayApprovalDecisions.AllowOnce))
            buttons.Add(Decide(getString("Inbox_ApprovalAllowOnce"), approval.Id, GatewayApprovalDecisions.AllowOnce, token));
        if (decisions.Contains(GatewayApprovalDecisions.Deny))
            buttons.Add(Decide(getString("Inbox_ApprovalDeny"), approval.Id, GatewayApprovalDecisions.Deny, token));
        buttons.Add(new(getString("Toast_ApprovalReview"), OpenInbox()));

        var title = getString(approval.Kind switch
        {
            GatewayApprovalKind.Exec => "Inbox_ApprovalExecTitle",
            GatewayApprovalKind.Plugin => "Inbox_ApprovalPluginTitle",
            _ => "Inbox_ApprovalTitle",
        });
        var lines = new List<string> { subject };
        if (decisions.Contains(GatewayApprovalDecisions.AllowOnce))
            lines.Add(getString("Toast_ApprovalRunsOnThisComputer"));
        if (approval.Scope is { } scope)
            lines.Add(InboxGatewayItems.ScopeLabel(scope, getString));
        return new(title, lines, buttons, OpenInbox(), deadline);
    }

    private static ApprovalToastButton Decide(string label, string approvalId, string decision, string token) =>
        new(label, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ActionKey] = DecideAction,
            [ApprovalIdKey] = approvalId,
            [DecisionKey] = decision,
            [TokenKey] = token,
        });

    private static IReadOnlyDictionary<string, string> OpenInbox() =>
        new Dictionary<string, string>(StringComparer.Ordinal) { [ActionKey] = OpenInboxAction };
}
