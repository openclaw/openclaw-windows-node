using OpenClaw.Shared;
using OpenClaw.Connection;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

internal sealed record UpdateReleaseCandidate(
    string? TagName,
    string? Body,
    bool HasTrustedMetadata,
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
}

internal sealed record UpdateCheckOutcome(
    bool UpdateFound,
    UpdateReleaseCandidate? SelectedRelease = null,
    string? ActivatedFallbackTag = null);

internal enum ReleaseSecurityClassification
{
    Unverified,
    Ordinary,
    Security,
}

internal static class UpdateReleasePolicy
{
    internal const string OrdinaryMarker =
        "<!-- openclaw-update: ordinary -->";
    internal const string SecurityMarker =
        "<!-- openclaw-update: security-critical -->";

    private static readonly System.Text.RegularExpressions.Regex MarkerShapePattern = new(
        @"<!--\s*openclaw-update\s*:.*?-->",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static ReleaseSecurityClassification Classify(
        UpdateReleaseCandidate? release)
    {
        if (release is not { HasTrustedMetadata: true } ||
            string.IsNullOrWhiteSpace(release.Body))
        {
            return ReleaseSecurityClassification.Unverified;
        }

        var ordinaryMarkerCount = CountMarkers(
            release.Body,
            OrdinaryMarker);
        var securityMarkerCount = CountMarkers(
            release.Body,
            SecurityMarker);

        // Any "openclaw-update:" marker-shaped comment that is not an exact recognized
        // marker (a typo, unknown classification, or other malformed variant) must fail
        // classification closed rather than silently disappear from the counts above. A
        // valid ordinary marker combined with such a malformed marker would otherwise be
        // misread as an unambiguous "ordinary" release.
        var totalMarkerLikeCount = MarkerShapePattern.Matches(release.Body).Count;
        if (totalMarkerLikeCount != ordinaryMarkerCount + securityMarkerCount)
        {
            return ReleaseSecurityClassification.Unverified;
        }

        return (ordinaryMarkerCount, securityMarkerCount) switch
        {
            (1, 0) => ReleaseSecurityClassification.Ordinary,
            (0, 1) => ReleaseSecurityClassification.Security,
            _ => ReleaseSecurityClassification.Unverified,
        };
    }

    private static int CountMarkers(string body, string marker)
    {
        var count = 0;
        var startIndex = 0;
        while ((startIndex = body.IndexOf(
                   marker,
                   startIndex,
                   StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            startIndex += marker.Length;
        }

        return count;
    }
}

internal static class CompanionUpdateSuppressionPolicy
{
    public static bool ShouldSuppress(
        GatewayUpdateStatus? gatewayStatus,
        UpdateCheckOutcome update) =>
        update.UpdateFound &&
        UpdateReleasePolicy.Classify(update.SelectedRelease) ==
            ReleaseSecurityClassification.Ordinary &&
        string.Equals(
            gatewayStatus?.EffectiveChannel,
            "extended-stable",
            StringComparison.OrdinalIgnoreCase);
}

internal static class GatewayUpdateStatusLookup
{
    public static bool CanQuery(
        bool hasHandshakeSnapshot,
        bool isConnected,
        IReadOnlyList<string> grantedScopes) =>
        hasHandshakeSnapshot &&
        isConnected &&
        OperatorScopeHelper.HasAdminScope(grantedScopes);

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
            return new UpdateCheckOutcome(
                UpdateFound: true,
                SelectedRelease: boundary.GetSelectedRelease());
        }

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
                SelectedRelease: release,
                ActivatedFallbackTag: release.TagName);
        }

        return new UpdateCheckOutcome(UpdateFound: false);
    }
}
