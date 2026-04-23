using Microsoft.Win32;
using System;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

/// <summary>
/// Handles openclaw:// deep link URI scheme registration and processing.
/// </summary>
public static class DeepLinkHandler
{
    private const string UriScheme = "openclaw";
    private const string UriSchemeKey = @"SOFTWARE\Classes\openclaw";

    public static void RegisterUriScheme()
    {
        // MSIX-packaged apps declare the protocol in Package.appxmanifest — skip registry
        if (Helpers.PackageHelper.IsPackaged)
        {
            Logger.Info("URI scheme handled by MSIX manifest (packaged mode)");
            return;
        }

        try
        {
            var exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

            using var key = Registry.CurrentUser.CreateSubKey(UriSchemeKey);
            key?.SetValue("", "URL:OpenClaw Protocol");
            key?.SetValue("URL Protocol", "");

            using var iconKey = key?.CreateSubKey("DefaultIcon");
            iconKey?.SetValue("", $"\"{exePath}\",0");

            using var commandKey = key?.CreateSubKey(@"shell\open\command");
            commandKey?.SetValue("", $"\"{exePath}\" \"%1\"");

            Logger.Info("URI scheme registered: openclaw://");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to register URI scheme: {ex.Message}");
        }
    }

    public static void Handle(string uri, DeepLinkActions actions)
    {
        var result = OpenClaw.Shared.DeepLinkParser.ParseDeepLink(uri);
        if (result == null)
            return;

        var path = result.Path;

        Logger.Info($"Handling deep link: {path}");

        switch (path.ToLowerInvariant())
        {
            case "settings":
                actions.OpenSettings?.Invoke();
                break;

            case "setup":
                actions.OpenSetup?.Invoke();
                break;

            case "chat":
                actions.OpenChat?.Invoke();
                break;

            case "dashboard":
                actions.OpenDashboard?.Invoke(null);
                break;

            case var p when p.StartsWith("dashboard/"):
                var dashboardPath = p["dashboard/".Length..];
                actions.OpenDashboard?.Invoke(dashboardPath);
                break;

            case "send":
                var sendMessage = result.Parameters.GetValueOrDefault("message");
                actions.OpenQuickSend?.Invoke(sendMessage);
                break;

            case "agent":
                var agentMessage = result.Parameters.GetValueOrDefault("message");
                if (!string.IsNullOrEmpty(agentMessage) && actions.SendMessage != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await actions.SendMessage(agentMessage);
                            Logger.Info($"Sent message via deep link: {agentMessage}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Failed to send message: {ex.Message}");
                        }
                    });
                }
                else if (!string.IsNullOrEmpty(agentMessage))
                {
                    Logger.Warn("Deep link: agent message received but SendMessage handler is not registered");
                }
                break;

            default:
                Logger.Warn($"Unknown deep link path: {path}");
                break;
        }
    }
}

public class DeepLinkActions
{
    public Action? OpenSettings { get; set; }
    public Action? OpenSetup { get; set; }
    public Action? OpenChat { get; set; }
    public Action<string?>? OpenDashboard { get; set; }
    public Action<string?>? OpenQuickSend { get; set; }
    public Func<string, Task>? SendMessage { get; set; }
}
