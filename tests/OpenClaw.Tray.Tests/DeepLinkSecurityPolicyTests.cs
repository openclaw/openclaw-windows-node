using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class DeepLinkSecurityPolicyTests
{
    [Theory]
    [InlineData("agent")]
    [InlineData("voice")]
    [InlineData("voice-start")]
    [InlineData("voice-stop")]
    [InlineData("ssh-restart")]
    [InlineData("restart-ssh")]
    [InlineData("restart-ssh-tunnel")]
    public void IsStateChangingPath_RejectsUnsafeActions(string path)
    {
        Assert.True(DeepLinkSecurityPolicy.IsStateChangingPath(path));
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("dashboard")]
    [InlineData("dashboard/sessions")]
    [InlineData("config")]
    [InlineData("support-context")]
    public void IsStateChangingPath_AllowsBenignActions(string path)
    {
        Assert.False(DeepLinkSecurityPolicy.IsStateChangingPath(path));
    }

    [Theory]
    [InlineData("openclaw://agent?message=hello%20world&token=secret", "openclaw://agent?<redacted>")]
    [InlineData("openclaw://send/private-message?message=secret", "openclaw://send/...?<redacted>")]
    [InlineData("openclaw://dashboard/sessions?filter=abc", "openclaw://dashboard/...?<redacted>")]
    [InlineData("openclaw://settings", "openclaw://settings")]
    public void RedactForLog_RemovesQueryAndPathPayloads(string uri, string expected)
    {
        Assert.Equal(expected, DeepLinkSecurityPolicy.RedactForLog(uri));
    }

    [Fact]
    public void RedactForLog_DoesNotEchoInvalidInput()
    {
        Assert.Equal("<invalid-deep-link>", DeepLinkSecurityPolicy.RedactForLog("https://example.com/?token=secret"));
    }

    [Fact]
    public void RequiresConfirmation_OnlyForStateChangingActions()
    {
        Assert.True(DeepLinkSecurityPolicy.RequiresConfirmation(
            DeepLinkParser.ParseDeepLink("openclaw://agent?message=ping")));
        Assert.False(DeepLinkSecurityPolicy.RequiresConfirmation(
            DeepLinkParser.ParseDeepLink("openclaw://dashboard/sessions")));
    }

    [Fact]
    public void BuildPipeName_IsDeterministicAndScoped()
    {
        var first = DeepLinkSecurityPolicy.BuildPipeName(@"C:\Users\alice\AppData\Local\OpenClawTray", "S-1-5-21-alice", 2);
        var second = DeepLinkSecurityPolicy.BuildPipeName(@"C:\Users\alice\AppData\Local\OpenClawTray", "S-1-5-21-alice", 2);
        var differentUser = DeepLinkSecurityPolicy.BuildPipeName(@"C:\Users\alice\AppData\Local\OpenClawTray", "S-1-5-21-bob", 2);
        var differentSession = DeepLinkSecurityPolicy.BuildPipeName(@"C:\Users\alice\AppData\Local\OpenClawTray", "S-1-5-21-alice", 3);
        var differentDataDir = DeepLinkSecurityPolicy.BuildPipeName(@"D:\isolated\OpenClawTray", "S-1-5-21-alice", 2);

        Assert.Equal(first, second);
        Assert.NotEqual(first, differentUser);
        Assert.NotEqual(first, differentSession);
        Assert.NotEqual(first, differentDataDir);
        Assert.StartsWith("OpenClawTray-DeepLink-", first);
        Assert.DoesNotContain("S-1-5-21-alice", first);
        Assert.DoesNotContain("Users", first);
    }

    [Fact]
    public void IsIpcPayloadWithinLimit_EnforcesUtf8ByteLimit()
    {
        var maxPayload = new string('a', DeepLinkSecurityPolicy.MaxIpcMessageBytes);
        var oversizedPayload = new string('a', DeepLinkSecurityPolicy.MaxIpcMessageBytes + 1);

        Assert.True(DeepLinkSecurityPolicy.IsIpcPayloadWithinLimit(maxPayload));
        Assert.False(DeepLinkSecurityPolicy.IsIpcPayloadWithinLimit(oversizedPayload));
        Assert.False(DeepLinkSecurityPolicy.IsIpcPayloadWithinLimit(""));
        Assert.False(DeepLinkSecurityPolicy.IsIpcPayloadWithinLimit(null));
    }
}
