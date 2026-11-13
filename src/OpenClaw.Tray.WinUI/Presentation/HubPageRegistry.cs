// <summary>
// Defines Hub page identities, command metadata, route normalization, and command search.
// UI hosts resolve tags through this registry instead of duplicating navigation conventions.
// Usage:
//   HubPageKind? page = HubPageRegistry.ResolvePage("local-ai");
//   var commands = HubPageRegistry.BuildCommands(context);
// </summary>

using System.Collections.Immutable;
using System.Globalization;
using OpenClawTray.Helpers;
#if !OPENCLAW_TRAY_TESTS
using OpenClawTray.Pages;
#endif

namespace OpenClawTray.Presentation;

/// <summary>Enumerates pages available from the companion Hub.</summary>
internal enum HubPageKind
{
    Connection,
    LocalAi,
    DefaultModel,
    Systems,
    Config,
    Usage,
    Permissions,
    Voice,
    Sandbox,
    Settings,
    About,
    Notifications,
    Debug,
    Sessions,
    Cron,
    Workspace,
    Dashboards
}

internal enum HubCommandActionKind
{
    Navigate,
    OpenDashboard,
    ToggleSetting
}

internal enum HubSettingToggle
{
    NodeMode,
    Camera,
    Canvas,
    ScreenCapture,
    BrowserControl
}

/// <summary>Describes an action initiated by a Hub command.</summary>
internal sealed record HubCommandAction(
    HubCommandActionKind Kind,
    string? Value = null,
    HubSettingToggle? Toggle = null)
{
    /// <summary>Creates a navigation action for a companion tag.</summary>
    public static HubCommandAction Navigate(string tag) =>
        new(HubCommandActionKind.Navigate, tag);

    /// <summary>Creates an action that opens a Dashboard path.</summary>
    public static HubCommandAction OpenDashboard(string? path = null) =>
        new(HubCommandActionKind.OpenDashboard, path);

    /// <summary>Creates an action that toggles a Hub setting.</summary>
    public static HubCommandAction ToggleSetting(HubSettingToggle toggle) =>
        new(HubCommandActionKind.ToggleSetting, Toggle: toggle);
}

internal sealed record HubCommand(
    string Icon,
    string Title,
    string? Subtitle,
    HubCommandAction Action)
{
    public override string ToString() => Title;
}

internal sealed record HubCommandToggleState(
    bool NodeMode,
    bool Camera,
    bool Canvas,
    bool ScreenCapture,
    bool BrowserControl);

internal sealed record HubCommandContext(
    string CurrentAgentId,
    bool DiagnosticsVisible,
    HubCommandToggleState? Toggles,
    ImmutableArray<string> SessionKeys,
    ImmutableDictionary<string, string> Resources);

/// <summary>Centralizes Hub routes, command construction, page resolution, and command search.</summary>
internal static class HubPageRegistry
{
    internal static (string TitleKey, string Icon) SearchPresentation(string tag) => tag switch
    {
        "chat" => ("HubWindow_NavigationViewItem_82.Content", FluentIconCatalog.Chat),
        "dashboard" => ("Search_Dashboard", FluentIconCatalog.Dashboard),
        "channels" => ("Search_Dashboard_Channels", FluentIconCatalog.Channels),
        _ => ResolvePage(tag) switch
        {
            HubPageKind.Connection => ("HubWindow_NavigationViewItem_88.Content", FluentIconCatalog.Plug),
            HubPageKind.LocalAi => ("HubWindow_NavigationViewItem_LocalAi.Content", FluentIconCatalog.Inference),
            HubPageKind.Sessions => ("HubWindow_NavigationViewItem_91.Content", FluentIconCatalog.Sessions),
            HubPageKind.Dashboards => ("HubWindow_NavDashboards.Content", FluentIconCatalog.Dashboard),
            HubPageKind.DefaultModel => ("HubWindow_NavigationViewItem_DefaultModel.Content", FluentIconCatalog.Settings),
            HubPageKind.Cron => ("HubWindow_NavigationViewItem_124.Content", FluentIconCatalog.Cron),
            HubPageKind.Workspace => ("WorkspacePage_TextBlock_22.Text", FluentIconCatalog.Workspace),
            HubPageKind.Systems => ("HubWindow_NavigationViewItem_112.Content", FluentIconCatalog.Devices),
            HubPageKind.Config => ("HubWindow_NavigationViewItem_118.Content", FluentIconCatalog.Settings),
            HubPageKind.Usage => ("HubWindow_NavigationViewItem_121.Content", FluentIconCatalog.Money),
            HubPageKind.Permissions => ("HubWindow_NavigationViewItem_136.Content", FluentIconCatalog.Permissions),
            HubPageKind.Settings => ("HubWindow_NavigationViewItem_133.Content", FluentIconCatalog.Settings),
            HubPageKind.About => ("HubWindow_AboutItem.Content", FluentIconCatalog.About),
            HubPageKind.Notifications => ("Onboarding_Perm_Notifications", FluentIconCatalog.Notifications),
            HubPageKind.Debug => ("HubWindow_NavigationViewItem_145.Content", FluentIconCatalog.Bug),
            _ => throw new ArgumentException("No search presentation is registered for this destination.", nameof(tag))
        }
    };

