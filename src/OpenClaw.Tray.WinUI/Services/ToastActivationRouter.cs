namespace OpenClawTray.Services;

public static class ToastActivationRouter
{
    internal static ActivationRoute? PlanRoute(string? action, Func<string, string?> getArgument)
    {
        ArgumentNullException.ThrowIfNull(getArgument);

        switch (action)
        {
            case "open_url":
                var url = getArgument("url");
                return string.IsNullOrWhiteSpace(url) ? null : new ActivationRoute.OpenUrl(url);
            case "open_dashboard":
                return new ActivationRoute.OpenDashboard(null);
            case "open_settings":
                return new ActivationRoute.OpenHub("settings");
            case "open_chat":
                return new ActivationRoute.OpenChat(getArgument("sessionKey"));
            case "open_activity":
                return new ActivationRoute.OpenHub("channels");
            case "copy_pairing_command":
                var command = getArgument("command");
                return string.IsNullOrWhiteSpace(command) ? null : new ActivationRoute.CopyPairingCommand(command);
            case "review_pairing":
                return new ActivationRoute.ReviewPairing();
            default:
                return null;
        }
    }

}
