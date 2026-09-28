using OpenClawTray.Helpers;
using OpenClawTray.Chat;
namespace OpenClaw.Tray.Tests;
public class PairedChatNavigationTests
{
    [Fact] public void ReopenPreservesPageButExplicitRetryAndGatewayChangeNavigate()
    {
        var state = new PairedChatNavigation();
        Assert.True(state.ShouldNavigate("http://localhost:18789"));
        state.RecordNavigation("http://localhost:18789");
        Assert.False(state.ShouldNavigate("http://localhost:18789"));
        Assert.True(state.ShouldNavigate("http://localhost:18789", true));
        Assert.True(state.ShouldNavigate("http://localhost:18790"));
    }
    [Fact] public void NativeCredentialNeverEntersBrowserUrl()
    {
        var first = ChatSurfaceResolver.BuildChatUrl("ws://localhost:18789", "native-device-token");
        Assert.Equal("http://localhost:18789", first);
        Assert.Equal(first, ChatSurfaceResolver.BuildChatUrl("ws://localhost:18789", "rotated-token"));
    }
    [Theory]
    [InlineData("http://other.test/#bootstrapToken=fixture")]
    [InlineData("http://localhost:18789/#token=fixture")]
    [InlineData("http://localhost:18789/#bootstrapToken=fixture&gatewayUrl=ws%3A%2F%2Fother.test")]
    public void RejectsWrongOriginOrWrongCredential(string handoff) =>
        Assert.Equal("http://localhost:18789", PairedChatNavigation.ResolveHandoff("http://localhost:18789", handoff));
    [Fact] public void AcceptsMatchingBrowserBootstrap()
    {
        var handoff = "http://localhost:18789/#bootstrapToken=fixture&gatewayUrl=ws%3A%2F%2Flocalhost%3A18789";
        Assert.Equal(handoff, PairedChatNavigation.ResolveHandoff("http://localhost:18789", handoff));
    }
}
