using Microsoft.Extensions.DependencyInjection;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

/// <summary>
/// Behavioral guard for the composition root. Locks build-time validation, singleton
/// identity of App-owned instances, transient page-view-model lifetime, and — most
/// importantly — that the container never disposes App-owned pre-built instances
/// (no double-dispose) while it does dispose what it created.
/// </summary>
public sealed class AppServiceRegistrationTests
{
    private static ServiceProvider BuildProvider(
        out RecordingUiDispatcher dispatcher,
        out FakeAppCommands commands,
        out SettingsManager settings,
        out TempDir temp)
    {
        temp = new TempDir();
        dispatcher = new RecordingUiDispatcher();
        commands = new FakeAppCommands();
        settings = new SettingsManager(temp.Path);

        var services = new ServiceCollection();
        services.AddOpenClawTrayCore(new AppServiceContext(dispatcher, commands, settings));
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }

    [Fact]
    public void Build_ValidatesOnBuild_WithoutThrowing()
    {
        // Reaching this point means BuildServiceProvider(ValidateOnBuild: true) did not
        // throw, i.e. every registration (including transient page view models) is
        // constructable from the registered dependencies.
        var provider = BuildProvider(out _, out _, out _, out var temp);
        using (provider)
        using (temp)
        {
            Assert.NotNull(provider);
        }
    }

    [Fact]
    public void AppOwnedSingletons_ResolveToTheProvidedInstances()
    {
        var provider = BuildProvider(out var dispatcher, out var commands, out var settings, out var temp);
        using (provider)
        using (temp)
        {
            Assert.Same(dispatcher, provider.GetRequiredService<IUiDispatcher>());
            Assert.Same(commands, provider.GetRequiredService<IAppCommands>());
            Assert.Same(settings, provider.GetRequiredService<SettingsManager>());
        }
    }

    [Fact]
    public void PageViewModels_AreTransient_AndReceiveInjectedServices()
    {
        var provider = BuildProvider(out var dispatcher, out var commands, out var settings, out var temp);
        using (provider)
        using (temp)
        {
            using var scope = provider.CreateScope();
            var first = scope.ServiceProvider.GetRequiredService<SettingsPageViewModel>();
            var second = scope.ServiceProvider.GetRequiredService<SettingsPageViewModel>();

            Assert.NotSame(first, second);
            Assert.Same(dispatcher, first.Dispatcher);
            Assert.Same(commands, first.AppCommands);
            Assert.Same(settings, first.Settings);
        }
    }

    [Fact]
    public void PageViewModel_ResolvedFromScope_IsDisposedWithScope()
    {
        var provider = BuildProvider(out _, out _, out _, out var temp);
        using (provider)
        using (temp)
        {
            SettingsPageViewModel vm;
            using (var scope = provider.CreateScope())
            {
                vm = scope.ServiceProvider.GetRequiredService<SettingsPageViewModel>();
                Assert.False(vm.IsDisposed);
            }

            Assert.True(vm.IsDisposed);
        }
    }

    [Fact]
    public void Dispose_DoesNotDisposeAppOwnedInstanceSingletons()
    {
        var provider = BuildProvider(out var dispatcher, out var commands, out _, out var temp);
        using (temp)
        {
            var manager = provider.GetRequiredService<NavigationScopeManager>();

            provider.Dispose();

            // App-owned pre-built instances: the container must NOT dispose them, so App
            // remains their sole owner and there is no double-dispose. Both the dispatcher
            // and IAppCommands are IDisposable instance registrations, so asserting both
            // proves the container never disposes an instance it did not create.
            Assert.False(dispatcher.Disposed);
            Assert.False(commands.Disposed);
            // Container-created singleton: the container DOES dispose it.
            Assert.True(manager.IsDisposed);
        }
    }
}
