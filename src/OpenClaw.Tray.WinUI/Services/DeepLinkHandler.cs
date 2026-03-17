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
        if (!uri.StartsWith("openclaw://", StringComparison.OrdinalIgnoreCase))
            return;

        var path = uri["openclaw://".Length..].TrimEnd('/');
        var queryIndex = path.IndexOf('?');
        var query = queryIndex >= 0 ? path[(queryIndex + 1)..] : "";
        path = queryIndex >= 0 ? path[..queryIndex] : path;

        Logger.Info($"Handling deep link: {path}");

        switch (path.ToLowerInvariant())
        {
            case "settings":
                actions.OpenSettings?.Invoke();
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
                var sendMessage = GetQueryParam(query, "message");
                actions.OpenQuickSend?.Invoke(sendMessage);
                break;

            case "agent":
                var agentMessage = GetQueryParam(query, "message");
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

    private static string? GetQueryParam(string query, string key)
    {
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(kv[1]);
            }
        }
        return null;
    }
}

public class DeepLinkActions
{
    public Action? OpenSettings { get; set; }
    public Action? OpenChat { get; set; }
    public Action<string?>? OpenDashboard { get; set; }
    public Action<string?>? OpenQuickSend { get; set; }
    public Func<string, Task>? SendMessage { get; set; }
}
