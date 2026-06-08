using Microsoft.Toolkit.Uwp.Notifications;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System.Diagnostics;

namespace OpenClawTray;

public partial class App
{
    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat args)
    {
        var arguments = ToastArguments.Parse(args.Argument);
        var action = GetToastArgument(arguments, "action");

        OnUiThread(() => ToastActivationRouter.Route(
            action,
            key => GetToastArgument(arguments, key),
            new ToastActivationActions
            {
                OpenUrl = url =>
                {
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Toast URL activation failed: {ex.Message}");
                    }
                },
                OpenDashboard = () => OpenDashboard(),
                OpenSettings = ShowSettings,
                OpenChat = ShowWebChat,
                OpenActivity = () => ShowHub("channels"),
                CopyPairingCommand = command =>
                {
                    CopyTextToClipboard(command);
                    _toastService!.ShowToast(new ToastContentBuilder()
                        .AddText(LocalizationHelper.GetString("Toast_PairingCommandCopied"))
                        .AddText(command));
                }
            }));
    }

    private static string? GetToastArgument(ToastArguments arguments, string key)
    {
        return arguments.TryGetValue(key, out var value)
            ? value
            : null;
    }

    public static void CopyTextToClipboard(string text)
    {
        ClipboardHelper.CopyText(text);
    }
}
