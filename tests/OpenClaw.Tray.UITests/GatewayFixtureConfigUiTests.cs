using System.Text.Json;
using System.Windows.Automation;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task ConfigEditor_SaveAppliesExactArrayReplacementAndRefreshesVisibleForm()
    {
        var configuration = new ConfigEditingFixture();
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "config" });
            await WaitUiAsync(run, () => FindInApp(run, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem),
                new PropertyCondition(AutomationElement.NameProperty, "\U0001F4C1 Agents"))) is not null,
                "configuration Agents section");
            var section = FindInApp(run, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem),
                new PropertyCondition(AutomationElement.NameProperty, "\U0001F4C1 Agents")))!;
            var window = AppWindows(run).Single(candidate => candidate.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "ConfigPageMarker")) is not null);
            ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern))
                .SetWindowVisualState(WindowVisualState.Maximized);
            // TreeView selection alone does not raise the production ItemInvoked event.
            ClickOwnedFixtureControl(run, window, section);
            await WaitUiAsync(run, () => ConfigArrayEditor(run, "research") is not null, "editable agent array");
            ((ValuePattern)ConfigArrayEditor(run, "research")!.GetCurrentPattern(ValuePattern.Pattern))
                .SetValue("""[{"id":"main"}]""");
            await WaitUiAsync(run, () => FindButton(run, "Save Changes")?.Current.IsEnabled == true, "enabled Save Changes");
            run.Gateway.HoldConfigApplies();
            Invoke(FindButton(run, "Save Changes")!);
            await run.WaitForAsync(() => Task.FromResult(run.Gateway.Requests.Any(request =>
                request.Method == "config.apply")), "dispatched config.apply");
            await WaitUiAsync(run, () => FindButton(run, "Save Changes")?.Current.IsEnabled == false,
                "save disabled until Gateway acknowledgement");
            if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
                OwnedWindowCapture.Save(run, window, "config-apply-pending.png");
            run.Gateway.ReleaseConfigApplies();
            await run.WaitForAsync(() => Task.FromResult(run.Gateway.Requests.Any(request =>
                request.Method == "config.apply" && request.Outcome != "pending")), "completed config.apply");
            Assert.Equal("ok", Assert.Single(run.Gateway.Requests, request => request.Method == "config.apply").Outcome);
            await WaitUiAsync(run, () => FindText(run, "Saving configuration") is null &&
                ConfigArrayEditor(run, "main") is not null, "refreshed configuration form");
            var array = ConfigArrayEditor(run, "main")!;
            Assert.False(array.Current.IsOffscreen);
            var value = ((ValuePattern)array.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
            using var parsed = JsonDocument.Parse(value);
            Assert.Single(parsed.RootElement.EnumerateArray());
            Assert.Equal("main", parsed.RootElement[0].GetProperty("id").GetString());
            var stored = JsonSerializer.SerializeToElement(configuration.Snapshot());
            Assert.Single(stored.GetProperty("parsed").GetProperty("agents").GetProperty("list").EnumerateArray());
            Assert.Single(run.Gateway.Requests, request => request.Method == "config.apply");
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "config.patch");
            if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
                OwnedWindowCapture.Save(run, window, "config-apply-refreshed.png", array);
        }, scenario: GatewayScenario.CreateConfigEditing(configuration));
    }

    private static AutomationElement? ConfigArrayEditor(OpenClaw.GatewayFixtureHost.GatewayFixtureRun run, string id)
    {
        foreach (var window in AppWindows(run))
        {
            foreach (AutomationElement element in window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
            {
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
                {
                    var value = ((ValuePattern)pattern).Current;
                    if (!value.IsReadOnly &&
                        value.Value.TrimStart().StartsWith("[", StringComparison.Ordinal) &&
                        value.Value.Contains($"\"{id}\"", StringComparison.Ordinal))
                        return element;
                }
            }
        }
        return null;
    }
}
