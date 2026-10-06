using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class LocalAiInstallRecoveryTests
{
    [Fact]
    public async Task ModelInstall_ResumesExactPartialWithValidatedRange()
    {
        using var temp = new TempDirectory();
        byte[] modelBytes = "verified-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        (string modelPath, string partialPath) = ResolveModelPaths(temp.Path, component, model);
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllBytesAsync(partialPath, modelBytes[..4]);
        RangeHeaderValue? observedRange = null;
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            observedRange = request.Headers.Range;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(modelBytes[4..]),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                4,
                modelBytes.Length - 1,
                modelBytes.Length);
            return response;
        }));

        var result = await CreateModelInstaller(client, temp.Path).InstallAsync(
            temp.Path,
            component,
            model,
            progress: null,
            CancellationToken.None);

        Assert.Equal("bytes=4-", observedRange?.ToString());
        Assert.Equal(modelPath, result.ModelPath);
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(modelPath));
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(result.LegacyModelPath!));
        Assert.False(File.Exists(partialPath));
    }

    [Fact]
    public async Task ModelInstall_ServerIgnoringRangeRestartsFromZero()
    {
        using var temp = new TempDirectory();
        byte[] modelBytes = "verified-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        (string modelPath, string partialPath) = ResolveModelPaths(temp.Path, component, model);
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllBytesAsync(partialPath, "old"u8.ToArray());
        using var client = new HttpClient(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(modelBytes),
        }));

        await CreateModelInstaller(client, temp.Path).InstallAsync(
            temp.Path,
            component,
            model,
            progress: null,
            CancellationToken.None);

        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(modelPath));
    }

    [Fact]
    public async Task ModelInstall_InvalidRangePreservesPreexistingPartial()
    {
        using var temp = new TempDirectory();
        byte[] modelBytes = "verified-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        (_, string partialPath) = ResolveModelPaths(temp.Path, component, model);
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllBytesAsync(partialPath, modelBytes[..4]);
        using var client = new HttpClient(new DelegateHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(modelBytes[4..]),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                3,
                modelBytes.Length - 2,
                modelBytes.Length);
            return response;
        }));

        await Assert.ThrowsAsync<HuggingFaceModelInstallException>(() =>
            CreateModelInstaller(client, temp.Path).InstallAsync(
                temp.Path,
                component,
                model,
                progress: null,
                CancellationToken.None));

        Assert.Equal(modelBytes[..4], await File.ReadAllBytesAsync(partialPath));
    }

    [Fact]
    public async Task ModelInstall_TransientFailurePreservesResumablePartial()
    {
        using var temp = new TempDirectory();
        byte[] modelBytes = "a-model-large-enough-to-retry"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        (_, string partialPath) = ResolveModelPaths(temp.Path, component, model);
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            long offset = request.Headers.Range?.Ranges.Single().From ?? 0;
            var content = new StreamContent(new ThrowAfterPrefixStream(modelBytes[(int)offset..], 2));
            var response = new HttpResponseMessage(
                offset == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = content,
            };
            if (offset > 0)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                    offset,
                    modelBytes.Length - 1,
                    modelBytes.Length);
            }
            return response;
        }));
        var installer = new HuggingFaceModelInstaller(
            client,
            (_, _) => Task.CompletedTask,
            () => CacheRoot(temp.Path));

        await Assert.ThrowsAsync<IOException>(() => installer.InstallAsync(
            temp.Path,
            component,
            model,
            progress: null,
            CancellationToken.None));

        Assert.True(File.Exists(partialPath));
        Assert.InRange(new FileInfo(partialPath).Length, 1, modelBytes.Length - 1);
    }

    [Fact]
    public async Task ModelInstall_VerifiedCompletePartialPromotesWithoutHttp()
    {
        using var temp = new TempDirectory();
        byte[] modelBytes = "verified-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        (string modelPath, string partialPath) = ResolveModelPaths(temp.Path, component, model);
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllBytesAsync(partialPath, modelBytes);
        using var client = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("HTTP must not be used for a verified complete partial.")));

        await CreateModelInstaller(client, temp.Path).InstallAsync(
            temp.Path,
            component,
            model,
            progress: null,
            CancellationToken.None);

        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(modelPath));
        Assert.False(File.Exists(partialPath));
    }

    [Fact]
    public async Task ModelInstall_MismatchedCompletePartialIsNotPromotedAndDownloadsFresh()
    {
        using var temp = new TempDirectory();
        byte[] modelBytes = "verified-model"u8.ToArray();
        byte[] mismatched = Enumerable.Repeat((byte)'x', modelBytes.Length).ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        (_, string partialPath) = ResolveModelPaths(temp.Path, component, model);
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllBytesAsync(partialPath, mismatched);
        int requests = 0;
        using var client = new HttpClient(new DelegateHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(modelBytes),
            };
        }));

        HuggingFaceModelInstallResult result = await CreateModelInstaller(client, temp.Path)
            .InstallAsync(
                temp.Path,
                component,
                model,
                progress: null,
                CancellationToken.None);

        Assert.Equal(1, requests);
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(result.ModelPath));
        Assert.False(File.Exists(partialPath));
    }

    [Fact]
    public async Task ModelInstall_ReusesVerifiedSnapshotWithoutHttp()
    {
        using var temp = new TempDirectory();
        byte[] modelBytes = "verified-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        (string modelPath, _) = ResolveModelPaths(temp.Path, component, model);
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        await File.WriteAllBytesAsync(modelPath, modelBytes);
        using var client = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("HTTP must not be used for a verified snapshot.")));

        HuggingFaceModelInstallResult result = await CreateModelInstaller(client, temp.Path)
            .InstallAsync(temp.Path, component, model, progress: null, CancellationToken.None);

        Assert.Equal(HuggingFaceModelInstallDisposition.ReusedVerified, result.Disposition);
        Assert.False(result.CreatedThisRun);
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(modelPath));
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(result.LegacyModelPath!));
    }

    [Fact]
    public async Task ModelInstall_ReusesVerifiedBlobByCopyingFromOpenHandle()
    {
        using var temp = new TempDirectory();
        byte[] modelBytes = "verified-blob-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        string cacheRoot = CacheRoot(temp.Path);
        HuggingFaceRevisionSource source = Assert.IsType<HuggingFaceRevisionSource>(
            model.Weights.Source);
        Assert.True(HuggingFaceHubCache.TryGetBlobPath(
            cacheRoot,
            source.RepositoryId,
            model.Weights.Sha256,
            out string blobPath,
            out string error), error);
        Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
        await File.WriteAllBytesAsync(blobPath, modelBytes);
        using var client = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("HTTP must not be used for a verified blob.")));

        HuggingFaceModelInstallResult result = await CreateModelInstaller(client, temp.Path)
            .InstallAsync(temp.Path, component, model, progress: null, CancellationToken.None);

        Assert.Equal(HuggingFaceModelInstallDisposition.ReusedVerified, result.Disposition);
        Assert.True(result.CreatedThisRun);
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(result.ModelPath));
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(blobPath));
    }

    [SetupCrossVolumeFact]
    public async Task ModelInstall_MaterializesLegacyCompatibilityCopyAcrossConfiguredVolume()
    {
        string configuredRoot = Environment.GetEnvironmentVariable("OPENCLAW_TEST_HF_CACHE_ROOT")!;
        using var temp = new TempDirectory();
        string cacheRoot = Path.Combine(
            Path.GetFullPath(configuredRoot),
            $"openclaw-model-install-{Guid.NewGuid():N}");
        Assert.False(string.Equals(
            Path.GetPathRoot(temp.Path),
            Path.GetPathRoot(cacheRoot),
            StringComparison.OrdinalIgnoreCase));
        byte[] modelBytes = "verified-cross-volume-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        using var client = new HttpClient(new DelegateHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(modelBytes),
            }));
        var installer = new HuggingFaceModelInstaller(
            client,
            (_, _) => Task.CompletedTask,
            () => cacheRoot);
        try
        {
            HuggingFaceModelInstallResult result = await installer.InstallAsync(
                temp.Path,
                component,
                model,
                progress: null,
                CancellationToken.None);

            Assert.Equal(modelBytes, await File.ReadAllBytesAsync(result.ModelPath));
            Assert.Equal(modelBytes, await File.ReadAllBytesAsync(result.LegacyModelPath!));
            Assert.NotEqual(
                Path.GetPathRoot(result.ModelPath),
                Path.GetPathRoot(result.LegacyModelPath));
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ModelInstall_RejectsMismatchedDestinationWithoutChangingIt()
    {
        using var temp = new TempDirectory();
        byte[] modelBytes = "verified-model"u8.ToArray();
        byte[] mismatched = Enumerable.Repeat((byte)'x', modelBytes.Length).ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        (string modelPath, _) = ResolveModelPaths(temp.Path, component, model);
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        await File.WriteAllBytesAsync(modelPath, mismatched);
        using var client = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("HTTP must not replace a mismatched shared artifact.")));

        HuggingFaceModelInstallException error = await Assert.ThrowsAsync<HuggingFaceModelInstallException>(
            () => CreateModelInstaller(client, temp.Path).InstallAsync(
                temp.Path,
                component,
                model,
                progress: null,
                CancellationToken.None));

        Assert.Contains("does not match", error.Message, StringComparison.Ordinal);
        Assert.Equal(mismatched, await File.ReadAllBytesAsync(modelPath));
    }

    [Fact]
    public async Task ModelInstall_ReplacesMismatchedAppOwnedCompatibilityCopy()
    {
        using var temp = new TempDirectory();
        byte[] modelBytes = "verified-model"u8.ToArray();
        byte[] mismatched = Enumerable.Repeat((byte)'x', modelBytes.Length).ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        (string modelPath, _) = ResolveModelPaths(temp.Path, component, model);
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        await File.WriteAllBytesAsync(modelPath, modelBytes);
        Assert.True(LocalAiPathPolicy.TryResolve(
            temp.Path,
            component,
            out LocalAiSetupPaths setupPaths,
            out string error), error);
        var source = Assert.IsType<HuggingFaceRevisionSource>(model.Weights.Source);
        Assert.True(LocalAiPathPolicy.TryGetModelPaths(
            setupPaths,
            source.RepositoryId,
            source.RevisionSha,
            model.Weights.RelativePath,
            out string legacyModelPath,
            out _,
            out error), error);
        Directory.CreateDirectory(Path.GetDirectoryName(legacyModelPath)!);
        await File.WriteAllBytesAsync(legacyModelPath, mismatched);
        using var client = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("HTTP must not run when the cache artifact is verified.")));

        HuggingFaceModelInstallResult result = await CreateModelInstaller(client, temp.Path)
            .InstallAsync(temp.Path, component, model, progress: null, CancellationToken.None);

        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(legacyModelPath));
        Assert.Equal(legacyModelPath, result.LegacyModelPath);
        Assert.True(result.LegacyCreatedThisRun);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelInstall_RejectsCacheRootInsideManagedInstallTree(bool useDescendant)
    {
        using var temp = new TempDirectory();
        byte[] modelBytes = "verified-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        string managedRoot = new LocalAiPaths(temp.Path).RootDirectory;
        string cacheRoot = useDescendant
            ? Path.Combine(managedRoot, "shared-hf-cache")
            : managedRoot;
        using var client = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("HTTP must not run for an unsafe cache root.")));
        var installer = new HuggingFaceModelInstaller(
            client,
            (_, _) => Task.CompletedTask,
            () => cacheRoot);

        HuggingFaceModelInstallException error =
            await Assert.ThrowsAsync<HuggingFaceModelInstallException>(() =>
                installer.InstallAsync(
                    temp.Path,
                    component,
                    model,
                    progress: null,
                    CancellationToken.None));

        Assert.Contains("must be outside", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(cacheRoot));
    }

    [Fact]
    public async Task ModelInstall_RejectsCacheRootAliasToManagedInstallTree()
    {
        using var temp = new TempDirectory();
        byte[] modelBytes = "verified-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        string managedRoot = new LocalAiPaths(temp.Path).RootDirectory;
        Directory.CreateDirectory(managedRoot);
        string cacheAlias = Path.Combine(temp.Path, "hf-cache-alias");
        CreateJunction(cacheAlias, managedRoot);
        using var client = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("HTTP must not run for an aliased cache root.")));
        var installer = new HuggingFaceModelInstaller(
            client,
            (_, _) => Task.CompletedTask,
            () => cacheAlias);
        try
        {
            HuggingFaceModelInstallException error =
                await Assert.ThrowsAsync<HuggingFaceModelInstallException>(() =>
                    installer.InstallAsync(
                        temp.Path,
                        component,
                        model,
                        progress: null,
                        CancellationToken.None));

            Assert.Contains("must be outside", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(managedRoot));
        }
        finally
        {
            if (Directory.Exists(cacheAlias))
                Directory.Delete(cacheAlias);
        }
    }

    internal sealed class SetupCrossVolumeFactAttribute : FactAttribute
    {
        public SetupCrossVolumeFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("OPENCLAW_TEST_HF_CACHE_ROOT")))
            {
                Skip = "Set OPENCLAW_TEST_HF_CACHE_ROOT to a directory on a distinct volume.";
            }
        }
    }

    [Fact]
    public async Task ModelInstall_RollbackPreservesVerifiedSharedCacheArtifact()
    {
        using var temp = new TempDirectory();
        byte[] modelBytes = "verified-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        using var client = new HttpClient(new DelegateHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(modelBytes),
            }));
        HuggingFaceModelInstaller installer = CreateModelInstaller(client, temp.Path);
        HuggingFaceModelInstallResult result = await installer.InstallAsync(
            temp.Path,
            component,
            model,
            progress: null,
            CancellationToken.None);

        installer.RemoveInstalledModel(temp.Path, result);

        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(result.ModelPath));
        Assert.False(File.Exists(result.LegacyModelPath));
    }

    [Fact]
    public async Task ModelInstall_ConcurrentWriterFailsClosedAndWinnerRemainsVerified()
    {
        using var temp = new TempDirectory();
        byte[] modelBytes = "verified-concurrent-model"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstClient = new HttpClient(new AsyncDelegateHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(
                    new BlockingReadStream(modelBytes, entered, release)),
            })));
        using var secondClient = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("The losing writer must not start another download.")));
        HuggingFaceModelInstaller firstInstaller = CreateModelInstaller(firstClient, temp.Path);
        HuggingFaceModelInstaller secondInstaller = CreateModelInstaller(secondClient, temp.Path);

        Task<HuggingFaceModelInstallResult> winner = firstInstaller.InstallAsync(
            temp.Path,
            component,
            model,
            progress: null,
            CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<IOException>(() => secondInstaller.InstallAsync(
            temp.Path,
            component,
            model,
            progress: null,
            CancellationToken.None));

        release.SetResult();
        HuggingFaceModelInstallResult result = await winner;
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(result.ModelPath));
    }

    [Fact]
    public async Task ModelInstall_PromotionRaceAcceptsValidWinnerAndPreservesPreexistingPartial()
    {
        using var temp = new TempDirectory();
        byte[] modelBytes = "verified-promotion-winner"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        (string modelPath, string partialPath) = ResolveModelPaths(temp.Path, component, model);
        byte[] prefix = modelBytes[..5];
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllBytesAsync(partialPath, prefix);
        using var client = new HttpClient(new DelegateHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(new EofCallbackStream(
                    modelBytes[prefix.Length..],
                    () => File.WriteAllBytes(modelPath, modelBytes))),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                prefix.Length,
                modelBytes.Length - 1,
                modelBytes.Length);
            return response;
        }));

        HuggingFaceModelInstallResult result = await CreateModelInstaller(client, temp.Path)
            .InstallAsync(temp.Path, component, model, progress: null, CancellationToken.None);

        Assert.Equal(HuggingFaceModelInstallDisposition.ReusedVerified, result.Disposition);
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(modelPath));
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(partialPath));
    }

    [Fact]
    public async Task ModelInstall_PromotionRaceWithoutValidWinnerPreservesPreexistingPartial()
    {
        using var temp = new TempDirectory();
        byte[] modelBytes = "verified-promotion-failure"u8.ToArray();
        byte[] mismatched = Enumerable.Repeat((byte)'x', modelBytes.Length).ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        (string modelPath, string partialPath) = ResolveModelPaths(temp.Path, component, model);
        byte[] prefix = modelBytes[..5];
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllBytesAsync(partialPath, prefix);
        using var client = new HttpClient(new DelegateHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(new EofCallbackStream(
                    modelBytes[prefix.Length..],
                    () => File.WriteAllBytes(modelPath, mismatched))),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                prefix.Length,
                modelBytes.Length - 1,
                modelBytes.Length);
            return response;
        }));

        await Assert.ThrowsAsync<HuggingFaceModelInstallException>(() =>
            CreateModelInstaller(client, temp.Path).InstallAsync(
                temp.Path,
                component,
                model,
                progress: null,
                CancellationToken.None));

        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(partialPath));
        Assert.Equal(mismatched, await File.ReadAllBytesAsync(modelPath));
    }

    [Fact]
    public async Task ModelInstall_ResumedDigestMismatchPreservesPreexistingPartial()
    {
        using var temp = new TempDirectory();
        byte[] modelBytes = "verified-resume-digest"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        (_, string partialPath) = ResolveModelPaths(temp.Path, component, model);
        byte[] prefix = modelBytes[..5];
        byte[] badRemainder = Enumerable.Repeat(
            (byte)'x',
            modelBytes.Length - prefix.Length).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllBytesAsync(partialPath, prefix);
        using var client = new HttpClient(new DelegateHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(badRemainder),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                prefix.Length,
                modelBytes.Length - 1,
                modelBytes.Length);
            return response;
        }));

        await Assert.ThrowsAsync<HuggingFaceModelInstallException>(() =>
            CreateModelInstaller(client, temp.Path).InstallAsync(
                temp.Path,
                component,
                model,
                progress: null,
                CancellationToken.None));

        byte[] preserved = await File.ReadAllBytesAsync(partialPath);
        Assert.Equal(modelBytes.Length, preserved.Length);
        Assert.Equal(prefix, preserved[..prefix.Length]);
    }

    [Fact]
    public async Task ModelInstall_OversizedResponsePreservesPreexistingPartial()
    {
        using var temp = new TempDirectory();
        byte[] modelBytes = "verified-oversized-response"u8.ToArray();
        LocalModelInfo model = CreateModel(modelBytes);
        LocalAiComponentIdentity component = TestComponent();
        (_, string partialPath) = ResolveModelPaths(temp.Path, component, model);
        byte[] prefix = modelBytes[..5];
        byte[] oversized = [.. modelBytes[prefix.Length..], (byte)'!'];
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllBytesAsync(partialPath, prefix);
        using var client = new HttpClient(new DelegateHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(new MemoryStream(oversized)),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                prefix.Length,
                modelBytes.Length - 1,
                modelBytes.Length);
            return response;
        }));

        await Assert.ThrowsAsync<HuggingFaceModelInstallException>(() =>
            CreateModelInstaller(client, temp.Path).InstallAsync(
                temp.Path,
                component,
                model,
                progress: null,
                CancellationToken.None));

        byte[] preserved = await File.ReadAllBytesAsync(partialPath);
        Assert.Equal(prefix, preserved);
    }

    [Fact]
    public async Task RuntimeInstall_ReplacesExactUnclaimedCatalogDirectory()
    {
        using var temp = new TempDirectory();
        byte[] binaryZip = CreateZip(("llama-server.exe", "server"u8.ToArray()));
        byte[] dependencyZip = CreateZip(("cudart64_13.dll", "cuda"u8.ToArray()));
        LlamaRuntimeVariant runtime = CreateRuntime(binaryZip, dependencyZip);
        LocalAiComponentIdentity component = LlamaRuntimeInstaller.Component(runtime);
        Assert.True(LocalAiPathPolicy.TryResolve(temp.Path, component, out LocalAiSetupPaths paths, out _));
        Directory.CreateDirectory(paths.InstallDirectory);
        await File.WriteAllTextAsync(Path.Combine(paths.InstallDirectory, "orphan.txt"), "orphan");
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            byte[] bytes = request.RequestUri!.AbsolutePath.EndsWith("runtime.zip", StringComparison.Ordinal)
                ? binaryZip
                : dependencyZip;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var installer = new LlamaRuntimeInstaller(
            new LocalAiArtifactInstaller(client),
            new ValidRuntimeInspector());

        LlamaRuntimeInstallResult result = await installer.InstallAsync(
            temp.Path,
            runtime,
            progress: null,
            CancellationToken.None);

        Assert.True(result.CreatedThisRun);
        Assert.False(File.Exists(Path.Combine(paths.InstallDirectory, "orphan.txt")));
        Assert.True(File.Exists(Path.Combine(paths.InstallDirectory, "llama-server.exe")));
    }

    [Fact]
    public async Task RuntimeInstall_FollowsOnlyApprovedGithubReleaseRedirects()
    {
        using var temp = new TempDirectory();
        byte[] binaryZip = CreateZip(("llama-server.exe", "server"u8.ToArray()));
        byte[] dependencyZip = CreateZip(("cudart64_13.dll", "cuda"u8.ToArray()));
        LlamaRuntimeVariant runtime = CreateRuntime(binaryZip, dependencyZip);
        var observedHosts = new List<string>();
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            observedHosts.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host == "github.com")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri(
                    $"https://release-assets.githubusercontent.com{request.RequestUri.AbsolutePath}");
                return redirect;
            }

            byte[] bytes = request.RequestUri.AbsolutePath.EndsWith("runtime.zip", StringComparison.Ordinal)
                ? binaryZip
                : dependencyZip;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var installer = new LlamaRuntimeInstaller(
            new LocalAiArtifactInstaller(client),
            new ValidRuntimeInspector());

        await installer.InstallAsync(temp.Path, runtime, progress: null, CancellationToken.None);

        Assert.Equal(
            ["github.com", "release-assets.githubusercontent.com", "github.com", "release-assets.githubusercontent.com"],
            observedHosts);
    }

    [Fact]
    public async Task RuntimeInstall_RejectsRedirectToUntrustedHostBeforeRequest()
    {
        using var temp = new TempDirectory();
        byte[] binaryZip = CreateZip(("llama-server.exe", "server"u8.ToArray()));
        byte[] dependencyZip = CreateZip(("cudart64_13.dll", "cuda"u8.ToArray()));
        LlamaRuntimeVariant runtime = CreateRuntime(binaryZip, dependencyZip);
        var requestCount = 0;
        using var client = new HttpClient(new DelegateHandler(_ =>
        {
            requestCount++;
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("https://example.invalid/runtime.zip");
            return redirect;
        }));
        var installer = new LlamaRuntimeInstaller(
            new LocalAiArtifactInstaller(client),
            new ValidRuntimeInspector());

        LocalAiArtifactInstallException exception = await Assert.ThrowsAsync<LocalAiArtifactInstallException>(
            () => installer.InstallAsync(temp.Path, runtime, progress: null, CancellationToken.None));

        Assert.Contains("untrusted host", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task ArtifactInstall_RejectsPinnedZipUnsafeEntryNameWithoutWritingOutsideRoot()
    {
        using var temp = new TempDirectory();
        byte[] archiveBytes = CreateZip(
            ("safe.txt", "safe"u8.ToArray()),
            ("../../../outside.txt", "outside"u8.ToArray()));
        var archive = new LocalAiPinnedArchive(
            "runtime.zip",
            new Uri("https://github.com/owner/repo/releases/download/v1/runtime.zip"),
            archiveBytes.Length,
            Sha256(archiveBytes));
        using var client = new HttpClient(new DelegateHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archiveBytes),
            }));
        var extractionProgress = new List<LocalAiArtifactInstallProgress>();
        var installer = new LocalAiArtifactInstaller(client);
        installer.ProgressChanged += (_, value) => extractionProgress.Add(value);
        string outsidePath = Path.Combine(temp.Path, "outside.txt");

        LocalAiArtifactInstallException exception =
            await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() =>
                installer.InstallAsync(
                    temp.Path,
                    TestComponent(),
                    [archive],
                    progress: null,
                    CancellationToken.None));

        Assert.Contains("unsafe path segment", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            extractionProgress,
            value => value.Phase == LocalAiArtifactInstallPhase.Extracting && value.Completed == 1);
        Assert.False(File.Exists(outsidePath));
        Assert.True(LocalAiPathPolicy.TryResolve(
            temp.Path,
            TestComponent(),
            out LocalAiSetupPaths paths,
            out string pathError), pathError);
        Assert.False(Directory.Exists(paths.InstallDirectory));
        Assert.Empty(Directory.EnumerateFiles(paths.RootDirectory, "safe.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.StagingDirectory));
    }

    [Theory]
    [InlineData((int)FileAttributes.ReparsePoint, "reparse point")]
    [InlineData(unchecked((int)0xA1FF0000), "symbolic link")]
    public async Task ArtifactInstall_RejectsLinkEntryAndCleansEarlierExtraction(
        int externalAttributes,
        string expectedError)
    {
        using var temp = new TempDirectory();
        byte[] archiveBytes = CreateZip(
            ("safe.txt", "safe"u8.ToArray(), 0),
            ("linked.txt", "target.txt"u8.ToArray(), externalAttributes));
        var archive = new LocalAiPinnedArchive(
            "runtime.zip",
            new Uri("https://github.com/owner/repo/releases/download/v1/runtime.zip"),
            archiveBytes.Length,
            Sha256(archiveBytes));
        using var client = new HttpClient(new DelegateHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archiveBytes),
            }));
        var extractionProgress = new List<LocalAiArtifactInstallProgress>();
        var installer = new LocalAiArtifactInstaller(client);
        installer.ProgressChanged += (_, value) => extractionProgress.Add(value);

        LocalAiArtifactInstallException exception =
            await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() =>
                installer.InstallAsync(
                    temp.Path,
                    TestComponent(),
                    [archive],
                    progress: null,
                    CancellationToken.None));

        Assert.Contains(expectedError, exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            extractionProgress,
            value => value.Phase == LocalAiArtifactInstallPhase.Extracting && value.Completed == 1);
        Assert.True(LocalAiPathPolicy.TryResolve(
            temp.Path,
            TestComponent(),
            out LocalAiSetupPaths paths,
            out string pathError), pathError);
        Assert.False(Directory.Exists(paths.InstallDirectory));
        Assert.Empty(Directory.EnumerateFiles(paths.RootDirectory, "safe.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.StagingDirectory));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    public void ArchiveDestination_RejectsTraversalWithEitherSeparator(string entryName)
    {
        using var temp = new TempDirectory();
        string stagingDirectory = Path.Combine(temp.Path, "staging");

        bool resolved = LocalAiPathPolicy.TryResolveArchiveEntryDestination(
            stagingDirectory,
            entryName,
            out string destinationPath,
            out string error);

        Assert.False(resolved);
        Assert.Empty(destinationPath);
        Assert.Contains("escapes its staging directory", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ArchiveDestination_RejectsCanonicalizedPrefixCollision()
    {
        using var temp = new TempDirectory();
        string stagingDirectory = Path.Combine(temp.Path, "staging");

        bool resolved = LocalAiPathPolicy.TryResolveArchiveEntryDestination(
            stagingDirectory,
            "../staging-sibling/outside.txt",
            out string destinationPath,
            out string error);

        Assert.False(resolved);
        Assert.Empty(destinationPath);
        Assert.Contains("escapes its staging directory", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ArchiveDestination_RejectsRootedPath()
    {
        using var temp = new TempDirectory();
        string stagingDirectory = Path.Combine(temp.Path, "staging");
        string rootedEntry = Path.Combine(
            Path.GetPathRoot(temp.Path)!,
            $"openclaw-rooted-{Guid.NewGuid():N}.txt");

        bool resolved = LocalAiPathPolicy.TryResolveArchiveEntryDestination(
            stagingDirectory,
            rootedEntry,
            out string destinationPath,
            out string error);

        Assert.False(resolved);
        Assert.Empty(destinationPath);
        Assert.Contains("escapes its staging directory", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ArchiveDestination_RejectsDescendantJunction()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        string stagingDirectory = Path.Combine(temp.Path, "staging");
        Directory.CreateDirectory(stagingDirectory);
        string junction = Path.Combine(stagingDirectory, "linked");
        CreateJunction(junction, outside.Path);
        try
        {
            bool resolved = LocalAiPathPolicy.TryResolveArchiveEntryDestination(
                stagingDirectory,
                "linked/outside.txt",
                out string destinationPath,
                out string error);

            Assert.False(resolved);
            Assert.Empty(destinationPath);
            Assert.Contains("reparse point", error, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(junction))
                Directory.Delete(junction);
        }
    }

    [Fact]
    public void ArchiveDestination_ResolvesValidNestedEntry()
    {
        using var temp = new TempDirectory();
        string stagingDirectory = Path.Combine(temp.Path, "staging");

        bool resolved = LocalAiPathPolicy.TryResolveArchiveEntryDestination(
            stagingDirectory,
            "bin/tools/llama-server.exe",
            out string destinationPath,
            out string error);

        Assert.True(resolved, error);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(stagingDirectory, "bin", "tools", "llama-server.exe")),
            destinationPath);
        Assert.Empty(error);
    }

    [Fact]
    public async Task Reconciler_ReusesOnlyMatchingManifestWithoutMutation()
    {
        using var temp = new TempDirectory();
        // Pin the hub cache to an empty directory. This asserts that a matching receipt is
        // reused untouched; with the ambient user cache it would instead depend on whether
        // that cache happens to already hold the default model, which legitimately triggers
        // the schema-3 to schema-4 migration and rewrites the receipt.
        using var environment = new EnvironmentScope("HF_HUB_CACHE", CacheRoot(temp.Path));
        LocalInferencePlan plan = CatalogPlan();
        const string gpuId = "GPU-0";
        LocalAiInstallManifest manifest = CreateManifest(temp.Path, plan, gpuId);
        var paths = new LocalAiPaths(temp.Path);
        await new LocalAiManifestStore(paths).SaveAsync(manifest);
        byte[] original = await File.ReadAllBytesAsync(paths.ManifestPath);
        var reconciler = new LocalAiInstallReconciler(
            new ValidRuntimeInspector(),
            new AcceptingModelVerifier());

        LocalAiReconcileResult result = await reconciler.ReconcileAsync(
            temp.Path,
            plan,
            gpuId,
            CancellationToken.None);

        Assert.True(result.Reused);
        Assert.False(result.RuntimeInstall!.CreatedThisRun);
        Assert.False(result.ModelInstall!.CreatedThisRun);
        Assert.Equal(original, await File.ReadAllBytesAsync(paths.ManifestPath));
    }

    [Fact]
    public async Task Reconciler_StagesVcRuntimeForExistingInstall_WithoutMutatingReceipt()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TempDirectory();
        using var app = new TempDirectory();
        using var environment = new EnvironmentScope("HF_HUB_CACHE", CacheRoot(temp.Path));
        LocalInferencePlan plan = CatalogPlan();
        const string gpuId = "GPU-0";
        LocalAiInstallManifest manifest = CreateManifest(temp.Path, plan, gpuId);
        var paths = new LocalAiPaths(temp.Path);
        await new LocalAiManifestStore(paths).SaveAsync(manifest);
        LocalAiResolvedInstall install = new LocalAiManifestStore(paths).ResolveAndValidate(manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(install.ExecutablePath)!);
        foreach (string fileName in LocalAiVcRuntimeStager.RequiredFiles)
            await File.WriteAllTextAsync(Path.Combine(app.Path, fileName), fileName);
        byte[] original = await File.ReadAllBytesAsync(paths.ManifestPath);

        var reconciler = new LocalAiInstallReconciler(
            new VcRuntimeAssertingInspector(),
            new AcceptingModelVerifier(),
            vcRuntimeStager: new LocalAiVcRuntimeStager(app.Path));

        LocalAiReconcileResult result = await reconciler.ReconcileAsync(
            temp.Path,
            plan,
            gpuId,
            CancellationToken.None);

        Assert.True(result.Reused);
        Assert.Equal(original, await File.ReadAllBytesAsync(paths.ManifestPath));
        Assert.All(
            LocalAiVcRuntimeStager.RequiredFiles,
            fileName => Assert.Equal(
                fileName,
                File.ReadAllText(Path.Combine(Path.GetDirectoryName(install.ExecutablePath)!, fileName))));
    }

    [Fact]
    public async Task ReadOnlyInspection_DoesNotStageVcRuntime()
    {
        using var temp = new TempDirectory();
        using var app = new TempDirectory();
        LocalInferencePlan plan = CatalogPlan();
        LocalAiInstallManifest manifest = CreateManifest(temp.Path, plan, "GPU-0");
        var paths = new LocalAiPaths(temp.Path);
        await new LocalAiManifestStore(paths).SaveAsync(manifest);
        LocalAiResolvedInstall install = new LocalAiManifestStore(paths).ResolveAndValidate(manifest);
        string runtimeDirectory = Path.GetDirectoryName(install.ExecutablePath)!;
        Directory.CreateDirectory(runtimeDirectory);
        foreach (string fileName in LocalAiVcRuntimeStager.RequiredFiles)
            await File.WriteAllTextAsync(Path.Combine(app.Path, fileName), fileName);
        var reconciler = new LocalAiInstallReconciler(
            new VcRuntimeAbsentInspector(),
            new AcceptingModelVerifier(),
            vcRuntimeStager: new LocalAiVcRuntimeStager(app.Path));

        Assert.False(await reconciler.InspectAsync(install, CancellationToken.None));
        Assert.DoesNotContain(
            LocalAiVcRuntimeStager.RequiredFiles,
            fileName => File.Exists(Path.Combine(runtimeDirectory, fileName)));
    }

    [Fact]
    public async Task Reconciler_ExplicitlyMigratesAndAdoptsVerifiedSchemaFourReceipt()
    {
        using var temp = new TempDirectory();
        using var environment = new EnvironmentScope(
            "HF_HUB_CACHE",
            CacheRoot(temp.Path));
        byte[] modelBytes = "verified-reconcile-model"u8.ToArray();
        byte[] runtimeZip = CreateZip(("llama-server.exe", "server"u8.ToArray()));
        byte[] dependencyZip = CreateZip(("dependency.dll", "dependency"u8.ToArray()));
        LocalModelInfo model = CreateModel(modelBytes);
        LlamaRuntimeVariant runtime = CreateRuntime(runtimeZip, dependencyZip);
        var profile = new LocalInferenceRunProfile(
            "test-profile",
            128,
            KvCachePrecision.F16,
            KvCachePrecision.F16,
            KvCachePrecision.F16,
            KvCachePrecision.F16,
            runtimeWorkspaceBytes: 1);
        var plan = new LocalInferencePlan(
            runtime,
            model,
            profile,
            LocalInferenceModelSelectionOrigin.Default);
        var paths = new LocalAiPaths(temp.Path);
        LocalAiInstallManifest manifest = CreateManifest(temp.Path, plan, "GPU-0");
        string legacyModelPath = paths.ResolveContainedPath(manifest.ModelPath, "modelPath");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyModelPath)!);
        await File.WriteAllBytesAsync(legacyModelPath, modelBytes);
        await new LocalAiManifestStore(paths).SaveAsync(manifest);

        LocalAiReconcileResult result = await new LocalAiInstallReconciler(
                new ValidRuntimeInspector(),
                new LocalAiModelFileVerifier())
            .ReconcileAsync(temp.Path, plan, "GPU-0", CancellationToken.None);

        LocalAiResolvedInstall resolved = Assert.IsType<LocalAiResolvedInstall>(
            result.ResolvedInstall);
        Assert.Equal(LocalAiInstallManifest.HubCacheReceiptSchemaVersion, resolved.Manifest.SchemaVersion);
        Assert.Equal(resolved.Manifest.CachedModelPath, resolved.ModelPath);
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(resolved.ModelPath));
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(legacyModelPath));
    }

    [Fact]
    public async Task Reconciler_RecoveryKeepsSchemaThreeReceiptAsRollbackBaselineAfterMigration()
    {
        using var temp = new TempDirectory();
        using var environment = new EnvironmentScope(
            "HF_HUB_CACHE",
            CacheRoot(temp.Path));
        byte[] modelBytes = "verified-recovery-model"u8.ToArray();
        byte[] runtimeZip = CreateZip(("llama-server.exe", "server"u8.ToArray()));
        byte[] dependencyZip = CreateZip(("dependency.dll", "dependency"u8.ToArray()));
        LocalModelInfo model = CreateModel(modelBytes);
        LlamaRuntimeVariant runtime = CreateRuntime(runtimeZip, dependencyZip);
        var plan = new LocalInferencePlan(
            runtime,
            model,
            new LocalInferenceRunProfile(
                "test-profile",
                128,
                KvCachePrecision.F16,
                KvCachePrecision.F16,
                KvCachePrecision.F16,
                KvCachePrecision.F16,
                runtimeWorkspaceBytes: 1),
            LocalInferenceModelSelectionOrigin.Default);
        var paths = new LocalAiPaths(temp.Path);
        LocalAiInstallManifest manifest = CreateManifest(temp.Path, plan, "GPU-0");
        string legacyModelPath = paths.ResolveContainedPath(manifest.ModelPath, "modelPath");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyModelPath)!);
        await File.WriteAllBytesAsync(legacyModelPath, modelBytes);
        await new LocalAiManifestStore(paths).SaveAsync(manifest);

        LocalAiReconcileResult result = await new LocalAiInstallReconciler(
                new ValidRuntimeInspector(),
                new LocalAiModelFileVerifier())
            .ReconcileAsync(
                temp.Path,
                plan,
                "GPU-0",
                CancellationToken.None,
                allowIncompleteInstallation: true);

        Assert.True(result.Reused);
        Assert.Equal(
            LocalAiInstallManifest.HubCacheReceiptSchemaVersion,
            result.ResolvedInstall?.Manifest.SchemaVersion);
        Assert.Equal(
            LocalAiInstallManifest.CurrentSchemaVersion,
            result.OriginalInstall?.Manifest.SchemaVersion);
        Assert.Equal(manifest.Endpoint, result.OriginalInstall?.Manifest.Endpoint);
    }

    [Fact]
    public async Task Reconciler_RecoveryRepairsMissingSchemaFourCompatibilityCopy()
    {
        using var temp = new TempDirectory();
        byte[] modelBytes = "verified-schema-four-recovery"u8.ToArray();
        byte[] runtimeZip = CreateZip(("llama-server.exe", "server"u8.ToArray()));
        byte[] dependencyZip = CreateZip(("dependency.dll", "dependency"u8.ToArray()));
        LocalModelInfo model = CreateModel(modelBytes);
        LlamaRuntimeVariant runtime = CreateRuntime(runtimeZip, dependencyZip);
        var plan = new LocalInferencePlan(
            runtime,
            model,
            new LocalInferenceRunProfile(
                "test-profile",
                128,
                KvCachePrecision.F16,
                KvCachePrecision.F16,
                KvCachePrecision.F16,
                KvCachePrecision.F16,
                runtimeWorkspaceBytes: 1),
            LocalInferenceModelSelectionOrigin.Default);
        var paths = new LocalAiPaths(temp.Path);
        LocalAiInstallManifest legacyManifest = CreateManifest(temp.Path, plan, "GPU-0");
        HuggingFaceRevisionSource source =
            Assert.IsType<HuggingFaceRevisionSource>(plan.Model.Weights.Source);
        string cacheRoot = CacheRoot(temp.Path);
        Assert.True(HuggingFaceHubCache.TryGetSnapshotPaths(
            cacheRoot,
            source.RepositoryId,
            source.RevisionSha,
            plan.Model.Weights.RelativePath,
            out string cachedModelPath,
            out _,
            out string error), error);
        Directory.CreateDirectory(Path.GetDirectoryName(cachedModelPath)!);
        await File.WriteAllBytesAsync(cachedModelPath, modelBytes);
        LocalAiInstallManifest manifest = legacyManifest with
        {
            SchemaVersion = LocalAiInstallManifest.HubCacheReceiptSchemaVersion,
            ModelCacheRoot = cacheRoot,
            CachedModelPath = cachedModelPath,
        };
        await new LocalAiManifestStore(paths).SaveAsync(manifest);

        LocalAiReconcileResult result = await new LocalAiInstallReconciler(
                new ValidRuntimeInspector(),
                new LocalAiModelFileVerifier())
            .ReconcileAsync(
                temp.Path,
                plan,
                "GPU-0",
                CancellationToken.None,
                allowIncompleteInstallation: true);

        Assert.False(result.Reused);
        Assert.NotNull(result.OriginalInstall);
        Assert.NotNull(result.RuntimeInstall);
        Assert.Null(result.ModelInstall);
    }

    [Fact]
    public async Task Reconciler_IncompleteRuntimeReacquiresWithoutRecoveryModeOrModelDownload()
    {
        // Regression: recovery for a broken runtime (model + additional assets
        // still verified valid) must give the caller everything it needs to
        // persist a schema-5 manifest without re-downloading the already-
        // verified draft checkpoint -- AcquireLocalAiModelStep's "reuse the
        // verified model" skip only re-populates SetupContext from the
        // reconcile result, it never re-runs acquisition itself.
        using var temp = new TempDirectory();
        byte[] primaryBytes = "verified-dflash-primary"u8.ToArray();
        byte[] draftBytes = "verified-dflash-draft"u8.ToArray();
        var draftSource = new HuggingFaceRevisionSource("owner/draft-repo", new string('c', 40));
        var draftWeights = new PinnedArtifact(
            "test-model-dflash-draft",
            ArtifactRole.ModelWeights,
            draftSource,
            "draft.gguf",
            draftBytes.Length,
            new Sha256Digest(Sha256(draftBytes)));
        LocalModelInfo model = CreateModelWithDraft(primaryBytes, draftWeights);
        LlamaRuntimeVariant runtime = CreateRuntime(
            CreateZip(("llama-server.exe", "server"u8.ToArray())),
            CreateZip(("dependency.dll", "dependency"u8.ToArray())));
        var plan = new LocalInferencePlan(
            runtime,
            model,
            new LocalInferenceRunProfile(
                "test-profile",
                128,
                KvCachePrecision.F16,
                KvCachePrecision.F16,
                KvCachePrecision.F16,
                KvCachePrecision.F16,
                runtimeWorkspaceBytes: 1),
            LocalInferenceModelSelectionOrigin.Default);
        var paths = new LocalAiPaths(temp.Path);
        string cacheRoot = CacheRoot(temp.Path);
        var primarySource = Assert.IsType<HuggingFaceRevisionSource>(model.Weights.Source);
        Assert.True(HuggingFaceHubCache.TryGetSnapshotPaths(
            cacheRoot,
            primarySource.RepositoryId,
            primarySource.RevisionSha,
            model.Weights.RelativePath,
            out string cachedModelPath,
            out _,
            out string error), error);
        Directory.CreateDirectory(Path.GetDirectoryName(cachedModelPath)!);
        await File.WriteAllBytesAsync(cachedModelPath, primaryBytes);
        Assert.True(HuggingFaceHubCache.TryGetSnapshotPaths(
            cacheRoot,
            draftSource.RepositoryId,
            draftSource.RevisionSha,
            draftWeights.RelativePath,
            out string cachedDraftPath,
            out _,
            out error), error);
        Directory.CreateDirectory(Path.GetDirectoryName(cachedDraftPath)!);
        await File.WriteAllBytesAsync(cachedDraftPath, draftBytes);

        LocalAiInstallManifest manifest = CreateManifest(temp.Path, plan, "GPU-0") with
        {
            SchemaVersion = LocalAiInstallManifest.AdditionalAssetsSchemaVersion,
            ModelCacheRoot = cacheRoot,
            CachedModelPath = cachedModelPath,
            AdditionalModelAssets = ImmutableArray.Create(new LocalAiAssetReceipt
            {
                FileName = "draft.gguf",
                SourceUrl = draftWeights.DownloadUri.AbsoluteUri,
                SizeBytes = draftWeights.SizeBytes,
                Sha256 = draftWeights.Sha256.Value,
            }),
            AdditionalModelPaths = ImmutableArray.Create(cachedDraftPath),
        };
        await new LocalAiManifestStore(paths, () => cacheRoot).SaveAsync(manifest);

        LocalAiReconcileResult result = await new LocalAiInstallReconciler(
                new InvalidRuntimeInspector(),
                new AcceptingModelVerifier(),
                () => cacheRoot)
            .ReconcileAsync(
                temp.Path,
                plan,
                "GPU-0",
                CancellationToken.None);

        Assert.False(result.Reused);
        Assert.Null(result.RuntimeInstall);
        Assert.NotNull(result.ModelInstall);
        ImmutableArray<HuggingFaceAdditionalAssetInstallResult> additionalInstalls =
            result.AdditionalModelInstalls ?? ImmutableArray<HuggingFaceAdditionalAssetInstallResult>.Empty;
        HuggingFaceAdditionalAssetInstallResult draftInstall = Assert.Single(additionalInstalls);
        Assert.Equal(cachedDraftPath, draftInstall.ModelPath);
        Assert.False(draftInstall.CreatedThisRun);
    }

    [Fact]
    public async Task Reconciler_UpgradesRetiredRuntimeReceiptInsteadOfFailingSetup()
    {
        // An install recorded before the runtime bump must upgrade, not end setup with an
        // uninstall instruction. The runtime is dropped so the acquirer installs the new
        // pin; the verified model is kept so an upgrade does not re-download it.
        using var temp = new TempDirectory();
        using var environment = new EnvironmentScope("HF_HUB_CACHE", CacheRoot(temp.Path));
        LocalInferencePlan plan = CatalogPlan();
        const string gpuId = "GPU-0";
        var paths = new LocalAiPaths(temp.Path);
        LlamaRuntimeVariant retired = LlamaRuntimeCatalog.FindInstalled("b10655-cuda13-x64")!;
        LocalAiInstallManifest manifest = CreateRetiredManifest(temp.Path, plan, gpuId);
        await new LocalAiManifestStore(paths).SaveAsync(manifest);
        var reconciler = new LocalAiInstallReconciler(
            new ValidRuntimeInspector(),
            new AcceptingModelVerifier());

        LocalAiReconcileResult result = await reconciler.ReconcileAsync(
            temp.Path,
            plan,
            gpuId,
            CancellationToken.None);

        Assert.False(result.Reused);
        Assert.Null(result.RuntimeInstall);
        Assert.NotNull(result.ModelInstall);
        Assert.NotNull(result.OriginalInstall);
        Assert.Equal(retired.ReleaseTag, result.OriginalInstall!.Manifest.EngineVersion);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "after-reconcile")]
    [InlineData(true, "after-reconcile")]
    [InlineData(false, "after-persist")]
    [InlineData(true, "after-persist")]
    public async Task RuntimeUpgrade_MigratesModelAndRestoresOriginalReceiptOnFailure(
        bool usesHubCache,
        string? failureStage)
    {
        using var temp = new TempDirectory();
        string cacheRoot = CacheRoot(temp.Path);
        byte[] modelBytes = "verified-upgrade-model"u8.ToArray();
        byte[] runtimeZip = CreateZip(("llama-server.exe", "new-server"u8.ToArray()));
        byte[] dependencyZip = CreateZip(("dependency.dll", "dependency"u8.ToArray()));
        LlamaRuntimeVariant runtime = CreateRuntime(runtimeZip, dependencyZip);
        LocalInferencePlan plan = CatalogPlan() with
        {
            Runtime = runtime,
            Model = CreateModel(modelBytes),
        };
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths, () => cacheRoot);
        LocalAiInstallManifest manifest = CreateRetiredManifest(temp.Path, plan, "GPU-0") with
        {
            GatewayFallbackModel = "openai/gpt-5",
        };
        string oldExecutable = paths.ResolveContainedPath(manifest.ExecutablePath, "executable");
        Directory.CreateDirectory(Path.GetDirectoryName(oldExecutable)!);
        await File.WriteAllTextAsync(oldExecutable, "old-server");
        string legacyModel = paths.ResolveContainedPath(manifest.ModelPath, "model");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyModel)!);
        await File.WriteAllBytesAsync(legacyModel, modelBytes);
        await store.SaveAsync(manifest);
        if (usesHubCache)
            manifest = (await store.MigrateLegacyModelToHubCacheAsync())!.Manifest;
        byte[] originalReceipt = await File.ReadAllBytesAsync(paths.ManifestPath);

        var context = CreateContext(temp.Path, confirmDestructive: false);
        context.Config.LocalAi.Enabled = true;
        context.Config.RollbackOnFailure = true;
        context.LocalAiPort = manifest.RequestedPort;
        context.LocalAiEligibility = new LocalInferenceEligibilityResult(
            LocalInferenceEligibilityStatus.Eligible,
            LocalInferenceEligibilityFailureCode.None,
            LocalInferenceSelectionFailureCode.None,
            plan,
            new GpuInfo(GpuVendor.Nvidia, "Test GPU", StableId: "GPU-0"),
            RequiredTotalMemoryBytes: 0,
            DetectedTotalMemoryBytes: 0,
            RequiredFreeMemoryBytes: 0,
            AvailableFreeMemoryBytes: 0);
        int runtimeDownloads = 0;
        using var runtimeClient = new HttpClient(new DelegateHandler(request =>
        {
            runtimeDownloads++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(
                    request.RequestUri!.AbsolutePath.EndsWith("runtime.zip", StringComparison.Ordinal)
                        ? runtimeZip
                        : dependencyZip),
            };
        }));
        using var modelClient = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("Upgrade must not download the verified primary model.")));
        var reconciler = new LocalAiInstallReconciler(
            new ValidRuntimeInspector(), new LocalAiModelFileVerifier(), () => cacheRoot);
        string? cachedModel = null;
        string? newExecutable = null;
        var pipeline = new SetupPipeline(
        [
            new ReconcileLocalAiInstallationStep(reconciler),
            new UpgradeCheckpointStep("after-reconcile", ctx =>
            {
                Assert.Null(ctx.LocalAiRecoveryOriginalInstall);
                Assert.Equal(manifest.SchemaVersion, ctx.LocalAiUpgradeOriginalInstall?.Manifest.SchemaVersion);
                Assert.Equal(oldExecutable, ctx.LocalAiUpgradeOriginalInstall?.ExecutablePath);
                Assert.Equal(cacheRoot, ctx.LocalAiModelInstall?.CacheRoot);
                cachedModel = ctx.LocalAiModelInstall!.ModelPath;
                return failureStage == "after-reconcile";
            }),
            new AcquireLocalAiRuntimeStep(new LlamaRuntimeInstaller(
                new LocalAiArtifactInstaller(runtimeClient), new ValidRuntimeInspector())),
            new AcquireLocalAiModelStep(CreateModelInstaller(modelClient, temp.Path)),
            new PersistLocalAiManifestStep(),
            new UpgradeCheckpointStep("after-persist", ctx =>
            {
                LocalAiResolvedInstall upgraded = Assert.IsType<LocalAiResolvedInstall>(ctx.LocalAiResolvedInstall);
                Assert.Equal(LlamaRuntimeCatalog.ReleaseTag, upgraded.Manifest.EngineVersion);
                Assert.Equal(LocalAiInstallManifest.HubCacheReceiptSchemaVersion, upgraded.Manifest.SchemaVersion);
                Assert.Equal(cacheRoot, upgraded.Manifest.ModelCacheRoot);
                Assert.Equal(cachedModel, upgraded.ModelPath);
                Assert.Equal(manifest.InstalledAtUtc, upgraded.Manifest.InstalledAtUtc);
                Assert.Equal(manifest.GatewayFallbackModel, upgraded.Manifest.GatewayFallbackModel);
                Assert.Null(upgraded.Endpoint);
                newExecutable = upgraded.ExecutablePath;
                Assert.True(File.Exists(newExecutable));
                return failureStage == "after-persist";
            }),
        ]);

        PipelineResult result = await pipeline.RunAsync(context);

        Assert.True(
            result.Outcome == (failureStage is null ? PipelineOutcome.Success : PipelineOutcome.Failed),
            result.Message);
        Assert.Equal(failureStage, result.FailedStepId);
        if (failureStage is not null)
            Assert.Equal("Injected upgrade failure.", result.Message);
        Assert.Equal(failureStage == "after-reconcile" ? 0 : 2, runtimeDownloads);
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(cachedModel!));
        Assert.Equal(modelBytes, await File.ReadAllBytesAsync(legacyModel));
        Assert.Equal("old-server", await File.ReadAllTextAsync(oldExecutable));
        LocalAiResolvedInstall persisted = (await store.LoadAsync())!;
        if (failureStage is null)
        {
            Assert.Equal(newExecutable, persisted.ExecutablePath);
            Assert.Equal(LlamaRuntimeCatalog.ReleaseTag, persisted.Manifest.EngineVersion);
        }
        else
        {
            Assert.Equal(originalReceipt, await File.ReadAllBytesAsync(paths.ManifestPath));
            Assert.Equal(oldExecutable, persisted.ExecutablePath);
            Assert.Null(context.LocalAiUpgradeOriginalInstall);
            if (newExecutable is not null)
                Assert.False(File.Exists(newExecutable));

            LocalAiReconcileResult retry = await reconciler.ReconcileAsync(
                temp.Path, plan, "GPU-0", CancellationToken.None);
            Assert.False(retry.Reused);
            Assert.Null(retry.RuntimeInstall);
            Assert.Equal(cacheRoot, retry.ModelInstall?.CacheRoot);
        }
    }

    [Fact]
    public async Task Reconciler_RejectsMigrationCacheRootInsideManagedInstallTree()
    {
        using var temp = new TempDirectory();
        LocalInferencePlan plan = CatalogPlan();
        const string gpuId = "GPU-0";
        var paths = new LocalAiPaths(temp.Path);
        string cacheRoot = Path.Combine(paths.RootDirectory, "shared-hf-cache");
        using var environment = new EnvironmentScope("HF_HUB_CACHE", cacheRoot);
        var store = new LocalAiManifestStore(paths);
        await store.SaveAsync(CreateManifest(temp.Path, plan, gpuId));
        byte[] original = await File.ReadAllBytesAsync(paths.ManifestPath);
        var reconciler = new LocalAiInstallReconciler(
            new ValidRuntimeInspector(),
            new AcceptingModelVerifier());

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            reconciler.ReconcileAsync(
                temp.Path,
                plan,
                gpuId,
                CancellationToken.None));

        Assert.Contains("must be outside", error.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllBytesAsync(paths.ManifestPath));
        Assert.False(Directory.Exists(cacheRoot));
    }

    [Fact]
    public async Task Reconciler_MigratesLegacyCudaPrefixedUuidSelector()
    {
        using var temp = new TempDirectory();
        LocalInferencePlan plan = CatalogPlan();
        const string gpuUuid = "GPU-cc66bca6-b5ff-dd70-995c-d81a07add980";
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        await store.SaveAsync(CreateManifest(temp.Path, plan, $"cuda:{gpuUuid}"));
        var reconciler = new LocalAiInstallReconciler(
            new ValidRuntimeInspector(),
            new AcceptingModelVerifier());

        LocalAiReconcileResult result = await reconciler.ReconcileAsync(
            temp.Path,
            plan,
            gpuUuid,
            CancellationToken.None);

        LocalAiResolvedInstall migrated = Assert.IsType<LocalAiResolvedInstall>(
            await store.LoadAsync());
        Assert.True(result.Reused);
        Assert.Equal(gpuUuid, result.ResolvedInstall?.Manifest.SelectedGpuId);
        Assert.Equal(gpuUuid, migrated.Manifest.SelectedGpuId);
        Assert.Equal(
            gpuUuid,
            LlamaServerRouterConfiguration.Build(paths, migrated)
                .Environment["CUDA_VISIBLE_DEVICES"]);
    }

    [Fact]
    public async Task Reconciler_RejectsDifferentGpuWithoutDeletingManifest()
    {
        using var temp = new TempDirectory();
        LocalInferencePlan plan = CatalogPlan();
        var paths = new LocalAiPaths(temp.Path);
        await new LocalAiManifestStore(paths).SaveAsync(CreateManifest(temp.Path, plan, "GPU-0"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LocalAiInstallReconciler(new ValidRuntimeInspector(), new AcceptingModelVerifier())
                .ReconcileAsync(temp.Path, plan, "GPU-1", CancellationToken.None));

        Assert.True(File.Exists(paths.ManifestPath));
    }

    [Fact]
    public async Task Reconciler_RecoveryRetainsReceiptWhileMissingModelIsRepaired()
    {
        using var temp = new TempDirectory();
        LocalInferencePlan plan = CatalogPlan();
        const string gpuId = "GPU-0";
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        LocalAiInstallManifest manifest = CreateManifest(temp.Path, plan, gpuId);
        await store.SaveAsync(manifest);
        var reconciler = new LocalAiInstallReconciler(
            new ValidRuntimeInspector(),
            new LocalAiModelFileVerifier());

        LocalAiReconcileResult result = await reconciler.ReconcileAsync(
            temp.Path,
            plan,
            gpuId,
            CancellationToken.None,
            allowIncompleteInstallation: true);

        Assert.False(result.Reused);
        Assert.NotNull(result.OriginalInstall);
        Assert.Equal(manifest.Endpoint, result.OriginalInstall!.Manifest.Endpoint);
        Assert.NotNull(result.RuntimeInstall);
        Assert.Null(result.ModelInstall);
        Assert.True(File.Exists(paths.ManifestPath));
    }

    [Fact]
    public async Task ReconcileStep_RecoveryPinsIncompleteReceiptAsRollbackBaseline()
    {
        using var temp = new TempDirectory();
        LocalInferencePlan plan = CatalogPlan();
        const string gpuId = "GPU-0";
        LocalAiInstallManifest manifest = CreateManifest(temp.Path, plan, gpuId);
        await new LocalAiManifestStore(new LocalAiPaths(temp.Path)).SaveAsync(manifest);
        var context = CreateContext(temp.Path, confirmDestructive: false);
        context.Config.LocalAi.Enabled = true;
        context.Config.LocalAiRecoveryGatewayId = "gateway-id";
        context.LocalAiEligibility = new LocalInferenceEligibilityResult(
            LocalInferenceEligibilityStatus.Eligible,
            LocalInferenceEligibilityFailureCode.None,
            LocalInferenceSelectionFailureCode.None,
            plan,
            new GpuInfo(GpuVendor.Nvidia, "Test GPU", StableId: gpuId),
            RequiredTotalMemoryBytes: 0,
            DetectedTotalMemoryBytes: 0,
            RequiredFreeMemoryBytes: 0,
            AvailableFreeMemoryBytes: 0);
        var step = new ReconcileLocalAiInstallationStep(new LocalAiInstallReconciler(
            new ValidRuntimeInspector(),
            new RejectingModelVerifier()));

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Skipped, result.Outcome);
        Assert.Equal(manifest.Endpoint, context.LocalAiRecoveryOriginalInstall?.Manifest.Endpoint);
        Assert.True(context.LocalAiRecoveryReceiptRollbackAllowed);
        Assert.NotNull(context.LocalAiRuntimeInstall);
        Assert.Null(context.LocalAiModelInstall);
    }

    [Fact]
    public async Task RecoveryPipeline_RewritesIncompleteReceiptAfterModelRepair()
    {
        using var temp = new TempDirectory();
        LocalInferencePlan plan = CatalogPlan();
        const string gpuId = "GPU-0";
        LocalAiInstallManifest manifest = CreateManifest(temp.Path, plan, gpuId) with
        {
            RequestedPort = 18803,
            GatewayFallbackModel = "openai/gpt-5",
        };
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(manifest);
        LocalAiResolvedInstall original = (await store.LoadAsync())!;
        var context = CreateContext(temp.Path, confirmDestructive: false);
        context.Config.LocalAi.Enabled = true;
        context.Config.LocalAiRecoveryGatewayId = "gateway-id";
        context.LocalAiPort = manifest.RequestedPort;
        context.LocalAiEligibility = new LocalInferenceEligibilityResult(
            LocalInferenceEligibilityStatus.Eligible,
            LocalInferenceEligibilityFailureCode.None,
            LocalInferenceSelectionFailureCode.None,
            plan,
            new GpuInfo(GpuVendor.Nvidia, "Test GPU", StableId: gpuId),
            RequiredTotalMemoryBytes: 0,
            DetectedTotalMemoryBytes: 0,
            RequiredFreeMemoryBytes: 0,
            AvailableFreeMemoryBytes: 0);
        var pipeline = new SetupPipeline(
        [
            new ReconcileLocalAiInstallationStep(new LocalAiInstallReconciler(
                new ValidRuntimeInspector(),
                new RejectingModelVerifier())),
            new CompleteModelRepairStep(temp.Path, plan),
            new PersistLocalAiManifestStep(),
        ]);

        PipelineResult result = await pipeline.RunAsync(context);

        Assert.Equal(PipelineOutcome.Success, result.Outcome);
        LocalAiResolvedInstall repaired = (await store.LoadAsync())!;
        Assert.Null(repaired.Endpoint);
        Assert.Equal(manifest.RequestedPort, repaired.Manifest.RequestedPort);
        Assert.Equal(manifest.GatewayFallbackModel, repaired.Manifest.GatewayFallbackModel);
        Assert.Equal(manifest.InstalledAtUtc, repaired.Manifest.InstalledAtUtc);
        Assert.Equal(LocalAiInstallManifest.HubCacheReceiptSchemaVersion, repaired.Manifest.SchemaVersion);
        Assert.Equal(CacheRoot(temp.Path), repaired.Manifest.ModelCacheRoot);
        Assert.Equal(repaired.Manifest.CachedModelPath, repaired.ModelPath);
        Assert.False(context.LocalAiManifestCreatedThisRun);
    }

    [Fact]
    public async Task RuntimeInstall_ReusesVerifiedCacheAfterLocalAiRootRemoval()
    {
        using var temp = new TempDirectory();
        byte[] binaryZip = CreateZip(("llama-server.exe", "server"u8.ToArray()));
        byte[] dependencyZip = CreateZip(("cudart64_13.dll", "cuda"u8.ToArray()));
        LlamaRuntimeVariant runtime = CreateRuntime(binaryZip, dependencyZip);
        int requests = 0;
        using var firstClient = new HttpClient(new DelegateHandler(request =>
        {
            requests++;
            byte[] bytes = request.RequestUri!.AbsolutePath.EndsWith("runtime.zip", StringComparison.Ordinal)
                ? binaryZip
                : dependencyZip;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var firstInstaller = new LlamaRuntimeInstaller(
            new LocalAiArtifactInstaller(firstClient, archiveCacheEnabled: true),
            new ValidRuntimeInspector());

        LlamaRuntimeInstallResult first = await firstInstaller.InstallAsync(
            temp.Path,
            runtime,
            progress: null,
            CancellationToken.None);

        Assert.Equal(2, requests);
        Assert.Equal(0, first.ReusedCachedArchiveCount);
        string cacheRoot = Path.Combine(temp.Path, "LocalAICache", "archives");
        string cachedBinary = Path.Combine(cacheRoot, Sha256(binaryZip), "runtime.zip");
        string cachedDependency = Path.Combine(cacheRoot, Sha256(dependencyZip), "dependency.zip");
        Assert.Equal(binaryZip, await File.ReadAllBytesAsync(cachedBinary));
        Assert.Equal(dependencyZip, await File.ReadAllBytesAsync(cachedDependency));

        Assert.True(LocalAiPathPolicy.TryDeleteManagedTree(
            temp.Path,
            new LocalAiPaths(temp.Path).RootDirectory,
            allowRoot: true,
            out string deleteError), deleteError);
        Assert.False(Directory.Exists(new LocalAiPaths(temp.Path).RootDirectory));

        using var secondClient = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("HTTP must not run for cached runtime archives.")));
        var secondInstaller = new LlamaRuntimeInstaller(
            new LocalAiArtifactInstaller(secondClient, archiveCacheEnabled: true),
            new ValidRuntimeInspector());

        LlamaRuntimeInstallResult second = await secondInstaller.InstallAsync(
            temp.Path,
            runtime,
            progress: null,
            CancellationToken.None);

        Assert.True(second.CreatedThisRun);
        Assert.Equal(2, second.ReusedCachedArchiveCount);
        Assert.Equal(2, requests);
        Assert.True(File.Exists(Path.Combine(second.InstallDirectory, "llama-server.exe")));
    }

    [Fact]
    public async Task RuntimeInstall_ReplacesCorruptCachedArchive()
    {
        using var temp = new TempDirectory();
        byte[] binaryZip = CreateZip(("llama-server.exe", "server"u8.ToArray()));
        byte[] dependencyZip = CreateZip(("cudart64_13.dll", "cuda"u8.ToArray()));
        LlamaRuntimeVariant runtime = CreateRuntime(binaryZip, dependencyZip);
        string cacheRoot = Path.Combine(temp.Path, "LocalAICache", "archives");
        string cachedBinary = Path.Combine(cacheRoot, Sha256(binaryZip), "runtime.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(cachedBinary)!);
        byte[] corrupt = new byte[binaryZip.Length];
        new Random(1234).NextBytes(corrupt);
        await File.WriteAllBytesAsync(cachedBinary, corrupt);
        int requests = 0;
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            requests++;
            byte[] bytes = request.RequestUri!.AbsolutePath.EndsWith("runtime.zip", StringComparison.Ordinal)
                ? binaryZip
                : dependencyZip;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var installer = new LlamaRuntimeInstaller(
            new LocalAiArtifactInstaller(client, archiveCacheEnabled: true),
            new ValidRuntimeInspector());

        LlamaRuntimeInstallResult result = await installer.InstallAsync(
            temp.Path,
            runtime,
            progress: null,
            CancellationToken.None);

        Assert.Equal(2, requests);
        Assert.Equal(0, result.ReusedCachedArchiveCount);
        Assert.Equal(binaryZip, await File.ReadAllBytesAsync(cachedBinary));
    }

    [Fact]
    public async Task RuntimeInstall_DoesNotWriteThroughJunctionedCacheRoot()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        byte[] binaryZip = CreateZip(("llama-server.exe", "server"u8.ToArray()));
        byte[] dependencyZip = CreateZip(("cudart64_13.dll", "cuda"u8.ToArray()));
        LlamaRuntimeVariant runtime = CreateRuntime(binaryZip, dependencyZip);
        string cacheRoot = Path.Combine(temp.Path, "LocalAICache");
        CreateJunction(cacheRoot, outside.Path);
        int requests = 0;
        try
        {
            using var client = new HttpClient(new DelegateHandler(request =>
            {
                requests++;
                byte[] bytes = request.RequestUri!.AbsolutePath.EndsWith("runtime.zip", StringComparison.Ordinal)
                    ? binaryZip
                    : dependencyZip;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }));
            var installer = new LlamaRuntimeInstaller(
                new LocalAiArtifactInstaller(client, archiveCacheEnabled: true),
                new ValidRuntimeInspector());

            LlamaRuntimeInstallResult result = await installer.InstallAsync(
                temp.Path,
                runtime,
                progress: null,
                CancellationToken.None);

            Assert.True(result.CreatedThisRun);
            Assert.Equal(0, result.ReusedCachedArchiveCount);
            Assert.True(File.Exists(Path.Combine(result.InstallDirectory, "llama-server.exe")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(LocalAiArtifactInstaller.DefaultRetainedStaleArchiveSets)]
    public async Task RuntimeInstall_PrunesCacheToNewestCompleteSets(int retainedSets)
    {
        using var temp = new TempDirectory();
        string unrelated = Directory.CreateDirectory(Path.Combine(ArchivesRoot(temp.Path), "not-a-hash")).FullName;
        CacheTestSet[] history = Enumerable.Range(1, 5).Select(index => CreateCacheTestSet($"old-{index}")).ToArray();
        CacheTestSet current = CreateCacheTestSet("current");

        foreach (CacheTestSet set in history.Append(current))
            await InstallCacheTestSetAsync(temp.Path, set, retainedSets);

        Assert.True(IsWhollyCached(temp.Path, current));
        for (int index = 0; index < history.Length; index++)
        {
            bool kept = index >= history.Length - retainedSets;
            Assert.Equal(kept, IsWhollyCached(temp.Path, history[index]));
            Assert.Equal(kept, IsPartlyCached(temp.Path, history[index]));
        }
        Assert.True(Directory.Exists(unrelated));
    }

    [Fact]
    public async Task RuntimeInstall_FailedAcquisitionDoesNotDisplaceCompleteSet()
    {
        using var temp = new TempDirectory();
        CacheTestSet complete = CreateCacheTestSet("complete");
        CacheTestSet failed = CreateCacheTestSet("failed");
        CacheTestSet next = CreateCacheTestSet("next");
        await InstallCacheTestSetAsync(temp.Path, complete, retainedSets: 1);

        await Assert.ThrowsAsync<LocalAiArtifactInstallException>(() => InstallCacheTestSetAsync(
            temp.Path,
            failed,
            retainedSets: 1,
            failDependencyDownload: true));

        Assert.True(IsCached(temp.Path, failed.Binary));
        Assert.True(IsWhollyCached(temp.Path, complete));

        await InstallCacheTestSetAsync(temp.Path, next, retainedSets: 1);

        Assert.True(IsWhollyCached(temp.Path, next));
        Assert.True(IsWhollyCached(temp.Path, complete));
        Assert.False(IsPartlyCached(temp.Path, failed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeInstall_RejectedOrCancelledInspectionLeavesCacheUnchanged(bool cancel)
    {
        using var temp = new TempDirectory();
        CacheTestSet baseline = CreateCacheTestSet("baseline");
        CacheTestSet rejected = CreateCacheTestSet("rejected");
        await InstallCacheTestSetAsync(temp.Path, baseline, retainedSets: 0);
        Dictionary<string, (byte[] Bytes, DateTime LastWriteUtc)> before = SnapshotCache(temp.Path);
        using var cancellation = new CancellationTokenSource();
        ILlamaRuntimeInspector inspector = cancel
            ? new CancellingRuntimeInspector(cancellation)
            : new InvalidRuntimeInspector();

        Exception? error = await Record.ExceptionAsync(() => InstallCacheTestSetAsync(
            temp.Path,
            rejected,
            retainedSets: 0,
            inspector: inspector,
            cancellationToken: cancellation.Token));

        Assert.IsAssignableFrom(cancel ? typeof(OperationCanceledException) : typeof(LocalAiArtifactInstallException), error);
        Dictionary<string, (byte[] Bytes, DateTime LastWriteUtc)> after = SnapshotCache(temp.Path);
        Assert.All(before, entry =>
        {
            Assert.True(after.TryGetValue(entry.Key, out var observed), entry.Key);
            Assert.Equal(entry.Value.Bytes, observed.Bytes);
            Assert.Equal(entry.Value.LastWriteUtc, observed.LastWriteUtc);
        });
        Assert.True(IsWhollyCached(temp.Path, rejected));
        Assert.True(LocalAiPathPolicy.TryResolve(
            temp.Path,
            LlamaRuntimeInstaller.Component(rejected.Runtime),
            out LocalAiSetupPaths paths,
            out string pathError), pathError);
        Assert.False(Directory.Exists(paths.InstallDirectory));
    }

    [Fact]
    public async Task RuntimeInstall_KeepsArchiveSharedWithRetainedSet()
    {
        using var temp = new TempDirectory();
        byte[] sharedDependency = CreateZip(("cudart64_13.dll", "shared"u8.ToArray()));
        CacheTestSet oldest = CreateCacheTestSet("oldest", sharedDependency);
        CacheTestSet retained = CreateCacheTestSet("retained", sharedDependency);
        CacheTestSet current = CreateCacheTestSet("current");

        foreach (CacheTestSet set in new[] { oldest, retained, current })
            await InstallCacheTestSetAsync(temp.Path, set, retainedSets: 1);

        Assert.False(IsCached(temp.Path, oldest.Binary));
        Assert.True(IsWhollyCached(temp.Path, retained));
        Assert.True(IsWhollyCached(temp.Path, current));
    }

    [Fact]
    public async Task RuntimeInstall_UnusableSetRecordsNeitherCountNorFail()
    {
        using var temp = new TempDirectory();
        CacheTestSet complete = CreateCacheTestSet("complete");
        CacheTestSet current = CreateCacheTestSet("current");
        await InstallCacheTestSetAsync(temp.Path, complete, retainedSets: 1);
        string setsRoot = Path.Combine(temp.Path, "LocalAICache", "sets");
        string malformed = Path.Combine(setsRoot, new string('d', 64) + ".json");
        await File.WriteAllTextAsync(malformed, "{ not json");
        LocalAiArchiveCacheRetention.SetMember[] missingMembers = [new(new string('e', 64), "gone.zip")];
        string missing = Path.Combine(setsRoot, LocalAiArchiveCacheRetention.SetId(missingMembers) + ".json");
        await File.WriteAllTextAsync(missing, System.Text.Json.JsonSerializer.Serialize(
            new LocalAiArchiveCacheRetention.SetRecord(
                LocalAiArchiveCacheRetention.SetRecordSchemaVersion,
                missingMembers,
                DateTimeOffset.UtcNow.AddYears(1))));

        await InstallCacheTestSetAsync(temp.Path, current, retainedSets: 1);

        Assert.True(IsWhollyCached(temp.Path, complete));
        Assert.True(IsWhollyCached(temp.Path, current));
        Assert.False(File.Exists(malformed));
        Assert.False(File.Exists(missing));
    }

    private sealed record CacheTestSet(LlamaRuntimeVariant Runtime, byte[] Binary, byte[] Dependency);

    /// <summary>Creates a two-archive runtime set whose pins are unique to <paramref name="tag"/>.</summary>
    private static CacheTestSet CreateCacheTestSet(string tag, byte[]? dependency = null)
    {
        byte[] binary = CreateZip(("llama-server.exe", System.Text.Encoding.UTF8.GetBytes($"server-{tag}")));
        dependency ??= CreateZip(("cudart64_13.dll", System.Text.Encoding.UTF8.GetBytes($"cuda-{tag}")));
        return new(CreateRuntime(binary, dependency), binary, dependency);
    }

    private static async Task InstallCacheTestSetAsync(
        string localDataDirectory,
        CacheTestSet set,
        int retainedSets,
        bool failDependencyDownload = false,
        ILlamaRuntimeInspector? inspector = null,
        CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient(new DelegateHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("runtime.zip", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(set.Binary) }
                : failDependencyDownload
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(set.Dependency) }));
        var installer = new LlamaRuntimeInstaller(
            new LocalAiArtifactInstaller(client, archiveCacheEnabled: true, retainedSets),
            inspector ?? new ValidRuntimeInspector());

        await installer.InstallAsync(localDataDirectory, set.Runtime, progress: null, cancellationToken);
    }

    private static string ArchivesRoot(string localDataDirectory) =>
        Path.Combine(localDataDirectory, "LocalAICache", "archives");

    private static bool IsCached(string localDataDirectory, byte[] archive) =>
        Directory.Exists(Path.Combine(ArchivesRoot(localDataDirectory), Sha256(archive)));

    private static bool IsWhollyCached(string localDataDirectory, CacheTestSet set) =>
        IsCached(localDataDirectory, set.Binary) && IsCached(localDataDirectory, set.Dependency);

    private static bool IsPartlyCached(string localDataDirectory, CacheTestSet set) =>
        IsCached(localDataDirectory, set.Binary) || IsCached(localDataDirectory, set.Dependency);

    private static Dictionary<string, (byte[] Bytes, DateTime LastWriteUtc)> SnapshotCache(string localDataDirectory) =>
        Directory.EnumerateFiles(Path.Combine(localDataDirectory, "LocalAICache"), "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)));

    [Fact]
    public async Task RuntimeInstall_DisabledCacheNeitherReadsWritesNorPrunes()
    {
        using var temp = new TempDirectory();
        byte[] binaryZip = CreateZip(("llama-server.exe", "server"u8.ToArray()));
        byte[] dependencyZip = CreateZip(("cudart64_13.dll", "cuda"u8.ToArray()));
        LlamaRuntimeVariant runtime = CreateRuntime(binaryZip, dependencyZip);
        string archivesRoot = Path.Combine(temp.Path, "LocalAICache", "archives");
        string cachedBinary = Path.Combine(archivesRoot, Sha256(binaryZip), "runtime.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(cachedBinary)!);
        await File.WriteAllBytesAsync(cachedBinary, binaryZip);
        string[] stale = Enumerable.Range(1, 5)
            .Select(index => Directory.CreateDirectory(
                Path.Combine(archivesRoot, new string((char)('0' + index), 64))).FullName)
            .ToArray();
        int requests = 0;
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            requests++;
            byte[] bytes = request.RequestUri!.AbsolutePath.EndsWith("runtime.zip", StringComparison.Ordinal)
                ? binaryZip
                : dependencyZip;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var installer = new LlamaRuntimeInstaller(
            new LocalAiArtifactInstaller(client, archiveCacheEnabled: false),
            new ValidRuntimeInspector());

        LlamaRuntimeInstallResult result = await installer.InstallAsync(
            temp.Path,
            runtime,
            progress: null,
            CancellationToken.None);

        Assert.Equal(2, requests);
        Assert.Equal(0, result.ReusedCachedArchiveCount);
        Assert.False(Directory.Exists(Path.Combine(archivesRoot, Sha256(dependencyZip))));
        Assert.All(stale, entry => Assert.True(Directory.Exists(entry)));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    public void ArchiveCacheDisableVariable_ParsesSetValues(string? value, bool disabled)
    {
        Assert.Equal(disabled, LocalAiArtifactInstaller.IsArchiveCacheDisabled(value));
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData("", 3)]
    [InlineData(" ", 3)]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData(" 5 ", 5)]
    [InlineData("-1", 3)]
    [InlineData("two", 3)]
    [InlineData("1.5", 3)]
    public void ArchiveCacheRetainedSetsVariable_ParsesSetValues(string? value, int expected)
    {
        Assert.Equal(expected, LocalAiArtifactInstaller.ParseRetainedArchiveSets(value));
    }

    [Fact]
    public async Task FreshProcessUninstall_RemovesCanonicalLocalAiRoot()
    {
        using var temp = new TempDirectory();
        string root = new LocalAiPaths(temp.Path).RootDirectory;
        string sharedCacheModel = Path.Combine(temp.Path, "hf-cache", "models--owner--repo", "snapshots", new string('a', 40), "model.gguf");
        string cachedArchive = Path.Combine(temp.Path, "LocalAICache", "archives", new string('a', 64), "runtime.zip");
        Directory.CreateDirectory(Path.Combine(root, "engines", "runtime"));
        Directory.CreateDirectory(Path.GetDirectoryName(sharedCacheModel)!);
        Directory.CreateDirectory(Path.GetDirectoryName(cachedArchive)!);
        await File.WriteAllTextAsync(Path.Combine(root, "state.json"), "corrupt but app-owned");
        await File.WriteAllTextAsync(Path.Combine(root, "engines", "runtime", "file.bin"), "data");
        await File.WriteAllTextAsync(sharedCacheModel, "shared");
        await File.WriteAllTextAsync(cachedArchive, "cached");
        SetupContext context = CreateContext(temp.Path, confirmDestructive: true);
        var messages = new List<string>();
        context.Logger.LogEmitted += (_, entry) => messages.Add(entry.Message);

        PipelineResult result = await new SetupPipeline([new PersistLocalAiManifestStep()])
            .UninstallAsync(context);

        Assert.Equal(PipelineOutcome.Success, result.Outcome);
        Assert.False(Directory.Exists(root));
        Assert.Equal("shared", await File.ReadAllTextAsync(sharedCacheModel));
        Assert.Equal("cached", await File.ReadAllTextAsync(cachedArchive));
        // The logger redacts 43-character identifiers as tokens, so the variable
        // name must stay readable in the sanitized uninstall message.
        string cacheMessage = Assert.Single(messages, message => message.StartsWith("Kept the verified", StringComparison.Ordinal));
        Assert.Contains(LocalAiArtifactInstaller.RetainedArchiveSetsEnvironmentVariable, cacheMessage);
        Assert.Contains("LocalAICache", cacheMessage);
    }

    [Fact]
    public async Task FreshProcessUninstall_RejectsDescendantJunctionBeforeDeletingAnything()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        string root = new LocalAiPaths(temp.Path).RootDirectory;
        Directory.CreateDirectory(root);
        string retained = Path.Combine(root, "retained.txt");
        string outsideFile = Path.Combine(outside.Path, "outside.txt");
        await File.WriteAllTextAsync(retained, "retain");
        await File.WriteAllTextAsync(outsideFile, "outside");
        string junction = Path.Combine(root, "linked");
        CreateJunction(junction, outside.Path);
        try
        {
            PipelineResult result = await new SetupPipeline([new PersistLocalAiManifestStep()])
                .UninstallAsync(CreateContext(temp.Path, confirmDestructive: true));

            Assert.Equal(PipelineOutcome.Failed, result.Outcome);
            Assert.True(File.Exists(retained));
            Assert.True(File.Exists(outsideFile));
        }
        finally
        {
            if (Directory.Exists(junction))
                Directory.Delete(junction);
        }
    }

    [Fact]
    public async Task NormalRollback_DoesNotRemoveExistingLocalAiRoot()
    {
        using var temp = new TempDirectory();
        string root = new LocalAiPaths(temp.Path).RootDirectory;
        Directory.CreateDirectory(root);
        string retained = Path.Combine(root, "retained.txt");
        await File.WriteAllTextAsync(retained, "retain");
        SetupContext context = CreateContext(temp.Path, confirmDestructive: false);

        await new PersistLocalAiManifestStep().RollbackAsync(context, CancellationToken.None);

        Assert.True(File.Exists(retained));
    }

    private static SetupContext CreateContext(string localDataDirectory, bool confirmDestructive)
    {
        var config = new SetupConfig { ConfirmDestructive = confirmDestructive };
        var logger = new SetupLogger(filePath: null, LogLevel.Trace);
        return new SetupContext(
            config,
            logger,
            new TransactionJournal(filePath: null),
            new CommandRunner(logger),
            CancellationToken.None,
            dataDir: Path.Combine(localDataDirectory, "roaming"),
            localDataDir: localDataDirectory);
    }

    private static LocalInferencePlan CatalogPlan()
    {
        LlamaRuntimeVariant runtime = LlamaRuntimeCatalog.Find(
            System.Runtime.InteropServices.Architecture.X64)!;
        return new LocalInferencePlan(
            runtime,
            LocalModelCatalog.Default,
            LocalModelCatalog.GetProfiles(LocalModelCatalog.Default)[0],
            LocalInferenceModelSelectionOrigin.Default);
    }

    private static LocalAiInstallManifest CreateManifest(
        string localDataDirectory,
        LocalInferencePlan plan,
        string gpuId)
    {
        LocalAiComponentIdentity component = LlamaRuntimeInstaller.Component(plan.Runtime);
        Assert.True(LocalAiPathPolicy.TryResolve(
            localDataDirectory,
            component,
            out LocalAiSetupPaths setupPaths,
            out string error), error);
        var paths = new LocalAiPaths(localDataDirectory);
        var source = Assert.IsType<HuggingFaceRevisionSource>(plan.Model.Weights.Source);
        Assert.True(LocalAiPathPolicy.TryGetModelPaths(
            setupPaths,
            source.RepositoryId,
            source.RevisionSha,
            plan.Model.Weights.RelativePath,
            out string modelPath,
            out _,
            out error), error);
        string executable = Path.Combine(setupPaths.InstallDirectory, LlamaRuntimeCatalog.ServerExecutableName);
        return new LocalAiInstallManifest
        {
            EngineVersion = LlamaRuntimeCatalog.ReleaseTag,
            Architecture = "x64",
            RuntimeId = plan.Runtime.Id,
            ModelCatalogId = plan.Model.Id,
            SelectedGpuId = gpuId,
            ExecutablePath = Path.GetRelativePath(paths.RootDirectory, executable),
            RuntimeAssets = plan.Runtime.Artifacts.Select(artifact => new LocalAiAssetReceipt
            {
                FileName = Path.GetFileName(artifact.RelativePath),
                SourceUrl = artifact.DownloadUri.AbsoluteUri,
                SizeBytes = artifact.SizeBytes,
                Sha256 = artifact.Sha256.Value,
            }).ToImmutableArray(),
            ModelPath = Path.GetRelativePath(paths.RootDirectory, modelPath),
            ModelId = $"{source.RepositoryId}@{source.RevisionSha}",
            ModelAlias = plan.Model.Id,
            ModelAsset = new LocalAiAssetReceipt
            {
                FileName = Path.GetFileName(plan.Model.Weights.RelativePath),
                SourceUrl = plan.Model.Weights.DownloadUri.AbsoluteUri,
                SizeBytes = plan.Model.Weights.SizeBytes,
                Sha256 = plan.Model.Weights.Sha256.Value,
            },
            Endpoint = "http://127.0.0.1:18803/v1",
            ContextLength = plan.Profile.ContextTokens,
            KeyCachePrecision = plan.Profile.KeyCachePrecision,
            ValueCachePrecision = plan.Profile.ValueCachePrecision,
            DraftKeyCachePrecision = plan.Profile.DraftKeyCachePrecision,
            DraftValueCachePrecision = plan.Profile.DraftValueCachePrecision,
        };
    }

    private static LocalAiInstallManifest CreateRetiredManifest(
        string localDataDirectory,
        LocalInferencePlan plan,
        string gpuId)
    {
        LlamaRuntimeVariant retired = LlamaRuntimeCatalog.FindInstalled("b10655-cuda13-x64")!;
        return CreateManifest(localDataDirectory, plan, gpuId) with
        {
            EngineVersion = "b10655",
            RuntimeId = retired.Id,
            // Use the shipped path, not the installer helper under test.
            ExecutablePath = Path.Combine("engines", "llama-server", "b10655", "win-x64", "llama-server.exe"),
            RuntimeAssets = retired.Artifacts.Select(artifact => new LocalAiAssetReceipt
            {
                FileName = Path.GetFileName(artifact.RelativePath),
                SourceUrl = artifact.DownloadUri.AbsoluteUri,
                SizeBytes = artifact.SizeBytes,
                Sha256 = artifact.Sha256.Value,
            }).ToImmutableArray(),
        };
    }

    private static LocalModelInfo CreateModel(byte[] bytes)
    {
        var source = new HuggingFaceRevisionSource("owner/repo", new string('a', 40));
        var artifact = new PinnedArtifact(
            "test-model",
            ArtifactRole.ModelWeights,
            source,
            "model.gguf",
            bytes.Length,
            new Sha256Digest(Sha256(bytes)));
        return new LocalModelInfo(
            "test-model",
            "Test model",
            "Test",
            "Q4",
            artifact,
            new LocalModelRunRecipe(
                128,
                128,
                1,
                1,
                1,
                128,
                true,
                true,
                SpeculativeDecodingMode.DraftMtp,
                1,
                new ModelSamplingPreset(0.6, 20, 0.95, 0, 1, 0)),
            IsDefault: true,
            IsExplicitAlternative: false,
            SupportsVision: false);
    }

    private static LocalModelInfo CreateModelWithDraft(byte[] primaryBytes, PinnedArtifact draftWeights)
    {
        var source = new HuggingFaceRevisionSource("owner/repo", new string('a', 40));
        var artifact = new PinnedArtifact(
            "test-model-dflash",
            ArtifactRole.ModelWeights,
            source,
            "model.gguf",
            primaryBytes.Length,
            new Sha256Digest(Sha256(primaryBytes)));
        return new LocalModelInfo(
            "test-model-dflash",
            "Test model (DFlash)",
            "Test",
            "Q4",
            artifact,
            new LocalModelRunRecipe(
                128,
                128,
                1,
                1,
                1,
                128,
                true,
                true,
                SpeculativeDecodingMode.DraftDFlash,
                1,
                new ModelSamplingPreset(0.6, 20, 0.95, 0, 1, 0),
                draftWeights),
            IsDefault: true,
            IsExplicitAlternative: false,
            SupportsVision: false);
    }

    private static LlamaRuntimeVariant CreateRuntime(byte[] binaryZip, byte[] dependencyZip)
    {
        var source = new GitHubReleaseSource("owner/repo", "v1", new string('b', 40));
        return new LlamaRuntimeVariant(
            "test-runtime",
            Architecture.X64,
            new Version(13, 0),
            [
                new PinnedArtifact(
                    "test-runtime-bin",
                    ArtifactRole.RuntimeBinary,
                    source,
                    "runtime.zip",
                    binaryZip.Length,
                    new Sha256Digest(Sha256(binaryZip))),
                new PinnedArtifact(
                    "test-runtime-dep",
                    ArtifactRole.RuntimeDependency,
                    source,
                    "dependency.zip",
                    dependencyZip.Length,
                    new Sha256Digest(Sha256(dependencyZip))),
            ]);
    }

    private static (string ModelPath, string PartialPath) ResolveModelPaths(
        string localDataDirectory,
        LocalAiComponentIdentity component,
        LocalModelInfo model)
    {
        var source = Assert.IsType<HuggingFaceRevisionSource>(model.Weights.Source);
        Assert.True(HuggingFaceHubCache.TryGetSnapshotPaths(
            CacheRoot(localDataDirectory),
            source.RepositoryId,
            source.RevisionSha,
            model.Weights.RelativePath,
            out string modelPath,
            out string partialPath,
            out string error), error);
        return (modelPath, partialPath);
    }

    private static HuggingFaceModelInstaller CreateModelInstaller(
        HttpClient client,
        string localDataDirectory) =>
        new(client, Task.Delay, () => CacheRoot(localDataDirectory));

    private static string CacheRoot(string localDataDirectory) =>
        Path.Combine(localDataDirectory, "hf-cache");

    private static LocalAiComponentIdentity TestComponent() =>
        new("llama-server", "v1", "win-x64");

    private static byte[] CreateZip(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                using Stream destination = entry.Open();
                destination.Write(content);
            }
        }
        return stream.ToArray();
    }

    private static byte[] CreateZip(params (string Name, byte[] Content, int ExternalAttributes)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content, int externalAttributes) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name);
                entry.ExternalAttributes = externalAttributes;
                using Stream destination = entry.Open();
                destination.Write(content);
            }
        }
        return stream.ToArray();
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void CreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Failed to start mklink.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }

    private sealed class AsyncDelegateHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }

    private sealed class BlockingReadStream(
        byte[] bytes,
        TaskCompletionSource entered,
        TaskCompletionSource release) : MemoryStream(bytes, writable: false)
    {
        private bool _blocked;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_blocked)
            {
                _blocked = true;
                entered.SetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class EofCallbackStream(byte[] bytes, Action callback)
        : MemoryStream(bytes, writable: false)
    {
        private bool _called;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);
            InvokeOnEof(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = base.Read(buffer);
            InvokeOnEof(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int read = await base.ReadAsync(buffer, cancellationToken);
            InvokeOnEof(read);
            return read;
        }

        private void InvokeOnEof(int read)
        {
            if (read != 0 || _called)
                return;
            _called = true;
            callback();
        }
    }

    private sealed class ThrowAfterPrefixStream(byte[] bytes, int prefixLength) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= prefixLength)
                throw new IOException("Simulated interrupted response body.");
            int length = Math.Min(Math.Min(count, prefixLength - _position), bytes.Length - _position);
            Array.Copy(bytes, _position, buffer, offset, length);
            _position += length;
            return length;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position >= prefixLength)
                return ValueTask.FromException<int>(new IOException("Simulated interrupted response body."));
            int length = Math.Min(Math.Min(buffer.Length, prefixLength - _position), bytes.Length - _position);
            bytes.AsMemory(_position, length).CopyTo(buffer);
            _position += length;
            return ValueTask.FromResult(length);
        }
    }

    private sealed class ValidRuntimeInspector : ILlamaRuntimeInspector
    {
        public Task<LlamaRuntimeInspection> InspectAsync(
            string installDirectory,
            LlamaRuntimeVariant runtime,
            CancellationToken cancellationToken) =>
            Task.FromResult(new LlamaRuntimeInspection(true, null));
    }

    private sealed class InvalidRuntimeInspector : ILlamaRuntimeInspector
    {
        public Task<LlamaRuntimeInspection> InspectAsync(
            string installDirectory,
            LlamaRuntimeVariant runtime,
            CancellationToken cancellationToken) =>
            Task.FromResult(new LlamaRuntimeInspection(false, "simulated corrupted runtime"));
    }

    private sealed class VcRuntimeAssertingInspector : ILlamaRuntimeInspector
    {
        public Task<LlamaRuntimeInspection> InspectAsync(
            string installDirectory,
            LlamaRuntimeVariant runtime,
            CancellationToken cancellationToken)
        {
            Assert.All(
                LocalAiVcRuntimeStager.RequiredFiles,
                fileName => Assert.True(File.Exists(Path.Combine(installDirectory, fileName))));
            return Task.FromResult(new LlamaRuntimeInspection(true, null));
        }
    }

    private sealed class VcRuntimeAbsentInspector : ILlamaRuntimeInspector
    {
        public Task<LlamaRuntimeInspection> InspectAsync(
            string installDirectory,
            LlamaRuntimeVariant runtime,
            CancellationToken cancellationToken)
        {
            Assert.DoesNotContain(
                LocalAiVcRuntimeStager.RequiredFiles,
                fileName => File.Exists(Path.Combine(installDirectory, fileName)));
            return Task.FromResult(new LlamaRuntimeInspection(false, "missing VC runtime"));
        }
    }

    /// <summary>Accepts the runtime but cancels the install as inspection returns.</summary>
    private sealed class CancellingRuntimeInspector(CancellationTokenSource cancellation) : ILlamaRuntimeInspector
    {
        public Task<LlamaRuntimeInspection> InspectAsync(
            string installDirectory,
            LlamaRuntimeVariant runtime,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromResult(new LlamaRuntimeInspection(true, null));
        }
    }

    private sealed class AcceptingModelVerifier : ILocalAiModelFileVerifier
    {
        public Task<bool> VerifyActiveAsync(
            LocalAiResolvedInstall install,
            PinnedArtifact artifact,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> VerifyLegacyCompatibilityAsync(
            LocalAiResolvedInstall install,
            LocalAiPaths paths,
            PinnedArtifact artifact,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> VerifyAdditionalAssetAsync(
            LocalAiResolvedInstall install,
            string cachedAssetPath,
            PinnedArtifact artifact,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class RejectingModelVerifier : ILocalAiModelFileVerifier
    {
        public Task<bool> VerifyActiveAsync(
            LocalAiResolvedInstall install,
            PinnedArtifact artifact,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> VerifyLegacyCompatibilityAsync(
            LocalAiResolvedInstall install,
            LocalAiPaths paths,
            PinnedArtifact artifact,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> VerifyAdditionalAssetAsync(
            LocalAiResolvedInstall install,
            string cachedAssetPath,
            PinnedArtifact artifact,
            CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class CompleteModelRepairStep(string localDataDirectory, LocalInferencePlan plan) : SetupStep
    {
        public override string Id => "complete-model-repair";
        public override string DisplayName => "Complete model repair";

        public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
        {
            LocalAiComponentIdentity component = LlamaRuntimeInstaller.Component(plan.Runtime);
            Assert.True(LocalAiPathPolicy.TryResolve(
                localDataDirectory,
                component,
                out LocalAiSetupPaths setupPaths,
                out string error), error);
            HuggingFaceRevisionSource source =
                Assert.IsType<HuggingFaceRevisionSource>(plan.Model.Weights.Source);
            Assert.True(LocalAiPathPolicy.TryGetModelPaths(
                setupPaths,
                source.RepositoryId,
                source.RevisionSha,
                plan.Model.Weights.RelativePath,
                out string legacyModelPath,
                out _,
                out error), error);
            Assert.True(HuggingFaceHubCache.TryGetSnapshotPaths(
                CacheRoot(localDataDirectory),
                source.RepositoryId,
                source.RevisionSha,
                plan.Model.Weights.RelativePath,
                out string cachedModelPath,
                out _,
                out error), error);
            ctx.LocalAiModelInstall = new HuggingFaceModelInstallResult(
                cachedModelPath,
                CacheRoot(localDataDirectory),
                HuggingFaceModelInstallDisposition.Downloaded,
                CreatedThisRun: true,
                legacyModelPath,
                LegacyCreatedThisRun: true);
            return Task.FromResult(StepResult.Ok("Model repaired."));
        }
    }

    private sealed class UpgradeCheckpointStep(
        string id,
        Func<SetupContext, bool> shouldFail) : SetupStep
    {
        public override string Id => id;
        public override string DisplayName => id;
        public override bool CanRetry => false;

        public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct) =>
            Task.FromResult(shouldFail(ctx) ? StepResult.Fail("Injected upgrade failure.") : StepResult.Ok("Checked."));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "OpenClawLocalAiRecoveryTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
