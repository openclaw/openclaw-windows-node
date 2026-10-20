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

    private WindowManager CreateManager(Action<Window?> applyTheme) =>
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
            ApplyTheme: applyTheme));
}
