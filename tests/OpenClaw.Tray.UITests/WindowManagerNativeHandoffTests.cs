using Microsoft.UI.Xaml;
using OpenClaw.SetupEngine;
using OpenClawTray.Services;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class WindowManagerNativeHandoffTests(UIThreadFixture ui)
{
    [Fact]
    public async Task ShutdownRejectsNativeHandoffWithoutOpeningAWindowOrWaitingForConnection()
    {
        await ui.RunOnUIAsync(async () =>
        {
            var windows = new List<Window>();
            var manager = new WindowManager(ui.Dispatcher, new WindowManagerCallbacks(
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
                ApplyTheme: window => windows.Add(Assert.IsType<Window>(window))));
            manager.BeginShutdown();
            var completion = new SetupNativeCompletion(
                new(SetupCompletionIntent.Dashboard, "fixture-gateway", "fixture-endpoint",
                    "fixture/model", "main", 1,
                    IdentityBinding: new string('A', 64), SessionKey: "agent:main:main"),
                new(SetupNativeDestination.Chat, "agent:main:main"));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.ShowNativeSetupAsync(completion, CancellationToken.None));

            Assert.Equal("The application is shutting down.", error.Message);
            Assert.Empty(windows);
            Assert.Null(manager.ActiveHubWindow);
        });
    }
}
