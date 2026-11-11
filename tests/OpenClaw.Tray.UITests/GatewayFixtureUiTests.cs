using System.Text.Json;
using System.Windows.Automation;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.Shared;
using OpenClaw.TestSupport.Gateway;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests;

[CollectionDefinition("Gateway fixture UI", DisableParallelization = true)]
public sealed class GatewayFixtureUiCollection { }

public sealed class GatewayFixtureUiFactAttribute : FactAttribute
{
    public GatewayFixtureUiFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_RUN_GATEWAY_FIXTURE_UI") != "1")
            Skip = "Opt in with scripts\\test-gateway-fixture.ps1. This test launches a real isolated Windows app.";
    }
}

[Collection("Gateway fixture UI")]
public sealed partial class GatewayFixtureUiTests(ITestOutputHelper output)
{
    private const string HandoffSettleTimeoutVariable = "OPENCLAW_GATEWAY_FIXTURE_HANDOFF_TIMEOUT_SECONDS";
    // Sender process, app relaunch, verify RPC and receipt persistence; hosted runners exceeded 20 s.
    private static readonly TimeSpan HandoffSettleTimeout =
        int.TryParse(Environment.GetEnvironmentVariable(HandoffSettleTimeoutVariable), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(45);
    // The footer photo is a flat disc about 24 DIPs across, so a painted one covers hundreds of pixels at any display scale.
    private const int OwnerPhotoChannelTolerance = 8;
    private const int OwnerPhotoMinimumPixels = 150;

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task HomeSidebar_CustomizeDoesNotNavigate_AndHomeRestoresCanonicalConversation()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.LongTitle, GatewayScenario.LongSessionKey);
            var window = FindHub(run);
            var scale = FindById(run, "WorkspaceTogglePane")!.Current.BoundingRectangle.Width / 40;
            var customize = FindById(run, "WorkspacePagesCustomize")!;
            var customizeBounds = customize.Current.BoundingRectangle;
            var addBounds = FindById(run, "WorkspaceSessionsAdd")!.Current.BoundingRectangle;
            Assert.InRange((customizeBounds.Right - addBounds.Right) / scale, -1, 1);
            Assert.InRange(customizeBounds.Width / scale, 39, 41);
            Assert.InRange(customizeBounds.Height / scale, 35, 37);
            Assert.Equal("Customize pages", customize.Current.Name);
            const string draft = "Keep this other conversation draft";
            ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).SetValue(draft);
            await WaitUiAsync(run, () => ComposerText(run) == draft &&
                FindById(run, "ChatComposerPrimaryAction") is { Current.Name: "Send", Current.IsEnabled: true },
                "non-main draft acknowledged before native pointer input");
            Assert.Null(FindById(run, $"WorkspaceSession:{GatewayScenario.MainSessionKey}"));

            async Task TogglePinAsync(string tag, bool keyboard)
            {
                var gear = FindById(run, "WorkspacePagesCustomize")!;
                if (keyboard)
                {
                    if (!SetForegroundWindow(new IntPtr(window.Current.NativeWindowHandle)) &&
                        !ForegroundBelongsTo(run.AppProcessId))
                        throw new InvalidOperationException("Cannot activate the isolated Workspace for customize keyboard proof.");
                    gear.SetFocus();
                    await WaitUiAsync(run, () => gear.Current.HasKeyboardFocus, "customize keyboard focus");
                    Send(run, window, " ");
                }
                else ClickOwnedElement(run, window, gear);
                await WaitUiAsync(run, () => FindById(run, $"WorkspacePagesToggle:{tag}") is not null, "customize flyout");
                foreach (var excluded in new[] { "settings", "local-ai", "voice", "permissions", "sandbox" })
                    Assert.Null(FindById(run, $"WorkspacePagesToggle:{excluded}"));
                Assert.Null(FindText(run, "This computer"));
                Invoke(FindById(run, $"WorkspacePagesToggle:{tag}")!);
                await WaitUiAsync(run, () => FindById(run, $"WorkspacePagesToggle:{tag}") is null, "customize flyout closed");
            }

            await TogglePinAsync("connection", keyboard: false);
            await WaitComposerSelectionAsync(run, GatewayScenario.LongSessionKey);
            Assert.Equal(draft, ComposerText(run));
            Invoke(FindById(run, "WorkspacePinnedPage:connection")!);
            await WaitUiAsync(run, () => FindById(run, "ConnectionPageMarker")?.Current.IsOffscreen == false, "embedded Connection");
            var connectionRow = FindById(run, "WorkspacePinnedPage:connection")!.GetRuntimeId();
            await TogglePinAsync("sessions", keyboard: false);
            Assert.False(FindById(run, "ConnectionPageMarker")!.Current.IsOffscreen);
            Assert.Null(FindById(run, "ChatComposerInput"));
            Assert.Equal(connectionRow, FindById(run, "WorkspacePinnedPage:connection")!.GetRuntimeId());
            await TogglePinAsync("sessions", keyboard: false);
            Assert.False(FindById(run, "ConnectionPageMarker")!.Current.IsOffscreen);
            Assert.Null(FindById(run, "WorkspacePinnedPage:sessions"));

            var home = FindById(run, "WorkspaceNavHome")!;
            var homeBounds = home.Current.BoundingRectangle;
            var pinBounds = FindById(run, "WorkspacePinnedPage:connection")!.Current.BoundingRectangle;
            var sessionsHeader = FindText(run, "Sessions")!.Current.BoundingRectangle;
            var other = FindById(run, $"WorkspaceSession:{GatewayScenario.LongSessionKey}")!.Current.BoundingRectangle;
            Assert.InRange((pinBounds.Top - homeBounds.Bottom) / scale, -1, 8);
            Assert.InRange((sessionsHeader.Top - pinBounds.Bottom) / scale, 4, 48);
            Assert.True(other.Top >= sessionsHeader.Bottom);
            Assert.Null(FindText(run, "Pages"));
            Assert.Null(FindText(run, "Pin pages here with the customize button."));
            Invoke(home);
            await WaitComposerSelectionAsync(run, GatewayScenario.MainSessionKey);
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.MainSentinel), "Home main history rendered");
            Assert.Null(FindById(run, $"WorkspaceSession:{GatewayScenario.MainSessionKey}"));
            Assert.True(((SelectionItemPattern)home.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected);
            Invoke(FindById(run, "WorkspaceBack")!);
            await WaitUiAsync(run, () => FindById(run, "ConnectionPageMarker")?.Current.IsOffscreen == false, "Back restores page");
            Invoke(FindById(run, "WorkspaceBack")!);
            await WaitComposerSelectionAsync(run, GatewayScenario.LongSessionKey);
            Assert.Equal(draft, ComposerText(run));
            await SelectSessionAsync(run, GatewayScenario.MainTitle, GatewayScenario.MainSessionKey);
            await SelectSessionAsync(run, GatewayScenario.LongTitle, GatewayScenario.LongSessionKey);
            Assert.Equal(draft, ComposerText(run));
            output.WriteLine("Native invocation, pin identity/order, canonical Home history and other-conversation draft assertions passed.");
            await TogglePinAsync("sessions", keyboard: true);
            await WaitComposerSelectionAsync(run, GatewayScenario.LongSessionKey);
            Assert.Equal(draft, ComposerText(run));
            await SelectSessionAsync(run, GatewayScenario.MainTitle, GatewayScenario.MainSessionKey);
            OwnedWindowCapture.Save(run, window, "simplified-home-sidebar.png", home, customize,
                FindById(run, "WorkspaceSessionsAdd")!);
        });
    }

    [Fact]
    public void SessionsScrollPolicy_RejectsOffscreenTargetWithoutAMechanism()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            RequireScrollMechanism(targetVisible: false, hasScrollItemPattern: false,
                hasScrollAncestor: false, "negative-control"));

        Assert.Contains("no UIA scrolling mechanism", exception.Message);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    public void SessionsScrollPolicy_AllowsVisibleOrActuallyScrollableTargets(
        bool visible, bool hasScrollItem, bool hasScrollAncestor) =>
        RequireScrollMechanism(visible, hasScrollItem, hasScrollAncestor, "positive-control");

    [Fact]
    public void SessionsScrollPolicy_RejectsNoMovementWhileTargetRemainsOffscreen()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            RequireScrollProgress(targetVisible: false, before: 42, after: 42, "negative-progress"));

        Assert.Contains("did not move", exception.Message);
    }

    [Theory]
    [InlineData(true, 42, 42)]
    [InlineData(false, 42, 67)]
    public void SessionsScrollPolicy_AllowsVisibilityOrMeasuredMovement(bool visible, double before, double after) =>
        RequireScrollProgress(visible, before, after, "positive-progress");

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task SessionsEyebrowNativeProof()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "sessions" });
            await WaitUiAsync(run, () => FindById(run, "SessionsPageMarker") is not null
                && FindById(run, "SessionsHubList") is not null, "Sessions eyebrow loaded");

            var marker = FindById(run, "SessionsPageMarker")!;
            Assert.Equal("Sessions", marker.Current.Name);
            CaptureSessionsWindow(run, "sessions-gradient-top-light.png");

            var pageScroll = FindScrollAncestor(marker)
                ?? throw new InvalidOperationException("Sessions page scroll ancestor is not discoverable.");
            var scroll = (ScrollPattern)pageScroll.GetCurrentPattern(ScrollPattern.Pattern);
            Assert.True(scroll.Current.VerticallyScrollable);
            scroll.SetScrollPercent(ScrollPattern.NoScroll, 55);
            await Task.Delay(150);
            Assert.True(((ScrollPattern)pageScroll.GetCurrentPattern(ScrollPattern.Pattern))
                .Current.VerticalScrollPercent > 0);
            CaptureSessionsWindow(run, "sessions-gradient-scrolled-light.png");

            ((SelectionItemPattern)FindById(run, "SessionsHubTabWorktrees")!
                .GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            await WaitUiAsync(run, () => FindById(run, "SessionsHubWorktree:wt-auth") is not null
                && FindById(run, "SessionsHubWorktree:wt-board") is not null, "Worktree card list loaded");
            CaptureSessionsWindow(run, "sessions-worktree-card-gaps-light.png");

        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task PinnedPages_DisconnectRetainsDisabledRowsAndFocus_AndReconnectEnablesSameRows()
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
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitComposerSelectionAsync(run, GatewayScenario.MainSessionKey);
            foreach (var tag in new[] { "sessions", "instances", "connection" })
            {
                await WaitUiAsync(run, () => FindById(run, "WorkspacePagesCustomize") is not null, "customization button");
                Invoke(FindById(run, "WorkspacePagesCustomize")!);
                await WaitUiAsync(run, () => FindById(run, $"WorkspacePagesToggle:{tag}") is not null, "pin toggle");
                Invoke(FindById(run, $"WorkspacePagesToggle:{tag}")!);
                await WaitUiAsync(run, () => FindById(run, $"WorkspacePinnedPage:{tag}") is not null, "pinned row");
            }
            var identities = new[] { "sessions", "instances", "connection" }.ToDictionary(
                tag => tag, tag => FindById(run, $"WorkspacePinnedPage:{tag}")!.GetRuntimeId());
            var composerIdentity = FindById(run, "ChatComposerInput")!.GetRuntimeId();
            var row = FindById(run, "WorkspacePinnedPage:sessions")!;
            row.SetFocus();
            await WaitUiAsync(run, () => row.Current.HasKeyboardFocus, "Gateway pin focused");
            run.Gateway.HoldConnectionHandshakes();
            releaseHandshake = run.Gateway.ReleaseConnectionHandshakes;
            await run.Gateway.CloseConnectionsAsync();
            await WaitUiAsync(run, () => FindById(run, "WorkspacePinnedPage:sessions") is { Current.IsEnabled: false },
                "saved Sessions pin remains visible but disabled");
            foreach (var (tag, identity) in identities)
            {
                var retained = FindById(run, $"WorkspacePinnedPage:{tag}")!;
                Assert.False(retained.Current.IsOffscreen);
                Assert.Equal(identity, retained.GetRuntimeId());
                Assert.Equal(tag == "connection", retained.Current.IsEnabled);
            }
            Assert.True(FindById(run, "WorkspacePagesCustomize")!.Current.HasKeyboardFocus);
            // Stay on Chat so the existing unavailable-current-Settings-page prune policy
            // cannot be mistaken for navigation caused by disabling or focusing a pin.
            Assert.Equal(composerIdentity, FindById(run, "ChatComposerInput")!.GetRuntimeId());
            Assert.Null(FindById(run, "ConnectionPageMarker"));
            try { Invoke(row); }
            catch (ElementNotEnabledException) { }
            await run.InvokeAsync("app.status", new { });
            Assert.Equal(composerIdentity, FindById(run, "ChatComposerInput")!.GetRuntimeId());
            Assert.Null(FindById(run, "SessionsPageMarker"));
            Assert.True(((SelectionItemPattern)FindById(run, "WorkspaceNavHome")!
                .GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected);
            await CaptureIfRequestedAsync(run, "pins-offline-retained-disabled.png");

            Invoke(FindById(run, "WorkspacePagesCustomize")!);
            await WaitUiAsync(run, () => FindById(run, "WorkspacePagesToggle:channels") is not null, "offline picker");
            foreach (var tag in identities.Keys)
            {
                var toggle = FindById(run, $"WorkspacePagesToggle:{tag}")!;
                Assert.True(toggle.Current.IsEnabled);
                Assert.Equal(ToggleState.On, ((TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState);
            }
            Invoke(FindById(run, "WorkspacePagesToggle:channels")!);
            await WaitUiAsync(run, () => FindById(run, "WorkspacePinnedPage:channels") is { Current.IsEnabled: false },
                "offline picker can add an unavailable page");
            Assert.Equal(composerIdentity, FindById(run, "ChatComposerInput")!.GetRuntimeId());
            ReleaseHandshake();
            await WaitUiAsync(run, () => FindById(run, "WorkspacePinnedPage:sessions") is { Current.IsEnabled: true },
                "retained pin enabled after reconnect");
            foreach (var (tag, identity) in identities)
            {
                var retained = FindById(run, $"WorkspacePinnedPage:{tag}")!;
                Assert.Equal(identity, retained.GetRuntimeId());
                Assert.True(retained.Current.IsEnabled);
            }
            Assert.True(FindById(run, "WorkspacePinnedPage:channels")!.Current.IsEnabled);
            Invoke(FindById(run, "WorkspacePinnedPage:sessions")!);
            await WaitUiAsync(run, () => FindById(run, "SessionsPageMarker")?.Current.IsOffscreen == false, "restored pin remains usable");
            await CaptureIfRequestedAsync(run, "pins-reconnected-enabled.png");
        }, releasePendingResponses: ReleaseHandshake);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task PinnedPages_HistoryFocusAndFeedbackRemainOnVisibleSurface()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput")?.Current.IsOffscreen == false, "initial composer");
            var window = FindHub(run);
            if (!SetForegroundWindow(new IntPtr(window.Current.NativeWindowHandle)))
                throw new InvalidOperationException("Cannot activate the isolated Workspace for pin keyboard proof.");

            foreach (var tag in new[] { "connection", "sessions" })
            {
                await WaitUiAsync(run, () => FindById(run, "WorkspacePagesCustomize")?.Current.IsOffscreen == false,
                    "customize button ready");
                Invoke(FindById(run, "WorkspacePagesCustomize")!);
                var toggleId = $"WorkspacePagesToggle:{tag}";
                await WaitUiAsync(run, () => FindById(run, toggleId) is not null, "pin toggle");
                Invoke(FindById(run, toggleId)!);
                await WaitUiAsync(run, () => FindById(run, $"WorkspacePinnedPage:{tag}") is not null, "pinned row");
            }

            Invoke(FindById(run, "WorkspacePinnedPage:connection")!);
            await WaitUiAsync(run, () => FindById(run, "ConnectionPageMarker")?.Current.IsOffscreen == false, "embedded Connection");
            Invoke(FindById(run, "WorkspaceBack")!);
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput")?.Current.IsOffscreen == false, "returned composer");
            FindById(run, "ChatComposerInput")!.SetFocus();
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput")!.Current.HasKeyboardFocus, "composer focus");
            Send(run, window, "%{RIGHT}");
            await WaitUiAsync(run, () => FindById(run, "ConnectionPageMarker")?.Current.IsOffscreen == false, "keyboard Forward");
            FindById(run, "WorkspacePinnedPage:sessions")!.SetFocus();
            Send(run, window, "%{LEFT}");
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput")?.Current.HasKeyboardFocus == true,
                "history restores composer despite a merely focused sidebar row");
            await CaptureIfRequestedAsync(run, "pin-history-composer-focus.png");

            Send(run, window, "%{RIGHT}");
            await WaitUiAsync(run, () => FindById(run, "ConnectionPageMarker")?.Current.IsOffscreen == false, "embedded Connection again");
            var sessions = FindById(run, "WorkspacePinnedPage:sessions")!;
            sessions.SetFocus();
            Send(run, window, "{ENTER}");
            await WaitUiAsync(run, () => FindById(run, "SessionsPageMarker")?.Current.IsOffscreen == false, "keyboard pin invocation");
            Assert.True(((SelectionItemPattern)sessions.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected);

            Send(run, window, "^k");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "Workspace search over embedded page");
            Assert.Null(FindById(run, "SettingsSearchBox"));
            OwnedWindowCapture.Save(run, window, "pin-embedded-search.png", FindById(run, "WorkspaceSearchBox")!);
            Send(run, window, "{ESC}");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is null, "embedded search dismissed");
            Invoke(FindById(run, "WorkspaceTogglePane")!);
            await WaitUiAsync(run, () => FindById(run, "WorkspaceReopenPane")?.Current.IsOffscreen == false, "collapsed embedded sidebar");
            Send(run, window, "^k");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is not null, "embedded search on collapsed sidebar anchor");
            Assert.Null(FindById(run, "SettingsSearchBox"));
            OwnedWindowCapture.Save(run, window, "pin-embedded-search-collapsed.png", FindById(run, "WorkspaceSearchBox")!);
            Send(run, window, "{ESC}");
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSearchBox") is null, "collapsed search dismissed");
            Invoke(FindById(run, "WorkspaceReopenPane")!);
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSessionsAdd")?.Current.IsOffscreen == false, "sidebar restored");

            Invoke(FindById(run, "WorkspaceSessionsAdd")!);
            await WaitUiAsync(run, () => FindById(run, "WorkspaceEmbeddedOperationFeedback")?.Current.IsOffscreen == false,
                "visible rejected-operation feedback above embedded content");
            Assert.Contains("Fixture Gateway is read-only", FindText(run, "Fixture Gateway is read-only")!.Current.Name);
            await CaptureIfRequestedAsync(run, "pin-operation-error-visible.png");

            await run.InvokeAsync("app.navigate", new { page = "settings" });
            await WaitUiAsync(run, () => FindById(run, "SettingsBack")?.Current.IsOffscreen == false, "full Settings presentation");
            Assert.True(FindById(run, "WorkspaceEmbeddedOperationFeedback") is null or { Current.IsOffscreen: true });
            Invoke(FindById(run, "SettingsBack")!);
            await WaitUiAsync(run, () => FindById(run, "WorkspaceEmbeddedOperationFeedback")?.Current.IsOffscreen == false,
                "undismissed feedback survives presentation change");
            var feedback = FindById(run, "WorkspaceEmbeddedOperationFeedback")!;
            var close = feedback.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            Assert.NotNull(close);
            Invoke(close);
            await WaitUiAsync(run, () => FindById(run, "WorkspaceEmbeddedOperationFeedback") is null or { Current.IsOffscreen: true },
                "user dismisses embedded feedback");
            FindById(run, "WorkspaceNavHome")!.SetFocus();
            Send(run, window, "{ENTER}");
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput")?.Current.IsOffscreen == false, "keyboard Home navigation");
            Assert.True(FindById(run, "WorkspaceNavHome")!.Current.HasKeyboardFocus);
            Assert.True(FindById(run, "WorkspaceOperationFeedback") is null or { Current.IsOffscreen: true });
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    [Trait("KeyboardProof", "Required")]
    public async Task PinnedSessions_TogglesArePointHittableAndTabOrdered_WithOperationFeedback()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "WorkspacePagesCustomize")?.Current.IsOffscreen == false,
                "pin customization");
            Invoke(FindById(run, "WorkspacePagesCustomize")!);
            await WaitUiAsync(run, () => FindById(run, "WorkspacePagesToggle:sessions") is not null, "Sessions pin toggle");
            Invoke(FindById(run, "WorkspacePagesToggle:sessions")!);
            await WaitUiAsync(run, () => FindById(run, "WorkspacePinnedPage:sessions") is not null, "Sessions pin");
            Invoke(FindById(run, "WorkspacePinnedPage:sessions")!);
            await WaitUiAsync(run, () => FindById(run, "SessionsPageMarker")?.Current.IsOffscreen == false, "pinned Sessions");
            var window = FindHub(run);
            var handle = new IntPtr(window.Current.NativeWindowHandle);
            await NativeKeyboardProof.ActivateAsync(handle);
            var windowPattern = (WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern);
            windowPattern.SetWindowVisualState(WindowVisualState.Normal);
            await WaitUiAsync(run, () => windowPattern.Current.WindowVisualState == WindowVisualState.Normal,
                "normal Workspace before resize");
            var scale = GetDpiForWindow(handle) / 96d;
            var workArea = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            Assert.True(workArea.Width >= 1000 * scale && workArea.Height >= 720 * scale,
                "The native proof work area must fit the 1000 by 720 DIP Workspace.");
            var transform = (TransformPattern)window.GetCurrentPattern(TransformPattern.Pattern);
            Assert.True(transform.Current.CanResize);
            Assert.True(transform.Current.CanMove);
            transform.Move(workArea.Left, workArea.Top);
            transform.Resize(1000 * scale, 720 * scale);
            await WaitUiAsync(run, () =>
            {
                var bounds = window.Current.BoundingRectangle;
                return Math.Abs(bounds.Width / scale - 1000) <= 2
                    && bounds.Left >= workArea.Left && bounds.Top >= workArea.Top
                    && bounds.Right <= workArea.Right && bounds.Bottom <= workArea.Bottom;
            }, "1000 DIP Workspace within work area");
            var hitSequence = 0;

            foreach (var theme in new[] { "Light", "Dark" })
            {
                await run.InvokeAsync("app.navigate", new { page = "settings" });
                await WaitUiAsync(run, () => FindById(run, "SettingsPageAppTheme")?.Current.IsOffscreen == false, "theme selector");
                var selector = FindById(run, "SettingsPageAppTheme")!;
                var dropdown = (ExpandCollapsePattern)selector.GetCurrentPattern(ExpandCollapsePattern.Pattern);
                dropdown.Expand();
                await WaitUiAsync(run, () => FindInApp(run, new AndCondition(
                    new PropertyCondition(AutomationElement.NameProperty, theme),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))) is not null, theme);
                var option = FindInApp(run, new AndCondition(
                    new PropertyCondition(AutomationElement.NameProperty, theme),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)))!;
                ((SelectionItemPattern)option.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                dropdown.Collapse();
                await WaitUiAsync(run, () => FindById(run, "SettingsBack") is { Current.IsEnabled: true, Current.IsOffscreen: false },
                    "return from theme settings");
                Invoke(FindById(run, "SettingsBack")!);
                await WaitUiAsync(run, () => FindById(run, "SessionsPageMarker")?.Current.IsOffscreen == false, "pinned Sessions restored");
                var pageIdentity = FindById(run, "SessionsHubHost")!.GetRuntimeId();

                var collapse = await WaitForElementByIdAsync(run, "WorkspaceTogglePane", "collapse button");
                AssertCenterHit(collapse);
                Invoke(collapse);
                await WaitUiAsync(run, () => FindById(run, "WorkspaceReopenPane")?.Current.IsOffscreen == false, "collapsed Chat");
                await AssertPinnedAllocationAsync(56);
                var reopen = FindById(run, "WorkspaceReopenPane")!;
                AssertCenterHit(reopen);
                var history = new[] { FindById(run, "SettingsBack")!, FindById(run, "SettingsForward")! };
                var enabledHistory = history.Where(button => button.Current.IsEnabled && !button.Current.IsOffscreen).ToArray();
                Assert.NotEmpty(enabledHistory);
                foreach (var button in history)
                {
                    Assert.False(button.Current.IsOffscreen);
                    Assert.True(reopen.Current.BoundingRectangle.Right <= button.Current.BoundingRectangle.Left,
                        "Reopen must not intersect the Settings history buttons.");
                }
                await NativeKeyboardProof.ActivateAsync(handle);
                reopen.SetFocus();
                await WaitUiAsync(run, () => reopen.Current.HasKeyboardFocus, "reopen focus");
                foreach (var button in enabledHistory)
                {
                    NativeKeyboardProof.Press(handle, Windows.System.VirtualKey.Tab);
                    await WaitUiAsync(run, () => button.Current.HasKeyboardFocus, "Tab reaches enabled history in order");
                    output.WriteLine($"Tab: {button.Current.AutomationId} [{string.Join(",", button.GetRuntimeId())}]");
                }
                NativeKeyboardProof.Press(handle, Windows.System.VirtualKey.Tab);
                await WaitUiAsync(run, () => HasRawAncestor(AutomationElement.FocusedElement,
                    FindById(run, "SessionsHubHost")!), "Tab enters Sessions page");
                output.WriteLine($"Tab page: {AutomationElement.FocusedElement.Current.AutomationId} [{string.Join(",", AutomationElement.FocusedElement.GetRuntimeId())}]");
                Assert.Equal(pageIdentity, FindById(run, "SessionsHubHost")!.GetRuntimeId());
                AssertCenterHit(reopen);
                Invoke(reopen);
                await WaitUiAsync(run, () => FindById(run, "WorkspaceSessionsAdd")?.Current.IsOffscreen == false, "Chat reopened");
                await AssertPinnedAllocationAsync(0);

                Invoke(FindById(run, "WorkspaceSessionsAdd")!);
                await WaitUiAsync(run, () => FindById(run, "WorkspaceEmbeddedOperationFeedback")?.Current.IsOffscreen == false,
                    "read-only rejected operation above pinned Sessions");
                var feedback = FindById(run, "WorkspaceEmbeddedOperationFeedback")!;
                var message = FindText(run, "Fixture Gateway is read-only");
                Assert.NotNull(message);
                Assert.True(HasRawAncestor(message, feedback));
                collapse = FindById(run, "WorkspaceTogglePane")!;
                AssertCenterHit(collapse);
                Invoke(collapse);
                await WaitUiAsync(run, () => FindById(run, "WorkspaceReopenPane")?.Current.IsOffscreen == false, "collapsed with feedback");
                await AssertPinnedAllocationAsync(56);
                reopen = FindById(run, "WorkspaceReopenPane")!;
                AssertCenterHit(reopen);
                await WaitUiAsync(run, () => !feedback.Current.IsOffscreen
                    && feedback.Current.BoundingRectangle.Height > 0
                    && feedback.Current.BoundingRectangle.Top >= reopen.Current.BoundingRectangle.Bottom
                    && history.All(button => feedback.Current.BoundingRectangle.Top >= button.Current.BoundingRectangle.Bottom),
                    "feedback below collapsed chrome");
                OwnedWindowCapture.Save(run, window, $"pinned-sessions-{theme.ToLowerInvariant()}-collapsed-feedback.png",
                    reopen, feedback, FindById(run, "SessionsHubHost")!);
                Invoke(reopen);
                await WaitUiAsync(run, () => FindById(run, "WorkspaceTogglePane")?.Current.IsOffscreen == false
                    && !feedback.Current.IsOffscreen
                    && feedback.Current.BoundingRectangle.Height > 0
                    && !feedback.Current.BoundingRectangle.IntersectsWith(
                        FindById(run, "WorkspaceTogglePane")!.Current.BoundingRectangle),
                    "reopened with retained unobscured feedback");
                await AssertPinnedAllocationAsync(0);
                AssertCenterHit(FindById(run, "WorkspaceTogglePane")!);
                Assert.Equal(pageIdentity, FindById(run, "SessionsHubHost")!.GetRuntimeId());
                OwnedWindowCapture.Save(run, window, $"pinned-sessions-{theme.ToLowerInvariant()}-reopened-feedback.png",
                    FindById(run, "WorkspaceTogglePane")!, feedback, FindById(run, "SessionsHubHost")!);
                var closeButtons = feedback.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                var closeButton = Assert.IsType<AutomationElement>(Assert.Single(closeButtons));
                Invoke(closeButton);
                await WaitUiAsync(run, () => FindById(run, "WorkspaceEmbeddedOperationFeedback") is null or { Current.IsOffscreen: true },
                    "dismiss feedback before next theme");
            }

            async Task AssertPinnedAllocationAsync(double expectedInset)
            {
                var settled = 0;
                await WaitUiAsync(run, () =>
                {
                    var navigation = FindById(run, "WorkspaceNavigation")!.Current.BoundingRectangle;
                    var settings = FindById(run, "WorkspaceSettingsHost")!.Current.BoundingRectangle;
                    var host = FindById(run, "SessionsHubHost")!.Current.BoundingRectangle;
                    var viewport = FindById(run, "SessionsHubPageScroll")!.Current.BoundingRectangle;
                    var matches = host.Height > 0 && viewport.Height > 0
                        && Math.Abs((settings.Top - navigation.Top) / scale) <= 1
                        && Math.Abs((host.Top - settings.Top) / scale) <= 1
                        && Math.Abs((viewport.Top - host.Top) / scale - expectedInset) <= 1;
                    settled = matches ? settled + 1 : 0;
                    return settled >= 2;
                }, $"settled pinned host outer 0 and viewport inset {expectedInset}");
                output.WriteLine($"Pinned allocation: host outer 0, page viewport {expectedInset} DIP.");
            }

            void AssertCenterHit(AutomationElement button)
            {
                Assert.Equal(run.AppProcessId, window.Current.ProcessId);
                Assert.Equal(run.AppProcessId, button.Current.ProcessId);
                Assert.True(button.Current.IsEnabled);
                Assert.False(button.Current.IsOffscreen);
                var bounds = button.Current.BoundingRectangle;
                Assert.True(bounds.Width > 0 && bounds.Height > 0);
                var point = new System.Windows.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                var receiptPath = Path.Combine(run.ArtifactsDirectory, $"pinned-sessions-center-hit-{++hitSequence:00}.json");
                var ancestors = new List<object>();
                var targetIdentity = button.GetRuntimeId();
                var belongsToTarget = false;
                WriteReceipt("before-query");
                var hit = AutomationElement.FromPoint(point);
                for (var current = hit; current is not null; current = TreeWalker.RawViewWalker.GetParent(current))
                {
                    ancestors.Add(Describe(current));
                    belongsToTarget |= current.GetRuntimeId().SequenceEqual(targetIdentity);
                    WriteReceipt("reading-raw-ancestry");
                }
                WriteReceipt(belongsToTarget ? "owned" : "wrong-owner");
                Assert.True(belongsToTarget,
                    $"Center hit does not belong to {button.Current.AutomationId} [{string.Join(",", button.GetRuntimeId())}].");
                output.WriteLine($"Center hit: {button.Current.AutomationId} [{string.Join(",", button.GetRuntimeId())}], hit [{string.Join(",", hit.GetRuntimeId())}]");

                void WriteReceipt(string outcome) => File.WriteAllText(receiptPath, JsonSerializer.Serialize(new
                {
                    outcome,
                    fixturePid = run.AppProcessId,
                    point = new { point.X, point.Y },
                    window = Describe(window),
                    target = Describe(button),
                    rawViewAncestry = ancestors
                }, new JsonSerializerOptions { WriteIndented = true }));

                static object Describe(AutomationElement element)
                {
                    var current = element.Current;
                    var rectangle = current.BoundingRectangle;
                    return new
                    {
                        runtimeId = element.GetRuntimeId(),
                        current.AutomationId,
                        current.ClassName,
                        controlType = current.ControlType.ProgrammaticName,
                        current.ProcessId,
                        hwnd = current.NativeWindowHandle,
                        bounds = new { rectangle.Left, rectangle.Top, rectangle.Width, rectangle.Height }
                    };
                }
            }

            static bool HasRawAncestor(AutomationElement? element, AutomationElement ancestor)
            {
                var expected = ancestor.GetRuntimeId();
                for (var current = element; current is not null; current = TreeWalker.RawViewWalker.GetParent(current))
                    if (current.GetRuntimeId().SequenceEqual(expected)) return true;
                return false;
            }
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task PinnedPages_BackgroundFollowsThemeWhenReturningFromSettings()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "WorkspacePagesCustomize") is not null, "pin customization");
            Invoke(FindById(run, "WorkspacePagesCustomize")!);
            await WaitUiAsync(run, () => FindById(run, "WorkspacePagesToggle:connection") is not null, "Connection pin toggle");
            Assert.Null(FindById(run, "WorkspacePagesToggle:settings"));
            Invoke(FindById(run, "WorkspacePagesToggle:connection")!);
            await WaitUiAsync(run, () => FindById(run, "WorkspacePinnedPage:connection") is not null, "Connection pin");
            Invoke(FindById(run, "WorkspacePinnedPage:connection")!);
            await WaitUiAsync(run, () => FindById(run, "ConnectionPageMarker")?.Current.IsOffscreen == false, "embedded Connection");
            var window = AppWindows(run).First(w => w.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "ConnectionPageMarker")) is not null);
            foreach (var theme in new[] { "Dark", "Light", "Dark" })
            {
                // Settings remains reachable normally, but is no longer itself a pinnable page.
                await run.InvokeAsync("app.navigate", new { page = "settings" });
                await WaitUiAsync(run, () => FindById(run, "SettingsPageAppTheme")?.Current.IsOffscreen == false, "theme selector");
                var selector = FindById(run, "SettingsPageAppTheme")!;
                ((ExpandCollapsePattern)selector.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
                await WaitUiAsync(run, () => FindInApp(run, new AndCondition(
                    new PropertyCondition(AutomationElement.NameProperty, theme),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))) is not null, $"theme {theme}");
                var option = FindInApp(run, new AndCondition(
                    new PropertyCondition(AutomationElement.NameProperty, theme),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)))!;
                ((SelectionItemPattern)option.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                ((ExpandCollapsePattern)selector.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
                await WaitUiAsync(run, () => FindById(run, "SettingsBack") is { Current.IsEnabled: true, Current.IsOffscreen: false },
                    "Settings history button after theme update");
                Invoke(FindById(run, "SettingsBack")!);
                var name = $"pinned-connection-theme-{theme.ToLowerInvariant()}.png";
                await WaitUiAsync(run, () =>
                {
                    var pageMarker = FindById(run, "ConnectionPageMarker");
                    if (pageMarker is null || pageMarker.Current.IsOffscreen) return false;
                    OwnedWindowCapture.Save(run, window, name, pageMarker);
                    var path = Path.Combine(run.ArtifactsDirectory, name);
                    using var metadata = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path, ".json")));
                    var marker = metadata.RootElement.GetProperty("assertions")[0];
                    var y = (int)(marker.GetProperty("top").GetDouble() + marker.GetProperty("Height").GetDouble()) + 8;
                    using var bitmap = new System.Drawing.Bitmap(path);
                    var color = bitmap.GetPixel(bitmap.Width - 50, y);
                    return theme == "Dark"
                        ? color.R < 128 && color.G < 128 && color.B < 128
                        : color.R > 192 && color.G > 192 && color.B > 192;
                }, $"embedded background matches {theme} after returning from Settings");
            }
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task PinnedPages_NativeSwitchTiming()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            const string draft = "Keep this draft while preparing pinned pages";
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput")?.Current.IsOffscreen == false,
                "initial chat composer");
            ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).SetValue(draft);
            var logPath = Path.Combine(run.Profile.DataDirectory, "openclaw-tray.log");
            var initialLogLength = File.Exists(logPath) ? File.ReadAllText(logPath).Length : 0;
            foreach (var tag in new[] { "connection", "sessions" })
            {
                Invoke(FindById(run, "WorkspacePagesCustomize")!);
                var toggleId = $"WorkspacePagesToggle:{tag}";
                await WaitUiAsync(run, () => FindById(run, toggleId) is not null, "pin toggle");
                Invoke(FindById(run, toggleId)!);
                await WaitUiAsync(run, () => FindById(run, $"WorkspacePinnedPage:{tag}") is not null, "pinned row");
            }

            Assert.True(((SelectionItemPattern)FindById(run, "WorkspaceNavHome")!
                .GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected);
            Assert.Equal(draft, ((ValuePattern)FindById(run, "ChatComposerInput")!
                .GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
            Assert.Null(FindById(run, "ConnectionPageMarker"));
            Assert.Null(FindById(run, "SessionsPageMarker"));
            var pinSaveLog = File.ReadAllText(logPath)[initialLogLength..];
            Assert.DoesNotContain("[SETTINGS] Change impact", pinSaveLog);
            Assert.DoesNotContain("Connecting to gateway:", pinSaveLog);
            var samples = new List<object>();
            for (var pass = 0; pass < 4; pass++)
            {
                foreach (var (tag, marker) in new[] { ("connection", "ConnectionPageMarker"), ("sessions", "SessionsPageMarker") })
                {
                    var row = FindById(run, $"WorkspacePinnedPage:{tag}")!;
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    Invoke(row);
                    var invocationMs = timer.Elapsed.TotalMilliseconds;
                    await WaitUiAsync(run, () => FindById(run, marker)?.Current.IsOffscreen == false, $"visible {tag}");
                    var visibleMs = timer.Elapsed.TotalMilliseconds;
                    Assert.True(((SelectionItemPattern)row.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected);
                    samples.Add(new { tag, pass, invocationMs, visibleMs });
                }
            }
            var selectedRow = FindById(run, "WorkspacePinnedPage:sessions")!;
            var selectedIdentity = selectedRow.GetRuntimeId();
            selectedRow.SetFocus();
            await WaitUiAsync(run, () => selectedRow.Current.HasKeyboardFocus, "pin focus before unrelated setting save");
            await run.InvokeAsync("app.settings.set", new { name = "HasSeenActivityStreamTip", value = "true" });
            await run.InvokeAsync("app.settings.get", new { name = "HasSeenActivityStreamTip" });
            selectedRow = FindById(run, "WorkspacePinnedPage:sessions")!;
            Assert.Equal(selectedIdentity, selectedRow.GetRuntimeId());
            Assert.True(selectedRow.Current.HasKeyboardFocus);
            Assert.True(((SelectionItemPattern)selectedRow.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected);
            var json = JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true });
            output.WriteLine(json);
            await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "pin-switch-timing.json"), json);
            await CaptureIfRequestedAsync(run, "pinned-sessions-timing.png");
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput")?.Current.IsOffscreen == false, "retained chat");
            Assert.Equal(draft, ((ValuePattern)FindById(run, "ChatComposerInput")!
                .GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task PinnedPages_SelectionCallbacksDoNotReinvokeNavigation()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            foreach (var tag in new[] { "connection", "sessions" })
            {
                Invoke(FindById(run, "WorkspacePagesCustomize")!);
                var toggleId = $"WorkspacePagesToggle:{tag}";
                await WaitUiAsync(run, () => FindById(run, toggleId) is not null, "pin toggle");
                Invoke(FindById(run, toggleId)!);
                await WaitUiAsync(run, () => FindById(run, $"WorkspacePinnedPage:{tag}") is not null, "pinned row");
            }

            // Use the real shipped NavigationView: its SelectedItem setter can synchronously
            // raise ItemInvoked even though the pin has SelectsOnInvoked=false.
            foreach (var (tag, marker) in new[]
            {
                ("connection", "ConnectionPageMarker"),
                ("sessions", "SessionsPageMarker"),
                ("connection", "ConnectionPageMarker"),
                ("connection", "ConnectionPageMarker"),
            })
            {
                Invoke(FindById(run, $"WorkspacePinnedPage:{tag}")!);
                await WaitUiAsync(run, () => FindById(run, marker)?.Current.IsOffscreen == false,
                    $"responsive embedded {tag} page");
                var row = FindById(run, $"WorkspacePinnedPage:{tag}")!;
                Assert.True(((SelectionItemPattern)row.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected);
                Assert.False(((SelectionItemPattern)FindById(run, "WorkspaceNavHome")!
                    .GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected);
                Assert.False(FindById(run, "WorkspacePagesCustomize")!.Current.IsOffscreen);
                run.EnsureRunning();
            }
            await CaptureIfRequestedAsync(run, "pinned-connection-selected.png");
            Invoke(FindById(run, "WorkspaceBack")!);
            await WaitUiAsync(run, () => FindById(run, "SessionsPageMarker")?.Current.IsOffscreen == false,
                "Back restores the preceding pin, not a recursive duplicate");
            Invoke(FindById(run, "WorkspaceForward")!);
            await WaitUiAsync(run, () => FindById(run, "ConnectionPageMarker")?.Current.IsOffscreen == false,
                "Forward restores the selected pin");
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput")?.Current.IsOffscreen == false,
                "chat remains usable after pin navigation");
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task SessionsDesignNativeWindowGallery()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "settings" });
            await WaitUiAsync(run, () => FindById(run, "NavSessions") is not null, "Settings navigation");
            Invoke(FindById(run, "NavSessions")!);
            await WaitUiAsync(run, () => FindById(run, "SessionsPageMarker") is not null
                && FindById(run, "SessionsHubList") is not null, "Sessions redesign loaded");
            CaptureSessionsWindow(run, "sessions-top.png");

            CaptureSessionsWidths(run);
            RestoreBaselineWindow(run);

            var manifest = new List<CaptureStateResult>();
            await CaptureStateAsync(run, manifest, "select", async () =>
            {
                ScrollIntoView(run, "SessionsHubSelectToggle");
                var select = FindById(run, "SessionsHubSelectToggle")!;
                AssertVisibleInHub(run, select, "select mode action");
                Invoke(select);
                await WaitUiAsync(run, () => FindById(run, "SessionsHubSelectedCount") is not null, "selection mode");
                foreach (var checkboxId in SettingsWindow(run).FindAll(TreeScope.Descendants,
                             new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox))
                         .Cast<AutomationElement>()
                         .Select(element => element.Current.AutomationId)
                         .Where(id => id.StartsWith("SessionsHubSelect:", StringComparison.Ordinal))
                         .Take(3)
                         .ToArray())
                {
                    ScrollIntoView(run, checkboxId);
                    var checkbox = FindById(run, checkboxId)!;
                    AssertVisibleInHub(run, checkbox, $"selection checkbox {checkboxId}");
                    Invoke(checkbox);
                }
                CaptureSessionsWindow(run, "sessions-select.png");
            });

            await CaptureStateAsync(run, manifest, "messages", async () =>
            {
                ScrollIntoView(run, "SessionsHubSearch");
                var search = FindById(run, "SessionsHubSearch")!;
                AssertVisibleInHub(run, search, "session name search");
                ((ValuePattern)search.GetCurrentPattern(ValuePattern.Pattern)).SetValue("backlog");
                await WaitUiAsync(run, () => FindButtonContaining(run, "backlog") is not null, "message search prompt");
                var prompt = FindButtonContaining(run, "backlog")!;
                AssertVisibleInHub(run, prompt, "message search prompt");
                Invoke(prompt);
                await WaitUiAsync(run, () => FindById(run, "SessionsHubTranscriptCount") is not null,
                    "message search results");
                CaptureSessionsWindow(run, "sessions-messages.png");
            });

            await CaptureStateAsync(run, manifest, "worktrees", async () =>
            {
                var tab = FindById(run, "SessionsHubTabWorktrees")!;
                AssertVisibleInHub(run, tab, "Worktrees tab");
                ((SelectionItemPattern)tab.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                var worktreeId = "SessionsHubWorktree:" + GatewayScenario.HubBusyWorktreeId;
                await WaitUiAsync(run, () => FindById(run, worktreeId) is not null, "worktrees list");
                ScrollIntoView(run, worktreeId);
                var worktree = FindById(run, worktreeId);
                AssertVisibleInHub(run, worktree!, "worktree row");
                if (worktree is not null && worktree.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expanderPattern))
                    ((ExpandCollapsePattern)expanderPattern).Expand();
                await Task.Delay(300);
                ScrollIntoView(run, worktreeId);
                CaptureSessionsWindow(run, "sessions-worktrees.png");
            });

            await CaptureStateAsync(run, manifest, "delete-dialog", async () =>
            {
                ScrollIntoView(run, "SessionsHubSelectToggle");
                var select = FindById(run, "SessionsHubSelectToggle")!;
                AssertVisibleInHub(run, select, "select mode for delete dialog");
                Invoke(select);
                await WaitUiAsync(run, () => FindById(run, "SessionsHubSelectedCount") is not null, "selection mode for delete");
                var checkbox = SettingsWindow(run).FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox))
                    .Cast<AutomationElement>()
                    .First(element => element.Current.AutomationId.StartsWith("SessionsHubSelect:", StringComparison.Ordinal));
                ScrollIntoView(run, checkbox.Current.AutomationId);
                checkbox = FindById(run, checkbox.Current.AutomationId)!;
                AssertVisibleInHub(run, checkbox, "delete selection checkbox");
                Invoke(checkbox);
                var delete = FindById(run, "SessionsHubDeleteSelected")!;
                AssertVisibleInHub(run, delete, "delete selected action");
                Invoke(delete);
                await WaitUiAsync(run, () => FindById(run, "SessionsHubDialog") is not null, "delete popup");
                CaptureSessionsWindow(run, "sessions-dialog-delete.png");
            });

            await CaptureStateAsync(run, manifest, "expanded", async () =>
            {
                var chatWindowCount = ChatWindowCount(run);
                await ExpandStoppedSessionRowAsync(run, "stopped session row");
                await WaitUiAsync(run, () => FindById(run, "SessionsHubDetails") is not null, "expanded session details");
                CaptureSessionsWindow(run, "sessions-expanded-top.png");
                ScrollIntoView(run, "SessionsHubDetails");
                await WaitUiAsync(run, () => FindById(run, "SessionsHubDetails") is { Current.IsOffscreen: false },
                    "visible expanded session details");
                Assert.Equal(chatWindowCount, ChatWindowCount(run));
                CaptureSessionsWindow(run, "sessions-expanded.png");
            });

            await CaptureStateAsync(run, manifest, "toast", async () =>
            {
                await ExpandStoppedSessionRowAsync(run, "stopped session row for toast");
                await WaitUiAsync(run, () => FindById(run, "SessionsHubCommandArchive") is not null,
                    "archive command for toast");
                var archive = FindById(run, "SessionsHubCommandArchive")!;
                AssertVisibleInHub(run, archive, "archive command for toast");
                Invoke(archive);
                await WaitUiAsync(run, () => FindById(run, "SessionsHubNotice") is not null, "archive toast");
                CaptureSessionsWindow(run, "sessions-toast.png");
            });

            await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "sessions-capture-manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            Assert.DoesNotContain(manifest, entry => entry.Status == "failed");
        }, scenario: GatewayScenario.CreateSessionsHub());

    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task HistoricalFractionalVoiceConversationRendersWithoutLoadingPlaceholder()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.VoiceTitle, GatewayScenario.VoiceSessionKey);
            await WaitUiAsync(run, () => RenderConsumedHistory(run, GatewayScenario.VoiceSessionKey)
                && IsVisibleInTimeline(run, GatewayScenario.VoiceFinalSentinel), "visible historical fractional voice reply");
            Assert.Null(FindText(run, "Loading messages..."));
            var snapshot = await run.InvokeAsync("app.chat.snapshot", new { threadId = GatewayScenario.VoiceSessionKey });
            var timeline = snapshot.GetProperty("selectedTimeline");
            Assert.True(timeline.GetProperty("historyLoaded").GetBoolean());
            var entries = timeline.GetProperty("entries").EnumerateArray().ToArray();
            Assert.Equal(30, entries.Length);
            for (var index = 0; index < entries.Length; index++)
            {
                var expectedText = index == 29 ? GatewayScenario.VoiceFinalSentinel : $"Fixture voice history message {index + 1:D2}.";
                Assert.Equal(expectedText, entries[index].GetProperty("Text").GetString());
            }
            await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "fractional-voice-snapshot.json"), snapshot.GetRawText());
            await CaptureIfRequestedAsync(run, "fractional-voice-history-loaded.png");
            await SelectSessionAsync(run, GatewayScenario.MainTitle, GatewayScenario.MainSessionKey);
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.MainSentinel), "integer typed control");
            await SelectSessionAsync(run, GatewayScenario.VoiceTitle, GatewayScenario.VoiceSessionKey);
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.VoiceFinalSentinel), "restored historical voice reply");
            Assert.Null(FindText(run, "Loading messages..."));
        }, scenario: GatewayScenario.CreateFractionalVoiceHistory());
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task ArchivedConversationIsRestoredFromSettingsNotSidebar()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.OtherTitle, GatewayScenario.OtherSessionKey);
            Assert.Null(FindById(run, "WorkspaceArchivedToggle"));
            await CaptureIfRequestedAsync(run, "sidebar-without-archives.png");
            await OpenSessionMenuAsync(run, GatewayScenario.OtherSessionKey);
            Invoke(FindById(run, "WorkspaceSessionMenuToggleArchive")!);
            await WaitUiAsync(run, () => FindById(run, $"WorkspaceSession:{GatewayScenario.OtherSessionKey}") is null,
                "archived conversation hidden from sidebar");

            await run.InvokeAsync("app.navigate", new { page = "sessions" });
            await WaitUiAsync(run, () => FindById(run, "SessionsPageStatusArchived") is not null, "Settings archived status filter");
            ((SelectionItemPattern)FindById(run, "SessionsPageStatusArchived")!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            var restoreId = $"SessionsPageUnarchive:{GatewayScenario.OtherSessionKey}";
            await WaitUiAsync(run, () => FindById(run, restoreId) is not null, "archived conversation in Settings");
            await WaitUiAsync(run, () => FindById(run, restoreId)?.Current.IsOffscreen == false, "visible unarchive action");
            await CaptureIfRequestedAsync(run, "settings-archived-conversation.png");
            Invoke(FindById(run, restoreId)!);
            await WaitUiAsync(run, () => FindById(run, restoreId) is null, "confirmed unarchive refresh");
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, $"WorkspaceSession:{GatewayScenario.OtherSessionKey}") is not null,
                "restored conversation returned to active sidebar");
            await SelectSessionAsync(run, GatewayScenario.OtherTitle, GatewayScenario.OtherSessionKey);
            Assert.Null(FindById(run, "WorkspaceArchivedToggle"));
            Assert.True(SessionSelected(run, GatewayScenario.OtherTitle));
        }, allowSessionMutations: true);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task SessionRemoval_WaitsForAcceptanceAndClearsLastMountedConversation()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.OtherTitle, GatewayScenario.OtherSessionKey);
            run.Gateway.HoldSessionMutations();
            await OpenSessionMenuAsync(run, GatewayScenario.OtherSessionKey);
            Invoke(FindById(run, "WorkspaceSessionMenuToggleArchive")!);
            await run.Gateway.WaitForRequestAsync("sessions.patch", GatewayScenario.OtherSessionKey);
            Assert.NotNull(FindById(run, "ChatComposerInput"));
            Assert.True(SessionSelected(run, GatewayScenario.OtherTitle));
            await CaptureIfRequestedAsync(run, "archive-pending.png");
            run.Gateway.ReleaseSessionMutations();
            await WaitUiAsync(run, () => FindById(run, $"WorkspaceSession:{GatewayScenario.OtherSessionKey}") is null,
                "accepted archive removed from active rows");
            await WaitUiAsync(run, () => SessionSelected(run, GatewayScenario.EdgeTitle),
                "accepted archive selected the remaining conversation");
            await WaitHistoryAsync(run, GatewayScenario.EdgeSessionKey);
            await OpenSessionMenuAsync(run, GatewayScenario.EdgeSessionKey);
            Invoke(FindById(run, "WorkspaceSessionMenuDelete")!);
            await WaitUiAsync(run, () => FindButton(run, "Delete") is not null, "delete confirmation");
            Invoke(FindButton(run, "Delete")!);
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSelectConversation") is { Current.IsOffscreen: false },
                "explicit empty chat after last removal");
            Assert.Null(FindById(run, "ChatComposerInput"));
            await CaptureIfRequestedAsync(run, "last-session-removed.png");
            if (FindById(run, "WorkspaceBack") is { Current.IsEnabled: true } back)
            {
                Invoke(back);
                await WaitUiAsync(run, () => !SessionSelected(run, GatewayScenario.EdgeTitle), "history excludes removed session");
            }
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "chat.send");
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "sessions.create");
        }, allowSessionMutations: true);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task SessionRemoval_RejectionPreservesSelectedConversationAndDraft()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.OtherTitle, GatewayScenario.OtherSessionKey);
            const string draft = "Keep this draft after rejection";
            ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).SetValue(draft);
            await OpenSessionMenuAsync(run, GatewayScenario.OtherSessionKey);
            Invoke(FindById(run, "WorkspaceSessionMenuToggleArchive")!);
            await WaitUiAsync(run, () => FindText(run, "Fixture Gateway is read-only") is not null, "visible archive rejection");
            Assert.True(SessionSelected(run, GatewayScenario.OtherTitle));
            Assert.Equal(draft, ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
            await CaptureIfRequestedAsync(run, "archive-rejected.png");
        });
    }

    private static async Task OpenSessionMenuAsync(GatewayFixtureRun run, string key)
    {
        var window = AppWindows(run).First(window => window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, $"WorkspaceSession:{key}")) is not null);
        if (!SetForegroundWindow(new IntPtr(window.Current.NativeWindowHandle)))
            throw new InvalidOperationException("Cannot activate the isolated Workspace for keyboard proof.");
        var row = FindById(run, $"WorkspaceSession:{key}")!;
        row.SetFocus();
        await WaitUiAsync(run, () => FindById(run, $"WorkspaceSession:{key}")?.Current.HasKeyboardFocus == true,
            "session row keyboard focus");
        System.Windows.Forms.SendKeys.SendWait("+{F10}");
        await WaitUiAsync(run, () => FindById(run, "WorkspaceSessionMenuToggleArchive") is not null, "session context menu");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task OwnerFooterUsesAuthenticatedProfileAndLiveConnectionStatus()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "WorkspaceOwner")?.Current.Name == "Fixture Owner",
                "authenticated profile in footer");
            var owner = FindById(run, "WorkspaceOwner")!;
            Assert.Contains("Connected", owner.Current.HelpText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Personal workspace", owner.Current.HelpText);
            await WaitForAuthenticatedOwnerPhotoAsync(run);
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task OwnerFooterRendersSharedOwnerForGatewayOwnerProfile()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "WorkspaceOwner")?.Current.Name == "Shared owner",
                "shared Gateway owner label in footer");
            var owner = FindById(run, "WorkspaceOwner")!;
            Assert.Equal("Shared owner", owner.Current.Name);
            Assert.Contains("Connected", owner.Current.HelpText, StringComparison.OrdinalIgnoreCase);
            await WaitForAuthenticatedOwnerPhotoAsync(run);
        }, scenario: GatewayScenario.CreateBrowseWithOwnerProfile("gateway-owner", "Fixture Shared Person"));
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task OwnerFooterLoadsAvatarForProfileWithoutEmailOrUploadMetadata()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "WorkspaceOwner")?.Current.Name == "Fixture Owner",
                "named profile without avatar eligibility hints in footer");
            var owner = FindById(run, "WorkspaceOwner")!;
            Assert.Contains("Connected", owner.Current.HelpText, StringComparison.OrdinalIgnoreCase);
            await WaitForAuthenticatedOwnerPhotoAsync(run);
            await run.InvokeAsync("app.navigate", new { page = "settings" });
            await WaitUiAsync(run, () => FindById(run, "SettingsOwner") is
                { Current.IsOffscreen: false, Current.Name: "Fixture Owner" },
                "authenticated owner in newly created Settings footer");
            if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
                await WaitUiAsync(run, () => OwnerPhotoPainted(run, "SettingsOwner", "settings-owner-profile.png"),
                    "cached owner photo painted in newly created Settings footer");
        }, scenario: GatewayScenario.CreateBrowseWithOwnerProfile("fixture-owner", "Fixture Owner"));
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task SessionHistoryRestoresSelectionTranscriptAndDraftAcrossAgents()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.LongSessionTitle, GatewayScenario.LongSessionKey);
            var home = FindById(run, "WorkspaceNavHome")!.Current.BoundingRectangle;
            var add = FindById(run, "WorkspaceSessionsAdd")!;
            Assert.True(add.Current.IsEnabled);
            Assert.InRange(add.Current.BoundingRectangle.Right - home.Right, -1, 1);
            const string draft = "Keep draft through back and forward";
            ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).SetValue(draft);
            await SelectSessionAsync(run, GatewayScenario.OtherSessionTitle, GatewayScenario.OtherSessionKey);
            await WaitUiAsync(run, () => run.Gateway.Requests.Any(request =>
                request.Method == "models.list" && request.SessionKey == GatewayScenario.OtherSessionKey),
                "session-scoped worker model request");
            await WaitUiAsync(run, () => FindById(run, "ChatComposerModelPicker") is { } picker
                && picker.Current.IsEnabled
                && picker.Current.Name == "Model: Fixture Worker", "worker-scoped model picker");
            Invoke(FindById(run, "ChatComposerModelPicker")!);
            await WaitUiAsync(run, () => FindById(run, "ChatModelChoice_fixture/worker") is { } choice
                && choice.Current.IsEnabled
                && choice.Current.Name.Contains("Fixture Worker", StringComparison.Ordinal),
                "worker-scoped model choice");
            await CaptureIfRequestedAsync(run, "worker-session-model-catalog.png");
            Assert.True(FindById(run, "WorkspaceBack")!.Current.IsEnabled);
            Invoke(FindById(run, "WorkspaceBack")!);
            await WaitUiAsync(run, () => SessionSelected(run, GatewayScenario.LongSessionTitle)
                && IsVisibleInTimeline(run, GatewayScenario.LongHistoryFinalMarker), "back to original session and transcript");
            await WaitUiAsync(run, () => FindById(run, "ChatComposerModelPicker")?.Current.Name == "Model: Fixture Browse",
                "gateway model catalog after leaving worker session");
            Assert.Equal(draft, ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
            Assert.True(FindById(run, "WorkspaceForward")!.Current.IsEnabled);
            Invoke(FindById(run, "WorkspaceForward")!);
            await WaitUiAsync(run, () => SessionSelected(run, GatewayScenario.OtherSessionTitle)
                && IsVisibleInTimeline(run, GatewayScenario.OtherHistoryMarker), "forward across agents");
            Assert.False(FindById(run, "WorkspaceForward")!.Current.IsEnabled);
            await run.InvokeAsync("app.navigate", new { page = "workspace:notifications" });
            Invoke(FindById(run, "WorkspaceBack")!);
            await WaitUiAsync(run, () => SessionSelected(run, GatewayScenario.OtherSessionTitle), "back from notifications");
            Invoke(FindById(run, "WorkspaceBack")!);
            await WaitUiAsync(run, () => SessionSelected(run, GatewayScenario.LongSessionTitle), "back to first conversation");
            await SelectSessionAsync(run, GatewayScenario.EmptyTitle, GatewayScenario.EmptySessionKey);
            Assert.False(FindById(run, "WorkspaceForward")!.Current.IsEnabled);
            Invoke(FindById(run, "WorkspaceBack")!);
            await WaitUiAsync(run, () => SessionSelected(run, GatewayScenario.LongSessionTitle), "back after branching history");
            Assert.Equal(draft, ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task CurrentModelOutsideManualPolicyRetainsLabelAndReasoning()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.EdgeTitle, GatewayScenario.EdgeSessionKey);
            await WaitUiAsync(run, () => FindById(run, "ChatComposerModelPicker") is { } picker
                && picker.Current.IsEnabled
                && picker.Current.Name == "Model: Fixture Retained Research", "retained current-model label");
            await WaitUiAsync(run, () => FindById(run, "ChatComposerReasoningPicker")?.Current.IsEnabled == true,
                "reasoning from retained model metadata");
            Invoke(FindById(run, "ChatComposerModelPicker")!);
            await WaitUiAsync(run, () => FindById(run, "ChatModelChoice_fixture/research") is { } choice
                && !choice.Current.IsEnabled
                && choice.Current.Name.Contains("Fixture Retained Research", StringComparison.Ordinal),
                "visible but nonselectable current model");
            Assert.Contains(run.Gateway.Requests, request => request.Method == "models.list"
                && request.SessionKey == GatewayScenario.EdgeSessionKey
                && request.ModelCatalogView == "configured");
            await CaptureIfRequestedAsync(run, "retained-current-model-catalog.png");
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task SessionsAddRequiresExplicitAssistantSelection()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSessionsAdd") is { } add &&
                !add.Current.IsEnabled && add.Current.HelpText == "Select assistant", "required assistant selection");
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "sessions.create");
            await CaptureIfRequestedAsync(run, "assistant-selection-required.png");
            await SelectSessionAsync(run, GatewayScenario.LongSessionTitle, GatewayScenario.LongSessionKey);
            await WaitUiAsync(run, () => FindById(run, "WorkspaceSessionsAdd")?.Current.IsEnabled == true,
                "conversation creation enabled after assistant selection");
            Assert.Equal(string.Empty, FindById(run, "WorkspaceSessionsAdd")!.Current.HelpText);
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "sessions.create");
            await CaptureIfRequestedAsync(run, "assistant-selection-ready.png");
        }, requireAgentSelection: true);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task NewAgentUsesGatewayCreationAndRefreshesWithoutReplacingConversation()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.LongSessionTitle, GatewayScenario.LongSessionKey);
            var commands = await run.InvokeAsync("app.search", new { query = "Connection" });
            Assert.Contains("Connection", commands.GetRawText(), StringComparison.OrdinalIgnoreCase);
            var draft = "Keep this draft while creating an agent";
            ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).SetValue(draft);
            var selector = FindById(run, "WorkspaceAssistantSelector")!;
            ((ExpandCollapsePattern)selector.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            await WaitUiAsync(run, () => FindById(run, "WorkspaceNewAgent") is not null, "new agent action");
            Invoke(FindById(run, "WorkspaceNewAgent")!);
            await WaitUiAsync(run, () => FindById(run, "AgentCreationName") is not null, "native creation dialog");
            ((ValuePattern)FindById(run, "AgentCreationName")!.GetCurrentPattern(ValuePattern.Pattern)).SetValue("Fixture Created");
            ((ValuePattern)FindById(run, "AgentCreationWorkspace")!.GetCurrentPattern(ValuePattern.Pattern)).SetValue("/fixture/new-agent");
            await WaitUiAsync(run, () => FindButton(run, "Create agent")?.Current.IsEnabled == true, "enabled create action");
            Invoke(FindButton(run, "Create agent")!);
            await WaitUiAsync(run, () => FindById(run, "AgentCreationDialog") is null, "confirmed creation");
            Assert.Single(run.Gateway.Requests, request => request.Method == "agents.create" && request.Outcome == "ok");
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "sessions.create");
            Assert.Equal(draft, ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
            Assert.True(SessionSelected(run, GatewayScenario.LongSessionTitle));
            ((ExpandCollapsePattern)selector.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            await WaitUiAsync(run, () => FindById(run, "WorkspaceAgent:fixture-created") is not null, "server-returned agent");
            Assert.Equal("Fixture Created, fixture-created", FindById(run, "WorkspaceAgent:fixture-created")!.Current.Name);
            Assert.Equal(ExpandCollapseState.Expanded,
                ((ExpandCollapsePattern)selector.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState);
            Assert.Equal(draft, ((ValuePattern)FindById(run, "ChatComposerInput")!.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
            Assert.True(SessionSelected(run, GatewayScenario.LongSessionTitle));
            ((ExpandCollapsePattern)selector.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
        }, allowAgentCreation: true);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task SidebarSwitchesRealHistoriesAndShowsMessage240AtBothWidths()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "WorkspaceNavigation") is not null, "native session navigation");
            for (var iteration = 0; iteration < 3; iteration++)
            {
                await SelectSessionAsync(run, GatewayScenario.LongSessionTitle, GatewayScenario.LongSessionKey);
                // This must pass BEFORE driving the scrollbar: a manual jump could hide a broken initial-tail request.
                await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.LongHistoryFinalMarker), "natural tail at message 240");
                await SelectSessionAsync(run, GatewayScenario.OtherSessionTitle, GatewayScenario.OtherSessionKey);
                await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.OtherHistoryMarker), "other session's visible history");
                Assert.False(IsVisibleInTimeline(run, GatewayScenario.LongHistoryFinalMarker));
            }
            await SelectSessionAsync(run, GatewayScenario.LongSessionTitle, GatewayScenario.LongSessionKey);
            var measurements = new List<object>();
            double narrowWidth = 0;
            foreach (var name in new[] { "narrow", "wide" })
            {
                var window = FindHub(run);
                var transform = (TransformPattern)window.GetCurrentPattern(TransformPattern.Pattern);
                Assert.True(transform.Current.CanResize);
                transform.Resize(name == "narrow" ? 900 : narrowWidth + 400, name == "narrow" ? 720 : 950);
                await ScrollToAsync(run, 0);
                await ScrollToAsync(run, 100);
                try
                {
                    await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.LongHistoryFinalMarker), $"message 240 visible at {name} width");
                }
                catch (TimeoutException)
                {
                    var timeline = FindTimeline(run);
                    var message = FindText(run, GatewayScenario.LongHistoryFinalMarker);
                    output.WriteLine(JsonSerializer.Serialize(new
                    {
                        width = name,
                        viewport = timeline?.Current.BoundingRectangle.ToString(),
                        messageBounds = message?.Current.BoundingRectangle.ToString(),
                        messageOffscreen = message?.Current.IsOffscreen,
                        window = window.Current.BoundingRectangle.ToString(),
                    }));
                    throw;
                }
                var bounds = window.Current.BoundingRectangle;
                if (name == "narrow")
                    narrowWidth = bounds.Width;
                else
                    Assert.True(bounds.Width >= narrowWidth + 200, "The host did not provide two meaningfully different window widths.");
                measurements.Add(new { name, width = bounds.Width, height = bounds.Height, finalMarker = GatewayScenario.LongHistoryFinalMarker });
                await CaptureIfRequestedAsync(run, $"long-history-{name}.png");
            }
            await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "layout-proof.json"), JsonSerializer.Serialize(measurements));
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task PopulatedPagesPreserveSelectionAndPreferencesStayInDisposableProfile()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.OtherSessionTitle, GatewayScenario.OtherSessionKey);
            foreach (var (page, marker) in new[]
            {
                ("sessions", "SessionsPageMarker"),
                ("settings", "SettingsPageMarker"),
                ("config", "ConfigPageMarker")
            })
            {
                await run.InvokeAsync("app.navigate", new { page });
                await WaitUiAsync(run, () => FindById(run, marker) is not null, page);
                if (page == "settings")
                {
                    var toggle = FindById(run, "SettingsPageShowToolCalls");
                    Assert.NotNull(toggle);
                    ((TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
                    await run.WaitForAsync(() =>
                    {
                        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(run.Profile.DataDirectory, "settings.json")));
                        return Task.FromResult(!settings.RootElement.GetProperty("ShowChatToolCalls").GetBoolean());
                    }, "profile-only preference persistence");
                }
                if (page == "config")
                {
                    await run.WaitForAsync(async () =>
                    {
                        using var result = await run.Client.CallToolExpectSuccessAsync("app.config.get");
                        return !result.RootElement.TryGetProperty("error", out _);
                    }, "synthetic Gateway configuration");
                    await CaptureIfRequestedAsync(run, "gateway-configuration.png");
                }
            }
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => SessionSelected(run, GatewayScenario.OtherSessionTitle)
                && IsVisibleInTimeline(run, GatewayScenario.OtherHistoryMarker), "selected session after page navigation");
            await run.InvokeAsync("app.navigate", new { page = "sessions" });
            await WaitUiAsync(run, () => FindText(run, GatewayScenario.LongSessionTitle) is not null, "long session row");
            var row = FindText(run, GatewayScenario.LongSessionTitle)!;
            for (var depth = 0; depth < 15 && row.Current.ControlType != ControlType.ListItem; depth++)
                row = TreeWalker.ControlViewWalker.GetParent(row)
                    ?? throw new InvalidOperationException("Session title has no list-item ancestor.");
            var openChat = row.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                .Cast<AutomationElement>()
                .FirstOrDefault(button => button.Current.AutomationId.StartsWith("SessionsHubOpen:", StringComparison.Ordinal));
            Assert.NotNull(openChat);
            Invoke(openChat);
            await WaitUiAsync(run, () => SessionSelected(run, GatewayScenario.LongSessionTitle)
                && IsVisibleInTimeline(run, GatewayScenario.LongHistoryFinalMarker), "Sessions-page action routes to long chat");
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task EmptySessionRendersConnectedComposerWithoutPreviousHistory()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.OtherSessionTitle, GatewayScenario.OtherSessionKey);
            await SelectSessionAsync(run, GatewayScenario.EmptyTitle, GatewayScenario.EmptySessionKey);
            await WaitUiAsync(run, () => RenderConsumedHistory(run, GatewayScenario.EmptySessionKey), "rendered empty-session history");
            Assert.True(SessionSelected(run, GatewayScenario.EmptyTitle));
            Assert.NotNull(FindById(run, "ChatComposerInput"));
            Assert.False(IsVisibleInTimeline(run, GatewayScenario.OtherHistoryMarker));
            var snapshot = await run.InvokeAsync("app.chat.snapshot", new { threadId = GatewayScenario.EmptySessionKey });
            Assert.Empty(snapshot.GetProperty("selectedTimeline").GetProperty("entries").EnumerateArray());
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task ReconnectKeepsDefinitelyUnsentQueuedMessageFailedAndVisible()
    {
        var scenario = GatewayScenario.CreateBrowse(allowChatSend: true);
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(
                run,
                GatewayScenario.MainTitle,
                GatewayScenario.MainSessionKey);
            run.Gateway.HoldChatSends();

            var activeSend = run.InvokeAsync("app.chat.send", new
            {
                threadId = GatewayScenario.MainSessionKey,
                message = "active-1669-current-head"
            });
            await run.Gateway.WaitForRequestAsync(
                "chat.send",
                GatewayScenario.MainSessionKey);
            await run.InvokeAsync("app.chat.send", new
            {
                threadId = GatewayScenario.MainSessionKey,
                message = "queued-1669-current-head"
            });

            JsonElement queued = default;
            await run.WaitForAsync(async () =>
            {
                var result = await run.InvokeAsync("app.chat.queue.list", new
                {
                    threadId = GatewayScenario.MainSessionKey
                });
                queued = result.GetProperty("selectedThread")
                    .GetProperty("messages")
                    .EnumerateArray()
                    .SingleOrDefault(message =>
                        message.GetProperty("text").GetString() ==
                        "queued-1669-current-head");
                return queued.ValueKind == JsonValueKind.Object &&
                       queued.GetProperty("sendState").GetString() == "Queued";
            }, "queued message visible before reconnect");
            var queuedId = queued.GetProperty("id").GetString()!;
            await WaitUiAsync(
                run,
                () => FindById(run, $"ChatQueuedMessage_{queuedId}") is not null,
                "queued message card before reconnect");
            await CaptureIfRequestedAsync(run, "reconnect-queued-before.png");

            await run.Gateway.CloseConnectionsAsync();
            run.Gateway.ReleaseChatSends();
            _ = await activeSend;

            await run.WaitForAsync(async () =>
            {
                var status = await run.InvokeAsync("app.status");
                return status.GetProperty("operatorState").GetString() ==
                    "Connected";
            }, "fixture reconnect");
            await run.WaitForAsync(async () =>
            {
                var result = await run.InvokeAsync("app.chat.queue.list", new
                {
                    threadId = GatewayScenario.MainSessionKey
                });
                queued = result.GetProperty("selectedThread")
                    .GetProperty("messages")
                    .EnumerateArray()
                    .SingleOrDefault(message =>
                        message.GetProperty("id").GetString() == queuedId);
                return queued.ValueKind == JsonValueKind.Object &&
                       queued.GetProperty("sendState").GetString() == "Failed";
            }, "queued message failed after reconnect");

            Assert.Equal(
                "The connection dropped before the gateway accepted this message.",
                queued.GetProperty("errorText").GetString());
            Assert.Single(
                run.Gateway.Requests,
                request => request.Method == "chat.send");
            await WaitUiAsync(
                run,
                () => FindById(
                    run,
                    $"ChatQueuedMessageRemoveFailed_{queuedId}") is not null,
                "failed queued message remove action");
            await CaptureIfRequestedAsync(run, "reconnect-queued-after.png");
        }, scenario: scenario);
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task LateHistoryForPreviousSessionCannotReplaceSelectedTranscript()
    {
        await WithAppAsync(async run =>
        {
            run.Gateway.HoldHistory(GatewayScenario.LongSessionKey);
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await SelectSessionAsync(run, GatewayScenario.LongSessionTitle, GatewayScenario.LongSessionKey, waitForHistory: false);
            await run.WaitForAsync(() => Task.FromResult(run.Gateway.Requests.Any(request =>
                request.Method == "chat.history" && request.SessionKey == GatewayScenario.LongSessionKey)), "held long-history request");
            await SelectSessionAsync(run, GatewayScenario.OtherSessionTitle, GatewayScenario.OtherSessionKey);
            await run.Gateway.ReleaseHistoryAsync(GatewayScenario.LongSessionKey);
            await WaitHistoryAsync(run, GatewayScenario.LongSessionKey);
            await WaitUiAsync(run, () => RenderConsumedHistory(run, GatewayScenario.LongSessionKey),
                "native composer rendering the snapshot containing delayed A history");
            Assert.True(SessionSelected(run, GatewayScenario.OtherSessionTitle));
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.OtherHistoryMarker), "other history after late response");
            Assert.False(IsVisibleInTimeline(run, GatewayScenario.LongHistoryFinalMarker));
            await SelectSessionAsync(run, GatewayScenario.LongSessionTitle, GatewayScenario.LongSessionKey);
            await WaitUiAsync(run, () => IsVisibleInTimeline(run, GatewayScenario.LongHistoryFinalMarker), "cached delayed history when actually selected");
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task ApprovalCardsGateAllowAndResolveThroughGateway()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is not null, "native chat composer");
            await WaitHistoryAsync(run, GatewayScenario.MainSessionKey);

            await run.Gateway.PublishAgentEventAsync(GatewayScenario.MainSessionKey, new
            {
                phase = "requested",
                approvalId = "fixture-safe-approval",
                command = "echo fixture-safe",
                title = "Command approval requested",
            });
            await WaitUiAsync(run, () => FindButton(run, "Allow once") is not null, "reviewable Allow button");
            Assert.NotNull(FindButton(run, "Always allow"));
            Assert.NotNull(FindButton(run, "Deny once"));
            Invoke(FindButton(run, "Allow once")!);
            await run.WaitForAsync(() => Task.FromResult(run.Gateway.Requests.Any(request =>
                request.Method == "exec.approval.resolve"
                && request.Decision == "allow-once"
                && request.Outcome == "ok")), "reviewable Allow RPC");
            var allowed = run.Gateway.Requests.Single(request =>
                request.Method == "exec.approval.resolve" && request.Decision == "allow-once");
            Assert.Equal("fixture-safe-approval", allowed.ApprovalId);
            Assert.Equal("allow-once", allowed.Decision);
            Assert.Equal("ok", allowed.Outcome);

            await run.Gateway.PublishAgentEventAsync(GatewayScenario.MainSessionKey, new
            {
                phase = "requested",
                approvalId = "fixture-unreviewable-approval",
                command = "",
                message = "Please approve this request.",
                title = "Command approval requested",
            });
            await WaitUiAsync(run, () => FindButton(run, "Deny once") is not null
                && FindText(run, "only Deny is available") is not null, "Deny-only approval");
            Assert.Null(FindButton(run, "Allow once"));
            Assert.Null(FindButton(run, "Always allow"));
            await CaptureIfRequestedAsync(run, "approval-deny-only.png");
            Invoke(FindButton(run, "Deny once")!);
            await run.WaitForAsync(() => Task.FromResult(run.Gateway.Requests.Any(request =>
                request.Method == "exec.approval.resolve"
                && request.Decision == "deny"
                && request.Outcome == "ok")), "Deny-only RPC");
            var denied = run.Gateway.Requests.Single(request =>
                request.Method == "exec.approval.resolve" && request.Decision == "deny");
            Assert.Equal("fixture-unreviewable-approval", denied.ApprovalId);
            Assert.Equal("deny", denied.Decision);
            Assert.Equal("ok", denied.Outcome);

            await run.Gateway.PublishAgentEventAsync(GatewayScenario.MainSessionKey, new
            {
                phase = "requested",
                approvalId = "fixture-superseded-approval",
                command = "echo superseded",
                title = "Command approval requested",
            });
            await WaitUiAsync(run, () => FindButton(run, "Allow once") is not null, "superseded Allow button");
            await run.Gateway.PublishAgentEventAsync(GatewayScenario.MainSessionKey, new
            {
                phase = "resolved",
                approvalId = "fixture-superseded-approval",
                decision = "deny",
            });
            await WaitUiAsync(run, () => FindButton(run, "Allow once") is null
                && FindButton(run, "Always allow") is null, "superseded actions removed");
            await Task.Delay(200);
            Assert.Equal(2, run.Gateway.Requests.Count(request => request.Method == "exec.approval.resolve"));
        });
    }

    private async Task WithAppAsync(Func<GatewayFixtureRun, Task> test, bool allowAgentCreation = false,
        bool requireAgentSelection = false, bool allowSessionMutations = false, GatewayScenario? scenario = null,
        Func<GatewayFixtureProfile, string>? prepareSetupHandoff = null, bool postSetupRestart = false,
        Action? releasePendingResponses = null)
    {
        var appPath = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_APP")
            ?? throw new InvalidOperationException("Set OPENCLAW_GATEWAY_FIXTURE_APP to the freshly built app. No installed-app fallback is allowed.");
        await using var run = await GatewayFixtureRun.StartAsync(appPath,
            Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS"),
            allowAgentCreation: allowAgentCreation, requireAgentSelection: requireAgentSelection,
            allowSessionMutations: allowSessionMutations,
            scenario: scenario, prepareSetupHandoff: prepareSetupHandoff, postSetupRestart: postSetupRestart,
            releasePendingResponses: releasePendingResponses);
        try
        {
            output.WriteLine($"Fixture run {run.Profile.RunId}, PID {run.AppProcessId}, artifacts: {run.ArtifactsDirectory}");
            // UIA is synchronous and can block inside a hung app. Keep the deadline
            // outside that worker so failure reporting and owned-process cleanup still run.
            await Task.Run(() => test(run)).WaitAsync(TimeSpan.FromSeconds(150)).ConfigureAwait(false);
            run.EnsureRunning();
            Assert.Empty(run.Gateway.UnexpectedRequests);
            var crashLog = Path.Combine(run.Profile.DataDirectory, "crash.log");
            Assert.False(File.Exists(crashLog) && new FileInfo(crashLog).Length > 0, "The real app wrote a crash log.");
            await run.WriteReportAsync("passed");
        }
        catch (Exception ex)
        {
            releasePendingResponses?.Invoke();
            await run.WriteReportAsync("failed", ex);
            if (run.IsRunning)
            {
                try { await CaptureIfRequestedAsync(run, "failure.png"); }
                catch (Exception captureError)
                {
                    output.WriteLine($"Failure screenshot unavailable: {captureError.Message}");
                    await File.WriteAllTextAsync(Path.Combine(run.ArtifactsDirectory, "screenshot-error.txt"), captureError.ToString());
                }
            }
            throw;
        }
        finally
        {
            // Release response gates before await-using disposes the server and joins its connections.
            releasePendingResponses?.Invoke();
        }
    }

    private static async Task SelectSessionAsync(GatewayFixtureRun run, string title, string key, bool waitForHistory = true)
    {
        var selector = await WaitForElementByIdAsync(run, "WorkspaceAssistantSelector", "agent selector");
        var agentId = $"WorkspaceAgent:{SessionDisplayResolver.Resolve(new SessionInfo { Key = key }).AgentId}";
        var selection = (SelectionPattern)selector.GetCurrentPattern(SelectionPattern.Pattern);
        if (!selection.Current.GetSelection().Any(item => item.Current.AutomationId == agentId))
        {
            var dropdown = (ExpandCollapsePattern)selector.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            dropdown.Expand();
            Invoke(await WaitForElementByIdAsync(run, agentId, "session's agent"));
            dropdown.Collapse();
        }
        var rowId = key == GatewayScenario.MainSessionKey ? "WorkspaceNavHome" : $"WorkspaceSession:{key}";
        Invoke(await WaitForElementByIdAsync(run, rowId, $"sidebar session {title}"));
        await WaitComposerSelectionAsync(run, key, waitForHistory);
        if (key != GatewayScenario.MainSessionKey)
            await WaitUiAsync(run, () => SessionSelected(run, title), $"selected session {title}");
        if (waitForHistory) await WaitHistoryAsync(run, key);
    }

    private static Task WaitHistoryAsync(GatewayFixtureRun run, string key) =>
        run.WaitForAsync(async () =>
        {
            var snapshot = await run.InvokeAsync("app.chat.snapshot", new { threadId = key });
            return snapshot.TryGetProperty("selectedTimeline", out var timeline)
                && timeline.ValueKind == JsonValueKind.Object
                && timeline.GetProperty("historyLoaded").GetBoolean();
        }, $"history loaded for {key}");

    private static bool SessionSelected(GatewayFixtureRun run, string title)
    {
        var item = FindInApp(run, new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, title),
            new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true)));
        return item is not null && ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected;
    }

    private static bool RenderConsumedHistory(GatewayFixtureRun run, string key)
    {
        var composer = FindById(run, "ChatComposerInput");
        if (composer is null || string.IsNullOrEmpty(composer.Current.ItemStatus)) return false;
        using var rendered = JsonDocument.Parse(composer.Current.ItemStatus);
        return rendered.RootElement.GetProperty("loadedThreadIds").EnumerateArray()
            .Any(thread => thread.GetString() == key);
    }

    private static void Invoke(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            ((InvokePattern)invoke).Invoke();
        else if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
            ((SelectionItemPattern)selection).Select();
        else if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
            ((TogglePattern)toggle).Toggle();
        else
            throw new InvalidOperationException($"Control '{element.Current.Name}' has no activation pattern. Available: {string.Join(", ", element.GetSupportedPatterns().Select(pattern => pattern.ProgrammaticName))}");
    }

    private static AutomationElement? FindById(GatewayFixtureRun run, string id) =>
        FindInApp(run, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private static AutomationElement? FindButton(GatewayFixtureRun run, string name) =>
        FindInApp(run, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, name)));

    private static AutomationElement? FindButtonContaining(GatewayFixtureRun run, string text)
    {
        foreach (var window in AppWindows(run))
        foreach (AutomationElement element in window.FindAll(TreeScope.Descendants,
                     new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
            if (element.Current.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
                return element;
        return null;
    }

    private static AutomationElement? FindText(GatewayFixtureRun run, string text)
    {
        foreach (var window in AppWindows(run))
        {
            var matches = window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            foreach (AutomationElement element in matches)
                if (element.Current.Name.Contains(text, StringComparison.Ordinal))
                    return element;
        }
        return null;
    }

    private static AutomationElement? FindInApp(GatewayFixtureRun run, Condition condition)
    {
        foreach (var window in AppWindows(run))
            if (window.FindFirst(TreeScope.Descendants, condition) is { } match)
                return match;
        return null;
    }

    private static IEnumerable<AutomationElement> AppWindows(GatewayFixtureRun run)
    {
        run.EnsureRunning();
        return AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, run.AppProcessId)).Cast<AutomationElement>()
            .Where(window => !window.Current.IsOffscreen);
    }

    private static AutomationElement FindHub(GatewayFixtureRun run) =>
        AppWindows(run).First(window => window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ChatComposerInput")) is not null);

    private static AutomationElement? FindTimeline(GatewayFixtureRun run) =>
        FindInApp(run, new PropertyCondition(AutomationElement.NameProperty, "Chat messages"));

    private static bool IsVisibleInTimeline(GatewayFixtureRun run, string text)
    {
        var timeline = FindTimeline(run);
        var message = FindText(run, text);
        return timeline is not null && message is not null && IsVisibleInTimeline(timeline, message);
    }

    private static bool IsVisibleInTimeline(AutomationElement timeline, AutomationElement message)
    {
        if (message.Current.IsOffscreen)
            return false;
        var viewport = timeline.Current.BoundingRectangle;
        var bounds = message.Current.BoundingRectangle;
        return !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0
            && bounds.Top >= viewport.Top - 1 && bounds.Bottom <= viewport.Bottom + 1
            && bounds.Right > viewport.Left && bounds.Left < viewport.Right;
    }

    private async Task ScrollToAsync(GatewayFixtureRun run, double percent)
    {
        AutomationElement? timeline = null;
        await WaitUiAsync(run, () => (timeline = FindTimeline(run)) is not null,
            "mounted chat timeline before scrolling");
        var scrollElement = timeline!;
        if (!scrollElement.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern))
        {
            scrollElement = scrollElement.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true))
                ?? throw new InvalidOperationException("Chat timeline has no accessible scroll control.");
            pattern = scrollElement.GetCurrentPattern(ScrollPattern.Pattern);
        }
        var scroll = (ScrollPattern)pattern;
        var before = scroll.Current.VerticalScrollPercent;
        scroll.SetScrollPercent(ScrollPattern.NoScroll, percent);
        await Task.Delay(100);
        try
        {
            var after = scroll.Current;
            output.WriteLine($"Timeline scroll {percent}%: before={before}%, after={after.VerticalScrollPercent}%, view={after.VerticalViewSize}%.");
        }
        catch (ElementNotAvailableException)
        {
            output.WriteLine($"Timeline scroll {percent}%: before={before}%; post-scroll metrics unavailable because the UIA provider was removed.");
        }
    }

    private static async Task<AutomationElement> WaitForElementByIdAsync(GatewayFixtureRun run, string id, string description)
    {
        AutomationElement? element = null;
        // Keep the successful poll's element instead of racing a second tree lookup.
        await WaitUiAsync(run, () => (element = FindById(run, id)) is not null, description);
        return element!;
    }

    private static async Task WaitUiAsync(GatewayFixtureRun run, Func<bool> condition, string description)
    {
        await run.WaitForAsync(() =>
        {
            try { return Task.FromResult(condition()); }
            catch (ElementNotAvailableException) { return Task.FromResult(false); }
        }, description);
    }

    private static void ScrollIntoView(GatewayFixtureRun run, string automationId)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                var element = FindById(run, automationId);
                if (element is null)
                {
                    Thread.Sleep(100);
                    continue;
                }
                if (IsVisibleInHub(run, element))
                    return;
                var hasScrollItem = element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var itemPattern);
                var scrollAncestor = FindScrollAncestor(element);
                RequireScrollMechanism(targetVisible: false, hasScrollItem, scrollAncestor is not null, automationId);
                if (hasScrollItem)
                    ((ScrollItemPattern)itemPattern).ScrollIntoView();
                else
                {
                    var scroll = (ScrollPattern)scrollAncestor!.GetCurrentPattern(ScrollPattern.Pattern);
                    var before = scroll.Current.VerticalScrollPercent;
                    if (!scroll.Current.VerticallyScrollable)
                        throw new InvalidOperationException($"{automationId} scroll ancestor is not vertically scrollable.");
                    var targetBounds = element.Current.BoundingRectangle;
                    var hubBounds = SettingsWindow(run).Current.BoundingRectangle;
                    var amount = targetBounds.IsEmpty || targetBounds.Top >= hubBounds.Bottom
                        ? ScrollAmount.LargeIncrement
                        : ScrollAmount.LargeDecrement;
                    scroll.Scroll(ScrollAmount.NoAmount, amount);
                    Thread.Sleep(150);
                    var after = ((ScrollPattern)scrollAncestor.GetCurrentPattern(ScrollPattern.Pattern))
                        .Current.VerticalScrollPercent;
                    element = FindById(run, automationId);
                    RequireScrollProgress(element is not null && IsVisibleInHub(run, element), before, after, automationId);
                }
                Thread.Sleep(150);
                element = FindById(run, automationId);
                if (element is not null && IsVisibleInHub(run, element))
                    return;
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or NullReferenceException)
            {
                Thread.Sleep(100);
            }
        }
        throw new InvalidOperationException($"Could not reacquire {automationId} for scrolling.");
    }

    private static AutomationElement? FindScrollAncestor(AutomationElement element)
    {
        var walker = TreeWalker.ControlViewWalker;
        for (var current = walker.GetParent(element); current is not null; current = walker.GetParent(current))
            if (current.TryGetCurrentPattern(ScrollPattern.Pattern, out _))
                return current;
        return null;
    }

    private static void RequireScrollMechanism(
        bool targetVisible,
        bool hasScrollItemPattern,
        bool hasScrollAncestor,
        string automationId)
    {
        if (!targetVisible && !hasScrollItemPattern && !hasScrollAncestor)
            throw new InvalidOperationException(
                $"{automationId} is offscreen and has no UIA scrolling mechanism.");
    }

    private static void RequireScrollProgress(bool targetVisible, double before, double after, string automationId)
    {
        if (!targetVisible && Math.Abs(after - before) < 0.01)
            throw new InvalidOperationException(
                $"{automationId} ScrollPattern did not move ({before:0.##} -> {after:0.##}).");
    }

    private static bool IsVisibleInHub(GatewayFixtureRun run, AutomationElement element)
    {
        var bounds = element.Current.BoundingRectangle;
        var hub = SettingsWindow(run).Current.BoundingRectangle;
        return !element.Current.IsOffscreen && !bounds.IsEmpty
            && bounds.Left >= hub.Left - 1 && bounds.Right <= hub.Right + 1
            && bounds.Top >= hub.Top - 1 && bounds.Bottom <= hub.Bottom + 1;
    }

    private static void AssertVisibleInHub(GatewayFixtureRun run, AutomationElement element, string state)
    {
        if (!IsVisibleInHub(run, element))
            throw new InvalidOperationException(
                $"{state} target {element.Current.AutomationId} is not fully visible in the owned Settings window.");
    }

    private static void CaptureSessionsWindow(GatewayFixtureRun run, string name)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") != "1")
            return;
        OwnedWindowCapture.Save(run, SettingsWindow(run), name);
    }

    private static AutomationElement SettingsWindow(GatewayFixtureRun run) =>
        AppWindows(run).Single(candidate =>
            candidate.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SessionsPageMarker")) is not null);

    private static int ChatWindowCount(GatewayFixtureRun run) =>
        AppWindows(run).Count(window =>
            window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspaceTitleBar")) is not null);

    private static void CaptureSessionsWidths(GatewayFixtureRun run)
    {
        try
        {
            var window = SettingsWindow(run);
            var handle = new IntPtr(window.Current.NativeWindowHandle);
            var hostMarker = FindById(run, "SessionsHubHost")
                ?? throw new InvalidOperationException("SessionsHubHost is not discoverable before resize.");
            if (hostMarker.Current.BoundingRectangle.IsEmpty)
                throw new InvalidOperationException("SessionsHubHost has no readable bounds before resize.");

            var metrics = new List<WidthCaptureResult>();
            foreach (var target in new[]
                     {
                         (Name: "1400", Host: 1386d, InitialWindow: 1661d, Tier: "Wide"),
                         (Name: "1000", Host: 1007d, InitialWindow: 1280d, Tier: "Wide"),
                         (Name: "760", Host: 754d, InitialWindow: 1021.14d, Tier: "Medium"),
                     })
            {
                var sizing = ResizeSessionsWindow(run, target.InitialWindow, target.Host, target.Tier);
                metrics.Add(CaptureWidth(run, $"sessions-width-{target.Name}", target.Host, target.Tier,
                    "host target", sizing));
            }

            var scale = GetDpiForWindow(handle) / 96d;
            Assert.True(GetWindowRect(handle, out var bounds));
            var shellWidthPx = (int)Math.Round(1000 * scale);
            Assert.True(SetWindowPos(handle, IntPtr.Zero, 0, bounds.Top, shellWidthPx, bounds.Height,
                SetWindowPosNoActivate | SetWindowPosNoZOrder));
            Thread.Sleep(400);
            var shellExpandedHost = WaitForStableHostWidth(run, requireStableNavWidth: true);
            var shellExpandedSizing = new WindowSizingResult(shellWidthPx, shellWidthPx, shellExpandedHost, 0);
            metrics.Add(CaptureWidth(run, "sessions-width-shell-min-expanded", null, "Medium",
                "1000 DIP shell minimum with pane expanded", shellExpandedSizing));
            var paneToggle = FindById(run, "SettingsTogglePane")!;
            AssertVisibleInHub(run, paneToggle, "Settings pane toggle");
            Invoke(paneToggle);
            var shellHost = WaitForStableHostWidth(run, requireStableNavWidth: true);
            var shellSizing = new WindowSizingResult(shellWidthPx, shellWidthPx, shellHost, 0);
            metrics.Add(CaptureWidth(run, "sessions-width-shell-min-collapsed", null, "Medium",
                "1000 DIP shell minimum with pane collapsed", shellSizing));

            Assert.True(metrics.Select(metric => metric.WindowWidthPx).Distinct().Count() >= 4);
            Assert.Equal(metrics.Count, metrics.Select(metric => metric.Sha256).Distinct(StringComparer.Ordinal).Count());
            File.WriteAllText(Path.Combine(run.ArtifactsDirectory, "sessions-widths.json"),
                JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            DumpSessionsUia(run, "sessions-width-failure-uia.json");
            CaptureSessionsWindow(run, "sessions-width-failure.png");
            throw;
        }
    }

    private static void RestoreBaselineWindow(GatewayFixtureRun run)
    {
        Invoke(FindById(run, "SettingsTogglePane")!);
        Thread.Sleep(250);

        var window = SettingsWindow(run);
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        var scale = GetDpiForWindow(handle) / 96d;
        Assert.True(GetWindowRect(handle, out var bounds));
        Assert.True(SetWindowPos(handle, IntPtr.Zero, 0, bounds.Top,
            (int)Math.Round(1000 * scale), (int)Math.Round(650 * scale),
            SetWindowPosNoActivate | SetWindowPosNoZOrder));
        Thread.Sleep(400);
    }

    private static WindowSizingResult ResizeSessionsWindow(
        GatewayFixtureRun run,
        double initialWindowWidthDip,
        double desiredHostWidthDip,
        string expectedTier)
    {
        var handle = new IntPtr(SettingsWindow(run).Current.NativeWindowHandle);
        var scale = GetDpiForWindow(handle) / 96d;
        Assert.True(GetWindowRect(handle, out var before));
        var initialWidthPx = (int)Math.Round(initialWindowWidthDip * scale);
        var finalWidthPx = initialWidthPx;
        Assert.True(SetWindowPos(handle, IntPtr.Zero, 0, before.Top, finalWidthPx, before.Height,
            SetWindowPosNoActivate | SetWindowPosNoZOrder));
        double measuredHost = 0;
        var corrections = 0;
        for (; corrections <= 2; corrections++)
        {
            measuredHost = WaitForStableHostWidth(run);
            var measuredTier = measuredHost >= 1000 ? "Wide" : measuredHost >= 720 ? "Medium" : "Narrow";
            if (Math.Abs(measuredHost - desiredHostWidthDip) <= 2 && measuredTier == expectedTier)
                break;
            if (corrections == 2)
                throw new InvalidOperationException(
                    $"Could not reach {desiredHostWidthDip:0.##} DIP {expectedTier} host after two corrections; measured {measuredHost:0.##} DIP {measuredTier}.");
            Assert.True(GetWindowRect(handle, out var current));
            finalWidthPx = current.Width + (int)Math.Round((desiredHostWidthDip - measuredHost) * scale);
            Assert.True(SetWindowPos(handle, IntPtr.Zero, 0, current.Top, finalWidthPx, current.Height,
                SetWindowPosNoActivate | SetWindowPosNoZOrder));
        }
        Assert.True(GetWindowRect(handle, out var after));
        Assert.InRange(after.Width, finalWidthPx - 1, finalWidthPx + 1);
        Assert.NotEqual(before.Width, after.Width);
        return new WindowSizingResult(initialWidthPx, finalWidthPx, measuredHost, corrections);
    }

    private static double WaitForStableHostWidth(GatewayFixtureRun run, bool requireStableNavWidth = false)
    {
        double? previous = null;
        double? previousNav = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var handle = new IntPtr(SettingsWindow(run).Current.NativeWindowHandle);
            var scale = GetDpiForWindow(handle) / 96d;
            var marker = FindById(run, "SessionsHubHost")
                ?? throw new InvalidOperationException("SessionsHubHost disappeared after resize.");
            var current = marker.Current.BoundingRectangle.Width / scale;
            if (current <= 0)
                throw new InvalidOperationException("SessionsHubHost reported an empty width after resize.");
            var navWidth = FindById(run, "NavSessions")?.Current.BoundingRectangle.Width / scale ?? 0;
            if (previous is { } value && Math.Abs(value - current) <= 0.1
                && (!requireStableNavWidth || previousNav is { } nav && Math.Abs(nav - navWidth) <= 0.1))
                return current;
            previous = current;
            previousNav = navWidth;
            Thread.Sleep(300);
        }
        throw new InvalidOperationException("SessionsHubHost width did not stabilize after resize.");
    }

    private static WidthCaptureResult CaptureWidth(
        GatewayFixtureRun run,
        string captureName,
        double? requestedHostWidth,
        string expectedTier,
        string note,
        WindowSizingResult sizing)
    {
        var window = SettingsWindow(run);
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        var scale = GetDpiForWindow(handle) / 96d;
        Assert.True(GetWindowRect(handle, out var bounds));
        var hostWidth = FindById(run, "SessionsHubHost")!.Current.BoundingRectangle.Width / scale;
        var contentWidth = Math.Min(Math.Max(0, hostWidth - 2 * (hostWidth < 720 ? 20 : 32)), 1200);
        var tier = hostWidth >= 1000 ? "Wide" : hostWidth >= 720 ? "Medium" : "Narrow";
        CaptureSessionsWindow(run, captureName + ".png");
        var imagePath = Path.Combine(run.ArtifactsDirectory, captureName + ".png");
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(imagePath)));
        var result = new WidthCaptureResult(
            requestedHostWidth,
            Math.Round(hostWidth, 2),
            Math.Round(contentWidth, 2),
            tier,
            bounds.Width,
            bounds.Height,
            Math.Round(bounds.Width / scale, 2),
            Math.Round(bounds.Height / scale, 2),
            $"0x{handle.ToInt64():X}",
            sizing.InitialWindowWidthPx,
            sizing.FinalWindowWidthPx,
            sizing.Corrections,
            sha,
            note);
        File.WriteAllText(Path.Combine(run.ArtifactsDirectory, captureName + "-metrics.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(expectedTier, tier);
        return result;
    }

    private static async Task CaptureStateAsync(
        GatewayFixtureRun run,
        List<CaptureStateResult> manifest,
        string state,
        Func<Task> capture)
    {
        try
        {
            await RestoreSessionsBaselineAsync(run);
            await capture();
            manifest.Add(new CaptureStateResult(state, "captured", null));
        }
        catch (Exception exception)
        {
            manifest.Add(new CaptureStateResult(state, "failed", exception.ToString()));
            DumpSessionsUia(run, $"sessions-{state}-uia.json");
            CaptureSessionsWindow(run, $"sessions-{state}-failure.png");
        }
    }

    private static async Task RestoreSessionsBaselineAsync(GatewayFixtureRun run)
    {
        var window = SettingsWindow(run);
        SetForegroundWindow(new IntPtr(window.Current.NativeWindowHandle));
        System.Windows.Forms.SendKeys.SendWait("{ESC}{ESC}");
        await Task.Delay(100);

        if (FindById(run, "SessionsHubTranscriptClose") is { } transcriptClose)
        {
            AssertVisibleInHub(run, transcriptClose, "close transcript search");
            Invoke(transcriptClose);
        }
        if (FindById(run, "SessionsHubClearSelection") is { } clearSelection)
        {
            AssertVisibleInHub(run, clearSelection, "clear session selection");
            Invoke(clearSelection);
        }
        var worktreesTab = FindById(run, "SessionsHubTabWorktrees")
            ?? throw new InvalidOperationException("Worktrees tab is unavailable while restoring the baseline.");
        AssertVisibleInHub(run, worktreesTab, "Worktrees tab reset");
        if (!worktreesTab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var worktreesSelection))
            throw new InvalidOperationException("Worktrees tab has no SelectionItemPattern.");
        ((SelectionItemPattern)worktreesSelection).Select();
        await WaitUiAsync(run, () => FindById(run, "SessionsHubWorktree:" + GatewayScenario.HubBusyWorktreeId) is not null,
            "worktrees reset state");

        var sessionsTab = FindById(run, "SessionsHubTabSessions")
            ?? throw new InvalidOperationException("Sessions tab is unavailable while restoring the baseline.");
        AssertVisibleInHub(run, sessionsTab, "Sessions tab reset");
        if (!sessionsTab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sessionsSelection))
            throw new InvalidOperationException("Sessions tab has no SelectionItemPattern.");
        ((SelectionItemPattern)sessionsSelection).Select();
        await WaitUiAsync(run, () => FindById(run, "SessionsHubList") is not null
            && FindById(run, StoppedSessionRowId) is not null,
            "sessions baseline restored");
    }

    private const string StoppedSessionRowId = "SessionsHubRow:agent:main:hub-deploy-failure";

    private static async Task ExpandStoppedSessionRowAsync(GatewayFixtureRun run, string description)
    {
        await WaitUiAsync(run, () => FindById(run, StoppedSessionRowId) is not null, description);
        ScrollIntoView(run, StoppedSessionRowId);
        var row = FindById(run, StoppedSessionRowId)!;
        AssertVisibleInHub(run, row, description);
        Assert.Contains("Stopped with an error", row.Current.Name, StringComparison.Ordinal);
        if (!row.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
            throw new InvalidOperationException($"{description} has no ExpandCollapsePattern.");
        ((ExpandCollapsePattern)expand).Expand();
    }

    private static void DumpSessionsUia(GatewayFixtureRun run, string fileName = "sessions-uia.json")
    {
        var windows = AppWindows(run).ToArray();
        var rows = windows.SelectMany(window =>
            window.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                .Where(element => element.Current.AutomationId.StartsWith("Sessions", StringComparison.Ordinal))
                .Select(element => new
                {
                    window = window.Current.Name,
                    element.Current.Name,
                    element.Current.AutomationId,
                    controlType = element.Current.ControlType.ProgrammaticName,
                    element.Current.ClassName,
                    hasScrollPattern = element.TryGetCurrentPattern(ScrollPattern.Pattern, out _),
                    nearestScrollAncestor = FindScrollAncestor(element)?.Current.AutomationId,
                    element.Current.IsOffscreen,
                    bounds = element.Current.BoundingRectangle.ToString(),
                })).ToArray();
        var scrollCandidates = windows.SelectMany(window =>
            window.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true))
                .Cast<AutomationElement>()
                .Select(element => new
                {
                    window = window.Current.Name,
                    element.Current.Name,
                    element.Current.AutomationId,
                    controlType = element.Current.ControlType.ProgrammaticName,
                    element.Current.ClassName,
                    bounds = element.Current.BoundingRectangle.ToString(),
                })).ToArray();
        File.WriteAllText(Path.Combine(run.ArtifactsDirectory, fileName),
            JsonSerializer.Serialize(new
            {
                pageScrollFound = FindById(run, "SessionsHubPageScroll") is not null,
                sessionsElements = rows,
                scrollCandidates,
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record CaptureStateResult(string State, string Status, string? Exception);
    private sealed record WidthCaptureResult(
        double? RequestedHostWidthDip,
        double MeasuredHostWidthDip,
        double ContentWidthDip,
        string Tier,
        int WindowWidthPx,
        int WindowHeightPx,
        double WindowWidthDip,
        double WindowHeightDip,
        string HubHwnd,
        int InitialWindowWidthPx,
        int FinalWindowWidthPx,
        int Corrections,
        string Sha256,
        string Note);
    private sealed record WindowSizingResult(
        int InitialWindowWidthPx,
        int FinalWindowWidthPx,
        double MeasuredHostWidthDip,
        int Corrections);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    private const uint SetWindowPosNoZOrder = 0x0004;
    private const uint SetWindowPosNoMove = 0x0002;
    private const uint SetWindowPosNoActivate = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);

    private static Task CaptureIfRequestedAsync(GatewayFixtureRun run, string name)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") != "1")
            return Task.CompletedTask;
        OwnedWindowCapture.Save(run, AppWindows(run).OrderByDescending(window =>
            window.Current.BoundingRectangle.Width * window.Current.BoundingRectangle.Height).First(), name);
        return Task.CompletedTask;
    }

    private static async Task WaitForAuthenticatedOwnerPhotoAsync(GatewayFixtureRun run)
    {
        Assert.Contains(run.Gateway.Requests, request => request.Method == "users.self" && request.Outcome == "ok");
        // The photo arrives over plain HTTP after the name, so wait for the fixture to serve it before the test ends.
        await run.WaitForAsync(() => Task.FromResult(run.Gateway.Requests.Any(request =>
            request.Method == FixtureGatewayServer.AvatarRequestMethod && request.Outcome == "ok")),
            "authenticated owner photo served");
        // Native capture is strict and opt-in, so only screenshot runs also wait for the photo to be painted.
        if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") == "1")
            await WaitUiAsync(run, () => OwnerPhotoPainted(run), "owner photo painted in footer");
    }

    /// <summary>Captures the Workspace window and reports whether the footer button shows the served photo's colour.</summary>
    private static bool OwnerPhotoPainted(GatewayFixtureRun run, string ownerAutomationId = "WorkspaceOwner",
        string captureName = "owner-profile.png")
    {
        var ownerId = new PropertyCondition(AutomationElement.AutomationIdProperty, ownerAutomationId);
        foreach (var window in AppWindows(run))
        {
            if (window.FindFirst(TreeScope.Descendants, ownerId) is not { } owner || owner.Current.IsOffscreen)
                continue;
            OwnedWindowCapture.Save(run, window, captureName, owner);
            return CountOwnerPhotoPixels(Path.Combine(run.ArtifactsDirectory, captureName)) >= OwnerPhotoMinimumPixels;
        }
        return false;
    }

    /// <summary>Counts pixels inside the capture's asserted footer button that match the served photo's colour.</summary>
    private static int CountOwnerPhotoPixels(string capturePath)
    {
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(capturePath, ".json")));
        var button = metadata.RootElement.GetProperty("assertions")[0];
        using var bitmap = new System.Drawing.Bitmap(capturePath);
        var left = Math.Max(0, (int)button.GetProperty("left").GetDouble());
        var top = Math.Max(0, (int)button.GetProperty("top").GetDouble());
        var right = Math.Min(bitmap.Width, left + (int)button.GetProperty("Width").GetDouble());
        var bottom = Math.Min(bitmap.Height, top + (int)button.GetProperty("Height").GetDouble());
        var red = (FixtureGatewayServer.OwnerAvatarRgb >> 16) & 0xFF;
        var green = (FixtureGatewayServer.OwnerAvatarRgb >> 8) & 0xFF;
        var blue = FixtureGatewayServer.OwnerAvatarRgb & 0xFF;
        var painted = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (Math.Abs(pixel.R - red) <= OwnerPhotoChannelTolerance
                    && Math.Abs(pixel.G - green) <= OwnerPhotoChannelTolerance
                    && Math.Abs(pixel.B - blue) <= OwnerPhotoChannelTolerance)
                    painted++;
            }
        }
        return painted;
    }
}
