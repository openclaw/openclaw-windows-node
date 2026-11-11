// Executes the shipped, file-local viewport owner on real WinUI controls.
// Run with the native UITests UI-thread fixture; these handler tests do not
// establish installed-upgrade or visible disconnect/retry acceptance.
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.UITests;

/// <summary>Exercises initial admission and retirement through the shipped viewport owner.</summary>
[Collection(UICollection.Name)]
public sealed class InitialTailPositionerLifecycleTests(UIThreadFixture ui)
{
    [Fact]
    public async Task PendingInitialLanding_SurvivesNonTailObservationAndReload_RetiringOldDispatch()
    {
        var items = await MountReaderAsync();
        Owner? owner = null;
        try
        {
            var oldVersion = await ui.RunOnUIAsync(() =>
            {
                owner = BeginInitialAtReader(items);
                var version = owner.Field<int>("_version");
                var requestType = typeof(OpenClawChatCoordinator).Assembly.GetType(
                    "OpenClawTray.Chat.InitialTailNavigationRequest", throwOnError: true)!;
                owner.Call("DispatchTailRequest", Activator.CreateInstance(requestType, version)!, false);
                Assert.Equal(1, owner.Field<int>("_pendingDispatches"));
                owner.Call("OnUnloaded", items, new RoutedEventArgs());
                Assert.True(owner.Field<bool>("_initialPending"));
                Assert.Equal(version + 1, owner.Field<int>("_version"));
                Assert.False(owner.Field<bool>("_awaitingLayout"));
                Assert.Null(owner.Field<object?>("_scrollView"));
                return Task.FromResult(version);
            });

            // Drain the real dispatcher after unload. The retired initial request
            // must not start a bring or turn the reader into a follower.
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.Equal(0, owner!.Field<int>("_pendingDispatches"));
                Assert.Equal(oldVersion + 1, owner.Field<int>("_version"));
                Assert.False(owner.Field<bool>("_following"));
                Assert.True(owner.Field<bool>("_initialPending"));
                owner.Call("OnLoaded", items, new RoutedEventArgs());
                Assert.Same(items.ScrollView, owner.Field<object>("_scrollView"));
                Assert.True(owner.Field<bool>("_awaitingLayout"));
            });
        }
        finally { await CleanupAsync(owner); }
    }

    [Theory]
    [InlineData("CancelPendingNavigation")]
    [InlineData("CompleteInitialLanding")]
    public async Task ClearedInitialLanding_DoesNotRearmReaderOnReload(string clearBoundary)
    {
        var items = await MountReaderAsync();
        Owner? owner = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                owner = BeginInitialAtReader(items);
                owner.Call(clearBoundary);
                Assert.False(owner.Field<bool>("_initialPending"));
                owner.Call("OnUnloaded", items, new RoutedEventArgs());
                owner.Call("OnLoaded", items, new RoutedEventArgs());
                Assert.Same(items.ScrollView, owner.Field<object>("_scrollView"));
                Assert.False(owner.Field<bool>("_initialPending"));
                Assert.False(owner.Field<bool>("_awaitingLayout"));
                Assert.False(owner.Field<bool>("_following"));
            });
        }
        finally { await CleanupAsync(owner); }
    }

    [Fact]
    public async Task ReplacementInitialRequest_ReloadAdmitsOnlyCurrentRequest()
    {
        var items = await MountReaderAsync();
        Owner? owner = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                owner = BeginInitialAtReader(items);
                var version = owner.Field<int>("_version");
                owner.Request(238, "replacement", "row-238");
                Assert.Equal(version + 1, owner.Field<int>("_version"));
                owner.Call("OnViewChanged", items.ScrollView!, new object());
                owner.Call("OnUnloaded", items, new RoutedEventArgs());
                owner.Call("OnLoaded", items, new RoutedEventArgs());
                Assert.Equal("replacement", owner.Field<string>("_requestKey"));
                Assert.Equal(238, owner.Field<int>("_tailIndex"));
                Assert.True(owner.Field<bool>("_initialPending"));
                Assert.True(owner.Field<bool>("_awaitingLayout"));
            });
        }
        finally { await CleanupAsync(owner); }
    }

    [Fact]
    public async Task ReaderHistoryRefresh_DoesNotCreateNewInitialAdmission()
    {
        var items = await MountReaderAsync();
        Owner? owner = null;
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                owner = BeginInitialAtReader(items);
                owner.Call("CancelPendingNavigation");
                owner.Request(239, "history-refresh", "row-239");
                owner.Call("OnUnloaded", items, new RoutedEventArgs());
                owner.Call("OnLoaded", items, new RoutedEventArgs());
                Assert.False(owner.Field<bool>("_initialPending"));
                Assert.False(owner.Field<bool>("_awaitingLayout"));
                Assert.False(owner.Field<bool>("_following"));
            });
        }
        finally { await CleanupAsync(owner); }
    }

    [Fact]
    public async Task ReaderMountBarrier_CannotCompleteFromDispatcherDrainBeforeNativeLoaded()
    {
        ItemsView? items = null;
        Grid? detachedHost = null;
        Task? mounted = null;
        using var cancellation = new CancellationTokenSource();
        try
        {
            await ui.RunOnUIAsync(() =>
            {
                ui.Container.Children.Clear();
                detachedHost = new Grid();
                items = CreateReader();
                mounted = AwaitReaderMountAsync(items, () => detachedHost.Children.Add(items), cancellation.Token);
            });
            // Reproduce the former precondition deterministically: a dispatcher
            // drain completes even though this native tree has not been loaded.
            await ui.YieldToRenderAsync();
            await ui.RunOnUIAsync(() =>
            {
                Assert.False(items!.IsLoaded);
                Assert.False(mounted!.IsCompleted);
                ui.Container.Children.Add(detachedHost!);
            });
            await mounted!;
            await ui.RunOnUIAsync(() => AssertReaderMounted(items!));
        }
        finally
        {
            try
            {
                cancellation.Cancel();
                if (mounted is not null)
                {
                    try { await mounted; }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    { /* Join the canceled test-owned wait; do not suppress timeout or assertion failures. */ }
                }
            }
            finally { await CleanupAsync(null); }
        }
    }

    private Task<ItemsView> MountReaderAsync() => ui.RunOnUIAsync(async () =>
    {
        ui.Container.Children.Clear();
        var items = CreateReader();
        try
        {
            await AwaitReaderMountAsync(items, () => ui.Container.Children.Add(items));
            AssertReaderMounted(items);
            return items;
        }
        catch
        {
            ui.Container.Children.Remove(items);
            throw;
        }
    });

    private static ItemsView CreateReader() => new()
    {
        Width = 320, Height = 240,
        Layout = new StackLayout(),
        ItemsSource = Enumerable.Range(0, 240).Select(i => $"row-{i}").ToArray(),
        ItemTemplate = (DataTemplate)XamlReader.Load(
            """<DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><ItemContainer><TextBlock Text="{Binding}" Height="40"/></ItemContainer></DataTemplate>""")
    };

    private static TimeSpan MountTimeout =>
        int.TryParse(Environment.GetEnvironmentVariable("OPENCLAW_UI_RENDER_WAIT_TIMEOUT_SECONDS"), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(10);

    // Must run on the fixture UI thread. Subscribe before insertion, then join
    // actual native Loaded/layout readiness rather than assuming a queue drain
    // scheduled the next XAML load pass. Every subscription is retired on failure.
    private static async Task AwaitReaderMountAsync(ItemsView items, Action attach, CancellationToken cancellationToken = default)
    {
        var mounted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe()
        {
            if (items.IsLoaded && items.XamlRoot is not null &&
                items.ScrollView is { IsLoaded: true, ViewportHeight: > 0, ScrollableHeight: > 60 })
                mounted.TrySetResult();
        }
        void Loaded(object sender, RoutedEventArgs args) => Observe();
        void Layout(object? sender, object args) => Observe();
        items.Loaded += Loaded;
        items.LayoutUpdated += Layout;
        try
        {
            attach();
            items.UpdateLayout();
            Observe();
            await mounted.Task.WaitAsync(MountTimeout, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Reader mount did not reach native readiness: loaded={items.IsLoaded}, " +
                $"hasXamlRoot={items.XamlRoot is not null}, scrollLoaded={items.ScrollView?.IsLoaded}, " +
                $"viewport={items.ScrollView?.ViewportHeight}, extent={items.ScrollView?.ScrollableHeight}.", ex);
        }
        finally
        {
            items.Loaded -= Loaded;
            items.LayoutUpdated -= Layout;
        }
    }

    private static void AssertReaderMounted(ItemsView items)
    {
        Assert.True(items.IsLoaded);
        Assert.NotNull(items.ScrollView);
        Assert.True(items.ScrollView.ScrollableHeight > 60);
        Assert.Equal(0, items.ScrollView.VerticalOffset);
    }

    private static Owner BeginInitialAtReader(ItemsView items)
    {
        var owner = new Owner(items);
        owner.Request(239, "initial", "row-239");
        Assert.True(owner.Field<bool>("_initialPending"));
        owner.Call("OnViewChanged", items.ScrollView!, new object());
        Assert.False(owner.Field<bool>("_following"));
        Assert.True(owner.Field<bool>("_initialPending"));
        return owner;
    }

    private Task CleanupAsync(Owner? owner) => ui.RunOnUIAsync(() =>
    {
        owner?.Dispose();
        ui.Container.Children.Clear();
    });

    // Reflection reaches the real file-local owner, not a copied state machine.
    private sealed class Owner : IDisposable
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly Type _type = typeof(OpenClawChatCoordinator).Assembly.GetTypes().Single(
            t => t.Name.EndsWith("__InitialTailPositioner", StringComparison.Ordinal));
        private readonly object _instance;

        public Owner(ItemsView items) => _instance = Activator.CreateInstance(
            _type, Members, binder: null, args: [items], culture: null)!;
        public T Field<T>(string name) => (T)_type.GetField(name, Members)!.GetValue(_instance)!;
        public void Call(string name, params object[] args) =>
            _type.GetMethod(name, Members)!.Invoke(_instance, args);
        public void Request(int index, string key, string tailKey) =>
            Call("Request", index, 240, key, tailKey, false, 0L, false);
        public void Dispose() => ((IDisposable)_instance).Dispose();
    }
}
