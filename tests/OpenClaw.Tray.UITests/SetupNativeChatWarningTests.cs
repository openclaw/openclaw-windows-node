using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.SetupEngine;
using OpenClawTray.Pages;
using OpenClawTray.Presentation;
using System;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class SetupNativeChatWarningTests(UIThreadFixture fixture)
{
    [Theory]
    [InlineData((int)SetupNativeChatWarning.Unavailable, InfoBarSeverity.Warning)]
    [InlineData((int)SetupNativeChatWarning.AuthorityUnconfirmed, InfoBarSeverity.Error)]
    [InlineData((int)SetupNativeChatWarning.RenderingFailed, InfoBarSeverity.Error)]
    public async Task ActualInfoBarAppliesStateAndClearsAllObsoletePresentation(
        int warningValue, InfoBarSeverity severity)
    {
        var warning = (SetupNativeChatWarning)warningValue;
        await fixture.RunOnUIAsync(() =>
        {
            var page = new ChatPage();
            fixture.Container.Children.Add(page);
            var bar = Assert.IsType<InfoBar>(page.FindName("NativeSetupError"));
            var recheck = Assert.IsType<Button>(page.FindName("NativeSetupCheckAgain"));
            page.ApplyNativeSetupWarning(warning, "test");
            Assert.True(bar.IsOpen);
            Assert.Equal(Visibility.Visible, bar.Visibility);
            Assert.Equal(severity, bar.Severity);
            Assert.False(bar.IsClosable);
            Assert.False(string.IsNullOrWhiteSpace(bar.Message));
            Assert.DoesNotContain("ChatPage_", bar.Message);
            Assert.True(recheck.IsEnabled);
            var message = bar.Message;
            page.ApplyNativeSetupWarning(warning, "node update");
            Assert.Equal(message, bar.Message);
            page.ApplyNativeSetupWarning(SetupNativeChatWarning.None, "recovered");
            Assert.False(bar.IsOpen);
            Assert.Equal(Visibility.Collapsed, bar.Visibility);
            Assert.Equal(string.Empty, bar.Message);
            Assert.False(recheck.IsEnabled);
            fixture.Container.Children.Remove(page);
        });
    }

    [Fact]
    public async Task ActualBindingInvalidationClearsBeforeOrdinaryRenderingAndSameDestinationRetains()
    {
        await fixture.RunOnUIAsync(() =>
        {
            var page = new ChatPage();
            fixture.Container.Children.Add(page);
            // TestApp deliberately has no product services. Seed only the real
            // navigation binding, never an authenticated transport or a bypass.
            var removed = typeof(ChatPage).GetField("_sessionRemoved", BindingFlags.Instance | BindingFlags.NonPublic)!;
            removed.SetValue(page, true);
            var binding = (SetupNativeChatBinding)typeof(ChatPage)
                .GetField("_nativeSetupBinding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
            var request = new SetupNativeNavigationRequest(new(
                new(SetupCompletionIntent.CustodianOnboarding, "test", "endpoint", "provider/model", "verified", 1,
                    IdentityBinding: new string('A', 64), SessionKey: "agent:verified:main"),
                new(SetupNativeDestination.Chat, "agent:verified:main")));
            binding.Bind(request);
            page.RetainNativeSetupForDestination(request.WorkspaceDestination!);
            Assert.False((bool)removed.GetValue(page)!);
            page.ApplyNativeSetupWarning(SetupNativeChatWarning.AuthorityUnconfirmed, "test");
            var bar = Assert.IsType<InfoBar>(page.FindName("NativeSetupError"));
            page.RetainNativeSetupForDestination(request.WorkspaceDestination!);
            Assert.True(bar.IsOpen);
            page.InvalidateNativeSetupForNavigation();
            Assert.False(bar.IsOpen);
            Assert.Equal(Visibility.Collapsed, bar.Visibility);
            Assert.Equal(string.Empty, bar.Message);
            Assert.Null(binding.Request);

            // A queued button activation after teardown is harmless, without
            // accessing App, reconnecting or invoking the legacy WebView retry.
            var recheck = Assert.IsType<Button>(page.FindName("NativeSetupCheckAgain"));
            recheck.IsEnabled = true;
            var peer = new ButtonAutomationPeer(recheck);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            Assert.False(bar.IsOpen);
            fixture.Container.Children.Remove(page);
        });
    }

    [Theory]
    [InlineData("agent:verified:main", true)]
    [InlineData(null, true)]
    [InlineData("agent:verified:notification", false)]
    public async Task ExplicitSessionQueueReleasesOnlyForeignSetupBinding(string? sessionKey, bool retained)
    {
        await fixture.RunOnUIAsync(() =>
        {
            var page = new ChatPage();
            fixture.Container.Children.Add(page);
            var binding = (SetupNativeChatBinding)typeof(ChatPage)
                .GetField("_nativeSetupBinding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
            var request = new SetupNativeNavigationRequest(new(
                new(SetupCompletionIntent.CustodianOnboarding, "test", "endpoint", "provider/model", "verified", 1,
                    IdentityBinding: new string('A', 64), SessionKey: "agent:verified:main"),
                new(SetupNativeDestination.Chat, "agent:verified:main")));
            binding.Bind(request);
            page.RetainNativeSetupForDestination(request.WorkspaceDestination!);
            page.ApplyNativeSetupWarning(SetupNativeChatWarning.AuthorityUnconfirmed, "test");
            var removed = typeof(ChatPage).GetField("_sessionRemoved", BindingFlags.Instance | BindingFlags.NonPublic)!;
            removed.SetValue(page, true);
            page.QueueSession(sessionKey);
            Assert.Equal(string.IsNullOrEmpty(sessionKey), (bool)removed.GetValue(page)!);
            Assert.Equal(retained, ReferenceEquals(request, binding.Request));
            var bar = Assert.IsType<InfoBar>(page.FindName("NativeSetupError"));
            Assert.Equal(retained, bar.IsOpen);
            Assert.Equal(sessionKey, typeof(ChatPage)
                .GetField("_pendingSessionKey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page));
            fixture.Container.Children.Remove(page);
        });
    }

    [Fact]
    public async Task ComposerSelectionClearsForeignBindingAndUpdatesMountedSessionWithoutReseeding()
    {
        await fixture.RunOnUIAsync(() =>
        {
            var page = new ChatPage();
            fixture.Container.Children.Add(page);
            var binding = (SetupNativeChatBinding)typeof(ChatPage)
                .GetField("_nativeSetupBinding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
            var request = new SetupNativeNavigationRequest(new(
                new(SetupCompletionIntent.CustodianOnboarding, "test", "endpoint", "provider/model", "verified", 1,
                    IdentityBinding: new string('A', 64), SessionKey: "agent:verified:main"),
                new(SetupNativeDestination.Chat, "agent:verified:main")));
            binding.Bind(request);
            page.RetainNativeSetupForDestination(request.WorkspaceDestination!);
            page.OnComposerSessionSelected(request.Completion.Target.SessionKey);
            Assert.Same(request, binding.Request);
            page.ApplyNativeSetupWarning(SetupNativeChatWarning.Unavailable, "test");
            page.OnComposerSessionSelected("agent:verified:child");
            Assert.Null(binding.Request);
            Assert.False(Assert.IsType<InfoBar>(page.FindName("NativeSetupError")).IsOpen);
            Assert.Equal("agent:verified:child", typeof(ChatPage)
                .GetField("_mountedThreadId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page));
            Assert.Null(typeof(ChatPage)
                .GetField("_pendingSessionKey", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page));
            fixture.Container.Children.Remove(page);
        });
    }
}
