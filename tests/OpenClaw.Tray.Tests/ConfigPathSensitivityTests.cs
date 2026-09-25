using OpenClawTray.Helpers;

namespace OpenClaw.Tray.Tests;

public class ConfigPathSensitivityTests
{
    [Theory]
    [InlineData("channels.nostr.nsec", true)]
    [InlineData("channels.nostr.NSEC", true)]
    [InlineData("nsec", true)]
    [InlineData("channels.nostr.privateKey", true)]
    [InlineData("channels.nostr.PrivateKey", true)]
    [InlineData("privateKey", true)]
    [InlineData("channels.googlechat.webhookUrl", true)]
    [InlineData("channels.slack.webhookUrls", true)]
    [InlineData("channels.discord.token", true)]
    [InlineData("channels.slack.signingSecret", true)]
    [InlineData("channels.telegram.botToken", true)]
    [InlineData("auth.password", true)]
    [InlineData("providers.apiKey", true)]
    [InlineData("providers.api_key", true)]
    [InlineData("channels.nostr.relays", false)]
    [InlineData("channels.nostr.nsecExtra", false)]
    [InlineData("channels.nostr.privateKeyExtra", false)]
    [InlineData("channels.discord.applicationId", false)]
    [InlineData("channels.googlechat.webhook", false)]
    public void IsSensitive_MasksSecretSegmentsAndLegacySecretNames(string path, bool expected)
    {
        Assert.Equal(expected, ConfigPathSensitivity.IsSensitive(path));
    }
}
