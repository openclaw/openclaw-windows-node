using System.Reflection;
using OpenClaw.Shared.ExecApprovals;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class SettingsSharedStateContractTests
{
    private static SettingsStore NewStore(out SettingsManager settings, out RecordingUiDispatcher dispatcher, out TempDir temp)
    {
        temp = new TempDir();
        settings = new SettingsManager(temp.Path);
        dispatcher = new RecordingUiDispatcher();
        return new SettingsStore(settings, dispatcher);
    }

    private static PermissionsPageViewModel NewPermissionsVm(
        ISettingsStore store,
        SettingsManager settings,
        RecordingUiDispatcher dispatcher,
        TempDir temp)
    {
        var runtimeHost = new FakePermissionsPageRuntimeHost();
        var runtimeSource = new PermissionsPageRuntimeSource(runtimeHost);
        var execStore = new ExecApprovalsStore(temp.Path, OpenClaw.Shared.NullLogger.Instance);
        return new PermissionsPageViewModel(store, execStore, new FakeAppCommands(), dispatcher, runtimeSource);
    }

    [Fact]
    public void ChangedEvent_UsesVersionedOriginAwareEventArgs()
    {
        var changed = typeof(ISettingsStore).GetEvent(nameof(ISettingsStore.Changed));
        Assert.NotNull(changed);

        var handlerType = changed!.EventHandlerType;
        Assert.NotNull(handlerType);
        Assert.True(
            handlerType!.IsGenericType &&
            handlerType.GetGenericTypeDefinition() == typeof(EventHandler<>),
            "Changed should expose typed event args so listeners can distinguish origin and version.");

        var argsType = handlerType.GetGenericArguments()[0];
        Assert.Equal(typeof(SettingsChangedEventArgs), argsType);
        Assert.NotNull(argsType.GetProperty(nameof(SettingsChangedEventArgs.Origin), BindingFlags.Instance | BindingFlags.Public));
        Assert.NotNull(argsType.GetProperty(nameof(SettingsChangedEventArgs.Version), BindingFlags.Instance | BindingFlags.Public));
    }

    [Fact]
    public void ExternalSave_UsesNullOrigin_AndReloadsBothActiveViewModels()
    {
        using var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        {
            var firstVm = new SettingsPageViewModel(store, new FakeAppCommands());
            var secondVm = new SettingsPageViewModel(store, new FakeAppCommands());
            firstVm.Activate(null);
            secondVm.Activate(null);

            var firstExternal = 0;
            var secondExternal = 0;
            SettingsChangedEventArgs? changed = null;
            firstVm.ExternalChanged += (_, _) => firstExternal++;
            secondVm.ExternalChanged += (_, _) => secondExternal++;
            store.Changed += (_, args) => changed = args;

            settings.NotifyBuild = false;
            settings.Save();

            Assert.NotNull(changed);
            Assert.Null(changed!.Origin);
            Assert.Equal(1, changed.Version);
            Assert.Equal(1, firstExternal);
            Assert.Equal(1, secondExternal);
            Assert.False(firstVm.NotifyBuild);
            Assert.False(secondVm.NotifyBuild);
        }
    }

    [Fact]
    public void TwoActiveSettingsPageViewModels_IgnoreOnlyOwnWrites_InBothDirections()
    {
        using var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        {
            var firstCommands = new FakeAppCommands();
            var secondCommands = new FakeAppCommands();
            var firstVm = new SettingsPageViewModel(store, firstCommands);
            var secondVm = new SettingsPageViewModel(store, secondCommands);
            firstVm.Activate(null);
            secondVm.Activate(null);

            var firstExternal = 0;
            var secondExternal = 0;
            firstVm.ExternalChanged += (_, _) => firstExternal++;
            secondVm.ExternalChanged += (_, _) => secondExternal++;

            firstVm.NotifyStock = false;

            Assert.False(settings.NotifyStock);
            Assert.Equal(0, firstExternal);
            Assert.Equal(1, secondExternal);
            Assert.False(secondVm.NotifyStock);

            secondVm.GlobalHotkeyEnabled = false;

            Assert.False(settings.GlobalHotkeyEnabled);
            Assert.Equal(1, firstExternal);
            Assert.Equal(1, secondExternal);
            Assert.False(firstVm.GlobalHotkeyEnabled);
        }
    }

    [Fact]
    public void SettingsPageAndPermissionsPage_IgnoreOnlyOwnWrites_InBothDirections()
    {
        using var store = NewStore(out var settings, out var dispatcher, out var temp);
        using (temp)
        {
            var settingsVm = new SettingsPageViewModel(store, new FakeAppCommands());
            var permissionsVm = NewPermissionsVm(store, settings, dispatcher, temp);
            settingsVm.Activate(null);
            permissionsVm.Activate(null);

            var settingsExternal = 0;
            var permissionsExternal = 0;
            settingsVm.ExternalChanged += (_, _) => settingsExternal++;
            permissionsVm.ExternalChanged += (_, _) => permissionsExternal++;

            settingsVm.NotifyStock = false;

            Assert.False(settings.NotifyStock);
            Assert.Equal(0, settingsExternal);
            Assert.Equal(1, permissionsExternal);

            permissionsVm.NodeModeEnabled = true;

            Assert.True(settings.EnableNodeMode);
            Assert.Equal(1, settingsExternal);
            Assert.Equal(1, permissionsExternal);
        }
    }

    [Fact]
    public void PermissionsPageViewModel_ReceivesOneExternalUpdate_PerDistinctAppSurfaceOrigin()
    {
        using var store = NewStore(out var settings, out var dispatcher, out var temp);
        using (temp)
        {
            var permissionsVm = NewPermissionsVm(store, settings, dispatcher, temp);
            permissionsVm.Activate(null);

            var externalChanges = 0;
            permissionsVm.ExternalChanged += (_, _) => externalChanges++;

            var trayOrigin = store.CreateOrigin();
            var hubOrigin = store.CreateOrigin();
            var connectionOrigin = store.CreateOrigin();
            var appOrigin = store.CreateOrigin();

            store.Update(trayOrigin, edit => edit.EnableNodeMode = true);
            store.Update(hubOrigin, edit => edit.NodeCameraEnabled = true);
            store.Update(connectionOrigin, edit => edit.NodeCanvasEnabled = true);
            store.Update(appOrigin, edit => edit.EnableMcpServer = true);

            Assert.Equal(4, externalChanges);
            Assert.True(permissionsVm.NodeModeEnabled);
            Assert.True(permissionsVm.Capabilities.Single(capability => capability.Key == PermissionsCapabilityKey.Camera).IsOn);
            Assert.True(permissionsVm.Capabilities.Single(capability => capability.Key == PermissionsCapabilityKey.Canvas).IsOn);
            Assert.True(permissionsVm.McpEnabled);
        }
    }

    [Fact]
    public void AppOwnedDirectWrite_CarriesOrigin_AndUpdatesOtherActiveViewModel()
    {
        using var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        {
            var firstVm = new SettingsPageViewModel(store, new SelfWritingAppCommands(store));
            var secondVm = new SettingsPageViewModel(store, new SelfWritingAppCommands(store));
            firstVm.Activate(null);
            secondVm.Activate(null);

            var firstExternal = 0;
            var secondExternal = 0;
            var events = new List<SettingsChangedEventArgs>();
            firstVm.ExternalChanged += (_, _) => firstExternal++;
            secondVm.ExternalChanged += (_, _) => secondExternal++;
            store.Changed += (_, args) => events.Add(args);

            firstVm.AutoStart = false;

            Assert.False(settings.AutoStart);
            Assert.Equal(0, firstExternal);
            Assert.Equal(1, secondExternal);
            Assert.False(secondVm.AutoStart);
            Assert.Single(events);
            Assert.All(events, args => Assert.NotNull(args.Origin));
        }
    }

    [Fact]
    public void Version_IsMonotonic_AcrossMixedWrites()
    {
        using var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        {
            var versions = new List<long>();
            var vm = new SettingsPageViewModel(store, new SelfWritingAppCommands(store));
            vm.Activate(null);
            store.Changed += (_, args) => versions.Add(args.Version);

            settings.Save();
            vm.ShowNotifications = false;
            vm.AutoStart = false;

            Assert.Equal(new long[] { 1, 2, 3 }, versions);
            Assert.Equal(3, store.Current.Version);
        }
    }

    [Fact]
    public void SettingsPageViewModel_OwnWriteWatermarkRejectsOlderQueuedExternalSnapshot()
    {
        using var store = NewStore(out var settings, out var dispatcher, out var temp);
        using (temp)
        {
            var vm = new SettingsPageViewModel(store, new FakeAppCommands());
            vm.Activate(null);

            dispatcher.HasThreadAccess = false;
            dispatcher.RunEnqueuedImmediately = false;
            settings.NotifyBuild = false;
            settings.Save();

            dispatcher.HasThreadAccess = true;
            vm.GlobalHotkeyEnabled = false;
            Assert.Equal(2, store.Current.Version);

            dispatcher.FlushPending();

            Assert.False(settings.GlobalHotkeyEnabled);
            Assert.False(store.Current.GlobalHotkeyEnabled);
            Assert.False(vm.GlobalHotkeyEnabled);
            Assert.False(vm.NotifyBuild);
        }
    }

    [Fact]
    public void PermissionsPageViewModel_OwnWriteWatermarkRejectsOlderQueuedUiApplication()
    {
        using var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        using (var viewModelDispatcher = new RecordingUiDispatcher())
        using (var vm = NewPermissionsVm(store, settings, viewModelDispatcher, temp))
        {
            vm.Activate(null);

            viewModelDispatcher.HasThreadAccess = false;
            viewModelDispatcher.RunEnqueuedImmediately = false;
            settings.NodeCameraEnabled = true;
            settings.Save();

            viewModelDispatcher.HasThreadAccess = true;
            vm.NodeModeEnabled = true;
            Assert.Equal(2, store.Current.Version);

            viewModelDispatcher.FlushPending();

            Assert.True(settings.EnableNodeMode);
            Assert.True(store.Current.EnableNodeMode);
            Assert.True(vm.NodeModeEnabled);
            Assert.True(vm.Capabilities.Single(
                capability => capability.Key == PermissionsCapabilityKey.Camera).IsOn);
        }
    }

    [Fact]
    public async Task ConcurrentDisjointUpdates_AreSerialized_AndPreserveBothFields()
    {
        using var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        {
            var originA = store.CreateOrigin();
            var originB = store.CreateOrigin();
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var first = Task.Run(async () =>
            {
                await go.Task;
                store.Update(originA, edit => edit.NotifyHealth = false);
            });

            var second = Task.Run(async () =>
            {
                await go.Task;
                store.Update(originB, edit => edit.NotifyBuild = false);
            });

            go.SetResult();
            await Task.WhenAll(first, second);

            Assert.False(settings.NotifyHealth);
            Assert.False(settings.NotifyBuild);
            Assert.Equal(2, store.Current.Version);
        }
    }

    [Fact]
    public void StaleSettingsPageViewModel_Write_DoesNotReplayWholeSnapshot()
    {
        using var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        {
            var staleVm = new SettingsPageViewModel(store, new FakeAppCommands());
            staleVm.Activate(null);
            staleVm.Deactivate();

            var activeVm = new SettingsPageViewModel(store, new FakeAppCommands());
            activeVm.Activate(null);
            activeVm.NotifyBuild = false;

            staleVm.GlobalHotkeyEnabled = false;

            Assert.False(settings.NotifyBuild);
            Assert.False(settings.GlobalHotkeyEnabled);
        }
    }

    [Fact]
    public void Dispose_Unsubscribes_FromSettingsManager()
    {
        var store = NewStore(out var settings, out _, out var temp);
        using (temp)
        {
            var changed = 0;
            store.Changed += (_, _) => changed++;

            store.Dispose();
            settings.Save();

            Assert.Equal(0, changed);
        }
    }

    [Fact]
    public void SettingsStoreSource_DoesNotUseThreadStaticSuppression()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.GetRepositoryRoot(),
            "src",
            "OpenClaw.Tray.WinUI",
            "Presentation",
            "SettingsStore.cs"));

        Assert.DoesNotContain("[ThreadStatic]", source);
        Assert.DoesNotContain("AsyncLocal", source);
    }

    [Fact]
    public void PermissionsPageViewModel_UsesSharedSettingsStoreInsteadOfConcreteManager()
    {
        var ctor = Assert.Single(
            typeof(PermissionsPageViewModel).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        var parameterTypes = ctor.GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        Assert.Contains(typeof(ISettingsStore), parameterTypes);
        Assert.Contains(typeof(IExecApprovalsPresentationStore), parameterTypes);
        Assert.Contains(typeof(IPermissionsPageRuntimeSource), parameterTypes);
        Assert.DoesNotContain(typeof(SettingsManager), parameterTypes);
    }
}
