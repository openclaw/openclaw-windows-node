using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Shared;
using OpenClaw.Shared.ExecApprovals;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

/// <summary>
/// Behavioral guard for the UI-thread dispatcher abstraction.
/// </summary>
public sealed class UiDispatcherContractTests
{
    [Fact]
    public void PageViewModel_ReceivesRegisteredDispatcher()
    {
        using var temp = new TempDir();
        var dispatcher = new RecordingUiDispatcher();
        var execApprovalsStore = new ExecApprovalsStore(temp.Path, NullLogger.Instance);

        var services = new ServiceCollection();
        services.AddOpenClawTrayCore(new AppServiceContext(
            dispatcher,
            new FakeAppCommands(),
            new SettingsManager(temp.Path),
            execApprovalsStore,
            new FakePermissionsPageRuntimeHost()));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        using var scope = provider.CreateScope();
        var permissionsVm = scope.ServiceProvider.GetRequiredService<PermissionsPageViewModel>();

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
