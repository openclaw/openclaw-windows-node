using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

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
    public async Task FreshProcessUninstall_RemovesCanonicalLocalAiRoot()
    {
        using var temp = new TempDirectory();
        string root = new LocalAiPaths(temp.Path).RootDirectory;
        string sharedCacheModel = Path.Combine(temp.Path, "hf-cache", "models--owner--repo", "snapshots", new string('a', 40), "model.gguf");
        Directory.CreateDirectory(Path.Combine(root, "engines", "runtime"));
        Directory.CreateDirectory(Path.GetDirectoryName(sharedCacheModel)!);
        await File.WriteAllTextAsync(Path.Combine(root, "state.json"), "corrupt but app-owned");
        await File.WriteAllTextAsync(Path.Combine(root, "engines", "runtime", "file.bin"), "data");
        await File.WriteAllTextAsync(sharedCacheModel, "shared");
        SetupContext context = CreateContext(temp.Path, confirmDestructive: true);

        PipelineResult result = await new SetupPipeline([new PersistLocalAiManifestStep()])
            .UninstallAsync(context);

        Assert.Equal(PipelineOutcome.Success, result.Outcome);
        Assert.False(Directory.Exists(root));
        Assert.Equal("shared", await File.ReadAllTextAsync(sharedCacheModel));
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
            CancellationToken cancellationToken) =>
            Task.FromResult(new LlamaRuntimeInspection(true, "valid", null));
    }

    private sealed class AcceptingModelVerifier : ILocalAiModelFileVerifier
    {
        public Task<bool> VerifyAsync(
            LocalAiResolvedInstall install,
            PinnedArtifact artifact,
            CancellationToken cancellationToken) => Task.FromResult(true);
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
