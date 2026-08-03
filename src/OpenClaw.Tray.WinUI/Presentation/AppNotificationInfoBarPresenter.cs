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
        string showMoreLabel)
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
        var notification = _bannerState.SelectVisibleNotification(
            bannerSnapshot,
            revealHiddenIfNeeded: displayedNotificationWasRemoved);
        if (notification is null)
            return AppNotificationInfoBarPresentation.Hidden;

        return new AppNotificationInfoBarPresentation(
            notification,
            ToSemanticSeverity(notification.Severity),
            GetAction(notification, snapshot.HasMultipleActiveNotifications, currentTag, showMoreLabel));
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

    public void HideActiveNotifications(AppNotificationSnapshot snapshot) =>
        _bannerState.HideActiveNotifications(snapshot);

    private static AppNotificationInfoBarAction GetAction(
        AppNotification notification,
        bool hasMultipleActiveNotifications,
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

        if (hasMultipleActiveNotifications)
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
