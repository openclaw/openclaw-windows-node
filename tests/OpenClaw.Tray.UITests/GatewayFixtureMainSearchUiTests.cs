using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    private const string MainSearchDraft = "Keep this draft through main search";
    private const string OrderingProbe = "Z";
    private static readonly TimeSpan MainSearchForegroundTimeout = TimeSpan.FromSeconds(5);

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
            FindById(run, "WorkspaceSearchBox")!.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))!.SetFocus();
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
            var edit = FindById(run, "WorkspaceSearchBox")!.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))!;
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

    private static void SetForeground(AutomationElement window)
    {
        if (!SetForegroundWindow(new IntPtr(window.Current.NativeWindowHandle)))
            throw new InvalidOperationException("Cannot activate the isolated Workspace for keyboard proof.");
    }

    // SendKeys is global input. Refuse to send unless the foreground window belongs to the fixture app
    // process (a flyout popup is a separate window of the same process) and no modifier key is held.
    private static void Send(GatewayFixtureRun run, AutomationElement window, string keys)
    {
        var deadline = DateTime.UtcNow + MainSearchForegroundTimeout;
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
        System.Windows.Forms.SendKeys.SendWait(keys);
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