    internal static string GetNativeSetupPage(OpenClaw.SetupEngine.SetupNativeDestination destination) => destination switch
    {
        OpenClaw.SetupEngine.SetupNativeDestination.Chat => "chat",
        _ => throw new ArgumentOutOfRangeException(nameof(destination)),
    };

    /// <summary>Channel setup destinations open the Gateway dashboard instead of a native page.</summary>
    internal static string? GetNativeSetupDashboardPath(OpenClaw.SetupEngine.SetupNativeDestination destination) => destination switch
    {
        OpenClaw.SetupEngine.SetupNativeDestination.WhatsApp or OpenClaw.SetupEngine.SetupNativeDestination.Telegram or
            OpenClaw.SetupEngine.SetupNativeDestination.Channels => "channels",
        _ => null,
    };

    public static ImmutableArray<string> CommandResourceKeys { get; } =
    [
        "Command_GoToConnection_Title",
        "Command_GoToConnection_Subtitle",
        "Command_GoToLocalAi_Title",
        "Command_GoToLocalAi_Subtitle",
        "Command_GoToDefaultModel_Title",
        "Command_GoToDefaultModel_Subtitle",
        "Command_GoToChat_Title",
        "Command_GoToChat_Subtitle",
        "Command_GoToSessions_Title",
        "Command_GoToSessions_Subtitle",
        "Command_GoToDashboards_Title",
        "Command_GoToDashboards_Subtitle",
        "Command_OpenSessionDashboard_TitleFormat",
        "Command_OpenSessionDashboard_Subtitle",
        "Command_GoToCron_Title",
        "Command_GoToCron_Subtitle",
        "Command_GoToWorkspace_Title",
        "Command_GoToWorkspace_Subtitle",
        "Command_GoToChannels_Title",
        "Command_GoToChannels_Subtitle",
        "Command_GoToSystems_Title",
        "Command_GoToSystems_Subtitle",
        "Command_GoToConfig_Title",
        "Command_GoToConfig_Subtitle",
        "Command_GoToUsage_Title",
        "Command_GoToUsage_Subtitle",
        "Command_GoToPermissions_Title",
        "Command_GoToPermissions_Subtitle",
        "Command_GoToSettings_Title",
        "Command_GoToSettings_Subtitle",
        "Command_GoToNotificationPreferences_Title",
        "Command_GoToNotificationPreferences_Subtitle",
        "Command_GoToAbout_Title",
        "Command_GoToAbout_Subtitle",
        "Command_GoToNotifications_Title",
        "Command_GoToNotifications_Subtitle",
        "Command_OpenDashboard_Title",
        "Command_OpenDashboard_Subtitle",
        "Command_GoToDiagnostics_Title",
        "Command_GoToDiagnostics_Subtitle",
        "Command_Subtitle_CurrentlyOn",
        "Command_Subtitle_CurrentlyOff",
        "Command_ToggleNodeMode_Title",
        "Command_ToggleCamera_Title",
        "Command_ToggleCanvas_Title",
        "Command_ToggleScreenCapture_Title",
        "Command_ToggleBrowserControl_Title"
    ];

    /// <summary>Maps a route alias to its canonical companion tag for the current agent.</summary>
    public static string NormalizeTag(string tag, string currentAgentId) => tag switch
    {
        "home" => "connection",
        "general" => "settings",
        "about" or "info" => "about",
        "notification-preferences" => "settings",
        "nodes" or "instances" => "systems",
        "cron" => $"agent:{currentAgentId}:cron",
        "workspace" => $"agent:{currentAgentId}:workspace",
        _ => tag
    };

