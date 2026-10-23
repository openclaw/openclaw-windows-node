using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClaw.Shared;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Gateway;
using OpenClawTray.Chat;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation.Adapters;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class ModelSignInProofTests(UIThreadFixture ui)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData(ElementTheme.Light, false, false)]
    [InlineData(ElementTheme.Dark, false, false)]
    [InlineData(ElementTheme.Light, true, false)]
    [InlineData(ElementTheme.Light, false, true)]
    public async Task ModelPicker_OpensNativeSignInAndSettlesThroughRealGatewayClient(ElementTheme theme, bool cancel, bool stale)
    {
        using var data = new TempDirectory();
        var fixture = new ModelSignInFixture();
        const string token = "model-sign-in-synthetic-loopback-token";
        await using var server = await FixtureGatewayServer.StartAsync(GatewayScenario.CreateModelSignIn(fixture), token);
        using var client = new OpenClawGatewayClient(server.Endpoint.AbsoluteUri, token, NullLogger.Instance,
            identityPath: Path.Combine(data.Path, "identity"), ignoreStoredDeviceToken: true,
            persistHandshakeDeviceTokens: false);
        var handshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.HandshakeSucceeded += (_, _) => handshake.TrySetResult();
        await using var provider = new OpenClawChatDataProvider(new GatewayClientChatBridge(client),
            ui.Dispatcher.AsPost(), Path.Combine(data.Path, "tool-meta.json"),
            Path.Combine(data.Path, "attachment-meta.json"), Path.Combine(data.Path, "chat-state.json"));
        await client.ConnectAsync();
        await handshake.Task.WaitAsync(Deadline);
        Window? window = null;
        Border? surface = null;
        MountedReactorChat? mounted = null;
        ContentDialog? dialog = null;
        try
        {
            await ui.RunOnUIAsync(async () =>
            {
                surface = new Border { RequestedTheme = theme };
                window = new Window { Content = surface };
                window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(80, 80, 900, 800));
                window.Activate();
                var session = ReactorChatHostExtensions.CreateComposerSession(surface,
                    new ChatComposerFactory(new WinUIDispatcher(ui.Dispatcher)), provider,
                    null, null, null, initialMuted: true);
                session.ViewModel.SetDraft("Retain this unsent fixture draft.");
                mounted = window.MountReactorChat(surface, provider, session, GatewayScenario.MainSessionKey);
                await WaitForRenderedConditionAsync(() => FindDescendants<Button>(surface).Any(button =>
                    AutomationProperties.GetAutomationId(button) == "ChatComposerModelPicker" && button.IsEnabled),
                    "connected production model picker");
                var picker = Find<Button>(surface, "ChatComposerModelPicker");
                picker.Flyout.ShowAt(picker);
                await WaitForRenderedConditionAsync(() => OpenElements<Button>(surface).Any(button =>
                    AutomationProperties.GetAutomationId(button) == "ChatModelSignIn"), "model sign-in entry");
                Invoke(OpenElements<Button>(surface).Single(button =>
                    AutomationProperties.GetAutomationId(button) == "ChatModelSignIn"));
                await WaitForRenderedConditionAsync(() => OpenElements<ContentDialog>(surface).Any(),
                    "native sign-in dialog");
                dialog = OpenElements<ContentDialog>(surface).Single();
                Assert.Equal(theme, dialog.ActualTheme);
                await WaitForRenderedConditionAsync(() => FindDescendants<Button>(dialog).Any(button =>
                    AutomationProperties.GetAutomationId(button) == "ModelSignInOption_" + ModelSignInFixture.Choice &&
                    button.IsEnabled), "server-provided sign-in option");
                Assert.Equal("Retain this unsent fixture draft.", session.ViewModel.Draft);
                await CaptureAsync(dialog, $"ModelSignIn-{theme}-accounts");
                Invoke(Find<Button>(dialog, "ModelSignInOption_" + ModelSignInFixture.Choice));
                await WaitForRenderedConditionAsync(() => FindDescendants<PasswordBox>(dialog).Any(),
                    "sensitive native sign-in question");
                var secret = Find<PasswordBox>(dialog, "ModelSignInSecret");
                Assert.Empty(secret.Password);
                Assert.DoesNotContain(FindDescendants<TextBox>(dialog), control =>
                    control.Text == ModelSignInFixture.Answer);
                await CaptureAsync(dialog, $"ModelSignIn-{theme}-question");
                if (stale)
                {
                    secret.Password = ModelSignInFixture.Answer;
                    await client.DisconnectAsync();
                    await WaitForRenderedConditionAsync(() =>
                        !Find<Button>(dialog, "ModelSignInCancel").IsEnabled &&
                        FindDescendants<TextBlock>(dialog).Any(text =>
                            text.Text.Contains("previous outcome may be uncertain", StringComparison.Ordinal)),
                        "retired sheet with disabled cancellation and explicit uncertain outcome");
                    Assert.Empty(secret.Password);
                    Assert.DoesNotContain(fixture.Requests, request => request.Method == "wizard.cancel");
                    await CaptureAsync(dialog, $"ModelSignIn-{theme}-context-changed");
                    dialog.Hide();
                    return;
                }
                if (cancel)
                    Invoke(Find<Button>(dialog, "ModelSignInCancel"));
                else
                {
                    secret.Password = ModelSignInFixture.Answer;
                    Invoke(Find<Button>(dialog, "ModelSignInContinue"));
                }
                await WaitForRenderedConditionAsync(() => FindDescendants<Button>(dialog).Any(button =>
                    AutomationProperties.GetAutomationId(button) == "ModelSignInRefresh" && button.IsEnabled),
                    "settled sign-in and refreshed accounts");
                Assert.Empty(secret.Password);
                Assert.Contains(FindDescendants<TextBlock>(dialog), text =>
                    text.Text.Contains(cancel ? "Sign-in ended" : "Sign-in finished", StringComparison.Ordinal));
                Assert.Equal("Retain this unsent fixture draft.", session.ViewModel.Draft);
                await CaptureAsync(dialog, $"ModelSignIn-{theme}-{(cancel ? "cancelled" : "finished")}");
                dialog.Hide();
            });
            Assert.Single(fixture.Requests, request => request.Method == "models.authLogin");
            if (!stale)
                Assert.Contains(fixture.Requests, request => request.Method == (cancel ? "wizard.cancel" : "wizard.next"));
            Assert.Equal(stale ? 1 : 2, fixture.Requests.Count(request => request.Method == "models.authStatus"));
            Assert.DoesNotContain(server.Requests, request => request.Method is "sessions.patch" or "config.patch");
            Assert.Empty(server.UnexpectedRequests);
        }
        finally
        {
            await ui.RunOnUIAsync(() =>
            {
                dialog?.Hide();
                mounted?.Dispose();
                window?.Close();
            });
            await client.DisconnectAsync();
        }
    }

    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Assert.Single(FindDescendants<T>(root), element => AutomationProperties.GetAutomationId(element) == id);

    private static IEnumerable<T> OpenElements<T>(FrameworkElement surface) where T : DependencyObject =>
        VisualTreeHelper.GetOpenPopupsForXamlRoot(surface.XamlRoot).SelectMany(popup =>
            (popup.Child is T self ? new[] { self } : []).Concat(FindDescendants<T>(popup.Child)));

    private static void Invoke(FrameworkElement element)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(element) ??
            FrameworkElementAutomationPeer.CreatePeerForElement(element);
        Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static async Task CaptureAsync(FrameworkElement element, string name)
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST") != "1") return;
        var directory = Environment.GetEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR");
        Assert.False(string.IsNullOrWhiteSpace(directory));
        await VisualTestCapture.CaptureAsync(element, name);
        Assert.True(Directory.Exists(Path.Combine(directory!, name)) &&
            Directory.GetFiles(Path.Combine(directory!, name), "*.png").Any(path => new FileInfo(path).Length > 0),
            "The current active native sign-in state must produce a nonempty capture.");
    }
}
