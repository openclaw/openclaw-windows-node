using OpenClaw.Shared;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class DesktopCompanionTests
{
    private static AppNotification Notification(string id, string? dedupeKey = null) => new()
    {
        Id = id, Title = "OpenClaw", Message = $"Message {id}", Source = "test", DedupeKey = dedupeKey
    };

    [Fact]
    public void NativeWindow_DelegatesVisualProjectionToReactor()
    {
        var root = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Windows");
        var window = File.ReadAllText(Path.Combine(root, "DesktopCompanionWindow.cs"));
        Assert.Contains("Component<DesktopCompanionSurface, DesktopCompanionSurfaceProps>", window);
        Assert.Contains("_host.Dispose()", window);
        Assert.DoesNotContain(".Text =", window);
        Assert.DoesNotContain(".Mood =", window);
        Assert.False(File.Exists(Path.Combine(root, "DesktopCompanionWindow.xaml")));
    }

    [Fact]
    public void MissingSetting_DefaultsOff()
    {
        Assert.False(new SettingsData().ShowDesktopCompanion);
        var legacy = SettingsData.FromJson("""{"ShowNotifications":true}""");
        Assert.NotNull(legacy);
        Assert.False(legacy.ShowDesktopCompanion);
    }

    [Fact]
    public void Setting_PersistsThroughViewModelAndReload_WithoutChangingOtherFields()
    {
        using var temp = new TempDir();
        var settings = new SettingsManager(temp.Path);
        using var dispatcher = new RecordingUiDispatcher();
        using var store = new SettingsStore(settings, dispatcher);
        var commands = new FakeAppCommands();
        using var vm = new SettingsPageViewModel(store, commands);
        vm.Activate(null);
        vm.ShowDesktopCompanion = true;
        Assert.True(store.Current.ShowDesktopCompanion);
        var reloaded = new SettingsManager(temp.Path);
        reloaded.Load();
        Assert.True(reloaded.ShowDesktopCompanion);
        Assert.True(reloaded.ShowNotifications);
        Assert.Equal(1, commands.NotifySettingsSavedCount);
        store.Update(null, edit => edit.ShowDesktopCompanion = false);
        Assert.False(vm.ShowDesktopCompanion);
        Assert.Equal(1, commands.NotifySettingsSavedCount);
    }

    [Fact]
    public void Observe_UsesNewestArrival_NotCurrentBanner_AndDoesNotReplayQueueRotation()
    {
        var service = new AppNotificationService();
        var state = new DesktopCompanionState();
        service.Show(Notification("old"));
        state.Reset(service.Snapshot);
        Assert.False(state.Observe(service.Snapshot, true));
        service.Show(Notification("new"));
        Assert.True(state.Observe(service.Snapshot, true));
        Assert.Equal("new", state.Current!.Id);
        service.ShowNext();
        Assert.False(state.Observe(service.Snapshot, true));
        service.Dismiss("old");
        Assert.False(state.Observe(service.Snapshot, true));
        service.Dismiss("new");
        Assert.True(state.Observe(service.Snapshot, true));
        Assert.Null(state.Current);
    }

    [Fact]
    public void CoalescedArrival_ReactsAgain_WithUpdatedTextAndSeverity()
    {
        var service = new AppNotificationService();
        var state = new DesktopCompanionState();
        service.Show(Notification("one", "same"));
        Assert.True(state.Observe(service.Snapshot, true));
        service.Show(Notification("two", "same") with { Message = "Recovered", Severity = AppNotificationSeverity.Success });
        Assert.True(state.Observe(service.Snapshot, true));
        Assert.Equal("one", state.Current!.Id);
        Assert.Equal("Recovered", state.Current.Message);
        Assert.Equal(AppNotificationSeverity.Success, state.Current.Severity);
        Assert.Equal(2, state.Current.OccurrenceCount);
    }

    [Fact]
    public void DisabledNotifications_AreNotReplayedAfterEnabling()
    {
        var service = new AppNotificationService();
        var state = new DesktopCompanionState();
        service.Show(Notification("muted"));
        Assert.False(state.Observe(service.Snapshot, false));
        Assert.False(state.Observe(service.Snapshot, true));
        service.Show(Notification("live"));
        Assert.True(state.Observe(service.Snapshot, true));
        Assert.True(state.Observe(service.Snapshot, false));
        Assert.Null(state.Current);
    }

    [Theory]
    [InlineData(-5000, -4000, -1920, -200, 1920, 1080, -1920, -200)]
    [InlineData(5000, 4000, 0, 0, 1920, 1040, 1560, 620)]
    [InlineData(400, 200, 0, 0, 1920, 1040, 400, 200)]
    [InlineData(-50, -50, 0, 0, 300, 300, 0, 0)]
    public void Placement_ClampsToMonitorWorkArea(int x, int y, int wx, int wy, int ww, int wh, int ex, int ey)
    {
        Assert.Equal(new DesktopCompanionBounds(ex, ey, 360, 420),
            DesktopCompanionBounds.Clamp(x, y, 360, 420, new(wx, wy, ww, wh)));
    }

    [Fact]
    public void Controller_TogglesLive_AndHidePersistsWithoutDismissingAppNotifications()
    {
        using var harness = new Harness();
        harness.Controller.Start();
        Assert.Empty(harness.Views);
        harness.Notifications.Show(Notification("old"));
        harness.Store.Update(null, edit => edit.ShowDesktopCompanion = true);
        var view = Assert.Single(harness.Views);
        Assert.Equal(1, view.ShowCount);
        Assert.Empty(view.Presented);
        harness.Notifications.Show(Notification("new"));
        Assert.Equal("new", Assert.Single(view.Presented)!.Id);
        view.Hide();
        Assert.True(view.Closed);
        Assert.False(harness.Store.Current.ShowDesktopCompanion);
        Assert.Equal(2, harness.Notifications.Snapshot.ActiveNotifications.Count);
        harness.Store.Update(null, edit => edit.ShowDesktopCompanion = true);
        Assert.Equal(2, harness.Views.Count);
        Assert.Empty(harness.Views[1].Presented);
        Assert.Empty(harness.Failures);
    }

    [Fact]
    public void Controller_NotificationToggleClearsBubble_ThemeUpdatesWithoutRecreatingView()
    {
        using var harness = new Harness();
        harness.Store.Update(null, edit => edit.ShowDesktopCompanion = true);
        harness.Controller.Start();
        var view = Assert.Single(harness.Views);
        harness.Notifications.Show(Notification("one"));
        harness.Store.Update(null, edit => { edit.ShowNotifications = false; edit.AppTheme = "Dark"; });
        Assert.Null(view.Presented.Last());
        Assert.Equal("Dark", view.Theme);
        harness.Notifications.Show(Notification("muted"));
        harness.Store.Update(null, edit => edit.ShowNotifications = true);
        Assert.Equal(2, view.Presented.Count);
        harness.Notifications.Show(Notification("fresh"));
        Assert.Equal("fresh", view.Presented.Last()!.Id);
        Assert.Single(harness.Views);
    }

    [Fact]
    public void Controller_DisposalFencesAlreadyQueuedWork()
    {
        using var harness = new Harness();
        harness.Store.Update(null, edit => edit.ShowDesktopCompanion = true);
        harness.Controller.Start();
        harness.Dispatcher.HasThreadAccess = false;
        harness.Dispatcher.RunEnqueuedImmediately = false;
        harness.Notifications.Show(Notification("queued"));
        harness.Controller.Dispose();
        harness.Dispatcher.FlushPending();
        Assert.True(harness.Views[0].Closed);
        Assert.Empty(harness.Views[0].Presented);
    }

    [Fact]
    public void Controller_DelayedSnapshotsCannotResurrectRemovedNotifications()
    {
        using var harness = new Harness();
        harness.Store.Update(null, edit => edit.ShowDesktopCompanion = true);
        harness.Controller.Start();
        harness.Dispatcher.HasThreadAccess = false;
        harness.Dispatcher.RunEnqueuedImmediately = false;
        harness.Notifications.Show(Notification("removed"));
        harness.Notifications.ClearAll();
        harness.Dispatcher.FlushPending();
        Assert.Empty(harness.Views[0].Presented);
    }

    [Fact]
    public void Controller_ViewFailureIsReportedOnce_AndOffOnAllowsRetry()
    {
        using var harness = new Harness();
        harness.FailCreation = true;
        harness.Store.Update(null, edit => edit.ShowDesktopCompanion = true);
        harness.Controller.Start();
        Assert.Single(harness.Failures);
        harness.Store.Update(null, edit => edit.AppTheme = "Light");
        harness.Notifications.Show(Notification("still-delivered"));
        Assert.Single(harness.Failures);
        Assert.Single(harness.Notifications.Snapshot.ActiveNotifications);
        harness.Store.Update(null, edit => edit.ShowDesktopCompanion = false);
        harness.FailCreation = false;
        harness.Store.Update(null, edit => edit.ShowDesktopCompanion = true);
        Assert.Single(harness.Views);
    }

    private sealed class View : IDesktopCompanionView
    {
        public event EventHandler? HideRequested;
        public List<AppNotification?> Presented { get; } = [];
        public bool Closed { get; private set; }
        public int ShowCount { get; private set; }
        public string? Theme { get; private set; }
        public void Show() => ShowCount++;
        public void ApplyTheme(string theme) => Theme = theme;
        public void Present(AppNotification? notification) => Presented.Add(notification);
        public void Close() => Closed = true;
        public void Hide() => HideRequested?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Harness : IDisposable
    {
        private readonly TempDir _temp = new();
        public RecordingUiDispatcher Dispatcher { get; } = new();
        public AppNotificationService Notifications { get; } = new();
        public SettingsStore Store { get; }
        public DesktopCompanionController Controller { get; }
        public List<View> Views { get; } = [];
        public List<Exception> Failures { get; } = [];
        public bool FailCreation { get; set; }

        public Harness()
        {
            Store = new(new SettingsManager(_temp.Path), Dispatcher);
            Controller = new(Store, Notifications, Dispatcher, () =>
            {
                if (FailCreation) throw new InvalidOperationException("Test view failure");
                var view = new View();
                Views.Add(view);
                return view;
            }, Failures.Add);
        }

        public void Dispose()
        {
            Controller.Dispose();
            Store.Dispose();
            Dispatcher.Dispose();
            _temp.Dispose();
        }
    }
}
