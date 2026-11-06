using System.Windows.Automation;
using OpenClaw.TestSupport.Gateway;
using OpenClaw.GatewayFixtureHost;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
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
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "usage" });
            await WaitUiAsync(run, () => FindText(run, "Fixture Provider") is not null
                && FindText(run, "Fixture Empty") is not null && FindText(run, "2026-10-31") is not null
                && FindText(run, "2026-10-30") is not null, "two providers and two daily rows");
            AutomationElement RowFor(string text)
            {
                var current = FindText(run, text) ?? throw new InvalidOperationException($"Missing synthetic row text {text}.");
                while (current.Current.ControlType != ControlType.ListItem)
                    current = TreeWalker.ControlViewWalker.GetParent(current)
                        ?? throw new InvalidOperationException($"No ListItem peer for {text}.");
                return current;
            }
            string ReadRow(string text)
            {
                var row = RowFor(text);
                if (row.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll)) ((ScrollItemPattern)scroll).ScrollIntoView();
                return row.Current.Name;
            }
            async Task<string> SelectPeriodAndReadRow(string periodId, string expectedCost)
            {
                var period = FindById(run, periodId);
                Assert.NotNull(period);
                Invoke(period);
                await WaitUiAsync(run, () => FindText(run, "2026-10-31") is not null
                    && RowFor("2026-10-31").FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.NameProperty, expectedCost)) is not null
                    && ReadRow("2026-10-31").Contains(expectedCost, StringComparison.Ordinal),
                    $"period row and accessible name updated to {expectedCost}");
                return ReadRow("2026-10-31");
            }
            var provider = ReadRow("Fixture Provider");
            var empty = ReadRow("Fixture Empty");
            var first = await SelectPeriodAndReadRow("Period7DaysItem", "$0.75");
            var second = ReadRow("2026-10-30");
            output.WriteLine($"Initial rows: provider=[{provider}], empty=[{empty}], day1=[{first}], day2=[{second}].");
            scenario.AdvanceAccessibilityUsage();
            await run.InvokeAsync("app.navigate", new { page = "settings" });
            await WaitUiAsync(run, () => FindById(run, "SettingsPageMarker") is not null, "leave Usage");
            await run.InvokeAsync("app.navigate", new { page = "usage" });
            await WaitUiAsync(run, () => FindText(run, "Fixture changed warning") is not null, "changed usage snapshot");
            var changedProvider = ReadRow("Fixture Provider");
            var changedDay = await SelectPeriodAndReadRow("Period7DaysItem", "$1.50");
            var thirtyDayRow = await SelectPeriodAndReadRow("Period30DaysItem", "$2.50");
            var sevenDayRow = await SelectPeriodAndReadRow("Period7DaysItem", "$1.50");
            output.WriteLine($"Changed rows: provider=[{changedProvider}], day1=[{changedDay}], 30 days=[{thirtyDayRow}], 7 days=[{sevenDayRow}].");
            foreach (var expected in new[] { "Fixture Provider", "Pro", "37% used", "Fixture quota warning" })
                Assert.Contains(expected, provider, StringComparison.Ordinal);
            Assert.Equal("Fixture Empty", empty);
            Assert.Contains("2026-10-31", first, StringComparison.Ordinal);
            Assert.Contains("$0.75", first, StringComparison.Ordinal);
            Assert.Contains("2026-10-30", second, StringComparison.Ordinal);
            Assert.Contains("$0.50", second, StringComparison.Ordinal);
            foreach (var expected in new[] { "Fixture Provider", "Pro", "53% used", "Fixture changed warning" })
                Assert.Contains(expected, changedProvider, StringComparison.Ordinal);
            Assert.Contains("2026-10-31", changedDay, StringComparison.Ordinal);
            Assert.Contains("$1.50", changedDay, StringComparison.Ordinal);
            Assert.Contains("2026-10-31", thirtyDayRow, StringComparison.Ordinal);
            Assert.Contains("$2.50", thirtyDayRow, StringComparison.Ordinal);
            Assert.Equal(changedDay, sevenDayRow);
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
            NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Down);
            await WaitUiAsync(run, () => ((SelectionItemPattern)radios[1].GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected,
                "Down selects the next Clipboard option");
            await WaitUiAsync(run, () => Automation.Compare(radios[1], AutomationElement.FocusedElement), "selected Clipboard option focused");
            NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Tab);
            await WaitUiAsync(run, () => !radios.Any(radio => Automation.Compare(radio, AutomationElement.FocusedElement)), "focus leaves Clipboard options");
            var after = AutomationElement.FocusedElement;
            Assert.NotNull(after);
            Assert.DoesNotContain(radios, radio => Automation.Compare(radio, after));
            NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Tab, shift: true);
            await WaitUiAsync(run, () => Automation.Compare(radios[1], AutomationElement.FocusedElement), "focus returns to selected Clipboard option");
            Assert.True(Automation.Compare(radios[1], AutomationElement.FocusedElement), "Shift+Tab returns to the selected option.");
            pattern.Collapse();
            expander.SetFocus();
            await WaitUiAsync(run, () => Automation.Compare(expander, AutomationElement.FocusedElement), "collapsed Clipboard expander focused");
            NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Tab);
            await WaitUiAsync(run, () => !Automation.Compare(expander, AutomationElement.FocusedElement), "focus leaves collapsed expander");
            after = AutomationElement.FocusedElement;
            Assert.NotNull(after);
            Assert.DoesNotContain(radios, radio => Automation.Compare(radio, after));
            pattern.Expand();
            Assert.Equal(4, expander.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton)).Count);
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
