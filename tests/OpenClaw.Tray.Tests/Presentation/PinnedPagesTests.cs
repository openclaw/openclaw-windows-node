using OpenClaw.Shared;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class PinnedPagesTests
{
    private static SettingsStore Store(string dir, out SettingsManager settings)
    {
        settings = new SettingsManager(dir);
        return new SettingsStore(settings, new RecordingUiDispatcher());
    }

    private static string[] Tags(IEnumerable<string> pins) => pins.ToArray();

    [Fact]
    public void Default_IsEmpty_AndNullOrEmptyBothMeanNoPins()
    {
        Assert.Empty(PinnedPages.Normalize(null));
        Assert.Empty(PinnedPages.Normalize([]));
        using var temp = new TempDir();
        using var store = Store(temp.Path, out _);
        Assert.Empty(store.Current.PinnedPagesFor("gw-a"));
        Assert.Empty(store.Current.PinnedPagesFor(null));
    }

    [Fact]
    public void Normalize_DropsBlankAndDuplicates_KeepsOrderAndUnknownTags()
    {
        var result = PinnedPages.Normalize(["usage", null!, " ", "cron", "usage", "future-page"]);
        Assert.Equal(["usage", "cron", "future-page"], Tags(result));
    }

    [Fact]
    public void Visible_OmitsUnknownTags_ButStorageKeepsThem()
    {
        var visible = PinnedPages.Visible(["future-page", "skills"]);
        Assert.Equal(["skills"], visible.Select(page => page.Tag).ToArray());
    }

    [Fact]
    public void Toggle_PinAppends_UnpinRemoves_DuplicatePinIsIdempotent_UnknownIgnored()
    {
        var pinned = PinnedPages.Toggle(["usage"], "cron", true);
        Assert.Equal(["usage", "cron"], Tags(pinned));
        Assert.Equal(["usage", "cron"], Tags(PinnedPages.Toggle(pinned, "cron", true)));
        Assert.Equal(["cron"], Tags(PinnedPages.Toggle(pinned, "usage", false)));
        Assert.Equal(["usage"], Tags(PinnedPages.Toggle(["usage"], "not-a-page", true)));
    }

    [Fact]
    public void Catalog_ExcludesHomeDebugSettingsAndThisComputer_AndHasUniqueTags()
    {
        var tags = PinnedPages.Catalog.Select(page => page.Tag).ToArray();
        Assert.DoesNotContain("home", tags);
        Assert.DoesNotContain("debug", tags);
        Assert.DoesNotContain("settings", tags);
        foreach (var tag in new[] { "local-ai", "voice", "permissions", "sandbox" })
            Assert.DoesNotContain(tag, tags);
        Assert.DoesNotContain(PinnedPages.Catalog, page => page.LabelKey == "WorkspaceShell_ThisComputer");
        Assert.Equal(HubPageKind.Settings, HubPageRegistry.ResolvePage("settings"));
        Assert.Equal(HubPageKind.LocalAi, HubPageRegistry.ResolvePage("local-ai"));
        Assert.Equal(HubPageKind.Voice, HubPageRegistry.ResolvePage("voice"));
        Assert.Equal(HubPageKind.Permissions, HubPageRegistry.ResolvePage("permissions"));
        Assert.Equal(HubPageKind.Sandbox, HubPageRegistry.ResolvePage("sandbox"));
        Assert.Equal(tags.Length, tags.Distinct().Count());
        Assert.Equal(
            ["connection", "sessions", "skills", "channels", "instances", "cron", "agentevents",
             "bindings", "config", "usage"],
            tags);
    }

    [Fact]
    public void ExcludedSettingsAndComputerPins_AreHiddenButSurvivePersistenceAndOtherPinEdits()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path);
        settings.SetPinnedPages("gw-a", ["settings", "local-ai", "voice", "permissions", "sandbox", "future-page", "connection"]);
        settings.Save();
        using (var store = Store(temp.Path, out _))
        {
            Assert.Equal(["connection"], PinnedPages.Visible(store.Current.PinnedPagesFor("gw-a")).Select(page => page.Tag).ToArray());
            store.Update(null, editor => editor.SetPagePinned("gw-a", "skills", true));
            foreach (var excluded in new[] { "settings", "local-ai", "voice", "permissions", "sandbox" })
                store.Update(null, editor => editor.SetPagePinned("gw-a", excluded, false));
        }
        using var reloaded = Store(temp.Path, out _);
        Assert.Equal(["settings", "local-ai", "voice", "permissions", "sandbox", "future-page", "connection", "skills"],
            Tags(reloaded.Current.PinnedPagesFor("gw-a")));
        Assert.Equal(["connection", "skills"], PinnedPages.Visible(reloaded.Current.PinnedPagesFor("gw-a")).Select(page => page.Tag).ToArray());
        foreach (var excluded in new[] { "settings", "local-ai", "voice", "permissions", "sandbox" })
            Assert.Empty(PinnedPages.Toggle([], excluded, true));
    }

    [Theory]
    [InlineData("Connected", true)]
    [InlineData("Connecting", false)]
    [InlineData("Disconnected", false)]
    [InlineData("Error", false)]
    public void Availability_GatewayPinsFollowConnection_ConnectionAlwaysReachable(string statusName, bool gatewayReachable)
    {
        var status = Enum.Parse<ConnectionStatus>(statusName);
        foreach (var page in PinnedPages.Catalog)
        {
            var expected = !HubPageRegistry.IsGatewayPageTag(page.Tag) || gatewayReachable;
            Assert.Equal(expected, PinnedPages.IsAvailable(page.Tag, status));
        }
        Assert.True(PinnedPages.IsAvailable("connection", status));
    }

    [Fact]
    public void Visible_AllKnownPinsRemainInStoredOrderAcrossConnectionStates()
    {
        string[] stored = ["cron", "connection", "usage"];
        foreach (var status in Enum.GetValues<ConnectionStatus>())
        {
            Assert.Equal(stored, PinnedPages.Visible(stored).Select(p => p.Tag).ToArray());
            Assert.Equal(status == ConnectionStatus.Connected, PinnedPages.IsAvailable("cron", status));
            Assert.True(PinnedPages.IsAvailable("connection", status));
        }
    }

    [Fact]
    public void QueuedPinInvocation_RechecksAvailabilityBeforeRoutingOrRecordingSidebarIntent()
    {
        var source = ReadSource("Windows", "WorkspaceWindow.Pins.cs");
        var invoke = source[source.IndexOf("private void OnPinItemInvoked(", StringComparison.Ordinal)..
            source.IndexOf("private void OnPagesFlyoutOpening(", StringComparison.Ordinal)];
        var gate = invoke.IndexOf("!PinnedPages.IsAvailable(candidate.Tag, PinStatus)", StringComparison.Ordinal);
        var intent = invoke.IndexOf("_sidebarInvoked = true", StringComparison.Ordinal);
        var route = invoke.IndexOf("OpenSettings(", StringComparison.Ordinal);
        Assert.True(gate >= 0 && intent > gate && route > intent);
        Assert.Contains("RebuildPins();", invoke[gate..intent]);
        Assert.Contains("return;", invoke[gate..intent]);
    }

    [Fact]
    public void ScopeKey_UnconfiguredCannotCollideWithGatewayId()
    {
        Assert.Equal(PinnedPages.UnconfiguredScope, PinnedPages.ScopeKey(null));
        Assert.Equal(PinnedPages.UnconfiguredScope, PinnedPages.ScopeKey("  "));
        Assert.Equal("gw-a", PinnedPages.ScopeKey(" gw-a "));
        Assert.False(Guid.TryParse(PinnedPages.UnconfiguredScope, out _));
    }

    [Fact]
    public void Persistence_RoundTripsAndIsolatesGateways()
    {
        using var temp = new TempDir();
        using (var store = Store(temp.Path, out _))
        {
            store.Update(null, e => e.SetPagePinned("gw-a", "cron", true));
            store.Update(null, e => e.SetPagePinned("gw-a", "skills", true));
            store.Update(null, e => e.SetPagePinned("gw-b", "usage", true));
        }
        using var reloaded = Store(temp.Path, out _);
        Assert.Equal(["cron", "skills"], Tags(reloaded.Current.PinnedPagesFor("gw-a")));
        Assert.Equal(["usage"], Tags(reloaded.Current.PinnedPagesFor("gw-b")));
        Assert.Empty(reloaded.Current.PinnedPagesFor(null));
    }

    [Fact]
    public void Unpin_LastPin_RemovesEntry_AndExplicitEmptyStaysEmpty()
    {
        using var temp = new TempDir();
        using var store = Store(temp.Path, out _);
        store.Update(null, e => e.SetPagePinned("gw-a", "cron", true));
        store.Update(null, e => e.SetPagePinned("gw-a", "cron", false));
        Assert.Empty(store.Current.PinnedPagesFor("gw-a"));
        Assert.Empty(store.Current.PinnedPagesByGateway);
    }

    [Fact]
    public void DisjointUpdates_AreReadModifyWrite_NotOverwritten()
    {
        using var temp = new TempDir();
        using var store = Store(temp.Path, out _);
        store.Update(null, e => e.SetPagePinned("gw-a", "cron", true));
        store.Update(null, e => e.SetPagePinned("gw-a", "usage", true));
        Assert.Equal(["cron", "usage"], Tags(store.Current.PinnedPagesFor("gw-a")));
    }

    [Fact]
    public void FailedUpdate_LeavesMemoryAndDiskUnchanged()
    {
        using var temp = new TempDir();
        using var store = Store(temp.Path, out _);
        store.Update(null, e => e.SetPagePinned("gw-a", "cron", true));
        Assert.Throws<InvalidOperationException>(() => store.Update(null, e =>
        {
            e.SetPagePinned("gw-a", "usage", true);
            throw new InvalidOperationException("boom");
        }));
        Assert.Equal(["cron"], Tags(store.Current.PinnedPagesFor("gw-a")));
        using var reloaded = Store(temp.Path, out _);
        Assert.Equal(["cron"], Tags(reloaded.Current.PinnedPagesFor("gw-a")));
    }

    [Fact]
    public void Snapshot_IsDetachedFromLaterWrites()
    {
        using var temp = new TempDir();
        using var store = Store(temp.Path, out _);
        store.Update(null, e => e.SetPagePinned("gw-a", "cron", true));
        var snapshot = store.Current;
        store.Update(null, e => e.SetPagePinned("gw-a", "usage", true));
        Assert.Equal(["cron"], Tags(snapshot.PinnedPagesFor("gw-a")));
    }

    [Fact]
    public void PinWrite_IsUiOnly_ChangesNoCapabilitySetting_AndPublishesStoreChange()
    {
        using var temp = new TempDir();
        using var store = Store(temp.Path, out var settings);
        var before = (settings.NodeCameraEnabled, settings.NodeScreenEnabled, settings.EnableNodeMode, settings.EnableMcpServer);
        var changes = 0;
        store.Changed += (_, _) => changes++;
        store.Update(store.CreateOrigin(), e => e.SetPagePinned("gw-a", "cron", true));
        Assert.Equal(before, (settings.NodeCameraEnabled, settings.NodeScreenEnabled, settings.EnableNodeMode, settings.EnableMcpServer));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void AgentScopedRoute_IsResolvedAtInvoke_FromGenericTag()
    {
        Assert.Equal("agent:ops:cron", WorkspaceNavigation.NormalizeSettingsTag("cron", "ops"));
        Assert.Equal("agent:main:cron", WorkspaceNavigation.NormalizeSettingsTag("cron", "main"));
    }

    [Fact]
    public void PinRoute_UsesChatSelectedAgent_NotLastSettingsAgent()
    {
        Assert.Equal("agent:A:cron", PinnedPages.ResolveRoute("cron", "A"));
        Assert.Equal("agent:C:cron", PinnedPages.ResolveRoute("cron", "C"));
        Assert.Equal("agent:C:workspace", PinnedPages.ResolveRoute("workspace", "C"));
        Assert.Equal("agent:main:cron", PinnedPages.ResolveRoute("cron", null));
        Assert.Equal("usage", PinnedPages.ResolveRoute("usage", "C"));
        Assert.Equal("instances", PinnedPages.ResolveRoute("instances", "A"));
    }

    [Fact]
    public void PinRoute_StoresOnlyGenericTags_SoChatAgentChangeRetargetsWithoutRewrite()
    {
        using var temp = new TempDir();
        using var store = Store(temp.Path, out _);
        store.Update(null, e => e.SetPagePinned("gw-a", "cron", true));
        var stored = Tags(store.Current.PinnedPagesFor("gw-a"));
        Assert.Equal(["cron"], stored);
        Assert.Equal("agent:A:cron", PinnedPages.ResolveRoute(stored[0], "A"));
        Assert.Equal("agent:C:cron", PinnedPages.ResolveRoute(stored[0], "C"));
        Assert.Equal(["cron"], Tags(store.Current.PinnedPagesFor("gw-a")));
    }

    [Fact]
    public void WindowWiring_UsesTypedTag_SingleRoutePath_AndNeverRaisesSettingsSaved()
    {
        var pins = ReadSource("Windows", "WorkspaceWindow.Pins.cs");
        Assert.Contains("OpenSettings(PinnedPages.ResolveRoute(pin.Tag, _agentId), embedded: true)", pins);
        Assert.Contains("Width = 22", pins);
        var window = ReadSource("Windows", "WorkspaceWindow.xaml.cs");
        Assert.Contains("ApplySettingsPresentation(view)", window);
        Assert.Contains("ContentHost.Visibility = embedded ? Visibility.Collapsed : Visibility.Visible", window);
        Assert.Contains("_settings?.ContentOnly = false", window);
        Assert.Contains("Embedded: true }", window);
        Assert.Contains("item.Tag is PinnedPageTag pin && IsPinDestination(pin)", window);
        // RenderSettings must sync selection (a nonselecting pin never raises SelectionChanged).
        var render = window[window.IndexOf("private void RenderSettings()", StringComparison.Ordinal)..];
        Assert.Contains("UpdateNavigationSelection();", render[..render.IndexOf("ShowSection(", StringComparison.Ordinal)]);
        Assert.Contains("PrunedFallback(_navigation.Current, fallbackTag)", window);
        Assert.Contains("SettingsExitFocus(_chatFocus,", window);
        Assert.Contains("_sidebarInvoked ? SettingsExitCause.SidebarActivation : SettingsExitCause.History", window);
        Assert.DoesNotContain("FocusInWorkspaceSidebar", window);
        var back = window[window.IndexOf("internal void NavigateBack()", StringComparison.Ordinal)..];
        Assert.Contains("_sidebarInvoked = false;", back[..back.IndexOf("GoBack()", StringComparison.Ordinal)]);
        var forward = window[window.IndexOf("private void NavigateForward()", StringComparison.Ordinal)..];
        Assert.Contains("_sidebarInvoked = false;", forward[..forward.IndexOf("GoForward()", StringComparison.Ordinal)]);
        Assert.Contains("_sidebarInvoked = true;", pins);
        Assert.Contains("args.Reason == InfoBarCloseReason.CloseButton", window);
        Assert.Contains("EmbeddedOperationInfo.IsOpen = embedded && OperationInfo.IsOpen", window);
        Assert.Contains("EmbeddedOperationInfo.IsOpen = OperationInfo.IsOpen && IsSettingsVisible && Destination.Embedded", window);
        Assert.Contains("SettingsSection.Children.Insert(0, view)", window);
        Assert.Contains("AccessibilityView.Raw", pins);
        Assert.Contains("SelectsOnInvoked = false", pins);
        Assert.DoesNotContain("SettingsSaved?.Invoke", pins);
        Assert.DoesNotContain("SettingsSaved(", pins);
        Assert.DoesNotContain("Navigate(", pins);
        Assert.Contains("_pinRows.Reconcile(", pins);
        Assert.DoesNotContain("NavView.MenuItems.Remove(", pins);
        Assert.Contains("FocusedElement, PinRowOwns", pins);
        var xaml = ReadSource("Windows", "WorkspaceWindow.xaml");
        Assert.True(xaml.IndexOf("x:Name=\"HomeItem\"", StringComparison.Ordinal) <
                    xaml.IndexOf("x:Name=\"SessionsSeparator\"", StringComparison.Ordinal));
        Assert.True(xaml.IndexOf("x:Name=\"SessionsSeparator\"", StringComparison.Ordinal) <
                    xaml.IndexOf("x:Name=\"SessionsHeader\"", StringComparison.Ordinal));
        Assert.Contains("NavView.MenuItems.IndexOf(HomeItem) + 1", pins);
        Assert.DoesNotContain("PagesHeader", xaml);
        Assert.DoesNotContain("PagesEmpty", xaml);
    }

    [Fact]
    public void PinPicker_RejectsStaleGatewayBeforePersisting_AndBackgroundUsesLiveThemeResource()
    {
        var pins = ReadSource("Windows", "WorkspaceWindow.Pins.cs");
        var toggle = pins[pins.IndexOf("private void OnPagePinToggled(", StringComparison.Ordinal)..
            pins.IndexOf("private void QueuePinnedPageHostPreparation()", StringComparison.Ordinal)];
        var fence = toggle.IndexOf("if (scope != CurrentPinScope())", StringComparison.Ordinal);
        var write = toggle.IndexOf("store.Update(", StringComparison.Ordinal);
        Assert.True(fence >= 0 && write > fence);
        var staleBranch = toggle[fence..write];
        Assert.Contains("PagesFlyout.Hide();", staleBranch);
        Assert.Contains("RebuildPins();", staleBranch);
        Assert.Contains("return;", staleBranch);
        Assert.Contains("Background=\"{ThemeResource ApplicationPageBackgroundThemeBrush}\"",
            ReadSource("Windows", "WorkspaceWindow.xaml"));
        Assert.DoesNotContain("Application.Current.Resources[\"ApplicationPageBackgroundThemeBrush\"]",
            ReadSource("Windows", "WorkspaceWindow.xaml.cs"));
    }

    [Fact]
    public void PreparingPinHost_KeepsPageActivationAndNativeTransitionsOnTheInvocationPath()
    {
        var pins = ReadSource("Windows", "WorkspaceWindow.Pins.cs");
        var preparation = pins[pins.IndexOf("private void QueuePinnedPageHostPreparation()", StringComparison.Ordinal)..];
        Assert.Contains("DispatcherQueuePriority.Low", preparation);
        Assert.Contains("IsClosed || Root.XamlRoot is null || _settings is not null", preparation);
        Assert.Contains("PinnedPagesFor(CurrentPinScope()).IsEmpty != false", preparation);
        Assert.Contains("_preparedSettings = new SettingsShellView();", preparation);
        Assert.DoesNotContain("BindToAppState", preparation);
        Assert.DoesNotContain("_settings =", preparation);
        Assert.DoesNotContain("OpenSettings(", preparation);
        Assert.DoesNotContain("ShowSection(", preparation);
        Assert.Contains("<NavigationThemeTransition/>", ReadSource("Controls", "SettingsShellView.xaml"));
        var window = ReadSource("Windows", "WorkspaceWindow.xaml.cs");
        Assert.Contains("var view = _preparedSettings ?? new SettingsShellView();", window);
        Assert.Contains("_preparedSettings?.Dispose();", window);
    }

    [Fact]
    public void ProgrammaticPinInvocation_IsIgnoredBeforeChangingNavigationOrFocusIntent()
    {
        var pins = ReadSource("Windows", "WorkspaceWindow.Pins.cs");
        var handler = pins[pins.IndexOf("private void OnPinItemInvoked(", StringComparison.Ordinal)..
            pins.IndexOf("private void OnPagesFlyoutOpening(", StringComparison.Ordinal)];
        Assert.True(handler.IndexOf("if (_updating) return;", StringComparison.Ordinal) >= 0);
        Assert.True(handler.IndexOf("if (_updating) return;", StringComparison.Ordinal) <
            handler.IndexOf("_sidebarInvoked = true;", StringComparison.Ordinal));
        var window = ReadSource("Windows", "WorkspaceWindow.xaml.cs");
        var selection = window[window.IndexOf("private void UpdateNavigationSelection()", StringComparison.Ordinal)..
            window.IndexOf("private bool IsPinDestination(", StringComparison.Ordinal)];
        Assert.Contains("var wasUpdating = _updating;", selection);
        Assert.Contains("finally { _updating = wasUpdating; }", selection);
    }

    private static string ReadSource(params string[] relative)
    {
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? Directory.GetCurrentDirectory();
        for (var dir = new DirectoryInfo(root); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, "src", "OpenClaw.Tray.WinUI", .. relative]);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException(string.Join('/', relative));
    }
}
