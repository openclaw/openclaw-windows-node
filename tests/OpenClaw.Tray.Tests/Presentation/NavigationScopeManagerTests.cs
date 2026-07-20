using Microsoft.Extensions.DependencyInjection;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

/// <summary>
/// Behavioral guard for the navigation-scope owner. Locks the activation /
/// deactivation / scope-disposal lifetime of transient page view models.
/// </summary>
public sealed class NavigationScopeManagerTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddTransient<FakeViewModel>();
        services.AddTransient<OtherFakeViewModel>();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }

    [Fact]
    public void Navigate_ResolvesAndActivatesViewModel()
    {
        using var provider = BuildProvider();
        using var manager = new NavigationScopeManager(provider);

        var vm = Assert.IsType<FakeViewModel>(manager.Navigate(typeof(FakeViewModel), "tag-a"));

        Assert.Same(vm, manager.CurrentViewModel);
        Assert.Equal(1, vm.ActivateCount);
        Assert.Equal("tag-a", vm.LastParameter);
        Assert.False(vm.Disposed);
    }

    [Fact]
    public void NavigatingAway_DeactivatesAndDisposesPreviousViewModel()
    {
        using var provider = BuildProvider();
        using var manager = new NavigationScopeManager(provider);

        var first = Assert.IsType<FakeViewModel>(manager.Navigate(typeof(FakeViewModel), null));
        var second = Assert.IsType<OtherFakeViewModel>(manager.Navigate(typeof(OtherFakeViewModel), null));

        Assert.Equal(1, first.DeactivateCount);
        Assert.True(first.Disposed);
        Assert.Same(second, manager.CurrentViewModel);
        Assert.Equal(1, second.ActivateCount);
        Assert.False(second.Disposed);
    }

    [Fact]
    public void Navigate_WithNullType_DeactivatesPreviousAndActivatesNothing()
    {
        using var provider = BuildProvider();
        using var manager = new NavigationScopeManager(provider);

        var first = Assert.IsType<FakeViewModel>(manager.Navigate(typeof(FakeViewModel), null));
        var result = manager.Navigate(null, null);

        Assert.Null(result);
        Assert.Null(manager.CurrentViewModel);
        Assert.Equal(1, first.DeactivateCount);
        Assert.True(first.Disposed);
    }

    [Fact]
    public void Dispose_DeactivatesAndDisposesCurrentViewModel()
    {
        using var provider = BuildProvider();
        var manager = new NavigationScopeManager(provider);
        var vm = Assert.IsType<FakeViewModel>(manager.Navigate(typeof(FakeViewModel), null));

        manager.Dispose();

        Assert.True(manager.IsDisposed);
        Assert.Equal(1, vm.DeactivateCount);
        Assert.True(vm.Disposed);
    }

    [Fact]
    public void Navigate_AfterDispose_Throws()
    {
        using var provider = BuildProvider();
        var manager = new NavigationScopeManager(provider);
        manager.Dispose();

        Assert.Throws<ObjectDisposedException>(() => manager.Navigate(typeof(FakeViewModel), null));
    }

    [Fact]
    public void NavigatingAway_DisposesScope_EvenWhenDeactivateThrows()
    {
        var services = new ServiceCollection();
        services.AddTransient<ThrowingDeactivateViewModel>();
        services.AddTransient<FakeViewModel>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
        using var manager = new NavigationScopeManager(provider);

        var vm = Assert.IsType<ThrowingDeactivateViewModel>(manager.Navigate(typeof(ThrowingDeactivateViewModel), null));

        // Navigating away triggers the throwing Deactivate; the scope must still be
        // disposed (finally), and the manager must not retain the failed view model.
        Assert.Throws<InvalidOperationException>(() => manager.Navigate(typeof(FakeViewModel), null));

        Assert.True(vm.Disposed);
        Assert.Null(manager.CurrentViewModel);
    }

    [Fact]
    public void Navigate_WhenActivateThrows_DisposesScopeAndClearsCurrent()
    {
        var created = new List<ThrowingActivateViewModel>();
        var services = new ServiceCollection();
        services.AddTransient(_ =>
        {
            var vm = new ThrowingActivateViewModel();
            created.Add(vm);
            return vm;
        });
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });
        using var manager = new NavigationScopeManager(provider);

        var ex = Record.Exception(() => manager.Navigate(typeof(ThrowingActivateViewModel), null));

        // Navigate must rethrow, must not retain a half-activated view model, and must
        // have disposed the just-created scope (and its transient view model), so no
        // half-activated scope leaks.
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Null(manager.CurrentViewModel);
        var vm = Assert.Single(created);
        Assert.True(vm.Disposed);
    }

    [Fact]
    public void Navigate_RejectsReentry_FromActivate()
    {
        NavigationScopeManager? manager = null;
        var services = new ServiceCollection();
        services.AddTransient<FakeViewModel>();
        services.AddTransient(_ => new ReentrantViewModel(manager!, reenterOnActivate: true, reenterOnDeactivate: false));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        manager = new NavigationScopeManager(provider);
        using var _ = manager;

        var vm = Assert.IsType<ReentrantViewModel>(manager.Navigate(typeof(ReentrantViewModel), null));

        // The re-entrant Navigate from within Activate must be rejected, not silently
        // installing an inner scope that the outer call would then leak.
        Assert.IsType<InvalidOperationException>(vm.ActivateException);
        Assert.Same(vm, manager.CurrentViewModel);
    }

    [Fact]
    public void Navigate_RejectsReentry_FromDeactivate()
    {
        NavigationScopeManager? manager = null;
        var services = new ServiceCollection();
        services.AddTransient<FakeViewModel>();
        services.AddTransient(_ => new ReentrantViewModel(manager!, reenterOnActivate: false, reenterOnDeactivate: true));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        manager = new NavigationScopeManager(provider);
        using var _ = manager;

        var first = Assert.IsType<ReentrantViewModel>(manager.Navigate(typeof(ReentrantViewModel), null));
        var second = Assert.IsType<FakeViewModel>(manager.Navigate(typeof(FakeViewModel), null));

        // The re-entrant Navigate from within the first VM's Deactivate must be rejected,
        // so the outer navigation to the second VM completes cleanly.
        Assert.IsType<InvalidOperationException>(first.DeactivateException);
        Assert.True(first.Disposed);
        Assert.Same(second, manager.CurrentViewModel);
    }

    [Fact]
    public void Dispose_DoesNotThrow_WhenDeactivateThrows()
    {
        var services = new ServiceCollection();
        services.AddTransient<ThrowingDeactivateViewModel>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var manager = new NavigationScopeManager(provider);
        var vm = Assert.IsType<ThrowingDeactivateViewModel>(manager.Navigate(typeof(ThrowingDeactivateViewModel), null));

        // Dispose must swallow a misbehaving Deactivate and still dispose the scope.
        manager.Dispose();

        Assert.True(manager.IsDisposed);
        Assert.True(vm.Disposed);
    }

    [Fact]
    public void Navigate_AtoB_DeactivatesAndDisposesA_BeforeActivatingB()
    {
        var log = new List<string>();
        var services = new ServiceCollection();
        services.AddTransient(_ => new OrderRecordingViewModel(log, "A"));
        // Distinguish B by a marker type so the map picks a different registration.
        services.AddTransient<BViewModel>(_ => new BViewModel(log));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var manager = new NavigationScopeManager(provider);

        manager.Navigate(typeof(OrderRecordingViewModel), null);
        manager.Navigate(typeof(BViewModel), null);

        // A must be deactivated and disposed exactly once, and both must happen before B activates.
        Assert.Equal(new[] { "activate:A", "deactivate:A", "dispose:A", "activate:B" }, log);
        Assert.Single(log, e => e == "deactivate:A");
        Assert.Single(log, e => e == "dispose:A");
    }

    [Fact]
    public void Reset_DeactivatesAndDisposesCurrent_AndAllowsRenavigation()
    {
        var services = new ServiceCollection();
        services.AddTransient<FakeViewModel>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var manager = new NavigationScopeManager(provider);

        var first = Assert.IsType<FakeViewModel>(manager.Navigate(typeof(FakeViewModel), null));
        manager.Reset();

        // Reset (window-close analog) tears down the current view model/scope but keeps
        // the manager usable.
        Assert.Equal(1, first.DeactivateCount);
        Assert.True(first.Disposed);
        Assert.Null(manager.CurrentViewModel);
        Assert.False(manager.IsDisposed);

        var second = Assert.IsType<FakeViewModel>(manager.Navigate(typeof(FakeViewModel), null));
        Assert.NotSame(first, second);
        Assert.Equal(1, second.ActivateCount);
    }

    [Fact]
    public void Reset_IsNoOp_WhenNothingActive()
    {
        using var provider = BuildProvider();
        using var manager = new NavigationScopeManager(provider);

        manager.Reset(); // must not throw

        Assert.Null(manager.CurrentViewModel);
        Assert.False(manager.IsDisposed);
    }

    [Fact]
    public void Reset_AfterDispose_Throws()
    {
        using var provider = BuildProvider();
        var manager = new NavigationScopeManager(provider);
        manager.Dispose();

        Assert.Throws<ObjectDisposedException>(() => manager.Reset());
    }
}
