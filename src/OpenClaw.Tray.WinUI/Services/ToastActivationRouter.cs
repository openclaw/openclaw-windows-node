namespace OpenClawTray.Services;

public sealed class ToastActivationActions
{
    public required Action<string> OpenUrl { get; init; }
    public required Action OpenDashboard { get; init; }
    public required Action OpenSettings { get; init; }
    public required Action<string?> OpenChat { get; init; }
    public required Action OpenActivity { get; init; }
    public required Action<string> CopyPairingCommand { get; init; }
    public required Action ReviewPairing { get; init; }
}

public static class ToastActivationRouter
{
    /// <summary>
    /// Compatibility entry point kept for existing callback-based callers/tests. Production
    /// activation goes through <see cref="PlanRoute"/> plus App's single
    /// <c>IActivationPlanSink</c> switch; this method only translates the same plan into the
    /// legacy <see cref="ToastActivationActions"/> shape so it must not gain its own mapping.
    /// </summary>
    public static void Route(
        string? action,
        Func<string, string?> getArgument,
        ToastActivationActions actions)
    {
        ArgumentNullException.ThrowIfNull(getArgument);
        ArgumentNullException.ThrowIfNull(actions);

        var route = PlanRoute(action, getArgument);
        if (route != null)
            Apply(route, actions);
    }

    /// <summary>The single toast-action route table. Toast routes are never state-changing.</summary>
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

    private static void Apply(ActivationRoute route, ToastActivationActions actions)
    {
        switch (route)
        {
            case ActivationRoute.OpenUrl r:
                actions.OpenUrl(r.Uri);
                break;
            case ActivationRoute.OpenDashboard:
                actions.OpenDashboard();
                break;
            case ActivationRoute.OpenChat r:
                actions.OpenChat(r.SessionKey);
                break;
            case ActivationRoute.CopyPairingCommand r:
                actions.CopyPairingCommand(r.Command);
                break;
            case ActivationRoute.ReviewPairing:
                actions.ReviewPairing();
                break;
            case ActivationRoute.OpenHub { Page: "settings" }:
                actions.OpenSettings();
                break;
            case ActivationRoute.OpenHub { Page: "channels" }:
                actions.OpenActivity();
                break;
        }
    }
}
