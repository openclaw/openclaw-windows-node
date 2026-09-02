using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

internal sealed record UpdateReleaseCandidate(
    string? TagName,
    string? Name,
    string? Body,
    bool Draft,
    bool Prerelease,
    bool Published,
    Func<bool> HasCompatibleAsset,
    Action Activate);

internal interface IUpdateCheckBoundary
{
    Task<bool> CheckForUpdatesAsync();

    UpdateReleaseCandidate? GetSelectedRelease();

    IEnumerable<UpdateReleaseCandidate> GetReleaseCandidates();

    bool IsReleaseHistoryComplete(string currentVersion);
}

internal sealed record UpdateCheckOutcome(
    bool UpdateFound,
    UpdateReleaseCandidate? SelectedRelease = null,
    string? ActivatedFallbackTag = null,
    bool HasSecurityCriticalRelease = false)
{
    public bool IsSecurityCritical =>
        UpdateFound &&
        (HasSecurityCriticalRelease ||
         UpdateReleasePolicy.IsSecurityCritical(SelectedRelease));
}

internal static class UpdateReleasePolicy
{
    internal const string SecurityCriticalMarker =
        "<!-- openclaw-update: security-critical -->";

    public static bool IsSecurityCritical(UpdateReleaseCandidate? release)
    {
        // An available update that cannot be inspected must never be hidden.
        if (release is null)
            return true;

        return ContainsSecuritySignal(release.Name) ||
               ContainsSecuritySignal(release.Body);
    }

    private static bool ContainsSecuritySignal(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains(SecurityCriticalMarker, StringComparison.OrdinalIgnoreCase) ||
         value.Contains("security", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("CVE-", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("GHSA-", StringComparison.OrdinalIgnoreCase));
}

internal static class CompanionUpdateSuppressionPolicy
{
    public static bool ShouldSuppress(
        GatewayUpdateStatus? gatewayStatus,
        UpdateCheckOutcome update) =>
        update.UpdateFound &&
        !update.IsSecurityCritical &&
        string.Equals(
            gatewayStatus?.EffectiveChannel,
            "extended-stable",
            StringComparison.OrdinalIgnoreCase);
}

internal static class GatewayUpdateStatusLookup
{
    public static async Task<GatewayUpdateStatus?> TryGetAsync(
        Func<Task<GatewayUpdateStatus?>> request,
        Action<Exception>? onUnavailable = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return await request();
        }
        catch (Exception ex)
        {
            onUnavailable?.Invoke(ex);
            return null;
        }
    }
}

internal static class UpdateCheckPipeline
{
    public static async Task<UpdateCheckOutcome> CheckAsync(
        IUpdateCheckBoundary boundary,
        string currentVersion)
    {
        ArgumentNullException.ThrowIfNull(boundary);

        if (await boundary.CheckForUpdatesAsync())
        {
            var selectedRelease = boundary.GetSelectedRelease();
            return new UpdateCheckOutcome(
                UpdateFound: true,
                SelectedRelease: selectedRelease,
                HasSecurityCriticalRelease:
                    !boundary.IsReleaseHistoryComplete(currentVersion) ||
                    UpdateReleasePolicy.IsSecurityCritical(selectedRelease) ||
                    HasEligibleSecurityCriticalRelease(boundary, currentVersion));
        }

        UpdateReleaseCandidate? fallbackRelease = null;
        var hasSecurityCriticalRelease =
            !boundary.IsReleaseHistoryComplete(currentVersion);
        foreach (var release in boundary.GetReleaseCandidates())
        {
            if (!IsEligibleWindowsRelease(release, currentVersion))
                continue;

            hasSecurityCriticalRelease |=
                UpdateReleasePolicy.IsSecurityCritical(release);
            fallbackRelease ??= release;
        }

        if (fallbackRelease is not null)
        {
            fallbackRelease.Activate();
            return new UpdateCheckOutcome(
                UpdateFound: true,
                SelectedRelease: fallbackRelease,
                ActivatedFallbackTag: fallbackRelease.TagName,
                HasSecurityCriticalRelease: hasSecurityCriticalRelease);
        }

        return new UpdateCheckOutcome(UpdateFound: false);
    }

    private static bool HasEligibleSecurityCriticalRelease(
        IUpdateCheckBoundary boundary,
        string currentVersion)
    {
        foreach (var release in boundary.GetReleaseCandidates())
        {
            if (IsEligibleWindowsRelease(release, currentVersion) &&
                UpdateReleasePolicy.IsSecurityCritical(release))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEligibleWindowsRelease(
        UpdateReleaseCandidate release,
        string currentVersion) =>
        !release.Draft &&
        !release.Prerelease &&
        release.Published &&
        OpenClawReleaseVersion.IsNewerStableRelease(
            release.TagName,
            currentVersion) &&
        release.HasCompatibleAsset();
}
