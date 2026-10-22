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
        await WaitUntilAsync(() => FindPopup(root, "WorkspaceOwnerConnectionStatus") is not null);
        Assert.Equal("Connection Status: Disconnected", FindPopup(root, "WorkspaceOwnerConnectionStatus")!.Current.Name);
        CaptureFlyout(root, theme, "owner-live-status");
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
        Assert.Equal("OpenClaw Settings", root.Current.Name);
        var title = Find(root, "SettingsTitleBar").Current.BoundingRectangle;
        var toggle = Find(root, "SettingsTogglePane").Current.BoundingRectangle;
        var search = Find(root, "SettingsSearch").Current.BoundingRectangle;
        var back = Find(root, "SettingsBack").Current.BoundingRectangle;
        var forward = Find(root, "SettingsForward").Current.BoundingRectangle;
        Assert.True(toggle.Top >= title.Bottom);
        Assert.Equal(toggle.Top, search.Top);
        Assert.Equal(toggle.Top, back.Top);
        Assert.Equal(toggle.Top, forward.Top);
        Assert.True(search.Left >= toggle.Right);
        Assert.True(back.Left >= search.Right);
        Assert.True(forward.Left >= back.Right);
        Assert.False(Find(root, "SettingsForward").Current.IsEnabled);
        await VerifySettingsFooterAsync(root, compact: false);
        await app.NavigateAsync("permissions", "PermissionsPage", "PermissionsPageMarker");
        Invoke(Find(root, "SettingsBack"));
        await WaitUntilAsync(() => IsVisible(root, "SettingsPageMarker"));
        Assert.True(Find(root, "SettingsForward").Current.IsEnabled);
        Invoke(Find(root, "SettingsForward"));
        await WaitUntilAsync(() => IsVisible(root, "PermissionsPageMarker"));
        Assert.False(Find(root, "SettingsForward").Current.IsEnabled);

        AutomationElement? SearchBox() => ProcessWindows(root.Current.ProcessId).Cast<AutomationElement>()
            .Select(window => window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsSearchBox")))
            .FirstOrDefault(element => element is not null && !element.Current.IsOffscreen);
        Invoke(Find(root, "SettingsSearch"));
        await WaitUntilAsync(() => SearchBox() is not null);
        var input = SearchBox()!.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        await WaitUntilAsync(() => input.Current.HasKeyboardFocus);
        System.Windows.Forms.SendKeys.SendWait("Go to Settings");
        await WaitUntilAsync(() => ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).Current.Value == "Go to Settings");
        System.Windows.Forms.SendKeys.SendWait("{ENTER}");
        await WaitUntilAsync(() => IsVisible(root, "SettingsPageMarker"));
        await WaitUntilAsync(() => SearchBox() is null);
        Assert.False(Find(root, "SettingsForward").Current.IsEnabled);
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
        await WaitUntilAsync(() => SearchBox()!.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)).Current.HasKeyboardFocus);
        System.Windows.Forms.SendKeys.SendWait("{ESC}");
        await WaitUntilAsync(() => SearchBox() is null);
        Invoke(Find(root, "SettingsTogglePane"));
        await WaitUntilAsync(() => IsVisible(root, "SettingsSearch"));
        await VerifySettingsFooterAsync(root, compact: false);
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
        await WaitUntilAsync(() => IsVisible(root, "SettingsConnectionStatus"));
        // IsPaneOpen changes before the native pane transition has finished arranging its footer.
        await Task.Delay(350);
        var status = Find(root, "SettingsConnectionStatus");
        Assert.Null(root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsNotifications")));
        var statusBounds = status.Current.BoundingRectangle;
        var settingsBounds = Find(root, "SettingsNavSettings").Current.BoundingRectangle;
        Assert.True(statusBounds.Top > Find(root, "SettingsTitleBar").Current.BoundingRectangle.Bottom);
        Assert.True(statusBounds.Bottom <= settingsBounds.Top, $"Status {statusBounds} overlaps Settings {settingsBounds}; compact={compact}");
        Assert.InRange(statusBounds.Left, settingsBounds.Left - 8, settingsBounds.Left + 8);
        var diagnostics = root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsNavDiagnostics"));
        if (diagnostics is not null && !diagnostics.Current.IsOffscreen)
            Assert.True(statusBounds.Bottom <= diagnostics.Current.BoundingRectangle.Top);
        if (compact)
        {
            Assert.Equal(Find(root, "SettingsTogglePane").Current.BoundingRectangle.Width, statusBounds.Width);
        }
        Assert.True(statusBounds.Right <= settingsBounds.Right + 4);
        foreach (var (button, marker) in new[]
        {
            (status, "GatewayStatusOpenConnection")
        })
        {
            AutomationElement? PopupAction() => ProcessWindows(root.Current.ProcessId).Cast<AutomationElement>()
                .Select(window => window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, marker)))
                .FirstOrDefault(element => element is not null && !element.Current.IsOffscreen);
            button.SetFocus();
            System.Windows.Forms.SendKeys.SendWait(" ");
            await WaitUntilAsync(() => PopupAction() is not null);
            System.Windows.Forms.SendKeys.SendWait("{ESC}");
            await WaitUntilAsync(() => PopupAction() is null);
            Assert.True(Find(root, "SettingsNavSettings").Current.IsEnabled);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsChatLink_OpensWorkspaceWithoutChangingSettingsOrDiscardingDraft(bool useMouse)
    {
        using var app = new AccessibilityAppFixture(initializeAxe: false, syntheticData: true, initialRoute: null);
        await app.NavigateAsync("chat", "ChatPage", "ChatComposerInput");
        var workspace = AutomationElement.FromHandle(app.HubWindowHandle);
        var workspaceHandle = app.HubWindowHandle;
        var composer = Find(workspace, "ChatComposerInput");
        var identity = composer.GetRuntimeId();
        const string draft = "Keep this draft when opening Chat from Settings";
        ((ValuePattern)composer.GetCurrentPattern(ValuePattern.Pattern)).SetValue(draft);
        Assert.Null(workspace.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "ChatDashboardButton")));
        await app.NavigateAsync("workspace:notifications", "NotificationsPage", "NotificationsPageMarker");
        await app.NavigateAsync("connection", "ConnectionPage", "ConnectionPageMarker");
        var settings = AutomationElement.FromHandle(app.HubWindowHandle);
        Assert.NotNull(Find(settings, "ConnectionDashboardButton"));
        Capture(app, "System", "connection-dashboard-card");
        ActivateChatLink();
        await WaitUntilAsync(() => IsVisible(workspace, "ChatComposerInput"));
        await AssertChatForegroundAsync();
        var retained = Find(workspace, "ChatComposerInput");
        Assert.Equal(identity, retained.GetRuntimeId());
        Assert.Equal(draft, ((ValuePattern)retained.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        Assert.NotNull(Find(settings, "ConnectionPageMarker"));
        ActivateChatLink();
        await AssertChatForegroundAsync();
        Assert.Equal(identity, Find(workspace, "ChatComposerInput").GetRuntimeId());
        var window = (WindowPattern)workspace.GetCurrentPattern(WindowPattern.Pattern);
        window.SetWindowVisualState(WindowVisualState.Minimized);
        ActivateChatLink();
        await AssertChatForegroundAsync();
        Assert.NotEqual(WindowVisualState.Minimized, window.Current.WindowVisualState);
        Assert.Equal(draft, ((ValuePattern)Find(workspace, "ChatComposerInput")
            .GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        await app.RefocusWorkspaceAsync();
        Capture(app, "System", "settings-chat-link");

        void ActivateChatLink()
        {
            var link = Find(settings, "SettingsNavChat");
            link.SetFocus();
            Assert.Equal(new IntPtr(settings.Current.NativeWindowHandle), GetForegroundWindow());
            if (useMouse)
            {
                var bounds = link.Current.BoundingRectangle;
                System.Windows.Forms.Cursor.Position = new System.Drawing.Point(
                    (int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
                MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero);
                MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero);
            }
            else
                System.Windows.Forms.SendKeys.SendWait("{ENTER}");
        }

        async Task AssertChatForegroundAsync()
        {
            await WaitUntilAsync(() => GetForegroundWindow() == workspaceHandle);
            await Task.Delay(250);
            Assert.Equal(workspaceHandle, GetForegroundWindow());
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
            Assert.NotEqual(workspace, app.HubWindowHandle);
            await app.RefocusWorkspaceAsync();
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
        await app.RefocusWorkspaceAsync();
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
        foreach (var key in new[] { "agent:main:main", "agent:main:fork" })
        {
            var item = Find(root, $"WorkspaceSession:{key}");
            Assert.False(item.Current.IsOffscreen);
            ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            await WaitUntilAsync(() => SelectionState(root, $"WorkspaceSession:{key}") == true);
            Assert.Equal(composerIdentity, Find(root, "ChatComposerInput").GetRuntimeId());
            await WaitUntilAsync(() => SelectionState(root, "WorkspaceNavHome") == false);
        }
        var input = Find(root, "ChatComposerInput");
        const string draft = "Keep the selected session draft while opening Settings";
        ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).SetValue(draft);
        await app.NavigateAsync("settings", "SettingsPage", "SettingsPageMarker");
        Assert.NotEqual(workspace, app.HubWindowHandle);
        await app.RefocusWorkspaceAsync();
        Assert.Equal(workspace, app.HubWindowHandle);
        await WaitUntilAsync(() => SelectionState(root, "WorkspaceSession:agent:main:fork") == true);
        Assert.Equal(draft, ((ValuePattern)Find(root, "ChatComposerInput").GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        var composerId = Find(root, "ChatComposerInput").GetRuntimeId();
        await FocusForKeyboardAsync(app, root, "WorkspaceTogglePane");
        // This test covers session/draft preservation. NativePagesAndOwnerLinks_KeepCompanionIndependent
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
        // Reopening the isolated Sessions companion republishes its snapshot.
        await app.NavigateAsync("sessions", "SessionsPage", "SessionsPageMarker");
        await app.RefocusWorkspaceAsync();
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
        await WaitUntilAsync(() => FindPopup(root, "WorkspaceOwnerConnectionStatus") is not null);
        Assert.Equal("Connection Status: Disconnected", FindPopup(root, "WorkspaceOwnerConnectionStatus")!.Current.Name);
        Invoke(FindPopup(root, "WorkspaceOwnerConnectionStatus")!);
        await WaitUntilAsync(() => FindPopup(root, "GatewayStatusOpenConnection") is not null);
        Assert.Equal(composerId, Find(root, "ChatComposerInput").GetRuntimeId());
        Assert.Equal(draft, ((ValuePattern)Find(root, "ChatComposerInput").GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        CaptureFlyout(root, theme, "owner-connection-status");
        Invoke(FindPopup(root, "GatewayStatusOpenConnection")!);
        await WaitForMarkerAsync(root.Current.ProcessId, "ConnectionPageMarker", workspace);
        await app.RefocusWorkspaceAsync();
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
    public async Task NativePagesAndOwnerLinks_KeepCompanionIndependent(string theme)
    {
        using var app = new AccessibilityAppFixture(initializeAxe: false, theme: theme, syntheticData: false);
        var companion = app.HubWindowHandle;
        await app.NavigateAsync("chat", "ChatPage", "WorkspaceOwner");
        var workspace = app.HubWindowHandle;
        Assert.NotEqual(companion, workspace);
        var workspaceElement = AutomationElement.FromHandle(workspace);
        var pid = workspaceElement.Current.ProcessId;
        var owner = Find(workspaceElement, "WorkspaceOwner");
        var notifications = Find(workspaceElement, "WorkspaceNotifications");
        Assert.True(notifications.Current.BoundingRectangle.Left >= owner.Current.BoundingRectangle.Right);
        Assert.False(Find(workspaceElement, "WorkspaceSessionsAdd").Current.IsEnabled);
        var sessionsHeader = FindHeading(workspaceElement, "Sessions").Current.BoundingRectangle;
        var initialHomeBounds = Find(workspaceElement, "WorkspaceNavHome").Current.BoundingRectangle;
        Assert.True(sessionsHeader.Top >= initialHomeBounds.Bottom - 1);
        Assert.InRange(sessionsHeader.Top - initialHomeBounds.Bottom, -1, 36);
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
            Assert.False(Find(workspaceElement, "WorkspaceBack").Current.IsEnabled);
        }
        await app.NavigateAsync("workspace:notifications", "NotificationsPage", "WorkspaceNotifications");
        Capture(app, theme, "notifications");

        foreach (var (label, route, page, marker) in new[]
        {
            ("Settings", "settings", "SettingsPage", "SettingsPageMarker"),
            ("Usage", "usage", "UsagePage", "UsagePageMarker"),
            ("Pair device", "channels", "ChannelsPage", "ChannelsPageMarker"),
            ("About OpenClaw", "about", "SettingsPage", "SettingsPageMarker")
        })
        {
            await app.RefocusWorkspaceAsync();
            Invoke(Find(AutomationElement.FromHandle(workspace), "WorkspaceOwner"));
            Assert.True(WaitForMenuItem(pid, "Get apps").Current.IsEnabled);
            Invoke(WaitForMenuItem(pid, label));
            var destination = await WaitForMarkerAsync(pid, marker, workspace);
            Assert.Equal(companion, new IntPtr(destination.Current.NativeWindowHandle));
            Assert.NotNull(Find(AutomationElement.FromHandle(workspace), "WorkspaceOwner"));
            // The explicit route is also exercised to verify focus/reuse and readiness.
            await app.NavigateAsync(route, page, marker);
            Assert.Equal(companion, app.HubWindowHandle);
            Capture(app, theme, route);
        }
        foreach (var (route, page, marker) in new[]
        {
            ("cron", "CronPage", "CronPageMarker"),
            ("sessions", "SessionsPage", "SessionsPageMarker"),
            ("skills", "SkillsPage", "SkillsPageMarker")
        })
        {
            await app.NavigateAsync(route, page, marker);
            Assert.Equal(companion, app.HubWindowHandle);
        }
        ((WindowPattern)AutomationElement.FromHandle(companion).GetCurrentPattern(WindowPattern.Pattern)).Close();
        Assert.NotNull(Find(AutomationElement.FromHandle(workspace), "WorkspaceOwner"));
        await app.NavigateAsync("usage", "UsagePage", "UsagePageMarker");
        Assert.NotEqual(workspace, app.HubWindowHandle);
        await app.NavigateAsync("workspace:home", "ChatPage", "WorkspaceNavHome");
        Assert.Equal(workspace, app.HubWindowHandle);

        Invoke(Find(workspaceElement, "WorkspaceOwner"));
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
        Invoke(Find(workspaceElement, "WorkspaceTogglePane"));
        await WaitUntilAsync(() => !IsVisible(workspaceElement, "WorkspaceOwner") &&
            !IsVisible(workspaceElement, "WorkspaceNotifications") &&
            !IsVisible(workspaceElement, "WorkspaceSessionsAdd") &&
            IsVisible(workspaceElement, "WorkspaceReopenPane"));
        Assert.InRange(collapseTime.ElapsedMilliseconds, 0, 500);
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
        var help = WaitForMenuItem(pid, "Help");
        ((ExpandCollapsePattern)help.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        Assert.True(WaitForMenuItem(pid, "GitHub").Current.IsEnabled);
        ((ExpandCollapsePattern)help.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
        Invoke(WaitForMenuItem(pid, "Settings"));
        Assert.NotNull(await WaitForMarkerAsync(pid, "SettingsPageMarker", workspace));
        await app.RefocusWorkspaceAsync();
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

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (predicate()) return;
            await Task.Delay(100);
        }
        Assert.True(predicate(), "Native navigation did not reach the expected state.");
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
                if (new IntPtr(window.Current.NativeWindowHandle) == exclude) continue;
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
