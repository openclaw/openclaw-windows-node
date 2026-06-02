using Microsoft.Toolkit.Uwp.Notifications;
using OpenClaw.Shared;
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
                        Logger.Warn($"Toast activation failed to open URL '{SanitizeToastUrlForLog(url)}': {ex.Message}");
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

    private static string SanitizeToastUrlForLog(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        var sanitized = TokenSanitizer.Sanitize(url.Trim());
        if (!Uri.TryCreate(sanitized, UriKind.Absolute, out var uri))
            return sanitized.Length <= 80 ? sanitized : $"{sanitized[..80]}...";

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        var safe = builder.Uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.SafeUnescaped);
        if (!string.IsNullOrEmpty(uri.Query))
            safe += "?[redacted]";
        if (!string.IsNullOrEmpty(uri.Fragment))
            safe += "#[redacted]";
        return safe;
    }

    public static void CopyTextToClipboard(string text)
    {
        ClipboardHelper.CopyText(text);
    }
}
