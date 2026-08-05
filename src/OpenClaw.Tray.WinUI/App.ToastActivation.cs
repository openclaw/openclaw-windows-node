using Microsoft.Toolkit.Uwp.Notifications;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using System.Diagnostics;
using System.Threading;

namespace OpenClawTray;

public partial class App
{
    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat args)
    {
        var activationRouter = _activationRouter;
        if (activationRouter == null)
            return;

        var plan = activationRouter.PlanToast(args.Argument);
        ObserveBackgroundFault(
            activationRouter.DispatchPlanAsync(plan, this, CancellationToken.None),
            "[App] Toast activation dispatch failed");
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
