using OpenClaw.Shared;

namespace OpenClawTray.Services;

internal static class ChatNotificationSpeechText
{
    public static string Resolve(OpenClawNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return string.IsNullOrEmpty(notification.FullMessage)
            ? notification.Message
            : notification.FullMessage;
    }
}
