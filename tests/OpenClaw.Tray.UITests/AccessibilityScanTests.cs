using System.Collections.Generic;
using System.Threading.Tasks;
using Axe.Windows.Core.Enums;
using Xunit;

namespace OpenClaw.Tray.UITests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AccessibilityCollection : ICollectionFixture<AccessibilityAppFixture>
{
    public const string Name = "Accessibility app";
}

/// <summary>
/// Scans every native Hub page in the real OpenClaw process. The test process
/// remains separate because Axe.Windows drives the target through UI Automation.
/// </summary>
[Collection(AccessibilityCollection.Name)]
public sealed class AccessibilityScanTests
{
    private readonly AccessibilityAppFixture _app;

    public AccessibilityScanTests(AccessibilityAppFixture app)
    {
        _app = app;
    }

    private static readonly IReadOnlyDictionary<string, RuleId[]> PageRuleExclusions =
        new Dictionary<string, RuleId[]>
        {
            // The public GridSplitter has an app-supplied localized name/type. Its
            // CommunityToolkit SizerBase child peer still reports both types as
            // "custom" and cannot be configured by the consuming XAML page.
            ["ConfigPage"] = [RuleId.LocalizedControlTypeNotCustom],
        };

    public static IEnumerable<object[]> PageTestData()
    {
        yield return ["AgentEventsPage", "agentevents", "AgentEventsPageMarker"];
        yield return ["BindingsPage", "bindings", "BindingsPageMarker"];
        yield return ["ChannelsPage", "channels", "ChannelsPageMarker"];
        yield return ["ChatPage", "chat", "ChatComposerInput"];
        yield return ["ConfigPage", "config", "ConfigPageMarker"];
        yield return ["ConnectionPage", "connection", "ConnectionPageMarker"];
        yield return ["CronPage", "cron", "CronPageMarker"];
        yield return ["DebugPage", "debug", "DebugPageMarker"];
        yield return ["InstancesPage", "instances", "InstancesPageMarker"];
        yield return ["NotificationsPage", "notifications", "NotificationsPageMarker"];
        yield return ["PermissionsPage", "permissions", "PermissionsPageMarker"];
        yield return ["SandboxPage", "sandbox", "SandboxPageMarker"];
        yield return ["SessionsPage", "sessions", "SessionsPageMarker"];
        yield return ["SettingsPage", "settings", "SettingsPageMarker"];
        yield return ["SkillsPage", "skills", "SkillsPageMarker"];
        yield return ["UsagePage", "usage", "UsagePageMarker"];
        yield return ["VoiceSettingsPage", "voice", "VoiceSettingsPageMarker"];
        yield return ["WorkspacePage", "workspace", "WorkspacePageMarker"];
    }

    [Theory]
    [Trait("Category", "Accessibility")]
    [MemberData(nameof(PageTestData))]
    public async Task Page_PassesAccessibilityScan(
        string pageName,
        string pageTag,
        string pageMarkerAutomationId)
    {
        await _app.NavigateAsync(pageTag, pageMarkerAutomationId);
        PageRuleExclusions.TryGetValue(pageName, out var exclusions);
        AxeHelper.AssertNoAccessibilityErrors(
            _app.HubWindowHandle,
            exclusions,
            context: pageName);
    }
}
