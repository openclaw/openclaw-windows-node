using Microsoft.Win32;
using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

/// <summary>
/// Handles openclaw:// deep link URI scheme registration and processing.
/// </summary>
public static class DeepLinkHandler
{
    private const string UriScheme = "openclaw";
    private const string UriSchemeKey = @"SOFTWARE\Classes\openclaw";

    [SupportedOSPlatform("windows")]
    public static void RegisterUriScheme()
    {
        // MSIX-packaged apps declare the protocol in Package.appxmanifest — skip registry
        if (IsPackagedApp())
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

#if OPENCLAW_TRAY_TESTS
    private static bool IsPackagedApp() => false;
#else
    private static bool IsPackagedApp() => OpenClawTray.Helpers.PackageHelper.IsPackaged;
#endif

    public static void Handle(string uri, DeepLinkActions actions)
    {
        var result = OpenClaw.Shared.DeepLinkParser.ParseDeepLink(uri);
        if (result == null)
            return;

        var path = result.Path?.TrimEnd('/') ?? string.Empty;

        Logger.Info($"Handling deep link: {path}");

        switch (path.ToLowerInvariant())
        {
            case "settings":
                actions.OpenHub?.Invoke("settings");
                break;

            case "chat":
                actions.OpenHub?.Invoke("chat");
                break;

            case "activity":
                actions.OpenActivityStream?.Invoke(result.Parameters.GetValueOrDefault("filter"));
                break;

            case "history":
                actions.OpenActivityStream?.Invoke("notification");
                break;

            case "commandcenter":
                actions.OpenHub?.Invoke("connection");
                break;

            case "setup":
                actions.OpenSetup?.Invoke();
                break;

            case "health":
            case "healthcheck":
            case "health-check":
                if (actions.RunHealthCheck != null)
                {
                    _ = Task.Run(actions.RunHealthCheck);
                }
                break;

            case "updates":
            case "update":
            case "check-updates":
            case "update-check":
                if (actions.CheckForUpdates != null)
                {
                    _ = actions.CheckForUpdates();
                }
                break;

            case "log":
            case "logs":
            case "log-file":
                actions.OpenLogFile?.Invoke();
                break;

            case "log-folder":
            case "logs-folder":
                actions.OpenLogFolder?.Invoke();
                break;

            case "config":
            case "config-folder":
            case "settings-folder":
                actions.OpenConfigFolder?.Invoke();
                break;

            case "diagnostics":
            case "diagnostics-folder":
                actions.OpenDiagnosticsFolder?.Invoke();
                break;

            case "support":
            case "support-context":
                actions.CopySupportContext?.Invoke();
                break;

            case "debug-bundle":
            case "diagnostics-bundle":
            case "support-bundle":
                actions.CopyDebugBundle?.Invoke();
                break;

            case "browser-setup":
            case "browser-guidance":
            case "browser-proxy-setup":
                actions.CopyBrowserSetupGuidance?.Invoke();
                break;

            case "ports":
            case "port-diagnostics":
            case "copy-port-diagnostics":
                actions.CopyPortDiagnostics?.Invoke();
                break;

            case "capabilities":
            case "capability-diagnostics":
            case "copy-capability-diagnostics":
                actions.CopyCapabilityDiagnostics?.Invoke();
                break;

            case "nodes":
            case "node-inventory":
            case "copy-node-inventory":
                actions.CopyNodeInventory?.Invoke();
                break;

            case "channels":
            case "channel-summary":
            case "copy-channel-summary":
                actions.CopyChannelSummary?.Invoke();
                break;

            case "activity-summary":
            case "copy-activity-summary":
                actions.CopyActivitySummary?.Invoke();
                break;

            case "extensibility":
            case "extensibility-summary":
            case "copy-extensibility-summary":
                actions.CopyExtensibilitySummary?.Invoke();
                break;

            case "ssh-restart":
            case "restart-ssh":
            case "restart-ssh-tunnel":
                actions.RestartSshTunnel?.Invoke();
                break;

            case "status":
            case "command-center":
                actions.OpenHub?.Invoke("connection");
                break;

            case "tray":
            case "tray-menu":
            case "menu":
                actions.OpenTrayMenu?.Invoke();
                break;

            case "notifications":
            case "notification-history":
                actions.OpenHub?.Invoke("activity");
                break;

            case "activity-stream":
                actions.OpenHub?.Invoke("activity");
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

            case "voice":
            case "voice-start":
                actions.OpenVoice?.Invoke();
                break;

            case "voice-stop":
                actions.StopVoice?.Invoke();
                break;

            default:
                if (path == "hub" || path.StartsWith("hub/"))
                {
                    var hubPage = path == "hub" ? null : path["hub/".Length..];
                    actions.OpenHub?.Invoke(hubPage);
                }
                else
                {
                    Logger.Warn($"Unknown deep link path: {path}");
                }
                break;
        }
    }
}

public class DeepLinkActions
{
    public Action? OpenSettings { get; set; }
    public Action? OpenSetup { get; set; }
    public Func<Task>? RunHealthCheck { get; set; }
    public Func<Task>? CheckForUpdates { get; set; }
    public Action? OpenLogFile { get; set; }
    public Action? OpenLogFolder { get; set; }
    public Action? OpenConfigFolder { get; set; }
    public Action? OpenDiagnosticsFolder { get; set; }
    public Action? OpenConnectionStatus { get; set; }
    public Action? CopySupportContext { get; set; }
    public Action? CopyDebugBundle { get; set; }
    public Action? CopyBrowserSetupGuidance { get; set; }
    public Action? CopyPortDiagnostics { get; set; }
    public Action? CopyCapabilityDiagnostics { get; set; }
    public Action? CopyNodeInventory { get; set; }
    public Action? CopyChannelSummary { get; set; }
    public Action? CopyActivitySummary { get; set; }
    public Action? CopyExtensibilitySummary { get; set; }
    public Action? RestartSshTunnel { get; set; }
    public Action? OpenChat { get; set; }
    public Action? OpenCommandCenter { get; set; }
    public Action? OpenTrayMenu { get; set; }
    public Action<string?>? OpenActivityStream { get; set; }
    public Action? OpenNotificationHistory { get; set; }
    public Action<string?>? OpenDashboard { get; set; }
    public Action<string?>? OpenQuickSend { get; set; }
    public Action<string?>? OpenHub { get; set; }
    public Func<string, Task>? SendMessage { get; set; }
    public Action? OpenVoice { get; set; }
    public Action? StopVoice { get; set; }
}
