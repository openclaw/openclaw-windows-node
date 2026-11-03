using System.Collections.Immutable;
using System.Net;
using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Connection.Tests;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

// Real runtime/receipt owners with only native process and HTTP boundaries substituted.
internal sealed class LocalAiSetupRuntimeFixture : ILlamaServerRuntimePlatform, ILocalAiManagedProcessHost,
    ILlamaServerClient, ILocalAiEndpointLifecycle
{
    public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.Parse("2026-08-18T12:00:00Z");
    public int Port { get; set; } = 28769;
    public List<LocalAiResolvedInstall> Withdrawn { get; } = [];
    public List<LocalAiResolvedInstall> Published { get; } = [];
    public Func<LocalAiResolvedInstall, Task>? OnPublish { get; init; }
    private Process? _process;

    public LlamaServerRuntimeService CreateRuntime(LocalAiPaths paths) => new(new()
    {
        Paths = paths,
        EndpointLifecycle = this,
        ResolveChildProcessPath = static path => path,
    }, NullLogger.Instance, this, this, this);

    public static async Task<LocalAiResolvedInstall> PrepareInstallAsync(LocalAiPaths paths)
    {
        var runtime = LlamaRuntimeCatalog.Find(System.Runtime.InteropServices.Architecture.X64)!;
        var model = LocalModelCatalog.FindInstalled(LocalModelCatalog.Qwen35BModelId)!;
        var source = (HuggingFaceRevisionSource)model.Weights.Source;
        var profile = LocalModelCatalog.GetProfiles(model)[0];
        static LocalAiAssetReceipt Receipt(PinnedArtifact artifact) => new()
        {
            FileName = Path.GetFileName(artifact.RelativePath), SourceUrl = artifact.DownloadUri.AbsoluteUri,
            SizeBytes = artifact.SizeBytes, Sha256 = artifact.Sha256.Value,
        };
        var manifest = new LocalAiInstallManifest
        {
            EngineVersion = runtime.ReleaseTag, Architecture = "x64", RuntimeId = runtime.Id,
            ModelCatalogId = model.Id, SelectedGpuId = "GPU-test", ModelAlias = model.Id,
            ExecutablePath = Path.Combine("engines", runtime.Id, LlamaRuntimeCatalog.ServerExecutableName),
            RuntimeAssets = runtime.Artifacts.Select(Receipt).ToImmutableArray(),
            ModelPath = Path.Combine("models", model.Weights.RelativePath),
            ModelId = $"{source.RepositoryId}@{source.RevisionSha}", ModelAsset = Receipt(model.Weights),
            ContextLength = profile.ContextTokens, KeyCachePrecision = profile.KeyCachePrecision,
            ValueCachePrecision = profile.ValueCachePrecision, DraftKeyCachePrecision = profile.DraftKeyCachePrecision,
            DraftValueCachePrecision = profile.DraftValueCachePrecision,
        };
        var store = new LocalAiManifestStore(paths);
        await store.SaveAsync(manifest);
        var install = (await store.LoadAsync())!;
        Directory.CreateDirectory(Path.GetDirectoryName(install.ExecutablePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(install.ModelPath)!);
        await File.WriteAllTextAsync(install.ExecutablePath, "fixture executable");
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(install.ExecutablePath)!,
            LlamaRuntimeCatalog.ServerImplementationLibraryName), "fixture library");
        SparseFixtureFile.Create(install.ModelPath, manifest.ModelAsset.SizeBytes);
        return install;
    }

    public WindowsTcpListenerSnapshotResult CaptureListeners() => new(
        _process is { HasExited: false } process
            ? [new(IPAddress.Loopback, Port, process.ProcessId, "llama-server", null, process.StartedAtUtc.UtcDateTime)]
            : [], Ipv4Complete: true, Ipv6Complete: true);

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); UtcNow += delay; return Task.CompletedTask; }

    public Task<ILocalAiManagedProcess> StartProcessAsync(LocalAiProcessStartSpec spec,
        Action<LocalAiManagedProcessExit> exited, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); _process = new(UtcNow); return Task.FromResult<ILocalAiManagedProcess>(_process); }

    public Task<LlamaServerRouterProbeResult> ProbeManagedModelAsync(Uri endpoint, string modelAlias,
        string expectedModelPath, CancellationToken cancellationToken = default) => Task.FromResult(
            new LlamaServerRouterProbeResult(true, LocalAiModelAvailabilityState.Verified, expectedModelPath, null));

    public Task<LocalAiEndpointLifecycleResult> QuiesceAsync(LocalAiResolvedInstall install,
        LocalAiQuiesceReason reason = LocalAiQuiesceReason.Teardown, CancellationToken cancellationToken = default)
    {
        if (reason == LocalAiQuiesceReason.Teardown) Withdrawn.Add(install);
        return Task.FromResult(LocalAiEndpointLifecycleResult.Ok());
    }

    public async Task<LocalAiEndpointLifecycleResult> PublishAsync(LocalAiResolvedInstall install,
        CancellationToken cancellationToken = default)
    {
        Published.Add(install);
        if (OnPublish is not null) await OnPublish(install);
        return LocalAiEndpointLifecycleResult.Ok();
    }

    public void Dispose() { }

    private sealed class Process(DateTimeOffset startedAtUtc) : ILocalAiManagedProcess
    {
        public int ProcessId => 4201;
        public DateTimeOffset StartedAtUtc => startedAtUtc;
        public bool HasExited { get; private set; }
        public Task StopAsync(TimeSpan timeout, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); HasExited = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { HasExited = true; return ValueTask.CompletedTask; }
    }
}
