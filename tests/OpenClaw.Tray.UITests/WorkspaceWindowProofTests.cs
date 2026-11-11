using System.Diagnostics;
using System.Windows.Automation;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.UITests;

[CollectionDefinition("Workspace native proof", DisableParallelization = true)]
public sealed class WorkspaceProofCollection;

[Collection("Workspace native proof")]
public sealed class WorkspaceWindowProofTests
{
    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task OfflineAssistantMenuKeepsConnectionEntryWithoutEnablingSessionCreation(string theme)
    {
        using var app = new AccessibilityAppFixture(
            initializeAxe: false, theme: theme, syntheticData: false);
        await app.NavigateAsync("chat", "ChatPage", "WorkspaceOwner");
        var workspace = AutomationElement.FromHandle(app.HubWindowHandle);
        var pid = workspace.Current.ProcessId;
        Assert.False(Find(workspace, "WorkspaceSessionsAdd").Current.IsEnabled);
        var selector = Find(workspace, "WorkspaceAssistantSelector");
        Assert.True(selector.Current.IsEnabled);
        var dropdown = (ExpandCollapsePattern)selector.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        dropdown.Expand();
        await WaitUntilAsync(() => ProcessWindows(pid).Cast<AutomationElement>().Any(window =>
            window.FindFirst(TreeScope.Descendants, new PropertyCondition(
                AutomationElement.AutomationIdProperty, "WorkspaceNewAgent")) is not null));
        var entry = ProcessWindows(pid).Cast<AutomationElement>()
            .Select(window => window.FindFirst(TreeScope.Descendants, new PropertyCondition(
                AutomationElement.AutomationIdProperty, "WorkspaceNewAgent")))
            .First(item => item is not null)!;
        Assert.Equal("New agent", entry.Current.Name);
        Assert.True(entry.Current.IsEnabled);
        dropdown.Collapse();
        AutomationElement? restoredCreation = null;
        await WaitUntilAsync(() => (restoredCreation = FindOrNull(workspace, "WorkspaceSessionsAdd")) is not null);
        Assert.NotNull(restoredCreation);
        Assert.False(restoredCreation.Current.IsEnabled);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task SidebarActionBackplates_AlignWithNavigationItems(string theme)
    {
        using var app = new AccessibilityAppFixture(initializeAxe: false, theme: theme,
            initialRoute: "sessions", agentIdentities: true);
        await app.NavigateAsync("chat", "ChatPage", "ChatComposerInput");
        var root = AutomationElement.FromHandle(app.HubWindowHandle);
        var scale = Find(root, "WorkspaceTogglePane").Current.BoundingRectangle.Width / 40;
        var home = Find(root, "WorkspaceNavHome").Current.BoundingRectangle;
        var assistant = Find(root, "WorkspaceAssistantSelector").Current.BoundingRectangle;
        // Native ComboBox automation bounds extend 4 DIP outside its Background.
        // NavigationViewItem automation bounds already match its inset LayoutRoot.
        Assert.InRange(assistant.Left + 4 * scale - home.Left, -1, 1);
        Assert.InRange(assistant.Right - 4 * scale - home.Right, -1, 1);
        foreach (var id in new[] { "WorkspaceSessionsAdd", "WorkspaceNotifications" })
        {
            var bounds = Find(root, id).Current.BoundingRectangle;
            Assert.InRange(bounds.Right - home.Right, -1, 1);
        }
        Capture(app, theme, "sidebar-backplates");
        if (!string.IsNullOrEmpty(ProofDirectory))
        {
            foreach (var id in new[] { "WorkspaceAssistantSelector", "WorkspaceNotifications" })
            {
                var bounds = Find(root, id).Current.BoundingRectangle;
                System.Windows.Forms.Cursor.Position = new System.Drawing.Point(
                    (int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
                await Task.Delay(250);
                Capture(app, theme, $"{id}-hover");
            }
        }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task AgentSelector_UsesConfiguredIdentityPictureNameAndId(string theme)
    {
        using var app = new AccessibilityAppFixture(initializeAxe: false, theme: theme,
            initialRoute: "sessions", agentIdentities: true);
        await app.NavigateAsync("chat", "ChatPage", "ChatComposerInput");
        var root = AutomationElement.FromHandle(app.HubWindowHandle);
        Assert.Null(root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ChatComposerSessionPicker")));
        var selector = Find(root, "WorkspaceAssistantSelector");
        var selection = (SelectionPattern)selector.GetCurrentPattern(SelectionPattern.Pattern);
        Assert.Contains(selection.Current.GetSelection(), item => item.Current.Name == "Configured assistant, main");
        var composer = Find(root, "ChatComposerInput");
        const string draft = "Retain draft while browsing agent identities";
        ((ValuePattern)composer.GetCurrentPattern(ValuePattern.Pattern)).SetValue(draft);
        Capture(app, theme, "agent-selected");
        var selectorBounds = selector.Current.BoundingRectangle;
        var homeBounds = Find(root, "WorkspaceNavHome").Current.BoundingRectangle;
        var toggle = Find(root, "WorkspaceTogglePane").Current.BoundingRectangle;
        var center = (toggle.Left + toggle.Right) / 2;
        AssertIconCenter(selectorBounds, center, color => color.R < 20 && color.G is > 110 and < 145 && color.B is > 110 and < 145);
        AssertIconCenter(homeBounds, center, color => color.B > color.R + 70 && color.B > color.G + 50);
        ((ExpandCollapsePattern)selector.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        await WaitUntilAsync(() => FindPopup(root, "WorkspaceAgent:research") is not null);
        Assert.Equal("Configured assistant, main", FindPopup(root, "WorkspaceAgent:main")!.Current.Name);
        Assert.Equal("Research assistant, research", FindPopup(root, "WorkspaceAgent:research")!.Current.Name);
        Assert.Equal("No configured icon, fallback", FindPopup(root, "WorkspaceAgent:fallback")!.Current.Name);
        Assert.NotNull(FindPopup(root, "WorkspaceNewAgent"));
        await Task.Delay(500);
        Assert.Equal(selectorBounds, selector.Current.BoundingRectangle);
        Assert.Equal(homeBounds, Find(root, "WorkspaceNavHome").Current.BoundingRectangle);
        CaptureFlyout(root, theme, "agent-identities");
        AssertIconCenter(FindPopup(root, "WorkspaceAgent:main")!.Current.BoundingRectangle, center,
            color => color.R < 20 && color.G is > 110 and < 145 && color.B is > 110 and < 145,
            expectedWidth: toggle.Width * 32 / 40);
        // "fallback" has no image or text avatar, so it gets the generated face: pale teal background (D5F6F3).
        AssertIconCenter(FindPopup(root, "WorkspaceAgent:fallback")!.Current.BoundingRectangle, center,
            color => color.R is > 190 and < 235 && color.G > color.R + 20 && color.B > color.R + 15,
            expectedWidth: toggle.Width * 32 / 40);
        ((ExpandCollapsePattern)selector.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
        await WaitUntilAsync(() => IsVisible(root, "ChatComposerInput"));
        Assert.Equal(selectorBounds, selector.Current.BoundingRectangle);
        Assert.Equal(homeBounds, Find(root, "WorkspaceNavHome").Current.BoundingRectangle);
        Assert.Equal(draft, ((ValuePattern)Find(root, "ChatComposerInput").GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        ((ExpandCollapsePattern)selector.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        await WaitUntilAsync(() => FindPopup(root, "WorkspaceNewAgent") is not null);
        ((SelectionItemPattern)FindPopup(root, "WorkspaceNewAgent")!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        await WaitUntilAsync(() => FindPopup(root, "AgentCreationDialog") is not null);
        var creation = FindPopup(root, "AgentCreationDialog")!;
        Assert.NotNull(creation.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "AgentCreationName")));
        Assert.Contains("operator.admin", string.Join(" ", creation.FindAll(TreeScope.Descendants,
            Condition.TrueCondition).Cast<AutomationElement>().Select(item => item.Current.Name)));
        CaptureFlyout(root, theme, "new-agent");
        var nameBounds = Find(creation, "AgentCreationName").Current.BoundingRectangle;
        using (var pixel = new System.Drawing.Bitmap(1, 1))
        {
            using var graphics = System.Drawing.Graphics.FromImage(pixel);
            graphics.CopyFromScreen((int)(nameBounds.Left - toggle.Width * 12 / 40),
                (int)(nameBounds.Top + nameBounds.Height / 2), 0, 0, pixel.Size);
            var background = pixel.GetPixel(0, 0);
            Assert.Equal(theme == "Dark", background.R + background.G + background.B < 384);
        }
        System.Windows.Forms.SendKeys.SendWait("{ESC}");
        await WaitUntilAsync(() => FindPopup(root, "AgentCreationDialog") is null);
        Assert.Equal(draft, ((ValuePattern)Find(root, "ChatComposerInput").GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        Assert.Contains(selection.Current.GetSelection(), item => item.Current.Name == "Configured assistant, main");
        ((WindowPattern)root.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Normal);
        foreach (var width in new[] { 1000, 1350 })
        {
            ((TransformPattern)root.GetCurrentPattern(TransformPattern.Pattern)).Resize(width, 800);
            await Task.Delay(350);
            selectorBounds = selector.Current.BoundingRectangle;
            homeBounds = Find(root, "WorkspaceNavHome").Current.BoundingRectangle;
            selector.SetFocus();
            System.Windows.Forms.SendKeys.SendWait("%{DOWN}");
            await WaitUntilAsync(() => FindPopup(root, "WorkspaceAgent:research") is not null);
            await Task.Delay(350);
            Assert.Equal(selectorBounds, selector.Current.BoundingRectangle);
            Assert.Equal(homeBounds, Find(root, "WorkspaceNavHome").Current.BoundingRectangle);
            System.Windows.Forms.SendKeys.SendWait("{ESC}");
            await Task.Delay(350);
            Assert.Equal(selectorBounds, selector.Current.BoundingRectangle);
            Assert.Equal(homeBounds, Find(root, "WorkspaceNavHome").Current.BoundingRectangle);
        }
        Invoke(Find(root, "WorkspaceTogglePane"));
        await WaitUntilAsync(() => IsVisible(root, "WorkspaceReopenPane") && !IsVisible(root, "WorkspaceAssistantSelector"));
        Assert.Null(root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ChatComposerSessionPicker")));
        Invoke(Find(root, "WorkspaceReopenPane"));
        await WaitUntilAsync(() => IsVisible(root, "WorkspaceAssistantSelector"));
        Assert.Equal(draft, ((ValuePattern)Find(root, "ChatComposerInput").GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        Capture(app, theme, "sidebar-composer");
        Invoke(Find(root, "WorkspaceOwner"));
        Assert.Equal("Owner", Find(root, "WorkspaceOwner").Current.Name);
        Assert.Equal("Disconnected", Find(root, "WorkspaceOwner").Current.HelpText);
        // Shorter account menu: profile, Gateway submenu, Settings, Pair a device, Help & about.
        await WaitUntilAsync(() => FindPopup(root, "WorkspaceOwnerGateway") is not null);
        foreach (var id in new[] { "WorkspaceOwnerProfile", "WorkspaceOwnerSettings", "WorkspaceOwnerPairDevice", "WorkspaceOwnerHelp" })
            Assert.NotNull(FindPopup(root, id));
        Assert.Equal("Disconnected", FindPopup(root, "WorkspaceOwnerGateway")!.Current.HelpText);
        Assert.False(FindPopup(root, "WorkspaceOwnerProfile")!.Current.IsEnabled);
        Assert.Null(FindPopup(root, "WorkspaceOwnerConnectionStatus"));
        CaptureFlyout(root, theme, "owner-account-menu");
        await ExpandOwnerGatewayMenuAsync(root);
        Assert.Equal("Connection Status: Disconnected", FindPopup(root, "WorkspaceOwnerConnectionStatus")!.Current.Name);
        CaptureFlyout(root, theme, "owner-live-status");
        System.Windows.Forms.SendKeys.SendWait("{ESC}");
        System.Windows.Forms.SendKeys.SendWait("{ESC}");
    }

    private static void AssertIconCenter(System.Windows.Rect bounds, double expected,
        Func<System.Drawing.Color, bool> isIcon, double? expectedWidth = null)
    {
        // Native ComboBox does not expose its selected presentation's decorative image through UIA.
        using var bitmap = new System.Drawing.Bitmap((int)bounds.Width, (int)bounds.Height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen((int)bounds.Left, (int)bounds.Top, 0, 0, bitmap.Size);
        var columns = new bool[bitmap.Width];
        for (var x = 0; x < bitmap.Width; x++)
            for (var y = 0; y < bitmap.Height; y++)
                columns[x] |= isIcon(bitmap.GetPixel(x, y));
        var left = 0;
        var right = -1;
        // Keep the widest colored span (the icon), not the narrow selection pill.
        // Cropping a percentage of a wide popup can cut into the avatar itself.
        for (var x = 0; x < columns.Length; x++)
        {
            if (!columns[x]) continue;
            var start = x;
            while (x + 1 < columns.Length && columns[x + 1]) x++;
            if (x - start > right - left)
            {
                left = start;
                right = x;
            }
        }
        Assert.True(right >= left, "Expected the rendered fixture avatar or Home artwork.");
        if (expectedWidth is { } width)
            Assert.InRange(right - left + 1, width - 2, width + 2);
        var actual = (int)bounds.Left + (left + right) / 2d;
        Assert.True(Math.Abs(actual - expected) <= 2,
            $"Rendered icon center {actual} must align with pane toggle center {expected}. Bounds={bounds}; span={left}..{right}.");
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task SettingsChrome_SearchAndBidirectionalHistory_StayBelowNativeTitle(string theme)
    {
        using var app = new AccessibilityAppFixture(initializeAxe: false, theme: theme, initialRoute: "settings");
        var root = AutomationElement.FromHandle(app.HubWindowHandle);
        var ownedWindow = app.HubWindowHandle;
        var ownedProcessId = root.Current.ProcessId;

        async Task<AutomationElement> WaitForSettingsForwardAsync(string pageMarkerId, bool expectedEnabled)
        {
            AutomationElement? readyForward = null;
            // A page marker does not establish sibling history-button readiness. Keep one
            // joint observation under the original owned root, within the existing wait budget.
            await WaitUntilAsync(() =>
            {
                try
                {
                    var rootInfo = root.Current;
                    if (app.HubWindowHandle != ownedWindow || rootInfo.ProcessId != ownedProcessId ||
                        new IntPtr(rootInfo.NativeWindowHandle) != ownedWindow)
                        return false;
                    var page = FindOrNull(root, pageMarkerId);
                    var button = FindOrNull(root, "SettingsForward");
                    if (page is null || button is null) return false;
                    var pageInfo = page.Current;
                    var buttonInfo = button.Current;
                    if (pageInfo.ProcessId != ownedProcessId || pageInfo.IsOffscreen ||
                        !HasVisibleBounds(pageInfo.BoundingRectangle) ||
                        buttonInfo.ProcessId != ownedProcessId || buttonInfo.ControlType != ControlType.Button ||
                        buttonInfo.IsOffscreen || !HasVisibleBounds(buttonInfo.BoundingRectangle) ||
                        buttonInfo.IsEnabled != expectedEnabled)
                        return false;
                    readyForward = button;
                    return true;
                }
                catch (ElementNotAvailableException) { return false; }
            }, () => $"Owned {pageMarkerId} and SettingsForward (enabled={expectedEnabled}) did not become ready.");
            return readyForward!;
        }

        static bool HasVisibleBounds(System.Windows.Rect bounds) =>
            !bounds.IsEmpty && double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
            double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) && bounds.Width > 0 && bounds.Height > 0;

        Assert.Null(root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspaceNavigation")));
        var title = Find(root, "WorkspaceTitleBar").Current.BoundingRectangle;
        var toggle = Find(root, "SettingsTogglePane").Current.BoundingRectangle;
        var search = Find(root, "SettingsSearch").Current.BoundingRectangle;
        var back = Find(root, "SettingsBack").Current.BoundingRectangle;
        var forward = Find(root, "SettingsForward").Current.BoundingRectangle;
        Assert.True(toggle.Top >= title.Bottom);
        Assert.Equal(toggle.Top, search.Top);
        Assert.Equal(toggle.Top, back.Top);
        Assert.Equal(toggle.Top, forward.Top);
        // The window-level history pair sits between the pane toggle and the search button, without overlap.
        Assert.True(back.Left >= toggle.Right, $"Back {back} overlaps pane toggle {toggle}");
        Assert.True(forward.Left >= back.Right);
        Assert.True(search.Left >= forward.Right, $"Search {search} overlaps Forward {forward}");
        Assert.False(Find(root, "SettingsForward").Current.IsEnabled);
        await VerifySettingsFooterAsync(root, compact: false);
        await app.NavigateAsync("permissions", "PermissionsPage", "PermissionsPageMarker");
        Invoke(Find(root, "SettingsBack"));
        var forwardAfterBack = await WaitForSettingsForwardAsync("SettingsPageMarker", expectedEnabled: true);
        Assert.True(forwardAfterBack.Current.IsEnabled);
        Invoke(forwardAfterBack);
        var forwardAfterForward = await WaitForSettingsForwardAsync("PermissionsPageMarker", expectedEnabled: false);
        Assert.False(forwardAfterForward.Current.IsEnabled);

        AutomationElement? SearchBox() => ProcessWindows(root.Current.ProcessId).Cast<AutomationElement>()
            .Select(window => window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsSearchBox")))
            .FirstOrDefault(element => element is not null && !element.Current.IsOffscreen);
        Invoke(Find(root, "SettingsSearch"));
        AutomationElement? readyInput = null;
        await WaitUntilAsync(() => (readyInput = SearchBox()) is not null);
        var input = readyInput!;
        Assert.Equal(ControlType.Edit, input.Current.ControlType);
        await WaitUntilAsync(() => input.Current.HasKeyboardFocus);
        System.Windows.Forms.SendKeys.SendWait("Go to General");
        await WaitUntilAsync(() => ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).Current.Value == "Go to General");
        System.Windows.Forms.SendKeys.SendWait("{ENTER}");
        var forwardAfterSearch = await WaitForSettingsForwardAsync("SettingsPageMarker", expectedEnabled: false);
        await WaitUntilAsync(() => SearchBox() is null);
        Assert.False(forwardAfterSearch.Current.IsEnabled);
        Invoke(Find(root, "SettingsTogglePane"));
        await WaitUntilAsync(() => !IsVisible(root, "SettingsSearch"));
        await VerifySettingsFooterAsync(root, compact: true);
        Assert.Equal(toggle.Top, Find(root, "SettingsTogglePane").Current.BoundingRectangle.Top);
        await WaitUntilAsync(() => root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().Where(item => !item.Current.IsOffscreen &&
                item.Current.BoundingRectangle.Left < toggle.Right)
            .All(item => item.Current.BoundingRectangle.Top >= toggle.Bottom));
        Find(root, "SettingsTogglePane").SetFocus();
        System.Windows.Forms.SendKeys.SendWait("^e");
        await WaitUntilAsync(() => SearchBox() is not null);
        await WaitUntilAsync(() => SearchBox() is { } searchInput && searchInput.Current.HasKeyboardFocus);
        System.Windows.Forms.SendKeys.SendWait("{ESC}");
        await WaitUntilAsync(() => SearchBox() is null);
        Assert.True(IsVisible(root, "SettingsPageMarker"));
        Find(root, "SettingsTogglePane").SetFocus();
        System.Windows.Forms.SendKeys.SendWait("^f");
        await WaitUntilAsync(() => SearchBox() is not null);
        System.Windows.Forms.SendKeys.SendWait("{ESC}");
        await WaitUntilAsync(() => SearchBox() is null);
        Invoke(Find(root, "SettingsTogglePane"));
        await WaitUntilAsync(() => IsVisible(root, "SettingsSearch"));
        await VerifySettingsFooterAsync(root, compact: false);
        // Regression: leaving Settings hides its (closed) search flyout. That must not leave the
        // deferred-open helper waiting for a close that never comes, which dropped every shortcut.
        await app.NavigateAsync("chat", "ChatPage", "ChatComposerInput");
        await app.NavigateAsync("settings", "SettingsPage", "SettingsPageMarker");
        Find(root, "SettingsTogglePane").SetFocus();
        System.Windows.Forms.SendKeys.SendWait("^f");
        await WaitUntilAsync(() => SearchBox() is not null);
        System.Windows.Forms.SendKeys.SendWait("{ESC}");
        await WaitUntilAsync(() => SearchBox() is null);
        await app.NavigateAsync("settings", "SettingsPage", "SettingsPageMarker");
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE_PROOF_DIR")))
        {
            var bounds = root.Current.BoundingRectangle;
            System.Windows.Forms.Cursor.Position = new System.Drawing.Point((int)bounds.Right - 100, (int)bounds.Bottom - 100);
            await Task.Delay(500);
        }
        Capture(app, theme, "settings-chrome");
    }

    private static async Task VerifySettingsFooterAsync(AutomationElement root, bool compact)
    {
        // Settings ends with the same account, Dashboard and bell footer as Chat.
        await WaitUntilAsync(() => IsVisible(root, "SettingsOwner") && IsVisible(root, "SettingsDashboard") &&
            IsVisible(root, "SettingsNotifications"));
        // IsPaneOpen changes before the native pane transition has finished arranging its footer.
        await Task.Delay(350);
        Assert.Null(root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsConnectionStatus")));
        Assert.False(IsVisible(root, "WorkspaceTitleBarNotifications"), "Title-bar actions must not duplicate the Settings footer.");
        var owner = Find(root, "SettingsOwner").Current.BoundingRectangle;
        var dashboard = Find(root, "SettingsDashboard").Current.BoundingRectangle;
        var bell = Find(root, "SettingsNotifications").Current.BoundingRectangle;
        Assert.True(owner.Top > Find(root, "WorkspaceTitleBar").Current.BoundingRectangle.Bottom);
        var settingsItem = root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsNavGeneral"));
        var footerTop = Math.Min(owner.Top, Math.Min(dashboard.Top, bell.Top));
        // The nav item list always ends above the footer, even when General is scrolled away.
        var menu = Find(root, "NavView").FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "MenuItemsScrollViewer"));
        Assert.NotNull(menu);
        Assert.True(menu.Current.BoundingRectangle.Bottom <= footerTop + 1,
            $"Nav items {menu.Current.BoundingRectangle} overlap footer at {footerTop}; compact={compact}");
        if (settingsItem is not null && !settingsItem.Current.IsOffscreen)
            Assert.True(settingsItem.Current.BoundingRectangle.Bottom <= footerTop + 1,
                $"General {settingsItem.Current.BoundingRectangle} overlaps footer at {footerTop}; compact={compact}");
        if (compact)
        {
            var toggle = Find(root, "SettingsTogglePane").Current.BoundingRectangle;
            Assert.True(owner.Bottom <= dashboard.Top && dashboard.Bottom <= bell.Top, $"Rail footer is not stacked: {owner} {dashboard} {bell}");
            foreach (var bounds in new[] { owner, dashboard, bell })
            {
                // Same 40 DIP target as the toggle; allow scaled-DPI rounding like the reopen-button check.
                Assert.InRange(Math.Abs(bounds.Width - toggle.Width), 0, 1);
                Assert.InRange(bounds.Left, toggle.Left - 8, toggle.Left + 8);
            }
        }
        else
        {
            Assert.True(owner.Right <= dashboard.Left && dashboard.Right <= bell.Left, $"Footer is not one row: {owner} {dashboard} {bell}");
            Assert.InRange(Math.Abs((owner.Top + owner.Bottom) / 2 - (bell.Top + bell.Bottom) / 2), 0, 2);
        }

        // The status pill's card is reached from the account menu's Gateway submenu status row.
        Invoke(Find(root, "SettingsOwner"));
        await ExpandOwnerGatewayMenuAsync(root);
        Invoke(FindPopup(root, "WorkspaceOwnerConnectionStatus")!);
        await WaitUntilAsync(() => FindPopup(root, "GatewayStatusOpenConnection") is not null);
        System.Windows.Forms.SendKeys.SendWait("{ESC}");
        await WaitUntilAsync(() => FindPopup(root, "GatewayStatusOpenConnection") is null);
        Invoke(Find(root, "SettingsNotifications"));
        await WaitUntilAsync(() => FindPopup(root, "WorkspaceNotificationsOpenPage") is not null);
        System.Windows.Forms.SendKeys.SendWait("{ESC}");
        await WaitUntilAsync(() => FindPopup(root, "WorkspaceNotificationsOpenPage") is null);
        Assert.True(Find(root, "SettingsDashboard").Current.IsEnabled);
    }
    [Theory]
    [InlineData("mouse")]
    [InlineData("enter")]
    [InlineData("escape")]
    public async Task SettingsBackToApp_ReturnsToAppDestinationInSameWindow(string activation)
    {
        using var app = new AccessibilityAppFixture(initializeAxe: false, syntheticData: true, initialRoute: null);
        await app.NavigateAsync("chat", "ChatPage", "ChatComposerInput");
        var workspace = AutomationElement.FromHandle(app.HubWindowHandle);
        var workspaceHandle = app.HubWindowHandle;
        var workspaceProcessId = workspace.Current.ProcessId;
        var composer = Find(workspace, "ChatComposerInput");
        var identity = composer.GetRuntimeId();
        const string draft = "Keep this draft when going back to the app from Settings";
        ((ValuePattern)composer.GetCurrentPattern(ValuePattern.Pattern)).SetValue(draft);
        Assert.Null(workspace.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ChatDashboardButton")));
        await app.NavigateAsync("connection", "ConnectionPage", "ConnectionPageMarker");
        var settings = AutomationElement.FromHandle(app.HubWindowHandle);
        Assert.Equal(workspaceHandle, app.HubWindowHandle);
        Assert.NotNull(Find(settings, "ConnectionDashboardButton"));
        Assert.NotNull(Find(settings, "SettingsTitle"));
        Capture(app, "System", "connection-dashboard-card");
        // The compact rail keeps Back to app as an arrow; the title and key hint return with the pane.
        await InvokeWhenEnabledAsync(settings, "SettingsTogglePane");
        await WaitUntilAsync(() => !IsVisible(settings, "SettingsTitle"));
        Assert.True(IsVisible(settings, "SettingsBackToApp"));
        Capture(app, "System", $"settings-header-rail-{activation}");
        await InvokeWhenEnabledAsync(settings, "SettingsTogglePane");
        await WaitUntilAsync(() => IsVisible(settings, "SettingsTitle"));
        // Leave the toggle so its keyboard tooltip cannot cover the header in the proof frame.
        Find(settings, "SettingsBackToApp").SetFocus();
        await Task.Delay(500);
        Capture(app, "System", $"settings-header-{activation}");
        ActivateBackToApp();
        await WaitUntilAsync(() => IsVisible(workspace, "ChatComposerInput"));
        await AssertChatForegroundAsync();
        var retained = Find(workspace, "ChatComposerInput");
        Assert.Equal(identity, retained.GetRuntimeId());
        Assert.Equal(draft, ((ValuePattern)retained.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        Assert.Equal(workspaceHandle, app.HubWindowHandle);
        Capture(app, "System", $"settings-back-to-app-{activation}");

        // Back to app returns to the app page Settings was opened from, not always to chat.
        // Seed an error through the shipped MCP producer: informational notifications intentionally stay out of Inbox.
        const string notificationTitle = "Settings return focus error fixture";
        await app.SendNotificationAsync(notificationTitle, "Keep this reminder while visiting Settings.");
        await app.NavigateAsync("workspace:notifications", "NotificationsPage", "NotificationsPageMarker");
        AutomationElement? readyInboxPage = null;
        AutomationElement? readyInboxHeading = null;
        AutomationElement[] readyInboxControls = [];
        await WaitUntilAsync(() => InboxPageReady() && IsVisible(readyInboxPage!, "WorkspaceNotificationsClear") &&
            Find(readyInboxPage!, "WorkspaceNotificationsClear").Current.IsEnabled &&
            IsVisible(readyInboxPage!, "WorkspaceNotificationsList") &&
            readyInboxPage!.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "Dismiss " + notificationTitle)) is not null);
        var inboxPage = readyInboxPage!;
        Assert.True(Find(inboxPage, "WorkspaceNotificationsClear").Current.IsEnabled,
            "The real producer must first populate the shared Inbox.");
        AssertNamedInboxPage();
        await app.NavigateAsync("connection", "ConnectionPage", "ConnectionPageMarker");
        settings = AutomationElement.FromHandle(app.HubWindowHandle);
        ActivateBackToApp();
        await WaitUntilAsync(InboxPageReady);
        Assert.False(IsVisible(workspace, "ConnectionPageMarker"));
        // Require a visible, enabled Inbox control in this exact named page, including the
        // empty case whose Settings/tabs remain usable. A hidden Settings button is not a destination.
        AssertNamedInboxPage();
        await WaitUntilAsync(
            () => GetForegroundWindow() == workspaceHandle && HasVisibleNotificationsFocus(workspace),
            () => $"Notifications did not receive focus. Expected workspace foreground: {GetForegroundWindow() == workspaceHandle}. " +
                $"Target controls: {DescribeNotificationFocus(workspace)}");
        Capture(app, "System", $"settings-back-to-notifications-populated-{activation}");

        inboxPage = readyInboxPage!;
        Invoke(Find(inboxPage, "WorkspaceNotificationsClear"));
        await WaitUntilAsync(() => InboxPageReady() && !IsVisible(readyInboxPage!, "WorkspaceNotificationsClear") &&
            !IsVisible(readyInboxPage!, "WorkspaceNotificationsList") && IsVisible(readyInboxPage!, "WorkspaceNotificationsEmpty"));
        AssertNamedInboxPage();
        await app.NavigateAsync("connection", "ConnectionPage", "ConnectionPageMarker");
        settings = AutomationElement.FromHandle(app.HubWindowHandle);
        ActivateBackToApp();
        await WaitUntilAsync(InboxPageReady);
        AssertNamedInboxPage();
        Assert.True(IsVisible(readyInboxPage!, "WorkspaceNotificationsEmpty"));
        Capture(app, "System", $"settings-back-to-notifications-empty-{activation}");
        await WaitUntilAsync(
            () => GetForegroundWindow() == workspaceHandle && HasVisibleNotificationsFocus(workspace),
            () => $"Empty Notifications did not receive focus. Expected workspace foreground: {GetForegroundWindow() == workspaceHandle}. " +
                $"Target controls: {DescribeNotificationFocus(workspace)}");

        void AssertNamedInboxPage()
        {
            var page = readyInboxPage!;
            Assert.Equal(ControlType.Pane, page.Current.ControlType);
            Assert.False(string.IsNullOrWhiteSpace(page.Current.Name));
            Assert.Equal(readyInboxHeading!.Current.Name, page.Current.Name);
            Assert.Equal(2, readyInboxControls.Length);
            foreach (var control in readyInboxControls)
                Assert.True(UsableInboxControl(control, page.Current.BoundingRectangle),
                    "Inbox must retain enabled native focus destinations even without rows.");
        }

        bool InboxPageReady()
        {
            readyInboxPage = null;
            readyInboxHeading = null;
            readyInboxControls = [];
            try
            {
                var owner = workspace.Current;
                if (app.HubWindowHandle != workspaceHandle || new IntPtr(owner.NativeWindowHandle) != workspaceHandle ||
                    !UsableInboxControl(workspace, owner.BoundingRectangle)) return false;
                // Returning from Settings rebuilds the page. Resolve a fresh page in
                // the retained owner, then keep all peers from the same successful poll.
                var page = FindOrNull(workspace, "NotificationsPage");
                if (!UsableInboxControl(page, owner.BoundingRectangle)) return false;
                var bounds = page!.Current.BoundingRectangle;
                var heading = FindOrNull(page, "NotificationsPageMarker");
                var settingsControl = FindOrNull(page, "WorkspaceNotificationsSettings");
                var tabs = FindOrNull(page, "WorkspaceNotificationsTabs");
                if (!UsableInboxControl(heading, bounds) || !UsableInboxControl(settingsControl, bounds) ||
                    !UsableInboxControl(tabs, bounds)) return false;
                readyInboxPage = page;
                readyInboxHeading = heading;
                readyInboxControls = [settingsControl!, tabs!];
                return true;
            }
            catch (ElementNotAvailableException) { return false; }
        }

        bool UsableInboxControl(AutomationElement? element, System.Windows.Rect viewport)
        {
            if (element is null) return false;
            var info = element.Current;
            var bounds = info.BoundingRectangle;
            return info.ProcessId == workspaceProcessId && !info.IsOffscreen && info.IsEnabled &&
                HasInboxBounds(viewport) && HasInboxBounds(bounds) && viewport.Contains(bounds);
        }

        static bool HasInboxBounds(System.Windows.Rect bounds) =>
            !bounds.IsEmpty && double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
            double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) && bounds.Width > 0 && bounds.Height > 0;

        void ActivateBackToApp()
        {
            var button = Find(settings, "SettingsBackToApp");
            button.SetFocus();
            Assert.Equal(new IntPtr(settings.Current.NativeWindowHandle), GetForegroundWindow());
            Assert.False(HasVisibleNotificationsFocus(workspace));
            switch (activation)
            {
                case "mouse":
                    var bounds = button.Current.BoundingRectangle;
                    System.Windows.Forms.Cursor.Position = new System.Drawing.Point(
                        (int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
                    MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
                    MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
                    break;
                case "enter":
                    System.Windows.Forms.SendKeys.SendWait("{ENTER}");
                    break;
                default:
                    System.Windows.Forms.SendKeys.SendWait("{ESC}");
                    break;
            }
        }

        async Task AssertChatForegroundAsync()
        {
            await WaitUntilAsync(() => GetForegroundWindow() == workspaceHandle);
            await Task.Delay(250);
            Assert.Equal(workspaceHandle, GetForegroundWindow());
        }
    }

    [Fact]
    public async Task ConfiguredFixtureChat_SettingsSections_GlobalBackForward_RetainSameWindowComposerDraftAndFocus()
    {
        // This provider has no Gateway connection, so use local Settings sections.
        // Gateway-owned sections such as Usage are correctly pruned from disconnected history.
        using var app = new AccessibilityAppFixture(initializeAxe: false, syntheticData: true, initialRoute: null);
        await app.NavigateAsync("chat", "ChatPage", "ChatComposerInput");
        var workspaceHandle = app.HubWindowHandle;
        var root = AutomationElement.FromHandle(workspaceHandle);
        var composer = Find(root, "ChatComposerInput");
        var identity = composer.GetRuntimeId();
        const string draft = "Draft kept across Settings sections and global Back/Forward";
        ((ValuePattern)composer.GetCurrentPattern(ValuePattern.Pattern)).SetValue(draft);
        await FocusForKeyboardAsync(app, root, "ChatComposerInput");
        Capture(app, "System", "retention-01-chat-draft");

        await app.NavigateAsync("settings", "SettingsPage", "SettingsPageMarker");
        await app.NavigateAsync("permissions", "PermissionsPage", "PermissionsPageMarker");
        Assert.Equal(workspaceHandle, app.HubWindowHandle);
        Capture(app, "System", "retention-02-settings-permissions");

        await InvokeWhenEnabledAsync(root, "SettingsBack");
        await WaitUntilAsync(() => IsVisible(root, "SettingsPageMarker"));
        await InvokeWhenEnabledAsync(root, "SettingsBack");
        await WaitUntilAsync(() => IsVisible(root, "ChatComposerInput"));
        AssertComposerRetained();
        await WaitUntilAsync(() => FindOrNull(root, "ChatComposerInput")?.Current.HasKeyboardFocus == true);
        Capture(app, "System", "retention-03-back-restores-draft-focus");

        Invoke(Find(root, "WorkspaceForward"));
        await WaitUntilAsync(() => IsVisible(root, "SettingsPageMarker"));
        Assert.True(Find(root, "SettingsForward").Current.IsEnabled);
        Invoke(Find(root, "SettingsForward"));
        await WaitUntilAsync(() => IsVisible(root, "PermissionsPageMarker"));
        Capture(app, "System", "retention-04-forward-restores-permissions");
        Invoke(Find(root, "SettingsBack"));
        await WaitUntilAsync(() => IsVisible(root, "SettingsPageMarker"));
        Invoke(Find(root, "SettingsBack"));
        await WaitUntilAsync(() => IsVisible(root, "ChatComposerInput"));
        AssertComposerRetained();
        Assert.Equal(workspaceHandle, app.HubWindowHandle);
        Capture(app, "System", "retention-05-back-restores-same-composer");

        void AssertComposerRetained()
        {
            var retained = Find(root, "ChatComposerInput");
            Assert.Equal(identity, retained.GetRuntimeId());
            Assert.Equal(draft, ((ValuePattern)retained.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        }
    }

    [Fact]
    public async Task DefaultLaunchAndCompanionRefocus_PreserveNativeComposerDraft()
    {
        // Synthetic provider exercises the real composer without sending gateway traffic.
        using var app = new AccessibilityAppFixture(initializeAxe: false, syntheticData: true, initialRoute: null);
        var workspace = app.HubWindowHandle;
        Assert.NotNull(Find(AutomationElement.FromHandle(workspace), "WorkspaceOwner"));
        await app.NavigateAsync("chat", "ChatPage", "ChatComposerInput");
        Assert.Equal(workspace, app.HubWindowHandle);
        var composer = Find(AutomationElement.FromHandle(workspace), "ChatComposerInput");
        var identity = composer.GetRuntimeId();
        const string draft = "Unsent native Workspace regression draft";
        ((ValuePattern)composer.GetCurrentPattern(ValuePattern.Pattern)).SetValue(draft);

        foreach (var (route, page, marker) in new[]
        {
            ("settings", "SettingsPage", "SettingsPageMarker"),
            ("usage", "UsagePage", "UsagePageMarker")
        })
        {
            await app.NavigateAsync(route, page, marker);
            Assert.Equal(workspace, app.HubWindowHandle);
            var settingsRoot = AutomationElement.FromHandle(workspace);
            Invoke(Find(settingsRoot, "SettingsBack"));
            await WaitUntilAsync(() => IsVisible(settingsRoot, "ChatComposerInput"));
            Assert.Equal(workspace, app.HubWindowHandle);
            var retained = Find(AutomationElement.FromHandle(workspace), "ChatComposerInput");
            Assert.Equal(identity, retained.GetRuntimeId());
            Assert.Equal(draft, ((ValuePattern)retained.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        }
        await app.NavigateAsync("workspace:notifications", "NotificationsPage", "NotificationsPageMarker");
        await app.RefocusWorkspaceAsync();
        Assert.NotNull(Find(AutomationElement.FromHandle(workspace), "NotificationsPageMarker"));
        await app.NavigateAsync("chat", "ChatPage", "ChatComposerInput");
        Assert.Equal(draft, ((ValuePattern)Find(AutomationElement.FromHandle(workspace), "ChatComposerInput")
            .GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        var root = AutomationElement.FromHandle(workspace);
        Invoke(Find(root, "WorkspaceBack"));
        await WaitUntilAsync(() => IsVisible(root, "NotificationsPageMarker"));
        Invoke(Find(root, "WorkspaceForward"));
        await WaitUntilAsync(() => IsVisible(root, "ChatComposerInput"));
        Assert.Equal(identity, Find(root, "ChatComposerInput").GetRuntimeId());
        Assert.Equal(draft, ((ValuePattern)Find(root, "ChatComposerInput")
            .GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task CompletedConversation_RemainsVisibleAfterSidebarRefresh(string theme)
    {
        using var app = new AccessibilityAppFixture(initializeAxe: false, theme: theme,
            syntheticData: true, initialRoute: "sessions");
        await app.NavigateAsync("chat", "ChatPage", "ChatComposerInput");
        var root = AutomationElement.FromHandle(app.HubWindowHandle);
        const string completedId = "WorkspaceSession:agent:main:completed-cleanup";
        Assert.True(IsVisible(root, completedId));
        Assert.Equal("Completed cleanup", Find(root, completedId).Current.Name);
        Assert.False(IsVisible(root, "WorkspaceSession:agent:main:cron:nightly-cleanup"));
        var input = Find(root, "ChatComposerInput");
        const string draft = "Continue a conversation after its last run finishes";
        ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).SetValue(draft);
        await app.NavigateAsync("sessions", "SessionsPage", "SessionsPageMarker");
        Invoke(Find(root, "SettingsBack"));
        await WaitUntilAsync(() => IsVisible(root, "ChatComposerInput"));
        await WaitUntilAsync(() => IsVisible(root, completedId));
        Assert.Equal(draft, ((ValuePattern)Find(root, "ChatComposerInput")
            .GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        Capture(app, theme, "completed-conversations");
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task SidebarSessions_SelectOriginalKeys_AndKeepCompanionDraft(string theme)
    {
        using var app = new AccessibilityAppFixture(initializeAxe: false, theme: theme, syntheticData: true, initialRoute: "sessions");
        await app.NavigateAsync("chat", "ChatPage", "ChatComposerInput");
        var workspace = app.HubWindowHandle;
        var root = AutomationElement.FromHandle(workspace);
        var composerIdentity = Find(root, "ChatComposerInput").GetRuntimeId();
        foreach (var key in new[] { "agent:main:fork", "agent:main:main", "agent:main:fork" })
        {
            var id = key == "agent:main:main" ? "WorkspaceNavHome" : $"WorkspaceSession:{key}";
            var item = Find(root, id);
            Assert.False(item.Current.IsOffscreen);
            ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            await WaitUntilAsync(() => SelectionState(root, id) == true);
            await WaitUntilAsync(() => root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                .Cast<AutomationElement>().Any(text => text.Current.Name.Contains($"Session: {key}.", StringComparison.Ordinal)));
            Assert.Equal(composerIdentity, Find(root, "ChatComposerInput").GetRuntimeId());
            await WaitUntilAsync(() => SelectionState(root, "WorkspaceNavHome") == (key == "agent:main:main"));
        }
        var input = Find(root, "ChatComposerInput");
        const string draft = "Keep the selected session draft while opening Settings";
        ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).SetValue(draft);
        await app.NavigateAsync("settings", "SettingsPage", "SettingsPageMarker");
        Assert.Equal(workspace, app.HubWindowHandle);
        Invoke(Find(root, "SettingsBack"));
        await WaitUntilAsync(() => IsVisible(root, "ChatComposerInput"));
        await WaitUntilAsync(() => SelectionState(root, "WorkspaceSession:agent:main:fork") == true);
        Assert.Equal(draft, ((ValuePattern)Find(root, "ChatComposerInput").GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        var composerId = Find(root, "ChatComposerInput").GetRuntimeId();
        await FocusForKeyboardAsync(app, root, "WorkspaceTogglePane");
        // This test covers session/draft preservation. NativePagesAndOwnerLinks_StayInOneWorkspaceWindow
        // separately requires Space activation and keyboard focus transfer after the Settings round-trip.
        Invoke(Find(root, "WorkspaceTogglePane"));
        await WaitUntilAsync(() => IsVisible(root, "WorkspaceReopenPane"));
        Capture(app, theme, "sidebar-hidden");
        await app.NavigateAsync("chat", "ChatPage", "ChatComposerInput");
        Assert.Equal(composerId, Find(root, "ChatComposerInput").GetRuntimeId());
        Assert.Equal(draft, ((ValuePattern)Find(root, "ChatComposerInput").GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        Assert.True(Find(root, "ChatComposerInput").Current.BoundingRectangle.Top >=
            Find(root, "WorkspaceReopenPane").Current.BoundingRectangle.Bottom);
        Invoke(Find(root, "WorkspaceReopenPane"));
        await WaitUntilAsync(() => IsVisible(root, "WorkspaceOwner"));
        await WaitUntilAsync(() => SelectionState(root, "WorkspaceSession:agent:main:fork") == true);
        Assert.Equal(draft, ((ValuePattern)Find(root, "ChatComposerInput").GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        await app.NavigateAsync("chat", "ChatPage", "ChatComposerInput");
        await app.NavigateAsync("sessions", "SessionsPage", "SessionsPageMarker");
        Invoke(Find(root, "SettingsBack"));
        await WaitUntilAsync(() => IsVisible(root, "ChatComposerInput"));
        await WaitUntilAsync(() => SelectionState(root, "WorkspaceSession:agent:main:fork") == true);
        await WaitUntilAsync(() => IsVisible(root, "WorkspaceNotifications"));
        Invoke(Find(root, "WorkspaceNotifications"));
        await WaitUntilAsync(() => FindPopup(root, "WorkspaceNotificationsOpenPage") is not null);
        Assert.Equal(composerId, Find(root, "ChatComposerInput").GetRuntimeId());
        Assert.Equal(draft, ((ValuePattern)Find(root, "ChatComposerInput").GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        Assert.False(IsVisible(root, "NotificationsPageMarker"));
        CaptureFlyout(root, theme, "notifications-flyout");
        System.Windows.Forms.SendKeys.SendWait("{ESC}");
        await WaitUntilAsync(() => FindPopup(root, "WorkspaceNotificationsOpenPage") is null);
        Invoke(Find(root, "WorkspaceOwner"));
        await ExpandOwnerGatewayMenuAsync(root);
        Assert.Equal("Connection Status: Disconnected", FindPopup(root, "WorkspaceOwnerConnectionStatus")!.Current.Name);
        Invoke(FindPopup(root, "WorkspaceOwnerConnectionStatus")!);
        await WaitUntilAsync(() => FindPopup(root, "GatewayStatusOpenConnection") is not null);
        Assert.Equal(composerId, Find(root, "ChatComposerInput").GetRuntimeId());
        Assert.Equal(draft, ((ValuePattern)Find(root, "ChatComposerInput").GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        CaptureFlyout(root, theme, "owner-connection-status");
        Invoke(FindPopup(root, "GatewayStatusOpenConnection")!);
        await WaitUntilAsync(() => IsVisible(root, "ConnectionPageMarker"));
        Invoke(Find(root, "SettingsBack"));
        await WaitUntilAsync(() => IsVisible(root, "ChatComposerInput"));
        Assert.Equal(draft, ((ValuePattern)Find(root, "ChatComposerInput").GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        Invoke(Find(root, "WorkspaceNotifications"));
        await WaitUntilAsync(() => FindPopup(root, "WorkspaceNotificationsOpenPage") is not null);
        Invoke(FindPopup(root, "WorkspaceNotificationsOpenPage")!);
        await WaitUntilAsync(() => IsVisible(root, "NotificationsPageMarker"));
        Invoke(Find(root, "WorkspaceBack"));
        await WaitUntilAsync(() => IsVisible(root, "ChatComposerInput"));
        await WaitUntilAsync(() => SelectionState(root, "WorkspaceSession:agent:main:fork") == true);
        await app.NavigateAsync("chat", "ChatPage", "ChatComposerInput");
        // Same-page chat navigation can acknowledge the existing composer while an adaptive
        // NavigationView transition has temporarily closed the pane. Restore it before querying
        // pane-owned controls so UI Automation does not race the NavigationView subtree.
        await EnsureWorkspacePaneOpenAsync(root);
        ((SelectionItemPattern)Find(root, "WorkspaceNavHome").GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        await WaitUntilAsync(() => SelectionState(root, "WorkspaceNavHome") == true);
        Assert.Equal(draft, ((ValuePattern)Find(root, "ChatComposerInput").GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task NativePagesAndOwnerLinks_StayInOneWorkspaceWindow(string theme)
    {
        using var app = new AccessibilityAppFixture(initializeAxe: false, theme: theme, syntheticData: false);
        await app.NavigateAsync("chat", "ChatPage", "WorkspaceOwner");
        var workspace = app.HubWindowHandle;
        var workspaceElement = AutomationElement.FromHandle(workspace);
        var pid = workspaceElement.Current.ProcessId;
        var owner = Find(workspaceElement, "WorkspaceOwner");
        var notifications = Find(workspaceElement, "WorkspaceNotifications");
        Assert.True(notifications.Current.BoundingRectangle.Left >= owner.Current.BoundingRectangle.Right);
        Assert.False(Find(workspaceElement, "WorkspaceSessionsAdd").Current.IsEnabled);
        var sessionsHeader = FindHeading(workspaceElement, "Sessions").Current.BoundingRectangle;
        var initialHomeBounds = Find(workspaceElement, "WorkspaceNavHome").Current.BoundingRectangle;
        var customize = Find(workspaceElement, "WorkspacePagesCustomize").Current.BoundingRectangle;
        var scale = Find(workspaceElement, "WorkspaceTogglePane").Current.BoundingRectangle.Width / 40;
        Assert.InRange((customize.Top - initialHomeBounds.Top) / scale, -1, 4);
        Assert.True(customize.Right <= initialHomeBounds.Right + 1);
        var defaultPin = Find(workspaceElement, "WorkspacePinnedPage:dashboards");
        Assert.Equal(pid, defaultPin.Current.ProcessId);
        Assert.False(defaultPin.Current.IsOffscreen);
        var defaultPinBounds = defaultPin.Current.BoundingRectangle;
        Assert.InRange((defaultPinBounds.Top - initialHomeBounds.Bottom) / scale, -1, 8);
        // The decorative separator follows the default pin, not Home. Keep the same gap contract.
        Assert.InRange((sessionsHeader.Top - defaultPinBounds.Bottom) / scale, 4, 48);
        foreach (var obsolete in new[] { "Pages", "Pin pages here with the customize button." })
            Assert.Null(workspaceElement.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, obsolete)));
        foreach (var id in new[] { "WorkspaceNavAgents", "WorkspaceNavDashboards", "WorkspaceNavSystems",
            "WorkspaceNavAutomations", "WorkspaceNavPlugins", "WorkspaceMore", "WorkspacePageHeading" })
            Assert.Null(workspaceElement.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id)));
        var backBounds = Find(workspaceElement, "WorkspaceBack").Current.BoundingRectangle;
        var forwardBounds = Find(workspaceElement, "WorkspaceForward").Current.BoundingRectangle;
        Assert.Equal(backBounds.Top, forwardBounds.Top);
        var toggleBounds = Find(workspaceElement, "WorkspaceTogglePane").Current.BoundingRectangle;
        var toggleOffsetY = toggleBounds.Top - Find(workspaceElement, "WorkspaceTitleBar").Current.BoundingRectangle.Bottom;
        Assert.Equal(toggleBounds.Top, backBounds.Top);
        var assistantBounds = Find(workspaceElement, "WorkspaceAssistantSelector").Current.BoundingRectangle;
        Assert.True(assistantBounds.Top >= toggleBounds.Bottom);
        Assert.True(forwardBounds.Left >= backBounds.Right);
        Assert.InRange(forwardBounds.Right, assistantBounds.Left, assistantBounds.Right + 1);
        Assert.True(forwardBounds.Bottom <= assistantBounds.Top);
        Assert.False(Find(workspaceElement, "WorkspaceForward").Current.IsEnabled);
        var dropdown = (ExpandCollapsePattern)Find(workspaceElement, "WorkspaceAssistantSelector")
            .GetCurrentPattern(ExpandCollapsePattern.Pattern);
        dropdown.Expand();
        await WaitUntilAsync(() => ProcessWindows(pid).Cast<AutomationElement>().Any(window => window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspaceNewAgent")) is not null));
        var newAgent = ProcessWindows(pid).Cast<AutomationElement>().Select(window => window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspaceNewAgent"))).First(item => item is not null)!;
        Assert.Equal("New agent", newAgent.Current.Name);
        Assert.True(newAgent.Current.IsEnabled);
        dropdown.Collapse();
        Capture(app, theme, "workspace");

        foreach (var route in new[] { "agents", "agent-detail", "writer-detail", "dashboards", "dashboard-detail", "canvas",
            "systems", "system-detail", "automations", "automation-detail", "plugins", "sessions", "skills",
            "usage", "activity", "tasks", "meetings", "apps", "portals", "more" })
        {
            await app.NavigateAsync($"workspace:{route}", "ChatPage", "WorkspaceNavHome");
            Assert.Equal(workspace, app.HubWindowHandle);
            Assert.True(((SelectionItemPattern)Find(workspaceElement, "WorkspaceNavHome")
                .GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected);
            Assert.False(IsVisible(workspaceElement, "WorkspacePageHeading"));
        }
        await app.NavigateAsync("workspace:notifications", "NotificationsPage", "WorkspaceNotifications");
        Capture(app, theme, "notifications");

        foreach (var (label, route, page, marker) in new[]
        {
            ("Settings", "settings", "SettingsPage", "SettingsPageMarker"),
            ("Usage", "usage", "UsagePage", "UsagePageMarker"),
            ("About OpenClaw", "about", "AboutPage", "AboutPageMarker")
        })
        {
            await app.NavigateAsync("workspace:home", "ChatPage", "WorkspaceNavHome");
            // Usage left the account menu; Settings is top level and About lives under Help & about.
            if (route != "usage")
            {
                var root = AutomationElement.FromHandle(workspace);
                Invoke(Find(root, "WorkspaceOwner"));
                if (route == "about")
                    await ExpandOwnerHelpMenuAsync(root);
                Invoke(WaitForMenuItem(pid, label));
                var destination = await WaitForMarkerAsync(pid, marker, IntPtr.Zero);
                Assert.Equal(workspace, new IntPtr(destination.Current.NativeWindowHandle));
                Assert.Single(ProcessWindows(pid).Cast<AutomationElement>(),
                    window => window.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsBack")) is not null);
            }
            await app.NavigateAsync(route, page, marker);
            Assert.Equal(workspace, app.HubWindowHandle);
            Capture(app, theme, route);
        }
        foreach (var (route, page, marker) in new[]
        {
            ("cron", "CronPage", "CronPageMarker"),
            ("sessions", "SessionsPage", "SessionsPageMarker")
        })
        {
            await app.NavigateAsync(route, page, marker);
            Assert.Equal(workspace, app.HubWindowHandle);
        }
        await app.NavigateAsync("workspace:home", "ChatPage", "WorkspaceNavHome");
        Assert.Equal(workspace, app.HubWindowHandle);

        Invoke(Find(workspaceElement, "WorkspaceOwner"));
        await ExpandOwnerHelpMenuAsync(workspaceElement);
        Assert.True(WaitForMenuItem(pid, "Get apps").Current.IsEnabled);
        Invoke(WaitForMenuItem(pid, "Connection event timeline"));
        var timeline = await WaitForMarkerAsync(pid, "ConnectionTimelineHeading", workspace);
        var timelineHandle = new IntPtr(timeline.Current.NativeWindowHandle);
        Assert.NotEqual(app.HubWindowHandle, timelineHandle);
        var workArea = System.Windows.Forms.Screen.FromHandle(workspace).WorkingArea;
        var timelineBounds = timeline.Current.BoundingRectangle;
        Assert.InRange(timelineBounds.Right, workArea.Right - 32, workArea.Right);
        Assert.True(timelineBounds.Left >= workArea.Left,
            $"Right-aligned timeline must fit the display: {timelineBounds}, work area {workArea}.");
        await app.RefocusWorkspaceAsync();
        Invoke(Find(workspaceElement, "WorkspaceOwner"));
        await ExpandOwnerHelpMenuAsync(workspaceElement);
        Invoke(WaitForMenuItem(pid, "Connection event timeline"));
        Assert.Equal(timelineHandle, new IntPtr((await WaitForMarkerAsync(pid, "ConnectionTimelineHeading", workspace)).Current.NativeWindowHandle));
        ((WindowPattern)timeline.GetCurrentPattern(WindowPattern.Pattern)).Close();
        Assert.NotNull(Find(AutomationElement.FromHandle(workspace), "WorkspaceOwner"));

        var nativeNavigation = Find(workspaceElement, "WorkspaceNavigation");
        Invoke(Find(workspaceElement, "WorkspaceNotifications"));
        await WaitUntilAsync(() => FindPopup(workspaceElement, "WorkspaceNotificationsOpenPage") is not null);
        var clear = FindPopup(workspaceElement, "WorkspaceNotificationsClear");
        if (clear is not null)
        {
            Invoke(clear);
            await WaitUntilAsync(() => FindPopup(workspaceElement, "WorkspaceNotificationsEmpty") is not null);
        }
        System.Windows.Forms.SendKeys.SendWait("{ESC}");
        await WaitUntilAsync(() => FindPopup(workspaceElement, "WorkspaceNotificationsOpenPage") is null);
        await Task.Delay(350);
        Invoke(Find(workspaceElement, "WorkspaceNotifications"));
        await WaitUntilAsync(() => FindPopup(workspaceElement, "WorkspaceNotificationsOpenPage") is not null);
        await WaitUntilAsync(() => FindPopup(workspaceElement, "WorkspaceNotificationsEmpty") is not null);
        Assert.False(IsVisible(workspaceElement, "NotificationsPageMarker"));
        Invoke(FindPopup(workspaceElement, "WorkspaceNotificationsOpenPage")!);
        await WaitUntilAsync(() => IsVisible(workspaceElement, "NotificationsPageMarker"));
        Invoke(Find(workspaceElement, "WorkspaceBack"));
        await WaitUntilAsync(() => !IsVisible(workspaceElement, "NotificationsPageMarker"));
        Assert.True(Find(workspaceElement, "WorkspaceForward").Current.IsEnabled);
        Invoke(Find(workspaceElement, "WorkspaceForward"));
        await WaitUntilAsync(() => IsVisible(workspaceElement, "NotificationsPageMarker"));
        Assert.False(Find(workspaceElement, "WorkspaceForward").Current.IsEnabled);
        ((SelectionItemPattern)Find(nativeNavigation, "WorkspaceNavHome").GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        await WaitUntilAsync(() => !IsVisible(workspaceElement, "NotificationsPageMarker"));
        Assert.False(Find(workspaceElement, "WorkspaceForward").Current.IsEnabled);

        var collapseTime = Stopwatch.StartNew();
        // TitleBar keeps its compact height whether or not its RightHeader actions are shown.
        var expandedTitleHeight = Find(workspaceElement, "WorkspaceTitleBar").Current.BoundingRectangle.Height;
        Assert.True(expandedTitleHeight < Find(workspaceElement, "WorkspaceTogglePane").Current.BoundingRectangle.Height,
            $"Title bar {expandedTitleHeight}px must stay compact (32 DIP), shorter than the 40 DIP pane toggle.");
        Invoke(Find(workspaceElement, "WorkspaceTogglePane"));
        await WaitUntilAsync(() => !IsVisible(workspaceElement, "WorkspaceOwner") &&
            !IsVisible(workspaceElement, "WorkspaceNotifications") &&
            !IsVisible(workspaceElement, "WorkspaceSessionsAdd") &&
            IsVisible(workspaceElement, "WorkspaceReopenPane"));
        Assert.InRange(collapseTime.ElapsedMilliseconds, 0, 500);
        // While Chat's pane is hidden, Dashboard and the bell move to the title bar; the account does not.
        await WaitUntilAsync(() => IsVisible(workspaceElement, "WorkspaceTitleBarDashboard") &&
            IsVisible(workspaceElement, "WorkspaceTitleBarNotifications"));
        Assert.False(IsVisible(workspaceElement, "WorkspaceTitleBarOwner"));
        Assert.Equal(expandedTitleHeight, Find(workspaceElement, "WorkspaceTitleBar").Current.BoundingRectangle.Height);
        Assert.True(Find(workspaceElement, "WorkspaceTitleBarNotifications").Current.BoundingRectangle.Bottom <=
            Find(workspaceElement, "WorkspaceTitleBar").Current.BoundingRectangle.Bottom + 1);
        // A real pointer click (not UIA Invoke) proves the button is outside the title-bar drag region.
        await Task.Delay(300);
        var bellBounds = Find(workspaceElement, "WorkspaceTitleBarNotifications").Current.BoundingRectangle;
        System.Windows.Forms.Cursor.Position = new System.Drawing.Point(
            (int)(bellBounds.Left + bellBounds.Width / 2), (int)(bellBounds.Top + bellBounds.Height / 2));
        MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
        MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
        await WaitUntilAsync(() => FindPopup(workspaceElement, "WorkspaceNotificationsOpenPage") is not null);
        System.Windows.Forms.SendKeys.SendWait("{ESC}");
        await WaitUntilAsync(() => FindPopup(workspaceElement, "WorkspaceNotificationsOpenPage") is null);
        await app.NavigateAsync("workspace:home", "ChatPage", "WorkspaceReopenPane");
        await WaitUntilAsync(() =>
        {
            var reopen = Find(workspaceElement, "WorkspaceReopenPane").Current.BoundingRectangle;
            var content = FindHeading(nativeNavigation, "Connect to gateway to start chatting").Current.BoundingRectangle;
            var navigationBounds = nativeNavigation.Current.BoundingRectangle;
            return reopen.Bottom <= content.Top &&
                Math.Abs((content.Left + content.Right) / 2 -
                    (navigationBounds.Left + navigationBounds.Right) / 2) <= 2;
        });
        var reopenBounds = Find(workspaceElement, "WorkspaceReopenPane").Current.BoundingRectangle;
        Assert.InRange(Math.Abs(reopenBounds.Width - toggleBounds.Width), 0, 1);
        Assert.InRange(Math.Abs(reopenBounds.Height - toggleBounds.Height), 0, 1);
        Assert.InRange(Math.Abs(reopenBounds.Top -
            Find(workspaceElement, "WorkspaceTitleBar").Current.BoundingRectangle.Bottom - toggleOffsetY), 0, 1);
        foreach (var id in new[] { "WorkspaceNavHome", "WorkspaceOwner", "WorkspaceNotifications",
            "WorkspaceSessionsAdd", "WorkspaceAssistantSelector", "WorkspaceTogglePane", "WorkspaceBack", "WorkspaceForward" })
            Assert.False(IsVisible(workspaceElement, id), $"Hidden sidebar control '{id}' is still visible.");
        Assert.False(IsVisible(workspaceElement, "WorkspaceCompactNotifications"));
        Assert.True(reopenBounds.Top >= Find(workspaceElement, "WorkspaceTitleBar").Current.BoundingRectangle.Bottom);
        Assert.InRange(reopenBounds.Left - nativeNavigation.Current.BoundingRectangle.Left, 0, 12);
        await app.NavigateAsync("workspace:notifications", "NotificationsPage", "WorkspaceReopenPane");
        Assert.Equal(workspace, app.HubWindowHandle);
        await FocusForKeyboardAsync(app, workspaceElement, "WorkspaceReopenPane");
        System.Windows.Forms.SendKeys.SendWait(" ");
        await WaitUntilAsync(() => IsVisible(workspaceElement, "WorkspaceOwner") &&
            IsVisible(workspaceElement, "WorkspaceNotifications") &&
            IsVisible(workspaceElement, "WorkspaceSessionsAdd") &&
            !IsVisible(workspaceElement, "WorkspaceReopenPane"));
        await WaitUntilAsync(() => !IsVisible(workspaceElement, "WorkspaceTitleBarNotifications") &&
            !IsVisible(workspaceElement, "WorkspaceTitleBarDashboard"));
        await WaitUntilAsync(() => Find(workspaceElement, "WorkspaceTogglePane").Current.HasKeyboardFocus);
        Assert.False(IsVisible(workspaceElement, "WorkspaceReopenPane"));

        ((WindowPattern)workspaceElement.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Normal);
        ((TransformPattern)workspaceElement.GetCurrentPattern(TransformPattern.Pattern)).Resize(1050, 760);
        await app.NavigateAsync("workspace:home", "ChatPage", "WorkspaceNavHome");
        Assert.True(IsVisible(workspaceElement, "WorkspaceOwner"));
        Assert.True(IsVisible(workspaceElement, "WorkspaceNotifications"));
        Assert.True(IsVisible(workspaceElement, "WorkspaceAssistantSelector"));
        Assert.True(IsVisible(workspaceElement, "WorkspaceSessionsAdd"));
        Assert.Equal(Find(workspaceElement, "WorkspaceTogglePane").Current.BoundingRectangle.Top,
            Find(workspaceElement, "WorkspaceForward").Current.BoundingRectangle.Top);
        Invoke(Find(workspaceElement, "WorkspaceOwner"));
        var help = WaitForMenuItem(pid, "Help & about");
        ((ExpandCollapsePattern)help.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        Assert.True(WaitForMenuItem(pid, "GitHub").Current.IsEnabled);
        ((ExpandCollapsePattern)help.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
        Invoke(WaitForMenuItem(pid, "Settings"));
        Assert.NotNull(await WaitForMarkerAsync(pid, "SettingsPageMarker", IntPtr.Zero));
        await InvokeWhenEnabledAsync(workspaceElement, "SettingsBack");
        await WaitUntilAsync(() => IsVisible(workspaceElement, "WorkspaceTogglePane"));
        await FocusForKeyboardAsync(app, workspaceElement, "WorkspaceTogglePane");
        System.Windows.Forms.SendKeys.SendWait(" ");
        await WaitUntilAsync(() => IsVisible(workspaceElement, "WorkspaceReopenPane") &&
            Find(workspaceElement, "WorkspaceReopenPane").Current.HasKeyboardFocus);
        ((TransformPattern)workspaceElement.GetCurrentPattern(TransformPattern.Pattern)).Resize(1000, 720);
        await app.NavigateAsync("workspace:home", "ChatPage", "WorkspaceReopenPane");
        Assert.False(IsVisible(workspaceElement, "WorkspaceOwner"));
        await WaitUntilAsync(() =>
        {
            var content = FindHeading(nativeNavigation, "Connect to gateway to start chatting").Current.BoundingRectangle;
            var bounds = nativeNavigation.Current.BoundingRectangle;
            return Math.Abs((content.Left + content.Right) / 2 - (bounds.Left + bounds.Right) / 2) <= 2;
        });
        await FocusForKeyboardAsync(app, workspaceElement, "WorkspaceReopenPane");
        System.Windows.Forms.SendKeys.SendWait("{TAB}");
        Assert.NotEqual("WorkspaceReopenPane", AutomationElement.FocusedElement.Current.AutomationId);
        Assert.DoesNotContain(AutomationElement.FocusedElement.Current.AutomationId,
            new[] { "WorkspaceOwner", "WorkspaceNotifications", "WorkspaceAssistantSelector", "WorkspaceNavHome", "WorkspaceSessionsAdd" });
        Invoke(Find(workspaceElement, "WorkspaceReopenPane"));
        await WaitUntilAsync(() => IsVisible(workspaceElement, "WorkspaceOwner"));
    }

    private static bool IsVisible(AutomationElement root, string id) =>
        FindOrNull(root, id) is { } element && !element.Current.IsOffscreen && element.Current.BoundingRectangle.Width > 0;

    private static bool HasVisibleNotificationsFocus(AutomationElement workspace)
    {
        try
        {
            var page = FindOrNull(workspace, "NotificationsPage");
            if (page is null || page.Current.IsOffscreen ||
                AutomationElement.FocusedElement is not { } focused || focused.Current.IsOffscreen ||
                !focused.Current.IsEnabled || focused.Current.BoundingRectangle.Width <= 0 ||
                focused.Current.BoundingRectangle.Height <= 0)
                return false;
            // Resolve peers inside the current page, not a footer flyout or another window
            // with the same IDs. Empty Inbox still has enabled Settings and category controls.
            var targets = new[] { "WorkspaceNotificationsClear", "WorkspaceNotificationsSettings",
                "WorkspaceNotificationsTabs", "WorkspaceNotificationsList" }
                .Select(id => FindOrNull(page, id)).OfType<AutomationElement>()
                .Where(element => !element.Current.IsOffscreen && element.Current.IsEnabled)
                .ToArray();
            for (var current = focused; current is not null; current = TreeWalker.ControlViewWalker.GetParent(current))
            {
                if (Automation.Compare(current, page)) return false;
                if (targets.Any(target => Automation.Compare(current, target))) return true;
            }
            return false;
        }
        catch (ElementNotAvailableException) { return false; }
    }

    private static string DescribeNotificationFocus(AutomationElement workspace)
    {
        // Only report known fixture controls and boolean state. UI names, data-derived IDs and
        // the focused element in another application must never enter failure logs.
        return string.Join("; ", new[] { "NotificationsPage", "WorkspaceNotificationsClear", "WorkspaceNotificationsList",
            "WorkspaceNotificationsSettings", "WorkspaceNotificationsTabs", "WorkspaceNotificationsEmpty", "SettingsBackToApp" }.Select(id =>
        {
            try
            {
                var element = FindOrNull(workspace, id);
                if (element is null) return $"{id}: absent";
                var info = element.Current;
                return $"{id}: enabled={info.IsEnabled}, offscreen={info.IsOffscreen}, focused={info.HasKeyboardFocus}";
            }
            catch (ElementNotAvailableException) { return $"{id}: unavailable"; }
        }));
    }

    /// <summary>Returns null while the item is missing or stale, e.g. while the sidebar rebuilds session items.</summary>
    private static bool? SelectionState(AutomationElement root, string id)
    {
        try
        {
            return FindOrNull(root, id) is { } element &&
                element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
                ? ((SelectionItemPattern)pattern).Current.IsSelected
                : null;
        }
        catch (ElementNotAvailableException) { return null; }
    }

    /// <summary>SendKeys targets the foreground window, so focus the control only after the Hub owns the foreground.</summary>
    private static async Task FocusForKeyboardAsync(AccessibilityAppFixture app, AutomationElement root, string id)
    {
        await app.EnsureHubForegroundAsync();
        // Let WinUI finish restoring activation focus before moving it.
        await Task.Delay(250);
        Find(root, id).SetFocus();
        await WaitUntilAsync(() =>
        {
            try
            {
                return GetForegroundWindow() == app.HubWindowHandle &&
                    FindOrNull(root, id)?.Current.HasKeyboardFocus == true;
            }
            catch (ElementNotAvailableException) { return false; }
        });
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, Func<string>? failureMessage = null)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (predicate()) return;
            await Task.Delay(100);
        }
        if (!predicate())
            Assert.Fail(failureMessage?.Invoke() ?? "Native navigation did not reach the expected state.");
    }

    private static async Task EnsureWorkspacePaneOpenAsync(AutomationElement root)
    {
        var timeout = Stopwatch.StartNew();
        TimeSpan? visibleSince = null;
        var reopenInvoked = false;
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (IsVisible(root, "WorkspaceNavHome"))
            {
                reopenInvoked = false;
                visibleSince ??= timeout.Elapsed;
                if (timeout.Elapsed - visibleSince >= TimeSpan.FromMilliseconds(500)) return;
            }
            else
            {
                visibleSince = null;
                if (!reopenInvoked && IsVisible(root, "WorkspaceReopenPane"))
                {
                    Invoke(Find(root, "WorkspaceReopenPane"));
                    reopenInvoked = true;
                }
            }

            await Task.Delay(100);
        }

        Assert.Fail("The workspace pane did not remain open after navigation.");
    }

    private static AutomationElement? FindOrNull(AutomationElement root, string id) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private static AutomationElement Find(AutomationElement root, string id) =>
        FindOrNull(root, id) ?? throw new InvalidOperationException($"Missing native control '{id}'.");

    private static AutomationElement FindHeading(AutomationElement root, string name) =>
        root.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
            new PropertyCondition(AutomationElement.NameProperty, name)))
        ?? throw new InvalidOperationException($"Missing native heading '{name}'.");

    private static void Invoke(AutomationElement element) =>
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    private static async Task InvokeWhenEnabledAsync(AutomationElement root, string id)
    {
        InvokePattern? readyInvoke = null;
        await WaitUntilAsync(() =>
        {
            try
            {
                var element = FindOrNull(root, id);
                if (element is null) return false;
                var info = element.Current;
                if (!info.IsEnabled || info.IsOffscreen ||
                    info.BoundingRectangle.Width <= 0 || info.BoundingRectangle.Height <= 0 ||
                    !element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                    return false;
                readyInvoke = (InvokePattern)pattern;
                return true;
            }
            catch (ElementNotAvailableException) { return false; }
        });
        // A fresh tree traversal can miss this still-live peer while WinUI updates its UIA tree.
        // Invoke the observed pattern once; never retry an action whose outcome may be unknown.
        readyInvoke!.Invoke();
    }

    private static AutomationElement WaitForMenuItem(int pid, string name)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            foreach (AutomationElement window in ProcessWindows(pid))
            {
                var item = window.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.NameProperty, name),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)));
                if (item is not null) return item;
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException($"Owner menu item '{name}' did not open.");
    }

    private static async Task<AutomationElement> WaitForMarkerAsync(int pid, string marker, IntPtr exclude)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            foreach (AutomationElement window in ProcessWindows(pid))
            {
                if (exclude != IntPtr.Zero && new IntPtr(window.Current.NativeWindowHandle) == exclude) continue;
                if (window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, marker)) is not null)
                    return window;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"Companion did not display '{marker}'.");
    }

    private static AutomationElementCollection ProcessWindows(int pid) =>
        AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, pid));

    private static AutomationElement? FindPopup(AutomationElement root, string id) =>
        ProcessWindows(root.Current.ProcessId).Cast<AutomationElement>()
            .Select(window => window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id)))
            .FirstOrDefault(element => element is not null && !element.Current.IsOffscreen);

    /// <summary>Expands the account menu's Gateway submenu, where the connection status row lives.</summary>
    private static async Task ExpandOwnerGatewayMenuAsync(AutomationElement root)
    {
        await WaitUntilAsync(() => FindPopup(root, "WorkspaceOwnerGateway") is not null);
        ((ExpandCollapsePattern)FindPopup(root, "WorkspaceOwnerGateway")!
            .GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        await WaitUntilAsync(() => FindPopup(root, "WorkspaceOwnerConnectionStatus") is not null);
        Assert.Empty(FindPopup(root, "WorkspaceOwnerConnectionStatus")!.Current.AcceleratorKey);
    }

    /// <summary>Expands the account menu's Help &amp; about submenu (docs, apps, timeline, About).</summary>
    private static async Task ExpandOwnerHelpMenuAsync(AutomationElement root)
    {
        await WaitUntilAsync(() => FindPopup(root, "WorkspaceOwnerHelp") is not null);
        ((ExpandCollapsePattern)FindPopup(root, "WorkspaceOwnerHelp")!
            .GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        await WaitUntilAsync(() => FindPopup(root, "WorkspaceOwnerAbout") is not null);
        Assert.Empty(FindPopup(root, "WorkspaceOwnerAbout")!.Current.AcceleratorKey);
    }

    private static void CaptureFlyout(AutomationElement root, string theme, string name)
    {
        var directory = ProofDirectory;
        if (string.IsNullOrEmpty(directory)) return;
        Thread.Sleep(350);
        Assert.Equal(root.Current.ProcessId, AutomationElement.FromHandle(GetForegroundWindow()).Current.ProcessId);
        var bounds = root.Current.BoundingRectangle;
        var rectangle = System.Drawing.Rectangle.Intersect(
            new System.Drawing.Rectangle((int)bounds.Left, (int)bounds.Top, (int)bounds.Width, (int)bounds.Height),
            System.Windows.Forms.SystemInformation.VirtualScreen);
        using var bitmap = new System.Drawing.Bitmap(rectangle.Width, rectangle.Height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(rectangle.Location, System.Drawing.Point.Empty, rectangle.Size);
        Directory.CreateDirectory(directory);
        bitmap.Save(Path.Combine(directory, $"{theme}-{name}.png"), System.Drawing.Imaging.ImageFormat.Png);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "mouse_event")]
    private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    private static string? ProofDirectory =>
        Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE_PROOF_DIR")
        ?? (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
            ? Path.Combine(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")
                ?? throw new InvalidOperationException("CI proof requires GITHUB_WORKSPACE."),
                "TestResults", "WorkspaceProof")
            : null);

    private static void Capture(AccessibilityAppFixture app, string theme, string page)
    {
        var directory = ProofDirectory;
        if (string.IsNullOrEmpty(directory)) return;
        using var environment = new EnvironmentScope("OPENCLAW_UI_SCREENSHOT_PATH", Path.Combine(directory, $"{theme}-{page}.png"));
        app.CaptureHubScreenshotIfRequested();
    }
}
