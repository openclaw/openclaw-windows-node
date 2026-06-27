using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using System;
using System.Security.Cryptography;
using System.Text;

namespace OpenClawTray.Services;

internal static class AppNotificationMapper
{
    public static AppNotification FromGatewayNotification(OpenClawNotification notification, string? chatActionLabel = null)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var title = NormalizeTitle(notification.Title);
        var message = NormalizeMessage(notification.Message, title);
        var category = NormalizeCategory(notification.Type);
        var hasChatAction = notification.IsChat && !string.IsNullOrWhiteSpace(chatActionLabel);

        return new AppNotification
        {
            Title = title,
            Message = message,
            Source = "gateway",
            Category = category,
            Severity = SeverityFromGatewayType(category),
            DedupeKey = BuildDedupeKey(
                "gateway",
                notification.Type,
                notification.Title,
                notification.Message,
                notification.SessionKey),
            ActionLabel = hasChatAction ? chatActionLabel : null,
            ActionRoute = hasChatAction ? GetChatActionRoute(notification.SessionKey) : null
        };
    }

    public static AppNotification FromNodeSystemNotification(SystemNotifyArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var title = NormalizeTitle(args.Title);
        var message = NormalizeMessage(args.Body, title);

        return new AppNotification
        {
            Title = title,
            Message = message,
            Source = "node",
            Category = "system.notify",
            Severity = SeverityFromText(title, message),
            DedupeKey = BuildDedupeKey("node-system-notify", args.Title, args.Body)
        };
    }

    public static AppNotification FromNodeActivity(
        string title,
        string message,
        string category,
        AppNotificationSeverity severity,
        string dedupeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupeKey);

        var normalizedTitle = NormalizeTitle(title);
        return new AppNotification
        {
            Title = normalizedTitle,
            Message = NormalizeMessage(message, normalizedTitle),
            Source = "node",
            Category = category.Trim(),
            Severity = severity,
            DedupeKey = dedupeKey.Trim()
        };
    }

    private static AppNotificationSeverity SeverityFromGatewayType(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "error" => AppNotificationSeverity.Error,
        "urgent" or "health" => AppNotificationSeverity.Warning,
        _ => AppNotificationSeverity.Informational
    };

    private static AppNotificationSeverity SeverityFromText(string title, string message)
    {
        var text = string.Concat(title, " ", message);
        return text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("denied", StringComparison.OrdinalIgnoreCase)
                ? AppNotificationSeverity.Error
                : AppNotificationSeverity.Informational;
    }

    private static string NormalizeTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "OpenClaw" : title.Trim();

    private static string NormalizeMessage(string? message, string title) =>
        string.IsNullOrWhiteSpace(message) ? title : message.Trim();

    private static string NormalizeCategory(string? category) =>
        string.IsNullOrWhiteSpace(category) ? "info" : category.Trim();

    private static string GetChatActionRoute(string? sessionKey) =>
        string.IsNullOrWhiteSpace(sessionKey)
            ? "chat"
            : AppNotificationActionRoutes.Chat(sessionKey);

    private static string BuildDedupeKey(string scope, params string?[] parts)
    {
        var raw = string.Join("\u001f", parts.Select(part => part?.Trim() ?? string.Empty));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        return $"{scope}:{hash}";
    }
}