    /// <summary>Resolves a normalized route tag to its Hub page, or null for an unknown tag.</summary>
    public static HubPageKind? ResolvePage(string? tag) => tag switch
    {
        "connection" => HubPageKind.Connection,
        "local-ai" => HubPageKind.LocalAi,
        "model" => HubPageKind.DefaultModel,
        "systems" or "nodes" or "instances" => HubPageKind.Systems,
        "config" => HubPageKind.Config,
        "usage" => HubPageKind.Usage,
        "capabilities" or "permissions" => HubPageKind.Permissions,
        "voice" => HubPageKind.Voice,
        "sandbox" => HubPageKind.Sandbox,
        "settings" or "notification-preferences" => HubPageKind.Settings,
        "info" or "about" => HubPageKind.About,
        "notifications" => HubPageKind.Notifications,
        "debug" => HubPageKind.Debug,
        "home" => HubPageKind.Connection,
        "general" => HubPageKind.Settings,
        "conversations" or "sessions" => HubPageKind.Sessions,
        "cron" => HubPageKind.Cron,
        "workspace" => HubPageKind.Workspace,
        _ when DashboardsRoute.IsDashboardsTag(tag) => HubPageKind.Dashboards,
        _ when SystemsMachine(tag) is not null => HubPageKind.Systems,
        _ when tag?.StartsWith("agent:", StringComparison.Ordinal) == true => ResolveAgentPage(tag),
        _ => null
    };

    private const string SystemsMachinePrefix = "systems:";

    /// <summary>Machine id from a <c>systems:&lt;environment id&gt;</c> deep link, or null.</summary>
    public static string? SystemsMachine(string? tag) =>
        tag is { Length: > 8 } && tag.StartsWith(SystemsMachinePrefix, StringComparison.Ordinal)
            ? tag[SystemsMachinePrefix.Length..]
            : null;

    /// <summary>The rail item tag for a destination; machine deep links highlight Systems.</summary>
    public static string NavigationTag(string tag) => SystemsMachine(tag) is null ? tag : "systems";

    private static HubPageKind? ResolveAgentPage(string tag)
    {
        var parts = tag.Split(':');
        if (parts.Length == 2)
            return HubPageKind.Workspace;

        return parts[2] switch
        {
            "sessions" => HubPageKind.Sessions,
            "cron" => HubPageKind.Cron,
            "workspace" => HubPageKind.Workspace,
            _ => null
        };
    }

    public static string ParseAgentId(string? tag)
    {
        if (tag?.StartsWith("agent:", StringComparison.Ordinal) != true)
            return "main";

        var parts = tag.Split(':');
        return parts.Length >= 2 ? parts[1] : "main";
    }

