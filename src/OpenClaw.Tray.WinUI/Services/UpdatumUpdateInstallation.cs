using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Octokit;
using OpenClaw.Connection;
using Updatum;

namespace OpenClawTray.Services;

internal sealed class UpdatumUpdateCheckBoundary(UpdatumManager updater) : IUpdateCheckBoundary
{
    public Task<bool> CheckForUpdatesAsync() => updater.CheckForUpdatesAsync();

    public UpdateReleaseCandidate? GetSelectedRelease() =>
        updater.LatestRelease is { } release ? CreateCandidate(release) : null;

    public IEnumerable<UpdateReleaseCandidate> GetReleaseCandidates() =>
        updater.Releases.Select(CreateCandidate);

    private UpdateReleaseCandidate CreateCandidate(Release release) => new(
        release.TagName, release.Body, HasTrustedMetadata: true,
        release.Draft, release.Prerelease, release.PublishedAt is not null,
        () => updater.GetCompatibleReleaseAsset(release) is not null,
        () => updater.ForceTriggerUpdateFromRelease(release),
        () => UpdatumPreparedUpdateInstallation.PrepareAsync(updater, release));
}

internal sealed record UpdateAssetSnapshot(
    long ReleaseId, string ReleaseTag, string RuntimeId,
    long AssetId, string AssetName, string DownloadUrl, long Size, string Digest)
{
    public void ValidateDownload(UpdatumDownloadedAsset download)
    {
        if (download.Release.Id != ReleaseId || download.Release.TagName != ReleaseTag ||
            download.ReleaseAsset.Id != AssetId || download.ReleaseAsset.Name != AssetName ||
            download.ReleaseAsset.BrowserDownloadUrl != DownloadUrl || download.ReleaseAsset.Size != Size ||
            Path.GetFileName(download.FilePath) != AssetName)
        {
            throw new InvalidDataException("The downloaded update does not match the selected release asset.");
        }
    }
}

// Octokit 14 does not expose the GitHub release asset digest. Read only the
// missing metadata from the fixed repository endpoint and bind it to its DTO.
internal sealed record ReleaseAssetVerificationMetadata(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("browser_download_url")] string? DownloadUrl,
    [property: JsonPropertyName("digest")] string? Digest);

internal sealed class UpdatumPreparedUpdateInstallation(
    UpdateAssetSnapshot selected,
    Func<Task<UpdatumDownloadedAsset?>> download,
    Func<UpdatumDownloadedAsset, Task<bool>> install,
    Func<string, long, string, Task<VerifiedWindowsUpdatePackage>>? verify = null)
    : IPreparedUpdateInstallation
{
    public static async Task<IPreparedUpdateInstallation> PrepareAsync(UpdatumManager updater, Release release)
    {
        var asset = updater.GetCompatibleReleaseAsset(release)
            ?? throw new InvalidDataException("The selected release has no compatible update asset.");
        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get,
            $"https://api.github.com/repos/openclaw/openclaw-windows-node/releases/assets/{asset.Id}");
        request.Headers.Add("Accept", "application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await UpdatumManager.HttpClient.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var metadata = await response.Content.ReadFromJsonAsync<ReleaseAssetVerificationMetadata>()
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The release asset verification metadata is missing.");
        var selected = CreateSnapshot(release, asset, metadata, EntryApplication.GenericRuntimeIdentifier);
        return new UpdatumPreparedUpdateInstallation(selected,
            () => updater.DownloadUpdateAsync(release),
            downloaded => updater.InstallUpdateAsync(downloaded));
    }

    internal static UpdateAssetSnapshot CreateSnapshot(
        Release release, ReleaseAsset asset, ReleaseAssetVerificationMetadata metadata, string runtimeId)
    {
        if (string.IsNullOrEmpty(release.TagName) || runtimeId is not ("win-x64" or "win-arm64"))
            throw new InvalidDataException("The release version or Windows update architecture is unsupported.");
        var version = release.TagName[0] is 'v' or 'V' ? release.TagName[1..] : release.TagName;
        // ci.yml publishes portable updater ZIPs under this exact naming contract.
        var expectedName = $"OpenClawTray-{version}-{runtimeId}.zip";
        if (asset.Name != expectedName || metadata.Id != asset.Id || metadata.Name != asset.Name ||
            metadata.Size <= 0 || metadata.Size != asset.Size || metadata.DownloadUrl != asset.BrowserDownloadUrl)
        {
            throw new InvalidDataException("The release asset identity does not match this Windows update.");
        }
        var digest = metadata.Digest;
        WindowsUpdatePackageVerifier.ValidateReleaseDigest(digest);
        return new UpdateAssetSnapshot(release.Id, release.TagName, runtimeId,
            asset.Id, asset.Name, asset.BrowserDownloadUrl, asset.Size, digest);
    }

    public async Task<bool> DownloadAndInstallAsync(Action readyToInstall)
    {
        var downloaded = await download()
            ?? throw new InvalidDataException("The update download did not complete.");
        selected.ValidateDownload(downloaded);
        using var verified = await (verify is null
            ? WindowsUpdatePackageVerifier.VerifyAsync(downloaded.FilePath, selected.Size, selected.Digest)
            : verify(downloaded.FilePath, selected.Size, selected.Digest));

        // Verification copies are already gone. Keep only the ZIP read lease
        // through Updatum's archive consumption; its existing script owns cleanup.
        readyToInstall();
        return await install(downloaded);
    }
}
