using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    // Real pointer input proves the title-bar pass-through regions: UIA Invoke would bypass the
    // window's non-client hit testing and pass even when a click would drag the window instead.
    [GatewayFixtureUiTheory]
    [Trait("Category", "GatewayFixture")]
    [Trait("KeyboardProof", "Required")]
    [InlineData("Light", false)]
    [InlineData("Dark", true)]
    public async Task SidePanel_TitleBarControlsAndCaptionRowTabsTakeRealPointerClicks(string theme, bool maximized)
    {
        await WithAppAsync(async run =>
        {
            if (Environment.GetEnvironmentVariable("OPENCLAW_RUN_KEYBOARD_PROOF") != "1")
                throw new InvalidOperationException("Side panel pointer proof requires explicit native input opt-in.");
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            var window = FindHub(run);
            var windowPattern = (WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern);
            windowPattern.SetWindowVisualState(maximized ? WindowVisualState.Maximized : WindowVisualState.Normal);
            await WaitUiAsync(run, () => windowPattern.Current.WindowVisualState ==
                (maximized ? WindowVisualState.Maximized : WindowVisualState.Normal), "requested window state");
            // Capture each element inside its wait so a click never acts on a peer that was not yet published.
            async Task<AutomationElement> VisibleAsync(string id, string description)
            {
                AutomationElement? element = null;
                await WaitUiAsync(run, () => (element = FindById(run, id)) is { Current.IsOffscreen: false }, description);
                return element!;
            }
            async Task<AutomationElement> TabAsync(Func<System.Windows.Rect, bool> placed, string description)
            {
                AutomationElement? tab = null;
                await WaitUiAsync(run, () => (tab = SubagentsTab(run)) is { } found && placed(found.Current.BoundingRectangle),
                    description);
                return tab!;
            }

            var toggle = await VisibleAsync("WorkspaceToggleSidePanel", "side panel toggle in the title bar");
            // The toggle sits flush against the caption buttons and shares their row.
            var minimize = (await VisibleAsync("Minimize", "caption buttons")).Current.BoundingRectangle;
            var toggleBounds = toggle.Current.BoundingRectangle;
            Assert.InRange(minimize.Left - toggleBounds.Right, -1, 8 * (toggleBounds.Width / 40) + 1);
            Assert.InRange(toggleBounds.Top + toggleBounds.Height / 2, minimize.Top, minimize.Bottom);

            await ClickOwnedElementAsync(run, window, toggle);
            await ClickOwnedElementAsync(run, window, await VisibleAsync("ConversationPanelLauncherSubagents", "panel launcher"));
            await TabAsync(bounds => bounds.Top < minimize.Bottom, "Subagents tab joins the caption row");
            await CaptureIfRequestedAsync(run, $"side-panel-{theme}-{(maximized ? "max" : "normal")}-split.png");

            // An operation banner above chat must stay visible and dismissable: the panel leaves the caption row.
            Invoke(await VisibleAsync("WorkspaceSessionsAdd", "new conversation action"));
            var banner = await VisibleAsync("WorkspaceOperationFeedback", "rejected-operation banner above chat");
            var bannerBottom = banner.Current.BoundingRectangle.Bottom;
            await TabAsync(bounds => bounds.Top >= bannerBottom - 1, "panel leaves the caption row while the banner shows");
            AutomationElement? dismiss = null;
            await WaitUiAsync(run, () => (dismiss = banner.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))) is { Current.IsOffscreen: false },
                "banner close button");
            await ClickOwnedElementAsync(run, window, dismiss!);
            await WaitUiAsync(run, () => FindById(run, "WorkspaceOperationFeedback") is null or { Current.IsOffscreen: true },
                "banner dismissed by pointer");
            await TabAsync(bounds => bounds.Top < minimize.Bottom, "tab rejoins the caption row");

            // Expanded with the sidebar collapsed, the tab starts after the reopen-sidebar button.
            await ClickOwnedElementAsync(run, window, await VisibleAsync("WorkspaceTogglePane", "sidebar toggle"));
            var reopen = await VisibleAsync("WorkspaceReopenPane", "sidebar collapsed");
            await ClickOwnedElementAsync(run, window, await VisibleAsync("WorkspaceExpandSidePanel", "expand button"));
            await WaitUiAsync(run, () => FindById(run, "WorkspaceExpandSidePanel")?.Current.Name == "Restore split", "expanded");
            var reopenRight = reopen.Current.BoundingRectangle.Right;
            var tab = await TabAsync(bounds => bounds.Left >= reopenRight, "tab clears the reopen-sidebar button");
            await CaptureIfRequestedAsync(run, $"side-panel-{theme}-{(maximized ? "max" : "normal")}-expanded.png");

            // The tab's Close button lies in the caption row; a pass-through miss would drag the window instead.
            AutomationElement? close = null;
            await WaitUiAsync(run, () => (close = tab.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "CloseButton"))) is { Current.IsOffscreen: false },
                "tab close button");
            Assert.True(close!.Current.BoundingRectangle.Top < minimize.Bottom, "Close must be tested inside the caption row.");
            await ClickOwnedElementAsync(run, window, close);
            await WaitUiAsync(run, () => SubagentsTab(run) is null, "tab closed by pointer in the caption row");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceExpandSidePanel") is null or { Current.IsOffscreen: true },
                "closing the last tab ends the expansion");

            // With no tabs, the toggle reopens the launcher; a second click minimizes it.
            await ClickOwnedElementAsync(run, window, await VisibleAsync("WorkspaceToggleSidePanel", "side panel toggle"));
            await WaitUiAsync(run, () => FindById(run, "ConversationPanelLauncher") is not null, "launcher reopened");
            await ClickOwnedElementAsync(run, window, await VisibleAsync("WorkspaceToggleSidePanel", "side panel toggle"));
            await WaitUiAsync(run, () => FindById(run, "ConversationPanelLauncher") is null, "panel minimized");
            Assert.Equal(maximized ? WindowVisualState.Maximized : WindowVisualState.Normal, windowPattern.Current.WindowVisualState);
        }, appTheme: theme);
    }

    private static AutomationElement? SubagentsTab(GatewayFixtureRun run) =>
        FindById(run, "ConversationPanelTabs")?.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
            new PropertyCondition(AutomationElement.NameProperty, "Subagents")));
}
