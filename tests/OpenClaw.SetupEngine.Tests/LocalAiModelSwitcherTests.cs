using System.Collections.Immutable;
using System.Net;
using System.Text;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiModelSwitcherTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private const string Sha = "50968a4468ef4233ed78cd7c3de230dd1d61a56b";

    [Fact]
    public async Task Resolver_PinsQuantizedFileAndReadsHeaderGeometry()
    {
        var handler = new HubHandler();
        handler.Revision("unsloth/Qwen3-0.6B-GGUF", "main", gated: "false");
        handler.Tree("unsloth/Qwen3-0.6B-GGUF",
            Lfs("Qwen3-0.6B-Q4_K_M.gguf", 396_705_472, 'a'),
            Lfs("Qwen3-0.6B-UD-Q4_K_M.gguf", 400_000_000, 'b'),
            Lfs("mmproj-Qwen3-0.6B-Q4_K_M.gguf", 100, 'c'),
            Lfs("Qwen3-0.6B-Q8_0.gguf", 600_000_000, 'd'));
        handler.File("unsloth/Qwen3-0.6B-GGUF", "Qwen3-0.6B-Q4_K_M.gguf", GgufHeader("qwen3", 28, 8, 128, 40960));

        LocalCustomModelRecipe recipe = await new HuggingFaceModelReferenceResolver(new HttpClient(handler))
            .ResolveAsync("unsloth/Qwen3-0.6B-GGUF:Q4_K_M", CancellationToken.None);

        Assert.Equal(new LocalCustomModelRecipe("unsloth/Qwen3-0.6B-GGUF", Sha, "Qwen3-0.6B-Q4_K_M.gguf",
            396_705_472, new string('a', 64), "Qwen3-0.6B-Q4_K_M", "qwen3", 28, 8, 128, 40960), recipe);
    }

    [Fact]
    public async Task Resolver_FollowsTreePaginationAndRequiresQuantForAmbiguousRepo()
    {
        var handler = new HubHandler();
        handler.Revision("owner/many-GGUF", "main", gated: "false");
        handler.PagedTree("owner/many-GGUF", $"https://huggingface.co/api/models/owner/many-GGUF/tree/{Sha}?recursive=true&cursor=2",
            Lfs("model-Q5_K_M.gguf", 10, 'a'));
        handler.Page($"/api/models/owner/many-GGUF/tree/{Sha}?recursive=true&cursor=2", Lfs("model-Q6_K.gguf", 10, 'b'));
        var resolver = new HuggingFaceModelReferenceResolver(new HttpClient(handler));

        LocalAiModelSwitchException error = await Assert.ThrowsAsync<LocalAiModelSwitchException>(
            () => resolver.ResolveAsync("owner/many-GGUF", CancellationToken.None));
        Assert.Contains("several GGUF files", error.Message);

        LocalAiModelSwitchException missing = await Assert.ThrowsAsync<LocalAiModelSwitchException>(
            () => resolver.ResolveAsync("owner/many-GGUF:Q4_K_M", CancellationToken.None));
        Assert.Contains("Q5_K_M", missing.Message);
        Assert.Contains("Q6_K", missing.Message);
    }

    [Theory]
    [InlineData("gated-GGUF", "\"auto\"", "gated")]
    [InlineData("missing-GGUF", null, "not found")]
    [InlineData("split-GGUF", "false", "Split GGUF")]
    public async Task Resolver_RejectsGatedMissingAndSplitModels(string repo, string? gated, string expected)
    {
        var handler = new HubHandler();
        if (gated is not null)
            handler.Revision($"owner/{repo}", "main", gated);
        handler.Tree($"owner/{repo}", Lfs("m-Q4_K_M-00001-of-00002.gguf", 10, 'a'), Lfs("m-Q4_K_M-00002-of-00002.gguf", 10, 'b'));

        LocalAiModelSwitchException error = await Assert.ThrowsAsync<LocalAiModelSwitchException>(
            () => new HuggingFaceModelReferenceResolver(new HttpClient(handler)).ResolveAsync($"owner/{repo}", CancellationToken.None));
        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OversizedCustomModel_NeedsForceAndNeverDownloadsWithoutIt()
    {
        using var fixture = await Fixture.CreateAsync();
        LocalAiModelSwitchTarget target = await fixture.Switcher.PrepareCustomAsync(Hardware(8 * GiB), "owner/big", default);

        Assert.True(target.RequiresForce);
        await Assert.ThrowsAsync<LocalAiModelSwitchException>(() => fixture.Switcher.SwitchAsync(target, false, null, default));
        Assert.Equal(0, fixture.Acquirer.Installs);
        Assert.Empty(fixture.Runtime.Calls);
    }

    [Fact]
    public async Task DownloadFailure_LeavesRuntimeAndReceiptUntouched()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Acquirer.Failure = new HuggingFaceModelInstallException("disk full");
        byte[] before = await File.ReadAllBytesAsync(fixture.Paths.ManifestPath);
        LocalAiModelSwitchTarget target = await fixture.Switcher.PrepareCustomAsync(Hardware(48 * GiB), "owner/small", default);

        LocalAiModelSwitchException error = await Assert.ThrowsAsync<LocalAiModelSwitchException>(
            () => fixture.Switcher.SwitchAsync(target, false, null, default));

        Assert.Contains("disk full", error.Message);
        Assert.Empty(fixture.Runtime.Calls);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Paths.ManifestPath));
    }

    [Fact]
    public async Task StoppedRuntime_CommitsCustomReceiptWithoutStarting()
    {
        using var fixture = await Fixture.CreateAsync();
        LocalAiModelSwitchTarget target = await fixture.Switcher.PrepareCustomAsync(Hardware(48 * GiB), "owner/small", default);

        await fixture.Switcher.SwitchAsync(target, false, null, default);

        Assert.Equal(["replace:" + target.Plan.Model.Id], fixture.Runtime.Calls);
        LocalAiInstallManifest committed = fixture.Runtime.Replacements.Single();
        Assert.Equal(LocalAiInstallManifest.CustomModelSchemaVersion, committed.SchemaVersion);
        Assert.Equal(fixture.Original.RuntimeId, committed.RuntimeId);
        Assert.Equal(fixture.Original.ExecutablePath, committed.ExecutablePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedStart_RestoresPreviousReceiptAndRestarts(bool startThrows)
    {
        using var fixture = await Fixture.CreateAsync(running: true);
        if (startThrows)
            fixture.Runtime.StartFailure = new InvalidOperationException("The native Gateway is not connected.");
        else
            fixture.Runtime.StartResults.Enqueue(LocalAiRuntimeState.Failed);
        fixture.Runtime.StartResults.Enqueue(LocalAiRuntimeState.Healthy);
        LocalAiModelSwitchTarget target = await fixture.Switcher.PrepareCustomAsync(Hardware(48 * GiB), "owner/small", default);

        LocalAiModelSwitchException error = await Assert.ThrowsAsync<LocalAiModelSwitchException>(
            () => fixture.Switcher.SwitchAsync(target, false, null, default));

        Assert.Contains("went back to the previous model", error.Message);
        Assert.Equal(["replace:" + target.Plan.Model.Id, "start", "replace:" + fixture.Original.ModelCatalogId, "start"],
            fixture.Runtime.Calls);
    }

    private static HostHardwareInfo Hardware(long vram) =>
        new(System.Runtime.InteropServices.Architecture.X64, 64 * GiB, 48 * GiB,
            [new GpuInfo(GpuVendor.Nvidia, "NVIDIA Test", vram, vram, DriverVersion: "620.0", CudaMajorVersion: 13, StableId: "GPU-test")],
            false, new LocalAiDiskSpaceInfo(500 * GiB, 500 * GiB, true));

    private static LocalCustomModelRecipe Recipe(long size) =>
        new("owner/model-GGUF", Sha, "model-Q4_K_M.gguf", size, new string('e', 64), "model-Q4_K_M", "llama", 32, 8, 128, 65536);

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp;

        private Fixture(TempDirectory temp, LocalAiPaths paths, LocalAiInstallManifest original, FakeRuntime runtime,
            FakeAcquirer acquirer, LocalAiModelSwitcher switcher)
        {
            _temp = temp; Paths = paths; Original = original; Runtime = runtime; Acquirer = acquirer; Switcher = switcher;
        }

        public LocalAiPaths Paths { get; }
        public LocalAiInstallManifest Original { get; }
        public FakeRuntime Runtime { get; }
        public FakeAcquirer Acquirer { get; }
        public LocalAiModelSwitcher Switcher { get; }

        public static async Task<Fixture> CreateAsync(bool running = false)
        {
            var temp = new TempDirectory("local-ai-switch-");
            var paths = new LocalAiPaths(temp.Path);
            LlamaRuntimeVariant runtimeVariant = LlamaRuntimeCatalog.Find(System.Runtime.InteropServices.Architecture.X64)!;
            LocalModelInfo model = LocalModelCatalog.Find(LocalModelCatalog.Bonsai2ModelId)!;
            LocalInferencePlan plan = new(runtimeVariant, model, LocalModelCatalog.GetProfiles(model)[0], LocalInferenceModelSelectionOrigin.Explicit);
            Assert.True(LocalAiPathPolicy.TryResolve(temp.Path, LlamaRuntimeInstaller.Component(runtimeVariant), out LocalAiSetupPaths setup, out string error), error);
            var source = (HuggingFaceRevisionSource)model.Weights.Source;
            Assert.True(LocalAiPathPolicy.TryGetModelPaths(setup, source.RepositoryId, source.RevisionSha, model.Weights.RelativePath,
                out string modelPath, out _, out error), error);
            var original = new LocalAiInstallManifest
            {
                EngineVersion = runtimeVariant.ReleaseTag,
                Architecture = "x64",
                RuntimeId = runtimeVariant.Id,
                ModelCatalogId = model.Id,
                SelectedGpuId = "GPU-test",
                ExecutablePath = Path.GetRelativePath(paths.RootDirectory, Path.Combine(setup.InstallDirectory, LlamaRuntimeCatalog.ServerExecutableName)),
                RuntimeAssets = runtimeVariant.Artifacts.Select(a => new LocalAiAssetReceipt
                {
                    FileName = Path.GetFileName(a.RelativePath), SourceUrl = a.DownloadUri.AbsoluteUri,
                    SizeBytes = a.SizeBytes, Sha256 = a.Sha256.Value,
                }).ToImmutableArray(),
                ModelPath = Path.GetRelativePath(paths.RootDirectory, modelPath),
                ModelId = $"{source.RepositoryId}@{source.RevisionSha}",
                ModelAlias = model.Id,
                ModelAsset = new LocalAiAssetReceipt
                {
                    FileName = Path.GetFileName(model.Weights.RelativePath), SourceUrl = model.Weights.DownloadUri.AbsoluteUri,
                    SizeBytes = model.Weights.SizeBytes, Sha256 = model.Weights.Sha256.Value,
                },
                ContextLength = plan.Profile.ContextTokens,
            };
            await new LocalAiManifestStore(paths).SaveAsync(original);
            var runtime = new FakeRuntime(running);
            var acquirer = new FakeAcquirer(temp.Path);
            var switcher = new LocalAiModelSwitcher(runtime, temp.Path, acquirer, new UnusedRuntimeAcquirer(),
                (reference, _) => Task.FromResult(Recipe(reference == "owner/big" ? 30 * GiB : 2 * GiB)));
            return new Fixture(temp, paths, original, runtime, acquirer, switcher);
        }

        public void Dispose() => _temp.Dispose();
    }

    private sealed class FakeAcquirer(string localDataDirectory) : IHuggingFaceModelAcquirer
    {
        public int Installs { get; private set; }
        public Exception? Failure { get; set; }

        public Task<HuggingFaceModelInstallResult> InstallAsync(string dir, LocalAiComponentIdentity component, LocalModelInfo model,
            IProgress<HuggingFaceModelInstallProgress>? progress, CancellationToken ct)
        {
            Installs++;
            if (Failure is not null)
                throw Failure;
            var source = (HuggingFaceRevisionSource)model.Weights.Source;
            string cacheRoot = Path.Combine(localDataDirectory, "hf-cache");
            Assert.True(HuggingFaceHubCache.TryGetSnapshotPaths(cacheRoot, source.RepositoryId, source.RevisionSha,
                model.Weights.RelativePath, out string cached, out _, out string error), error);
            Assert.True(LocalAiPathPolicy.TryResolve(dir, component, out LocalAiSetupPaths setup, out error), error);
            Assert.True(LocalAiPathPolicy.TryGetModelPaths(setup, source.RepositoryId, source.RevisionSha,
                model.Weights.RelativePath, out string legacy, out _, out error), error);
            progress?.Report(new(model.Weights.SizeBytes, model.Weights.SizeBytes));
            return Task.FromResult(new HuggingFaceModelInstallResult(cached, cacheRoot,
                HuggingFaceModelInstallDisposition.Downloaded, true, legacy));
        }

        public Task<HuggingFaceAdditionalAssetInstallResult> InstallAdditionalAssetAsync(string dir, PinnedArtifact artifact,
            IProgress<HuggingFaceModelInstallProgress>? progress, CancellationToken ct) => throw new NotSupportedException();
        public void RemoveInstalledModel(string dir, HuggingFaceModelInstallResult install) { }
        public void RemovePartialModel(string dir, LocalAiComponentIdentity component, LocalModelInfo model) { }
    }

    private sealed class UnusedRuntimeAcquirer : ILlamaRuntimeAcquirer
    {
        public Task<LlamaRuntimeInstallResult> InstallAsync(string dir, LlamaRuntimeVariant runtime,
            IProgress<LocalAiArtifactInstallProgress>? progress, CancellationToken ct) =>
            throw new InvalidOperationException("Same-family switches reuse the installed runtime.");
        public void RemoveInstalledRuntime(string dir, LlamaRuntimeInstallResult install) { }
    }

    private sealed class FakeRuntime(bool running) : ILocalAiRuntime
    {
        public List<string> Calls { get; } = [];
        public List<LocalAiInstallManifest> Replacements { get; } = [];
        public Queue<LocalAiRuntimeState> StartResults { get; } = new();
        public LocalAiRuntimeSnapshot Snapshot { get; private set; } = Create(running ? LocalAiRuntimeState.Healthy : LocalAiRuntimeState.Stopped);
        public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged { add { } remove { } }

        public Task<LocalAiRuntimeSnapshot> ReplaceInstallAsync(LocalAiInstallManifest replacement, CancellationToken cancellationToken = default)
        {
            Calls.Add("replace:" + replacement.ModelCatalogId);
            Replacements.Add(replacement);
            Snapshot = Create(LocalAiRuntimeState.Stopped);
            return Task.FromResult(Snapshot);
        }

        /// <summary>Thrown by the next start only, then cleared.</summary>
        public Exception? StartFailure { get; set; }

        public Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("start");
            if (StartFailure is { } failure)
            {
                StartFailure = null;
                return Task.FromException<LocalAiRuntimeSnapshot>(failure);
            }
            Snapshot = Create(StartResults.Count > 0 ? StartResults.Dequeue() : LocalAiRuntimeState.Healthy);
            return Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> ResumeAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static LocalAiRuntimeSnapshot Create(LocalAiRuntimeState state) =>
            LocalAiRuntimeSnapshot.Initial(new Uri("http://127.0.0.1:18080/v1"), DateTimeOffset.UtcNow) with
            {
                State = state,
                Ownership = state == LocalAiRuntimeState.Healthy ? LocalAiOwnership.CompanionManaged : LocalAiOwnership.None,
                Detail = state == LocalAiRuntimeState.Failed ? "did not fit" : null,
            };
    }

    private static string Lfs(string path, long size, char hash) =>
        $$$"""{"type":"file","path":"{{{path}}}","size":{{{size}}},"lfs":{"oid":"{{{new string(hash, 64)}}}","size":{{{size}}}}}""";

    private static byte[] GgufHeader(string architecture, uint blocks, uint kvHeads, uint keyLength, uint context)
    {
        var body = new MemoryStream();
        void U32(uint v) => body.Write(BitConverter.GetBytes(v));
        void U64(ulong v) => body.Write(BitConverter.GetBytes(v));
        void Str(string v) { byte[] b = Encoding.UTF8.GetBytes(v); U64((ulong)b.Length); body.Write(b); }
        void Uint(string key, uint value) { Str(key); U32(4); U32(value); }
        Str("general.architecture"); U32(8); Str(architecture);
        Uint($"{architecture}.block_count", blocks);
        Uint($"{architecture}.attention.head_count_kv", kvHeads);
        Uint($"{architecture}.attention.key_length", keyLength);
        Uint($"{architecture}.context_length", context);
        var file = new MemoryStream();
        file.Write("GGUF"u8);
        file.Write(BitConverter.GetBytes(3u));
        file.Write(BitConverter.GetBytes(0ul));
        file.Write(BitConverter.GetBytes(5ul));
        body.WriteTo(file);
        file.Write(new byte[64]);
        return file.ToArray();
    }

    private sealed class HubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

        public void Revision(string repo, string revision, string gated) =>
            _routes[$"/api/models/{repo}/revision/{revision}"] = () => Json($$"""{"id":"{{repo}}","sha":"{{Sha}}","gated":{{gated}}}""");

        public void Tree(string repo, params string[] entries) => PagedTree(repo, null, entries);

        public void PagedTree(string repo, string? next, params string[] entries) =>
            _routes[$"/api/models/{repo}/tree/{Sha}?recursive=true"] = () =>
            {
                HttpResponseMessage response = Json("[" + string.Join(",", entries) + "]");
                if (next is not null)
                    response.Headers.TryAddWithoutValidation("Link", $"<{next}>; rel=\"next\"");
                return response;
            };

        public void Page(string pathAndQuery, params string[] entries) =>
            _routes[pathAndQuery] = () => Json("[" + string.Join(",", entries) + "]");

        public void File(string repo, string path, byte[] content) =>
            _routes[$"/{repo}/resolve/{Sha}/{path}"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string key = request.RequestUri!.PathAndQuery;
            HttpResponseMessage response = _routes.TryGetValue(key, out var route) ? route() : new HttpResponseMessage(HttpStatusCode.NotFound);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
