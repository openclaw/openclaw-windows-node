using Microsoft.Win32;
using System;
using System.Runtime.Versioning;

namespace OpenClawTray.Services;

/// <summary>
/// Handles this build variant's deep link URI scheme registration and processing.
/// </summary>
public static class DeepLinkHandler
{
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

            var uriSchemeKey = $@"SOFTWARE\Classes\{AppIdentity.ProtocolScheme}";
            using var key = Registry.CurrentUser.CreateSubKey(uriSchemeKey);
            key?.SetValue("", "URL:OpenClaw Protocol");
            key?.SetValue("URL Protocol", "");

            using var iconKey = key?.CreateSubKey("DefaultIcon");
            iconKey?.SetValue("", $"\"{exePath}\",0");

            using var commandKey = key?.CreateSubKey(@"shell\open\command");
            commandKey?.SetValue("", $"\"{exePath}\" \"%1\"");

            Logger.Info($"URI scheme registered: {AppIdentity.ProtocolScheme}://");
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

    internal static ActivationRoute? PlanRoute(string uri, string scheme)
    {
        var result = OpenClaw.Shared.DeepLinkParser.ParseDeepLink(uri, scheme);
        if (result == null)
            return null;

        var path = result.Path?.TrimEnd('/') ?? string.Empty;

        Logger.Info($"Handling deep link: {DeepLinkSecurityPolicy.RedactForLog(uri)}");

        switch (path.ToLowerInvariant())
        {
            case "settings":
                return new ActivationRoute.OpenHub("settings");

            case "chat":
                return new ActivationRoute.OpenHub("chat");

            case "activity":
                // ActivityPage was removed. Redirect by filter: channel events
                // now live on the Channels page; sessions/usage/nodes have their
                // own dedicated pages; notifications fall through to Channels.
                {
                    var filter = result.Parameters.GetValueOrDefault("filter");
                    return new ActivationRoute.OpenHub(filter switch
                    {
                        "session" => "sessions",
                        "usage" => "usage",
                        "node" => "instances",
                        _ => "channels",
                    });
                }

            case "history":
                // Legacy notification-history alias — Channels page is the closest match.
                return new ActivationRoute.OpenHub("channels");

            case "commandcenter":
                return new ActivationRoute.OpenHub("connection");

            case "setup":
                return new ActivationRoute.OpenSetup();

            case "health":
            case "healthcheck":
            case "health-check":
                return new ActivationRoute.RunHealthCheck();

            case "updates":
            case "update":
            case "check-updates":
            case "update-check":
                return new ActivationRoute.CheckForUpdates();

            case "log":
            case "logs":
            case "log-file":
                return new ActivationRoute.OpenLogFile();

            case "log-folder":
            case "logs-folder":
                return new ActivationRoute.OpenLogFolder();

            case "config":
            case "config-folder":
            case "settings-folder":
                return new ActivationRoute.OpenConfigFolder();

            case "diagnostics":
            case "diagnostics-folder":
                return new ActivationRoute.OpenDiagnosticsFolder();

            case "support":
            case "support-context":
                return new ActivationRoute.CopyDiagnostics(DiagnosticsCopyKind.SupportContext);

            case "debug-bundle":
            case "diagnostics-bundle":
            case "support-bundle":
                return new ActivationRoute.CopyDiagnostics(DiagnosticsCopyKind.DebugBundle);

            case "browser-setup":
            case "browser-guidance":
            case "browser-proxy-setup":
                return new ActivationRoute.CopyDiagnostics(DiagnosticsCopyKind.BrowserSetupGuidance);

            case "ports":
            case "port-diagnostics":
            case "copy-port-diagnostics":
                return new ActivationRoute.CopyDiagnostics(DiagnosticsCopyKind.PortDiagnostics);

            case "capabilities":
            case "capability-diagnostics":
            case "copy-capability-diagnostics":
                return new ActivationRoute.CopyDiagnostics(DiagnosticsCopyKind.CapabilityDiagnostics);

            case "nodes":
            case "node-inventory":
            case "copy-node-inventory":
                return new ActivationRoute.CopyDiagnostics(DiagnosticsCopyKind.NodeInventory);

            case "channels":
            case "channel-summary":
            case "copy-channel-summary":
                return new ActivationRoute.CopyDiagnostics(DiagnosticsCopyKind.ChannelSummary);

            case "activity-summary":
            case "copy-activity-summary":
                return new ActivationRoute.CopyDiagnostics(DiagnosticsCopyKind.ActivitySummary);

            case "extensibility":
            case "extensibility-summary":
            case "copy-extensibility-summary":
                return new ActivationRoute.CopyDiagnostics(DiagnosticsCopyKind.ExtensibilitySummary);

            case "ssh-restart":
            case "restart-ssh":
            case "restart-ssh-tunnel":
                return new ActivationRoute.RestartSshTunnel();

            case "status":
            case "command-center":
                return new ActivationRoute.OpenHub("connection");

            case "tray":
            case "tray-menu":
            case "menu":
                return new ActivationRoute.OpenTrayMenu();

            case "notifications":
            case "notification-history":
            case "activity-stream":
                // ActivityPage removed — channel events now live on the Channels page.
                return new ActivationRoute.OpenHub("channels");

            case "dashboard":
                return new ActivationRoute.OpenDashboard(null);

            case var p when p.StartsWith("dashboard/"):
                return new ActivationRoute.OpenDashboard(p["dashboard/".Length..]);

            case "agent":
                var agentMessage = result.Parameters.GetValueOrDefault("message");
                return string.IsNullOrEmpty(agentMessage) ? null : new ActivationRoute.SendMessage(agentMessage);

            case "voice":
            case "voice-start":
                return new ActivationRoute.OpenVoice();

            case "voice-stop":
                return new ActivationRoute.StopVoice();

            default:
                if (path == "hub" || path.StartsWith("hub/"))
                {
                    var hubPage = path == "hub" ? null : path["hub/".Length..];
                    return new ActivationRoute.OpenHub(hubPage);
                }

                Logger.Warn($"Unknown deep link path: {path}");
                return null;
        }
    }

}
