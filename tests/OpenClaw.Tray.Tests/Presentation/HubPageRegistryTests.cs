using System.Collections.Immutable;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class HubPageRegistryTests
{
    [Theory]
    [InlineData("home", "connection")]
    [InlineData("general", "settings")]
    [InlineData("about", "about")]
    [InlineData("info", "about")]
    [InlineData("notification-preferences", "settings")]
    [InlineData("nodes", "systems")]
    [InlineData("instances", "systems")]
    [InlineData("cron", "agent:alpha:cron")]
    [InlineData("workspace", "agent:alpha:workspace")]
    [InlineData("HOME", "HOME")]
    [InlineData("Cron", "Cron")]
    public void NormalizeTag_PreservesExactCaseSensitiveAliases(string tag, string expected)
    {
        Assert.Equal(expected, HubPageRegistry.NormalizeTag(tag, "alpha"));
    }

    [Theory]
    [InlineData("local-ai", (int)HubPageKind.LocalAi)]
    [InlineData("model", (int)HubPageKind.DefaultModel)]
    [InlineData("nodes", (int)HubPageKind.Systems)]
    [InlineData("instances", (int)HubPageKind.Systems)]
    [InlineData("systems", (int)HubPageKind.Systems)]
    [InlineData("systems:node:studio", (int)HubPageKind.Systems)]
    [InlineData("capabilities", (int)HubPageKind.Permissions)]
    [InlineData("permissions", (int)HubPageKind.Permissions)]
    [InlineData("conversations", (int)HubPageKind.Sessions)]
    [InlineData("settings", (int)HubPageKind.Settings)]
    [InlineData("general", (int)HubPageKind.Settings)]
    [InlineData("notification-preferences", (int)HubPageKind.Settings)]
    [InlineData("info", (int)HubPageKind.About)]
    [InlineData("about", (int)HubPageKind.About)]
    [InlineData("agent:main", (int)HubPageKind.Workspace)]
    [InlineData("agent:main:sessions", (int)HubPageKind.Sessions)]
    [InlineData("agent:main:cron", (int)HubPageKind.Cron)]
    [InlineData("agent:main:workspace", (int)HubPageKind.Workspace)]
    [InlineData("dashboards", (int)HubPageKind.Dashboards)]
    [InlineData("dashboards?session=agent%3Amain%3Adashboard%3Aops", (int)HubPageKind.Dashboards)]
    public void ResolvePage_OwnsDirectLegacyAndAgentMappings(string tag, int expected)
    {
        Assert.Equal((HubPageKind)expected, HubPageRegistry.ResolvePage(tag));
    }

    [Theory]
    [InlineData("systems:node:studio", "node:studio", "systems")]
    [InlineData("systems:gateway", "gateway", "systems")]
    [InlineData("systems:", null, "systems:")]
    [InlineData("systems", null, "systems")]
    [InlineData("SYSTEMS:gateway", null, "SYSTEMS:gateway")]
    public void SystemsMachineDeepLink_SelectsAMachineAndHighlightsSystems(string tag, string? machine, string navTag)
    {
        Assert.Equal(machine, HubPageRegistry.SystemsMachine(tag));
        Assert.Equal(navTag, HubPageRegistry.NavigationTag(tag));
    }

    [Theory]
    [InlineData("bindings")]
    [InlineData("chat")]
    [InlineData("channels")]
    [InlineData("activity")]
    [InlineData("PERMISSIONS")]
    [InlineData("Agent:main")]
    [InlineData("agent:main:unknown")]
    [InlineData("skills")]
    [InlineData("agent:main:skills")]
    [InlineData("agentevents")]
    [InlineData("agent:main:agentevents")]
    [InlineData("unknown")]
    [InlineData("Dashboards")]
    [InlineData("")]
    [InlineData(null)]
    public void ResolvePage_RejectsUnknownOrWrongCaseTags(string? tag)
    {
        Assert.Null(HubPageRegistry.ResolvePage(tag));
    }

    [Theory]
    [InlineData("agent:main")]
    [InlineData("AGENT:MAIN")]
    [InlineData("chat")]
    [InlineData("SESSIONS")]
    [InlineData("instances")]
    [InlineData("systems")]
    [InlineData("config")]
    [InlineData("usage")]
    [InlineData("cron")]
    [InlineData("workspace")]
    [InlineData("dashboards")]
    [InlineData("dashboards?session=agent%3Amain%3Amain")]
    public void GatewayClassification_PreservesCaseInsensitiveCurrentSet(string tag)
    {
        Assert.True(HubPageRegistry.IsGatewayPageTag(tag));
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("channels")]
    [InlineData("permissions")]
    [InlineData("local-ai")]
    [InlineData("model")]
    [InlineData("debug")]
    [InlineData("skills")]
    [InlineData("bindings")]
    [InlineData("agentevents")]
    [InlineData("BINDINGS")]
    [InlineData(null)]
    public void GatewayClassification_RejectsNonGatewayTags(string? tag)
    {
        Assert.False(HubPageRegistry.IsGatewayPageTag(tag));
    }

    [Theory]
    [InlineData("config", true)]
    [InlineData("CONFIG", true)]
    [InlineData("cron", true)]
    [InlineData("CRON", true)]
    [InlineData("agent:main:cron", true)]
    [InlineData("agent:research:cron", true)]
    [InlineData("dashboards", true)]
    [InlineData("dashboards?session=agent%3Amain%3Amain", true)]
    [InlineData("systems", true)]
    [InlineData("instances", true)]
    [InlineData("nodes", true)]
    [InlineData("systems:node:studio", true)]
    [InlineData("channels", false)]
    [InlineData(null, false)]
    public void KeepVisibleDuringDisconnect_PreservesPagesWithOfflineRecovery(string? tag, bool expected)
    {
        Assert.Equal(expected, HubPageRegistry.ShouldKeepCurrentPageVisibleDuringDisconnect(tag));
    }

    [Fact]
    public void BuildCommands_PreservesBaseOrderActionsIconsAndResourceKeys()
    {
        var commands = HubPageRegistry.BuildCommands(Context());

        Assert.Equal(
            new string?[]
            {
                "connection", "local-ai", "model", "chat", "sessions", "dashboards",
                "agent:alpha:cron", "agent:alpha", "channels", "systems", "config",
                "usage", "permissions", "settings", "notifications", "notification-preferences",
                "about", null
            },
            commands.Select(CommandValue).ToArray());
        Assert.Equal(
            [
                "🔌", "AI", "AI", "💬", "🧠", "📊", "🧠", "🧠",
                "📡", "📡", "📡", "📡", "🛡️", "⚙️", "🔔", "🔔", "ℹ️", "🌐"
            ],
            commands.Select(command => command.Icon).ToArray());
        Assert.Equal("Command_GoToConnection_Title", commands[0].Title);
        Assert.Equal("Command_GoToConnection_Subtitle", commands[0].Subtitle);
        Assert.Equal("Cron alpha", commands[6].Title);
        Assert.Equal(HubCommandActionKind.OpenDashboard, commands[17].Action.Kind);
        Assert.Null(commands[17].Action.Value);
        Assert.Single(commands, command => command.Action == HubCommandAction.Navigate("chat"));
    }

    [Fact]
    public void BuildCommands_DiagnosticToggleAndSessionsPreserveInsertionOrderAndSemantics()
    {
        var commands = HubPageRegistry.BuildCommands(Context(
            diagnosticsVisible: true,
            toggles: new HubCommandToggleState(true, false, true, false, true),
            sessions: ["session/z", "session/a"]));

        Assert.Equal("debug", CommandValue(commands[18]));
        Assert.Equal(
            [
                HubSettingToggle.NodeMode,
                HubSettingToggle.Camera,
                HubSettingToggle.Canvas,
                HubSettingToggle.ScreenCapture,
                HubSettingToggle.BrowserControl
            ],
            commands.Skip(19).Take(5).Select(command => command.Action.Toggle).ToArray());
        Assert.Equal(
            new string?[] { "Command_Subtitle_CurrentlyOn", "Command_Subtitle_CurrentlyOff", "Command_Subtitle_CurrentlyOn", "Command_Subtitle_CurrentlyOff", "Command_Subtitle_CurrentlyOn" },
            commands.Skip(19).Take(5).Select(command => command.Subtitle).ToArray());
        var sessionCommands = commands.TakeLast(4).ToArray();
        Assert.Equal(
            ["Go to session: session/z", "Command_OpenSessionDashboard_TitleFormat", "Go to session: session/a", "Command_OpenSessionDashboard_TitleFormat"],
            sessionCommands.Select(command => command.Title).ToArray());
        Assert.Equal(
            new string?[] { "sessions/session/z", "dashboards?session=session%2Fz", "sessions/session/a", "dashboards?session=session%2Fa" },
            sessionCommands.Select(command => command.Action.Value).ToArray());
        Assert.Equal(
            [HubCommandActionKind.OpenDashboard, HubCommandActionKind.Navigate, HubCommandActionKind.OpenDashboard, HubCommandActionKind.Navigate],
            sessionCommands.Select(command => command.Action.Kind).ToArray());
        Assert.Equal("Open in dashboard", sessionCommands[0].Subtitle);
        Assert.Equal("Command_OpenSessionDashboard_Subtitle", sessionCommands[1].Subtitle);
    }

    private static HubCommandContext Context(
        bool diagnosticsVisible = false,
        HubCommandToggleState? toggles = null,
        IReadOnlyList<string>? sessions = null)
    {
        var resources = HubPageRegistry.CommandResourceKeys.ToImmutableDictionary(
            key => key,
            key => key switch
            {
                "Command_GoToCron_Title" => "Cron {0}",
                "Command_GoToWorkspace_Title" => "Workspace {0}",
                _ => key
            },
            StringComparer.Ordinal);
        return new HubCommandContext(
            "alpha",
            diagnosticsVisible,
            toggles,
            (sessions ?? []).ToImmutableArray(),
            resources);
    }

    private static string? CommandValue(HubCommand command) => command.Action.Value;
}
