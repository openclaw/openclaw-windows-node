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
        var visible = PinnedPages.Visible(["future-page", "voice"], ConnectionStatus.Connected);
        Assert.Equal(["voice"], visible.Select(page => page.Tag).ToArray());
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
    public void Catalog_ExcludesHomeAndDebug_AndHasUniqueTags()
    {
        var tags = PinnedPages.Catalog.Select(page => page.Tag).ToArray();
        Assert.DoesNotContain("home", tags);
        Assert.DoesNotContain("debug", tags);
        Assert.Equal(tags.Length, tags.Distinct().Count());
    }

    [Theory]
    [InlineData("Connected", true)]
    [InlineData("Connecting", false)]
    [InlineData("Disconnected", false)]
    [InlineData("Error", false)]
    public void Availability_GatewayPagesFollowConnection_LocalPagesAndConnectionAlwaysReachable(string statusName, bool gatewayReachable)
    {
        var status = Enum.Parse<ConnectionStatus>(statusName);
        foreach (var page in PinnedPages.Catalog)
        {
            var expected = !HubPageRegistry.IsGatewayPageTag(page.Tag) || gatewayReachable;
            Assert.Equal(expected, PinnedPages.IsAvailable(page.Tag, status));
        }
        Assert.True(PinnedPages.IsAvailable("connection", status));
        Assert.True(PinnedPages.IsAvailable("permissions", status));
    }

    [Fact]
    public void Visible_PinsSurviveDisconnectAndReturnOnReconnect()
    {
        string[] stored = ["cron", "connection", "usage"];
        Assert.Equal(["connection"], PinnedPages.Visible(stored, ConnectionStatus.Disconnected).Select(p => p.Tag).ToArray());
        Assert.Equal(stored, PinnedPages.Visible(stored, ConnectionStatus.Connected).Select(p => p.Tag).ToArray());
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
            store.Update(null, e => e.SetPagePinned("gw-a", "voice", true));
            store.Update(null, e => e.SetPagePinned("gw-b", "usage", true));
        }
        using var reloaded = Store(temp.Path, out _);
        Assert.Equal(["cron", "voice"], Tags(reloaded.Current.PinnedPagesFor("gw-a")));
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
        Assert.Equal("voice", PinnedPages.ResolveRoute("voice", "A"));
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
        Assert.Contains("IsInsideNavigationItem(now) ? null : _chatFocus", window);
        Assert.Contains("EmbeddedOperationInfo.IsOpen = IsSettingsVisible && Destination.Embedded", window);
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
                    xaml.IndexOf("x:Name=\"PagesHeader\"", StringComparison.Ordinal));
        Assert.True(xaml.IndexOf("x:Name=\"PagesEmpty\"", StringComparison.Ordinal) <
                    xaml.IndexOf("x:Name=\"SessionsHeader\"", StringComparison.Ordinal));
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
