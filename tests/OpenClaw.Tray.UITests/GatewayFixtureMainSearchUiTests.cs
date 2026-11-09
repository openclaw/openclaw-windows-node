using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    private const string MainSearchDraft = "Keep this draft through main search";
    private const string OrderingProbe = "Z";
    private static readonly TimeSpan FixtureForegroundTimeout = TimeSpan.FromSeconds(5);

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task MainSearch_OpensFromButtonAndShortcuts_ClosesWithEscape_AndKeepsChatDraft()
    {
        await WithAppAsync(async run =>
        {
            var (window, composer, identity) = await PrepareChatWithDraftAsync(run);

            Invoke(FindById(run, "WorkspaceSearch")!);
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "search flyout from button");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox")!.Current.HasKeyboardFocus
                || FindById(run, "WorkspaceSearchBox")!.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.HasKeyboardFocusProperty, true)) is not null,
                "search box focus");
            OwnedWindowCapture.Save(run, window, "main-search-open.png", FindById(run, "WorkspaceSearchBox")!);
            await EscapeAndAssertDraftAsync(run, window, identity, expectComposerFocus: false);

            foreach (var chord in new[] { "^k", "^e" })
            {
                FocusComposer(run, window);
                Send(run, window, chord);
                await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, $"search flyout from {chord}");
                await EscapeAndAssertDraftAsync(run, window, identity, expectComposerFocus: true);
            }

            // Ctrl+F belongs to the chat content. The probe key is processed after Ctrl+F, so it
            // reaching the composer proves Ctrl+F was delivered and did not move focus to search.
            FocusComposer(run, window);
            Send(run, window, "^f");
            Send(run, window, OrderingProbe);
            await WaitUiAsync(run, () => ComposerText(run).Contains(OrderingProbe), "ordering probe reaches the composer after Ctrl+F");
            Assert.Null(FindById(run, "WorkspaceSearchBox"));
            Assert.Equal(MainSearchDraft, ComposerText(run).Replace(OrderingProbe, string.Empty));
            ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).SetValue(MainSearchDraft);
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task MainSearch_WorksWithCollapsedSidebar_AndRoutesToSettingsWithBackRetainingDraft()
    {
        await WithAppAsync(async run =>
        {
            var (window, _, identity) = await PrepareChatWithDraftAsync(run);

            Invoke(FindById(run, "WorkspaceTogglePane")!);
            await WaitUiAsync(run, () => FindById(run, "WorkspaceReopenPane") is { Current.IsOffscreen: false },
                "collapsed sidebar");
            SetForeground(window);
            Send(run, window, "^k");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "search with collapsed sidebar");
            OwnedWindowCapture.Save(run, window, "main-search-collapsed-sidebar.png", FindById(run, "WorkspaceSearchBox")!);
            await EscapeAndAssertDraftAsync(run, window, identity, expectComposerFocus: false);

            FocusComposer(run, window);
            Send(run, window, "^k");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "search before routing");
            SearchEdit(run, "WorkspaceSearchBox").SetFocus();
            Send(run, window, "Settings{ENTER}");
            await WaitUiAsync(run, () => FindById(run, "SettingsPageMarker") is { Current.IsOffscreen: false },
                "Settings route from main search");
            Assert.Null(FindById(run, "WorkspaceSearchBox"));
            OwnedWindowCapture.Save(run, window, "main-search-routed-to-settings.png", FindById(run, "SettingsPageMarker")!);

            Send(run, window, "%{LEFT}");
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is { Current.IsOffscreen: false },
                "Back to chat");
            // No SetFocus here: Back must restore composer focus by itself.
            await WaitUiAsync(run,             () => FindById(run, "ChatComposerInput") is { Current.HasKeyboardFocus: true },
                "composer focus restored by Back after search routing");
            Assert.Equal(identity, FindById(run, "ChatComposerInput")!.GetRuntimeId());
            Assert.Equal(MainSearchDraft, ComposerText(run));
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task MainSearch_InSettings_CtrlFAndCtrlKOpenTheSettingsSearchFlyoutOnly()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "settings" });
            await WaitUiAsync(run, () => FindById(run, "SettingsPageMarker") is { Current.IsOffscreen: false },
                "Settings page");
            var window = AppWindows(run).First(w => w.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsPageMarker")) is not null);
            foreach (var chord in new[] { "^f", "^k" })
            {
                SetForeground(window);
                window.FindAll(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true),
                    new PropertyCondition(AutomationElement.IsOffscreenProperty, false))).Cast<AutomationElement>()
                    .First(e => e.Current.AutomationId != "WorkspaceSearch").SetFocus();
                Assert.Null(FindById(run, "SettingsSearchBox"));
                Send(run, window, chord);
                await WaitUiAsync(run, () => FindById(run, "SettingsSearchBox") is not null, $"Settings search from {chord}");
                Assert.Null(FindById(run, "WorkspaceSearchBox"));
                if (chord == "^f")
                    OwnedWindowCapture.Save(run, window, "settings-ctrlf-search.png", FindById(run, "SettingsSearchBox")!);
                Send(run, window, "{ESC}");
                await WaitUiAsync(run, () => FindById(run, "SettingsSearchBox") is null, $"Settings search closed after {chord}");
            }
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task MainSearch_UnmatchedQueryIsIgnored_ThenValidQueryInSameFlyoutStillRoutes()
    {
        await WithAppAsync(async run =>
        {
            var (window, _, identity) = await PrepareChatWithDraftAsync(run);

            SetForeground(window);
            Send(run, window, "^k");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "search flyout");
            var edit = SearchEdit(run, "WorkspaceSearchBox");
            edit.SetFocus();
            const string unmatched = "zzqqxx-no-such-command";
            Send(run, window, unmatched);
            await WaitUiAsync(run, () => ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).Current.Value == unmatched,
                "unmatched query typed");
            OwnedWindowCapture.Save(run, window, "main-search-unmatched-query.png", FindById(run, "WorkspaceSearchBox")!);
            Send(run, window, "{ENTER}");

            // Keys are processed in order: the probe after Enter reaching the same box proves Enter was
            // handled, the flyout stayed open and focus stayed in the search box.
            Send(run, window, OrderingProbe);
            await WaitUiAsync(run, () => ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).Current.Value == unmatched + OrderingProbe,
                "probe typed after unmatched Enter");
            Assert.NotNull(FindById(run, "WorkspaceSearchBox"));
            Assert.True(FindById(run, "SettingsPageMarker") is null or { Current.IsOffscreen: true });
            ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue("Settings");
            edit.SetFocus();
            Send(run, window, "{ENTER}");
            await WaitUiAsync(run, () => FindById(run, "SettingsPageMarker") is { Current.IsOffscreen: false },
                "valid query routes after unmatched one");

            Send(run, window, "%{LEFT}");
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is { Current.IsOffscreen: false }, "Back to chat");
            Assert.Equal(identity, FindById(run, "ChatComposerInput")!.GetRuntimeId());
            Assert.Equal(MainSearchDraft, ComposerText(run));
        });
    }

    private static async Task<(AutomationElement Window, AutomationElement Composer, int[] Identity)> PrepareChatWithDraftAsync(
        GatewayFixtureRun run)
    {
        await run.InvokeAsync("app.navigate", new { page = "chat" });
        await WaitVerifiedChatAsync(run);
        var window = FindHub(run);
        var composer = FindById(run, "ChatComposerInput")!;
        ((ValuePattern)composer.GetCurrentPattern(ValuePattern.Pattern)).SetValue(MainSearchDraft);
        SetForeground(window);
        return (window, composer, composer.GetRuntimeId());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task MainSearch_MessageHitOpensItsNativeConversationWithoutSending()
    {
        await WithAppAsync(async run =>
        {
            var (window, _, _) = await PrepareChatWithDraftAsync(run);
            await SelectSessionAsync(run, GatewayScenario.LongTitle, GatewayScenario.LongSessionKey);
            Assert.Null(FindById(run, "WorkspaceSearchBox"));
            Send(run, window, "^e");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "icon-only search opened");
            ((ValuePattern)SearchEdit(run, "WorkspaceSearchBox").GetCurrentPattern(ValuePattern.Pattern)).SetValue("lighthouse");
            await WaitUiAsync(run, () =>
            {
                var results = FindById(run, "SearchResultsList");
                return results is not null && results.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "In messages")) is not null &&
                    results.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                        .Any(item => item.Current.Name.Contains(GatewayScenario.MainSentinel, StringComparison.Ordinal));
            }, "message result with Gateway snippet inside the results list");
            OwnedWindowCapture.Save(run, window, "main-search-message-result.png", FindById(run, "SearchResultsList")!);
            SearchEdit(run, "WorkspaceSearchBox").SetFocus();
            Send(run, window, "{ENTER}");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is null, "search closed after activation");
            await WaitComposerSelectionAsync(run, GatewayScenario.MainSessionKey);
            await WaitUiAsync(run, () => FindById(run, "WorkspaceNavHome") is { } home &&
                ((SelectionItemPattern)home.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected,
                "canonical main conversation selected as Home");
            Assert.Equal(MainSearchDraft, ComposerText(run));
        });
    }

    private static AutomationElement SearchEdit(GatewayFixtureRun run, string id)
    {
        var element = FindById(run, id)!;
        return element.Current.ControlType == ControlType.Edit ? element :
            element.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))!;
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    [Trait("Category", "Accessibility")]
    public async Task MainSearch_OpenPopupEmptyResultsAndAccessibleInputStatusPassAccessibility()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitVerifiedChatAsync(run);
            var window = FindHub(run);
            SetForeground(window);
            Invoke(FindById(run, "WorkspaceSearch")!);
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null &&
                FindById(run, "SearchResult_0") is { Current.IsOffscreen: false }, "empty search popup");
            Scan("empty");
            var input = (ValuePattern)SearchEdit(run, "WorkspaceSearchBox").GetCurrentPattern(ValuePattern.Pattern);
            input.SetValue("camera");
            await WaitUiAsync(run, () => FindById(run, "SearchResult_0")?.Current.Name.StartsWith("Camera.", StringComparison.Ordinal) == true &&
                FindById(run, "WorkspaceSearchBox")?.Current.ItemStatus.Contains("searching", StringComparison.OrdinalIgnoreCase) != true,
                "camera destination and settled source status");
            Scan("results");
            input.SetValue("zzqqxx-no-such-destination");
            await WaitUiAsync(run, () => FindById(run, "SearchResult_0") is null &&
                FindById(run, "WorkspaceSearchBox") is { Current.IsOffscreen: false } inputStatus &&
                !string.IsNullOrEmpty(inputStatus.Current.ItemStatus) &&
                !inputStatus.Current.ItemStatus.Contains("searching", StringComparison.OrdinalIgnoreCase), "unmatched accessible input status");
            Scan("status");

            void Scan(string state)
            {
                var popup = FindById(run, "WorkspaceSearchFlyout")!;
                Assert.DoesNotContain(popup.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ScrollBar)).Cast<AutomationElement>(),
                    bar => !bar.Current.IsOffscreen && bar.Current.Orientation == OrientationType.Horizontal);
                AxeHelper.AssertNoAccessibilityErrors(new(window.Current.NativeWindowHandle),
                    context: $"MainSearch-{state}", targetProcessId: run.AppProcessId);
                Assert.NotNull(FindById(run, "WorkspaceSearchBox"));
                Assert.Null(FindById(run, "SearchStatus"));
            }
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task MainSearch_ControlEnterIsInert_AndArrowActivationOnlyNavigatesToTheSetting()
    {
        await WithAppAsync(async run =>
        {
            var (window, _, _) = await PrepareChatWithDraftAsync(run);
            var before = await run.InvokeAsync("app.settings.get", new { name = "NodeCameraEnabled" });
            Send(run, window, "^e");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "search opened");
            var edit = SearchEdit(run, "WorkspaceSearchBox");
            ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue("camera");
            await WaitUiAsync(run, () => FindById(run, "SearchResult_0")?.Current.Name.StartsWith("Camera.", StringComparison.Ordinal) == true,
                "camera destination selected");
            edit.SetFocus();
            Send(run, window, "^{ENTER}");
            Send(run, window, OrderingProbe);
            await WaitUiAsync(run, () =>
            {
                var value = ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
                return value.Length == "camera".Length + OrderingProbe.Length &&
                    value.Replace(OrderingProbe, "", StringComparison.Ordinal) == "camera";
            },
                "Control+Enter leaves the search field active");
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "chat.send");
            ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue("camera");
            await WaitUiAsync(run, () => FindById(run, "SearchResult_0")?.Current.Name.StartsWith("Camera.", StringComparison.Ordinal) == true,
                "camera result restored");
            edit.SetFocus();
            Send(run, window, "{DOWN}");
            await WaitUiAsync(run, () => FindById(run, "SearchResult_0") is { Current.HasKeyboardFocus: true }, "native result row focus");
            Send(run, window, "{ENTER}");
            await WaitUiAsync(run, () => FindById(run, "PermissionsPageMarker") is { Current.IsOffscreen: false },
                "permission settings destination");
            var after = await run.InvokeAsync("app.settings.get", new { name = "NodeCameraEnabled" });
            Assert.Equal(before.GetRawText(), after.GetRawText());
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "chat.send");
            await run.InvokeAsync("app.navigate", new { page = "settings" });
            await WaitUiAsync(run, () => FindById(run, "SettingsPageMarker") is { Current.IsOffscreen: false },
                "different Settings page before searching for Camera");
            Assert.Null(FindInApp(run, new AndCondition(
                new PropertyCondition(AutomationElement.NameProperty, "Camera"),
                new PropertyCondition(AutomationElement.HasKeyboardFocusProperty, true))));
            SetForeground(window);
            Send(run, window, "^k");
            await WaitUiAsync(run, () => FindById(run, "SettingsSearchBox") is not null, "Settings search");
            edit = SearchEdit(run, "SettingsSearchBox");
            ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue("camera");
            await WaitUiAsync(run, () => FindById(run, "SearchResult_0")?.Current.Name
                .StartsWith("Camera.", StringComparison.Ordinal) == true, "Settings camera result");
            edit.SetFocus();
            Send(run, window, "{ENTER}");
            await WaitUiAsync(run, () => FindById(run, "SettingsSearchBox") is null &&
                FindInApp(run, new AndCondition(
                    new PropertyCondition(AutomationElement.NameProperty, "Camera"),
                    new PropertyCondition(AutomationElement.HasKeyboardFocusProperty, true))) is not null,
                "ordinary Settings search still reveals its target while its original flyout closes");
            Assert.Equal(before.GetRawText(),
                (await run.InvokeAsync("app.settings.get", new { name = "NodeCameraEnabled" })).GetRawText());
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task MainSearch_NativePageNamesAndIconsNavigateWithoutCommandPrefixes()
    {
        await WithAppAsync(async run =>
        {
            var (window, _, _) = await PrepareChatWithDraftAsync(run);
            var inputId = "WorkspaceSearchBox";
            foreach (var (query, title, marker) in new[]
            {
                ("About", "About", "AboutPageMarker"),
                ("General", "General", "SettingsPageMarker"),
                ("Notification preferences", "General", "SettingsPageMarker"),
                ("Models", "Models", "DefaultModelPageMarker"),
                ("Config", "Config", "ConfigPageMarker")
            })
            {
                Send(run, window, "^k");
                await WaitUiAsync(run, () => FindById(run, inputId) is not null, "shared search popup");
                var edit = SearchEdit(run, inputId);
                ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue(query);
                await WaitUiAsync(run, () => FindById(run, "SearchResult_0")?.Current.Name
                    .StartsWith(title + ".", StringComparison.Ordinal) == true &&
                    FindById(run, inputId)?.Current.ItemStatus.Contains("searching", StringComparison.OrdinalIgnoreCase) != true,
                    "plain localized destination name and settled results");
                var row = FindById(run, "SearchResult_0")!;
                Assert.DoesNotContain("Go to", row.Current.Name, StringComparison.OrdinalIgnoreCase);
                row.SetFocus();
                await WaitUiAsync(run, () => FindById(run, "SearchResult_0") is { Current.HasKeyboardFocus: true },
                    "rendered native destination row focus");
                OwnedWindowCapture.Save(run, window, $"search-native-{query.Replace(" ", "-").ToLowerInvariant()}.png", row);
                edit.SetFocus();
                Send(run, window, "{ENTER}");
                await WaitUiAsync(run, () => FindById(run, inputId) is null &&
                    FindById(run, marker) is { Current.IsOffscreen: false },
                    $"exact native {title} page");
                if (query == "Models")
                {
                    var heading = FindById(run, marker)!;
                    Assert.Equal("Models", heading.Current.Name);
                    await WaitForStablePageGeometryAsync(run, window, heading.Current.BoundingRectangle.Left);
                    OwnedWindowCapture.Save(run, window, "models-page.png", heading);
                }
                inputId = "SettingsSearchBox";
            }
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method is "config.patch" or "chat.send");
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task MainSearch_LongDescriptionsRemainFullyVisibleWithoutFooterMessages()
    {
        await WithAppAsync(async run =>
        {
            run.Gateway.OverrideSearchRead("skills.status", _ => new
            {
                skills = Enumerable.Range(1, 6).Select(number => new
                {
                    name = $"Layout skill {number}",
                    skillKey = $"layout-{number}",
                    description = $"Layout description {number}: first line.\nSecond line stays inside the result.\nFinal line remains readable.",
                    enabled = true
                }).ToArray()
            });
            var (window, _, _) = await PrepareChatWithDraftAsync(run);
            Send(run, window, "^k");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "popup");
            var edit = SearchEdit(run, "WorkspaceSearchBox");
            ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue("Layout skill");
            await WaitUiAsync(run, () => FindById(run, "SearchResult_4") is not null &&
                FindById(run, "WorkspaceSearchBox")?.Current.ItemStatus.Contains("searching", StringComparison.OrdinalIgnoreCase) != true,
                "long skill results and settled status");
            var list = FindById(run, "SearchResultsList")!;
            var more = FindById(run, "SearchMore");
            Assert.NotNull(more);
            Assert.Null(list.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SearchMore")));
            Assert.Null(FindById(run, "SearchStatus"));
            Assert.True(more!.Current.BoundingRectangle.Bottom <= list.Current.BoundingRectangle.Top,
                "More results must be beside the search input, not after or over the result list.");
            Assert.Null(FindInApp(run, new PropertyCondition(AutomationElement.NameProperty,
                "Enter: Open. Alt+Enter: Dashboard when available. Esc: Close.")));
            var scrolling = list.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern) ? list :
                list.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true))!;
            var scroll = (ScrollPattern)(pattern ?? scrolling.GetCurrentPattern(ScrollPattern.Pattern));
            scroll.SetScrollPercent(ScrollPattern.NoScroll, 100);
            await WaitUiAsync(run, () => IsFullyVisibleInScrollViewport(FindById(run, "SearchResult_4")),
                "last complete result without footer messages");
            var last = FindById(run, "SearchResult_4")!;
            Assert.Contains("Final line remains readable.", last.Current.Name);
            OwnedWindowCapture.Save(run, window, "search-no-footer.png", list);
            more!.SetFocus();
            Send(run, window, "{ENTER}");
            await WaitUiAsync(run, () => FindById(run, "SearchResult_5") is not null, "more results without closing search");
            await WaitUiAsync(run, () => FindById(run, "SearchResult_5") is { Current.HasKeyboardFocus: true },
                "keyboard More transfers focus to its first newly revealed result");
            try
            {
                await WaitUiAsync(run, () => IsFullyVisibleInScrollViewport(FindById(run, "SearchResult_5")),
                    "expanded last description");
            }
            catch (TimeoutException)
            {
                var result = FindById(run, "SearchResult_5")!;
                output.WriteLine($"Expanded row: bounds={result.Current.BoundingRectangle}, " +
                    $"offscreen={result.Current.IsOffscreen}, focus={result.Current.HasKeyboardFocus}");
                for (var parent = TreeWalker.ControlViewWalker.GetParent(result); parent is not null;
                    parent = TreeWalker.ControlViewWalker.GetParent(parent))
                {
                    if (!parent.TryGetCurrentPattern(ScrollPattern.Pattern, out var currentPattern)) continue;
                    var state = ((ScrollPattern)currentPattern).Current;
                    output.WriteLine($"Scroll viewport: bounds={parent.Current.BoundingRectangle}, " +
                        $"vertical={state.VerticalScrollPercent}, view={state.VerticalViewSize}");
                }
                throw;
            }
            Assert.Contains("Final line remains readable.", FindById(run, "SearchResult_5")!.Current.Name);
            Assert.True(FindById(run, "SearchMore") is null or { Current.IsOffscreen: true });
            OwnedWindowCapture.Save(run, window, "search-expanded-last-description.png", list);
            Assert.NotNull(FindById(run, "WorkspaceSearchBox"));
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "skills.update");
        }, scenario: GatewayScenario.CreateSearchNavigation());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task MainSearch_ExpandingMultipleGroupsKeepsEveryResultDistinctAndReachable()
    {
        await WithAppAsync(async run =>
        {
            var (window, _, _) = await PrepareChatWithDraftAsync(run);
            run.Gateway.OverrideSearchRead("skills.status", _ => new
            {
                skills = Enumerable.Range(1, 6).Select(number => new
                {
                    name = $"Fixture navigation field skill {number}", skillKey = $"expansion-{number}",
                    description = "Synthetic skill", enabled = true
                }).ToArray()
            });
            Send(run, window, "^k");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "popup");
            ((ValuePattern)SearchEdit(run, "WorkspaceSearchBox").GetCurrentPattern(ValuePattern.Pattern)).SetValue("Fixture navigation field");
            await WaitUiAsync(run, () => FindById(run, "SearchMore") is not null &&
                FindById(run, "WorkspaceSearchBox")?.Current.ItemStatus.Contains("searching", StringComparison.OrdinalIgnoreCase) != true,
                "both groups settled");
            ScrollSearch(run);
            FindById(run, "SearchMore")!.SetFocus();
            Send(run, window, "{ENTER}");
            await WaitUiAsync(run, () => FindById(run, "SearchResult_5") is { Current.HasKeyboardFocus: true } row &&
                row.Current.Name.StartsWith("Fixture navigation field 6.", StringComparison.Ordinal),
                "first new result in the earlier group");
            Assert.True(IsFullyVisibleInScrollViewport(FindById(run, "SearchResult_5")));
            ScrollSearch(run);
            await WaitUiAsync(run, () => FindById(run, "SearchResult_13") is { Current.IsOffscreen: false } row &&
                row.Current.Name.StartsWith("Fixture navigation field skill 6.", StringComparison.Ordinal),
                "later group has its new result and shifted index");
            Assert.True(IsFullyVisibleInScrollViewport(FindById(run, "SearchResult_13")));
            Assert.StartsWith("Fixture navigation field skill 1.", FindById(run, "SearchResult_8")!.Current.Name, StringComparison.Ordinal);
            Assert.True(FindById(run, "SearchMore") is null or { Current.IsOffscreen: true });
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method is "skills.update" or "chat.send");
        }, scenario: GatewayScenario.CreateSearchNavigation());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task MainSearch_KeyboardMoreAndRetryOperateTheFocusedButtonWithoutNavigation()
    {
        var scenario = GatewayScenario.CreateSearchNavigation();
        await WithAppAsync(async run =>
        {
            var (window, _, _) = await PrepareChatWithDraftAsync(run);
            run.Gateway.FailSearchRead("sessions.search");
            Send(run, window, "^k");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "popup");
            var edit = SearchEdit(run, "WorkspaceSearchBox");
            ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue("Fixture navigation field");
            await WaitUiAsync(run, () => FindById(run, "SearchMore") is not null &&
                FindById(run, "SearchRetry") is not null, "more and retry buttons");
            Assert.True(edit.Current.BoundingRectangle.Width >= 120,
                "The query input must remain usable while More and Retry are both visible.");
            ScrollSearch(run);
            var before = ResultCount();
            FindById(run, "SearchMore")!.SetFocus();
            Send(run, window, "{ENTER}");
            await WaitUiAsync(run, () => ResultCount() > before, "native Enter expands results");
            Assert.NotNull(FindById(run, "WorkspaceSearchBox"));
            var requests = run.Gateway.Requests.Count(request => request.Method == "sessions.search");
            run.Gateway.ClearSearchReadOverride("sessions.search");
            ScrollSearch(run);
            FindById(run, "SearchRetry")!.SetFocus();
            Send(run, window, "{ENTER}");
            await WaitUiAsync(run, () => run.Gateway.Requests.Count(request => request.Method == "sessions.search") > requests &&
                FindById(run, "SearchRetry") is null or { Current.IsOffscreen: true }, "native Enter retries the failed source");
            Assert.NotNull(FindById(run, "WorkspaceSearchBox"));
            Assert.Null(FindById(run, "ConfigPageMarker"));
            OwnedWindowCapture.Save(run, window, "search-keyboard-more-retry.png", FindById(run, "SearchResultsList")!);

            int ResultCount() => FindById(run, "SearchResultsList")!.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count;
        }, scenario: scenario);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task MainSearch_AbsentConfigSectionRevealsItsSchemaFieldWithoutWriting()
    {
        await WithAppAsync(async run =>
        {
            var (window, _, _) = await PrepareChatWithDraftAsync(run);
            Send(run, window, "^k");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "popup");
            var edit = SearchEdit(run, "WorkspaceSearchBox");
            ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue("Fixture navigation field 8");
            await WaitUiAsync(run, () => FindById(run, "SearchResult_0")?.Current.Name
                .StartsWith("Fixture navigation field 8.", StringComparison.Ordinal) == true, "schema-only result");
            edit.SetFocus();
            Send(run, window, "{ENTER}");
            await WaitUiAsync(run, () => IsFullyVisibleInScrollViewport(FindById(run, "ConfigField:field8")),
                "requested field in the absent optional section");
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method is "config.patch" or "config.set" or "config.apply");
            OwnedWindowCapture.Save(run, window, "search-absent-config-field.png", FindById(run, "ConfigField:field8")!);
        }, scenario: GatewayScenario.CreateSearchNavigation());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task MainSearch_FailedConfigRevealDoesNotFollowManualSectionNavigation()
    {
        await WithAppAsync(async run =>
        {
            var (window, _, _) = await PrepareChatWithDraftAsync(run);
            Send(run, window, "^k");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "popup");
            var edit = SearchEdit(run, "WorkspaceSearchBox");
            ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue("Unrenderable navigation field");
            await WaitUiAsync(run, () => FindById(run, "SearchResult_0")?.Current.Name
                .StartsWith("Unrenderable navigation field.", StringComparison.Ordinal) == true, "unrenderable schema result");
            edit.SetFocus();
            Send(run, window, "{ENTER}");
            await WaitUiAsync(run, () => FindInApp(run,
                new PropertyCondition(AutomationElement.NameProperty, "Unrenderable Navigation")) is not null,
                "initial section without a renderable field");
            var section = FindInApp(run, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem),
                new PropertyCondition(AutomationElement.NameProperty, "\U0001F4C1 Optional Navigation")))!;
            Assert.NotNull(section);
            section.SetFocus();
            Send(run, window, "{ENTER}");
            await WaitUiAsync(run, () => IsFullyVisibleInScrollViewport(FindById(run, "ConfigField:field1")) &&
                section.Current.HasKeyboardFocus, "manual navigation retains tree focus and section-top scroll");
            Assert.False(FindById(run, "ConfigField:field8")!.Current.HasKeyboardFocus);
            Assert.Equal("unchanged", ((ValuePattern)FindById(run, "ConfigField:field8")!
                .GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method is "config.patch" or "config.set");
        }, scenario: GatewayScenario.CreateSearchNavigation(includeUnrenderableSection: true));
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task MainSearch_MetadataRefreshPreservesResultFocusWithoutStealingInputFocus()
    {
        var scenario = GatewayScenario.CreateSearchNavigation();
        await WithAppAsync(async run =>
        {
            var (window, _, _) = await PrepareChatWithDraftAsync(run);
            Send(run, window, "^k");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "popup");
            var edit = SearchEdit(run, "WorkspaceSearchBox");
            ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue("Fixture");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox")?.Current.ItemStatus
                .Contains("searching", StringComparison.OrdinalIgnoreCase) != true &&
                FindById(run, "SearchMore") is not null, "settled metadata");
            ScrollSearch(run);
            var previousRows = FindById(run, "SearchResultsList")!.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                .Cast<AutomationElement>().Select(item => item.Current.Name).ToHashSet();
            Invoke(FindById(run, "SearchMore")!);
            await WaitUiAsync(run, () => FindInApp(run, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.HasKeyboardFocusProperty, true))) is { } revealed &&
                !previousRows.Contains(revealed.Current.Name), "More focus handoff before metadata navigation");
            ScrollSearch(run, 0);
            const string title = GatewayScenario.EmptyTitle;
            await WaitUiAsync(run, () => FindInApp(run, new PropertyCondition(
                AutomationElement.NameProperty, title + ". main. ")) is { Current.IsOffscreen: false },
                "metadata target visible after scrolling back");
            var row = FindInApp(run, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.NameProperty, title + ". main. ")))!;
            Assert.NotNull(row);
            ((SelectionItemPattern)row.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            row.SetFocus();
            const string revised = title + " revised";
            scenario.RenameSession(GatewayScenario.EmptySessionKey, revised, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await run.Gateway.NotifySessionsChangedAsync();
            await WaitUiAsync(run, () => FindInApp(run, new PropertyCondition(
                AutomationElement.NameProperty, revised + ". main. ")) is { Current.HasKeyboardFocus: true },
                "same selected conversation keeps focus after metadata update");
            Assert.True(IsFullyVisibleInScrollViewport(FindInApp(run,
                new PropertyCondition(AutomationElement.NameProperty, revised + ". main. "))));
            edit.SetFocus();
            var list = FindById(run, "SearchResultsList")!;
            var scrollElement = list.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern) ? list :
                list.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true))!;
            var scroll = (ScrollPattern)(pattern ?? scrollElement.GetCurrentPattern(ScrollPattern.Pattern));
            Assert.True(scroll.Current.VerticallyScrollable);
            scroll.SetScrollPercent(ScrollPattern.NoScroll, 100);
            await WaitUiAsync(run, () => scroll.Current.VerticalScrollPercent >= 99, "result list scrolled to the end");
            edit.SetFocus();
            await WaitUiAsync(run, () => edit.Current.HasKeyboardFocus, "input focus before the metadata refresh");
            const string updated = title + " updated";
            scenario.RenameSession(GatewayScenario.EmptySessionKey, updated);
            await run.Gateway.NotifySessionsChangedAsync();
            await WaitUiAsync(run, () => FindInApp(run, new PropertyCondition(
                AutomationElement.NameProperty, updated + ". main. ")) is not null, "second metadata update");
            Assert.True(edit.Current.HasKeyboardFocus);
            Assert.True(scroll.Current.VerticalScrollPercent >= 99, "Metadata refresh reset the user's result scroll.");
            row = FindInApp(run, new PropertyCondition(AutomationElement.NameProperty, updated + ". main. "))!;
            row.SetFocus();
            Send(run, window, "{ENTER}");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is null && SessionSelected(run, updated),
                "Enter still selects the exact conversation");
        }, scenario: scenario);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task MainSearch_DisappearingFocusedResultReturnsToInputAndSelectsTheNextMatch()
    {
        var scenario = GatewayScenario.CreateSearchNavigation();
        await WithAppAsync(async run =>
        {
            run.Gateway.OverrideSearchRead("sessions.list", _ => new { sessions = Array.Empty<object>() });
            run.Gateway.FailSearchRead("sessions.search");
            var (window, _, _) = await PrepareChatWithDraftAsync(run);
            Send(run, window, "^k");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "popup");
            var edit = SearchEdit(run, "WorkspaceSearchBox");
            ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue("Fixture:");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox")?.Current.ItemStatus
                .Contains("searching", StringComparison.OrdinalIgnoreCase) != true &&
                FindInApp(run, new PropertyCondition(AutomationElement.NameProperty,
                    GatewayScenario.EmptyTitle + ". main. ")) is { Current.IsOffscreen: false }, "settled conversations");
            var row = FindInApp(run, new PropertyCondition(AutomationElement.NameProperty,
                GatewayScenario.EmptyTitle + ". main. "))!;
            ((SelectionItemPattern)row.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            row.SetFocus();
            scenario.RenameSession(GatewayScenario.EmptySessionKey, "Renamed conversation");
            await run.Gateway.NotifySessionsChangedAsync();
            await WaitUiAsync(run, () => FindInApp(run, new PropertyCondition(AutomationElement.NameProperty,
                    GatewayScenario.EmptyTitle + ". main. ")) is null &&
                edit.Current.HasKeyboardFocus, "vanished result returns focus to the query");
            var selected = ((SelectionPattern)FindById(run, "SearchResultsList")!
                .GetCurrentPattern(SelectionPattern.Pattern)).Current.GetSelection();
            var next = Assert.Single(selected);
            Assert.StartsWith(GatewayScenario.MainTitle + ".", next.Current.Name, StringComparison.Ordinal);
            Send(run, window, "{ENTER}");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is null, "Enter closes search");
            await WaitComposerSelectionAsync(run, GatewayScenario.MainSessionKey);

            Send(run, window, "^k");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "popup for last match");
            edit = SearchEdit(run, "WorkspaceSearchBox");
            ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue("Renamed conversation");
            await WaitUiAsync(run, () => FindById(run, "SearchResult_0")?.Current.Name
                .StartsWith("Renamed conversation.", StringComparison.Ordinal) == true &&
                !edit.Current.ItemStatus.Contains("searching", StringComparison.OrdinalIgnoreCase), "single settled match");
            row = FindById(run, "SearchResult_0")!;
            row.SetFocus();
            scenario.RenameSession(GatewayScenario.EmptySessionKey, "No longer matches");
            await run.Gateway.NotifySessionsChangedAsync();
            await WaitUiAsync(run, () => FindById(run, "SearchResult_0") is null && edit.Current.HasKeyboardFocus,
                "last disappearing result returns focus to the query");
            Send(run, window, "{ENTER}");
            Send(run, window, OrderingProbe);
            await WaitUiAsync(run, () =>
            {
                var value = ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
                return value.Length == "Renamed conversation".Length + OrderingProbe.Length &&
                    value.Replace(OrderingProbe, "", StringComparison.Ordinal) == "Renamed conversation";
            }, "ordering probe reaches the query after inert Enter");
            Assert.NotNull(FindById(run, "WorkspaceSearchBox"));
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "chat.send");
        }, scenario: scenario);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task MainSearch_CrossSurfaceImmediateReopeningKeepsTheCurrentSearchUsable()
    {
        await WithAppAsync(async run =>
        {
            var (window, _, _) = await PrepareChatWithDraftAsync(run);
            Send(run, window, "^k");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "Workspace popup");
            var input = SearchEdit(run, "WorkspaceSearchBox");
            ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).SetValue("camera");
            await WaitUiAsync(run, () => FindById(run, "SearchResult_0")?.Current.Name.StartsWith("Camera.", StringComparison.Ordinal) == true,
                "setting target");
            input.SetFocus();
            Send(run, window, "{ENTER}");
            Send(run, window, "^k");
            await WaitUiAsync(run, () => FindById(run, "SettingsSearchBox") is not null, "immediately reopened Settings popup");
            input = SearchEdit(run, "SettingsSearchBox");
            ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).SetValue("Fixture: main conversation");
            await WaitUiAsync(run, () => FindById(run, "SearchResult_0")?.Current.Name.StartsWith(GatewayScenario.MainTitle + ".", StringComparison.Ordinal) == true,
                "chat target after the old Workspace popup closed");
            input.SetFocus();
            Send(run, window, "{ENTER}");
            Send(run, window, "^k");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "immediately reopened Workspace popup");
            input = SearchEdit(run, "WorkspaceSearchBox");
            ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).SetValue("lighthouse");
            await WaitUiAsync(run, () => FindById(run, "SearchResult_0")?.Current.Name.Contains(GatewayScenario.MainSentinel, StringComparison.Ordinal) == true,
                "remote search still works after the old Settings popup closed");
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "chat.send");
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task MainSearch_SkillsAreDashboardDestinationsRatherThanRetiredNativePages()
    {
        await WithAppAsync(async run =>
        {
            object SkillData(System.Text.Json.JsonElement parameters)
            {
                var selected = parameters.TryGetProperty("agentId", out var agent) && agent.GetString() == "main";
                return new { skills = new[] { new { name = selected ? "Navigation skill" : "Foreign agent skill",
                    skillKey = selected ? "fixture-navigation" : "foreign-skill", description = "Synthetic navigation skill", enabled = true } } };
            }
            run.Gateway.OverrideSearchRead("skills.status", SkillData);
            var (window, _, _) = await PrepareChatWithDraftAsync(run);
            Send(run, window, "^k");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "popup");
            var edit = SearchEdit(run, "WorkspaceSearchBox");
            ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue("Navigation skill");
            await WaitUiAsync(run, () => FindById(run, "SearchResult_0")?.Current.Name.StartsWith("Navigation skill.", StringComparison.Ordinal) == true,
                "selected-agent skill result");
            Assert.Contains("Dashboard", FindById(run, "SearchResult_0")!.Current.Name);
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "skills.update");
            Assert.Null(FindInApp(run, new PropertyCondition(AutomationElement.NameProperty, "Foreign agent skill")));
            Assert.Null(FindById(run, "SkillsPageMarker"));
            OwnedWindowCapture.Save(run, window, "search-skills-dashboard.png", FindById(run, "SearchResult_0")!);
        }, scenario: GatewayScenario.CreateSearchNavigation());
    }

    private static void SetForeground(AutomationElement window)
    {
        NativeKeyboardProof.ActivateAsync(new IntPtr(window.Current.NativeWindowHandle)).GetAwaiter().GetResult();
    }

    private static void ScrollSearch(GatewayFixtureRun run, double percent = 100)
    {
        var list = FindById(run, "SearchResultsList")!;
        var element = list.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern) ? list :
            list.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true))!;
        var scroll = (ScrollPattern)(pattern ?? element.GetCurrentPattern(ScrollPattern.Pattern));
        if (scroll.Current.VerticallyScrollable) scroll.SetScrollPercent(ScrollPattern.NoScroll, percent);
    }

    private static bool IsFullyVisibleInScrollViewport(AutomationElement? element)
    {
        if (element is null || element.Current.IsOffscreen) return false;
        var bounds = element.Current.BoundingRectangle;
        for (var parent = TreeWalker.ControlViewWalker.GetParent(element); parent is not null;
            parent = TreeWalker.ControlViewWalker.GetParent(parent))
        {
            if (!parent.TryGetCurrentPattern(ScrollPattern.Pattern, out _)) continue;
            var viewport = parent.Current.BoundingRectangle;
            return bounds.Width > 0 && bounds.Height > 0 && bounds.Top >= viewport.Top - 1 &&
                bounds.Bottom <= viewport.Bottom + 1 && bounds.Left >= viewport.Left - 1 &&
                bounds.Right <= viewport.Right + 1;
        }
        return false;
    }

    // Native input is global. Refuse to send unless the foreground window belongs to the fixture app
    // process (a flyout popup is a separate window of the same process) and no modifier key is held.
    private static void Send(GatewayFixtureRun run, AutomationElement window, string keys)
    {
        var deadline = DateTime.UtcNow + FixtureForegroundTimeout;
        while (!ForegroundBelongsTo(run.AppProcessId))
        {
            if (DateTime.UtcNow > deadline)
                throw new InvalidOperationException($"Fixture app PID {run.AppProcessId} is not foreground; refusing to send keys '{keys}'.");
            SetForegroundWindow(new IntPtr(window.Current.NativeWindowHandle));
            Thread.Sleep(100);
        }
        foreach (var modifier in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
        {
            if ((GetAsyncKeyState(modifier) & 0x8000) != 0)
                throw new InvalidOperationException("A modifier key is held; refusing to send keys.");
        }
        var foreground = GetForegroundWindow();
        switch (keys)
        {
            case "^k": NativeKeyboardProof.Press(foreground, Windows.System.VirtualKey.K, control: true); break;
            case "^e": NativeKeyboardProof.Press(foreground, Windows.System.VirtualKey.E, control: true); break;
            case "^f": NativeKeyboardProof.Press(foreground, Windows.System.VirtualKey.F, control: true); break;
            case "%{LEFT}": NativeKeyboardProof.Press(foreground, Windows.System.VirtualKey.Left, alt: true); break;
            case "%{RIGHT}": NativeKeyboardProof.Press(foreground, Windows.System.VirtualKey.Right, alt: true); break;
            case "{ESC}": NativeKeyboardProof.Press(foreground, Windows.System.VirtualKey.Escape); break;
            case "{ENTER}": NativeKeyboardProof.Press(foreground, Windows.System.VirtualKey.Enter); break;
            case "^{ENTER}": NativeKeyboardProof.Press(foreground, Windows.System.VirtualKey.Enter, control: true); break;
            case "%{ENTER}": NativeKeyboardProof.Press(foreground, Windows.System.VirtualKey.Enter, alt: true); break;
            case "{DOWN}": NativeKeyboardProof.Press(foreground, Windows.System.VirtualKey.Down); break;
            case "Settings{ENTER}":
                NativeKeyboardProof.TypeText(foreground, "Settings");
                NativeKeyboardProof.Press(foreground, Windows.System.VirtualKey.Enter);
                break;
            default: NativeKeyboardProof.TypeText(foreground, keys); break;
        }
    }

    private static bool ForegroundBelongsTo(int processId)
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var owner);
        return owner == (uint)processId;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private static void FocusComposer(GatewayFixtureRun run, AutomationElement window)
    {
        SetForeground(window);
        FindById(run, "ChatComposerInput")!.SetFocus();
    }

    private static string ComposerText(GatewayFixtureRun run) =>
        ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;

    private static async Task EscapeAndAssertDraftAsync(
        GatewayFixtureRun run, AutomationElement window, int[] composerIdentity, bool expectComposerFocus)
    {
        Send(run, window, "{ESC}");
        await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is null, "search flyout closed by Escape");
        if (expectComposerFocus)
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput")!.Current.HasKeyboardFocus, "composer focus restored by Escape");
        Assert.Equal(composerIdentity, FindById(run, "ChatComposerInput")!.GetRuntimeId());
        Assert.Equal(MainSearchDraft, ComposerText(run));
    }
}
