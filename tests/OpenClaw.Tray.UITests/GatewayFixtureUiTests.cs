using System.Runtime.InteropServices;
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
            await WaitUiAsync(run, () => FindById(run, "ChatComposerInput") is { } composer &&
                ((ValuePattern)composer.GetCurrentPattern(ValuePattern.Pattern)).Current.Value == draft &&
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

            // Removing an automation peer is not an acknowledgment of the remaining rows' arrange pass.
            // Keep two matching native samples, without another action or an extra timeout.
            (string Identity, System.Windows.Rect Home, System.Windows.Rect DefaultPin, System.Windows.Rect Pin,
                System.Windows.Rect Header, System.Windows.Rect Other, System.Windows.Rect Anchor)? previous = null;
            AutomationElement? readyHome = null;
            var samples = 0;
            double? firstGap = null, lastGap = null;
            static bool Usable(System.Windows.Rect rect) => !rect.IsEmpty && rect.Width > 0 && rect.Height > 0
                && double.IsFinite(rect.Left) && double.IsFinite(rect.Top)
                && double.IsFinite(rect.Right) && double.IsFinite(rect.Bottom);
            AutomationElement? InWorkspace(string id) => window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            try
            {
                await WaitUiAsync(run, () =>
                {
                    var prior = previous;
                    previous = null; // A missing/stale peer or invalid sample breaks consecutive readiness.
                    samples++;
                    if (!double.IsFinite(scale) || scale <= 0
                        || FindById(run, "WorkspacePagesToggle:sessions") is not null
                        || InWorkspace("WorkspacePinnedPage:sessions") is not null)
                        return false;
                    var homeCandidate = InWorkspace("WorkspaceNavHome");
                    var defaultPin = InWorkspace("WorkspacePinnedPage:dashboards");
                    var pin = InWorkspace("WorkspacePinnedPage:connection");
                    var anchor = InWorkspace("WorkspaceSessionsAdd");
                    var conversation = InWorkspace($"WorkspaceSession:{GatewayScenario.LongSessionKey}");
                    var connection = InWorkspace("ConnectionPageMarker");
                    if (homeCandidate is null || defaultPin is null || pin is null || anchor is null || conversation is null || connection is null)
                        return false;
                    AutomationElement[] owners = [window, homeCandidate, defaultPin, pin, anchor, conversation, connection];
                    if (owners.Any(element => element.Current.ProcessId != run.AppProcessId || element.Current.IsOffscreen)
                        || !pin.GetRuntimeId().SequenceEqual(connectionRow))
                        return false;
                    var homeRect = homeCandidate.Current.BoundingRectangle;
                    var defaultPinRect = defaultPin.Current.BoundingRectangle;
                    var pinRect = pin.Current.BoundingRectangle;
                    var anchorRect = anchor.Current.BoundingRectangle;
                    var otherRect = conversation.Current.BoundingRectangle;
                    if (!Usable(homeRect) || !Usable(defaultPinRect) || !Usable(pinRect) || !Usable(anchorRect) || !Usable(otherRect))
                        return false;

                    // The authored header has Sessions text beside its existing add button. Use that
                    // native row band, not the first Sessions substring or an assumed UIA parent chain.
                    AutomationElement? header = null;
                    var headerRect = System.Windows.Rect.Empty;
                    var labels = window.FindAll(TreeScope.Descendants, new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                        new PropertyCondition(AutomationElement.NameProperty, "Sessions"),
                        new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));
                    foreach (AutomationElement label in labels)
                    {
                        var bounds = label.Current.BoundingRectangle;
                        if (label.Current.ProcessId != run.AppProcessId || label.Current.IsOffscreen || !Usable(bounds)
                            || bounds.Top < anchorRect.Top || bounds.Bottom > anchorRect.Bottom
                            || bounds.Left < homeRect.Left || bounds.Right > anchorRect.Left)
                            continue;
                        if (header is not null) return false; // Ambiguous labels are not a section receipt.
                        header = label;
                        headerRect = bounds;
                    }
                    if (header is null) return false;
                    var homeGap = (defaultPinRect.Top - homeRect.Bottom) / scale;
                    var pinGap = (pinRect.Top - defaultPinRect.Bottom) / scale;
                    var headerGap = (headerRect.Top - pinRect.Bottom) / scale;
                    if (!double.IsFinite(homeGap) || !double.IsFinite(pinGap) || !double.IsFinite(headerGap)) return false;
                    firstGap ??= headerGap;
                    lastGap = headerGap;
                    if (homeGap is < -1 or > 8 || pinGap is < -1 or > 8 || headerGap is < 4 or > 48 || otherRect.Top < headerRect.Bottom)
                        return false;
                    // Runtime IDs are numeric, role-delimited and private to this observer; no UI text is retained.
                    var identities = owners.Append(header).Select(element => element.GetRuntimeId()).ToArray();
                    if (identities.Any(id => id is null || id.Length == 0)) return false;
                    var identity = string.Join("|", identities.Select(id => string.Join(",", id)));
                    var sample = (identity, homeRect, defaultPinRect, pinRect, headerRect, otherRect, anchorRect);
                    previous = sample;
                    if (prior is null || prior.Value != sample) return false;
                    readyHome = homeCandidate;
                    return true;
                }, "stable post-unpin sidebar geometry");
            }
            finally
            {
                try
                {
                    output.WriteLine("Sidebar post-unpin geometry: " + JsonSerializer.Serialize(
                        new { samples, firstGap, lastGap, stable = readyHome is not null }));
                }
                catch (Exception) { /* Best-effort numeric diagnostics must not replace the test failure. */ }
            }
            var settled = previous!.Value;
            var home = readyHome!;
            var homeBounds = settled.Home;
            var defaultPinBounds = settled.DefaultPin;
            var pinBounds = settled.Pin;
            var sessionsHeader = settled.Header;
            var other = settled.Other;
            Assert.InRange((defaultPinBounds.Top - homeBounds.Bottom) / scale, -1, 8);
            Assert.InRange((pinBounds.Top - defaultPinBounds.Bottom) / scale, -1, 8);
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

            // Any Gateway-dependent catalog page that is not pinned above proves the offline picker.
            const string offlinePinTag = "usage";
            Invoke(FindById(run, "WorkspacePagesCustomize")!);
            await WaitUiAsync(run, () => FindById(run, $"WorkspacePagesToggle:{offlinePinTag}") is not null, "offline picker");
            foreach (var tag in identities.Keys)
            {
                var toggle = FindById(run, $"WorkspacePagesToggle:{tag}")!;
                Assert.True(toggle.Current.IsEnabled);
                Assert.Equal(ToggleState.On, ((TogglePattern)toggle.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState);
            }
            Invoke(FindById(run, $"WorkspacePagesToggle:{offlinePinTag}")!);
            await WaitUiAsync(run, () => FindById(run, $"WorkspacePinnedPage:{offlinePinTag}") is { Current.IsEnabled: false },
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
            Assert.True(FindById(run, $"WorkspacePinnedPage:{offlinePinTag}")!.Current.IsEnabled);
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
    public async Task PinnedSystems_ToggleHitTabAndFeedbackRemainOnVisibleSurface()
    {
        foreach (var theme in new[] { "Light", "Dark" })
            await WithAppAsync(async run =>
            {
                if (Environment.GetEnvironmentVariable("OPENCLAW_RUN_KEYBOARD_PROOF") != "1")
                    throw new InvalidOperationException("Pinned Systems requires explicit native keyboard proof opt-in.");
                await run.InvokeAsync("app.navigate", new { page = "chat" });
                await WaitUiAsync(run, () => FindById(run, "WorkspacePagesCustomize") is { Current.IsOffscreen: false },
                    "Systems pin customization");
                var window = FindHub(run);
                var hwnd = new IntPtr(window.Current.NativeWindowHandle);
                var scale = SystemsWindowDpi(hwnd) / 96d;
                var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                    Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd),
                    Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
                ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Normal);
                Assert.True(1000 * scale <= work.Width, "The display must contain the exact 1000-DIP pinned proof window.");
                Assert.True(SystemsSetWindowPos(hwnd, IntPtr.Zero, work.X, work.Y,
                    (int)Math.Round(1000 * scale), Math.Min(work.Height, (int)Math.Round(900 * scale)), 0x0014));
                await WaitUiAsync(run, () => Math.Abs(window.Current.BoundingRectangle.Width / scale - 1000) <= 2,
                    "normal 1000-DIP pinned Systems window");
                await NativeKeyboardProof.ActivateAsync(hwnd);
                bool IsWithin(AutomationElement element, AutomationElement ancestor)
                {
                    for (AutomationElement? current = element; current is not null;
                         current = TreeWalker.RawViewWalker.GetParent(current))
                        if (current.GetRuntimeId().SequenceEqual(ancestor.GetRuntimeId()))
                            return true;
                    return false;
                }
                var hitSequence = 0;
                AutomationElement AssertToggleHit(string id)
                {
                    var toggle = FindById(run, id) ?? throw new InvalidOperationException($"Missing {id}.");
                    var bounds = toggle.Current.BoundingRectangle;
                    var point = new System.Windows.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
                    var ancestors = new List<object>();
                    var receiptPath = Path.Combine(run.ArtifactsDirectory, $"toggle-hit-{++hitSequence:D2}.json");
                    var target = new { runtimeId = toggle.GetRuntimeId(), toggle.Current.ProcessId,
                        toggle.Current.ClassName, controlType = toggle.Current.ControlType.ProgrammaticName,
                        toggle.Current.AutomationId, bounds };
                    var windowBounds = window.Current.BoundingRectangle;
                    var windowState = ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Current.WindowVisualState.ToString();
                    string Receipt(string stage) => JsonSerializer.Serialize(new { id, theme, run.AppProcessId, stage,
                        hwnd = hwnd.ToInt64(), point, windowBounds,
                        windowState, target, hitAncestors = ancestors });
                    File.WriteAllText(receiptPath, Receipt("before-point-query"));
                    var hit = AutomationElement.FromPoint(point);
                    File.WriteAllText(receiptPath, Receipt("point-query-returned"));
                    for (AutomationElement? current = hit; current is not null;
                         current = TreeWalker.RawViewWalker.GetParent(current))
                    {
                        ancestors.Add(new { runtimeId = current.GetRuntimeId(), current.Current.ProcessId,
                            current.Current.ClassName, controlType = current.Current.ControlType.ProgrammaticName,
                            current.Current.AutomationId, bounds = current.Current.BoundingRectangle });
                        File.WriteAllText(receiptPath, Receipt("reading-hit-ancestry"));
                    }
                    var receipt = Receipt("hit-ancestry-complete");
                    File.WriteAllText(receiptPath, receipt);
                    output.WriteLine(receipt);
                    Assert.False(toggle.Current.IsOffscreen, receipt);
                    Assert.True(toggle.Current.IsEnabled, receipt);
                    Assert.True(bounds.Width > 0 && bounds.Height > 0, receipt);
                    Assert.True(IsWithin(hit, toggle), $"Pinned {id} center ownership failed. {receipt}");
                    return toggle;
                }
                Invoke(FindById(run, "WorkspacePagesCustomize")!);
                // The pin catalog keeps the stored "instances" tag for existing pins; routing normalizes it to Systems.
                await WaitUiAsync(run, () => FindById(run, "WorkspacePagesToggle:instances") is not null, "Systems pin toggle");
                Invoke(FindById(run, "WorkspacePagesToggle:instances")!);
                await WaitUiAsync(run, () => FindById(run, "WorkspacePinnedPage:instances") is { Current.IsOffscreen: false },
                    "Systems production pinned route");
                Invoke(FindById(run, "WorkspacePinnedPage:instances")!);
                await WaitUiAsync(run, () => FindById(run, "SystemsPageMarker") is { Current.IsOffscreen: false } &&
                    FindById(run, "SystemsRow_gateway") is not null, "populated embedded Systems");
                // Panels such as WorkspaceSettingsHost and SystemsPageRoot have no UIA peer. The embedded host
                // shares the Workspace navigation row without a top margin for inset pages, so that control anchors it.
                var openHostTop = FindById(run, "WorkspaceNavigation")!.Current.BoundingRectangle.Top;
                Invoke(AssertToggleHit("WorkspaceTogglePane"));
                await WaitUiAsync(run, () => FindById(run, "WorkspaceReopenPane") is { Current.IsOffscreen: false } &&
                    FindById(run, "SystemsPageScroll") is { Current.BoundingRectangle.Height: > 0 }, "collapsed Systems host");
                await WaitUiAsync(run, () =>
                    Math.Abs((FindById(run, "WorkspaceNavigation")!.Current.BoundingRectangle.Top - openHostTop) / scale) <= 1 &&
                    Math.Abs((FindById(run, "SystemsPageScroll")!.Current.BoundingRectangle.Top -
                        FindById(run, "WorkspaceNavigation")!.Current.BoundingRectangle.Top) / scale - 56) <= 1,
                    "collapsed pinned Systems host and viewport allocations settle");
                var reopen = AssertToggleHit("WorkspaceReopenPane");
                var host = FindById(run, "WorkspaceNavigation")!.Current.BoundingRectangle;
                var viewport = FindById(run, "SystemsPageScroll")!.Current.BoundingRectangle;
                Assert.InRange(Math.Abs((host.Top - openHostTop) / scale), 0, 1);
                Assert.InRange(Math.Abs((viewport.Top - host.Top) / scale - 56), 0, 1);
                foreach (var id in new[] { "SettingsBack", "SettingsForward" })
                    Assert.True(reopen.Current.BoundingRectangle.Right <= FindById(run, id)!.Current.BoundingRectangle.Left,
                        $"Pinned reopen overlaps {id}.");
                reopen.SetFocus();
                await WaitUiAsync(run, () => reopen.Current.HasKeyboardFocus, "pinned Systems toggle focus");
                foreach (var id in new[] { "SettingsBack", "SettingsForward" })
                {
                    var history = FindById(run, id)!;
                    if (!history.Current.IsEnabled)
                        continue;
                    NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Tab);
                    await WaitUiAsync(run, () => history.Current.HasKeyboardFocus, $"pinned Systems native Tab to {id}");
                }
                NativeKeyboardProof.Press(hwnd, Windows.System.VirtualKey.Tab);
                await WaitUiAsync(run, () => IsWithin(AutomationElement.FocusedElement,
                    FindById(run, "SystemsPageScroll")!), "pinned history native Tab to Systems content");
                OwnedWindowCapture.Save(run, window, $"{theme}-pinned-systems-collapsed.png",
                    AssertToggleHit("WorkspaceReopenPane"), FindById(run, "SystemsPageMarker")!);
                Invoke(AssertToggleHit("WorkspaceReopenPane"));
                await WaitUiAsync(run, () => FindById(run, "WorkspaceSessionsAdd") is { Current.IsOffscreen: false } &&
                    FindById(run, "WorkspaceTogglePane") is { Current.IsOffscreen: false }, "reopened Systems host");
                await WaitUiAsync(run, () => Math.Abs((FindById(run, "SystemsPageScroll")!.Current.BoundingRectangle.Top -
                    FindById(run, "WorkspaceNavigation")!.Current.BoundingRectangle.Top) / scale) <= 1,
                    "reopened pinned Systems removes viewport inset");
                host = FindById(run, "WorkspaceNavigation")!.Current.BoundingRectangle;
                viewport = FindById(run, "SystemsPageScroll")!.Current.BoundingRectangle;
                Assert.InRange(Math.Abs((viewport.Top - host.Top) / scale), 0, 1);
                Invoke(FindById(run, "WorkspaceSessionsAdd")!);
                await WaitUiAsync(run, () => FindById(run, "WorkspaceEmbeddedOperationFeedback") is { Current.IsOffscreen: false },
                    "explicit rejected operation on pinned Systems");
                Assert.Contains("Fixture Gateway is read-only", FindText(run, "Fixture Gateway is read-only")!.Current.Name);
                Invoke(AssertToggleHit("WorkspaceTogglePane"));
                await WaitUiAsync(run, () => FindById(run, "WorkspaceReopenPane") is { Current.IsOffscreen: false } &&
                    FindById(run, "WorkspaceEmbeddedOperationFeedback") is { Current.IsOffscreen: false },
                    "collapsed pinned Systems keeps operation feedback");
                await WaitUiAsync(run, () =>
                    FindById(run, "WorkspaceEmbeddedOperationFeedback") is { Current.IsOffscreen: false } info &&
                    new[] { "WorkspaceReopenPane", "SettingsBack", "SettingsForward" }.All(id =>
                        FindById(run, id)!.Current.BoundingRectangle.Bottom <= info.Current.BoundingRectangle.Top),
                    "collapsed operation feedback settles below toggle and history");
                var feedback = FindById(run, "WorkspaceEmbeddedOperationFeedback")!;
                Assert.True(AssertToggleHit("WorkspaceReopenPane").Current.BoundingRectangle.Bottom <= feedback.Current.BoundingRectangle.Top);
                foreach (var id in new[] { "SettingsBack", "SettingsForward" })
                    Assert.True(FindById(run, id)!.Current.BoundingRectangle.Bottom <= feedback.Current.BoundingRectangle.Top);
                OwnedWindowCapture.Save(run, window, $"{theme}-pinned-systems-feedback.png",
                    AssertToggleHit("WorkspaceReopenPane"), feedback);
                Invoke(AssertToggleHit("WorkspaceReopenPane"));
                await WaitUiAsync(run, () => FindById(run, "WorkspaceTogglePane") is { Current.IsOffscreen: false },
                    "pinned Systems feedback permits reopening");
                await WaitUiAsync(run, () =>
                    FindById(run, "WorkspaceEmbeddedOperationFeedback") is { Current.IsOffscreen: false } info &&
                    FindById(run, "WorkspaceTogglePane") is { Current.IsOffscreen: false } toggle &&
                    (toggle.Current.BoundingRectangle.Right <= info.Current.BoundingRectangle.Left ||
                     toggle.Current.BoundingRectangle.Bottom <= info.Current.BoundingRectangle.Top),
                    "reopened operation feedback remains visible without covering toggle");
                feedback = FindById(run, "WorkspaceEmbeddedOperationFeedback")!;
                Assert.False(feedback.Current.IsOffscreen);
                var finalToggle = AssertToggleHit("WorkspaceTogglePane");
                Assert.True(finalToggle.Current.BoundingRectangle.Right <= feedback.Current.BoundingRectangle.Left ||
                    finalToggle.Current.BoundingRectangle.Bottom <= feedback.Current.BoundingRectangle.Top);
                OwnedWindowCapture.Save(run, window, $"{theme}-pinned-systems-reopened.png",
                    finalToggle, FindById(run, "SystemsPageMarker")!, feedback);
            }, scenario: GatewayScenario.CreateSystems(empty: false), appTheme: theme);
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
    public async Task SettingsPane_KeepsToggleChoiceAcrossPinnedPagesAndRestart()
    {
        await WithAppAsync(async run =>
        {
            var app = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_APP")!;
            const int CollapsedPaneStablePolls = 5;
            // Search sits in the section-list toolbar and shows only while the list is expanded.
            bool ListExpanded() => FindById(run, "SettingsSearch") is { Current.IsOffscreen: false };
            async Task ShowFullSettingsAsync(string description)
            {
                await run.InvokeAsync("app.navigate", new { page = "settings" });
                await WaitUiAsync(run, () => FindById(run, "SettingsPageMarker")?.Current.IsOffscreen == false
                    && FindById(run, "SettingsTogglePane") is { Current.IsOffscreen: false }, description);
            }
            async Task WaitForCollapsedSettingsAsync(string description)
            {
                var stablePolls = 0;
                await WaitUiAsync(run, () =>
                {
                    if (FindById(run, "SettingsTogglePane") is not { Current.IsOffscreen: false } || ListExpanded())
                    {
                        stablePolls = 0;
                        return false;
                    }

                    return ++stablePolls >= CollapsedPaneStablePolls;
                }, description);
            }
            async Task ShowPinnedConnectionAsync()
            {
                Invoke(await WaitForElementByIdAsync(run, "WorkspacePinnedPage:connection", "Connection pin"));
                await WaitUiAsync(run, () => FindById(run, "ConnectionPageMarker")?.Current.IsOffscreen == false, "embedded Connection");
            }

            await ShowFullSettingsAsync("Settings");
            await WaitUiAsync(run, ListExpanded, "section list expanded by default");
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            Invoke(await WaitForElementByIdAsync(run, "WorkspacePagesCustomize", "pin customization"));
            Invoke(await WaitForElementByIdAsync(run, "WorkspacePagesToggle:connection", "Connection pin toggle"));
            // A pinned page hides the section list. Exit while one is showing.
            await ShowPinnedConnectionAsync();
            await run.RestartAppAsync(app);

            await ShowFullSettingsAsync("Settings after restart");
            await WaitUiAsync(run, ListExpanded, "section list still expanded after exiting from a pinned page");
            Invoke(FindById(run, "SettingsTogglePane")!);
            await WaitUiAsync(run, () => !ListExpanded(), "toggle collapses the section list");
            await run.WaitForAsync(() =>
            {
                using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(run.Profile.DataDirectory, "settings.json")));
                return Task.FromResult(!settings.RootElement.GetProperty("HubNavPaneOpen").GetBoolean());
            }, "toggle choice persisted");

            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await ShowPinnedConnectionAsync();
            await ShowFullSettingsAsync("Settings after the pinned page");
            await WaitForCollapsedSettingsAsync("section list stays collapsed after returning from a pinned page");

            await run.RestartAppAsync(app);
            await ShowFullSettingsAsync("Settings after restarting with the list collapsed");
            await WaitForCollapsedSettingsAsync("section list stays collapsed after restarting");
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
    public async Task HistoricalFractionalVoiceConversationRendersWithoutLoadingPlaceholder()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            var observation = new VoiceTailObservation();
            await SelectSessionAsync(run, GatewayScenario.VoiceTitle, GatewayScenario.VoiceSessionKey,
                observeHistory: observation.ObserveHistory);
            try
            {
                await WaitUiAsync(run, () =>
                {
                    var native = observation.BeginPoll();
                    // Keep the original short circuit and native reads. Unknown visibility
                    // means it was not evaluated, not that the final row was offscreen.
                    try
                    {
                        return RenderConsumedHistory(run, GatewayScenario.VoiceSessionKey, native)
                            && IsVisibleInTimeline(run, GatewayScenario.VoiceFinalSentinel, native);
                    }
                    finally { native.CompletedAtUtc = DateTimeOffset.UtcNow; }
                }, "visible historical fractional voice reply");
                observation.WaitSucceeded = true;
            }
            finally
            {
                // Cached values only: no new UIA or MCP calls on the failure path.
                await observation.WriteAsync(run.ArtifactsDirectory);
            }
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
            await WaitUiAsync(run, () => FindById(run, "SessionsPageArchived") is not null, "Settings archive section");
            var section = FindById(run, "SessionsPageArchived")!;
            ScrollAncestorToBottom(run, section);
            ((ExpandCollapsePattern)section.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            var restoreId = $"SessionsPageUnarchive:{GatewayScenario.OtherSessionKey}";
            await WaitUiAsync(run, () => FindById(run, restoreId) is not null, "archived conversation in Settings");
            ScrollAncestorToBottom(run, FindById(run, restoreId)!);
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

    private static void ScrollAncestorToBottom(GatewayFixtureRun run, AutomationElement element)
    {
        object Describe(AutomationElement node) => new
        {
            runtimeId = node.GetRuntimeId(),
            automationId = node.Current.AutomationId,
            className = node.Current.ClassName,
            controlType = node.Current.ControlType.ProgrammaticName,
            bounds = DescribeScrollBounds(node.Current.BoundingRectangle),
            isOffscreen = node.Current.IsOffscreen
        };
        if (element.Current.ProcessId != run.AppProcessId)
            throw new InvalidOperationException("Scroll target is not owned by the fixture app.");
        var targetBefore = Describe(element);
        var ancestors = new List<object>();
        var receiptPath = Path.Combine(run.ArtifactsDirectory, $"scroll-to-bottom-{Guid.NewGuid():N}.json");
        for (AutomationElement? parent = element; parent is not null; parent = TreeWalker.ControlViewWalker.GetParent(parent))
        {
            var hasPattern = parent.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern);
            var scroll = hasPattern ? (ScrollPattern)pattern : null;
            ancestors.Add(new
            {
                element = Describe(parent),
                pattern = hasPattern ? ScrollPattern.Pattern.ProgrammaticName : null,
                verticallyScrollable = scroll?.Current.VerticallyScrollable,
                verticalScrollPercent = scroll?.Current.VerticalScrollPercent
            });
            if (parent.Current.ProcessId != run.AppProcessId)
                break;
            if (scroll is not null && !scroll.Current.VerticallyScrollable &&
                VisibleTargetFitsViewport(element.Current.BoundingRectangle, parent.Current.BoundingRectangle,
                    element.Current.IsOffscreen || parent.Current.IsOffscreen))
            {
                // A page that already fits has a real viewport but cannot scroll.
                // Accept only measured containment, never absence of ScrollPattern.
                File.WriteAllText(receiptPath, JsonSerializer.Serialize(new
                {
                    run.ArtifactsDirectory, run.AppProcessId, targetBefore,
                    targetAfter = Describe(element), viewport = Describe(parent),
                    outcome = "Already visible in non-scrollable viewport",
                    targetVisibleContainment = true, ancestors
                }));
                return;
            }
            if (scroll is not null && scroll.Current.VerticallyScrollable)
            {
                var selectedBefore = Describe(parent);
                var before = scroll.Current.VerticalScrollPercent;
                Exception? scrollFailure = null;
                try
                {
                    scroll.SetScrollPercent(ScrollPattern.NoScroll, 100);
                }
                catch (Exception error)
                {
                    scrollFailure = error;
                    throw;
                }
                finally
                {
                    try
                    {
                        var viewport = parent.Current.BoundingRectangle;
                        var target = element.Current.BoundingRectangle;
                        File.WriteAllText(receiptPath, JsonSerializer.Serialize(new
                        {
                            run.ArtifactsDirectory,
                            run.AppProcessId,
                            targetBefore,
                            targetAfter = Describe(element),
                            selectedBefore,
                            selectedAfter = Describe(parent),
                            pattern = ScrollPattern.Pattern.ProgrammaticName,
                            before,
                            after = scroll.Current.VerticalScrollPercent,
                            targetVisibleContainment = !element.Current.IsOffscreen && !target.IsEmpty &&
                                !viewport.IsEmpty && viewport.Contains(target),
                            ancestors
                        }));
                    }
                    catch (Exception observationError) when (scrollFailure is not null)
                    {
                        throw new AggregateException(scrollFailure, observationError);
                    }
                }
                return;
            }
        }
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(new
        {
            run.ArtifactsDirectory,
            run.AppProcessId,
            targetBefore,
            outcome = "No vertically scrollable ControlView ancestor",
            ancestors
        }));
        throw new InvalidOperationException($"No vertically scrollable ancestor for {element.Current.AutomationId}. Diagnostics: {receiptPath}");
    }

    private static object DescribeScrollBounds(System.Windows.Rect bounds)
    {
        static double? Finite(double value) => double.IsFinite(value) ? value : null;
        return new
        {
            invalidBounds = bounds.IsEmpty || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
                !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height),
            x = Finite(bounds.X), y = Finite(bounds.Y),
            width = Finite(bounds.Width), height = Finite(bounds.Height)
        };
    }

    [Fact]
    public void ScrollObservation_EmptyBoundsRemainExplicitlyInvalidAndSerializable()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(DescribeScrollBounds(System.Windows.Rect.Empty)));
        Assert.True(json.RootElement.GetProperty("invalidBounds").GetBoolean());
        foreach (var coordinate in new[] { "x", "y", "width", "height" })
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty(coordinate).ValueKind);
    }

    [Fact]
    public void ScrollObservation_FiniteBoundsRetainNegativeCoordinatesAndExactDimensions()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            DescribeScrollBounds(new System.Windows.Rect(-10, 20, 30, 40))));
        Assert.False(json.RootElement.GetProperty("invalidBounds").GetBoolean());
        Assert.Equal(-10, json.RootElement.GetProperty("x").GetDouble());
        Assert.Equal(20, json.RootElement.GetProperty("y").GetDouble());
        Assert.Equal(30, json.RootElement.GetProperty("width").GetDouble());
        Assert.Equal(40, json.RootElement.GetProperty("height").GetDouble());
    }

    private static bool VisibleTargetFitsViewport(System.Windows.Rect target, System.Windows.Rect viewport, bool offscreen) =>
        !offscreen && !target.IsEmpty && !viewport.IsEmpty &&
        target.Width > 0 && target.Height > 0 && viewport.Width > 0 && viewport.Height > 0 &&
        double.IsFinite(target.Left) && double.IsFinite(target.Top) &&
        double.IsFinite(target.Right) && double.IsFinite(target.Bottom) &&
        double.IsFinite(viewport.Left) && double.IsFinite(viewport.Top) &&
        double.IsFinite(viewport.Right) && double.IsFinite(viewport.Bottom) && viewport.Contains(target);

    [Theory]
    [InlineData(10, 10, 20, 20, false, true)]
    [InlineData(0, 0, 100, 100, false, true)]
    [InlineData(10, 10, 20, 20, true, false)]
    [InlineData(90, 10, 20, 20, false, false)]
    [InlineData(10, 90, 20, 20, false, false)]
    [InlineData(10, 10, 0, 20, false, false)]
    [InlineData(double.NaN, 10, 20, 20, false, false)]
    public void NonScrollableViewportAdmission_RequiresVisiblePositiveContainedBounds(
        double x, double y, double width, double height, bool offscreen, bool expected) =>
        Assert.Equal(expected, VisibleTargetFitsViewport(new System.Windows.Rect(x, y, width, height),
            new System.Windows.Rect(0, 0, 100, 100), offscreen));

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

    private void ClickOwnedElement(GatewayFixtureRun run, AutomationElement window, AutomationElement target)
    {
        run.EnsureRunning();
        Assert.Equal(run.AppProcessId, window.Current.ProcessId);
        Assert.Equal(run.AppProcessId, target.Current.ProcessId);
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        Assert.True(SetForegroundWindow(handle), "Cannot foreground owned fixture window.");
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        Assert.NotEqual(IntPtr.Zero, previous);
        try
        {
            ConfirmForegroundAfterRequest(handle);
            AssertOwnedForeground(run, handle, "before-target-hit-test");
            var bounds = target.Current.BoundingRectangle;
            Assert.True(bounds.Width > 0 && bounds.Height > 0 && !target.Current.IsOffscreen);
            var point = new FixturePoint { X = (int)(bounds.Left + bounds.Width / 2), Y = (int)(bounds.Top + bounds.Height / 2) };
            Assert.Equal(handle, GetAncestor(WindowFromPoint(point), 2));
            var hit = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
            // WinUI can return its content bridge instead of the target peer for point queries.
            var bridgeContainsTarget = hit.Current.ClassName == "Microsoft.UI.Content.DesktopChildSiteBridge"
                && hit.Current.ProcessId == run.AppProcessId
                && hit.FindFirst(TreeScope.Descendants, new PropertyCondition(
                    AutomationElement.AutomationIdProperty, target.Current.AutomationId)) is not null;
            Assert.True(target.Current.AutomationId == hit.Current.AutomationId || bridgeContainsTarget,
                $"Target {target.Current.AutomationId} at {bounds}, point {point.X},{point.Y}; hit " +
                $"{hit.Current.AutomationId}/{hit.Current.Name}/{hit.Current.ClassName}/{hit.Current.ControlType.ProgrammaticName} at {hit.Current.BoundingRectangle}");
            Assert.True(SetCursorPos(point.X, point.Y));
            Assert.True(GetCursorPos(out var actual));
            Assert.Equal(point, actual);
            AssertOwnedForeground(run, handle, "before-native-pointer-input");
            Assert.Equal(handle, GetAncestor(WindowFromPoint(actual), 2));
            var inputs = new[]
            {
                new FixtureInput { Mouse = new FixtureMouse { Flags = 2 } },
                new FixtureInput { Mouse = new FixtureMouse { Flags = 4 } }
            };
            var sent = SendInput(2, inputs, Marshal.SizeOf<FixtureInput>());
            if (sent == 1) SendInput(1, [inputs[1]], Marshal.SizeOf<FixtureInput>());
            Assert.Equal(2u, sent);
        }
        finally { SetThreadDpiAwarenessContext(previous); }
    }

    private static void ConfirmForegroundAfterRequest(IntPtr handle, TimeSpan? foregroundTimeout = null) =>
        ConfirmForegroundAfterRequest(handle,
            () => SendMessageTimeout(handle, 0, IntPtr.Zero, IntPtr.Zero, 0,
                checked((uint)FixtureForegroundTimeout.TotalMilliseconds), out _) != IntPtr.Zero,
            GetForegroundWindow, foregroundTimeout);

    private static void ConfirmForegroundAfterRequest(
        IntPtr handle, Func<bool> acknowledge, Func<IntPtr> getForeground, TimeSpan? foregroundTimeout = null)
    {
        // Cross-thread activation is asynchronous. WM_NULL waits for the target queue to process it.
        if (!acknowledge())
            throw new InvalidOperationException(
                $"Owned window 0x{handle.ToInt64():X} did not acknowledge activation within " +
                $"{FixtureForegroundTimeout.TotalMilliseconds:0} ms (Win32 error {Marshal.GetLastWin32Error()}); " +
                "refusing pointer input.");
        // A popup can still hold foreground after the target queue acknowledges activation.
        var foreground = IntPtr.Zero;
        if (!SpinWait.SpinUntil(() => (foreground = getForeground()) == handle,
                foregroundTimeout ?? FixtureForegroundTimeout))
            AssertOwnedForeground(handle, foreground);
    }

    private static void AssertOwnedForeground(IntPtr handle, Func<IntPtr> getForeground) =>
        AssertOwnedForeground(handle, getForeground());

    private static void AssertOwnedForeground(IntPtr handle, IntPtr foreground)
    {
        if (foreground == handle) return;
        GetWindowThreadProcessId(foreground, out var processId);
        var className = new System.Text.StringBuilder(256);
        var classLength = GetClassName(foreground, className, className.Capacity);
        throw new InvalidOperationException(
            $"Expected owned window 0x{handle.ToInt64():X} in the foreground, but found " +
            $"0x{foreground.ToInt64():X} (PID {processId}, class {(classLength > 0 ? className.ToString() : "<unavailable>")}); " +
            "refusing pointer input.");
    }

    private void AssertOwnedForeground(GatewayFixtureRun run, IntPtr expected, string phase)
    {
        var actual = GetForegroundWindow();
        if (actual != expected)
        {
            object WindowState(IntPtr handle)
            {
                var threadId = GetWindowThreadProcessId(handle, out var processId);
                return new
                {
                    hwnd = handle.ToInt64(),
                    threadId,
                    processId,
                    belongsToTaskApp = processId == run.AppProcessId,
                    exists = IsWindow(handle),
                    visible = IsWindowVisible(handle),
                    minimized = IsIconic(handle),
                    maximized = IsZoomed(handle),
                };
            }

            // Read ownership and native state only, never foreign window text or pixels.
            output.WriteLine("fixture-foreground-mismatch: " + JsonSerializer.Serialize(new
            {
                phase,
                timestampUtc = DateTimeOffset.UtcNow,
                appProcessId = run.AppProcessId,
                runnerProcessId = Environment.ProcessId,
                expected = WindowState(expected),
                actual = WindowState(actual),
            }));
        }
        Assert.Equal(expected, actual);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FixturePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FixtureInput { public uint Type; public FixtureMouse Mouse; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FixtureMouse { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, System.Text.StringBuilder className, int maxCount);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(FixturePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out FixturePoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, FixtureInput[] inputs, int size);

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task OwnerFooterUsesAuthenticatedProfileAndLiveConnectionStatus()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "WorkspaceOwner")?.Current.Name == "Fixture",
                "authenticated profile first name in footer");
            var owner = FindById(run, "WorkspaceOwner")!;
            Assert.Contains("Connected", owner.Current.HelpText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Personal workspace", owner.Current.HelpText);
            await WaitForAuthenticatedOwnerPhotoAsync(run);
        });
    }

    [GatewayFixtureUiFact]
    [Trait("Category", "GatewayFixture")]
    public async Task OwnerFooterRendersNamedSharedOwnerForGatewayOwnerProfile()
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "chat" });
            await WaitUiAsync(run, () => FindById(run, "WorkspaceOwner")?.Current.Name == "Fixture · Shared owner",
                "named shared Gateway owner label in footer");
            var owner = FindById(run, "WorkspaceOwner")!;
            Assert.Equal("Fixture · Shared owner", owner.Current.Name);
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
            await WaitUiAsync(run, () => FindById(run, "WorkspaceOwner")?.Current.Name == "Fixture",
                "named profile without avatar eligibility hints in footer");
            var owner = FindById(run, "WorkspaceOwner")!;
            Assert.Contains("Connected", owner.Current.HelpText, StringComparison.OrdinalIgnoreCase);
            await WaitForAuthenticatedOwnerPhotoAsync(run);
            await run.InvokeAsync("app.navigate", new { page = "settings" });
            await WaitUiAsync(run, () => FindById(run, "SettingsOwner") is
                { Current.IsOffscreen: false, Current.Name: "Fixture" },
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
            var openChat = row.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "Open in chat")));
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
        Action? releasePendingResponses = null, string appTheme = "Light",
        Action<GatewayFixtureProfile>? configureProfile = null,
        bool connectGateway = true, TimeSpan? testTimeout = null)
    {
        var appPath = Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_APP")
            ?? throw new InvalidOperationException("Set OPENCLAW_GATEWAY_FIXTURE_APP to the freshly built app. No installed-app fallback is allowed.");
        var run = await GatewayFixtureRun.StartAsync(appPath,
            Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_ARTIFACTS"),
            allowAgentCreation: allowAgentCreation, requireAgentSelection: requireAgentSelection,
            allowSessionMutations: allowSessionMutations,
            scenario: scenario, appTheme: appTheme, prepareSetupHandoff: prepareSetupHandoff, postSetupRestart: postSetupRestart,
            releasePendingResponses: releasePendingResponses, configureProfile: configureProfile,
            connectGateway: connectGateway);
        Task body = Task.CompletedTask;
        var failed = false;
        try
        {
            output.WriteLine($"Fixture run {run.Profile.RunId}, PID {run.AppProcessId}, artifacts: {run.ArtifactsDirectory}");
            // UIA is synchronous and can block inside a hung app. Keep the deadline
            // outside that worker so failure reporting and owned-process cleanup still run.
            body = Task.Run(() => test(run));
            await body.WaitAsync(testTimeout ?? TimeSpan.FromSeconds(150)).ConfigureAwait(false);
            run.EnsureRunning();
            Assert.Empty(run.Gateway.UnexpectedRequests);
            var crashLog = Path.Combine(run.Profile.DataDirectory, "crash.log");
            Assert.False(File.Exists(crashLog) && new FileInfo(crashLog).Length > 0, "The real app wrote a crash log.");
            await run.WriteReportAsync("passed");
        }
        catch (Exception ex)
        {
            failed = true;
            // The owner bounds the whole synchronous capture entry, stops its app,
            // joins body/capture workers, then disposes. Never return to another fixture
            // with a live UIA worker or overwrite the original assertion with teardown.
            await run.FinishUiFailureAsync(ex, body,
                token => CaptureIfRequestedAsync(run, "failure.png", token), releasePendingResponses);
            throw;
        }
        finally
        {
            if (!failed)
            {
                // Release response gates before the server joins its connections.
                try { releasePendingResponses?.Invoke(); }
                finally { await run.DisposeAsync(); }
            }
        }
    }

    private const string SessionAgentSelectorId = "WorkspaceAssistantSelector";
    private sealed record SessionSelectionStage(string Stage, string IntendedAgent, string ObservedAgent,
        bool? RowPresent = null, bool? RowOffscreen = null, bool? RowSelected = null,
        string? Operation = null, string? Edge = null, int Attempt = 0, int ElapsedMs = 0,
        int OperationElapsedMs = 0, bool? PopupCollapsed = null, int UnavailableCount = 0,
        string? ExceptionCategory = null, int? HResult = null,
        string? LastUnavailableOperation = null, int? LastUnavailableHResult = null,
        bool CountersSaturated = false);

    private static async Task SelectSessionAsync(GatewayFixtureRun run, string title, string key, bool waitForHistory = true,
        Action<SessionSelectionStage>? observe = null, Action<JsonElement>? observeHistory = null)
    {
        var agentId = $"WorkspaceAgent:{SessionDisplayResolver.Resolve(new SessionInfo { Key = key }).AgentId}";
        static string SafeAgent(string id) => id switch
        {
            "WorkspaceAgent:main" => "main", "WorkspaceAgent:research" => "research", "" => "none", _ => "other"
        };
        var probe = new SessionSelectionProbe(SafeAgent(agentId), observe);
        void Trace(string stage, bool? present = null, bool? offscreen = null, bool? selected = null) =>
            probe.Emit(probe.Current with { Stage = stage, RowPresent = present, RowOffscreen = offscreen, RowSelected = selected });
        Task WaitSelectionAsync(string stage, Func<bool> condition, string description) =>
            WaitUiAsync(run, () => { probe.BeginAttempt(stage); return condition(); }, description, onProbeError: probe.Fault);
        bool AgentAcknowledged(AutomationElement? element)
        {
            if (element is null)
            {
                probe.Emit(probe.Current with { Stage = "agent-observed", ObservedAgent = "none" });
                return false;
            }
            var pattern = probe.Read("selection-pattern", () => (SelectionPattern)element.GetCurrentPattern(SelectionPattern.Pattern));
            var items = probe.Read("get-selection", () => pattern.Current.GetSelection());
            var id = probe.Read("selected-id", () => items.Length == 1 ? items[0].Current.AutomationId : string.Empty);
            probe.Emit(probe.Current with { Stage = "agent-observed",
                ObservedAgent = items.Length == 1 ? SafeAgent(id) : items.Length == 0 ? "none" : "multiple" });
            return items.Length == 1 && string.Equals(id, agentId, StringComparison.Ordinal);
        }
        try
        {
            AutomationElement? selector = null;
            AutomationElement? selectorWindow = null;
            var ownedProcessId = run.AppProcessId;
            var ownedWindowHandle = 0;
            int[] windowRuntimeId = [];
            int[] selectorRuntimeId = [];

            static bool HasFiniteBounds(System.Windows.Rect bounds) =>
                !bounds.IsEmpty && double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
                double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) && bounds.Width > 0 && bounds.Height > 0;

            bool SelectorIsLive()
            {
                probe.Run("owned-app-check", run.EnsureRunning);
                return probe.Read("retained-selector-current", () =>
                {
                    if (selector is null || selectorWindow is null) return false;
                    var owner = selectorWindow.Current;
                    var current = selector.Current;
                    var ownerBounds = owner.BoundingRectangle;
                    var bounds = current.BoundingRectangle;
                    return run.AppProcessId == ownedProcessId && owner.ProcessId == ownedProcessId &&
                        ownedWindowHandle != 0 && owner.NativeWindowHandle == ownedWindowHandle &&
                        owner.ControlType == ControlType.Window && owner.IsEnabled && !owner.IsOffscreen &&
                        windowRuntimeId.Length > 0 && selectorWindow.GetRuntimeId().SequenceEqual(windowRuntimeId) &&
                        current.ProcessId == ownedProcessId && current.ControlType == ControlType.ComboBox &&
                        current.AutomationId == SessionAgentSelectorId && current.IsEnabled && !current.IsOffscreen &&
                        selectorRuntimeId.Length > 0 && selector.GetRuntimeId().SequenceEqual(selectorRuntimeId) &&
                        HasFiniteBounds(ownerBounds) && HasFiniteBounds(bounds) && ownerBounds.Contains(bounds);
                });
            }

            bool FindOwnedSelector()
            {
                selector = null;
                selectorWindow = null;
                // Capture the owner from the existing initial discovery, not another global query.
                foreach (var window in AppWindows(run, probe))
                {
                    var match = probe.Read("owned-descendant-lookup", () => window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, SessionAgentSelectorId)));
                    if (match is null) continue;
                    var identity = probe.Read("selector-owner-capture", () =>
                        (Handle: window.Current.NativeWindowHandle, Window: window.GetRuntimeId(), Selector: match.GetRuntimeId()));
                    selectorWindow = window;
                    selector = match;
                    ownedWindowHandle = identity.Handle;
                    windowRuntimeId = identity.Window;
                    selectorRuntimeId = identity.Selector;
                    return SelectorIsLive();
                }
                return false;
            }

            await WaitSelectionAsync("selector-lookup", () => probe.Read("selector-lookup", FindOwnedSelector),
                "live owned agent selector");
            if (!AgentAcknowledged(selector))
            {
                var dropdown = probe.Read("expand-pattern", () =>
                    (ExpandCollapsePattern)selector!.GetCurrentPattern(ExpandCollapsePattern.Pattern));
                probe.Run("expand", dropdown.Expand);
                AutomationElement? item = null;
                await WaitSelectionAsync("agent-item-lookup", () =>
                    (item = probe.Read("item-lookup", () => FindById(run, agentId, probe))) is not null, "session's agent");
                // ComboBox selection is a required native contract, not a generic Invoke fallback.
                // WinUI's SelectionItem provider collapses its parent after this one selection.
                var selection = probe.Read("selection-item-pattern", () =>
                    (SelectionItemPattern)item!.GetCurrentPattern(SelectionItemPattern.Pattern));
                probe.Run("select-once", selection.Select);
            }
            await WaitSelectionAsync("agent-acknowledgement", () =>
            {
                // AssistantSelector is the same PaneHeader control for this selection's lifetime.
                // Reuse only its live peer, never cached properties/selection or another window's peer.
                if (!SelectorIsLive()) return false;
                var popup = probe.Read("popup-pattern", () =>
                    (ExpandCollapsePattern)selector!.GetCurrentPattern(ExpandCollapsePattern.Pattern));
                var collapsed = probe.Read("popup-state", () => popup.Current.ExpandCollapseState) == ExpandCollapseState.Collapsed;
                probe.Emit(probe.Current with { PopupCollapsed = collapsed });
                var selected = AgentAcknowledged(selector);
                return collapsed && selected;
            }, "collapsed popup and exact selected session agent acknowledged");
            var rowId = key == GatewayScenario.MainSessionKey ? "WorkspaceNavHome" : $"WorkspaceSession:{key}";
            AutomationElement? row = null;
            await WaitSelectionAsync("row-lookup", () =>
            {
                row = probe.Read("row-lookup", () => FindById(run, rowId, probe));
                Trace("row-observed", present: row is not null);
                return row is not null;
            }, $"sidebar session {title}");
            var selectedRow = row!;
            if (observe is not null)
            {
                var offscreen = probe.Read("row-offscreen", () => selectedRow.Current.IsOffscreen);
                bool? selected = probe.Read("row-selection", () => selectedRow.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
                    ? (bool?)((SelectionItemPattern)pattern).Current.IsSelected : null);
                Trace("row-before-invoke", true, offscreen, selected);
            }
            probe.Run("row-invoke", () => Invoke(selectedRow));
            Trace("composer-acknowledgement");
            await WaitComposerSelectionAsync(run, key, waitForHistory);
            if (key != GatewayScenario.MainSessionKey)
                await WaitUiAsync(run, () => SessionSelected(run, title), $"selected session {title}");
            if (waitForHistory) await WaitHistoryAsync(run, key, observeHistory);
            Trace("selection-complete", true, selected: key == GatewayScenario.MainSessionKey ? null : true);
        }
        catch (Exception error)
        {
            probe.Fault(error); // Cached operation only; never query UIA while reporting a failure.
            throw;
        }
    }

    private static Task WaitHistoryAsync(GatewayFixtureRun run, string key, Action<JsonElement>? observe = null) =>
        run.WaitForAsync(async () =>
        {
            var snapshot = await run.InvokeAsync("app.chat.snapshot", new { threadId = key });
            var loaded = snapshot.TryGetProperty("selectedTimeline", out var timeline)
                && timeline.ValueKind == JsonValueKind.Object
                && timeline.GetProperty("historyLoaded").GetBoolean();
            observe?.Invoke(snapshot);
            return loaded;
        }, $"history loaded for {key}");

    private static bool SessionSelected(GatewayFixtureRun run, string title)
    {
        var item = FindInApp(run, new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, title),
            new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true)));
        return item is not null && ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected;
    }

    private static bool RenderConsumedHistory(GatewayFixtureRun run, string key,
        VoiceTailNativeObservation? observation = null)
    {
        var composer = FindById(run, "ChatComposerInput");
        if (observation is not null) observation.ComposerPresent = composer is not null;
        if (composer is null || string.IsNullOrEmpty(composer.Current.ItemStatus))
        {
            if (observation is not null) observation.RenderConsumedHistory = false;
            return false;
        }
        using var rendered = JsonDocument.Parse(composer.Current.ItemStatus);
        var consumed = rendered.RootElement.GetProperty("loadedThreadIds").EnumerateArray()
            .Any(thread => thread.GetString() == key);
        if (observation is not null)
        {
            observation.RenderConsumedHistory = consumed;
            observation.SelectedThreadMatches = rendered.RootElement.TryGetProperty("selectedThreadId", out var selected)
                && selected.ValueKind == JsonValueKind.String && selected.GetString() == key;
        }
        return consumed;
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

    private static AutomationElement? FindById(GatewayFixtureRun run, string id, SessionSelectionProbe? probe = null) =>
        FindInApp(run, new PropertyCondition(AutomationElement.AutomationIdProperty, id), probe);

    private static AutomationElement? FindButton(GatewayFixtureRun run, string name) =>
        FindInApp(run, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, name)));

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

    private static AutomationElement? FindInApp(GatewayFixtureRun run, Condition condition, SessionSelectionProbe? probe = null)
    {
        foreach (var window in AppWindows(run, probe))
        {
            var match = probe is null ? window.FindFirst(TreeScope.Descendants, condition)
                : probe.Read("owned-descendant-lookup", () => window.FindFirst(TreeScope.Descendants, condition));
            if (match is not null) return match;
        }
        return null;
    }

    private static IEnumerable<AutomationElement> AppWindows(GatewayFixtureRun run, SessionSelectionProbe? probe = null)
    {
        if (probe is null) run.EnsureRunning();
        else probe.Run("owned-app-check", run.EnsureRunning);
        AutomationElementCollection ReadWindows() => AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, run.AppProcessId));
        var windows = probe is null ? ReadWindows() : probe.Read("owned-window-enumeration", ReadWindows);
        return windows.Cast<AutomationElement>().Where(window => !(probe is null ? window.Current.IsOffscreen
            : probe.Read("owned-window-offscreen", () => window.Current.IsOffscreen)));
    }

    private static AutomationElement FindHub(GatewayFixtureRun run) =>
        AppWindows(run).First(window => window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ChatComposerInput")) is not null);

    private static AutomationElement? FindTimeline(GatewayFixtureRun run) =>
        FindInApp(run, new PropertyCondition(AutomationElement.NameProperty, "Chat messages"));

    private static bool IsVisibleInTimeline(GatewayFixtureRun run, string text,
        VoiceTailNativeObservation? observation = null)
    {
        var timeline = FindTimeline(run);
        var message = FindText(run, text);
        if (observation is not null)
        {
            observation.TimelinePresent = timeline is not null;
            observation.FinalElementPresent = message is not null;
        }
        var visible = timeline is not null && message is not null && IsVisibleInTimeline(timeline, message, observation);
        if (observation is not null) observation.FinalVisible = visible;
        return visible;
    }

    private static bool IsVisibleInTimeline(AutomationElement timeline, AutomationElement message,
        VoiceTailNativeObservation? observation = null)
    {
        var offscreen = message.Current.IsOffscreen;
        if (observation is not null) observation.FinalOffscreen = offscreen;
        if (offscreen)
            return false;
        var viewport = timeline.Current.BoundingRectangle;
        var bounds = message.Current.BoundingRectangle;
        if (observation is not null)
        {
            observation.FinalHasPositiveBounds = !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0;
            observation.FinalWithinVerticalViewport = bounds.Top >= viewport.Top - 1 && bounds.Bottom <= viewport.Bottom + 1;
            observation.FinalOverlapsHorizontalViewport = bounds.Right > viewport.Left && bounds.Left < viewport.Right;
        }
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

    private static async Task WaitUiAsync(GatewayFixtureRun run, Func<bool> condition, string description,
        TimeSpan? timeout = null, Action<Exception>? onProbeError = null)
    {
        await run.WaitForAsync(() =>
        {
            try { return Task.FromResult(condition()); }
            catch (ElementNotAvailableException error)
            {
                onProbeError?.Invoke(error);
                return Task.FromResult(false);
            }
            catch (Exception error) { onProbeError?.Invoke(error); throw; }
        }, description, timeout);
    }

    private static Task CaptureIfRequestedAsync(GatewayFixtureRun run, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Environment.GetEnvironmentVariable("OPENCLAW_GATEWAY_FIXTURE_SCREENSHOTS") != "1")
            return Task.CompletedTask;
        OwnedWindowCapture.Save(run, AppWindows(run).OrderByDescending(window =>
            window.Current.BoundingRectangle.Width * window.Current.BoundingRectangle.Height).First(), name, cancellationToken);
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
