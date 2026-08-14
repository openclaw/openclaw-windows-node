using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Shared;
using OpenClaw.Shared.ExecApprovals;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

/// <summary>
/// Behavioral integration proof that composes the real composition root, the navigation
/// scope manager, and the registered transient page view models.
/// </summary>
public sealed class NavigationIntegrationTests
{
    private static ServiceProvider BuildRealContainer(out TempDir temp)
    {
        temp = new TempDir();
        var services = new ServiceCollection();
        var execApprovalsStore = new ExecApprovalsStore(temp.Path, NullLogger.Instance);
        services.AddOpenClawTrayCore(new AppServiceContext(
            new RecordingUiDispatcher(),
            new FakeAppCommands(),
            new SettingsManager(temp.Path),
            execApprovalsStore,
            new FakePermissionsPageRuntimeHost()));
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

            var a = Assert.IsType<SettingsPageViewModel>(manager.Navigate(typeof(SettingsPageViewModel), "a"));
            Assert.True(a.IsActive);

            var b = Assert.IsType<PermissionsPageViewModel>(manager.Navigate(typeof(PermissionsPageViewModel), "b"));
            Assert.False(a.IsActive);
            Assert.True(a.IsDisposed);
            Assert.True(b.IsActive);
            Assert.False(b.IsDisposed);

            manager.Reset();
            Assert.False(b.IsActive);
            Assert.True(b.IsDisposed);
            Assert.Null(manager.CurrentViewModel);

            var a2 = Assert.IsType<SettingsPageViewModel>(manager.Navigate(typeof(SettingsPageViewModel), "a2"));
            Assert.NotSame(a, a2);
            Assert.True(a2.IsActive);

            provider.Dispose();
            Assert.True(manager.IsDisposed);
            Assert.True(a2.IsDisposed);
        }
    }

    [Fact]
    public void RegistryPermissionsRoute_UsesTransientActivationLifecycle()
    {
        using var provider = BuildRealContainer(out var temp);
        using (temp)
        {
            Assert.Equal(HubPageKind.Permissions, HubPageRegistry.ResolvePage("permissions"));
            Assert.Equal(HubPageKind.Permissions, HubPageRegistry.ResolvePage("capabilities"));

            var manager = provider.GetRequiredService<NavigationScopeManager>();
            var first = Assert.IsType<PermissionsPageViewModel>(
                manager.Navigate(typeof(PermissionsPageViewModel), "permissions"));
            Assert.True(first.IsActive);

            manager.Navigate(typeof(SettingsPageViewModel), "settings");
            Assert.False(first.IsActive);
            Assert.True(first.IsDisposed);

            var reopened = Assert.IsType<PermissionsPageViewModel>(
                manager.Navigate(typeof(PermissionsPageViewModel), "capabilities"));
            Assert.NotSame(first, reopened);
            Assert.True(reopened.IsActive);

            manager.Reset();
            Assert.False(reopened.IsActive);
            Assert.True(reopened.IsDisposed);
        }
    }

    [Fact]
    public void FrameHandlerSimulation_ContainsActivationException_AndDisposesScope()
    {
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
            }
        }

        var escaped = Record.Exception(() => FrameHandler(typeof(ThrowingActivateViewModel)));

        Assert.Null(escaped);
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

            Assert.True(manager.IsDisposed);
            Assert.Throws<ObjectDisposedException>(() => manager.Navigate(typeof(SettingsPageViewModel), null));
        }
    }
}
