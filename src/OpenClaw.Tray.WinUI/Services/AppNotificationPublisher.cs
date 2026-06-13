namespace OpenClawTray.Services;

internal static class AppNotificationPublisher
{
    public static void Show(
        AppNotificationService? service,
        string title,
        string message,
        string source,
        string category,
        AppNotificationSeverity severity,
        string dedupeKey,
        string actionRoute,
        string actionLabel,
        string? id = null)
    {
        if (service is null)
            return;

        service.Show(new AppNotification
        {
            Id = id ?? "",
            Title = title,
            Message = message,
            Source = source,
            Category = category,
            Severity = severity,
            DedupeKey = dedupeKey,
            ActionRoute = actionRoute,
            ActionLabel = actionLabel
        });
    }
}