    public static bool IsGatewayPageTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        return tag.StartsWith("agent:", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("chat", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("sessions", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("systems", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("instances", StringComparison.OrdinalIgnoreCase) ||
            SystemsMachine(tag) is not null ||
            tag.Equals("config", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("usage", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("cron", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("workspace", StringComparison.OrdinalIgnoreCase) ||
            DashboardsRoute.IsDashboardsTag(tag);
    }

    /// <summary>
    /// Pages that stay open when the Gateway disconnects. Systems keeps showing
    /// last-known machines with its own disconnected state instead of bouncing to Connection.
    /// </summary>
    public static bool ShouldKeepCurrentPageVisibleDuringDisconnect(string? currentTag) =>
        ResolvePage(currentTag?.ToLowerInvariant()) is HubPageKind.Config or HubPageKind.Cron or HubPageKind.Systems ||
        DashboardsRoute.IsDashboardsTag(currentTag);

    /// <summary>True for the Systems page, its legacy aliases and machine deep links.</summary>
    public static bool IsSystemsTag(string? tag) => ResolvePage(tag) == HubPageKind.Systems;

#if !OPENCLAW_TRAY_TESTS
    public static Type? ResolvePageType(string? tag) => ResolvePage(tag) switch
    {
        HubPageKind.Connection => typeof(ConnectionPage),
        HubPageKind.LocalAi => typeof(LocalAiPage),
        HubPageKind.DefaultModel => typeof(DefaultModelPage),
        HubPageKind.Systems => typeof(SystemsPage),
        HubPageKind.Config => typeof(ConfigPage),
        HubPageKind.Usage => typeof(UsagePage),
        HubPageKind.Permissions => typeof(PermissionsPage),
        HubPageKind.Voice => typeof(VoiceSettingsPage),
        HubPageKind.Sandbox => typeof(SandboxPage),
        HubPageKind.Settings => typeof(SettingsPage),
        HubPageKind.About => typeof(AboutPage),
        HubPageKind.Notifications => typeof(NotificationsPage),
        HubPageKind.Debug => typeof(DebugPage),
        HubPageKind.Sessions => typeof(SessionsPage),
        HubPageKind.Cron => typeof(CronPage),
        HubPageKind.Workspace => typeof(WorkspacePage),
        HubPageKind.Dashboards => typeof(DashboardsPage),
        _ => null
    };
#endif

    /// <summary>Builds the commands currently available for the supplied Hub context.</summary>
    public static ImmutableArray<HubCommand> BuildCommands(HubCommandContext context)
    {
        var commands = ImmutableArray.CreateBuilder<HubCommand>();

        AddNavigation(commands, context, "🔌", "Command_GoToConnection", "connection");
        AddNavigation(commands, context, "AI", "Command_GoToLocalAi", "local-ai");
        AddNavigation(commands, context, "AI", "Command_GoToDefaultModel", "model");
        AddNavigation(commands, context, "💬", "Command_GoToChat", "chat");
        AddNavigation(commands, context, "🧠", "Command_GoToSessions", "sessions");
        AddNavigation(commands, context, "📊", "Command_GoToDashboards", DashboardsRoute.Tag);
        commands.Add(new HubCommand(
            "🧠",
            Format(context, "Command_GoToCron_Title", context.CurrentAgentId),
            Get(context, "Command_GoToCron_Subtitle"),
            HubCommandAction.Navigate($"agent:{context.CurrentAgentId}:cron")));
        commands.Add(new HubCommand(
            "🧠",
            Format(context, "Command_GoToWorkspace_Title", context.CurrentAgentId),
            Get(context, "Command_GoToWorkspace_Subtitle"),
            HubCommandAction.Navigate($"agent:{context.CurrentAgentId}")));
        AddNavigation(commands, context, "📡", "Command_GoToChannels", "channels");
        AddNavigation(commands, context, "📡", "Command_GoToSystems", "systems");
        AddNavigation(commands, context, "📡", "Command_GoToConfig", "config");
        AddNavigation(commands, context, "📡", "Command_GoToUsage", "usage");
        AddNavigation(commands, context, "🛡️", "Command_GoToPermissions", "permissions");
        AddNavigation(commands, context, "⚙️", "Command_GoToSettings", "settings");
        AddNavigation(commands, context, "🔔", "Command_GoToNotifications", "notifications");
        AddNavigation(commands, context, "🔔", "Command_GoToNotificationPreferences", "notification-preferences");
        AddNavigation(commands, context, "ℹ️", "Command_GoToAbout", "about");
        commands.Add(new HubCommand(
            "🌐",
            Get(context, "Command_OpenDashboard_Title"),
            Get(context, "Command_OpenDashboard_Subtitle"),
            HubCommandAction.OpenDashboard()));

        if (context.DiagnosticsVisible)
            AddNavigation(commands, context, "🐛", "Command_GoToDiagnostics", "debug");

        if (context.Toggles is { } toggles)
        {
            AddToggle(commands, context, "🔌", "Command_ToggleNodeMode_Title", HubSettingToggle.NodeMode, toggles.NodeMode);
            AddToggle(commands, context, "📷", "Command_ToggleCamera_Title", HubSettingToggle.Camera, toggles.Camera);
            AddToggle(commands, context, "🎨", "Command_ToggleCanvas_Title", HubSettingToggle.Canvas, toggles.Canvas);
            AddToggle(commands, context, "🖥️", "Command_ToggleScreenCapture_Title", HubSettingToggle.ScreenCapture, toggles.ScreenCapture);
            AddToggle(commands, context, "🌐", "Command_ToggleBrowserControl_Title", HubSettingToggle.BrowserControl, toggles.BrowserControl);
        }

        foreach (var sessionKey in context.SessionKeys)
        {
            commands.Add(new HubCommand(
                "🧠",
                $"Go to session: {sessionKey}",
                "Open in dashboard",
                HubCommandAction.OpenDashboard($"sessions/{sessionKey}")));
            commands.Add(new HubCommand(
                "📊",
                Format(context, "Command_OpenSessionDashboard_TitleFormat", sessionKey),
                Get(context, "Command_OpenSessionDashboard_Subtitle"),
                HubCommandAction.Navigate(DashboardsRoute.ForSession(sessionKey))));
        }

        return commands.ToImmutable();
    }

    private static void AddNavigation(
        ImmutableArray<HubCommand>.Builder commands,
        HubCommandContext context,
        string icon,
        string resourcePrefix,
        string tag)
    {
        commands.Add(new HubCommand(
            icon,
            Get(context, resourcePrefix + "_Title"),
            Get(context, resourcePrefix + "_Subtitle"),
            HubCommandAction.Navigate(tag)));
    }

    private static void AddToggle(
        ImmutableArray<HubCommand>.Builder commands,
        HubCommandContext context,
        string icon,
        string titleResourceKey,
        HubSettingToggle toggle,
        bool isOn)
    {
        commands.Add(new HubCommand(
            icon,
            Get(context, titleResourceKey),
            Get(context, isOn ? "Command_Subtitle_CurrentlyOn" : "Command_Subtitle_CurrentlyOff"),
            HubCommandAction.ToggleSetting(toggle)));
    }

    private static string Get(HubCommandContext context, string key) =>
        context.Resources.TryGetValue(key, out var value) ? value : key;

    private static string Format(HubCommandContext context, string key, params object?[] args)
    {
        var template = Get(context, key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}
