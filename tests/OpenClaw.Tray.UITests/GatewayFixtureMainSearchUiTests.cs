using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    private const string MainSearchDraft = "Keep this draft through main search";
    private static readonly TimeSpan MainSearchNegativeWindow = TimeSpan.FromMilliseconds(750);

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
            await EscapeAndAssertDraftAsync(run, identity);

            foreach (var chord in new[] { "^k", "^e" })
            {
                FocusComposer(run, window);
                System.Windows.Forms.SendKeys.SendWait(chord);
                await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, $"search flyout from {chord}");
                await EscapeAndAssertDraftAsync(run, identity);
            }

            FocusComposer(run, window);
            System.Windows.Forms.SendKeys.SendWait("^f");
            await Task.Delay(MainSearchNegativeWindow);
            Assert.Null(FindById(run, "WorkspaceSearchBox"));
            Assert.Equal(MainSearchDraft, ComposerText(run));

            // Delivery sentinel: the same focus state still opens search with Ctrl+K, so the earlier Ctrl+F was not lost to focus.
            System.Windows.Forms.SendKeys.SendWait("^k");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "search flyout from Ctrl+K after Ctrl+F");
            await EscapeAndAssertDraftAsync(run, identity);
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
            System.Windows.Forms.SendKeys.SendWait("^k");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "search with collapsed sidebar");
            OwnedWindowCapture.Save(run, window, "main-search-collapsed-sidebar.png", FindById(run, "WorkspaceSearchBox")!);
            await EscapeAndAssertDraftAsync(run, identity);

            System.Windows.Forms.SendKeys.SendWait("^k");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "search before routing");
            FindById(run, "WorkspaceSearchBox")!.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))!.SetFocus();
            System.Windows.Forms.SendKeys.SendWait("Settings{ENTER}");
            await WaitUiAsync(run, () => FindById(run, "SettingsPageMarker") is { Current.IsOffscreen: false },
                "Settings route from main search");
            Assert.Null(FindById(run, "WorkspaceSearchBox"));
            OwnedWindowCapture.Save(run, window, "main-search-routed-to-settings.png", FindById(run, "SettingsPageMarker")!);

            SetForeground(window);
            System.Windows.Forms.SendKeys.SendWait("%{LEFT}");
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is { Current.IsOffscreen: false },
                "Back to chat");
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
                System.Windows.Forms.SendKeys.SendWait(chord);
                await WaitUiAsync(run, () => FindById(run, "SettingsSearchBox") is not null, $"Settings search from {chord}");
                Assert.Null(FindById(run, "WorkspaceSearchBox"));
                if (chord == "^f")
                    OwnedWindowCapture.Save(run, window, "settings-ctrlf-search.png", FindById(run, "SettingsSearchBox")!);
                System.Windows.Forms.SendKeys.SendWait("{ESC}");
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
            System.Windows.Forms.SendKeys.SendWait("^k");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "search flyout");
            var edit = FindById(run, "WorkspaceSearchBox")!.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))!;
            edit.SetFocus();
            const string unmatched = "zzqqxx-no-such-command";
            System.Windows.Forms.SendKeys.SendWait(unmatched);
            await WaitUiAsync(run, () => ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).Current.Value == unmatched,
                "unmatched query typed");
            OwnedWindowCapture.Save(run, window, "main-search-unmatched-query.png", FindById(run, "WorkspaceSearchBox")!);
            System.Windows.Forms.SendKeys.SendWait("{ENTER}");

            // Enter on an unmatched query is a no-op; a following valid query in the same flyout proves Enter is processed.
            await Task.Delay(MainSearchNegativeWindow);
            Assert.NotNull(FindById(run, "WorkspaceSearchBox"));
            Assert.True(FindById(run, "SettingsPageMarker") is null or { Current.IsOffscreen: true });
            ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue("Settings");
            edit.SetFocus();
            System.Windows.Forms.SendKeys.SendWait("{ENTER}");
            await WaitUiAsync(run, () => FindById(run, "SettingsPageMarker") is { Current.IsOffscreen: false },
                "valid query routes after unmatched one");

            SetForeground(window);
            System.Windows.Forms.SendKeys.SendWait("%{LEFT}");
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

    private static void SetForeground(AutomationElement window)
    {
        if (!SetForegroundWindow(new IntPtr(window.Current.NativeWindowHandle)))
            throw new InvalidOperationException("Cannot activate the isolated Workspace for keyboard proof.");
    }

    private static void FocusComposer(GatewayFixtureRun run, AutomationElement window)
    {
        SetForeground(window);
        FindById(run, "ChatComposerInput")!.SetFocus();
    }

    private static string ComposerText(GatewayFixtureRun run) =>
        ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;

    private static async Task EscapeAndAssertDraftAsync(GatewayFixtureRun run, int[] composerIdentity)
    {
        System.Windows.Forms.SendKeys.SendWait("{ESC}");
        await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is null, "search flyout closed by Escape");
        Assert.Equal(composerIdentity, FindById(run, "ChatComposerInput")!.GetRuntimeId());
        Assert.Equal(MainSearchDraft, ComposerText(run));
    }
}
