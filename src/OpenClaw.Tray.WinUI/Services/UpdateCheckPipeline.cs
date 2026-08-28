using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

internal sealed record UpdateReleaseCandidate(
    string? TagName,
    bool Draft,
    bool Prerelease,
    bool Published,
    Func<bool> HasCompatibleAsset,
    Action Activate);

internal interface IUpdateCheckBoundary
{
    Task<bool> CheckForUpdatesAsync();

    IEnumerable<UpdateReleaseCandidate> GetReleaseCandidates();
}

internal sealed record UpdateCheckOutcome(
    bool UpdateFound,
    string? ActivatedFallbackTag = null);

internal static class UpdateCheckPipeline
{
    public static async Task<UpdateCheckOutcome> CheckAsync(
        IUpdateCheckBoundary boundary,
        string currentVersion)
    {
        ArgumentNullException.ThrowIfNull(boundary);

        if (await boundary.CheckForUpdatesAsync())
            return new UpdateCheckOutcome(UpdateFound: true);

        foreach (var release in boundary.GetReleaseCandidates())
        {
            if (release.Draft ||
                release.Prerelease ||
                !release.Published ||
                !OpenClawReleaseVersion.IsNewerStableRelease(
                    release.TagName,
                    currentVersion) ||
                !release.HasCompatibleAsset())
            {
                continue;
            }

            release.Activate();
            return new UpdateCheckOutcome(
                UpdateFound: true,
                ActivatedFallbackTag: release.TagName);
        }

        return new UpdateCheckOutcome(UpdateFound: false);
    }
}
