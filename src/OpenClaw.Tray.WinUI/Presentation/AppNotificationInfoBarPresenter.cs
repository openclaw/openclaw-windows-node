using OpenClawTray.Services;

namespace OpenClawTray.Presentation;

internal enum AppNotificationInfoBarSeverity
{
    Informational,
    Success,
    Warning,
    Error
}

internal enum AppNotificationInfoBarActionKind
{
    None,
    NotificationRoute,
    ShowMore
}

internal sealed record AppNotificationInfoBarAction(
    AppNotificationInfoBarActionKind Kind,
    string? Label,
    string? Route,
    bool IsEnabled)
{
    public static AppNotificationInfoBarAction None { get; } =
        new(AppNotificationInfoBarActionKind.None, null, null, true);
}

internal sealed record AppNotificationInfoBarPresentation(
    AppNotification? Notification,
    AppNotificationInfoBarSeverity Severity,
    AppNotificationInfoBarAction Action)
{
    public bool IsVisible => Notification is not null;

    public static AppNotificationInfoBarPresentation Hidden { get; } =
        new(null, AppNotificationInfoBarSeverity.Informational, AppNotificationInfoBarAction.None);
}

internal sealed class AppNotificationInfoBarPresenter
{
    private readonly AppNotificationBannerState _bannerState = new();

    public AppNotificationInfoBarPresentation Present(
        AppNotificationSnapshot snapshot,
        string? displayedNotificationId,
        bool isInfoBarOpen,
        string? currentTag,
        string showMoreLabel,
        bool doNotDisturb = false)
    {
        var bannerActive = snapshot.ActiveNotifications
            .Where(notification => IsBannerSeverity(notification.Severity))
            .ToList();
        var bannerSnapshot = bannerActive.Count == snapshot.ActiveNotifications.Count
            ? snapshot
            : snapshot with { ActiveNotifications = bannerActive };

        var displayedNotificationWasRemoved =
            !string.IsNullOrEmpty(displayedNotificationId) &&
            isInfoBarOpen &&
            !bannerSnapshot.ActiveNotifications.Any(notification =>
                string.Equals(notification.Id, displayedNotificationId, StringComparison.Ordinal));
        // Do not disturb leaves only approval banners eligible; hidden-banner state still sees every banner.
        var notification = _bannerState.SelectVisibleNotification(
            bannerSnapshot,
            revealHiddenIfNeeded: displayedNotificationWasRemoved,
            isEligible: candidate => DoNotDisturbPolicy.AllowsBanner(doNotDisturb, candidate));
        if (notification is null)
            return AppNotificationInfoBarPresentation.Hidden;

        return new AppNotificationInfoBarPresentation(
            notification,
            ToSemanticSeverity(notification.Severity),
            GetAction(notification, InboxProjection.Build(snapshot.ActiveNotifications).Entries.Count > 1, currentTag, showMoreLabel));
    }

    public AppNotificationInfoBarPresentation UpdateCurrentTag(
        AppNotificationInfoBarPresentation presentation,
        string? currentTag)
    {
        if (presentation.Action.Kind != AppNotificationInfoBarActionKind.ShowMore)
            return presentation;

        return presentation with
        {
            Action = presentation.Action with
            {
                IsEnabled = !string.Equals(currentTag, "notifications", StringComparison.Ordinal)
            }
        };
    }

    /// <summary>
    /// Hides the banners the user could have seen. While Do not disturb is on, suppressed
    /// banners stay unhidden so they can appear once it turns off.
    /// </summary>
    public void HideActiveNotifications(AppNotificationSnapshot snapshot, bool doNotDisturb = false) =>
        _bannerState.HideActiveNotifications(
            snapshot,
            isEligible: notification => DoNotDisturbPolicy.AllowsBanner(doNotDisturb, notification));

    private static AppNotificationInfoBarAction GetAction(
        AppNotification notification,
        bool hasMoreInboxEntries,
        string? currentTag,
        string showMoreLabel)
    {
        if (!string.IsNullOrWhiteSpace(notification.ActionLabel) &&
            !string.IsNullOrWhiteSpace(notification.ActionRoute))
        {
            return new AppNotificationInfoBarAction(
                AppNotificationInfoBarActionKind.NotificationRoute,
                notification.ActionLabel,
                notification.ActionRoute,
                true);
        }

        // "Show more" opens the Inbox, so it needs another Inbox entry to show.
        if (hasMoreInboxEntries)
        {
            return new AppNotificationInfoBarAction(
                AppNotificationInfoBarActionKind.ShowMore,
                showMoreLabel,
                null,
                !string.Equals(currentTag, "notifications", StringComparison.Ordinal));
        }

        return AppNotificationInfoBarAction.None;
    }

    private static bool IsBannerSeverity(AppNotificationSeverity severity) =>
        severity is AppNotificationSeverity.Error or AppNotificationSeverity.Warning;

    private static AppNotificationInfoBarSeverity ToSemanticSeverity(
        AppNotificationSeverity severity) => severity switch
    {
        AppNotificationSeverity.Success => AppNotificationInfoBarSeverity.Success,
        AppNotificationSeverity.Warning => AppNotificationInfoBarSeverity.Warning,
        AppNotificationSeverity.Error => AppNotificationInfoBarSeverity.Error,
        _ => AppNotificationInfoBarSeverity.Informational
    };
}
