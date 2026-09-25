using System.Diagnostics;
using System.Windows.Automation;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.UITests;

[CollectionDefinition("Workspace native proof", DisableParallelization = true)]
public sealed class WorkspaceProofCollection;

[Collection("Workspace native proof")]
public sealed class WorkspaceWindowProofTests
{
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
        await app.NavigateAsync("workspace:agents", "WorkspaceContentPage", "WorkspacePageHeading");
        await app.RefocusWorkspaceAsync();
        Assert.NotNull(Find(AutomationElement.FromHandle(workspace), "WorkspacePageHeading"));
        await app.NavigateAsync("chat", "ChatPage", "ChatComposerInput");
        Assert.Equal(draft, ((ValuePattern)Find(AutomationElement.FromHandle(workspace), "ChatComposerInput")
            .GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        var root = AutomationElement.FromHandle(workspace);
        Invoke(Find(root, "WorkspaceBack"));
        await WaitUntilAsync(() => root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspacePageHeading"))?.Current.Name == "Agents");
        Invoke(Find(root, "WorkspaceForward"));
        await WaitUntilAsync(() => IsVisible(root, "ChatComposerInput"));
        Assert.Equal(identity, Find(root, "ChatComposerInput").GetRuntimeId());
        Assert.Equal(draft, ((ValuePattern)Find(root, "ChatComposerInput")
            .GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
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
        var pagesHeader = FindHeading(workspaceElement, "Pages").Current.BoundingRectangle;
        var sessionsHeader = FindHeading(workspaceElement, "Sessions").Current.BoundingRectangle;
        var moreBounds = Find(workspaceElement, "WorkspaceMore").Current.BoundingRectangle;
        Assert.True(sessionsHeader.Top >= moreBounds.Bottom - 1);
        Assert.InRange(sessionsHeader.Top - moreBounds.Bottom, -1, 36);
        Assert.True(sessionsHeader.Top > pagesHeader.Top);
        var backBounds = Find(workspaceElement, "WorkspaceBack").Current.BoundingRectangle;
        var forwardBounds = Find(workspaceElement, "WorkspaceForward").Current.BoundingRectangle;
        Assert.Equal(backBounds.Top, forwardBounds.Top);
        var toggleBounds = Find(workspaceElement, "WorkspaceTogglePane").Current.BoundingRectangle;
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
            new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspaceNewConversation")) is not null));
        var newConversation = ProcessWindows(pid).Cast<AutomationElement>().Select(window => window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspaceNewConversation"))).First(item => item is not null)!;
        Assert.Equal("New conversation", newConversation.Current.Name);
        Assert.False(newConversation.Current.IsEnabled);
        dropdown.Collapse();
        Capture(app, theme, "workspace");

        foreach (var route in new[] { "agents", "agent-detail", "writer-detail", "dashboards", "dashboard-detail", "canvas",
            "systems", "system-detail", "plugins", "activity", "tasks", "meetings", "apps", "portals", "more" })
        {
            await app.NavigateAsync($"workspace:{route}", "WorkspaceContentPage", "WorkspacePageHeading");
            Assert.Equal(workspace, app.HubWindowHandle);
            Capture(app, theme, route);
        }
        await app.NavigateAsync("workspace:automations", "CronPage", "CronPageMarker");
        Assert.Equal(workspace, app.HubWindowHandle);
        Capture(app, theme, "automations");
        await app.NavigateAsync("workspace:automation-detail", "CronPage", "CronPageMarker");
        Assert.Equal(workspace, app.HubWindowHandle);
        await app.NavigateAsync("workspace:sessions", "SessionsPage", "SessionsPageMarker");
        Assert.Equal(workspace, app.HubWindowHandle);
        await app.NavigateAsync("workspace:skills", "SkillsPage", "SkillsPageMarker");
        Assert.Equal(workspace, app.HubWindowHandle);
        await app.NavigateAsync("workspace:usage", "UsagePage", "UsagePageMarker");
        Assert.Equal(workspace, app.HubWindowHandle);
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
            Invoke(Find(AutomationElement.FromHandle(workspace), "WorkspaceOwner"));
            Invoke(WaitForMenuItem(pid, label));
            var destination = await WaitForMarkerAsync(pid, marker, workspace);
            Assert.Equal(companion, new IntPtr(destination.Current.NativeWindowHandle));
            Assert.NotNull(Find(AutomationElement.FromHandle(workspace), "WorkspaceOwner"));
            // The explicit route is also exercised to verify focus/reuse and readiness.
            await app.NavigateAsync(route, page, marker);
            Assert.Equal(companion, app.HubWindowHandle);
            Capture(app, theme, route);
        }
        ((WindowPattern)AutomationElement.FromHandle(companion).GetCurrentPattern(WindowPattern.Pattern)).Close();
        Assert.NotNull(Find(AutomationElement.FromHandle(workspace), "WorkspaceOwner"));
        await app.NavigateAsync("usage", "UsagePage", "UsagePageMarker");
        Assert.NotEqual(workspace, app.HubWindowHandle);
        await app.NavigateAsync("workspace:agents", "WorkspaceContentPage", "WorkspacePageHeading");
        Assert.Equal(workspace, app.HubWindowHandle);

        await app.NavigateAsync("workspace:systems", "WorkspaceContentPage", "WorkspacePageHeading");
        var timelineAction = AutomationElement.FromHandle(workspace).FindFirst(TreeScope.Descendants,
            new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "Connection event timeline")));
        Assert.NotNull(timelineAction);
        Invoke(timelineAction);
        var timeline = await WaitForMarkerAsync(pid, "ConnectionTimelineHeading", workspace);
        var timelineHandle = new IntPtr(timeline.Current.NativeWindowHandle);
        Assert.NotEqual(app.HubWindowHandle, timelineHandle);
        Assert.True(timeline.Current.BoundingRectangle.Left > workspaceElement.Current.BoundingRectangle.Left);
        Invoke(timelineAction);
        Assert.Equal(timelineHandle, new IntPtr((await WaitForMarkerAsync(pid, "ConnectionTimelineHeading", workspace)).Current.NativeWindowHandle));
        ((WindowPattern)timeline.GetCurrentPattern(WindowPattern.Pattern)).Close();
        Assert.NotNull(Find(AutomationElement.FromHandle(workspace), "WorkspaceOwner"));

        var nativeNavigation = Find(workspaceElement, "WorkspaceNavigation");
        foreach (var (id, title) in new[]
        {
            ("WorkspaceNavAgents", "Agents"), ("WorkspaceNavDashboards", "Dashboards"),
            ("WorkspaceNavSystems", "Systems"), ("WorkspaceNavPlugins", "Plugins"), ("WorkspaceMore", "More")
        })
        {
            var item = Find(nativeNavigation, id);
            ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            await WaitUntilAsync(() => workspaceElement.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspacePageHeading"))?.Current.Name == title);
            Assert.True(((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected);
        }

        Invoke(Find(workspaceElement, "WorkspaceBack"));
        await WaitForHeadingAsync(workspaceElement, "Plugins");
        Assert.True(Find(workspaceElement, "WorkspaceForward").Current.IsEnabled);
        Invoke(Find(workspaceElement, "WorkspaceForward"));
        await WaitForHeadingAsync(workspaceElement, "More");
        Assert.False(Find(workspaceElement, "WorkspaceForward").Current.IsEnabled);
        Invoke(Find(workspaceElement, "WorkspaceBack"));
        await WaitForHeadingAsync(workspaceElement, "Plugins");
        ((SelectionItemPattern)Find(nativeNavigation, "WorkspaceNavSystems").GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        await WaitForHeadingAsync(workspaceElement, "Systems");
        Assert.False(Find(workspaceElement, "WorkspaceForward").Current.IsEnabled);

        Invoke(Find(workspaceElement, "WorkspaceTogglePane"));
        await WaitUntilAsync(() => !IsVisible(workspaceElement, "WorkspaceOwner") &&
            !IsVisible(workspaceElement, "WorkspaceNotifications") &&
            !IsVisible(workspaceElement, "WorkspaceSessionsAdd") &&
            IsVisible(workspaceElement, "WorkspaceReopenPane") &&
            IsVisible(workspaceElement, "WorkspaceCompactNotifications"));
        Assert.False(IsVisible(workspaceElement, "WorkspaceSessionsAdd"));
        Assert.Equal("Systems", Find(workspaceElement, "WorkspacePageHeading").Current.Name);
        await app.NavigateAsync("workspace:systems", "WorkspaceContentPage", "WorkspaceCompactNotifications");
        await WaitUntilAsync(() =>
        {
            var reopen = Find(nativeNavigation, "WorkspaceReopenPane").Current.BoundingRectangle;
            var home = Find(nativeNavigation, "WorkspaceNavHome").Current.BoundingRectangle;
            var bell = Find(nativeNavigation, "WorkspaceCompactNotifications").Current.BoundingRectangle;
            return reopen.Bottom <= home.Top &&
                Math.Abs(CenterX(reopen) - CenterX(home)) <= 1 &&
                Math.Abs(CenterX(bell) - CenterX(home)) <= 1;
        });
        var reopenBounds = Find(nativeNavigation, "WorkspaceReopenPane").Current.BoundingRectangle;
        var homeBounds = Find(nativeNavigation, "WorkspaceNavHome").Current.BoundingRectangle;
        var compactNotificationBounds = Find(nativeNavigation, "WorkspaceCompactNotifications").Current.BoundingRectangle;
        Assert.InRange(Math.Abs(reopenBounds.Width - toggleBounds.Width), 0, 1);
        Assert.InRange(Math.Abs(reopenBounds.Height - toggleBounds.Height), 0, 1);
        Assert.True(reopenBounds.Bottom <= homeBounds.Top);
        Assert.InRange(Math.Abs(CenterX(homeBounds) - CenterX(reopenBounds)), 0, 1);
        Assert.InRange(Math.Abs(CenterX(homeBounds) - CenterX(compactNotificationBounds)), 0, 1);
        Assert.True(compactNotificationBounds.Top > Find(nativeNavigation, "WorkspaceMore").Current.BoundingRectangle.Bottom);
        Invoke(Find(nativeNavigation, "WorkspaceCompactNotifications"));
        await WaitUntilAsync(() => IsVisible(workspaceElement, "NotificationsPageMarker"));
        Assert.False(IsVisible(workspaceElement, "WorkspaceOwner"));
        Assert.True(IsVisible(workspaceElement, "WorkspaceReopenPane"));
        await app.NavigateAsync("workspace:notifications", "NotificationsPage", "WorkspaceCompactNotifications");
        Assert.Equal(workspace, app.HubWindowHandle);
        Invoke(Find(nativeNavigation, "WorkspaceReopenPane"));
        await WaitUntilAsync(() => IsVisible(workspaceElement, "WorkspaceOwner") &&
            IsVisible(workspaceElement, "WorkspaceNotifications") &&
            IsVisible(workspaceElement, "WorkspaceSessionsAdd") &&
            !IsVisible(workspaceElement, "WorkspaceReopenPane") &&
            !IsVisible(workspaceElement, "WorkspaceCompactNotifications"));
        Assert.False(IsVisible(workspaceElement, "WorkspaceReopenPane"));
        Assert.False(IsVisible(workspaceElement, "WorkspaceCompactNotifications"));

        ((WindowPattern)workspaceElement.GetCurrentPattern(WindowPattern.Pattern)).SetWindowVisualState(WindowVisualState.Normal);
        ((TransformPattern)workspaceElement.GetCurrentPattern(TransformPattern.Pattern)).Resize(1050, 760);
        await app.NavigateAsync("workspace:plugins", "WorkspaceContentPage", "WorkspacePageHeading");
        Assert.True(IsVisible(workspaceElement, "WorkspaceOwner"));
        Assert.True(IsVisible(workspaceElement, "WorkspaceNotifications"));
        Assert.True(IsVisible(workspaceElement, "WorkspaceAssistantSelector"));
        Assert.True(IsVisible(workspaceElement, "WorkspaceSessionsAdd"));
        Assert.Equal(Find(workspaceElement, "WorkspaceTogglePane").Current.BoundingRectangle.Top,
            Find(workspaceElement, "WorkspaceForward").Current.BoundingRectangle.Top);
        Invoke(Find(workspaceElement, "WorkspaceOwner"));
        Invoke(WaitForMenuItem(pid, "Settings"));
        Assert.NotNull(await WaitForMarkerAsync(pid, "SettingsPageMarker", workspace));
    }

    private static bool IsVisible(AutomationElement root, string id)
    {
        var element = root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, id));
        return element is not null && !element.Current.IsOffscreen && element.Current.BoundingRectangle.Width > 0;
    }

    private static double CenterX(System.Windows.Rect bounds) => bounds.Left + bounds.Width / 2;

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

    private static Task WaitForHeadingAsync(AutomationElement root, string title) =>
        WaitUntilAsync(() => root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspacePageHeading"))?.Current.Name == title);

    private static AutomationElement Find(AutomationElement root, string id) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id))
        ?? throw new InvalidOperationException($"Missing native control '{id}'.");

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

    private static void Capture(AccessibilityAppFixture app, string theme, string page)
    {
        var directory = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE_PROOF_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        using var environment = new EnvironmentScope("OPENCLAW_UI_SCREENSHOT_PATH", Path.Combine(directory, $"{theme}-{page}.png"));
        app.CaptureHubScreenshotIfRequested();
    }
}
