using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class UpdateChannelPolicyTests
{
    [Fact]
    public void Resolve_DefaultsToStableLatestOnlyZipUpdates()
    {
        var settings = UpdateChannelPolicy.Resolve(_ => null);

        Assert.False(settings.AllowPreReleases);
        Assert.True(settings.FetchOnlyLatestRelease);
        Assert.Equal("zip", settings.AssetExtensionFilter);
    }

    [Theory]
    [InlineData("alpha")]
    [InlineData("Alpha")]
    [InlineData(" prerelease ")]
    [InlineData("pre-release")]
    public void Resolve_AlphaChannelAllowsPreReleasesAndFetchesReleaseList(string channel)
    {
        var settings = UpdateChannelPolicy.Resolve(key =>
            key == UpdateChannelPolicy.ChannelEnvironmentVariable ? channel : null);

        Assert.True(settings.AllowPreReleases);
        Assert.False(settings.FetchOnlyLatestRelease);
        Assert.Equal("zip", settings.AssetExtensionFilter);
    }

    [Theory]
    [InlineData("")]
    [InlineData("stable")]
    [InlineData("beta")]
    public void Resolve_NonAlphaChannelStaysOnStableLatestOnly(string? channel)
    {
        var settings = UpdateChannelPolicy.Resolve(key =>
            key == UpdateChannelPolicy.ChannelEnvironmentVariable ? channel : null);

        Assert.False(settings.AllowPreReleases);
        Assert.True(settings.FetchOnlyLatestRelease);
        Assert.Equal("zip", settings.AssetExtensionFilter);
    }
}
