// The Inbox "Do not disturb" rule shared by every alert surface: while it is on, Windows
// toasts (and their sounds) and in-app banners stay silent for everything except approvals,
// because a blocked agent is waiting on the user. The Inbox keeps collecting items and the
// bell keeps counting them; only interruptions are suppressed. WinUI-free.
//
// Usage:
//   if (!DoNotDisturbPolicy.AllowsAlert(settings.DoNotDisturb, alertKind)) return;   // ToastService
//   var kind = DoNotDisturbPolicy.KindOf(appNotification);                          // publishers
//   var banner = DoNotDisturbPolicy.AllowsBanner(doNotDisturb, notification);       // InfoBar

using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

/// <summary>
/// Whether an interruption asks the user to decide something (an approval, a device pairing
/// or scope-upgrade request), which Do not disturb never silences.
/// </summary>
internal enum AlertKind
{
    Standard,
    Approval,
}

internal static class DoNotDisturbPolicy
{
    /// <summary>True when a toast, its sound or a banner of <paramref name="kind"/> may interrupt.</summary>
    public static bool AllowsAlert(bool doNotDisturb, AlertKind kind) =>
        !doNotDisturb || kind == AlertKind.Approval;

    /// <summary>
    /// App notifications come through Do not disturb when the Inbox files them as approvals or
    /// as access requests (device pairing and scope upgrades), which users decide like approvals.
    /// </summary>
    public static AlertKind KindOf(AppNotification? notification) =>
        notification is not null &&
        InboxProjection.Classify(notification)?.Kind is InboxEntryKind.Approval or InboxEntryKind.AccessRequest
            ? AlertKind.Approval
            : AlertKind.Standard;

    /// <summary>True when <paramref name="notification"/> may still appear as an in-app banner.</summary>
    public static bool AllowsBanner(bool doNotDisturb, AppNotification notification) =>
        AllowsAlert(doNotDisturb, KindOf(notification));
}
