using OpenClawTray.Services;

namespace OpenClawTray.Presentation.Usage;

/// <summary>
/// The Usage page's two handoffs, routed through the app's existing navigation owners. The browser opens the Gateway
/// Control UI <c>/usage</c> route through <see cref="IAppCommands.OpenDashboard"/>, which resolves credentials, keeps the
/// Gateway's mount path and reports launch failures. The web page keeps its range and selection in memory, so no range
/// or session is passed. A conversation opens in native chat by handing its key over as the pending chat session, the
/// same path Sessions, Cron and Notifications use.
/// </summary>
internal static class UsageHandoffs
{
    public const string WebUsagePath = "usage";
    public const string ChatPageTag = "chat";

    public static (Action OpenWebUsage, Action<string> OpenConversation) For(
        IAppCommands commands, Action<string> setPendingChatSessionKey)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(setPendingChatSessionKey);
        return (
            () => commands.OpenDashboard(WebUsagePath),
            sessionKey =>
            {
                if (string.IsNullOrWhiteSpace(sessionKey)) return;
                setPendingChatSessionKey(sessionKey);
                commands.Navigate(ChatPageTag);
            });
    }
}
