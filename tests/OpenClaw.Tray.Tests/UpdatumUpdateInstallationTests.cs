using Octokit;
using OpenClaw.Connection;
using OpenClaw.TestSupport;
using OpenClawTray.Services;
using Updatum;

namespace OpenClaw.Tray.Tests;

public sealed class UpdatumUpdateInstallationTests
{
    private const string Digest = "sha256:719b88f8d8f7de6a0fb2293565ae56e30693c96763047f33f9e3cfcae6f2b600";

    [Fact]
    public void Coordinator_PreparesBeforePromptAndKeepsFailureVisible()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Services", "UpdateCoordinator.cs"));
        var prepared = source.IndexOf("installation = await release.PrepareInstallationAsync()", StringComparison.Ordinal);
        var prompted = source.IndexOf("new UpdateDialog(releaseTag, changelog)", StringComparison.Ordinal);
        Assert.True(prepared >= 0 && prompted > prepared);
        Assert.Contains("var release = checkOutcome.SelectedRelease", source);
        Assert.Contains("return await installation.DownloadAndInstallAsync", source);
        Assert.Contains("Detail = ex.Message", source);
        Assert.Contains("return !installed", source);
        Assert.DoesNotContain("await updater.InstallUpdateAsync", source);
    }

    [Theory]
    [InlineData("win-x64", "v2026.9.4")]
    [InlineData("win-arm64", "v2026.9.4")]
    [InlineData("win-x64", "v2026.9.4-2")]
    public void Snapshot_BindsTheProducerAssetAndSelectedRelease(string runtimeId, string tag)
    {
        var asset = Asset($"OpenClawTray-{tag[1..]}-{runtimeId}.zip");
        var release = Release(tag, asset);
        var metadata = Metadata(asset);

        var snapshot = UpdatumPreparedUpdateInstallation.CreateSnapshot(release, asset, metadata, runtimeId);

        Assert.Equal(release.Id, snapshot.ReleaseId);
        Assert.Equal(tag, snapshot.ReleaseTag);
        Assert.Equal(runtimeId, snapshot.RuntimeId);
        Assert.Equal(asset.Id, snapshot.AssetId);
        Assert.Equal(asset.Name, snapshot.AssetName);
        Assert.Equal(asset.BrowserDownloadUrl, snapshot.DownloadUrl);
        Assert.Equal(asset.Size, snapshot.Size);
        Assert.Equal(Digest, snapshot.Digest);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("size")]
    [InlineData("url")]
    [InlineData("digest")]
    public void MetadataMismatch_RejectsPreparation(string mismatch)
    {
        var asset = Asset();
        var metadata = Metadata(asset);
        metadata = mismatch switch
        {
            "id" => metadata with { Id = metadata.Id + 1 },
            "name" => metadata with { Name = "other.zip" },
            "size" => metadata with { Size = metadata.Size + 1 },
            "url" => metadata with { DownloadUrl = metadata.DownloadUrl + "?other" },
            "digest" => metadata with { Digest = null },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<InvalidDataException>(() =>
            UpdatumPreparedUpdateInstallation.CreateSnapshot(Release("v2026.9.4", asset), asset, metadata, "win-x64"));
    }

    [Theory]
    [InlineData("win-x86", "OpenClawTray-2026.9.4-win-x86.zip")]
    [InlineData("win-arm64", "OpenClawTray-2026.9.4-win-x64.zip")]
    [InlineData("win-x64", "OpenClawCompanion-Setup-x64.exe")]
    public void WrongArchitectureOrInstallerAsset_IsRejected(string runtimeId, string name)
    {
        var asset = Asset(name);
        Assert.Throws<InvalidDataException>(() =>
            UpdatumPreparedUpdateInstallation.CreateSnapshot(
                Release("v2026.9.4", asset), asset, Metadata(asset), runtimeId));
    }

    [Theory]
    [InlineData("release")]
    [InlineData("tag")]
    [InlineData("asset")]
    [InlineData("name")]
    [InlineData("url")]
    [InlineData("size")]
    [InlineData("path")]
    public async Task ChangedDownloadIdentity_NeverReachesVerificationOrInstallation(string mismatch)
    {
        var asset = Asset();
        var release = Release("v2026.9.4", asset);
        var snapshot = UpdatumPreparedUpdateInstallation.CreateSnapshot(release, asset, Metadata(asset), "win-x64");
        snapshot = mismatch switch
        {
            "release" => snapshot with { ReleaseId = release.Id + 1 },
            "tag" => snapshot with { ReleaseTag = "v2026.9.5" },
            "asset" => snapshot with { AssetId = asset.Id + 1 },
            "name" => snapshot with { AssetName = "other.zip" },
            "url" => snapshot with { DownloadUrl = asset.BrowserDownloadUrl + "?other" },
            "size" => snapshot with { Size = asset.Size + 1 },
            "path" => snapshot,
            _ => throw new InvalidOperationException()
        };
        var downloaded = new UpdatumDownloadedAsset(release, asset,
            mismatch == "path" ? "other.zip" : asset.Name);
        var installation = new UpdatumPreparedUpdateInstallation(snapshot,
            () => Task.FromResult<UpdatumDownloadedAsset?>(downloaded),
            _ => throw new InvalidOperationException("Installation must not run."),
            (_, _, _) => throw new InvalidOperationException("Verification must not run."));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            installation.DownloadAndInstallAsync(() => throw new InvalidOperationException("Ready must not run.")));
        Assert.Contains("selected release asset", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handoff_HoldsTheArchiveAndPropagatesActualInstallerResult(bool installed)
    {
        using var temp = new TempDirectory("openclaw-update-handoff-");
        var asset = Asset();
        var release = Release("v2026.9.4", asset);
        var path = temp.Combine(asset.Name);
        File.WriteAllText(path, "downloaded bytes");
        var downloaded = new UpdatumDownloadedAsset(release, asset, path);
        var snapshot = UpdatumPreparedUpdateInstallation.CreateSnapshot(release, asset, Metadata(asset), "win-x64");
        var order = new List<string>();
        var installation = new UpdatumPreparedUpdateInstallation(snapshot,
            () =>
            {
                order.Add("download");
                return Task.FromResult<UpdatumDownloadedAsset?>(downloaded);
            },
            async value =>
            {
                Assert.Same(downloaded, value);
                order.Add("install");
                using var reader = File.OpenRead(value.FilePath);
                Assert.Throws<IOException>(() => File.OpenWrite(path).Dispose());
                await Task.Yield();
                Assert.Throws<IOException>(() => File.Delete(path));
                return installed;
            },
            (file, size, digest) =>
            {
                order.Add("verify");
                Assert.Equal(path, file);
                Assert.Equal(asset.Size, size);
                Assert.Equal(Digest, digest);
                return Task.FromResult(new VerifiedWindowsUpdatePackage(File.OpenRead(file)));
            });

        var result = await installation.DownloadAndInstallAsync(() => order.Add("ready"));

        Assert.Equal(installed, result);
        Assert.Equal(["download", "verify", "ready", "install"], order);
        using var writable = File.OpenWrite(path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerificationOrInstallerFailure_PreservesErrorAndReleasesLease(bool failDuringInstall)
    {
        using var temp = new TempDirectory("openclaw-update-handoff-");
        var asset = Asset();
        var release = Release("v2026.9.4", asset);
        var path = temp.Combine(asset.Name);
        File.WriteAllText(path, "downloaded bytes");
        var snapshot = UpdatumPreparedUpdateInstallation.CreateSnapshot(release, asset, Metadata(asset), "win-x64");
        var expected = new InvalidDataException("specific rejection reason");
        var ready = false;
        var installation = new UpdatumPreparedUpdateInstallation(snapshot,
            () => Task.FromResult<UpdatumDownloadedAsset?>(new(release, asset, path)),
            _ => throw (failDuringInstall ? expected : new InvalidOperationException("Installation must not run.")),
            (file, _, _) => failDuringInstall
                ? Task.FromResult(new VerifiedWindowsUpdatePackage(File.OpenRead(file)))
                : throw expected);

        var actual = await Assert.ThrowsAsync<InvalidDataException>(() =>
            installation.DownloadAndInstallAsync(() => ready = true));

        Assert.Same(expected, actual);
        Assert.Equal(failDuringInstall, ready);
        using var writable = File.OpenWrite(path);
    }

    private static ReleaseAsset Asset(string name = "OpenClawTray-2026.9.4-win-x64.zip") =>
        new("", 42, "", name, "", "uploaded", "application/zip", 100, 0,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            $"https://github.com/openclaw/openclaw-windows-node/releases/download/v2026.9.4/{name}", new Author());

    private static Release Release(string tag, ReleaseAsset asset) =>
        new("", "", "", "", 21, "", tag, "main", tag, "", false, false,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, new Author(), "", "", [asset]);

    private static ReleaseAssetVerificationMetadata Metadata(ReleaseAsset asset) =>
        new(asset.Id, asset.Name, asset.Size, asset.BrowserDownloadUrl, Digest);
}
