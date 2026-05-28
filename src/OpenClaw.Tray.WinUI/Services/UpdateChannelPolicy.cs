using System;

namespace OpenClawTray.Services;

internal readonly record struct UpdateChannelSettings(
    bool AllowPreReleases,
    bool FetchOnlyLatestRelease,
    string AssetExtensionFilter);

internal static class UpdateChannelPolicy
{
    internal const string ChannelEnvironmentVariable = "OPENCLAW_UPDATE_CHANNEL";
    internal const string StableChannel = "stable";
    internal const string AlphaChannel = "alpha";
    internal const string PreReleaseChannel = "prerelease";
    internal const string ReleaseAssetExtension = "zip";

    internal static UpdateChannelSettings Resolve(Func<string, string?>? envLookup = null)
    {
        envLookup ??= Environment.GetEnvironmentVariable;
        var channel = envLookup(ChannelEnvironmentVariable);
        var allowPreReleases = IsPreReleaseChannel(channel);

        return new UpdateChannelSettings(
            AllowPreReleases: allowPreReleases,
            FetchOnlyLatestRelease: !allowPreReleases,
            AssetExtensionFilter: ReleaseAssetExtension);
    }

    internal static bool IsPreReleaseChannel(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
            return false;

        var normalized = channel.Trim();
        return string.Equals(normalized, AlphaChannel, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, PreReleaseChannel, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "pre-release", StringComparison.OrdinalIgnoreCase);
    }
}
