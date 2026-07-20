using Microsoft.Extensions.DependencyInjection;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

/// <summary>
/// Behavioral integration proof that composes the real composition root, the navigation
/// scope manager, and the registered transient page view models — driving the full
/// lifecycle (open → navigate A/B → close → reopen → shutdown) and asserting the
/// ownership invariants end to end without hosting HubWindow.
/// </summary>
public sealed class NavigationIntegrationTests
{
    private static ServiceProvider BuildRealContainer(out TempDir temp)
    {
        temp = new TempDir();
        var services = new ServiceCollection();
        services.AddOpenClawTrayCore(new AppServiceContext(
            new RecordingUiDispatcher(), new FakeAppCommands(), new SettingsManager(temp.Path)));
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }

    [Fact]
    public void FullLifecycle_OpenNavigateCloseReopenShutdown_HonorsOwnership()
    {
        var provider = BuildRealContainer(out var temp);
        using (temp)
        {
            var manager = provider.GetRequiredService<NavigationScopeManager>();

            // open + navigate A (Settings)
            var a = Assert.IsType<SettingsPageViewModel>(manager.Navigate(typeof(SettingsPageViewModel), "a"));
            Assert.True(a.IsActive);

            // navigate B (Permissions) — A deactivated + disposed before B is active
            var b = Assert.IsType<PermissionsPageViewModel>(manager.Navigate(typeof(PermissionsPageViewModel), "b"));
            Assert.False(a.IsActive);
            Assert.True(a.IsDisposed);
            Assert.True(b.IsActive);
            Assert.False(b.IsDisposed);

            // close (Hub close → Reset) — B deactivated + disposed, nothing current
            manager.Reset();
            Assert.False(b.IsActive);
            Assert.True(b.IsDisposed);
            Assert.Null(manager.CurrentViewModel);

            // reopen — fresh A instance, not the disposed one
            var a2 = Assert.IsType<SettingsPageViewModel>(manager.Navigate(typeof(SettingsPageViewModel), "a2"));
            Assert.NotSame(a, a2);
            Assert.True(a2.IsActive);

            // shutdown — dispose the provider; the container disposes the manager it created,
            // which tears down the current scope/view model.
            provider.Dispose();
            Assert.True(manager.IsDisposed);
            Assert.True(a2.IsDisposed);
        }
    }

    [Fact]
    public void FrameHandlerSimulation_ContainsActivationException_AndDisposesScope()
    {
        // Mirror HubWindow.OnContentFrameNavigated: the activator call is wrapped in a
        // try/catch so an activation throw cannot escape the frame-navigated handler.
        var created = new List<ThrowingActivateViewModel>();
        var services = new ServiceCollection();
        services.AddTransient(_ =>
        {
            var vm = new ThrowingActivateViewModel();
            created.Add(vm);
            return vm;
        });
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var manager = new NavigationScopeManager(provider);

        void FrameHandler(Type vmType)
        {
            try
            {
                manager.Navigate(vmType, null);
            }
            catch
            {
                // Contained — mirrors the HubWindow try/catch-log around the activation hook.
            }
        }

        var escaped = Record.Exception(() => FrameHandler(typeof(ThrowingActivateViewModel)));

        Assert.Null(escaped);
        // The half-activated scope was disposed, not leaked.
        Assert.Null(manager.CurrentViewModel);
        Assert.True(Assert.Single(created).Disposed);
    }

    [Fact]
    public void AfterShutdown_LateNavigation_DoesNotResolveFromDisposedProvider()
    {
        var provider = BuildRealContainer(out var temp);
        using (temp)
        {
            var manager = provider.GetRequiredService<NavigationScopeManager>();
            manager.Navigate(typeof(SettingsPageViewModel), null);

            provider.Dispose();

            // The manager is disposed with the provider, so a late navigation cannot
            // create a new scope / resolve a view model from the disposed provider.
            Assert.True(manager.IsDisposed);
            Assert.Throws<ObjectDisposedException>(() => manager.Navigate(typeof(SettingsPageViewModel), null));
        }
    }
}
