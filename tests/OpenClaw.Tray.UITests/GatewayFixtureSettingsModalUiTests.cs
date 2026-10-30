using System.Windows.Automation;
using System.Diagnostics;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.SetupEngine;
using OpenClaw.Shared;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiTheory]
    [InlineData(null, false)]
    [InlineData(SetupNativeDestination.Channels, false)]
    [InlineData(SetupNativeDestination.Skills, false)]
    [InlineData(SetupNativeDestination.Chat, false)]
    [InlineData(SetupNativeDestination.Chat, true)]
    [Trait("Category", "GatewayFixture")]
    public async Task SharedRootDialog_DefersNavigationAndRejectsNativeBinding(
        SetupNativeDestination? destination, bool settingsOrigin)
    {
        var scenario = GatewayScenario.CreateNativeSetup((method, _) => method == "openclaw.setup.verify"
            ? new { ok = true, modelRef = "fixture/browse", latencyMs = 1 }
            : throw new InvalidOperationException($"Unexpected setup operation: {method}"));
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitVerifiedChatAsync(run);
            if (destination == SetupNativeDestination.Chat)
                await SelectSessionAsync(run, GatewayScenario.OtherSessionTitle, GatewayScenario.OtherSessionKey);
            var window = FindHub(run);
            var hwnd = window.Current.NativeWindowHandle;
            var composer = FindById(run, "ChatComposerInput")!;
            var identity = composer.GetRuntimeId();
            const string draft = "Keep this draft behind the shared-root dialog";
            ((ValuePattern)composer.GetCurrentPattern(ValuePattern.Pattern)).SetValue(draft);
            if (settingsOrigin)
            {
                await run.InvokeAsync("app.navigate", new { page = "sessions" });
                await WaitUiAsync(run, () => FindText(run, GatewayScenario.MainTitle) is not null, "Sessions page");
                var row = FindText(run, GatewayScenario.MainTitle)!;
                for (var depth = 0; depth < 15 && row.Current.ControlType != ControlType.ListItem; depth++)
                    row = TreeWalker.ControlViewWalker.GetParent(row)
                        ?? throw new InvalidOperationException("Session title has no list-item ancestor.");
                Invoke(row.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "More actions"))));
                var reset = new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                    new PropertyCondition(AutomationElement.NameProperty, "Reset"));
                await WaitUiAsync(run, () => FindInApp(run, reset) is not null, "session action menu");
                Invoke(FindInApp(run, reset)!);
                await WaitUiAsync(run, () => FindText(run, "Reset session?") is { Current.IsOffscreen: false },
                    "Settings-origin ContentDialog");
            }
            else
            {
                ((ExpandCollapsePattern)FindById(run, "WorkspaceAssistantSelector")!
                    .GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
                await WaitUiAsync(run, () => FindById(run, "WorkspaceNewAgent") is not null, "new assistant action");
                Invoke(FindById(run, "WorkspaceNewAgent")!);
                await WaitUiAsync(run, () => FindById(run, "AgentCreationDialog") is not null, "chat-origin ContentDialog");
            }
            var dialogIdentity = FindButton(run, "Cancel")!.GetRuntimeId();
            var route = destination switch
            {
                SetupNativeDestination.Channels => "channels",
                SetupNativeDestination.Skills => "skills",
                SetupNativeDestination.Chat => "chat",
                _ => "settings"
            };
            var marker = destination switch
            {
                SetupNativeDestination.Channels => "ChannelsPageMarker",
                SetupNativeDestination.Skills => "SkillsPageMarker",
                SetupNativeDestination.Chat => "ChatComposerInput",
                _ => "SettingsPageMarker"
            };
            if (destination is { } target)
            {
                var start = run.Profile.CreateStartInfo(
                    Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_APP")!, run.McpPort,
                    IssueSyntheticHandoff(run.Profile, target));
                using var sender = Process.Start(start) ?? throw new InvalidOperationException("Handoff forwarding failed.");
                await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "handoff-sender.json"),
                    JsonSerializer.Serialize(new { pid = sender.Id, startedUtc = sender.StartTime.ToUniversalTime(),
                        executable = start.FileName, run.Profile.DataDirectory, run.McpPort }));
                try
                {
                    await sender.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    Assert.Equal(0, sender.ExitCode);
                }
                finally
                {
                    if (!sender.HasExited)
                    {
                        sender.Kill(entireProcessTree: true);
                        await sender.WaitForExitAsync();
                    }
                }
                await run.WaitForAsync(async () =>
                {
                    var receiptPath = Path.Combine(run.Profile.DataDirectory, "setup-dashboard-handoff", "pending.json");
                    if (!File.Exists(receiptPath)) return target == SetupNativeDestination.Chat;
                    if (target == SetupNativeDestination.Chat) return false;
                    using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath));
                    return receipt.RootElement.GetProperty("State").GetString() == "retry";
                }, "native request settled after rejecting the unavailable destination");
                Assert.Contains(run.Gateway.Requests,
                    request => request.Method == "openclaw.setup.verify" && request.Outcome == "ok");
                var registry = new GatewayRegistry(run.Profile.DataDirectory);
                var identityPath = registry.GetIdentityDirectory(run.Profile.GatewayId);
                File.Delete(Path.Combine(identityPath, "device-key-ed25519.json"));
                new DeviceIdentity(identityPath).Initialize();
            }
            else
                await run.InvokeAsync("app.navigate", new { page = route });

            if (destination != SetupNativeDestination.Chat)
                Assert.Null(FindById(run, marker));
            Assert.Null(FindById(run, "ConnectionPageMarker"));
            Assert.Equal(dialogIdentity, FindButton(run, "Cancel")!.GetRuntimeId());
            if (settingsOrigin)
            {
                Assert.False(FindById(run, "SessionsPageMarker")!.Current.IsOffscreen);
                Assert.True(FindById(run, "ChatComposerInput") is null or { Current.IsOffscreen: true });
            }
            else
            {
                Assert.Equal(identity, FindById(run, "ChatComposerInput")!.GetRuntimeId());
                Assert.Equal(draft, ((ValuePattern)FindById(run, "ChatComposerInput")!
                    .GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
                if (destination == SetupNativeDestination.Chat)
                    Assert.True(SessionSelected(run, GatewayScenario.OtherSessionTitle));
            }
            Assert.Null(FindById(run, "ChatNativeSetupError"));
            OwnedWindowCapture.Save(run, window, "shared-root-dialog-retains-origin.png", FindButton(run, "Cancel")!);
            Invoke(FindButton(run, "Cancel")!);
            var afterDialogMarker = destination is null ? marker : "ConnectionPageMarker";
            await WaitUiAsync(run, () => FindById(run, afterDialogMarker) is { Current.IsOffscreen: false },
                "deferred ordinary Settings or native-failure recovery destination");
            var destinationWindow = Assert.Single(AppWindows(run), candidate => candidate.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, afterDialogMarker)) is { Current.IsOffscreen: false });
            Assert.Equal(hwnd, destinationWindow.Current.NativeWindowHandle);
            await run.InvokeAsync("app.navigate", new { page = route });
            await WaitUiAsync(run, () => FindById(run, marker) is { Current.IsOffscreen: false }, "unbound ordinary destination");
            Assert.Null(FindText(run, "The verified Gateway or agent changed. Return to the original Gateway and reopen AI setup. No other account was opened."));
            Assert.Null(FindById(run, "ChatNativeSetupError"));
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is { Current.IsOffscreen: false }, "retained chat");
            Assert.Equal(identity, FindById(run, "ChatComposerInput")!.GetRuntimeId());
            Assert.Equal(draft, ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
            if (destination == SetupNativeDestination.Chat)
                Assert.True(SessionSelected(run, GatewayScenario.OtherSessionTitle));
            await run.InvokeAsync("app.navigate", new { page = route });
            await WaitUiAsync(run, () => FindById(run, marker) is { Current.IsOffscreen: false }, "later ordinary destination visit");
            Assert.Null(FindText(run, "The verified Gateway or agent changed. Return to the original Gateway and reopen AI setup. No other account was opened."));
            Assert.Null(FindById(run, "ChatNativeSetupError"));
            OwnedWindowCapture.Save(run, destinationWindow, "shared-root-dialog-later-unbound-destination.png", FindById(run, marker)!);
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "agents.create");
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "sessions.reset");
        }, scenario: scenario);
    }

    [GatewayFixtureUiTheory]
    [InlineData(null, "ConnectionPageMarker")]
    [InlineData("connection", "ConnectionPageMarker")]
    [InlineData("permissions", "PermissionsPageMarker")]
    [Trait("Category", "GatewayFixture")]
    public async Task SettingsDisconnectPruning_WaitsForRealDialogBeforeRenderingFallbackOrLatestIntent(
        string? queuedRoute, string expectedMarker)
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "sessions" });
            await WaitUiAsync(run, () => FindText(run, GatewayScenario.MainTitle) is not null,
                "populated Sessions page");
            Assert.False(FindById(run, "NavSessions")!.Current.IsOffscreen);
            var window = Assert.Single(AppWindows(run), candidate =>
                candidate.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "SessionsPageMarker")) is not null);
            var hwnd = window.Current.NativeWindowHandle;
            var row = FindText(run, GatewayScenario.MainTitle)!;
            for (var depth = 0; depth < 15 && row.Current.ControlType != ControlType.ListItem; depth++)
                row = TreeWalker.ControlViewWalker.GetParent(row)
                    ?? throw new InvalidOperationException("Session title has no list-item ancestor.");
            var more = row.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "More actions")));
            Assert.NotNull(more);
            Invoke(more);
            await WaitUiAsync(run, () => FindInApp(run, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                new PropertyCondition(AutomationElement.NameProperty, "Reset"))) is not null, "session action menu");
            Invoke(FindInApp(run, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                new PropertyCondition(AutomationElement.NameProperty, "Reset")))!);
            await WaitUiAsync(run, () => FindText(run, "Reset session?") is { Current.IsOffscreen: false },
                "real Settings ContentDialog");
            var cancel = FindButton(run, "Cancel")!;
            var dialogIdentity = cancel.GetRuntimeId();
            OwnedWindowCapture.Save(run, window, "01-settings-dialog-before-disconnect.png",
                FindText(run, "Reset session?")!, cancel);

            // Stop only this run's synthetic server so reconnect cannot hide the prune transition.
            await run.Gateway.DisposeAsync();
            await run.WaitForAsync(async () =>
                (await run.InvokeAsync("app.status")).GetProperty("operatorState").GetString() != "Connected",
                "fixture operator disconnect");
            await WaitUiAsync(run, () => FindById(run, "NavSessions") is null or { Current.IsOffscreen: true },
                "Gateway navigation pruned after disconnect");
            if (queuedRoute is not null)
                await run.InvokeAsync("app.navigate", new { page = queuedRoute });

            Assert.NotNull(FindById(run, "SessionsPageMarker"));
            Assert.Null(FindById(run, "ConnectionPageMarker"));
            Assert.Null(FindById(run, "PermissionsPageMarker"));
            Assert.Equal(dialogIdentity, FindButton(run, "Cancel")!.GetRuntimeId());
            OwnedWindowCapture.Save(run, window, "02-settings-dialog-retains-pruned-page.png",
                FindText(run, "Reset session?")!, FindButton(run, "Cancel")!);

            Invoke(FindButton(run, "Cancel")!);
            await WaitUiAsync(run, () => FindById(run, expectedMarker) is { Current.IsOffscreen: false },
                "required fallback or latest intent after dialog closes");
            Assert.Null(FindText(run, "Reset session?"));
            Assert.Null(FindById(run, "SessionsPageMarker"));
            var destinationWindow = Assert.Single(AppWindows(run), candidate =>
                !candidate.Current.IsOffscreen && candidate.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, expectedMarker))
                    is { Current.IsOffscreen: false });
            Assert.Equal(hwnd, destinationWindow.Current.NativeWindowHandle);
            var mainWindow = Assert.Single(AppWindows(run), candidate =>
                !candidate.Current.IsOffscreen && candidate.FindFirst(TreeScope.Descendants,
                    new OrCondition(
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspaceTitleBar"),
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsNavigationToolbar"))) is not null);
            Assert.Equal(hwnd, mainWindow.Current.NativeWindowHandle);
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "sessions.reset");
            OwnedWindowCapture.Save(run, destinationWindow, "03-settings-dialog-closed-destination.png",
                destinationWindow.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, expectedMarker)));
        });
    }
}
