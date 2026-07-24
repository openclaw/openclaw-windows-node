using Microsoft.Extensions.DependencyInjection;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

/// <summary>
/// Behavioral guard for the UI-thread dispatcher abstraction. Confirms view models
/// depend on <see cref="IUiDispatcher"/> via DI (not a concrete dispatcher queue) and
/// locks the basic dispatcher contract.
/// </summary>
public sealed class UiDispatcherContractTests
{
    [Fact]
    public void PageViewModel_ReceivesRegisteredDispatcher()
    {
        using var temp = new TempDir();
        var dispatcher = new RecordingUiDispatcher();

        var services = new ServiceCollection();
        services.AddOpenClawTrayCore(new AppServiceContext(
            dispatcher, new FakeAppCommands(), new SettingsManager(temp.Path)));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        using var scope = provider.CreateScope();
        var settingsVm = scope.ServiceProvider.GetRequiredService<SettingsPageViewModel>();
        var permissionsVm = scope.ServiceProvider.GetRequiredService<PermissionsPageViewModel>();

        // Both page view models resolve from the registered core, so their IUiDispatcher
        // dependency is satisfied from DI (not a concrete dispatcher queue). The placeholder
        // permissions view model still exposes the injected instance for a strong same-reference check.
        Assert.NotNull(settingsVm);
        Assert.Same(dispatcher, permissionsVm.Dispatcher);
    }

    [Fact]
    public void TryEnqueue_RunsActionAndReportsThreadAccess()
    {
        var dispatcher = new RecordingUiDispatcher();
        var ran = false;

        var queued = dispatcher.TryEnqueue(() => ran = true);

        Assert.True(queued);
        Assert.True(ran);
        Assert.True(dispatcher.HasThreadAccess);
        Assert.Equal(1, dispatcher.EnqueuedCount);
    }
}
