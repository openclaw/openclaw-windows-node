using System.Text.Json;
using System.Windows.Automation;
using System.Globalization;
using OpenClaw.TestSupport.Gateway;
using OpenClaw.GatewayFixtureHost;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task SandboxPageKeepsPermissionControlsWithoutTransferConfigurationCards()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "sandbox" });
            await WaitUiAsync(run, () => FindById(run, "SandboxPageMarker") is { Current.IsOffscreen: false },
                "visible Sandbox page");
            var marker = FindById(run, "SandboxPageMarker")!;
            var window = AppWindows(run).Single(candidate => candidate.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SandboxPageMarker")) is not null);
            AutomationElement? Named(string name) => window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, name));
            await WaitUiAsync(run, () => Named("Security level") is { Current.IsOffscreen: false },
                "visible Sandbox permission presets");
            Assert.Null(FindById(run, "TransferBudgetBox"));
            Assert.Null(Named("Upload storage location"));
            Assert.Null(Named("File transfer budget (MiB)"));
            Assert.Null(Named("Choose parent folder…"));
            if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
            {
                await WaitForStablePageGeometryAsync(run, window, marker.Current.BoundingRectangle.Left);
                OwnedWindowCapture.Save(run, window, "sandbox-managed-staging.png", marker, Named("Security level")!);
            }
            Assert.False(FindById(run, "SandboxContainedOperations") is { Current.IsOffscreen: false });
            var scope = FindById(run, "ScopeExpander")!;
            ((ExpandCollapsePattern)scope.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            await WaitUiAsync(run, () => FindById(run, "SandboxContainedOperations") is { Current.IsOffscreen: false },
                "contained operations in the expanded scope section");
            var operations = FindById(run, "SandboxContainedOperations")!;
            Assert.Null(FindById(run, "SandboxContainmentDetails"));
            Assert.Contains("system.run", operations.Current.Name);
            Assert.Contains("file actions", operations.Current.Name);
            if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
            {
                await WaitForStablePageGeometryAsync(run, window, marker.Current.BoundingRectangle.Left);
                OwnedWindowCapture.Save(run, window, "sandbox-containment-scope.png", operations);
            }
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    [Trait("Category", "Accessibility")]
    public async Task SettingsPages_GroupPreferencesInGeneral_AndKeepConnectionAboutAndConsentSeparate()
    {
        Action? releaseHandshake = null;
        void ReleaseHandshake()
        {
            var release = releaseHandshake;
            releaseHandshake = null;
            release?.Invoke();
        }
        await WithAppAsync(async run =>
        {
            using var discovery = await run.Client.ListToolsAsync();
            var tools = discovery.RootElement.GetProperty("result").GetProperty("tools")
                .EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToArray();
            Assert.Contains("app.search", tools);
            Assert.Contains("app.navigate", tools);
            await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "mcp-tools.json"),
                discovery.RootElement.GetRawText());
            var commands = await run.InvokeAsync("app.search", new { query = "Connection" });
            Assert.Equal(JsonValueKind.Array, commands.ValueKind);
            Assert.Equal("\uE839", Assert.Single(commands.EnumerateArray(),
                command => command.GetProperty("Title").GetString() == "Connection").GetProperty("Icon").GetString());

            foreach (var (route, marker, control) in new[]
            {
                ("settings", "SettingsPageMarker", "SettingsPageAdvancedExpander"),
                ("connection", "ConnectionPageMarker", "SettingsNavConnection"),
                ("about", "AboutPageMarker", "SettingsPageCheckUpdates"),
                ("permissions", "PermissionsPageMarker", "SettingsPageScreenRecording"),
                ("notifications", "NotificationsPageMarker", "NotificationsPageMarker")
            })
            {
                var navigationId = route switch
                {
                    "connection" => "SettingsNavConnection",
                    "about" => "SettingsNavAbout",
                    _ => null
                };
                if (navigationId is not null)
                {
                    var navigationItem = FindById(run, navigationId)!;
                    ((ScrollItemPattern)navigationItem.GetCurrentPattern(ScrollItemPattern.Pattern)).ScrollIntoView();
                    Invoke(navigationItem);
                }
                else
                {
                    await run.InvokeAsync("app.navigate", new { page = route });
                }
                await WaitUiAsync(run, () => FindById(run, marker) is { Current.IsOffscreen: false },
                    $"{route} page mounted");
                Assert.NotNull(FindById(run, control));
                if (route != "settings")
                    Assert.Null(FindById(run, "SettingsPageNotifyHealth"));
                else
                    Assert.NotNull(FindById(run, "SettingsPageNotifyHealth"));
                if (route != "permissions")
                    Assert.Null(FindById(run, "SettingsPageScreenRecording"));
                if (route != "about")
                    Assert.Null(FindById(run, "SettingsPageCheckUpdates"));

                if (route == "permissions")
                    foreach (var id in new[] { "SettingsPageScreenRecording", "SettingsPageCameraRecording", "SettingsPageLocationConsent" })
                        Assert.True(FindById(run, id)!.TryGetCurrentPattern(TogglePattern.Pattern, out _));

                var markerCondition = new PropertyCondition(AutomationElement.AutomationIdProperty, marker);
                var window = AppWindows(run).Single(candidate =>
                    candidate.FindFirst(TreeScope.Descendants, markerCondition) is not null);
                var pageLeft = FindById(run, marker)!.Current.BoundingRectangle.Left;
                await WaitForStablePageGeometryAsync(run, window, pageLeft);
                OwnedWindowCapture.Save(run, window, $"settings-split-{route}.png", FindById(run, marker)!);
                if (route == "permissions")
                {
                    var cards = new[] { "AppAccessScreenCaptureCard", "AppAccessCameraCaptureCard", "AppAccessLocationCard" }
                        .Select(id => FindById(run, id)!).ToArray();
                    ((ScrollItemPattern)cards[^1].GetCurrentPattern(ScrollItemPattern.Pattern)).ScrollIntoView();
                    await WaitUiAsync(run, () => cards.All(card => !card.Current.IsOffscreen),
                        "all three app-access consent rows visible");
                    await WaitForStablePageGeometryAsync(run, window, pageLeft);
                    OwnedWindowCapture.Save(run, window, "permissions-app-access.png", cards);
                    var consents = new[]
                    {
                        ("SettingsPageScreenRecording", "ScreenRecordingConsentGiven"),
                        ("SettingsPageCameraRecording", "CameraRecordingConsentGiven"),
                        ("SettingsPageLocationConsent", "LocationConsentGiven")
                    };
                    foreach (var (id, setting) in consents)
                    {
                        var toggle = (TogglePattern)FindById(run, id)!.GetCurrentPattern(TogglePattern.Pattern);
                        Assert.Equal(ToggleState.Off, toggle.Current.ToggleState);
                        toggle.Toggle();
                        await run.WaitForAsync(async () =>
                        {
                            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(
                                Path.Combine(run.Profile.DataDirectory, "settings.json")));
                            return persisted.RootElement.GetProperty(setting).GetBoolean();
                        }, $"{setting} persisted");
                    }
                    await run.InvokeAsync("app.navigate", new { page = "settings" });
                    await WaitUiAsync(run, () => FindById(run, "SettingsPageMarker") is not null, "leave Permissions");
                    await run.InvokeAsync("app.navigate", new { page = "permissions" });
                    await WaitUiAsync(run, () => FindById(run, "PermissionsPageMarker") is not null, "reload Permissions");
                    using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(
                        Path.Combine(run.Profile.DataDirectory, "settings.json")));
                    var receipt = new Dictionary<string, bool>();
                    foreach (var (id, setting) in consents)
                    {
                        Assert.Equal(ToggleState.On,
                            ((TogglePattern)FindById(run, id)!.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState);
                        Assert.True(saved.RootElement.GetProperty(setting).GetBoolean());
                        receipt[setting] = true;
                    }
                    foreach (var setting in new[] { "EnableNodeMode", "NodeScreenEnabled", "NodeCameraEnabled", "NodeLocationEnabled" })
                    {
                        Assert.False(saved.RootElement.GetProperty(setting).GetBoolean());
                        receipt[setting] = false;
                    }
                    await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "consent-persistence.json"),
                        JsonSerializer.Serialize(receipt));
                }
            }

            var about = await run.InvokeAsync("app.search", new { query = "About" });
            Assert.Equal("\uE946", Assert.Single(about.EnumerateArray(),
                command => command.GetProperty("Title").GetString() == "About").GetProperty("Icon").GetString());
            var preferences = await run.InvokeAsync("app.search", new { query = "Notification preferences" });
            Assert.Equal("General", Assert.Single(preferences.EnumerateArray()).GetProperty("Title").GetString());
            Assert.Equal(0, (await run.InvokeAsync("app.search", new { query = "Event Stream" })).GetArrayLength());
            var rejected = new Dictionary<string, JsonElement>();
            foreach (var route in new[] { "agentevents", "agent:main:agentevents" })
            {
                using var response = await run.Client.CallToolAsync("app.navigate", new { page = route });
                Assert.True(response.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
                Assert.NotNull(FindById(run, "NotificationsPageMarker"));
                rejected[route] = response.RootElement.Clone();
            }
            await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "settings-navigation-mcp.json"),
                JsonSerializer.Serialize(new { connection = commands, about, preferences, rejected }));

            var connection = FindById(run, "SettingsNavConnection")!;
            ((ScrollItemPattern)connection.GetCurrentPattern(ScrollItemPattern.Pattern)).ScrollIntoView();
            var systemsName = new PropertyCondition(AutomationElement.NameProperty, "Systems");
            Assert.NotNull(FindInApp(run, systemsName));
            run.Gateway.HoldConnectionHandshakes();
            releaseHandshake = run.Gateway.ReleaseConnectionHandshakes;
            await run.Gateway.CloseConnectionsAsync();
            await run.WaitForAsync(async () =>
                (await run.InvokeAsync("app.status")).GetProperty("operatorState").GetString() != "Connected",
                "fixture operator disconnected");
            await WaitUiAsync(run, () => FindInApp(run, systemsName) is null,
                "Gateway-dependent entries hidden while disconnected");
            Assert.True(connection.Current.IsEnabled);
            ((ScrollItemPattern)connection.GetCurrentPattern(ScrollItemPattern.Pattern)).ScrollIntoView();
            var gatewayHeader = FindInApp(run, new PropertyCondition(AutomationElement.NameProperty, "Gateway"));
            Assert.NotNull(gatewayHeader);
            Assert.False(gatewayHeader.Current.IsOffscreen);
            Invoke(connection);
            await WaitUiAsync(run, () => FindById(run, "ConnectionPageMarker") is { Current.IsOffscreen: false },
                "Connection remains reachable under Gateway while offline");
            var connectionWindow = AppWindows(run).Single(window => window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "ConnectionPageMarker")) is not null);
            var connectionHeading = FindById(run, "ConnectionPageMarker")!;
            await WaitForStablePageGeometryAsync(run, connectionWindow, connectionHeading.Current.BoundingRectangle.Left);
            OwnedWindowCapture.Save(run, connectionWindow, "connection-offline.png", connection, connectionHeading);
            ReleaseHandshake();
        }, scenario: GatewayScenario.CreateAccessibilityBaseline(),
            releasePendingResponses: ReleaseHandshake);
    }

    private const int StableGeometryPolls = 5;

    // A SettingsExpander slides its items in for a few hundred milliseconds after mount or expansion.
    // Wait until the page text stops moving so an evidence frame is never captured mid-animation.
    private static Task WaitForStablePageGeometryAsync(GatewayFixtureRun run, AutomationElement window, double pageLeft)
    {
        var request = new CacheRequest();
        request.Add(AutomationElement.BoundingRectangleProperty);
        var text = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
        string? previous = null;
        var unchangedPolls = 0;
        return WaitUiAsync(run, () =>
        {
            AutomationElementCollection elements;
            using (request.Activate())
                elements = window.FindAll(TreeScope.Descendants, text);
            var current = string.Join('|', elements.Cast<AutomationElement>()
                .Select(element => element.Cached.BoundingRectangle)
                .Where(bounds => bounds.Left >= pageLeft - 1));
            unchangedPolls = current == previous ? unchangedPolls + 1 : 0;
            previous = current;
            return unchangedPolls >= StableGeometryPolls;
        }, "page geometry to stop moving");
    }

    private AutomationElement ConfigRowRemoveButton(AutomationElement window, AutomationElement edit)
    {
        var bounds = edit.Current.BoundingRectangle;
        var buttons = window.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>()
            .Where(button => !button.Current.IsOffscreen)
            .Where(button =>
            {
                var rect = button.Current.BoundingRectangle;
                return rect.Left >= bounds.Right - 1 && rect.Top < bounds.Bottom && rect.Bottom > bounds.Top;
            }).ToArray();
        output.WriteLine($"Row geometry: edit=[{bounds}], adjacent buttons={buttons.Length}.");
        foreach (var button in buttons)
            output.WriteLine($"Adjacent button: class=[{button.Current.ClassName}] id=[{button.Current.AutomationId}] name=[{button.Current.Name}] bounds=[{button.Current.BoundingRectangle}].");
        return Assert.Single(buttons);
    }

    private async Task<AutomationElement> OpenAccessibilityConfigAsync(GatewayFixtureRun run)
    {
        await run.InvokeAsync("app.navigate", new { page = "config" });
        await WaitUiAsync(run, () => FindById(run, "ConfigPageMarker") is not null, "Config page navigation");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await run.Gateway.WaitForCompletedRequestAsync("config.schema", cancellationToken: deadline.Token);
        await run.Gateway.WaitForCompletedRequestAsync("config.get", cancellationToken: deadline.Token);
        output.WriteLine("Config schema and config.get responses completed.");
        var marker = new PropertyCondition(AutomationElement.AutomationIdProperty, "ConfigPageMarker");
        var window = AppWindows(run).Single(w => w.FindFirst(TreeScope.Descendants, marker) is not null);
        var treeCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tree);
        await WaitUiAsync(run, () => window.FindFirst(TreeScope.Descendants, treeCondition) is not null, "mounted Config tree");
        var tree = window.FindFirst(TreeScope.Descendants, treeCondition);
        Assert.NotNull(tree);
        var itemCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem);
        AutomationElement? messages = null;
        await WaitUiAsync(run, () => (messages = tree.FindAll(TreeScope.Descendants, itemCondition).Cast<AutomationElement>()
            .SingleOrDefault(e => e.Current.Name.EndsWith("Messages", StringComparison.Ordinal))) is not null, "Messages tree item");
        output.WriteLine($"Config tree peer id=[{tree.Current.AutomationId}], Messages peer type=[{messages!.Current.ControlType.ProgrammaticName}].");
        var hwnd = new IntPtr(window.Current.NativeWindowHandle);
        await NativeKeyboardProof.ActivateAsync(hwnd);
        messages.SetFocus();
        await WaitUiAsync(run, () => Automation.Compare(messages, AutomationElement.FocusedElement), "Messages item focused");
        NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Enter);
        await WaitUiAsync(run, () => window.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>()
            .Any(e => e.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                && ((ValuePattern)pattern).Current.Value == "alpha"), "Messages item invoked and alpha editor mounted");
        output.WriteLine("Messages activation mounted the alpha editor.");
        return window;
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    [Trait("Category", "Accessibility")]
    public async Task SessionsList_ExposesItsNameOnTheActualPopulatedListPeer()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "sessions" });
            await WaitUiAsync(run, () => FindById(run, "SessionsPageMarker") is not null
                && FindText(run, GatewayScenario.MainTitle) is not null
                && FindText(run, GatewayScenario.LongTitle) is not null, "two populated session rows");
            var first = FindText(run, GatewayScenario.MainTitle)!;
            var second = FindText(run, GatewayScenario.LongTitle)!;
            static AutomationElement? ListAncestor(AutomationElement element)
            {
                var walker = TreeWalker.ControlViewWalker;
                for (var current = walker.GetParent(element); current is not null; current = walker.GetParent(current))
                    if (current.Current.ControlType == ControlType.List) return current;
                return null;
            }
            var list = ListAncestor(first);
            Assert.NotNull(list);
            Assert.True(Automation.Compare(list, ListAncestor(second)), "Both session rows must belong to the same list peer.");
            output.WriteLine($"List peer: name=[{list.Current.Name}], automationId=[{list.Current.AutomationId}], class=[{list.Current.ClassName}], type=[{list.Current.ControlType.ProgrammaticName}]");
            Assert.Equal("Sessions", list.Current.Name);
        }, scenario: GatewayScenario.CreateAccessibilityBaseline());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    [Trait("Category", "Accessibility")]
    public async Task ClipboardExpander_ExposesNamedSelectableRadioChildren()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "sandbox" });
            await WaitUiAsync(run, () => FindById(run, "SandboxClipboardExpander") is not null, "clipboard expander");
            var expander = FindById(run, "SandboxClipboardExpander")!;
            Assert.Equal("📋 Clipboard", expander.Current.Name);
            var expand = (ExpandCollapsePattern)expander.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            if (expand.Current.ExpandCollapseState != ExpandCollapseState.Expanded) expand.Expand();
            var radios = expander.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton));
            var items = radios.Cast<AutomationElement>().ToArray();
            Assert.Equal(new[] { "None (default)", "Read: agent can see what you copied", "Write: agent can replace your clipboard (cannot read it)", "Read & write" }, items.Select(item => item.Current.Name));
            Assert.All(items, item => Assert.True(item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _)));
            Assert.Single(items, item => ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected);
            expand.Collapse();
            expand.Expand();
            Assert.Equal(4, expander.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton)).Count);
        }, scenario: GatewayScenario.CreateAccessibilityBaseline());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    [Trait("Category", "Accessibility")]
    public async Task NotificationOptions_ExposeTheEightNamedTogglePeers()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "settings" });
            await WaitUiAsync(run, () => FindById(run, "SettingsPageNotifyHealth") is not null, "notification settings");
            Assert.NotNull(FindById(run, "SettingsPageMarker"));
            Assert.Null(FindById(run, "SettingsNavNotificationPreferences"));
            Assert.NotNull(FindText(run, "Show notifications for:"));
            foreach (var (id, name) in new[] {
                ("Health", "Health alerts"), ("Urgent", "Urgent messages"), ("Reminder", "Reminders"),
                ("Email", "Email summaries"), ("Calendar", "Calendar events"), ("Build", "Build notifications"),
                ("Stock", "Stock alerts"), ("Info", "Info messages") })
            {
                var box = FindById(run, $"SettingsPageNotify{id}");
                Assert.NotNull(box);
                Assert.Equal(ControlType.CheckBox, box.Current.ControlType);
                Assert.Equal(name, box.Current.Name);
                Assert.True(box.TryGetCurrentPattern(TogglePattern.Pattern, out _));
            }

            var pageLeft = FindById(run, "SettingsPageMarker")!.Current.BoundingRectangle.Left;
            var master = FindById(run, "GeneralNotificationsCard")!;
            var categories = FindById(run, "GeneralNotificationCategoriesCard")!;
            ((ScrollItemPattern)categories.GetCurrentPattern(ScrollItemPattern.Pattern)).ScrollIntoView();
            await WaitUiAsync(run, () => !master.Current.IsOffscreen && !categories.Current.IsOffscreen,
                "General notification controls visible");
            var marker = new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsPageMarker");
            var window = AppWindows(run).Single(candidate =>
                candidate.FindFirst(TreeScope.Descendants, marker) is not null);
            await WaitForStablePageGeometryAsync(run, window, pageLeft);
            OwnedWindowCapture.Save(run, window, "general-notifications.png", master, categories);

            var health = (TogglePattern)FindById(run, "SettingsPageNotifyHealth")!.GetCurrentPattern(TogglePattern.Pattern);
            var original = health.Current.ToggleState;
            health.Toggle();
            var expected = original == ToggleState.On ? ToggleState.Off : ToggleState.On;
            await run.InvokeAsync("app.navigate", new { page = "about" });
            await WaitUiAsync(run, () => FindById(run, "AboutPageMarker") is not null, "leave General");
            await run.InvokeAsync("app.navigate", new { page = "notification-preferences" });
            await WaitUiAsync(run, () => FindById(run, "SettingsPageMarker") is not null
                && FindById(run, "SettingsPageNotifyHealth") is not null, "preferences alias opens General");
            Assert.Equal(expected,
                ((TogglePattern)FindById(run, "SettingsPageNotifyHealth")!.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState);
        }, scenario: GatewayScenario.CreateAccessibilityBaseline());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    [Trait("Category", "Accessibility")]
    public async Task General_AdvancedExpanderKeepsDiagnosticsToggleReachable()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "settings" });
            await WaitUiAsync(run, () => FindById(run, "SettingsPageAdvancedExpander") is not null,
                "General advanced settings");
            var pageLeft = FindById(run, "SettingsPageMarker")!.Current.BoundingRectangle.Left;
            var expander = FindById(run, "SettingsPageAdvancedExpander")!;
            Assert.True(FindById(run, "SettingsPageShowDiagnostics") is null or { Current.IsOffscreen: true },
                "Diagnostics must be hidden inside the initially collapsed disclosure.");
            var header = Assert.Single(expander.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsExpandCollapsePatternAvailableProperty, true))
                .Cast<AutomationElement>());
            var pattern = (ExpandCollapsePattern)header.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            Assert.Equal(ExpandCollapseState.Collapsed, pattern.Current.ExpandCollapseState);
            pattern.Expand();
            await WaitUiAsync(run, () => FindById(run, "SettingsPageShowDiagnostics") is not null,
                "diagnostics visibility switch");

            var toggle = FindById(run, "SettingsPageShowDiagnostics")!;
            Assert.Equal("Enable Diagnostics", toggle.Current.Name);
            Assert.True(toggle.TryGetCurrentPattern(TogglePattern.Pattern, out _));
            var diagnostics = (TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern);
            if (diagnostics.Current.ToggleState == ToggleState.On)
                diagnostics.Toggle();
            await WaitUiAsync(run, () => FindById(run, "SettingsNavDiagnostics") is null,
                "Diagnostics navigation hidden by preference");
            Assert.NotNull(FindById(run, "SettingsNavGeneral"));
            diagnostics.Toggle();
            await WaitUiAsync(run, () => FindById(run, "SettingsNavDiagnostics") is not null,
                "Diagnostics navigation restored by preference");
            Assert.NotNull(FindById(run, "SettingsPageMarker"));
            var card = Assert.Single(expander.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ClassNameProperty, "SettingsCard")).Cast<AutomationElement>(),
                candidate => candidate.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsPageShowDiagnostics")) is not null);
            ((ScrollItemPattern)card.GetCurrentPattern(ScrollItemPattern.Pattern)).ScrollIntoView();
            await WaitUiAsync(run, () => !card.Current.IsOffscreen && !toggle.Current.IsOffscreen,
                "visible diagnostics row and switch");
            var marker = new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsPageMarker");
            var window = AppWindows(run).Single(candidate =>
                candidate.FindFirst(TreeScope.Descendants, marker) is not null);
            await WaitForStablePageGeometryAsync(run, window, pageLeft);
            OwnedWindowCapture.Save(run, window, "settings-advanced-expanded.png", expander, card, toggle);

            pattern.Collapse();
            await WaitUiAsync(run, () => FindById(run, "SettingsPageShowDiagnostics") is null or { Current.IsOffscreen: true },
                "diagnostics switch hidden again");
        }, scenario: GatewayScenario.CreateAccessibilityBaseline());
    }


    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    [Trait("Category", "Accessibility")]
    [Trait("KeyboardProof", "Required")]
    public async Task ConfigActions_ExposeNamesAfterSyntheticLocalEdit()
    {
        await WithAppAsync(async run =>
        {
            var window = await OpenAccessibilityConfigAsync(run);
            var edits = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            var alpha = edits.Cast<AutomationElement>().FirstOrDefault(e => e.TryGetCurrentPattern(ValuePattern.Pattern, out var value)
                && ((ValuePattern)value).Current.Value == "alpha");
            Assert.NotNull(alpha);
            ((ValuePattern)alpha.GetCurrentPattern(ValuePattern.Pattern)).SetValue("alpha edited");
            await WaitUiAsync(run, () => FindById(run, "SaveButton")?.Current.IsEnabled == true
                && FindById(run, "DiscardButton")?.Current.IsEnabled == true, "valid unsaved local edit");
            var discard = FindById(run, "DiscardButton")!;
            var save = FindById(run, "SaveButton")!;
            Assert.Equal("Discard changes", discard.Current.Name);
            output.WriteLine($"Config save: name=[{save.Current.Name}], id=[{save.Current.AutomationId}]");
            Assert.DoesNotContain(run.Gateway.Requests, r => r.Method is "config.set" or "config.patch" or "config.apply");
            Assert.Equal("Save Changes", save.Current.Name);
        }, scenario: GatewayScenario.CreateAccessibilityBaseline());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    [Trait("Category", "Accessibility")]
    [Trait("KeyboardProof", "Required")]
    public async Task ConfigRemove_ExposesNameAndDistinctRowAssociation()
    {
        await WithAppAsync(async run =>
        {
            var window = await OpenAccessibilityConfigAsync(run);
            var edits = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            var alpha = edits.Cast<AutomationElement>().FirstOrDefault(e => e.TryGetCurrentPattern(ValuePattern.Pattern, out var value)
                && ((ValuePattern)value).Current.Value == "alpha");
            Assert.NotNull(alpha);
            var bravo = edits.Cast<AutomationElement>().Single(e => e.TryGetCurrentPattern(ValuePattern.Pattern, out var value)
                && ((ValuePattern)value).Current.Value == "bravo");
            var alphaRemove = ConfigRowRemoveButton(window, alpha!);
            var bravoRemove = ConfigRowRemoveButton(window, bravo);
            Assert.False(Automation.Compare(alphaRemove, bravoRemove));
            output.WriteLine($"Alpha remove: name=[{alphaRemove.Current.Name}], type=[{alphaRemove.Current.ControlType.ProgrammaticName}]; Bravo remove: name=[{bravoRemove.Current.Name}].");
            Assert.Equal("Remove item", alphaRemove.Current.Name);
            Assert.Equal("Remove item", bravoRemove.Current.Name);
        }, scenario: GatewayScenario.CreateAccessibilityBaseline());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    [Trait("Category", "Accessibility")]
    [Trait("KeyboardProof", "Required")]
    public async Task ConfigRemove_KeyboardReachabilityAndDiscardRestoresRows()
    {
        await WithAppAsync(async run =>
        {
            var window = await OpenAccessibilityConfigAsync(run);
            var hwnd = new IntPtr(window.Current.NativeWindowHandle);
            var edits = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            var alpha = edits.Cast<AutomationElement>().FirstOrDefault(e => e.TryGetCurrentPattern(ValuePattern.Pattern, out var value)
                && ((ValuePattern)value).Current.Value == "alpha");
            Assert.NotNull(alpha);
            var bravo = edits.Cast<AutomationElement>().Single(e => e.TryGetCurrentPattern(ValuePattern.Pattern, out var value)
                && ((ValuePattern)value).Current.Value == "bravo");
            var alphaRemove = ConfigRowRemoveButton(window, alpha!);
            alpha!.SetFocus();
            await WaitUiAsync(run, () => Automation.Compare(alpha, AutomationElement.FocusedElement), "alpha editor focused");
            NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Tab);
            await WaitUiAsync(run, () => Automation.Compare(alphaRemove, AutomationElement.FocusedElement), "focus reaches alpha remove");
            Assert.True(Automation.Compare(alphaRemove, AutomationElement.FocusedElement), "Tab from alpha must reach its associated remove action.");
            NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Enter);
            bool HasValue(string expected) => window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Cast<AutomationElement>()
                .Any(e => e.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                    && ((ValuePattern)pattern).Current.Value == expected);
            await WaitUiAsync(run, () => !HasValue("alpha") && HasValue("bravo"), "alpha removed and bravo retained");
            var focused = AutomationElement.FocusedElement;
            Assert.NotNull(focused);
            Assert.Equal(run.AppProcessId, focused.Current.ProcessId);
            output.WriteLine($"Focus after removal: type=[{focused.Current.ControlType.ProgrammaticName}] id=[{focused.Current.AutomationId}].");
            var discard = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "DiscardButton"));
            Assert.NotNull(discard);
            var remainingBravo = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
                .Cast<AutomationElement>().Single(e => e.TryGetCurrentPattern(ValuePattern.Pattern, out var value)
                    && ((ValuePattern)value).Current.Value == "bravo");
            var bravoRemove = ConfigRowRemoveButton(window, remainingBravo);
            var reachedBravo = Automation.Compare(bravoRemove, focused);
            bool reachedDiscard = Automation.Compare(discard, focused);
            for (var i = 0; i < 40 && !(reachedDiscard && reachedBravo); i++)
            {
                var previousFocus = focused.GetRuntimeId();
                NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Tab);
                await WaitUiAsync(run, () => !previousFocus.SequenceEqual(AutomationElement.FocusedElement.GetRuntimeId()), "focus advances after Tab");
                focused = AutomationElement.FocusedElement;
                Assert.NotNull(focused);
                Assert.Equal(run.AppProcessId, focused.Current.ProcessId);
                reachedBravo |= Automation.Compare(bravoRemove, focused);
                reachedDiscard = Automation.Compare(discard, focused);
            }
            Assert.True(reachedBravo, "The remaining row remove action must remain reachable by Tab.");
            Assert.True(reachedDiscard, "Discard must remain reachable by Tab after removal.");
            NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Enter);
            await WaitUiAsync(run, () => HasValue("alpha") && HasValue("bravo")
                && window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "SaveButton"))?.Current.IsEnabled == false,
                "Discard restored both rows and cleared pending changes");
            Assert.DoesNotContain(run.Gateway.Requests, r => r.Method is "config.set" or "config.patch" or "config.apply");
        }, scenario: GatewayScenario.CreateAccessibilityBaseline());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    [Trait("Category", "Accessibility")]
    public async Task UsageRows_StayReadableAcrossChangedSnapshotAndPeriodSwitch()
    {
        var scenario = GatewayScenario.CreateAccessibilityBaseline();
        var firstDate = DateTime.Today.AddDays(-1);
        var secondDate = DateTime.Today.AddDays(-2);
        var firstId = "UsageDailyRow_" + firstDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var secondId = "UsageDailyRow_" + secondDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "usage" });
            await WaitUiAsync(run, () => FindById(run, "UsageProvider_fixture") is not null
                && FindById(run, "UsageProvider_fixture-empty") is not null, "two named provider cards");
            void Select(string id, string label)
            {
                var selector = FindById(run, id);
                Assert.NotNull(selector);
                var matches = selector.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, label)).Cast<AutomationElement>();
                var item = Assert.Single(matches, candidate => candidate.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _));
                ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            }
            string ReadRow(string id) => FindById(run, id)?.Current.Name
                ?? throw new InvalidOperationException($"Missing named Usage row {id}.");
            async Task<string> SelectPeriodAndReadRow(string periodLabel, string expectedCost)
            {
                await WaitUiAsync(run, () => FindById(run, "UsageChartView") is not null, "loaded daily controls");
                Select("UsagePresets", periodLabel);
                if (FindById(run, "UsageDailyTable") is null) Select("UsageChartView", "Table");
                await WaitUiAsync(run, () => FindById(run, firstId) is { } row
                    && row.Current.Name.Contains(expectedCost, StringComparison.Ordinal),
                    $"period row and accessible name updated to {expectedCost}");
                return ReadRow(firstId);
            }
            var provider = ReadRow("UsageProvider_fixture");
            var empty = ReadRow("UsageProvider_fixture-empty");
            var first = await SelectPeriodAndReadRow("7 days", "$0.75");
            var second = ReadRow(secondId);
            output.WriteLine($"Initial rows: provider=[{provider}], empty=[{empty}], day1=[{first}], day2=[{second}].");
            scenario.AdvanceAccessibilityUsage();
            await run.InvokeAsync("app.navigate", new { page = "settings" });
            await WaitUiAsync(run, () => FindById(run, "SettingsPageMarker") is not null, "leave Usage");
            await run.InvokeAsync("app.navigate", new { page = "usage" });
            await WaitUiAsync(run, () => FindText(run, "Fixture changed warning") is not null, "changed usage snapshot");
            var changedProvider = ReadRow("UsageProvider_fixture");
            var changedDay = await SelectPeriodAndReadRow("7 days", "$1.50");
            var thirtyDayRow = await SelectPeriodAndReadRow("30 days", "$2.50");
            var sevenDayRow = await SelectPeriodAndReadRow("7 days", "$1.50");
            output.WriteLine($"Changed rows: provider=[{changedProvider}], day1=[{changedDay}], 30 days=[{thirtyDayRow}], 7 days=[{sevenDayRow}].");
            foreach (var expected in new[] { "Fixture Provider", "Pro", "37%", "Fixture quota warning" })
                Assert.Contains(expected, provider, StringComparison.Ordinal);
            Assert.Equal("Fixture Empty", empty);
            Assert.Contains(firstDate.ToString("D", CultureInfo.GetCultureInfo("en-US")), first, StringComparison.Ordinal);
            Assert.Contains("$0.75", first, StringComparison.Ordinal);
            Assert.Contains(secondDate.ToString("D", CultureInfo.GetCultureInfo("en-US")), second, StringComparison.Ordinal);
            Assert.Contains("$0.50", second, StringComparison.Ordinal);
            foreach (var expected in new[] { "Fixture Provider", "Pro", "53%", "Fixture changed warning" })
                Assert.Contains(expected, changedProvider, StringComparison.Ordinal);
            Assert.Contains(firstDate.ToString("D", CultureInfo.GetCultureInfo("en-US")), changedDay, StringComparison.Ordinal);
            Assert.Contains("$1.50", changedDay, StringComparison.Ordinal);
            Assert.Contains(firstDate.ToString("D", CultureInfo.GetCultureInfo("en-US")), thirtyDayRow, StringComparison.Ordinal);
            Assert.Contains("$2.50", thirtyDayRow, StringComparison.Ordinal);
            Assert.Equal(changedDay, sevenDayRow);
            File.WriteAllText(Path.Combine(run.ArtifactsDirectory, "usage-accessible-rows.json"),
                JsonSerializer.Serialize(new { provider, empty, first, second, changedProvider, changedDay, thirtyDayRow, sevenDayRow }));
            var window = AppWindows(run).Single(window => window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "UsagePageRoot")) is not null);
            OwnedWindowCapture.Save(run, window, "Usage-accessible-rows-updated.png");
        }, scenario: scenario);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    [Trait("Category", "Accessibility")]
    [Trait("KeyboardProof", "Required")]
    public async Task NotificationOptions_ExposeContextAndSupportKeyboardTraversal()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "settings" });
            await WaitUiAsync(run, () => FindById(run, "SettingsPageNotifyHealth") is not null
                && FindText(run, "Show notifications for:") is not null, "notification context and checkboxes");
            var heading = FindText(run, "Show notifications for:")!;
            var first = FindById(run, "SettingsPageNotifyHealth")!;
            var window = AppWindows(run).Single(w => w.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsPageNotifyHealth")) is not null);
            string Ancestors(AutomationElement element)
            {
                var names = new List<string>();
                for (var node = element; node is not null && !Automation.Compare(node, window); node = TreeWalker.ControlViewWalker.GetParent(node))
                    names.Add($"{node.Current.ControlType.ProgrammaticName}:[{node.Current.Name}]");
                return string.Join(" -> ", names);
            }
            output.WriteLine($"Notification heading ancestry: {Ancestors(heading)}");
            output.WriteLine($"First notification checkbox ancestry: {Ancestors(first)}");
            var hwnd = new IntPtr(window.Current.NativeWindowHandle);
            await NativeKeyboardProof.ActivateAsync(hwnd);
            first.SetFocus();
            await WaitUiAsync(run, () => Automation.Compare(first, AutomationElement.FocusedElement), "first notification focused");
            var expected = new[] { "Health alerts", "Urgent messages", "Reminders", "Email summaries", "Calendar events", "Build notifications", "Stock alerts", "Info messages" };
            var actual = new List<string>();
            for (var i = 0; i < expected.Length; i++)
            {
                var focused = AutomationElement.FocusedElement;
                Assert.NotNull(focused);
                Assert.Equal(run.AppProcessId, focused.Current.ProcessId);
                actual.Add(focused.Current.Name);
                if (i + 1 < expected.Length)
                {
                    NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Tab);
                    await WaitUiAsync(run, () => AutomationElement.FocusedElement.Current.Name == expected[i + 1], $"focus reaches {expected[i + 1]}");
                }
            }
            output.WriteLine($"Notification Tab sequence: {string.Join(" | ", actual)}");
            Assert.Equal(expected, actual);
            NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Tab);
            await WaitUiAsync(run, () => !expected.Contains(AutomationElement.FocusedElement.Current.Name), "focus leaves notification options");
            var after = AutomationElement.FocusedElement;
            Assert.NotNull(after);
            Assert.DoesNotContain(after.Current.Name, expected);
            NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Tab, shift: true);
            await WaitUiAsync(run, () => AutomationElement.FocusedElement.Current.Name == "Info messages", "focus returns to last notification");
            Assert.Equal("Info messages", AutomationElement.FocusedElement.Current.Name);
        }, scenario: GatewayScenario.CreateAccessibilityBaseline());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    [Trait("Category", "Accessibility")]
    [Trait("KeyboardProof", "Required")]
    public async Task ClipboardExpander_SupportsKeyboardSelectionAndCollapse()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "sandbox" });
            await WaitUiAsync(run, () => FindById(run, "SandboxClipboardExpander") is not null, "Clipboard expander");
            var expander = FindById(run, "SandboxClipboardExpander")!;
            var window = AppWindows(run).Single(w => w.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SandboxClipboardExpander")) is not null);
            Assert.Equal("📋 Clipboard", expander.Current.Name);
            var pattern = (ExpandCollapsePattern)expander.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            if (pattern.Current.ExpandCollapseState != ExpandCollapseState.Expanded) pattern.Expand();
            var radios = expander.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton)).Cast<AutomationElement>().ToArray();
            Assert.Equal(4, radios.Length);
            output.WriteLine($"Clipboard radio enabled states: {string.Join(", ", radios.Select(r => r.Current.IsEnabled))}.");
            Assert.All(radios, radio => Assert.True(radio.Current.IsEnabled,
                "Clipboard keyboard proof requires enabled sandbox controls; check this host's support state."));
            var hwnd = new IntPtr(window.Current.NativeWindowHandle);
            await NativeKeyboardProof.ActivateAsync(hwnd);
            radios[0].SetFocus();
            await WaitUiAsync(run, () => Automation.Compare(radios[0], AutomationElement.FocusedElement), "first Clipboard option focused");
            // Individual WinUI RadioButtons move focus with arrows; Space commits the choice.
            NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Down);
            await WaitUiAsync(run, () => Automation.Compare(radios[1], AutomationElement.FocusedElement), "next Clipboard option focused");
            NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Space);
            await WaitUiAsync(run, () => ((SelectionItemPattern)radios[1].GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected &&
                !((SelectionItemPattern)radios[0].GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected,
                "Space selects Read and unselects None");
            Assert.True(Automation.Compare(radios[1], AutomationElement.FocusedElement), "selected Clipboard option remains focused");
            // GroupName makes selection exclusive, but each standalone radio remains a tab stop.
            foreach (var index in new[] { 2, 3 })
            {
                NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Tab);
                await WaitUiAsync(run, () => Automation.Compare(radios[index], AutomationElement.FocusedElement),
                    $"Tab focuses Clipboard option {index}");
                AssertReadSelection(radios, "forward Tab");
            }
            NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Tab);
            await WaitUiAsync(run, () => !radios.Any(radio => Automation.Compare(radio, AutomationElement.FocusedElement)), "focus leaves Clipboard options");
            var after = AutomationElement.FocusedElement;
            Assert.NotNull(after);
            Assert.DoesNotContain(radios, radio => Automation.Compare(radio, after));
            AssertReadSelection(radios, "outside group");
            foreach (var index in new[] { 3, 2, 1 })
            {
                NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Tab, shift: true);
                await WaitUiAsync(run, () => Automation.Compare(radios[index], AutomationElement.FocusedElement),
                    $"Shift+Tab focuses Clipboard option {index}");
                AssertReadSelection(radios, "reverse Shift+Tab");
            }
            Assert.True(Automation.Compare(radios[1], AutomationElement.FocusedElement), "Reverse traversal returns to Read without changing selection.");
            LogClipboardState("before Collapse");
            pattern.Collapse();
            LogClipboardState("after Collapse");
            try
            {
                // UIA command completion does not imply the native collapse transition has settled.
                await WaitUiAsync(run, ClipboardIsCollapsedAndHidden, "Clipboard collapsed and options hidden");
            }
            catch
            {
                LogClipboardState("collapse wait failed");
                throw;
            }
            expander = FindById(run, "SandboxClipboardExpander")!;
            pattern = (ExpandCollapsePattern)expander.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            expander.SetFocus();
            await WaitUiAsync(run, () => Automation.Compare(expander, AutomationElement.FocusedElement), "collapsed Clipboard expander focused");
            LogClipboardState("after expander focus");
            Assert.True(ClipboardIsCollapsedAndHidden(), "Focusing the collapsed Clipboard expander must not reopen its options.");
            NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Tab);
            await WaitUiAsync(run, () => !Automation.Compare(expander, AutomationElement.FocusedElement), "focus leaves collapsed expander");
            LogClipboardState("after Tab");
            Assert.True(ClipboardIsCollapsedAndHidden(), "Tab must leave Clipboard options collapsed and hidden.");
            after = AutomationElement.FocusedElement;
            Assert.NotNull(after);
            Assert.DoesNotContain(radios, radio => Automation.Compare(radio, after));
            pattern.Expand();
            await WaitUiAsync(run, () =>
            {
                var live = FindById(run, "SandboxClipboardExpander");
                return live is not null &&
                    ((ExpandCollapsePattern)live.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState == ExpandCollapseState.Expanded &&
                    ClipboardRadios(live) is { Length: 4 } options && options.All(radio => !radio.Current.IsOffscreen);
            }, "Clipboard expanded and options visible again");
            radios = ClipboardRadios(FindById(run, "SandboxClipboardExpander")!);
            AssertReadSelection(radios, "reexpanded group after Tab exclusion");

            await run.InvokeAsync("app.navigate", new { page = "permissions" });
            await WaitUiAsync(run, () => FindById(run, "PermissionsPageMarker") is { Current.IsOffscreen: false }, "Permissions page active");
            await run.InvokeAsync("app.navigate", new { page = "sandbox" });
            await WaitUiAsync(run, () => FindById(run, "SandboxClipboardExpander") is not null, "Clipboard page reactivated");
            var restored = FindById(run, "SandboxClipboardExpander")!;
            var restoredPattern = (ExpandCollapsePattern)restored.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            if (restoredPattern.Current.ExpandCollapseState != ExpandCollapseState.Expanded) restoredPattern.Expand();
            var restoredRadios = restored.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton)).Cast<AutomationElement>().ToArray();
            Assert.Equal(4, restoredRadios.Length);
            await WaitUiAsync(run, () => restoredRadios.Select((radio, index) =>
                    ((SelectionItemPattern)radio.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected == (index == 1)).All(matches => matches),
                "keyboard-selected Clipboard permission persists after page reactivation");

            AutomationElement[] ClipboardRadios(AutomationElement owner) => owner.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton)).Cast<AutomationElement>().ToArray();

            bool ClipboardIsCollapsedAndHidden()
            {
                var live = FindById(run, "SandboxClipboardExpander");
                return live is not null &&
                    ((ExpandCollapsePattern)live.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState == ExpandCollapseState.Collapsed &&
                    ClipboardRadios(live).All(radio => radio.Current.IsOffscreen);
            }

            void LogClipboardState(string phase)
            {
                try
                {
                    var live = FindById(run, "SandboxClipboardExpander");
                    if (live is null)
                    {
                        output.WriteLine($"Clipboard {phase}: peer unavailable.");
                        return;
                    }
                    var state = ((ExpandCollapsePattern)live.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState;
                    var options = ClipboardRadios(live);
                    var focused = AutomationElement.FocusedElement;
                    var inTargetWindow = false;
                    if (focused is not null && focused.Current.ProcessId == run.AppProcessId)
                    {
                        for (var current = focused; current is not null; current = TreeWalker.ControlViewWalker.GetParent(current))
                        {
                            if (!Automation.Compare(current, window)) continue;
                            inTargetWindow = true;
                            break;
                        }
                    }
                    // Only known synthetic controls and ownership booleans, never arbitrary desktop content.
                    output.WriteLine($"Clipboard {phase}: state={state}; options={options.Length}; targetWindow={inTargetWindow}.");
                    for (var index = 0; index < options.Length; index++)
                    {
                        var radio = options[index];
                        output.WriteLine($"Clipboard option {index}: offscreen={radio.Current.IsOffscreen}; focused={radio.Current.HasKeyboardFocus}; " +
                            $"selected={((SelectionItemPattern)radio.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected}.");
                    }
                }
                catch (ElementNotAvailableException)
                {
                    output.WriteLine($"Clipboard {phase}: peer unavailable during transition.");
                }
            }

            void AssertReadSelection(AutomationElement[] options, string phase)
            {
                var focused = AutomationElement.FocusedElement;
                Assert.NotNull(focused);
                Assert.Equal(run.AppProcessId, focused.Current.ProcessId);
                var inTargetWindow = false;
                for (var current = focused; current is not null; current = TreeWalker.ControlViewWalker.GetParent(current))
                {
                    if (!Automation.Compare(current, window)) continue;
                    inTargetWindow = true;
                    break;
                }
                Assert.True(inTargetWindow, "Clipboard traversal must remain in its owned window.");
                var selected = options.Select(radio =>
                    ((SelectionItemPattern)radio.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected).ToArray();
                Assert.Equal(new[] { false, true, false, false }, selected);
                // Report only fixture ordinals and booleans, never arbitrary focused-control properties.
                output.WriteLine($"Clipboard {phase}: focused option={Array.FindIndex(options, radio => Automation.Compare(radio, focused))}; " +
                    $"selected={string.Join(",", selected)}; targetWindow={inTargetWindow}.");
            }
        }, scenario: GatewayScenario.CreateAccessibilityBaseline());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    [Trait("Category", "Accessibility")]
    [Trait("KeyboardProof", "Required")]
    public async Task SessionsRows_ArrowNavigationEntersLaterRowActions()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "sessions" });
            await WaitUiAsync(run, () => FindText(run, GatewayScenario.MainTitle) is not null
                && FindText(run, GatewayScenario.LongTitle) is not null, "two sessions");
            AutomationElement Row(string title)
            {
                var node = FindText(run, title)!;
                while (node.Current.ControlType != ControlType.ListItem)
                    node = TreeWalker.ControlViewWalker.GetParent(node) ?? throw new InvalidOperationException("Session title has no ListItem ancestor.");
                return node;
            }
            var first = Row(GatewayScenario.MainTitle);
            var second = Row(GatewayScenario.LongTitle);
            AutomationElement Action(AutomationElement row, string name) => row.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, name)))
                ?? throw new InvalidOperationException($"Missing {name} on session row.");
            Assert.False(Automation.Compare(Action(first, "Open in chat"), Action(second, "Open in chat")));
            Assert.False(Automation.Compare(Action(first, "More actions"), Action(second, "More actions")));
            var window = AppWindows(run).Single(w => w.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SessionsPageMarker")) is not null);
            var hwnd = new IntPtr(window.Current.NativeWindowHandle);
            await NativeKeyboardProof.ActivateAsync(hwnd);
            if (first.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll)) ((ScrollItemPattern)scroll).ScrollIntoView();
            first.SetFocus();
            await WaitUiAsync(run, () => Automation.Compare(first, AutomationElement.FocusedElement), "first session row focused");
            var previous = AutomationElement.FocusedElement.GetRuntimeId();
            NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Down);
            await WaitUiAsync(run, () => AutomationElement.FocusedElement is { } next
                && !next.GetRuntimeId().SequenceEqual(previous), "focus changes after Down");
            var focused = AutomationElement.FocusedElement;
            output.WriteLine($"After Down: name=[{focused.Current.Name}] type=[{focused.Current.ControlType.ProgrammaticName}] id=[{focused.Current.AutomationId}].");
            Assert.True(Automation.Compare(second, focused), "Down from the first row should reach the second row.");
            foreach (var expected in new[] { "Open in chat", "More actions" })
            {
                previous = focused.GetRuntimeId();
                NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Tab);
                await WaitUiAsync(run, () => AutomationElement.FocusedElement is { } next
                    && !next.GetRuntimeId().SequenceEqual(previous), "next session action focus");
                focused = AutomationElement.FocusedElement;
                output.WriteLine($"After Tab: name=[{focused.Current.Name}] type=[{focused.Current.ControlType.ProgrammaticName}].");
                var expectedPeer = second.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, expected)));
                Assert.NotNull(expectedPeer);
                Assert.True(Automation.Compare(expectedPeer, focused), $"Tab should reach this row's {expected} action.");
            }
            var occurrence = run.Gateway.Requests.Count(r => r.Method == "sessions.list") + 1;
            Invoke(FindInApp(run, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "Refresh sessions")))!);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await run.Gateway.WaitForCompletedRequestAsync("sessions.list", occurrence: occurrence, cancellationToken: deadline.Token);
            await WaitUiAsync(run, () => FindText(run, GatewayScenario.MainTitle) is not null
                && FindText(run, GatewayScenario.LongTitle) is not null, "sessions after refresh");
            await run.InvokeAsync("app.navigate", new { page = "settings" });
            await WaitUiAsync(run, () => FindById(run, "SettingsPageMarker") is not null, "leave sessions");
            await run.InvokeAsync("app.navigate", new { page = "sessions" });
            await WaitUiAsync(run, () => FindText(run, GatewayScenario.MainTitle) is not null
                && FindText(run, GatewayScenario.LongTitle) is not null, "sessions after re-entry");
            Assert.NotNull(Action(Row(GatewayScenario.MainTitle), "Open in chat"));
            Assert.NotNull(Action(Row(GatewayScenario.LongTitle), "More actions"));
        }, scenario: GatewayScenario.CreateAccessibilityBaseline());
    }


}
