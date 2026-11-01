using System.Windows.Automation;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
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
    public async Task UsageProviderRow_ExposesItsVisibleDataOnTheListItemPeer()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "usage" });
            await WaitUiAsync(run, () => FindById(run, "UsagePageMarker") is not null
                && FindText(run, "Fixture Provider") is not null
                && FindText(run, "Fixture quota warning") is not null, "populated provider row");
            var text = FindText(run, "Fixture Provider")!;
            var walker = TreeWalker.ControlViewWalker;
            var row = walker.GetParent(text);
            while (row is not null && row.Current.ControlType != ControlType.ListItem)
                row = walker.GetParent(row);
            Assert.NotNull(row);
            output.WriteLine($"Provider row: name=[{row.Current.Name}], id=[{row.Current.AutomationId}], class=[{row.Current.ClassName}]");
            foreach (var expected in new[] { "Fixture Provider", "Pro", "37% used", "Fixture quota warning" })
                Assert.Contains(expected, row.Current.Name, StringComparison.Ordinal);
        }, scenario: GatewayScenario.CreateAccessibilityBaseline());
    }


    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    [Trait("Category", "Accessibility")]
    public async Task BindingsRefresh_HasAnAccessibleNameOnTheEnabledButton()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "bindings" });
            await WaitUiAsync(run, () => FindById(run, "BindingsPageMarker") is not null
                && FindText(run, "fixture") is not null, "populated binding route");
            var button = FindById(run, "RefreshButton");
            Assert.NotNull(button);
            Assert.Equal(ControlType.Button, button.Current.ControlType);
            Assert.True(button.Current.IsEnabled);
            Assert.False(button.Current.IsOffscreen);
            output.WriteLine($"Bindings refresh: name=[{button.Current.Name}], id=[{button.Current.AutomationId}]");
            Assert.Equal("Refresh", button.Current.Name);
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
        }, scenario: GatewayScenario.CreateAccessibilityBaseline());
    }


    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    [Trait("Category", "Accessibility")]
    public async Task ConfigActions_ExposeNamesAfterSyntheticLocalEdit()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "config" });
            await WaitUiAsync(run, () => FindById(run, "ConfigTree") is not null && FindInApp(run, new PropertyCondition(AutomationElement.NameProperty, "📁 Messages")) is not null, "fixture config tree");
            Invoke(FindInApp(run, new PropertyCondition(AutomationElement.NameProperty, "📁 Messages"))!);
            await WaitUiAsync(run, () => FindText(run, "Fixture labels") is not null, "synthetic scalar array editor");
            var edits = FindHub(run).FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
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

}
