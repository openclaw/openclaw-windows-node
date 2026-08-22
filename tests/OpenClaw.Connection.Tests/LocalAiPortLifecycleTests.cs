using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;
using System.Collections.Immutable;
using System.Net;

namespace OpenClaw.Connection.Tests;

public sealed class LocalAiPortLifecycleTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(80, false)]
    [InlineData(65_535, true)]
    [InlineData(65_536, false)]
    public void PortPolicy_IsConsistent(int port, bool accepted)
    {
        Assert.Equal(accepted, LocalAiPortPolicy.TryValidate(port, out _));
    }

    [Fact]
    public async Task Manifest_RoundTripsValidatedGatewayFallbackModel()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));

        await store.SaveAsync(ValidManifest() with { GatewayFallbackModel = "openai/gpt-5" });

        LocalAiResolvedInstall saved = (await store.LoadAsync())!;
        Assert.Equal("openai/gpt-5", saved.Manifest.GatewayFallbackModel);
    }

    [Fact]
    public async Task Manifest_AcceptsAndIgnoresLegacyHardwareProfileId()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        await store.SaveAsync(ValidManifest() with { HardwareProfileId = "retired-profile-id" });

        LocalAiResolvedInstall saved = (await store.LoadAsync())!;
        LlamaServerRouterLaunchPlan launch = LlamaServerRouterConfiguration.Build(paths, saved);

        Assert.Equal("retired-profile-id", saved.Manifest.HardwareProfileId);
        Assert.Equal(LocalModelCatalog.Qwen35BModelId, launch.ModelAlias);
    }

    [Fact]
    public async Task Manifest_OmitsLegacyHardwareProfileIdFromNewWrites()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var paths = new LocalAiPaths(temp.Path);
        await new LocalAiManifestStore(paths).SaveAsync(ValidManifest());

        string json = await File.ReadAllTextAsync(paths.ManifestPath);

        Assert.DoesNotContain("hardwareProfileId", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Router_RejectsRuntimeArchitectureMismatchWithoutHardwareProfile()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        await store.SaveAsync(ValidManifest() with { Architecture = "x64" });
        LocalAiResolvedInstall saved = (await store.LoadAsync())!;

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => LlamaServerRouterConfiguration.Build(paths, saved));

        Assert.Contains("architecture and runtime", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("llamacpp/other-model")]
    [InlineData("missing-provider-separator")]
    [InlineData("provider/model/extra")]
    public async Task Manifest_RejectsUnsafeGatewayFallbackModel(string fallbackModel)
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(ValidManifest() with { GatewayFallbackModel = fallbackModel }));
    }

    [Fact]
    public async Task AutomaticPort_IsBoundByChildAndPersistedOnlyAfterOwnedHealth()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new List<string>();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_765);
        var client = new FakeClient(events);
        var lifecycle = new FakeLifecycle(events);
        await using var runtime = CreateRuntime(paths, host, platform, client, lifecycle);

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Healthy, snapshot.State);
        Assert.Equal(28_765, snapshot.Endpoint.Port);
        Assert.Equal("0", ArgumentAfter(host.LastSpec!.Arguments, "--port"));
        Assert.Equal(["quiesce", "start", "probe:28765", "publish:28765"], events);
        Assert.Equal([28_765], client.ProbedPorts);
        LocalAiResolvedInstall? saved = await new LocalAiManifestStore(paths).LoadAsync();
        Assert.Equal(0, saved!.Manifest.RequestedPort);
        Assert.Equal(28_765, saved.Endpoint!.Port);
    }

    [Fact]
    public async Task AutomaticPort_NeverProbesListenerWithoutMatchingProcessStartTime()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var platform = new FakePlatform();
        var host = new FakeProcessHost(
            platform,
            [],
            selectedPort: 28_766,
            listenerStartOffset: TimeSpan.FromMinutes(-1));
        var client = new FakeClient([]);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            client,
            new FakeLifecycle([]));

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Conflict, snapshot.State);
        Assert.Empty(client.ProbedPorts);
        Assert.True(host.Process!.StopCount > 0);
        LocalAiResolvedInstall? saved = await new LocalAiManifestStore(paths).LoadAsync();
        Assert.Null(saved!.Endpoint);
    }

    [Fact]
    public async Task FixedPortConflict_QuiescesEndpointConsumerBeforeReturning()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        const int fixedPort = 28_770;
        var store = new LocalAiManifestStore(paths);
        LocalAiResolvedInstall install = (await store.LoadAsync())!;
        await store.SaveAsync(install.Manifest with
        {
            RequestedPort = fixedPort,
            Endpoint = $"http://127.0.0.1:{fixedPort}/v1",
        });
        var events = new List<string>();
        var platform = new FakePlatform();
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback,
            fixedPort,
            9001,
            "other-process",
            @"C:\other\server.exe",
            platform.UtcNow.UtcDateTime));
        var host = new FakeProcessHost(platform, events, selectedPort: fixedPort);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            new FakeLifecycle(events));

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Conflict, snapshot.State);
        Assert.Equal(["quiesce"], events);
        Assert.Null(host.LastSpec);
    }

    [Fact]
    public async Task PreparationFailure_QuiescesEndpointConsumerBeforeReturning()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        LocalAiResolvedInstall install = (await new LocalAiManifestStore(paths).LoadAsync())!;
        File.Delete(install.ExecutablePath);
        var events = new List<string>();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_772);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            new FakeLifecycle(events));

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, snapshot.State);
        Assert.Equal(["quiesce"], events);
        Assert.Null(host.LastSpec);
    }

    [Fact]
    public async Task AutomaticPort_RejectsWildcardChildListenerWithoutProbing()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new List<string>();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(
            platform,
            events,
            selectedPort: 28_771,
            listenerAddress: IPAddress.Any);
        var client = new FakeClient(events);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            client,
            new FakeLifecycle(events));

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Conflict, snapshot.State);
        Assert.Empty(client.ProbedPorts);
        Assert.Equal(["quiesce", "start", "stop"], events);
        LocalAiResolvedInstall? saved = await new LocalAiManifestStore(paths).LoadAsync();
        Assert.Null(saved!.Endpoint);
    }

    [Fact]
    public async Task Stop_QuiescesEndpointConsumerBeforeListenerDisappears()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new List<string>();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_769);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            new FakeLifecycle(events));
        await runtime.EnsureStartedAsync();
        events.Clear();

        LocalAiRuntimeSnapshot stopped = await runtime.StopAsync();

        Assert.Equal(LocalAiRuntimeState.Stopped, stopped.State);
        Assert.Equal(["quiesce", "stop"], events);
    }

    [Fact]
    public async Task PublishFailure_StopsChildAndLeavesEndpointConsumerQuiesced()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new List<string>();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_767);
        var lifecycle = new FakeLifecycle(events) { FailPublish = true };
        await using var runtime = CreateRuntime(paths, host, platform, new FakeClient(events), lifecycle);

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, snapshot.State);
        Assert.Equal(1, host.Process!.StopCount);
        Assert.Equal(["quiesce", "start", "probe:28767", "publish:28767", "stop"], events);

        // The endpoint receipt is already durable but the provider is still absent.
        // A later tray start must safely allocate again and complete publication.
        await runtime.DisposeAsync();
        var retryPlatform = new FakePlatform();
        var retryHost = new FakeProcessHost(retryPlatform, [], selectedPort: 28_768);
        await using var retry = CreateRuntime(
            paths,
            retryHost,
            retryPlatform,
            new FakeClient([]),
            new FakeLifecycle([]));
        LocalAiRuntimeSnapshot recovered = await retry.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Healthy, recovered.State);
        Assert.Equal(28_768, recovered.Endpoint.Port);
    }

    private static LlamaServerRuntimeService CreateRuntime(
        LocalAiPaths paths,
        FakeProcessHost host,
        FakePlatform platform,
        FakeClient client,
        ILocalAiEndpointLifecycle lifecycle) => new(
            new LlamaServerRuntimeOptions
            {
                Paths = paths,
                EndpointLifecycle = lifecycle,
                HealthPollInterval = TimeSpan.FromMilliseconds(1),
                StartupTimeout = TimeSpan.FromSeconds(1),
                RestartDelay = TimeSpan.Zero,
            },
            NullLogger.Instance,
            host,
            platform,
            client);

    private static async Task<LocalAiPaths> PrepareInstallAsync(TempDirectory temp)
    {
        var paths = new LocalAiPaths(temp.Path);
        LocalAiInstallManifest manifest = ValidManifest();
        string executable = paths.ResolveContainedPath(manifest.ExecutablePath, nameof(manifest.ExecutablePath));
        string model = paths.ResolveContainedPath(manifest.ModelPath, nameof(manifest.ModelPath));
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        await File.WriteAllTextAsync(executable, "test executable");
        await using (var stream = new FileStream(model, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(manifest.ModelAsset.SizeBytes);
        await new LocalAiManifestStore(paths).SaveAsync(manifest);
        return paths;
    }

    private static string ArgumentAfter(IReadOnlyList<string> arguments, string name)
    {
        int index = Array.IndexOf(arguments.ToArray(), name);
        Assert.InRange(index, 0, arguments.Count - 2);
        return arguments[index + 1];
    }

    private static LocalAiInstallManifest ValidManifest() => new()
    {
        EngineVersion = "b10488",
        Architecture = "arm64",
        RuntimeId = "b10488-cuda13-arm64",
        ModelCatalogId = "qwen3.6-35b-a3b-mtp-q4-k-m",
        SelectedGpuId = "GPU-01234567-89ab-cdef-0123-456789abcdef",
        ExecutablePath = Path.Combine("engines", "llama-b10488", "llama-server.exe"),
        RuntimeAssets =
        [
            new LocalAiAssetReceipt
            {
                FileName = "llama-b10488-bin-win-cuda-13.4-arm64.zip",
                SourceUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b10488/llama-b10488-bin-win-cuda-13.4-arm64.zip",
                SizeBytes = 140_379_054,
                Sha256 = "75554d62f4af8f4150d3b4b0cca7df62d44105e98fb7cd92ab2d177e382b441d",
            },
            new LocalAiAssetReceipt
            {
                FileName = "cudart-llama-bin-win-cuda-13.4-arm64.zip",
                SourceUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b10488/cudart-llama-bin-win-cuda-13.4-arm64.zip",
                SizeBytes = 153_318_797,
                Sha256 = "5a40dc7c5fa3d0a80ceeba4f16f9e8d25d87bcf1399c9233588953c43436c33c",
            },
        ],
        ModelPath = Path.Combine("models", "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf"),
        ModelId = "unsloth/Qwen3.6-35B-A3B-MTP-GGUF@5bc3e238d916f48a861bac2f8a1990a0e9b7e98d",
        ModelAlias = "qwen3.6-35b-a3b-mtp-q4-k-m",
        ModelAsset = new LocalAiAssetReceipt
        {
            FileName = "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf",
            SourceUrl = "https://huggingface.co/unsloth/Qwen3.6-35B-A3B-MTP-GGUF/resolve/5bc3e238d916f48a861bac2f8a1990a0e9b7e98d/Qwen3.6-35B-A3B-UD-Q4_K_M.gguf?download=true",
            SizeBytes = 22_663_387_424,
            Sha256 = "0b21525e972670ed59e1812e170b27c26355381f0656ecc4e25617ece7dac58b",
        },
        RequestedPort = 0,
        Endpoint = null,
        ContextLength = 262_144,
        InstalledAtUtc = DateTimeOffset.Parse("2026-08-18T12:00:00Z"),
    };

    private sealed class FakePlatform : ILlamaServerRuntimePlatform
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.Parse("2026-08-18T12:00:00Z");
        public List<WindowsTcpListenerInfo> Listeners { get; } = [];

        public WindowsTcpListenerSnapshotResult CaptureListeners() =>
            new([.. Listeners], Ipv4Complete: true, Ipv6Complete: true);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UtcNow += delay;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProcessHost(
        FakePlatform platform,
        List<string> events,
        int selectedPort,
        TimeSpan? listenerStartOffset = null,
        IPAddress? listenerAddress = null) : ILocalAiManagedProcessHost
    {
        public LocalAiProcessStartSpec? LastSpec { get; private set; }
        public FakeProcess? Process { get; private set; }

        public Task<ILocalAiManagedProcess> StartProcessAsync(
            LocalAiProcessStartSpec spec,
            Action<LocalAiManagedProcessExit> exited,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("start");
            LastSpec = spec;
            Process = new FakeProcess(4201, platform.UtcNow, platform, events);
            platform.Listeners.Add(new WindowsTcpListenerInfo(
                listenerAddress ?? IPAddress.Loopback,
                selectedPort,
                Process.ProcessId,
                "llama-server",
                @"C:\managed\llama-server.exe",
                (Process.StartedAtUtc + (listenerStartOffset ?? TimeSpan.Zero)).UtcDateTime));
            return Task.FromResult<ILocalAiManagedProcess>(Process);
        }
    }

    private sealed class FakeProcess(
        int processId,
        DateTimeOffset startedAtUtc,
        FakePlatform platform,
        List<string> events) : ILocalAiManagedProcess
    {
        public int ProcessId { get; } = processId;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        public bool HasExited { get; private set; }
        public int StopCount { get; private set; }

        public Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("stop");
            StopCount++;
            HasExited = true;
            platform.Listeners.Clear();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeClient(List<string> events) : ILlamaServerClient
    {
        public List<int> ProbedPorts { get; } = [];

        public Task<LlamaServerRouterProbeResult> ProbeRouterAsync(
            Uri endpoint,
            string modelAlias,
            string expectedModelPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbedPorts.Add(endpoint.Port);
            events.Add($"probe:{endpoint.Port}");
            return Task.FromResult(new LlamaServerRouterProbeResult(
                true,
                LocalAiModelAvailabilityState.Verified,
                expectedModelPath,
                null));
        }

        public void Dispose() { }
    }

    private sealed class FakeLifecycle(List<string> events) : ILocalAiEndpointLifecycle
    {
        public bool FailPublish { get; init; }

        public Task<LocalAiEndpointLifecycleResult> QuiesceAsync(
            LocalAiResolvedInstall install,
            CancellationToken cancellationToken = default)
        {
            events.Add("quiesce");
            return Task.FromResult(LocalAiEndpointLifecycleResult.Ok());
        }

        public Task<LocalAiEndpointLifecycleResult> PublishAsync(
            LocalAiResolvedInstall install,
            CancellationToken cancellationToken = default)
        {
            events.Add($"publish:{install.Endpoint!.Port}");
            return Task.FromResult(FailPublish
                ? LocalAiEndpointLifecycleResult.Failed("publish failed")
                : LocalAiEndpointLifecycleResult.Ok());
        }
    }
}
