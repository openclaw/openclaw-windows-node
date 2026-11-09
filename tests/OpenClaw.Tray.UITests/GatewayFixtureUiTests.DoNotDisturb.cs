// Native DND saves through the shipped InboxView handler, with owned-profile restart.
// The stable phase is explicitly selected by test-gateway-fixture.ps1 -StableAppPath.
// Fixture isolation suppresses OS notifications; these cases make no OS toast/sound claim.
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using OpenClaw.Connection;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    private const string DndToggleId = "WorkspaceNotificationsDoNotDisturb";
    private const string DndBellHelpPrefix = "Do not disturb is on. ";
    private static readonly string[] DndUnrelatedFalseSettings =
        ["NotifyInfo", "NotifyHealth", "NotifyBuild", "EnableNodeMode", "AutoStart"];

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task InboxDoNotDisturb_KeepsRowsAndBadgeSuppressesOrdinaryBannerAndPersistsNativeSaves()
    {
        const string warning = "Slack channel needs attention";
        const string error = "Fixture DND backup failed";
        const string freshError = "Fixture DND second backup failed";
        const string approvalId = "fixture-dnd-pending";
        const string command = "echo fixture-dnd-review";
        await WithAppAsync(async run =>
        {
            await OpenInboxFeedPageAsync(run, "All");
            await WaitUiAsync(run, () => InboxFeedRow(run, warning + ",") is not null, "real health warning in Inbox");
            // Match the existing notification proof: acknowledge only this known host
            // warning, never clear arbitrary reminders to manufacture an empty Inbox.
            const string hostWarning = "Windows containment unavailable: commands run uncontained";
            var dismiss = FindButtons(run, "Dismiss " + hostWarning);
            Assert.InRange(dismiss.Count, 0, 1);
            if (dismiss.Count == 1)
            {
                Invoke(dismiss[0]);
                await WaitUiAsync(run, () => FindButtons(run, "Dismiss " + hostWarning).Count == 0,
                    "known host warning acknowledged");
            }
            await WaitDndStateAsync(run, false);
            var originalCount = 0;
            await WaitUiAsync(run, () => (originalCount = DndBellCount(run)) > 0
                && InboxFeedVisibleId(run, "WorkspaceNotificationsTabAll")?.Current.Name == $"All {originalCount}",
                "initial Inbox tab and bell count acknowledged");
            Assert.Equal("Approvals", InboxFeedVisibleId(run, "WorkspaceNotificationsTabApprovals")!.Current.Name);
            await PrepareDndPreferencesAsync(run);
            var baseline = CaptureDndProfile(run);
            await ShowDndSettingsAsync(run);
            await WaitDndBannerAsync(run, warning); // Positive control before suppression.

            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => InboxFeedVisibleId(run, "WorkspaceNotifications") is { Current.IsEnabled: true },
                "visible Workspace footer before native DND interaction");
            await OpenDndBellAsync(run);
            await ToggleDndAsync(run, wasOn: false);
            await OpenDndPageFromBellAsync(run);
            await WaitDndStateAsync(run, true);
            await WaitDndCountAsync(run, originalCount, true);
            Assert.NotNull(InboxFeedRow(run, warning + ","));

            // Canonical Gateway event and shipped system.notify, not injected rows,
            // severity, fake clocks, notification services or new production MCP hooks.
            await run.Gateway.PublishExecApprovalRequestedAsync(GatewayScenario.MainSessionKey, approvalId, command);
            using (var tools = await run.Client.ListToolsAsync())
                Assert.Contains(tools.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray(),
                    tool => tool.GetProperty("name").GetString() == "system.notify");
            await SendDndErrorAsync(run, error);
            await WaitUiAsync(run, () => InboxFeedRow(run, warning + ",") is not null
                && InboxFeedRow(run, error + ",") is not null
                && FindInboxReviewExpander(run, command) is not null
                && InboxFeedVisibleId(run, "WorkspaceNotificationsTabApprovals")?.Current.Name == "Approvals 1",
                "ordinary warning, new error and pending approval retained while DND is on");
            await WaitDndCountAsync(run, originalCount + 2, true);
            await ShowDndSettingsAsync(run);
            await WaitDndBannerAsync(run, null);

            await OpenInboxFeedPageAsync(run, "All");
            await ToggleDndAsync(run, wasOn: true);
            await WaitDndCountAsync(run, originalCount + 2, false);
            await ShowDndSettingsAsync(run);
            await WaitDndBannerAsync(run, warning); // Existing warning restored without a new event.
            var banner = InboxFeedVisibleId(run, "AppNotificationInfoBar")!;
            var close = banner.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "CloseButton"));
            Assert.NotNull(close);
            Invoke(close);
            await WaitDndBannerAsync(run, null);

            await OpenInboxFeedPageAsync(run, "All");
            await ToggleDndAsync(run, wasOn: false);
            await ToggleDndAsync(run, wasOn: true);
            await WaitDndCountAsync(run, originalCount + 2, false);
            Assert.NotNull(FindInboxReviewExpander(run, command));
            await ShowDndSettingsAsync(run);
            await WaitDndBannerAsync(run, null); // DND cycling must not resurrect a closed banner.
            await SendDndErrorAsync(run, freshError);
            await WaitDndBannerAsync(run, freshError); // Positive delivery control after close/off.
            await OpenInboxFeedPageAsync(run, "All");
            await WaitDndCountAsync(run, originalCount + 3, false);
            await run.Gateway.PublishExecApprovalResolvedAsync(GatewayScenario.MainSessionKey, approvalId, "deny");
            await WaitUiAsync(run, () => FindInboxReviewExpander(run, command) is null
                && InboxFeedVisibleId(run, "WorkspaceNotificationsTabApprovals")?.Current.Name == "Approvals",
                "real resolved event retires only the pending approval");
            await WaitDndCountAsync(run, originalCount + 2, false);
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method.EndsWith(".approval.resolve", StringComparison.Ordinal));

            await ToggleDndAsync(run, wasOn: false);
            var candidate = RequiredDndApp("OPENCLAW_GATEWAY_FIXTURE_APP");
            await run.RestartAppAsync(candidate);
            await OpenInboxFeedPageAsync(run, "All");
            await WaitDndStateAsync(run, true);
            await WaitDndPersistedAsync(run, true);
            await AssertDndProfileAsync(run, baseline);
            await ToggleDndAsync(run, wasOn: true);
            await run.RestartAppAsync(candidate);
            await OpenInboxFeedPageAsync(run, "All");
            await WaitDndStateAsync(run, false);
            await WaitDndPersistedAsync(run, false);
            await AssertDndProfileAsync(run, baseline);
            await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "dnd-native-proof.json"),
                JsonSerializer.Serialize(new
                {
                    bellAndPageNativeToggle = true, ordinaryAlertAndApprovalRowsRetained = true,
                    badgeCountRetained = true, ordinaryBannerSuppressedAndRestored = true,
                    closedBannerStayedClosed = true, newBannerAfterOff = true,
                    restartedValues = new[] { true, false }, unrelatedPreferencesAndIdentityPreserved = true,
                    boundary = "Isolated native UI and profile persistence. No OS toast/sound or approval-banner exception proof."
                }));
        }, scenario: GatewayScenario.CreateChannelIssue("Fixture channel authorization failed."));
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    [Trait("FixturePhase", "ReleasedProfile")]
    public async Task InboxDoNotDisturb_ReleasedProfileNativeSavesSurviveCandidateRestarts()
    {
        var stable = RequiredDndApp("OPENCLAW_GATEWAY_FIXTURE_STABLE_APP");
        var candidate = RequiredDndApp("OPENCLAW_GATEWAY_FIXTURE_APP");
        Assert.NotEqual(Path.GetFullPath(stable), Path.GetFullPath(candidate), StringComparer.OrdinalIgnoreCase);
        var nativeId = Guid.NewGuid().ToString();
        var wslId = Guid.NewGuid().ToString();
        var startedAt = DateTime.UtcNow;
        await WithAppAsync(async run =>
        {
            await run.WaitForAsync(() => Task.FromResult(ReadDndJson(run, "gateways.json")["gateways"]!.AsArray()
                .Single(record => record!["id"]!.GetValue<string>() == run.Profile.GatewayId)!["lastConnected"]
                ?.GetValue<DateTime>() >= startedAt), "stable app persisted its successful connection");
            // Only old supported settings are written by the stable app. No DND UI,
            // MCP setting, or hand-edited DND value is used to simulate a saved choice.
            await PrepareDndPreferencesAsync(run);
            var stableSettings = ReadDndJson(run, "settings.json");
            var legacyDndFieldAbsent = !stableSettings.ContainsKey("DoNotDisturb");
            // A future latest-stable build may know DND. Keep exercising its profile
            // without claiming absent-field migration when it serialized the default.
            if (!legacyDndFieldAbsent) Assert.False(stableSettings["DoNotDisturb"]!.GetValue<bool>());
            var identity = Path.Combine(run.Profile.DataDirectory, "gateways", run.Profile.GatewayId, "device-key-ed25519.json");
            Assert.True(File.Exists(identity), "Stable must create its own synthetic operator identity.");
            foreach (var id in new[] { nativeId, wslId })
            {
                var directory = Path.Combine(run.Profile.DataDirectory, "gateways", id);
                Directory.CreateDirectory(directory);
                // Same released-format inactive identity setup as the existing compatibility fact.
                File.Copy(identity, Path.Combine(directory, "device-key-ed25519.json"));
            }
            var baseline = CaptureDndProfile(run);
            Assert.Equal(3, baseline.Identities.Count);
            Assert.Equal(run.Profile.GatewayId, baseline.Registry["activeId"]!.GetValue<string>());
            var nativeRecord = baseline.Registry["gateways"]!.AsArray().Single(record => record!["id"]!.GetValue<string>() == nativeId)!;
            var wslRecord = baseline.Registry["gateways"]!.AsArray().Single(record => record!["id"]!.GetValue<string>() == wslId)!;
            Assert.Equal("OpenClaw.Fixture_native", nativeRecord["nativePackageFamilyName"]!.GetValue<string>());
            Assert.Equal($"OpenClawFixture-{run.Profile.RunId[..8]}", wslRecord["setupManagedDistroName"]!.GetValue<string>());
            await run.WriteReportAsync("stable-profile-persisted");
            File.Copy(Path.Combine(run.ArtifactsDirectory, "run.json"), Path.Combine(run.ArtifactsDirectory, "stable-run.json"));
            await run.RestartAppAsync(candidate);
            await OpenInboxFeedPageAsync(run, "All");
            await WaitDndStateAsync(run, false); // Default an absent field, or preserve stable's untouched false default.
            await AssertDndProfileAsync(run, baseline);

            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await OpenDndBellAsync(run);
            await ToggleDndAsync(run, wasOn: false);
            await OpenDndPageFromBellAsync(run);
            await WaitDndStateAsync(run, true);
            await WaitDndPersistedAsync(run, true);
            await AssertDndProfileAsync(run, baseline);
            await run.RestartAppAsync(candidate);
            await OpenInboxFeedPageAsync(run, "All");
            await WaitDndStateAsync(run, true);
            await WaitDndPersistedAsync(run, true);
            await AssertDndProfileAsync(run, baseline);
            await ToggleDndAsync(run, wasOn: true);
            await AssertDndProfileAsync(run, baseline);
            await run.RestartAppAsync(candidate);
            await OpenInboxFeedPageAsync(run, "All");
            await WaitDndStateAsync(run, false);
            await WaitDndPersistedAsync(run, false);
            await AssertDndProfileAsync(run, baseline);
            await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "dnd-released-profile-proof.json"),
                JsonSerializer.Serialize(new
                {
                    stableAppSha256 = DndFileHash(stable), candidateAppSha256 = DndFileHash(candidate),
                    legacyDndFieldAbsent, candidateInitialValue = false,
                    nativeSavedAndRestartedValues = new[] { true, false },
                    gatewayCount = baseline.Identities.Count, gatewayFieldsAndIdentityBytesPreserved = true,
                    unrelatedPreferencesPreserved = true,
                    boundary = "Released portable profile to candidate native save/restart. Not installer, Store, OS notifications or packaged privacy proof."
                }));
        }, initialAppPath: stable, configureProfile: profile =>
        {
            // The current fixture serializer knows DND. Remove that default from the
            // initial synthetic legacy input BEFORE launching stable; never add it to stable.
            var path = Path.Combine(profile.DataDirectory, "settings.json");
            var settings = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.True(settings.Remove("DoNotDisturb"));
            File.WriteAllText(path, settings.ToJsonString());
            // Keep the existing portable-compatibility baseline: one active fixture
            // record plus inactive native/WSL ownership metadata, without those runtimes.
            var registry = new GatewayRegistry(profile.DataDirectory);
            registry.Load();
            var distro = $"OpenClawFixture-{profile.RunId[..8]}";
            registry.AddOrUpdate(new GatewayRecord
            {
                Id = nativeId, Url = "ws://127.0.0.1:18789", FriendlyName = $"Local ({distro})",
                IsLocal = true, NativePackageFamilyName = "OpenClaw.Fixture_native"
            });
            registry.AddOrUpdate(new GatewayRecord
            {
                Id = wslId, Url = "ws://127.0.0.1:18790", FriendlyName = $"Local ({distro})",
                IsLocal = true, SetupManagedDistroName = distro
            });
            registry.Save();
        });
    }

    private static async Task OpenDndBellAsync(GatewayFixtureRun run)
    {
        Invoke(await WaitForElementByIdAsync(run, "WorkspaceNotifications", "Inbox bell before DND toggle"));
        await WaitUiAsync(run, () => InboxFeedVisibleId(run, "WorkspaceNotificationsOpenPage") is not null
            && InboxFeedVisibleId(run, DndToggleId) is not null, "native DND toggle in bell flyout");
    }

    private static async Task OpenDndPageFromBellAsync(GatewayFixtureRun run)
    {
        Invoke(InboxFeedVisibleId(run, "WorkspaceNotificationsOpenPage")!);
        await WaitUiAsync(run, () => InboxFeedVisibleId(run, "WorkspaceNotificationsOpenPage") is null
            && InboxFeedVisibleId(run, "NotificationsPageMarker") is not null, "bell closes into full Inbox page");
    }

    private static Task WaitDndStateAsync(GatewayFixtureRun run, bool expected) =>
        WaitUiAsync(run, () => InboxFeedVisibleId(run, DndToggleId) is { } toggle
            && ((TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState
                == (expected ? ToggleState.On : ToggleState.Off)
            && InboxFeedVisibleId(run, "WorkspaceNotifications") is { } bell
            && bell.Current.HelpText.StartsWith(DndBellHelpPrefix, StringComparison.Ordinal) == expected,
            "native DND toggle and bell agree with expected saved state");

    private static async Task ToggleDndAsync(GatewayFixtureRun run, bool wasOn)
    {
        await WaitDndStateAsync(run, wasOn);
        var toggle = InboxFeedVisibleId(run, DndToggleId)!;
        Assert.True(toggle.Current.IsEnabled);
        Assert.Equal(run.AppProcessId, toggle.Current.ProcessId);
        ((TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern)).Toggle(); // Exactly one native action.
        await WaitDndStateAsync(run, !wasOn);
        await WaitDndPersistedAsync(run, !wasOn);
    }

    private static Task WaitDndPersistedAsync(GatewayFixtureRun run, bool expected) =>
        run.WaitForAsync(() => Task.FromResult(
            ReadDndJson(run, "settings.json")["DoNotDisturb"]?.GetValue<bool>() == expected),
            "native DND choice persisted by the shipping field-scoped save");

    private static int DndBellCount(GatewayFixtureRun run)
    {
        var text = InboxFeedVisibleId(run, "WorkspaceNotifications")?.Current.HelpText ?? "";
        if (text.StartsWith(DndBellHelpPrefix, StringComparison.Ordinal)) text = text[DndBellHelpPrefix.Length..];
        if (text == "Nothing waiting") return 0;
        var parts = text.Split(' ');
        return parts.Length == 2 && parts[1] == "waiting" && int.TryParse(parts[0], out var count) ? count : -1;
    }

    private static Task WaitDndCountAsync(GatewayFixtureRun run, int expected, bool on) =>
        WaitUiAsync(run, () =>
        {
            var bell = InboxFeedVisibleId(run, "WorkspaceNotifications");
            var badgeText = expected > 9 ? "9+" : expected.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return bell is not null && DndBellCount(run) == expected
                && InboxFeedVisibleId(run, "WorkspaceNotificationsTabAll")?.Current.Name == $"All {expected}"
                && bell.Current.HelpText.StartsWith(DndBellHelpPrefix, StringComparison.Ordinal) == on
                && bell.FindAll(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                    new PropertyCondition(AutomationElement.NameProperty, badgeText)))
                    .Cast<AutomationElement>().Any(element => !element.Current.IsOffscreen
                        && element.Current.BoundingRectangle.Width > 0 && element.Current.BoundingRectangle.Height > 0);
        }, "native Inbox tab, visible badge and bell help retain the independent expected count");

    private static async Task ShowDndSettingsAsync(GatewayFixtureRun run)
    {
        await run.InvokeAsync("app.navigate", new { page = "settings" });
        await WaitUiAsync(run, () => InboxFeedVisibleId(run, "SettingsPageMarker") is not null, "Settings banner host mounted");
    }

    private static Task WaitDndBannerAsync(GatewayFixtureRun run, string? title) =>
        WaitUiAsync(run, () =>
        {
            var banner = InboxFeedVisibleId(run, "AppNotificationInfoBar");
            return title is null ? banner is null : banner is not null
                && banner.FindAll(TreeScope.Subtree, new PropertyCondition(AutomationElement.NameProperty, title))
                    .Cast<AutomationElement>().Any(element => !element.Current.IsOffscreen);
        }, title is null ? "ordinary banner absent" : "expected ordinary banner visibly present");

    private static async Task SendDndErrorAsync(GatewayFixtureRun run, string title)
    {
        var result = await run.InvokeAsync("system.notify", new { title, body = "Synthetic backup failed. No user data is involved.", sound = false });
        Assert.True(result.GetProperty("sent").GetBoolean());
    }

    private static async Task PrepareDndPreferencesAsync(GatewayFixtureRun run)
    {
        foreach (var name in new[] { "NotifyInfo", "NotifyHealth", "NotifyBuild" })
            await run.InvokeAsync("app.settings.set", new { name, value = "false" });
        await AssertDndPreferencesAsync(run);
    }

    private static async Task AssertDndPreferencesAsync(GatewayFixtureRun run)
    {
        var settings = ReadDndJson(run, "settings.json");
        foreach (var name in DndUnrelatedFalseSettings)
        {
            Assert.False((await run.InvokeAsync("app.settings.get", new { name })).GetBoolean(), name);
            Assert.False(settings[name]!.GetValue<bool>(), name);
        }
        Assert.True((await run.InvokeAsync("app.settings.get", new { name = "EnableMcpServer" })).GetBoolean());
        Assert.True(settings["EnableMcpServer"]!.GetValue<bool>());
        Assert.Equal(run.Gateway.Endpoint.AbsoluteUri, settings["GatewayUrl"]!.GetValue<string>());
    }

    private static (JsonObject Registry, Dictionary<string, string> Identities) CaptureDndProfile(GatewayFixtureRun run)
    {
        var registry = ReadDndJson(run, "gateways.json");
        var identities = registry["gateways"]!.AsArray().ToDictionary(record => record!["id"]!.GetValue<string>(),
            record => DndFileHash(Path.Combine(run.Profile.DataDirectory, "gateways", record!["id"]!.GetValue<string>(), "device-key-ed25519.json")));
        return (registry, identities);
    }

    private static async Task AssertDndProfileAsync(GatewayFixtureRun run,
        (JsonObject Registry, Dictionary<string, string> Identities) before)
    {
        var after = ReadDndJson(run, "gateways.json");
        Assert.True(JsonNode.DeepEquals(before.Registry["activeId"], after["activeId"]));
        var records = after["gateways"]!.AsArray().ToDictionary(record => record!["id"]!.GetValue<string>(), record => record!);
        Assert.Equal(before.Identities.Keys.Order(), records.Keys.Order());
        foreach (var record in before.Registry["gateways"]!.AsArray())
        {
            var id = record!["id"]!.GetValue<string>();
            foreach (var (field, value) in record!.AsObject())
                if (field != "lastConnected")
                    Assert.True(JsonNode.DeepEquals(value, records[id][field]), $"Existing gateway field '{field}' changed during native DND persistence.");
            Assert.Equal(before.Identities[id], DndFileHash(Path.Combine(run.Profile.DataDirectory, "gateways", id, "device-key-ed25519.json")));
        }
        await AssertDndPreferencesAsync(run);
    }

    private static JsonObject ReadDndJson(GatewayFixtureRun run, string file) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(run.Profile.DataDirectory, file)))!.AsObject();

    private static string DndFileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string RequiredDndApp(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} to an explicit fixture-compatible app.");
}
