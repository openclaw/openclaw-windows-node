using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using OpenClawTray.Services;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class WindowManagerRuntimeAnchorTests(UIThreadFixture ui)
{
    [Fact]
    public async Task RuntimeAnchor_IsHiddenReusedAndClosedWithoutOpeningAHub()
    {
        await ui.RunOnUIAsync(async () =>
        {
            var windows = new List<Window>();
            var manager = CreateManager(window => windows.Add(Assert.IsType<Window>(window)));
            try
            {
                manager.InitializeRuntimeAnchor();
                var anchor = Assert.Single(windows);
                Assert.False(anchor.AppWindow.IsVisible);
                Assert.Null(manager.ActiveHubWindow);

                manager.InitializeRuntimeAnchor();
                Assert.Same(anchor, Assert.Single(windows));
                var closed = false;
                anchor.Closed += (_, _) => closed = true;
                await manager.CloseForShutdownAsync();
                Assert.True(closed);
                manager.InitializeRuntimeAnchor();
                Assert.Single(windows);
                Assert.Null(manager.ActiveHubWindow);
            }
            finally
            {
                await manager.CloseForShutdownAsync();
            }
        });
    }

    [Fact]
    public async Task ShutdownBeforeStartup_DoesNotCreateARuntimeAnchor()
    {
        await ui.RunOnUIAsync(() =>
        {
            var windows = new List<Window>();
            var manager = CreateManager(window => windows.Add(Assert.IsType<Window>(window)));
            manager.BeginShutdown();
            manager.InitializeRuntimeAnchor();
            Assert.Empty(windows);
            Assert.Null(manager.ActiveHubWindow);
        });
    }

    [Fact]
    public async Task MainWindowVisibility_RequiresEverySetupWindowMinimizedHiddenOrClosed()
    {
        await ui.RunOnUIAsync(async () =>
        {
            var windows = new List<Window>();
            bool? visible = null;
            var manager = CreateManager(window => windows.Add(Assert.IsAssignableFrom<Window>(window)),
                value => visible = value);
            try
            {
                manager.InitializeRuntimeAnchor();
                Assert.False(manager.HasVisibleMainWindow);
                manager.ShowNativeSetupStartupProgress();
                manager.ShowSetupRestartProgress();
                Assert.Equal(3, windows.Count);
                var first = windows[1];
                var second = windows[2];
                await TestSupport.WaitForRenderedConditionAsync(
                    () => first.AppWindow.IsVisible && second.AppWindow.IsVisible && visible == true,
                    "both setup windows visible");
                var firstPresenter = Assert.IsType<OverlappedPresenter>(first.AppWindow.Presenter);
                var secondPresenter = Assert.IsType<OverlappedPresenter>(second.AppWindow.Presenter);
                firstPresenter.Minimize();
                await TestSupport.WaitForRenderedConditionAsync(
                    () => firstPresenter.State == OverlappedPresenterState.Minimized,
                    "first setup minimized");
                Assert.True(manager.HasVisibleMainWindow);
                Assert.True(visible);
                secondPresenter.Minimize();
                await TestSupport.WaitForRenderedConditionAsync(
                    () => !manager.HasVisibleMainWindow && visible == false,
                    "all setup windows minimized");
                firstPresenter.Restore(activateWindow: false);
                await TestSupport.WaitForRenderedConditionAsync(
                    () => manager.HasVisibleMainWindow && visible == true,
                    "restored setup blocks lobster");
                first.Close();
                await TestSupport.WaitForRenderedConditionAsync(
                    () => !manager.HasVisibleMainWindow && visible == false,
                    "closed setup no longer blocks lobster");
                secondPresenter.Restore(activateWindow: false);
                await TestSupport.WaitForRenderedConditionAsync(
                    () => manager.HasVisibleMainWindow && visible == true,
                    "remaining setup restored");
                second.AppWindow.Hide();
                await TestSupport.WaitForRenderedConditionAsync(
                    () => !manager.HasVisibleMainWindow && visible == false,
                    "hidden setup no longer blocks lobster");
                second.Close();
                Assert.False(manager.HasVisibleMainWindow);
            }
            finally
            {
                await manager.CloseForShutdownAsync();
            }
        });
    }

    private WindowManager CreateManager(Action<Window?> applyTheme, Action<bool>? visibilityChanged = null) =>
        new(ui.Dispatcher, new WindowManagerCallbacks(
            GetAppState: () => null,
            GetAppNotificationService: () => null,
            GetConnectionManager: () => null,
            GetGatewayRegistry: () => null,
            GetSettings: () => null,
            GetGatewayDirectConnectService: () => null,
            GetLocalAiRuntime: () => null,
            GetNodeService: () => null,
            GetPageActivator: () => null,
            GetPendingChatSessionKey: () => null,
            GetStartupArgs: () => null,
            IsDeepLinkArg: _ => false,
            RequiresSetup: () => false,
            Connect: () => { },
            Disconnect: () => { },
            SettingsSaved: (_, _) => { },
            AdvancedSetupRequested: (_, _) => { },
            SetupCompleted: (_, _) => { },
            ApplyTheme: applyTheme,
            MainWindowVisibilityChanged: visibilityChanged));
}
