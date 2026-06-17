namespace OpenClawTray.Services;

public sealed class ToastActivationActions
{
    public required Action<string> OpenUrl { get; init; }
    public required Action OpenDashboard { get; init; }
    public required Action OpenSettings { get; init; }
    public required Action OpenChat { get; init; }
    public required Action OpenActivity { get; init; }
    public required Action<string> CopyPairingCommand { get; init; }
    public required Action ReviewPairing { get; init; }
}

public static class ToastActivationRouter
{
    public static void Route(
        string? action,
        Func<string, string?> getArgument,
        ToastActivationActions actions)
    {
        ArgumentNullException.ThrowIfNull(getArgument);
        ArgumentNullException.ThrowIfNull(actions);

        switch (action)
        {
            case "open_url":
                var url = getArgument("url");
                if (!string.IsNullOrWhiteSpace(url))
                    actions.OpenUrl(url);
                break;
            case "open_dashboard":
                actions.OpenDashboard();
                break;
            case "open_settings":
                actions.OpenSettings();
                break;
            case "open_chat":
                actions.OpenChat();
                break;
            case "open_activity":
                actions.OpenActivity();
                break;
            case "copy_pairing_command":
                var command = getArgument("command");
                if (!string.IsNullOrWhiteSpace(command))
                    actions.CopyPairingCommand(command);
                break;
            case "review_pairing":
                actions.ReviewPairing();
                break;
        }
    }
}
