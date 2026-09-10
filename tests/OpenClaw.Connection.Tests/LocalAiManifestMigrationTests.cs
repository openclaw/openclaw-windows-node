using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenClaw.Connection.Tests;

public sealed class LocalAiManifestMigrationTests
{
    private const string RepositoryId = "owner/repository";
    private const string RelativeModelPath = "model.gguf";
    private static readonly string Revision = new('a', 40);

    [Fact]
    public async Task Load_DoesNotTriggerCacheMigration()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);

        LocalAiResolvedInstall loaded = (await fixture.Store.LoadAsync())!;

        Assert.Equal(LocalAiInstallManifest.CurrentSchemaVersion, loaded.Manifest.SchemaVersion);
        Assert.False(File.Exists(fixture.CachedModelPath));
        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(fixture.LegacyModelPath));
    }

    [Fact]
    public async Task Load_CopiesVerifiedLegacyWeightsAndRecordsTransitionalReceipt()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);
        var progress = new List<LocalAiModelMigrationProgress>();

        LocalAiResolvedInstall migrated = (await fixture.Store.MigrateLegacyModelToHubCacheAsync(
            new InlineProgress<LocalAiModelMigrationProgress>(progress.Add)))!;

        Assert.Equal(LocalAiInstallManifest.HubCacheReceiptSchemaVersion, migrated.Manifest.SchemaVersion);
        Assert.Equal(fixture.LegacyModelPath, migrated.ModelPath);
        Assert.Equal(fixture.CacheRoot, migrated.Manifest.ModelCacheRoot);
        Assert.Equal(fixture.CachedModelPath, migrated.Manifest.CachedModelPath);
        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(fixture.CachedModelPath));
        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(fixture.LegacyModelPath));
        Assert.Contains(progress, value => value.Phase == LocalAiModelMigrationPhase.VerifyingLegacyModel);
        Assert.Contains(progress, value => value.Phase == LocalAiModelMigrationPhase.CopyingToCache);
        Assert.Contains(progress, value => value.Phase == LocalAiModelMigrationPhase.VerifyingCacheCopy);
    }

    [Fact]
    public async Task Migration_AllowsExistingReadHandleOnActiveLegacyModel()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);
        await using var activeModel = new FileStream(
            fixture.LegacyModelPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        LocalAiResolvedInstall migrated = (await fixture.Store.MigrateLegacyModelToHubCacheAsync())!;

        Assert.Equal(LocalAiInstallManifest.HubCacheReceiptSchemaVersion, migrated.Manifest.SchemaVersion);
        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(fixture.CachedModelPath));
    }

    [Fact]
    public async Task Load_IsIdempotentAndDoesNotRewriteVerifiedCacheContent()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);
        _ = await fixture.Store.MigrateLegacyModelToHubCacheAsync();
        DateTime firstWrite = File.GetLastWriteTimeUtc(fixture.CachedModelPath);
        string firstManifest = await File.ReadAllTextAsync(fixture.Paths.ManifestPath);

        _ = await fixture.Store.MigrateLegacyModelToHubCacheAsync();

        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(fixture.CachedModelPath));
        Assert.Equal(firstManifest, await File.ReadAllTextAsync(fixture.Paths.ManifestPath));
        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(fixture.LegacyModelPath));
    }

    [Fact]
    public async Task Load_RecoversWhenVerifiedDestinationExistsBeforeReceiptCommit()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.CachedModelPath)!);
        await File.WriteAllBytesAsync(fixture.CachedModelPath, fixture.Content);
        DateTime existingWrite = File.GetLastWriteTimeUtc(fixture.CachedModelPath);

        LocalAiResolvedInstall migrated = (await fixture.Store.MigrateLegacyModelToHubCacheAsync())!;

        Assert.Equal(existingWrite, File.GetLastWriteTimeUtc(fixture.CachedModelPath));
        Assert.Equal(LocalAiInstallManifest.HubCacheReceiptSchemaVersion, migrated.Manifest.SchemaVersion);
        Assert.True(File.Exists(fixture.LegacyModelPath));
    }

    [ConnectionSymbolicLinkFact]
    public async Task Load_ReusesVerifiedStandardSnapshotSymlink()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);
        var digest = new Sha256Digest(fixture.Manifest.ModelAsset.Sha256);
        Assert.True(HuggingFaceHubCache.TryGetBlobPath(
            fixture.CacheRoot,
            RepositoryId,
            digest,
            out string blobPath,
            out string error), error);
        Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.CachedModelPath)!);
        await File.WriteAllBytesAsync(blobPath, fixture.Content);
        File.CreateSymbolicLink(
            fixture.CachedModelPath,
            Path.GetRelativePath(Path.GetDirectoryName(fixture.CachedModelPath)!, blobPath));

        LocalAiResolvedInstall migrated = (await fixture.Store.MigrateLegacyModelToHubCacheAsync())!;

        Assert.Equal(fixture.CachedModelPath, migrated.Manifest.CachedModelPath);
        Assert.NotNull(new FileInfo(fixture.CachedModelPath).LinkTarget);
        Assert.True(File.Exists(fixture.LegacyModelPath));
    }

    [Fact]
    public async Task Load_RejectsMismatchedPreExistingDestinationWithoutChangingEitherCopy()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);
        byte[] mismatched = Enumerable.Repeat((byte)0x5a, fixture.Content.Length).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.CachedModelPath)!);
        await File.WriteAllBytesAsync(fixture.CachedModelPath, mismatched);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.MigrateLegacyModelToHubCacheAsync());

        Assert.Contains("unsafe or does not match", error.Message, StringComparison.Ordinal);
        Assert.Equal(mismatched, await File.ReadAllBytesAsync(fixture.CachedModelPath));
        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(fixture.LegacyModelPath));
        Assert.Equal(
            LocalAiInstallManifest.CurrentSchemaVersion,
            await ReadPersistedSchemaVersionAsync(fixture.Paths.ManifestPath));
    }

    [ConnectionSymbolicLinkFact]
    public async Task Load_RejectsSnapshotSymlinkOutsideCacheAndPreservesLegacyReceipt()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        using var outside = new TempDirectory("local-ai-cache-outside-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);
        string outsideModel = outside.Combine("model.gguf");
        await File.WriteAllBytesAsync(outsideModel, fixture.Content);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.CachedModelPath)!);
        File.CreateSymbolicLink(fixture.CachedModelPath, outsideModel);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.MigrateLegacyModelToHubCacheAsync());

        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(outsideModel));
        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(fixture.LegacyModelPath));
        Assert.Equal(
            LocalAiInstallManifest.CurrentSchemaVersion,
            await ReadPersistedSchemaVersionAsync(fixture.Paths.ManifestPath));
    }

    [Fact]
    public async Task Migration_RecoversFromInterruptedPartialCopy()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);
        string destinationDirectory = Path.GetDirectoryName(fixture.CachedModelPath)!;
        Directory.CreateDirectory(destinationDirectory);
        string partial = Path.Combine(
            destinationDirectory,
            $".openclaw-migration-{fixture.Manifest.ModelAsset.Sha256}.partial");
        await File.WriteAllBytesAsync(partial, "interrupted copy"u8.ToArray());

        LocalAiResolvedInstall migrated = (await fixture.Store.MigrateLegacyModelToHubCacheAsync())!;

        Assert.Equal(LocalAiInstallManifest.HubCacheReceiptSchemaVersion, migrated.Manifest.SchemaVersion);
        Assert.False(File.Exists(partial));
        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(fixture.CachedModelPath));
    }

    [Fact]
    public async Task Migration_ReplacesHardLinkedPartialWithoutChangingItsOtherLink()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);
        string destinationDirectory = Path.GetDirectoryName(fixture.CachedModelPath)!;
        Directory.CreateDirectory(destinationDirectory);
        string partial = Path.Combine(
            destinationDirectory,
            $".openclaw-migration-{fixture.Manifest.ModelAsset.Sha256}.partial");
        string outside = temp.Combine("outside-partial.bin");
        byte[] outsideContent = "must remain unchanged"u8.ToArray();
        await File.WriteAllBytesAsync(outside, outsideContent);
        Assert.True(TryCreateHardLink(partial, outside));

        LocalAiResolvedInstall migrated = (await fixture.Store.MigrateLegacyModelToHubCacheAsync())!;

        Assert.Equal(LocalAiInstallManifest.HubCacheReceiptSchemaVersion, migrated.Manifest.SchemaVersion);
        Assert.Equal(outsideContent, await File.ReadAllBytesAsync(outside));
        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(fixture.CachedModelPath));
    }

    [Fact]
    public async Task Load_RejectsMismatchedLegacySourceWithoutWritingCacheOrReceipt()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);
        await File.WriteAllBytesAsync(
            fixture.LegacyModelPath,
            Enumerable.Repeat((byte)0x41, fixture.Content.Length).ToArray());

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.MigrateLegacyModelToHubCacheAsync());

        Assert.False(File.Exists(fixture.CachedModelPath));
        Assert.Equal(
            LocalAiInstallManifest.CurrentSchemaVersion,
            await ReadPersistedSchemaVersionAsync(fixture.Paths.ManifestPath));
    }

    [ConnectionSymbolicLinkFact]
    public async Task Migration_RejectsLegacyModelSymlinkOutsideManagedModelRoot()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);
        string outsideModel = temp.Combine("outside-model.gguf");
        await File.WriteAllBytesAsync(outsideModel, fixture.Content);
        File.Delete(fixture.LegacyModelPath);
        File.CreateSymbolicLink(fixture.LegacyModelPath, outsideModel);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.MigrateLegacyModelToHubCacheAsync());

        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(outsideModel));
        Assert.False(File.Exists(fixture.CachedModelPath));
    }

    [Fact]
    public async Task Migration_MissingLegacySourceDoesNotCreateCacheDirectories()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);
        File.Delete(fixture.LegacyModelPath);

        LocalAiResolvedInstall unchanged = (await fixture.Store.MigrateLegacyModelToHubCacheAsync())!;

        Assert.Equal(LocalAiInstallManifest.CurrentSchemaVersion, unchanged.Manifest.SchemaVersion);
        Assert.False(Directory.Exists(fixture.CacheRoot));
    }

    [Fact]
    public async Task Migration_MissingLegacyDirectoryTreeDoesNotCreateCacheDirectories()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);
        Directory.Delete(Path.GetDirectoryName(fixture.LegacyModelPath)!, recursive: true);

        LocalAiResolvedInstall unchanged = (await fixture.Store.MigrateLegacyModelToHubCacheAsync())!;

        Assert.Equal(LocalAiInstallManifest.CurrentSchemaVersion, unchanged.Manifest.SchemaVersion);
        Assert.False(Directory.Exists(fixture.CacheRoot));
    }

    [Fact]
    public async Task ManifestDeletion_DoesNotDeleteMigratedSharedCacheContent()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);
        _ = await fixture.Store.MigrateLegacyModelToHubCacheAsync();

        await fixture.Store.DeleteAsync();

        Assert.False(File.Exists(fixture.Paths.ManifestPath));
        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(fixture.CachedModelPath));
        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(fixture.LegacyModelPath));
    }

    [Fact]
    public async Task Migration_RejectsConcurrentManifestUpdateWithoutLosingIt()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(
            temp,
            async cancellationToken =>
            {
                JsonObject current =
                    (JsonNode.Parse(await File.ReadAllTextAsync(
                        Path.Combine(temp.Path, "app-data", "LocalAI", "state.json"),
                        cancellationToken)) as JsonObject)!;
                current["endpoint"] = "http://127.0.0.1:28888/v1";
                await File.WriteAllTextAsync(
                    Path.Combine(temp.Path, "app-data", "LocalAI", "state.json"),
                    current.ToJsonString(),
                    cancellationToken);
            });

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.MigrateLegacyModelToHubCacheAsync());

        Assert.Contains("changed while", error.Message, StringComparison.Ordinal);
        JsonObject persisted =
            (JsonNode.Parse(await File.ReadAllTextAsync(fixture.Paths.ManifestPath)) as JsonObject)!;
        Assert.Equal("http://127.0.0.1:28888/v1", persisted["endpoint"]!.GetValue<string>());
        Assert.Equal(
            LocalAiInstallManifest.CurrentSchemaVersion,
            persisted["schemaVersion"]!.GetValue<int>());
        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(fixture.CachedModelPath));
    }

    [Fact]
    public async Task Migration_RejectsConcurrentManifestDeletionWithoutResurrectingIt()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        LocalAiManifestStore? concurrentStore = null;
        MigrationFixture fixture = await CreateFixtureAsync(
            temp,
            cancellationToken => concurrentStore!.DeleteAsync(cancellationToken));
        concurrentStore = new LocalAiManifestStore(fixture.Paths);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.MigrateLegacyModelToHubCacheAsync());

        Assert.Contains("changed while", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.Paths.ManifestPath));
        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(fixture.CachedModelPath));
    }

    [Fact]
    public async Task Migration_UsesVerifiedDestinationThatWinsPromotionRace()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(
            temp,
            beforeMigrationPromotion: async (destinationPath, cancellationToken) =>
            {
                await File.WriteAllBytesAsync(destinationPath, FixtureContent, cancellationToken);
            });

        LocalAiResolvedInstall migrated = (await fixture.Store.MigrateLegacyModelToHubCacheAsync())!;

        Assert.Equal(LocalAiInstallManifest.HubCacheReceiptSchemaVersion, migrated.Manifest.SchemaVersion);
        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(fixture.CachedModelPath));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(Path.GetDirectoryName(fixture.CachedModelPath)!),
            path => Path.GetFileName(path).StartsWith(".openclaw-migration-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Migration_RejectsAttemptedTemporaryChangeAndRemovesOwnedPartial()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(
            temp,
            beforeMigrationPromotion: async (destinationPath, cancellationToken) =>
            {
                string partial = Path.Combine(
                    Path.GetDirectoryName(destinationPath)!,
                    $".openclaw-migration-{Convert.ToHexString(SHA256.HashData(FixtureContent)).ToLowerInvariant()}.partial");
                await File.WriteAllBytesAsync(
                    partial,
                    Enumerable.Repeat((byte)0x7f, FixtureContent.Length).ToArray(),
                    cancellationToken);
            });

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.MigrateLegacyModelToHubCacheAsync());

        Assert.Equal(
            LocalAiInstallManifest.CurrentSchemaVersion,
            await ReadPersistedSchemaVersionAsync(fixture.Paths.ManifestPath));
        Assert.False(File.Exists(fixture.CachedModelPath));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(Path.GetDirectoryName(fixture.CachedModelPath)!),
            path => Path.GetFileName(path).StartsWith(".openclaw-migration-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Migration_RejectsDestinationDirectorySwapWithoutWritingOutsideCache()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        string? preservedDirectory = null;
        string outsideDirectory = temp.Combine("outside-cache");
        Directory.CreateDirectory(outsideDirectory);
        MigrationFixture fixture = await CreateFixtureAsync(
            temp,
            beforeMigrationPromotion: (destinationPath, _) =>
            {
                string destinationDirectory = Path.GetDirectoryName(destinationPath)!;
                preservedDirectory = destinationDirectory + "-preserved";
                Directory.Move(destinationDirectory, preservedDirectory);
                Assert.True(TryCreateJunction(destinationDirectory, outsideDirectory));
                return Task.CompletedTask;
            });

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.MigrateLegacyModelToHubCacheAsync());

        Assert.Contains("promoted safely", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(outsideDirectory));
        Assert.NotNull(preservedDirectory);
        Assert.Equal(
            LocalAiInstallManifest.CurrentSchemaVersion,
            await ReadPersistedSchemaVersionAsync(fixture.Paths.ManifestPath));
    }

    [Fact]
    public async Task Migration_HonorsCancellationBeforeMutation()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Store.MigrateLegacyModelToHubCacheAsync(
                null,
                cancellation.Token));

        Assert.False(File.Exists(fixture.CachedModelPath));
        Assert.Equal(
            LocalAiInstallManifest.CurrentSchemaVersion,
            await ReadPersistedSchemaVersionAsync(fixture.Paths.ManifestPath));
    }

    [Fact]
    public async Task Migration_CancellationDuringCopyRemovesOwnedPartialAndPreservesLegacyState()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<LocalAiModelMigrationProgress>(value =>
        {
            if (value.Phase == LocalAiModelMigrationPhase.CopyingToCache)
                cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Store.MigrateLegacyModelToHubCacheAsync(
                progress,
                cancellation.Token));

        Assert.False(File.Exists(fixture.CachedModelPath));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(Path.GetDirectoryName(fixture.CachedModelPath)!),
            path => Path.GetFileName(path).StartsWith(".openclaw-migration-", StringComparison.Ordinal));
        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(fixture.LegacyModelPath));
        Assert.Equal(
            LocalAiInstallManifest.CurrentSchemaVersion,
            await ReadPersistedSchemaVersionAsync(fixture.Paths.ManifestPath));
    }

    [Fact]
    public async Task Migration_CancellationWhileVerifyingRecoveredPartialReleasesItForRetry()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);
        string partial = Path.Combine(
            Path.GetDirectoryName(fixture.CachedModelPath)!,
            $".openclaw-migration-{fixture.Manifest.ModelAsset.Sha256}.partial");
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        await File.WriteAllBytesAsync(partial, fixture.Content);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Store.MigrateLegacyModelToHubCacheAsync(
                new InlineProgress<LocalAiModelMigrationProgress>(value =>
                {
                    if (value.Phase == LocalAiModelMigrationPhase.VerifyingCacheCopy)
                        cancellation.Cancel();
                }),
                cancellation.Token));

        LocalAiResolvedInstall migrated = (await fixture.Store.MigrateLegacyModelToHubCacheAsync())!;
        Assert.Equal(LocalAiInstallManifest.HubCacheReceiptSchemaVersion, migrated.Manifest.SchemaVersion);
        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(fixture.CachedModelPath));
    }

    [Fact]
    public async Task Load_RejectsTamperedSchemaFourCacheReceipt()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);
        _ = await fixture.Store.MigrateLegacyModelToHubCacheAsync();
        JsonObject persisted =
            (JsonNode.Parse(await File.ReadAllTextAsync(fixture.Paths.ManifestPath)) as JsonObject)!;
        persisted["cachedModelPath"] = Path.Combine(fixture.CacheRoot, "other", "model.gguf");
        await File.WriteAllTextAsync(fixture.Paths.ManifestPath, persisted.ToJsonString());

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.LoadAsync());

        Assert.Contains("cache migration receipt", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migration_AcceptsUtf8BomManifest()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);
        byte[] json = await File.ReadAllBytesAsync(fixture.Paths.ManifestPath);
        await File.WriteAllBytesAsync(
            fixture.Paths.ManifestPath,
            [.. Encoding.UTF8.Preamble, .. json]);

        LocalAiResolvedInstall migrated = (await fixture.Store.MigrateLegacyModelToHubCacheAsync())!;

        Assert.Equal(LocalAiInstallManifest.HubCacheReceiptSchemaVersion, migrated.Manifest.SchemaVersion);
    }

    [Fact]
    public async Task Save_RejectsEncodedTraversalInHuggingFaceRelativePath()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);
        LocalAiInstallManifest unsafeManifest = fixture.Manifest with
        {
            ModelAsset = fixture.Manifest.ModelAsset with
            {
                SourceUrl =
                    $"https://huggingface.co/{RepositoryId}/resolve/{Revision}/%2e%2e/model.gguf?download=true",
            },
        };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.SaveAsync(unsafeManifest));
    }

    [Fact]
    public async Task Migration_MapsFlatLegacyFileIntoNestedHuggingFaceSnapshotPath()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        MigrationFixture fixture = await CreateFixtureAsync(temp);
        LocalAiInstallManifest nestedManifest = fixture.Manifest with
        {
            ModelAsset = fixture.Manifest.ModelAsset with
            {
                SourceUrl =
                    $"https://huggingface.co/{RepositoryId}/resolve/{Revision}/weights/model.gguf?download=true",
            },
        };
        await fixture.Store.SaveAsync(nestedManifest);
        Assert.True(HuggingFaceHubCache.TryGetSnapshotPaths(
            fixture.CacheRoot,
            RepositoryId,
            Revision,
            "weights/model.gguf",
            out string nestedCachedModelPath,
            out _,
            out string error), error);

        LocalAiResolvedInstall migrated = (await fixture.Store.MigrateLegacyModelToHubCacheAsync())!;

        Assert.Equal(nestedCachedModelPath, migrated.Manifest.CachedModelPath);
        Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(nestedCachedModelPath));
    }

    [Fact]
    public async Task Migration_RejectsReparsePointCacheRootWithoutWritingItsTarget()
    {
        using var temp = new TempDirectory("local-ai-cache-migration-");
        string outsideCache = temp.Combine("outside-cache-root");
        string cacheRoot = temp.Combine("hf-cache-link");
        Directory.CreateDirectory(outsideCache);
        Assert.True(TryCreateJunction(cacheRoot, outsideCache));
        MigrationFixture fixture = await CreateFixtureAsync(
            temp,
            cacheRootOverride: cacheRoot);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => fixture.Store.MigrateLegacyModelToHubCacheAsync());

        Assert.Empty(Directory.EnumerateFileSystemEntries(outsideCache));
        Assert.Equal(
            LocalAiInstallManifest.CurrentSchemaVersion,
            await ReadPersistedSchemaVersionAsync(fixture.Paths.ManifestPath));
    }

    [CrossVolumeFact]
    public async Task Migration_CopiesAcrossConfiguredDistinctVolume()
    {
        string configuredRoot = Environment.GetEnvironmentVariable("OPENCLAW_TEST_HF_CACHE_ROOT")!;

        using var temp = new TempDirectory("local-ai-cache-migration-");
        string cacheRoot = Path.Combine(
            Path.GetFullPath(configuredRoot),
            $"openclaw-cross-volume-{Guid.NewGuid():N}");
        Assert.False(string.Equals(
            Path.GetPathRoot(temp.Path),
            Path.GetPathRoot(cacheRoot),
            StringComparison.OrdinalIgnoreCase));
        try
        {
            MigrationFixture fixture = await CreateFixtureAsync(temp, cacheRootOverride: cacheRoot);

            LocalAiResolvedInstall migrated = (await fixture.Store.MigrateLegacyModelToHubCacheAsync())!;

            Assert.Equal(LocalAiInstallManifest.HubCacheReceiptSchemaVersion, migrated.Manifest.SchemaVersion);
            Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(fixture.CachedModelPath));
            Assert.Equal(fixture.Content, await File.ReadAllBytesAsync(fixture.LegacyModelPath));
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    private static async Task<MigrationFixture> CreateFixtureAsync(
        TempDirectory temp,
        Func<CancellationToken, Task>? beforeMigrationCommit = null,
        Func<string, CancellationToken, Task>? beforeMigrationPromotion = null,
        string? cacheRootOverride = null)
    {
        byte[] content = FixtureContent;
        string digest = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        string cacheRoot = cacheRootOverride ?? temp.Combine("hf-cache");
        var paths = new LocalAiPaths(temp.Combine("app-data"));
        string legacyRelativePath = Path.Combine(
            "models",
            "owner",
            "repository",
            Revision,
            "model.gguf");
        string legacyModelPath = paths.ResolveContainedPath(legacyRelativePath, "modelPath");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyModelPath)!);
        await File.WriteAllBytesAsync(legacyModelPath, content);

        var manifest = new LocalAiInstallManifest
        {
            EngineVersion = "b1",
            Architecture = "x64",
            RuntimeId = "llama-server-test",
            ModelCatalogId = "test-model",
            SelectedGpuId = "GPU-TEST",
            ExecutablePath = Path.Combine("engines", "llama-server.exe"),
            RuntimeAssets = ImmutableArray.Create(new LocalAiAssetReceipt
            {
                FileName = "runtime.zip",
                SourceUrl = "https://example.invalid/runtime.zip",
                SizeBytes = 1,
                Sha256 = new string('a', 64),
            }),
            ModelPath = legacyRelativePath,
            ModelId = $"{RepositoryId}@{Revision}",
            ModelAlias = "test-model",
            ModelAsset = new LocalAiAssetReceipt
            {
                FileName = "model.gguf",
                SourceUrl = $"https://huggingface.co/{RepositoryId}/resolve/{Revision}/{RelativeModelPath}?download=true",
                SizeBytes = content.Length,
                Sha256 = digest,
            },
            ContextLength = 4096,
        };
        var store = new LocalAiManifestStore(
            paths,
            () => cacheRoot,
            beforeMigrationCommit,
            beforeMigrationPromotion);
        await store.SaveAsync(manifest);
        Assert.True(HuggingFaceHubCache.TryGetSnapshotPaths(
            cacheRoot,
            RepositoryId,
            Revision,
            RelativeModelPath,
            out string cachedModelPath,
            out _,
            out string error), error);
        return new(paths, store, manifest, cacheRoot, legacyModelPath, cachedModelPath, content);
    }

    private static bool TryCreateHardLink(string linkPath, string existingPath)
        => RunMklink($"/H \"{linkPath}\" \"{existingPath}\"");

    private static bool TryCreateJunction(string linkPath, string targetPath)
        => RunMklink($"/J \"{linkPath}\" \"{targetPath}\"");

    private static bool RunMklink(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            Arguments = $"/d /c mklink {arguments}",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        process!.WaitForExit();
        return process.ExitCode == 0;
    }

    private static async Task<int> ReadPersistedSchemaVersionAsync(string manifestPath)
    {
        JsonObject manifest = (JsonNode.Parse(await File.ReadAllTextAsync(manifestPath)) as JsonObject)!;
        return manifest["schemaVersion"]!.GetValue<int>();
    }

    private sealed record MigrationFixture(
        LocalAiPaths Paths,
        LocalAiManifestStore Store,
        LocalAiInstallManifest Manifest,
        string CacheRoot,
        string LegacyModelPath,
        string CachedModelPath,
        byte[] Content);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static readonly byte[] FixtureContent = "verified legacy model weights"u8.ToArray();
}

internal sealed class ConnectionSymbolicLinkFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> s_isAvailable = new(Probe, isThreadSafe: true);

    public ConnectionSymbolicLinkFactAttribute()
    {
        if (!s_isAvailable.Value)
            Skip = "Creating symbolic links requires Windows Developer Mode or elevation.";
    }

    private static bool Probe()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"openclaw-connection-symlink-probe-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, "target");
            File.WriteAllText(target, "probe");
            File.CreateSymbolicLink(Path.Combine(directory, "link"), target);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A failed capability probe must not fail the test host.
            }
        }
    }
}

internal sealed class CrossVolumeFactAttribute : FactAttribute
{
    public CrossVolumeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("OPENCLAW_TEST_HF_CACHE_ROOT")))
        {
            Skip = "Set OPENCLAW_TEST_HF_CACHE_ROOT to a directory on a distinct volume.";
        }
    }
}
