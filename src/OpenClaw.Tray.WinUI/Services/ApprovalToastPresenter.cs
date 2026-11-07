// Shows an actionable Windows toast when a Gateway approval arrives while OpenClaw is in
// the background, and withdraws it once the approval is decided, expires or the connection
// changes. Content and argument rules live in ApprovalToastPlan; tokens and decisions in
// OperatorApprovalDecisions. Honors the "Show notifications" preference and never toasts
// approvals the connection cannot decide or ones hydrated on connect.
//
// Usage:
//   var toasts = new ApprovalToastPresenter(source, decisions, LocalizationHelper.GetString,
//       notificationsEnabled: () => settings.ShowNotifications, isAppForeground: AppForeground.IsActive,
//       isHostConfirmedLocal: approval => approval.Host == "gateway" && activeGatewayIsLoopbackWithoutTunnel,
//       show: (plan, tag, group) => toastService.ShowWithdrawableToast(plan, tag, group),
//       withdraw: toastService.RemoveToast);
//   ...
//   toasts.Dispose();

using OpenClaw.Shared;
using OpenClawTray.Presentation;
using System.Security.Cryptography;
using System.Text;

namespace OpenClawTray.Services;

internal sealed class ApprovalToastPresenter : IDisposable
{
    internal const string ToastGroup = "openclaw-approvals";
    // Windows limits toast tags to 64 characters; approval ids are hashed to a fixed length.
    private const int TagHexLength = 32;

    private readonly InboxGatewaySource _source;
    private readonly OperatorApprovalDecisions _decisions;
    private readonly Func<string, string> _getString;
    private readonly Func<bool> _notificationsEnabled;
    private readonly Func<bool> _isAppForeground;
    private readonly Func<GatewayPendingApproval, bool> _isHostConfirmedLocal;
    private readonly Func<ApprovalToastPlan, string, string, bool> _show;
    private readonly Action<string, string> _withdraw;
    private readonly Dictionary<string, string> _shownTags = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <param name="isHostConfirmedLocal">True only when the approval's command runs on this computer.</param>
    public ApprovalToastPresenter(
        InboxGatewaySource source,
        OperatorApprovalDecisions decisions,
        Func<string, string> getString,
        Func<bool> notificationsEnabled,
        Func<bool> isAppForeground,
        Func<GatewayPendingApproval, bool> isHostConfirmedLocal,
        Func<ApprovalToastPlan, string, string, bool> show,
        Action<string, string> withdraw)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _decisions = decisions ?? throw new ArgumentNullException(nameof(decisions));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _notificationsEnabled = notificationsEnabled ?? throw new ArgumentNullException(nameof(notificationsEnabled));
        _isAppForeground = isAppForeground ?? throw new ArgumentNullException(nameof(isAppForeground));
        _isHostConfirmedLocal = isHostConfirmedLocal ?? throw new ArgumentNullException(nameof(isHostConfirmedLocal));
        _show = show ?? throw new ArgumentNullException(nameof(show));
        _withdraw = withdraw ?? throw new ArgumentNullException(nameof(withdraw));
        _source.ApprovalArrived += OnApprovalArrived;
        _source.Changed += OnSourceChanged;
    }

    internal static string TagFor(string approvalId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(approvalId)))[..TagHexLength];

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _source.ApprovalArrived -= OnApprovalArrived;
        _source.Changed -= OnSourceChanged;
    }

    // Both handlers run on the UI thread (InboxGatewaySource raises its events there).
    private void OnApprovalArrived(object? sender, GatewayPendingApproval approval)
    {
        if (_disposed || _shownTags.ContainsKey(approval.Id) ||
            !_notificationsEnabled() || _isAppForeground() || !_source.CanDecideApprovals)
            return;
        if (!_source.TryGetPendingApproval(approval.Id, out var pending))
            return;
        var hostConfirmedLocal = _isHostConfirmedLocal(approval);
        if (_decisions.IssueToastToken(approval, ApprovalToastPlan.Decisions(approval, hostConfirmedLocal)) is not { } token)
            return;

        var shown = false;
        try
        {
            if (ApprovalToastPlan.Build(approval, token, _getString, hostConfirmedLocal, pending.Deadline) is { } plan)
            {
                var tag = TagFor(approval.Id);
                shown = _show(plan, tag, ToastGroup);
                if (shown)
                    _shownTags[approval.Id] = tag;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Approval] approval toast failed: {ex.GetType().Name}");
        }
        if (!shown)
            _decisions.RevokeToastTokens(approval.Id);
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        if (_disposed || _shownTags.Count == 0)
            return;
        foreach (var (approvalId, tag) in _shownTags.ToList())
        {
            if (_source.TryGetPendingApproval(approvalId, out _))
                continue;
            _shownTags.Remove(approvalId);
            _decisions.RevokeToastTokens(approvalId);
            try
            {
                _withdraw(tag, ToastGroup);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Approval] approval toast withdraw failed: {ex.GetType().Name}");
            }
        }
    }
}
