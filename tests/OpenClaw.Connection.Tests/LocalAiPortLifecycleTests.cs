using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.Connection.Tests;

public sealed class LocalAiPortLifecycleTests
{
    [Fact]
    public async Task ChildProcessPathResolver_ResolvesExistingFileAndDirectoryPaths()
    {
        using var temp = new TempDirectory("local-ai-child-path-resolver-");
        string file = temp.Combine("runtime", "llama-server.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "launcher");

        string resolvedDirectory = LocalAiChildProcessPathResolver.Resolve(Path.GetDirectoryName(file)!);
        string resolvedFile = LocalAiChildProcessPathResolver.Resolve(file);

        Assert.True(Path.IsPathFullyQualified(resolvedDirectory));
        Assert.True(Path.IsPathFullyQualified(resolvedFile));
        Assert.True(Directory.Exists(resolvedDirectory));
        Assert.True(File.Exists(resolvedFile));
    }

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
    public void LegacyRouterProbe_RemainsSourceCompatible()
    {
        using var client = new LlamaServerClient();
#pragma warning disable CS0618
        Func<Uri, string, string, CancellationToken, Task<LlamaServerRouterProbeResult>> legacyProbe =
            client.ProbeRouterAsync;
#pragma warning restore CS0618

        Assert.NotNull(legacyProbe);
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
        JsonObject legacyJson = (JsonNode.Parse(await File.ReadAllTextAsync(paths.ManifestPath)) as JsonObject)!;
        legacyJson.Remove("keyCachePrecision");
        legacyJson.Remove("valueCachePrecision");
        legacyJson.Remove("draftKeyCachePrecision");
        legacyJson.Remove("draftValueCachePrecision");
        await File.WriteAllTextAsync(paths.ManifestPath, legacyJson.ToJsonString());

        LocalAiResolvedInstall saved = (await store.LoadAsync())!;
        LlamaServerRouterLaunchPlan launch = LlamaServerRouterConfiguration.Build(paths, saved);

        Assert.Equal("retired-profile-id", saved.Manifest.HardwareProfileId);
        Assert.Equal(KvCachePrecision.F16, saved.Manifest.KeyCachePrecision);
        Assert.Contains("cache-type-k = f16", launch.PresetContent);
        Assert.Equal(LocalModelCatalog.Qwen35BModelId, launch.ModelAlias);
    }

    [Fact]
    public async Task RouterPreset_BoundsOmittedGenerationAtGatewayMaximum()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        await store.SaveAsync(ValidManifest() with { Endpoint = "http://127.0.0.1:28765/v1" });
        LocalAiResolvedInstall saved = (await store.LoadAsync())!;

        LlamaServerRouterLaunchPlan launch = LlamaServerRouterConfiguration.Build(paths, saved);
        using JsonDocument provider = JsonDocument.Parse(
            LocalAiGatewayProviderDefinition.BuildProviderJson(saved));
        int gatewayMaximum = provider.RootElement
            .GetProperty("models")[0]
            .GetProperty("maxTokens")
            .GetInt32();

        Assert.Equal(LocalAiGatewayProviderDefinition.MaximumOutputTokens, gatewayMaximum);
        Assert.Contains(
            $"n-predict = {gatewayMaximum}",
            launch.PresetContent.Split(Environment.NewLine));
        Assert.Contains("ctx-size = 262144", launch.PresetContent.Split(Environment.NewLine));
        Assert.Contains("cache-type-k = q8_0", launch.PresetContent.Split(Environment.NewLine));
        Assert.Contains("cache-type-v = q8_0", launch.PresetContent.Split(Environment.NewLine));
        Assert.Contains("cache-type-k-draft = q8_0", launch.PresetContent.Split(Environment.NewLine));
        Assert.Contains("cache-type-v-draft = q8_0", launch.PresetContent.Split(Environment.NewLine));
    }

    [Fact]
    public async Task Manifest_OmitsLegacyHardwareProfileIdFromNewWrites()
    {
        using var temp = new TempDirectory("local-ai-manifest-");
        var paths = new LocalAiPaths(temp.Path);
        await new LocalAiManifestStore(paths).SaveAsync(ValidManifest());

        string json = await File.ReadAllTextAsync(paths.ManifestPath);

        Assert.DoesNotContain("hardwareProfileId", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"keyCachePrecision\": \"q8_0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"draftValueCachePrecision\": \"q8_0\"", json, StringComparison.Ordinal);
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
    public async Task Runtime_RejectsSchemaFourCacheContentBeforeProcessStart()
    {
        using var temp = new TempDirectory("local-ai-cache-runtime-");
        var paths = new LocalAiPaths(temp.Path);
        byte[] expected = "expected-cache-model"u8.ToArray();
        byte[] tampered = "tampered-cache-model"u8.ToArray();
        Assert.Equal(expected.Length, tampered.Length);
        LocalAiInstallManifest manifest = ValidManifest();
        string cacheRoot = temp.Combine("hf-cache");
        const string repositoryId = "unsloth/Qwen3.6-35B-A3B-MTP-GGUF";
        const string revision = "5bc3e238d916f48a861bac2f8a1990a0e9b7e98d";
        Assert.True(HuggingFaceHubCache.TryGetSnapshotPaths(
            cacheRoot,
            repositoryId,
            revision,
            "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf",
            out string cachedModelPath,
            out _,
            out string error), error);
        manifest = manifest with
        {
            SchemaVersion = LocalAiInstallManifest.HubCacheReceiptSchemaVersion,
            ModelCacheRoot = cacheRoot,
            CachedModelPath = cachedModelPath,
            ModelAsset = manifest.ModelAsset with
            {
                SizeBytes = expected.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(),
            },
        };
        string executable = paths.ResolveContainedPath(
            manifest.ExecutablePath,
            nameof(manifest.ExecutablePath));
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        Directory.CreateDirectory(Path.GetDirectoryName(cachedModelPath)!);
        await File.WriteAllTextAsync(executable, "test executable");
        await File.WriteAllTextAsync(
            Path.Combine(
                Path.GetDirectoryName(executable)!,
                LlamaRuntimeCatalog.ServerImplementationLibraryName),
            "test implementation library");
        await File.WriteAllBytesAsync(cachedModelPath, tampered);
        await new LocalAiManifestStore(paths).SaveAsync(manifest);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_765);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            new FakeLifecycle(events));

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, snapshot.State);
        Assert.Contains("no longer matches", snapshot.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("start", events);
        Assert.Null(host.LastSpec);
    }

    [ConnectionSymbolicLinkFact]
    public async Task Runtime_BindsNativeModelPathToVerifiedBlobAfterSnapshotLinkReplacement()
    {
        using var temp = new TempDirectory("local-ai-cache-runtime-link-");
        using var outside = new TempDirectory("local-ai-cache-runtime-outside-");
        var paths = new LocalAiPaths(temp.Path);
        byte[] content = "verified-cache-model"u8.ToArray();
        LocalAiInstallManifest manifest = ValidManifest();
        string cacheRoot = temp.Combine("hf-cache");
        const string repositoryId = "unsloth/Qwen3.6-35B-A3B-MTP-GGUF";
        const string revision = "5bc3e238d916f48a861bac2f8a1990a0e9b7e98d";
        Assert.True(HuggingFaceHubCache.TryGetSnapshotPaths(
            cacheRoot,
            repositoryId,
            revision,
            "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf",
            out string cachedModelPath,
            out _,
            out string error), error);
        Assert.True(HuggingFaceHubCache.TryGetBlobPath(
            cacheRoot,
            repositoryId,
            new Sha256Digest(manifest.ModelAsset.Sha256),
            out string blobPath,
            out error), error);
        manifest = manifest with
        {
            SchemaVersion = LocalAiInstallManifest.HubCacheReceiptSchemaVersion,
            ModelCacheRoot = cacheRoot,
            CachedModelPath = cachedModelPath,
        };

        string executable = paths.ResolveContainedPath(
            manifest.ExecutablePath,
            nameof(manifest.ExecutablePath));
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        Directory.CreateDirectory(Path.GetDirectoryName(cachedModelPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
        await File.WriteAllTextAsync(executable, "test executable");
        await File.WriteAllTextAsync(
            Path.Combine(
                Path.GetDirectoryName(executable)!,
                LlamaRuntimeCatalog.ServerImplementationLibraryName),
            "test implementation library");
        await File.WriteAllBytesAsync(blobPath, content);
        File.CreateSymbolicLink(
            cachedModelPath,
            Path.GetRelativePath(Path.GetDirectoryName(cachedModelPath)!, blobPath));
        await new LocalAiManifestStore(paths).SaveAsync(manifest);

        string outsidePath = outside.Combine("replacement.gguf");
        await File.WriteAllBytesAsync(outsidePath, content);
        string? probedModelPath = null;
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_765)
        {
            BeforeStart = _ =>
            {
                string preset = File.ReadAllText(paths.RouterPresetPath);
                Assert.Contains($"model = {blobPath}", preset, StringComparison.OrdinalIgnoreCase);
                File.Delete(cachedModelPath);
                File.CreateSymbolicLink(cachedModelPath, outsidePath);
            },
        };
        var client = new FakeClient(events, (expectedModelPath, _) =>
        {
            probedModelPath = expectedModelPath;
            return ReadyProbe(expectedModelPath);
        });
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            client,
            new FakeLifecycle(events),
            modelFileVerifier: new FakeModelFileVerifier(blobPath));

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.True(
            snapshot.State == LocalAiRuntimeState.Healthy,
            $"Expected a healthy runtime but got {snapshot.State}: {snapshot.Detail}");
        Assert.Equal(blobPath, probedModelPath, ignoreCase: true);
        Assert.Contains("start", events);
        Assert.NotNull(host.LastSpec);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("local-api-test-credential")]
    public async Task AutomaticPort_IsBoundByChildAndPersistedOnlyAfterOwnedHealth(string? apiKey)
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_765);
        var client = new FakeClient(events);
        var lifecycle = new FakeLifecycle(events);
        await using var runtime = CreateRuntime(paths, host, platform, client, lifecycle,
            getApiKey: apiKey is null ? null : () => apiKey);

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Healthy, snapshot.State);
        Assert.Equal(28_765, snapshot.Endpoint.Port);
        Assert.Equal("0", ArgumentAfter(host.LastSpec!.Arguments, "--port"));
        if (apiKey is not null)
        {
            Assert.Equal(apiKey, host.LastSpec.Environment["LLAMA_API_KEY"]);
            Assert.DoesNotContain(host.LastSpec.Arguments, argument => argument.Contains(apiKey, StringComparison.Ordinal));
            Assert.DoesNotContain(apiKey, await File.ReadAllTextAsync(paths.RouterPresetPath));
            Assert.DoesNotContain(apiKey, await File.ReadAllTextAsync(paths.ManifestPath));
        }
        Assert.Equal(["quiesce:EndpointCycle", "start", "probe:28765", "publish:28765"], events);
        Assert.Equal([28_765], client.ProbedPorts);
        LocalAiResolvedInstall? saved = await new LocalAiManifestStore(paths).LoadAsync();
        Assert.Equal(0, saved!.Manifest.RequestedPort);
        Assert.Equal(28_765, saved.Endpoint!.Port);
    }

    [Fact]
    public async Task Start_UsesChildVisiblePhysicalExecutableWorkingDirectoryAndPresetPaths()
    {
        using var temp = new TempDirectory("local-ai-child-paths-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        string physicalRoot = temp.Combine("physical-local-ai");
        string ResolveForChild(string path) => Path.Combine(
            physicalRoot,
            Path.GetRelativePath(paths.RootDirectory, path));
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_765);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            new FakeLifecycle(events),
            resolveChildProcessPath: ResolveForChild);

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Healthy, snapshot.State);
        LocalAiResolvedInstall install = (await new LocalAiManifestStore(paths).LoadAsync())!;
        Assert.Equal(ResolveForChild(install.ExecutablePath), host.LastSpec!.ExecutablePath);
        Assert.Equal(ResolveForChild(Path.GetDirectoryName(install.ExecutablePath)!), host.LastSpec.WorkingDirectory);
        Assert.Equal(ResolveForChild(paths.RouterPresetPath), ArgumentAfter(host.LastSpec.Arguments, "--models-preset"));
        Assert.Contains(
            $"model = {ResolveForChild(install.ModelPath)}",
            await File.ReadAllTextAsync(paths.RouterPresetPath),
            StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(paths.RouterPresetPath));
    }

    [Fact]
    public async Task RecoveryPort_ReusesVerifiedEndpointWithoutChangingAutomaticPortPreference()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var store = new LocalAiManifestStore(paths);
        var install = (await store.LoadAsync())!;
        await store.SaveAsync(install.Manifest with { Endpoint = "http://127.0.0.1:28765/v1" });
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_765);
        await using var runtime = CreateRuntime(paths, host, platform,
            new FakeClient(events), new FakeLifecycle(events),
            getRecoveryPort: current => current.Endpoint?.Port);

        var snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Healthy, snapshot.State);
        Assert.Equal("28765", ArgumentAfter(host.LastSpec!.Arguments, "--port"));
        Assert.Equal(0, (await store.LoadAsync())!.Manifest.RequestedPort);
        Assert.Equal(28_765, snapshot.Endpoint.Port);
    }

    [Fact]
    public async Task Startup_WithdrawsProviderBeforeBindingFreshAutomaticPort()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var store = new LocalAiManifestStore(paths);
        LocalAiResolvedInstall install = (await store.LoadAsync())!;
        await store.SaveAsync(install.Manifest with { Endpoint = "http://127.0.0.1:28765/v1" });

        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_766);
        var client = new FakeClient(events);
        await using var runtime = CreateRuntime(paths, host, platform, client, new FakeLifecycle(events));

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Healthy, snapshot.State);
        Assert.Equal(28_766, snapshot.Endpoint.Port);
        Assert.Equal("0", ArgumentAfter(host.LastSpec!.Arguments, "--port"));
        Assert.Equal(
            ["quiesce:EndpointCycle", "start", "probe:28766", "publish:28766"],
            events);
    }

    [Fact]
    public async Task Refresh_TrustConflictExceptionRetriesEndpointCycleBeforeStopping()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) => call == 2
                ? Task.FromException<LocalAiEndpointLifecycleResult>(
                    new IOException("terminal withdrawal interrupted"))
                : Task.FromResult(LocalAiEndpointLifecycleResult.Ok()),
        };
        var host = new FakeProcessHost(platform, events, selectedPort: 28_786);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        events.Clear();
        platform.Ipv4Complete = false;

        IOException error = await Assert.ThrowsAsync<IOException>(() => runtime.RefreshAsync());

        Assert.Equal("terminal withdrawal interrupted", error.Message);
        Assert.Equal(LocalAiRuntimeState.Failed, runtime.Snapshot.State);
        Assert.Equal(LocalAiOwnership.None, runtime.Snapshot.Ownership);
        Assert.True(host.Process!.HasExited);
        Assert.Equal(["quiesce:EndpointCycle", "quiesce:EndpointCycle", "stop"], events);
    }

    [Fact]
    public async Task Refresh_EndpointChangePublishExceptionFailsAndWithdrawsNewEndpoint()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events);
        var host = new FakeProcessHost(platform, events, selectedPort: 28_787);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        events.Clear();
        platform.Listeners[0] = platform.Listeners[0] with { Port = 28_788 };
        lifecycle.PublishException = new IOException("republish wrote then failed");

        IOException error = await Assert.ThrowsAsync<IOException>(() => runtime.RefreshAsync());

        Assert.Equal("republish wrote then failed", error.Message);
        Assert.Equal(LocalAiRuntimeState.Failed, runtime.Snapshot.State);
        Assert.Equal(LocalAiOwnership.None, runtime.Snapshot.Ownership);
        Assert.True(host.Process!.HasExited);
        Assert.Equal(new Uri("http://127.0.0.1:28788/v1"), lifecycle.QuiescedEndpoints[^1]);
        Assert.Equal(
            ["probe:28788", "quiesce:EndpointCycle", "publish:28788", "quiesce:Teardown", "stop"],
            events);
    }

    [Fact]
    public async Task Refresh_EndpointChangePublishCancellationPreservesManagedProcess()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        using var cancellation = new CancellationTokenSource();
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events);
        var host = new FakeProcessHost(platform, events, selectedPort: 28_789);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        events.Clear();
        platform.Listeners[0] = platform.Listeners[0] with { Port = 28_790 };
        bool canceled = false;
        lifecycle.PublishHandler = (_, token) =>
        {
            if (!canceled)
            {
                canceled = true;
                cancellation.Cancel();
                return Task.FromCanceled<LocalAiEndpointLifecycleResult>(cancellation.Token);
            }
            return Task.FromResult(LocalAiEndpointLifecycleResult.Ok());
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.RefreshAsync(cancellation.Token));

        Assert.False(host.Process!.HasExited);
        Assert.Equal(LocalAiRuntimeState.Healthy, runtime.Snapshot.State);
        Assert.Equal(
            ["probe:28790", "quiesce:EndpointCycle", "publish:28790", "publish:28790"],
            events);
        Assert.DoesNotContain("quiesce:Teardown", events);
    }

    [Fact]
    public async Task Refresh_CanceledManifestRebindTimesOutAndCompletesTeardown()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        using var cancellation = new CancellationTokenSource();
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) =>
            {
                if (call == 2)
                    cancellation.Cancel();
                return Task.FromResult(LocalAiEndpointLifecycleResult.Ok());
            },
        };
        var host = new FakeProcessHost(platform, events, selectedPort: 28_791);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle,
            shutdownTimeout: TimeSpan.FromMilliseconds(20));
        Assert.Equal(LocalAiRuntimeState.Healthy, (await runtime.EnsureStartedAsync()).State);
        platform.Listeners[0] = platform.Listeners[0] with { Port = 28_792 };
        events.Clear();

        string lockPath = Path.Combine(
            paths.RootDirectory,
            $".{Path.GetFileName(paths.ManifestPath)}.lock");
        await using var writeLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.RefreshAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.True(host.Process!.HasExited);
        Assert.Equal(LocalAiRuntimeState.Failed, runtime.Snapshot.State);
        Assert.Equal(LocalAiOwnership.None, runtime.Snapshot.Ownership);
        Assert.Contains("could not restore gateway routing", runtime.Snapshot.Detail, StringComparison.Ordinal);
        Assert.Equal(
            ["probe:28792", "quiesce:EndpointCycle", "quiesce:Teardown", "stop"],
            events);
    }

    [Fact]
    public async Task Refresh_QuiesceExceptionStopsManagedProcessAfterFallbackTeardown()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) => call == 2
                ? Task.FromException<LocalAiEndpointLifecycleResult>(
                    new IOException("endpoint-cycle withdrawal failed"))
                : Task.FromResult(LocalAiEndpointLifecycleResult.Ok()),
        };
        var host = new FakeProcessHost(platform, events, selectedPort: 28_784);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        events.Clear();
        platform.Listeners.Clear();

        IOException error = await Assert.ThrowsAsync<IOException>(() => runtime.RefreshAsync());

        Assert.Equal("endpoint-cycle withdrawal failed", error.Message);
        Assert.Equal(LocalAiRuntimeState.Failed, runtime.Snapshot.State);
        Assert.Equal(LocalAiOwnership.None, runtime.Snapshot.Ownership);
        Assert.True(host.Process!.HasExited);
        Assert.Equal(
            ["quiesce:EndpointCycle", "quiesce:Teardown", "stop"],
            events);
    }

    [Fact]
    public async Task Restart_WithdrawsStaleRouteWhenTheLastVerifiedPortIsTaken()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var store = new LocalAiManifestStore(paths);
        LocalAiResolvedInstall install = (await store.LoadAsync())!;
        await store.SaveAsync(install.Manifest with { Endpoint = "http://127.0.0.1:28765/v1" });

        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback,
            28_765,
            9001,
            "other-process",
            @"C:\other\server.exe",
            platform.UtcNow.UtcDateTime));
        var host = new FakeProcessHost(platform, events, selectedPort: 28_771);
        var client = new FakeClient(events);
        await using var runtime = CreateRuntime(paths, host, platform, client, new FakeLifecycle(events));

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Healthy, snapshot.State);
        Assert.Equal("0", ArgumentAfter(host.LastSpec!.Arguments, "--port"));
        Assert.Equal(
            ["quiesce:EndpointCycle", "start", "probe:28771", "publish:28771"],
            events);
    }

    [Fact]
    public async Task Startup_HungProbeEndsAtTheStartupTimeout()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var store = new LocalAiManifestStore(paths);
        LocalAiResolvedInstall install = (await store.LoadAsync())!;
        await store.SaveAsync(install.Manifest with { Endpoint = "http://127.0.0.1:28765/v1" });

        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback,
            28_765,
            9001,
            "other-process",
            @"C:\other\server.exe",
            platform.UtcNow.UtcDateTime));
        var host = new FakeProcessHost(platform, events, selectedPort: 28_771);
        var client = new FakeClient(events) { WaitForCancellation = true };
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            client,
            new FakeLifecycle(events),
            startupTimeout: TimeSpan.FromMilliseconds(200));
        var started = System.Diagnostics.Stopwatch.StartNew();

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, snapshot.State);
        Assert.Contains("startup timeout", snapshot.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Startup_RealStalledLoopbackBodyEndsAtTheBudget()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var headersSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stalled = new CancellationTokenSource();
        Task accept = AcceptAndHoldBodyAsync(listener, stalled.Token, headersSent);
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: port);
        using var http = new LlamaServerClient(
            new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(2),
            },
            timeout: TimeSpan.FromSeconds(30));
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            http,
            new FakeLifecycle(events),
            startupTimeout: TimeSpan.FromSeconds(1));
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();
            Console.WriteLine(
                "real-transport startup state={0} elapsed_ms={1} detail={2} port={3}",
                snapshot.State,
                started.ElapsedMilliseconds,
                snapshot.Detail,
                port);
            Assert.Equal(LocalAiRuntimeState.Failed, snapshot.State);
            Assert.Contains("startup timeout", snapshot.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.True(headersSent.Task.IsCompletedSuccessfully);
            Assert.False(accept.IsCompleted);
            Assert.True(host.Process!.HasExited);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(8));
        }
        finally
        {
            stalled.Cancel();
            listener.Stop();
            try
            {
                await accept.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task Dispose_RealStalledLoopbackBodyDoesNotWaitOutTheBudget()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var headersSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stalled = new CancellationTokenSource();
        Task accept = AcceptAndHoldBodyAsync(listener, stalled.Token, headersSent);
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: port);
        using var http = new LlamaServerClient(
            new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(2),
            },
            timeout: TimeSpan.FromSeconds(30));
        var runtime = CreateRuntime(
            paths,
            host,
            platform,
            http,
            new FakeLifecycle(events),
            startupTimeout: TimeSpan.FromSeconds(30));
        Task<LocalAiRuntimeSnapshot> starting = runtime.EnsureStartedAsync();
        await headersSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposeStarted = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await runtime.DisposeAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
            Console.WriteLine(
                "real-transport dispose elapsed_ms={0} port={1}",
                disposeStarted.ElapsedMilliseconds,
                port);
            Assert.True(disposeStarted.Elapsed < TimeSpan.FromSeconds(5));
        }
        finally
        {
            stalled.Cancel();
            listener.Stop();
        }
    }

    private static async Task AcceptAndHoldBodyAsync(
        TcpListener listener,
        CancellationToken cancellationToken,
        TaskCompletionSource? headersSent = null)
    {
        try
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            await using NetworkStream stream = client.GetStream();
            byte[] headers = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n"u8.ToArray();
            await stream.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            headersSent?.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            headersSent?.TrySetCanceled(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            headersSent?.TrySetCanceled();
        }
    }

    [Fact]
    public async Task Dispose_DuringHungStartupDoesNotWaitForTheProbe()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var store = new LocalAiManifestStore(paths);
        LocalAiResolvedInstall install = (await store.LoadAsync())!;
        await store.SaveAsync(install.Manifest with { Endpoint = "http://127.0.0.1:28765/v1" });

        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback,
            28_765,
            9001,
            "other-process",
            @"C:\other\server.exe",
            platform.UtcNow.UtcDateTime));
        var host = new FakeProcessHost(platform, events, selectedPort: 28_771);
        var client = new FakeClient(events) { WaitForCancellation = true };
        var runtime = CreateRuntime(
            paths,
            host,
            platform,
            client,
            new FakeLifecycle(events),
            startupTimeout: TimeSpan.FromSeconds(30));
        Task<LocalAiRuntimeSnapshot> starting = runtime.EnsureStartedAsync();
        await client.ProbeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(starting.IsCompleted);

        var disposeStarted = System.Diagnostics.Stopwatch.StartNew();
        await runtime.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);

        Assert.True(disposeStarted.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Restart_PortRelocationFailureRestoresTerminalRouting()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var store = new LocalAiManifestStore(paths);
        LocalAiResolvedInstall install = (await store.LoadAsync())!;
        await store.SaveAsync(install.Manifest with { Endpoint = "http://127.0.0.1:28765/v1" });

        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback,
            28_765,
            9001,
            "other-process",
            @"C:\other\server.exe",
            platform.UtcNow.UtcDateTime));
        var host = new FakeProcessHost(platform, events, selectedPort: 28_771) { SuppressListener = true };
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            new FakeLifecycle(events),
            startupTimeout: TimeSpan.FromMilliseconds(5));

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, snapshot.State);
        Assert.Equal(
            ["quiesce:EndpointCycle", "start", "quiesce:Teardown", "stop"],
            events);
    }

    [Fact]
    public async Task Restart_EndpointCycleFailureRestoresTerminalRouting()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var store = new LocalAiManifestStore(paths);
        LocalAiResolvedInstall install = (await store.LoadAsync())!;
        await store.SaveAsync(install.Manifest with { Endpoint = "http://127.0.0.1:28765/v1" });

        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback,
            28_765,
            9001,
            "other-process",
            @"C:\other\server.exe",
            platform.UtcNow.UtcDateTime));
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) => Task.FromResult(
                call == 1
                    ? LocalAiEndpointLifecycleResult.Failed("endpoint-cycle verification failed")
                    : LocalAiEndpointLifecycleResult.Ok()),
        };
        var host = new FakeProcessHost(platform, events, selectedPort: 28_771);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle);

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, snapshot.State);
        Assert.Equal(
            ["quiesce:EndpointCycle", "quiesce:Teardown"],
            events);
        Assert.Null(host.LastSpec);
    }

    [Fact]
    public async Task Restart_EndpointCycleCancellationRestoresTerminalRouting()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var store = new LocalAiManifestStore(paths);
        LocalAiResolvedInstall install = (await store.LoadAsync())!;
        await store.SaveAsync(install.Manifest with { Endpoint = "http://127.0.0.1:28765/v1" });

        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback,
            28_765,
            9001,
            "other-process",
            @"C:\other\server.exe",
            platform.UtcNow.UtcDateTime));
        using var cancellation = new CancellationTokenSource();
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) =>
            {
                if (call != 1)
                    return Task.FromResult(LocalAiEndpointLifecycleResult.Ok());
                cancellation.Cancel();
                return Task.FromCanceled<LocalAiEndpointLifecycleResult>(cancellation.Token);
            },
        };
        var host = new FakeProcessHost(platform, events, selectedPort: 28_771);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.EnsureStartedAsync(cancellation.Token));

        Assert.Equal(LocalAiRuntimeState.Stopped, runtime.Snapshot.State);
        Assert.Equal(
            ["quiesce:EndpointCycle", "quiesce:Teardown"],
            events);
        Assert.Null(host.LastSpec);
    }

    [Fact]
    public async Task Restart_RestoresTerminalRoutingWhenStartupNeverBecomesHealthy()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var store = new LocalAiManifestStore(paths);
        LocalAiResolvedInstall install = (await store.LoadAsync())!;
        await store.SaveAsync(install.Manifest with { Endpoint = "http://127.0.0.1:28765/v1" });

        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_765) { SuppressListener = true };
        var client = new FakeClient(events);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            client,
            new FakeLifecycle(events),
            startupTimeout: TimeSpan.FromMilliseconds(5));

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, snapshot.State);
        Assert.Equal(
            ["quiesce:EndpointCycle", "start", "quiesce:Teardown", "stop"],
            events);
    }

    [Fact]
    public async Task Restart_LaunchExceptionRestoresTerminalRouting()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var store = new LocalAiManifestStore(paths);
        LocalAiResolvedInstall install = (await store.LoadAsync())!;
        await store.SaveAsync(install.Manifest with { Endpoint = "http://127.0.0.1:28765/v1" });

        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_765) { ThrowOnStart = true };
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            new FakeLifecycle(events));

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, snapshot.State);
        Assert.Equal(["quiesce:EndpointCycle", "quiesce:Teardown"], events);
        Assert.Null(host.LastSpec);
        LocalAiResolvedInstall? saved = await new LocalAiManifestStore(paths).LoadAsync();
        Assert.Equal(28_765, saved!.Endpoint!.Port);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_ExhaustedAutomaticRestoresTerminalRouting(bool teardownFails)
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var store = new LocalAiManifestStore(paths);
        LocalAiResolvedInstall install = (await store.LoadAsync())!;
        await store.SaveAsync(install.Manifest with { Endpoint = "http://127.0.0.1:28765/v1" });

        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_765);
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) => Task.FromResult(
                teardownFails && call == 4
                    ? LocalAiEndpointLifecycleResult.Failed("exhausted teardown failed")
                    : LocalAiEndpointLifecycleResult.Ok()),
        };
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle,
            startupTimeout: TimeSpan.FromSeconds(5),
            maxRestartAttempts: 1);

        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        int settled = events.Count;

        await TriggerExitAndWaitForStateAsync(
            runtime,
            host,
            snapshot => snapshot.State == LocalAiRuntimeState.Healthy);
        LocalAiRuntimeSnapshot exhausted = await TriggerExitAndWaitForStateAsync(
            runtime,
            host,
            snapshot => snapshot.State == LocalAiRuntimeState.Failed &&
                snapshot.Detail?.Contains(
                    teardownFails ? "safely disabled" : "exited unexpectedly",
                    StringComparison.Ordinal) == true);

        Assert.Equal(LocalAiRuntimeState.Failed, exhausted.State);
        Assert.Equal(LocalAiOwnership.None, exhausted.Ownership);
        Assert.Equal(
            [
                "quiesce:EndpointCycle",
                "quiesce:EndpointCycle",
                "start",
                "probe:28765",
                "publish:28765",
                "quiesce:Teardown",
            ],
            events.Skip(settled).ToArray());
    }

    [Fact]
    public async Task Restart_AutomaticEndpointCycleAndTeardownFailureReportsTerminalCleanup()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) => Task.FromResult(
                call is 2 or 3
                    ? LocalAiEndpointLifecycleResult.Failed("automatic withdrawal failed")
                    : LocalAiEndpointLifecycleResult.Ok()),
        };
        var host = new FakeProcessHost(platform, events, selectedPort: 28_765);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle,
            maxRestartAttempts: 1);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        int settled = events.Count;

        LocalAiRuntimeSnapshot failed = await TriggerExitAndWaitForStateAsync(
            runtime,
            host,
            snapshot => snapshot.State == LocalAiRuntimeState.Failed &&
                snapshot.Detail?.Contains("Terminal gateway routing could not be restored", StringComparison.Ordinal) == true);

        Assert.Equal(LocalAiOwnership.None, failed.Ownership);
        Assert.Equal(
            ["quiesce:EndpointCycle", "quiesce:Teardown"],
            events.Skip(settled).ToArray());
    }

    [Fact]
    public async Task Restart_AutomaticNotInstalledOutcomeCompletesTerminalTeardown()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var teardown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform
        {
            AfterDelay = () => File.Delete(paths.ManifestPath),
        };
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) =>
            {
                if (call == 3)
                    teardown.TrySetResult();
                return Task.FromResult(LocalAiEndpointLifecycleResult.Ok());
            },
        };
        var host = new FakeProcessHost(platform, events, selectedPort: 28_765);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle,
            maxRestartAttempts: 1);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        int settled = events.Count;

        FakeProcess process = host.Process!;
        process.MarkExited();
        platform.Listeners.Clear();
        host.LastExitCallback!(new LocalAiManagedProcessExit(
            process.ProcessId,
            process.StartedAtUtc,
            1));
        await teardown.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LocalAiRuntimeState.NotInstalled, runtime.Snapshot.State);
        Assert.Equal(
            ["quiesce:EndpointCycle", "quiesce:Teardown"],
            events.Skip(settled).ToArray());
        Assert.DoesNotContain("start", events.Skip(settled));
    }

    [Fact]
    public async Task Restart_AutomaticStartupExceptionCompletesTerminalTeardown()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var teardown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new SynchronizedEventLog();
        bool failListenerCapture = false;
        var platform = new FakePlatform
        {
            AfterCapture = () =>
            {
                if (failListenerCapture)
                    throw new InvalidOperationException("listener capture failed");
            },
        };
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (_, reason, _) =>
            {
                if (reason == LocalAiQuiesceReason.Teardown)
                    teardown.TrySetResult();
                return Task.FromResult(LocalAiEndpointLifecycleResult.Ok());
            },
        };
        var host = new FakeProcessHost(platform, events, selectedPort: 28_765);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle,
            maxRestartAttempts: 1);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        int settled = events.Count;
        failListenerCapture = true;

        FakeProcess process = host.Process!;
        process.MarkExited();
        host.LastExitCallback!(new LocalAiManagedProcessExit(
            process.ProcessId,
            process.StartedAtUtc,
            1));
        await teardown.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LocalAiRuntimeState.Failed, runtime.Snapshot.State);
        Assert.Equal(
            ["quiesce:EndpointCycle", "quiesce:Teardown"],
            events.Skip(settled).ToArray());
        Assert.DoesNotContain("start", events.Skip(settled));
    }

    [Fact]
    public async Task Restart_AutomaticEndpointCycleFailureCompletesTeardown()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) => Task.FromResult(
                call == 2
                    ? LocalAiEndpointLifecycleResult.Failed("automatic endpoint-cycle failed")
                    : LocalAiEndpointLifecycleResult.Ok()),
        };
        var host = new FakeProcessHost(platform, events, selectedPort: 28_765);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle,
            maxRestartAttempts: 1);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        int settled = events.Count;

        LocalAiRuntimeSnapshot failed = await TriggerExitAndWaitForStateAsync(
            runtime,
            host,
            snapshot => snapshot.State == LocalAiRuntimeState.Failed &&
                snapshot.Detail == "automatic endpoint-cycle failed");

        Assert.Equal(LocalAiRuntimeState.Failed, failed.State);
        Assert.Equal(
            ["quiesce:EndpointCycle", "quiesce:Teardown"],
            events.Skip(settled).ToArray());
    }

    private static async Task<LocalAiRuntimeSnapshot> TriggerExitAndWaitForStateAsync(
        LlamaServerRuntimeService runtime,
        FakeProcessHost host,
        Func<LocalAiRuntimeSnapshot, bool> predicate)
    {
        var completion = new TaskCompletionSource<LocalAiRuntimeSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStateChanged(object? sender, LocalAiRuntimeSnapshotChangedEventArgs args)
        {
            if (predicate(args.Snapshot))
                completion.TrySetResult(args.Snapshot);
        }

        runtime.StateChanged += OnStateChanged;
        try
        {
            FakeProcess process = host.Process!;
            process.MarkExited();
            host.LastExitCallback!(new LocalAiManagedProcessExit(
                process.ProcessId,
                process.StartedAtUtc,
                1));
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            runtime.StateChanged -= OnStateChanged;
        }
    }

    [Fact]
    public async Task Restart_CancellationWithdrawsRetainedRouteBeforeDisposingChild()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var store = new LocalAiManifestStore(paths);
        LocalAiResolvedInstall install = (await store.LoadAsync())!;
        await store.SaveAsync(install.Manifest with { Endpoint = "http://127.0.0.1:28765/v1" });

        var events = new SynchronizedEventLog();
        using var cancellation = new CancellationTokenSource();
        var platform = new FakePlatform
        {
            YieldOnDelay = true,
            AfterDelay = cancellation.Cancel,
        };
        var host = new FakeProcessHost(platform, events, selectedPort: 28_765) { SuppressListener = true };
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            new FakeLifecycle(events),
            startupTimeout: TimeSpan.FromSeconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.EnsureStartedAsync(cancellation.Token));

        Assert.Equal(LocalAiRuntimeState.Stopped, runtime.Snapshot.State);
        Assert.Equal(
            ["quiesce:EndpointCycle", "start", "quiesce:Teardown", "stop"],
            events);
    }

    [Fact]
    public async Task Restart_CancellationDuringPresetPreparationRestoresTerminalRouting()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var store = new LocalAiManifestStore(paths);
        LocalAiResolvedInstall install = (await store.LoadAsync())!;
        await store.SaveAsync(install.Manifest with { Endpoint = "http://127.0.0.1:28765/v1" });

        var events = new SynchronizedEventLog();
        using var cancellation = new CancellationTokenSource();
        var platform = new FakePlatform
        {
            AfterCapture = cancellation.Cancel,
        };
        var lifecycle = new FakeLifecycle(events);
        var host = new FakeProcessHost(platform, events, selectedPort: 28_765);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.EnsureStartedAsync(cancellation.Token));

        Assert.Equal(LocalAiRuntimeState.Stopped, runtime.Snapshot.State);
        Assert.Equal(["quiesce:EndpointCycle", "quiesce:Teardown"], events);
        Assert.Null(host.LastSpec);
    }

    [Fact]
    public async Task Restart_TeardownFailurePreservesLiveManagedListener()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_765)
        {
            SuppressListener = true,
        };
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) => Task.FromResult(
                call == 1
                    ? LocalAiEndpointLifecycleResult.Ok()
                    : LocalAiEndpointLifecycleResult.Failed("teardown failed")),
        };
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle,
            startupTimeout: TimeSpan.FromMilliseconds(5));

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, snapshot.State);
        Assert.Equal(LocalAiOwnership.CompanionManaged, snapshot.Ownership);
        Assert.Equal(host.Process!.ProcessId, snapshot.ProcessId);
        Assert.False(host.Process.HasExited);
        Assert.Equal(
            ["quiesce:EndpointCycle", "start", "quiesce:Teardown"],
            events);
    }

    [Theory]
    [InlineData(LocalAiModelAvailabilityState.Unknown, true)]
    [InlineData(LocalAiModelAvailabilityState.Loaded, false)]
    public async Task AutomaticPort_DoesNotPersistOrPublishWithoutReadyModelEvidence(
        LocalAiModelAvailabilityState modelState,
        bool pathMatches)
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_774);
        var client = new FakeClient(events, (expectedModelPath, _) => new(
            true,
            modelState,
            pathMatches ? expectedModelPath : expectedModelPath + ".other",
            "The managed model is not ready."));
        var lifecycle = new FakeLifecycle(events);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            client,
            lifecycle,
            startupTimeout: TimeSpan.FromMilliseconds(2));

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, snapshot.State);
        Assert.DoesNotContain(events, value => value.StartsWith("publish:", StringComparison.Ordinal));
        Assert.True(host.Process!.StopCount > 0);
        LocalAiResolvedInstall? saved = await new LocalAiManifestStore(paths).LoadAsync();
        Assert.Null(saved!.Endpoint);
    }

    [Fact]
    public async Task Refresh_UpdatesPublicationWhenManagedModelReadinessChanges()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_773);
        var client = new FakeClient(events, (expectedModelPath, probeNumber) => probeNumber == 2
            ? new(
                true,
                LocalAiModelAvailabilityState.Loaded,
                expectedModelPath + ".other",
                "The managed model path changed.")
            : new(
                true,
                LocalAiModelAvailabilityState.Verified,
                expectedModelPath,
                "The managed model is ready."));
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            client,
            new FakeLifecycle(events));
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        events.Clear();

        LocalAiRuntimeSnapshot refreshed = await runtime.RefreshAsync();

        Assert.Equal(LocalAiRuntimeState.Starting, refreshed.State);
        Assert.Equal(LocalAiModelAvailabilityState.Unknown, refreshed.ModelEvidence.State);
        Assert.Equal(["probe:28773", "quiesce:EndpointCycle"], events);
        events.Clear();

        LocalAiRuntimeSnapshot recovered = await runtime.RefreshAsync();

        Assert.Equal(LocalAiRuntimeState.Healthy, recovered.State);
        Assert.Equal(["probe:28773", "publish:28773"], events);
    }

    [Theory]
    [InlineData(RefreshOwnershipLoss.Incomplete, LocalAiRuntimeState.Conflict, LocalAiQuiesceReason.EndpointCycle, true)]
    [InlineData(RefreshOwnershipLoss.Conflict, LocalAiRuntimeState.Conflict, LocalAiQuiesceReason.EndpointCycle, true)]
    [InlineData(RefreshOwnershipLoss.MissingEndpoint, LocalAiRuntimeState.Starting, LocalAiQuiesceReason.EndpointCycle, false)]
    public async Task Refresh_QuiescesPublishedRouteWhenEndpointOwnershipIsLost(
        RefreshOwnershipLoss ownershipLoss,
        LocalAiRuntimeState expectedState,
        LocalAiQuiesceReason expectedReason,
        bool expectedExited)
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_775);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            new FakeLifecycle(events));
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        events.Clear();

        switch (ownershipLoss)
        {
            case RefreshOwnershipLoss.Incomplete:
                platform.Ipv4Complete = false;
                break;
            case RefreshOwnershipLoss.Conflict:
                platform.Listeners[0] = platform.Listeners[0] with { Address = IPAddress.Any };
                break;
            case RefreshOwnershipLoss.MissingEndpoint:
                platform.Listeners.Clear();
                break;
            default:
                throw new InvalidOperationException("Unknown ownership-loss case.");
        }

        LocalAiRuntimeSnapshot refreshed = await runtime.RefreshAsync();

        Assert.Equal(expectedState, refreshed.State);
        Assert.Equal(
            expectedExited
                ? [$"quiesce:{expectedReason}", "stop"]
                : [$"quiesce:{expectedReason}"],
            events);
        Assert.Equal(expectedExited, host.Process!.HasExited);
        if (expectedExited)
        {
            events.Clear();

            LocalAiRuntimeSnapshot retained = await runtime.RefreshAsync();

            Assert.Equal(expectedState, retained.State);
            Assert.Empty(events);
        }
    }

    [Fact]
    public async Task Startup_TeardownFailureAfterExitedChildPublishesFailedCleanupState()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) => Task.FromResult(
                call == 1
                    ? LocalAiEndpointLifecycleResult.Ok()
                    : LocalAiEndpointLifecycleResult.Failed("terminal teardown failed")),
        };
        var host = new FakeProcessHost(platform, events, selectedPort: 28_780)
        {
            ImmediateExit = true,
        };
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle);

        LocalAiRuntimeSnapshot failed = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, failed.State);
        Assert.Equal(LocalAiOwnership.None, failed.Ownership);
        Assert.Null(failed.ProcessId);
        Assert.Contains("could not be safely disabled", failed.Detail, StringComparison.Ordinal);
        Assert.True(failed.GatewayRouteRequiresResolution);
        Assert.Equal(
            ["quiesce:EndpointCycle", "start", "quiesce:Teardown", "stop"],
            events);
    }

    [Fact]
    public async Task Startup_CancellationTeardownFailureAfterExitedChildDoesNotPublishStopped()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        using var cancellation = new CancellationTokenSource();
        var events = new SynchronizedEventLog();
        FakeProcessHost? host = null;
        var platform = new FakePlatform
        {
            AfterDelay = () =>
            {
                host!.Process!.MarkExited();
                cancellation.Cancel();
            },
        };
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) => Task.FromResult(
                call == 1
                    ? LocalAiEndpointLifecycleResult.Ok()
                    : LocalAiEndpointLifecycleResult.Failed("terminal teardown failed")),
        };
        host = new FakeProcessHost(platform, events, selectedPort: 28_782)
        {
            SuppressListener = true,
        };
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.EnsureStartedAsync(cancellation.Token));

        Assert.Equal(LocalAiRuntimeState.Failed, runtime.Snapshot.State);
        Assert.Equal(LocalAiOwnership.None, runtime.Snapshot.Ownership);
        Assert.Contains("could not be safely disabled", runtime.Snapshot.Detail, StringComparison.Ordinal);
        Assert.True(runtime.Snapshot.GatewayRouteRequiresResolution);
        Assert.Equal(
            ["quiesce:EndpointCycle", "start", "quiesce:Teardown", "stop"],
            events);
    }

    [Fact]
    public async Task Startup_EndpointCycleExceptionPublishesFailedState()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) => call == 1
                ? Task.FromException<LocalAiEndpointLifecycleResult>(
                    new IOException("endpoint-cycle withdrawal failed"))
                : Task.FromResult(LocalAiEndpointLifecycleResult.Ok()),
        };
        var platform = new FakePlatform();
        await using var runtime = CreateRuntime(
            paths,
            new FakeProcessHost(platform, events, selectedPort: 28_783),
            platform,
            new FakeClient(events),
            lifecycle);

        LocalAiRuntimeSnapshot failed = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, failed.State);
        Assert.Equal(LocalAiOwnership.None, failed.Ownership);
        Assert.Equal("endpoint-cycle withdrawal failed", failed.Detail);
        Assert.False(failed.GatewayRouteRequiresResolution);
        Assert.Equal(["quiesce:EndpointCycle", "quiesce:Teardown"], events);
    }

    [Fact]
    public async Task Startup_PublishExceptionWithdrawsUsingVerifiedEndpoint()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events)
        {
            PublishException = new IOException("publish wrote then failed"),
        };
        await using var runtime = CreateRuntime(
            paths,
            new FakeProcessHost(platform, events, selectedPort: 28_785),
            platform,
            new FakeClient(events),
            lifecycle);

        LocalAiRuntimeSnapshot failed = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, failed.State);
        Assert.Equal(
            [null, new Uri("http://127.0.0.1:28785/v1")],
            lifecycle.QuiescedEndpoints);
        Assert.False(failed.GatewayRouteRequiresResolution);
        Assert.Equal(
            ["quiesce:EndpointCycle", "start", "probe:28785", "publish:28785", "quiesce:Teardown", "stop"],
            events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_UnsafeOrUnverifiableListenerStopsWhenTeardownFails(
        bool incompleteListenerSnapshot)
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        FakePlatform? platform = null;
        platform = new FakePlatform
        {
            AfterCapture = incompleteListenerSnapshot
                ? () => platform!.Ipv4Complete = false
                : null,
        };
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) => Task.FromResult(
                call == 2
                    ? LocalAiEndpointLifecycleResult.Failed("terminal teardown failed")
                    : LocalAiEndpointLifecycleResult.Ok()),
        };
        var host = new FakeProcessHost(
            platform,
            events,
            selectedPort: 28_785,
            listenerAddress: incompleteListenerSnapshot ? null : IPAddress.Any);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle);

        LocalAiRuntimeSnapshot failed = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, failed.State);
        Assert.Equal(LocalAiOwnership.None, failed.Ownership);
        Assert.True(host.Process!.HasExited);
        Assert.Contains("untrusted managed listener was stopped", failed.Detail, StringComparison.Ordinal);
        Assert.Equal(
            ["quiesce:EndpointCycle", "start", "quiesce:Teardown", "stop"],
            events);
    }

    [Fact]
    public async Task Startup_PreservedListenerRemainsSupervisedAfterTeardownFailure()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) => Task.FromResult(
                call == 2
                    ? LocalAiEndpointLifecycleResult.Failed("terminal teardown failed")
                    : LocalAiEndpointLifecycleResult.Ok()),
        };
        var host = new FakeProcessHost(platform, events, selectedPort: 28_781)
        {
            SuppressListener = true,
        };
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle,
            startupTimeout: TimeSpan.FromMilliseconds(2),
            maxRestartAttempts: 0);

        LocalAiRuntimeSnapshot startupFailure = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiOwnership.CompanionManaged, startupFailure.Ownership);

        LocalAiRuntimeSnapshot exitFailure = await TriggerExitAndWaitForStateAsync(
            runtime,
            host,
            snapshot => snapshot.State == LocalAiRuntimeState.Failed &&
                snapshot.Detail?.Contains("exited unexpectedly", StringComparison.Ordinal) == true);

        Assert.Equal(LocalAiOwnership.None, exitFailure.Ownership);
        Assert.Equal(
            ["quiesce:EndpointCycle", "start", "quiesce:Teardown", "quiesce:Teardown"],
            events);
    }

    [Fact]
    public async Task Refresh_TeardownFailurePreservesUnverifiedManagedProcess()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_776);
        var client = new FakeClient(events, (expectedModelPath, probeNumber) => probeNumber == 1
            ? ReadyProbe(expectedModelPath)
            : new(
                false,
                LocalAiModelAvailabilityState.Unknown,
                null,
                "The managed model is not ready."));
        var lifecycle = new FakeLifecycle(events);
        await using var runtime = CreateRuntime(paths, host, platform, client, lifecycle);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        events.Clear();
        lifecycle.FailQuiesce = true;

        LocalAiRuntimeSnapshot refreshed = await runtime.RefreshAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, refreshed.State);
        Assert.Equal(LocalAiOwnership.CompanionManaged, refreshed.Ownership);
        Assert.Equal(host.Process!.ProcessId, refreshed.ProcessId);
        Assert.False(host.Process.HasExited);
        Assert.Equal(
            ["probe:28776", "quiesce:EndpointCycle", "quiesce:Teardown"],
            events);
    }

    [Fact]
    public async Task Refresh_EndpointCycleFailureUsesTeardownBeforeStopping()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_776);
        var client = new FakeClient(events, (expectedModelPath, probeNumber) => probeNumber == 1
            ? ReadyProbe(expectedModelPath)
            : new(
                false,
                LocalAiModelAvailabilityState.Unknown,
                null,
                "The managed model is not ready."));
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) => Task.FromResult(
                call == 2
                    ? LocalAiEndpointLifecycleResult.Failed("endpoint-cycle failed")
                    : LocalAiEndpointLifecycleResult.Ok()),
        };
        await using var runtime = CreateRuntime(paths, host, platform, client, lifecycle);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        events.Clear();

        LocalAiRuntimeSnapshot refreshed = await runtime.RefreshAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, refreshed.State);
        Assert.Equal(LocalAiOwnership.None, refreshed.Ownership);
        Assert.Null(refreshed.ProcessId);
        Assert.True(host.Process!.HasExited);
        Assert.Equal(
            ["probe:28776", "quiesce:EndpointCycle", "quiesce:Teardown", "stop"],
            events);
    }

    [Fact]
    public async Task Refresh_QuiesceExceptionPreservesManagedProcessWhenTeardownAlsoFails()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_779);
        var lifecycle = new FakeLifecycle(events);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        events.Clear();
        platform.Listeners.Clear();
        lifecycle.QuiesceException = new IOException("route withdrawal failed");

        IOException error = await Assert.ThrowsAsync<IOException>(() => runtime.RefreshAsync());

        Assert.Equal("route withdrawal failed", error.Message);
        Assert.Equal(LocalAiRuntimeState.Failed, runtime.Snapshot.State);
        Assert.Equal(LocalAiOwnership.CompanionManaged, runtime.Snapshot.Ownership);
        Assert.Equal(host.Process!.ProcessId, runtime.Snapshot.ProcessId);
        Assert.False(host.Process.HasExited);
        Assert.Equal(
            ["quiesce:EndpointCycle", "quiesce:Teardown"],
            events);
    }

    [Fact]
    public async Task Refresh_UnsafeListenerStopsWhenEndpointCycleCleanupFails()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) => Task.FromResult(
                call is 2 or 3
                    ? LocalAiEndpointLifecycleResult.Failed("endpoint-cycle cleanup failed")
                    : LocalAiEndpointLifecycleResult.Ok()),
        };
        var host = new FakeProcessHost(platform, events, selectedPort: 28_779);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        platform.Ipv4Complete = false;
        events.Clear();

        LocalAiRuntimeSnapshot failed = await runtime.RefreshAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, failed.State);
        Assert.Equal(LocalAiOwnership.None, failed.Ownership);
        Assert.True(host.Process!.HasExited);
        Assert.Contains("untrusted managed listener was stopped", failed.Detail, StringComparison.Ordinal);
        Assert.Equal(
            ["quiesce:EndpointCycle", "quiesce:EndpointCycle", "stop"],
            events);
    }

    [Fact]
    public async Task Refresh_RecoveryRebindsFreshlyVerifiedEndpointBeforePublishing()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_777);
        var client = new FakeClient(events, (expectedModelPath, probeNumber) => probeNumber == 2
            ? new(
                false,
                LocalAiModelAvailabilityState.Unknown,
                null,
                "The managed model is not ready.")
            : ReadyProbe(expectedModelPath));
        var lifecycle = new FakeLifecycle(events);
        await using var runtime = CreateRuntime(paths, host, platform, client, lifecycle);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        LocalAiRuntimeSnapshot unavailable = await runtime.RefreshAsync();
        Assert.Equal(LocalAiRuntimeState.Starting, unavailable.State);

        var store = new LocalAiManifestStore(paths);
        LocalAiResolvedInstall install = (await store.LoadAsync())!;
        await store.SaveAsync(install.Manifest with { Endpoint = "http://127.0.0.1:29999/v1" });
        events.Clear();

        LocalAiRuntimeSnapshot recovered = await runtime.RefreshAsync();

        Assert.Equal(LocalAiRuntimeState.Healthy, recovered.State);
        Assert.Equal(28_777, recovered.Endpoint.Port);
        Assert.Equal(["probe:28777", "publish:28777"], events);
        LocalAiResolvedInstall rebound = (await store.LoadAsync())!;
        Assert.Equal(28_777, rebound.Endpoint!.Port);
    }

    [Fact]
    public async Task Refresh_RecoveryPublishFailureCompletesTerminalTeardown()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_778);
        var client = new FakeClient(events, (expectedModelPath, probeNumber) => probeNumber == 2
            ? new(
                false,
                LocalAiModelAvailabilityState.Unknown,
                null,
                "The managed model is not ready.")
            : ReadyProbe(expectedModelPath));
        var lifecycle = new FakeLifecycle(events);
        await using var runtime = CreateRuntime(paths, host, platform, client, lifecycle);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        LocalAiRuntimeSnapshot unavailable = await runtime.RefreshAsync();
        Assert.Equal(LocalAiRuntimeState.Starting, unavailable.State);
        events.Clear();
        lifecycle.FailPublish = true;

        LocalAiRuntimeSnapshot failed = await runtime.RefreshAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, failed.State);
        Assert.Equal(LocalAiOwnership.None, failed.Ownership);
        Assert.True(host.Process!.HasExited);
        Assert.Equal(
            ["probe:28778", "publish:28778", "quiesce:Teardown", "stop"],
            events);
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
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        platform.Listeners.Add(new WindowsTcpListenerInfo(
            IPAddress.Loopback,
            fixedPort,
            9001,
            "other-process",
            @"C:\other\server.exe",
            platform.UtcNow.UtcDateTime));
        var host = new FakeProcessHost(platform, events, selectedPort: fixedPort);
        var lifecycle = new FakeLifecycle(events);
        await using var runtime = CreateRuntime(paths, host, platform, new FakeClient(events), lifecycle);

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Conflict, snapshot.State);
        Assert.Equal(["quiesce:Teardown"], events);
        Assert.Null(host.LastSpec);
    }

    [Fact]
    public async Task PreparationFailure_QuiescesEndpointConsumerBeforeReturning()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        LocalAiResolvedInstall install = (await new LocalAiManifestStore(paths).LoadAsync())!;
        File.Delete(install.ExecutablePath);
        var events = new SynchronizedEventLog();
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
        Assert.Equal(["quiesce:EndpointCycle", "quiesce:Teardown"], events);
        Assert.Null(host.LastSpec);
    }

    [Fact]
    public async Task MissingImplementationLibrary_FailsBeforeStartingNativeProcess()
    {
        using var temp = new TempDirectory("local-ai-missing-impl-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        LocalAiResolvedInstall install = (await new LocalAiManifestStore(paths).LoadAsync())!;
        File.Delete(Path.Combine(
            Path.GetDirectoryName(install.ExecutablePath)!,
            LlamaRuntimeCatalog.ServerImplementationLibraryName));
        var events = new SynchronizedEventLog();
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
        Assert.Contains("implementation library", snapshot.Detail, StringComparison.Ordinal);
        Assert.Equal(["quiesce:EndpointCycle", "quiesce:Teardown"], events);
        Assert.Null(host.LastSpec);
    }

    [Fact]
    public async Task AutomaticPort_RejectsWildcardChildListenerWithoutProbing()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
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
        Assert.Equal(["quiesce:EndpointCycle", "start", "quiesce:Teardown", "stop"], events);
        LocalAiResolvedInstall? saved = await new LocalAiManifestStore(paths).LoadAsync();
        Assert.Null(saved!.Endpoint);
    }

    [Fact]
    public async Task Resume_RenewsTheBoundedRestartBudgetAfterRecovery()
    {
        using var temp = new TempDirectory("local-ai-resume-budget-");
        var paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_769);
        await using var runtime = CreateRuntime(paths, host, platform,
            new FakeClient(events), new FakeLifecycle(events), maxRestartAttempts: 1);
        Assert.Equal(LocalAiRuntimeState.Healthy, (await runtime.EnsureStartedAsync()).State);
        await TriggerExitAndWaitForStateAsync(runtime, host,
            snapshot => snapshot.State == LocalAiRuntimeState.Healthy);
        await TriggerExitAndWaitForStateAsync(runtime, host,
            snapshot => snapshot.State == LocalAiRuntimeState.Failed);
        Assert.Equal(LocalAiRuntimeState.Healthy, (await runtime.ResumeAsync()).State);
        var recovered = await TriggerExitAndWaitForStateAsync(runtime, host,
            snapshot => snapshot.State == LocalAiRuntimeState.Healthy);
        Assert.Equal(LocalAiRuntimeState.Healthy, recovered.State);
    }

    [Fact]
    public async Task Stop_QueuedAutomaticResumeCannotOverrideStopButExplicitStartCan()
    {
        using var temp = new TempDirectory("local-ai-stop-intent-");
        var paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var withdrawing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = async (call, _, ct) =>
            {
                if (call == 2)
                {
                    withdrawing.SetResult();
                    await release.Task.WaitAsync(ct);
                }
                return LocalAiEndpointLifecycleResult.Ok();
            }
        };
        await using var runtime = CreateRuntime(paths,
            new FakeProcessHost(platform, events, selectedPort: 28_769),
            platform, new FakeClient(events), lifecycle);
        Assert.Equal(LocalAiRuntimeState.Healthy, (await runtime.EnsureStartedAsync()).State);
        var stopping = runtime.StopAsync();
        await withdrawing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([false, true, false], lifecycle.RecoveryIntents);
        var resume = runtime.ResumeAsync();
        Assert.False(resume.IsCompleted);
        release.SetResult();
        await stopping;
        Assert.Equal(LocalAiRuntimeState.Stopped, (await resume).State);
        Assert.Equal(LocalAiRuntimeState.Stopped, (await runtime.ResumeAsync()).State);
        Assert.Equal(1, events.Count(value => value == "start"));
        Assert.Equal(LocalAiRuntimeState.Healthy, (await runtime.EnsureStartedAsync()).State);
        Assert.Equal([false, true, false, false, true], lifecycle.RecoveryIntents);
        Assert.Equal(2, events.Count(value => value == "start"));
    }

    [Fact]
    public async Task Stop_QuiescesEndpointConsumerBeforeListenerDisappears()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
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
        Assert.Equal(["quiesce:Teardown", "stop"], events);

        File.Delete(paths.ManifestPath);
        LocalAiRuntimeSnapshot refreshed = await runtime.RefreshAsync();

        Assert.Equal(LocalAiRuntimeState.NotInstalled, refreshed.State);
    }

    [Fact]
    public async Task Stop_QuiesceExceptionRetriesTeardownBeforeStopping()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) => call == 2
                ? Task.FromException<LocalAiEndpointLifecycleResult>(
                    new IOException("stop withdrawal interrupted"))
                : Task.FromResult(LocalAiEndpointLifecycleResult.Ok()),
        };
        var host = new FakeProcessHost(platform, events, selectedPort: 28_769);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        events.Clear();

        IOException error = await Assert.ThrowsAsync<IOException>(() => runtime.StopAsync());

        Assert.Equal("stop withdrawal interrupted", error.Message);
        Assert.Equal(LocalAiRuntimeState.Failed, runtime.Snapshot.State);
        Assert.Equal(LocalAiOwnership.None, runtime.Snapshot.Ownership);
        Assert.True(host.Process!.HasExited);
        Assert.Equal(["quiesce:Teardown", "quiesce:Teardown", "stop"], events);
    }

    [Fact]
    public async Task Stop_ProcessShutdownExceptionPublishesRetryableFailure()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_769);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            new FakeLifecycle(events));
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        host.Process!.StopException = new IOException("process shutdown failed");
        events.Clear();

        IOException error = await Assert.ThrowsAsync<IOException>(() => runtime.StopAsync());

        Assert.Equal("process shutdown failed", error.Message);
        Assert.Equal(LocalAiRuntimeState.Failed, runtime.Snapshot.State);
        Assert.Equal(LocalAiOwnership.CompanionManaged, runtime.Snapshot.Ownership);
        Assert.False(host.Process.HasExited);
        Assert.Contains("managed listener remains running", runtime.Snapshot.Detail, StringComparison.Ordinal);
        Assert.Equal(["quiesce:Teardown", "stop"], events);

        host.Process.StopException = null;
        events.Clear();
        LocalAiRuntimeSnapshot stopped = await runtime.StopAsync();

        Assert.Equal(LocalAiRuntimeState.Stopped, stopped.State);
        Assert.True(host.Process.HasExited);
        Assert.Equal(["quiesce:Teardown", "stop"], events);
    }

    [Fact]
    public async Task Startup_ProcessShutdownExceptionPublishesRetryableFailure()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_793)
        {
            SuppressListener = true,
            InitialStopException = new IOException("process shutdown failed"),
        };
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            new FakeLifecycle(events),
            startupTimeout: TimeSpan.FromSeconds(1));

        IOException error = await Assert.ThrowsAsync<IOException>(() => runtime.EnsureStartedAsync());

        Assert.Equal("process shutdown failed", error.Message);
        Assert.Equal(LocalAiRuntimeState.Failed, runtime.Snapshot.State);
        Assert.Equal(LocalAiOwnership.CompanionManaged, runtime.Snapshot.Ownership);
        Assert.False(host.Process!.HasExited);
        Assert.Contains("remains running", runtime.Snapshot.Detail, StringComparison.Ordinal);

        host.Process.StopException = null;
        Assert.Equal(LocalAiRuntimeState.Stopped, (await runtime.StopAsync()).State);
    }

    [Fact]
    public async Task Dispose_ProcessShutdownFailureRetriesAndReleasesProcess()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_769);
        var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            new FakeLifecycle(events));
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        host.Process!.StopException = new IOException("process shutdown failed");
        events.Clear();

        await runtime.DisposeAsync();

        Assert.Equal(2, host.Process.StopCount);
        Assert.Equal(1, host.Process.DisposeCount);
        Assert.Equal(["quiesce:Teardown", "stop", "stop"], events);
    }

    [Fact]
    public async Task Stop_FailedWithdrawalLatchesIntentAndPreventsAutomaticRestart()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) => call switch
            {
                2 => Task.FromException<LocalAiEndpointLifecycleResult>(
                    new IOException("stop withdrawal interrupted")),
                3 => Task.FromResult(LocalAiEndpointLifecycleResult.Failed("stop teardown failed")),
                _ => Task.FromResult(LocalAiEndpointLifecycleResult.Ok()),
            },
        };
        var host = new FakeProcessHost(platform, events, selectedPort: 28_769);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle,
            maxRestartAttempts: 2);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        events.Clear();

        await Assert.ThrowsAsync<IOException>(() => runtime.StopAsync());
        Assert.Equal(LocalAiOwnership.CompanionManaged, runtime.Snapshot.Ownership);
        int settled = events.Count;

        LocalAiRuntimeSnapshot refreshed = await runtime.RefreshAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, refreshed.State);
        Assert.Equal(settled, events.Count);

        LocalAiRuntimeSnapshot exited = await TriggerExitAndWaitForStateAsync(
            runtime,
            host,
            snapshot => snapshot.Detail?.Contains("exited unexpectedly", StringComparison.Ordinal) == true);

        Assert.Equal(LocalAiRuntimeState.Failed, exited.State);
        Assert.Equal(["quiesce:Teardown"], events.Skip(settled).ToArray());
        Assert.DoesNotContain(events.Skip(settled), value => value is "start" || value.StartsWith("publish:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_DuringAutomaticRestartDelayInvalidatesPendingRestart()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopTeardownEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStopTeardown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform
        {
            AfterDelay = () =>
            {
                delayEntered.TrySetResult();
                releaseDelay.Task.GetAwaiter().GetResult();
            },
        };
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = async (call, reason, _) =>
            {
                if (call == 3 && reason == LocalAiQuiesceReason.Teardown)
                {
                    stopTeardownEntered.TrySetResult();
                    await releaseStopTeardown.Task.ConfigureAwait(false);
                }
                return LocalAiEndpointLifecycleResult.Ok();
            },
        };
        var host = new FakeProcessHost(platform, events, selectedPort: 28_769);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle,
            maxRestartAttempts: 1);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        int settled = events.Count;

        FakeProcess process = host.Process!;
        process.MarkExited();
        platform.Listeners.Clear();
        host.LastExitCallback!(new LocalAiManagedProcessExit(
            process.ProcessId,
            process.StartedAtUtc,
            1));
        await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task<LocalAiRuntimeSnapshot> stopTask = runtime.StopAsync();
        await stopTeardownEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        releaseDelay.TrySetResult();
        releaseStopTeardown.TrySetResult();
        LocalAiRuntimeSnapshot stopped = await stopTask;
        LocalAiRuntimeSnapshot refreshed = await runtime.RefreshAsync();

        Assert.Equal(LocalAiRuntimeState.Stopped, stopped.State);
        Assert.Equal(LocalAiRuntimeState.Stopped, refreshed.State);
        Assert.DoesNotContain("start", events.Skip(settled));
        Assert.Equal(
            ["quiesce:EndpointCycle", "quiesce:Teardown"],
            events.Skip(settled).ToArray());
    }

    [Fact]
    public async Task Stop_FromFailClosedConflictCompletesTerminalTeardown()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_769);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            new FakeLifecycle(events));
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        platform.Ipv4Complete = false;

        LocalAiRuntimeSnapshot conflict = await runtime.RefreshAsync();
        Assert.Equal(LocalAiRuntimeState.Conflict, conflict.State);
        Assert.Equal(LocalAiOwnership.None, conflict.Ownership);
        events.Clear();

        LocalAiRuntimeSnapshot stopped = await runtime.StopAsync();

        Assert.Equal(LocalAiRuntimeState.Stopped, stopped.State);
        Assert.Equal(["quiesce:Teardown"], events);
    }

    [Fact]
    public async Task RestartAsync_UsesEndpointCycleUntilReplacementIsPublished()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_769);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            new FakeLifecycle(events));
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        events.Clear();

        LocalAiRuntimeSnapshot restarted = await runtime.RestartAsync();

        Assert.Equal(LocalAiRuntimeState.Healthy, restarted.State);
        Assert.Equal(
            [
                "quiesce:EndpointCycle",
                "stop",
                "quiesce:EndpointCycle",
                "start",
                "probe:28769",
                "publish:28769",
            ],
            events);
        Assert.DoesNotContain("quiesce:Teardown", events);
    }

    [Fact]
    public async Task RestartAsync_InitialEndpointCycleExceptionCompletesTeardownBeforeStopping()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) => call == 2
                ? Task.FromException<LocalAiEndpointLifecycleResult>(
                    new IOException("restart withdrawal failed"))
                : Task.FromResult(LocalAiEndpointLifecycleResult.Ok()),
        };
        var host = new FakeProcessHost(platform, events, selectedPort: 28_769);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        events.Clear();

        IOException error = await Assert.ThrowsAsync<IOException>(() => runtime.RestartAsync());

        Assert.Equal("restart withdrawal failed", error.Message);
        Assert.Equal(LocalAiRuntimeState.Failed, runtime.Snapshot.State);
        Assert.Equal(LocalAiOwnership.None, runtime.Snapshot.Ownership);
        Assert.True(host.Process!.HasExited);
        Assert.Equal(["quiesce:EndpointCycle", "quiesce:Teardown", "stop"], events);
    }

    [Fact]
    public async Task RestartAsync_FirstOperationExceptionStillCompletesTeardown()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) => call == 1
                ? Task.FromException<LocalAiEndpointLifecycleResult>(
                    new IOException("restart withdrawal failed"))
                : Task.FromResult(LocalAiEndpointLifecycleResult.Ok()),
        };
        await using var runtime = CreateRuntime(
            paths,
            new FakeProcessHost(platform, events, selectedPort: 28_769),
            platform,
            new FakeClient(events),
            lifecycle);

        IOException error = await Assert.ThrowsAsync<IOException>(() => runtime.RestartAsync());

        Assert.Equal("restart withdrawal failed", error.Message);
        Assert.Equal(LocalAiRuntimeState.Failed, runtime.Snapshot.State);
        Assert.Equal(["quiesce:EndpointCycle", "quiesce:Teardown"], events);
    }

    [Fact]
    public async Task RestartAsync_RepeatedTeardownFailureReportsPreservedListener()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) => Task.FromResult(
                call is 4 or 5
                    ? LocalAiEndpointLifecycleResult.Failed("terminal teardown failed")
                    : LocalAiEndpointLifecycleResult.Ok()),
        };
        var host = new FakeProcessHost(platform, events, selectedPort: 28_769);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        lifecycle.FailPublish = true;
        events.Clear();

        LocalAiRuntimeSnapshot failed = await runtime.RestartAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, failed.State);
        Assert.Equal(LocalAiOwnership.CompanionManaged, failed.Ownership);
        Assert.Equal(host.Process!.ProcessId, failed.ProcessId);
        Assert.False(host.Process.HasExited);
        Assert.Contains("managed listener remains running", failed.Detail, StringComparison.Ordinal);
        Assert.Equal(
            [
                "quiesce:EndpointCycle",
                "stop",
                "quiesce:EndpointCycle",
                "start",
                "probe:28769",
                "publish:28769",
                "quiesce:Teardown",
                "quiesce:Teardown",
            ],
            events);
    }

    [Fact]
    public async Task RestartAsync_SuccessfulCleanupRetryStopsPreservedListener()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events)
        {
            QuiesceHandler = (call, _, _) => Task.FromResult(
                call == 4
                    ? LocalAiEndpointLifecycleResult.Failed("terminal teardown failed")
                    : LocalAiEndpointLifecycleResult.Ok()),
        };
        var host = new FakeProcessHost(platform, events, selectedPort: 28_769);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            lifecycle);
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        lifecycle.FailPublish = true;
        events.Clear();

        LocalAiRuntimeSnapshot failed = await runtime.RestartAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, failed.State);
        Assert.Equal(LocalAiOwnership.None, failed.Ownership);
        Assert.Null(failed.ProcessId);
        Assert.True(host.Process!.HasExited);
        Assert.Equal("Local AI restart did not complete.", failed.Detail);
        Assert.Equal(
            [
                "quiesce:EndpointCycle",
                "stop",
                "quiesce:EndpointCycle",
                "start",
                "probe:28769",
                "publish:28769",
                "quiesce:Teardown",
                "quiesce:Teardown",
                "stop",
            ],
            events);
    }

    [Fact]
    public async Task RestartAsync_CancellationAfterEndpointCycleStopStillCompletesTeardown()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        using var cancellation = new CancellationTokenSource();
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_769);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            new FakeLifecycle(events));
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        host.Process!.AfterStop = cancellation.Cancel;
        events.Clear();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runtime.RestartAsync(cancellation.Token));

        Assert.Equal(LocalAiRuntimeState.Failed, runtime.Snapshot.State);
        Assert.Equal(LocalAiOwnership.None, runtime.Snapshot.Ownership);
        Assert.Equal(
            ["quiesce:EndpointCycle", "stop", "quiesce:Teardown"],
            events);
    }

    [Fact]
    public async Task RestartAsync_NotInstalledOutcomeWithdrawsRetainedRoute()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_769);
        await using var runtime = CreateRuntime(
            paths,
            host,
            platform,
            new FakeClient(events),
            new FakeLifecycle(events));
        LocalAiRuntimeSnapshot started = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, started.State);
        File.Delete(paths.ManifestPath);
        events.Clear();

        LocalAiRuntimeSnapshot restarted = await runtime.RestartAsync();

        Assert.Equal(LocalAiRuntimeState.NotInstalled, restarted.State);
        Assert.Equal(["quiesce:EndpointCycle", "stop", "quiesce:Teardown"], events);
    }

    [Fact]
    public async Task PublishFailure_StopsChildAndLeavesEndpointConsumerQuiesced()
    {
        using var temp = new TempDirectory("local-ai-port-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var host = new FakeProcessHost(platform, events, selectedPort: 28_767);
        var lifecycle = new FakeLifecycle(events) { FailPublish = true };
        await using var runtime = CreateRuntime(paths, host, platform, new FakeClient(events), lifecycle);

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, snapshot.State);
        Assert.Equal(1, host.Process!.StopCount);
        Assert.Equal(["quiesce:EndpointCycle", "start", "probe:28767", "publish:28767", "quiesce:Teardown", "stop"], events);

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

    [Fact]
    public async Task FailedFirstStartCannotBePublishedByReconnect()
    {
        using var temp = new TempDirectory("local-ai-failed-use-");
        var paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events) { FailPublish = true };
        var host = new FakeProcessHost(platform, events, selectedPort: 28_767);
        await using var runtime = CreateRuntime(paths, host, platform, new FakeClient(events), lifecycle);
        Assert.Equal(LocalAiRuntimeState.Failed, (await runtime.EnsureStartedAsync()).State);
        Assert.False(lifecycle.AutomaticRecoveryEnabled);
        lifecycle.FailPublish = false;
        events.Clear();
        await runtime.ResumeAsync();
        Assert.Empty(events);
        await runtime.ReconcileStoppedAsync();
        Assert.Equal(["quiesce:Teardown"], events);
    }

    [Fact]
    public async Task StoppedReconciliationNeverStartsAndCannotUndoNewExplicitStart()
    {
        using var temp = new TempDirectory("local-ai-stopped-recovery-");
        var paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events);
        await lifecycle.SetAutomaticRecoveryEnabledAsync(false);
        await using var runtime = CreateRuntime(paths,
            new FakeProcessHost(platform, events, selectedPort: 28_767), platform, new FakeClient(events), lifecycle);
        Assert.Equal(LocalAiRuntimeState.Stopped, (await runtime.ReconcileStoppedAsync()).State);
        Assert.Equal(["quiesce:Teardown"], events);
        Assert.Equal(LocalAiRuntimeState.Healthy, (await runtime.EnsureStartedAsync()).State);
        events.Clear();
        Assert.Equal(LocalAiRuntimeState.Healthy, (await runtime.ReconcileStoppedAsync()).State);
        Assert.Empty(events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownWithdrawsBeforeStopping(bool fail)
    {
        using var temp = new TempDirectory("local-ai-shutdown-");
        var paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events);
        var runtime = CreateRuntime(paths, new FakeProcessHost(platform, events, selectedPort: 28_767),
            platform, new FakeClient(events), lifecycle);
        Assert.Equal(LocalAiRuntimeState.Healthy, (await runtime.EnsureStartedAsync()).State);
        lifecycle.FailQuiesce = fail;
        events.Clear();
        await runtime.DisposeAsync();
        Assert.Equal(["quiesce:Teardown", "stop"], events);
        Assert.True(lifecycle.AutomaticRecoveryEnabled);
    }

    [Fact]
    public async Task HealthyStartCompletesPublicationInsideGateBeforeQueuedStop()
    {
        using var temp = new TempDirectory("local-ai-publication-gate-");
        var paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events);
        await using var runtime = CreateRuntime(paths,
            new FakeProcessHost(platform, events, selectedPort: 28_767), platform, new FakeClient(events), lifecycle);
        await runtime.EnsureStartedAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifecycle.CompleteStartHandler = async ct =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return LocalAiEndpointLifecycleResult.Ok();
        };
        var usingHealthy = runtime.EnsureStartedAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = runtime.StopAsync();
        Assert.False(stopping.IsCompleted);
        Assert.False(lifecycle.AutomaticRecoveryEnabled);
        release.SetResult();
        Assert.Equal(LocalAiRuntimeState.Healthy, (await usingHealthy).State);
        Assert.Equal(LocalAiRuntimeState.Stopped, (await stopping).State);
        Assert.False(lifecycle.AutomaticRecoveryEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedStartAdmissionPreservesPreviouslyRunningIntent(bool restart)
    {
        using var temp = new TempDirectory("local-ai-start-admission-");
        var paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events);
        await using var runtime = CreateRuntime(paths,
            new FakeProcessHost(platform, events, selectedPort: 28_767), platform, new FakeClient(events), lifecycle);
        await runtime.EnsureStartedAsync();
        lifecycle.PrepareStartHandler = _ => throw new InvalidOperationException("Gateway temporarily offline.");
        events.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => restart ? runtime.RestartAsync() : runtime.EnsureStartedAsync());
        Assert.True(lifecycle.AutomaticRecoveryEnabled);
        Assert.Equal(LocalAiRuntimeState.Healthy, runtime.Snapshot.State);
        Assert.Empty(events);
        Assert.Equal(LocalAiRuntimeState.Healthy, (await runtime.ReconcileStoppedAsync()).State);
        Assert.Empty(events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedStartCompletionRequiresExplicitRetryRatherThanRefresh(bool restart)
    {
        using var temp = new TempDirectory("local-ai-port-");
        var paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events);
        await using var runtime = CreateRuntime(paths,
            new FakeProcessHost(platform, events, selectedPort: 28_767), platform, new FakeClient(events), lifecycle);
        await runtime.EnsureStartedAsync();
        lifecycle.CompleteStartHandler = _ => Task.FromResult(
            LocalAiEndpointLifecycleResult.Failed("Publication outcome needs reconciliation."));

        var failed = await (restart ? runtime.RestartAsync() : runtime.EnsureStartedAsync());
        Assert.Equal(LocalAiRuntimeState.Failed, failed.State);
        Assert.True(failed.GatewayRouteRequiresResolution);
        Assert.False(lifecycle.AutomaticRecoveryEnabled);
        events.Clear();
        Assert.Equal(failed, await runtime.RefreshAsync());
        Assert.Equal(failed, await runtime.ResumeAsync());
        Assert.Empty(events);

        lifecycle.CompleteStartHandler = null;
        Assert.Equal(LocalAiRuntimeState.Healthy, (await runtime.EnsureStartedAsync()).State);
        Assert.True(lifecycle.AutomaticRecoveryEnabled);
    }

    [Theory]
    [InlineData("start-publication")]
    [InlineData("restart-publication")]
    [InlineData("restart-withdrawal")]
    [InlineData("restart-cancellation")]
    [InlineData("restart-exception")]
    public async Task AdmittedStartFailureWithRetainedListenerCannotRepublishThroughRefresh(string failure)
    {
        using var temp = new TempDirectory("local-ai-failed-intent-");
        var paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events);
        var host = new FakeProcessHost(platform, events, selectedPort: 28_767);
        await using var runtime = CreateRuntime(paths, host, platform, new FakeClient(events), lifecycle);
        if (failure != "start-publication")
            Assert.Equal(LocalAiRuntimeState.Healthy, (await runtime.EnsureStartedAsync()).State);
        if (failure.EndsWith("publication", StringComparison.Ordinal))
            lifecycle.PublishHandler = (_, _) =>
            {
                lifecycle.FailQuiesce = true;
                return Task.FromResult(LocalAiEndpointLifecycleResult.Failed("Publication failed."));
            };
        else if (failure == "restart-withdrawal")
            lifecycle.FailQuiesce = true;
        else
            lifecycle.CompleteStartHandler = _ =>
            {
                lifecycle.FailQuiesce = true;
                return Task.FromException<LocalAiEndpointLifecycleResult>(
                    failure == "restart-cancellation" ? new OperationCanceledException() : new IOException("Completion failed."));
            };

        if (failure is "restart-cancellation" or "restart-exception")
            await Assert.ThrowsAnyAsync<Exception>(() => runtime.RestartAsync());
        else
            Assert.Equal(LocalAiRuntimeState.Failed,
                (await (failure == "start-publication" ? runtime.EnsureStartedAsync() : runtime.RestartAsync())).State);
        Assert.False(host.Process!.HasExited);
        Assert.False(lifecycle.AutomaticRecoveryEnabled);
        Assert.True(runtime.Snapshot.GatewayRouteRequiresResolution);
        lifecycle.FailQuiesce = false;
        lifecycle.PublishHandler = null;
        lifecycle.CompleteStartHandler = null;
        var failed = runtime.Snapshot;
        events.Clear();
        Assert.Equal(failed, await runtime.RefreshAsync());
        Assert.Equal(failed, await runtime.ResumeAsync());
        Assert.Empty(events);
        Assert.False(lifecycle.AutomaticRecoveryEnabled);
        var teardown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifecycle.QuiesceHandler = (_, reason, _) =>
        {
            if (reason == LocalAiQuiesceReason.Teardown) teardown.TrySetResult();
            return Task.FromResult(LocalAiEndpointLifecycleResult.Ok());
        };
        var process = host.Process!;
        process.MarkExited();
        platform.Listeners.Clear();
        host.LastExitCallback!(new LocalAiManagedProcessExit(process.ProcessId, process.StartedAtUtc, 1));
        await teardown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.RefreshAsync();
        Assert.DoesNotContain("start", events);
        Assert.DoesNotContain(events, item => item.StartsWith("publish:", StringComparison.Ordinal));
        Assert.False(lifecycle.AutomaticRecoveryEnabled);
        Assert.Equal(LocalAiRuntimeState.Stopped, (await runtime.ReconcileStoppedAsync()).State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingNativeStartCompletionInterruptionRetainsDisabledReceiptAndExplicitRecovery(bool cancel)
    {
        using var temp = new TempDirectory("local-ai-completion-interrupted-");
        var paths = await PrepareInstallAsync(temp);
        var store = new LocalAiNativeBindingStore(paths);
        store.Save(new("native", "endpoint", "identity", "llamacpp/model", "cloud/model", "hash", false));
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events) { BindingStore = store };
        var host = new FakeProcessHost(platform, events, selectedPort: 28_767);
        await using var runtime = CreateRuntime(paths, host, platform, new FakeClient(events), lifecycle);
        Assert.Equal(LocalAiRuntimeState.Healthy, (await runtime.EnsureStartedAsync()).State);
        using var cancellation = new CancellationTokenSource();
        lifecycle.CompleteStartHandler = ct =>
        {
            Assert.False(store.Load()!.AutomaticRecoveryEnabled);
            if (cancel)
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            throw new IOException("Completion connection dropped.");
        };
        await Assert.ThrowsAnyAsync<Exception>(() => runtime.EnsureStartedAsync(cancellation.Token));
        Assert.Equal(LocalAiRuntimeState.Failed, runtime.Snapshot.State);
        Assert.Equal(LocalAiOwnership.CompanionManaged, runtime.Snapshot.Ownership);
        Assert.True(runtime.Snapshot.GatewayRouteRequiresResolution);
        Assert.False(host.Process!.HasExited);
        Assert.False(store.Load()!.AutomaticRecoveryEnabled);
        var failed = runtime.Snapshot;
        events.Clear();
        Assert.Equal(failed, await runtime.RefreshAsync());
        Assert.Equal(failed, await runtime.ResumeAsync());
        Assert.Empty(events);
        lifecycle.CompleteStartHandler = null;
        Assert.Equal(LocalAiRuntimeState.Healthy, (await runtime.EnsureStartedAsync()).State);
        Assert.True(store.Load()!.AutomaticRecoveryEnabled);
    }

    [Fact]
    public async Task AdmittedStartWithUnreadyRetainedListenerSettlesAsRetryableFailure()
    {
        using var temp = new TempDirectory("local-ai-unready-intent-");
        var paths = await PrepareInstallAsync(temp);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events);
        var client = new FakeClient(events, (path, count) => count == 2
            ? new(false, LocalAiModelAvailabilityState.Unknown, null, "Temporarily unready.")
            : new(true, LocalAiModelAvailabilityState.Verified, path, null));
        await using var runtime = CreateRuntime(paths,
            new FakeProcessHost(platform, events, selectedPort: 28_767), platform, client, lifecycle);
        Assert.Equal(LocalAiRuntimeState.Healthy, (await runtime.EnsureStartedAsync()).State);
        var failed = await runtime.EnsureStartedAsync();
        Assert.Equal(LocalAiRuntimeState.Failed, failed.State);
        Assert.Equal(runtime.Snapshot, failed);
        Assert.True(failed.GatewayRouteRequiresResolution);
        Assert.False(lifecycle.AutomaticRecoveryEnabled);
        events.Clear();
        Assert.Equal(failed, await runtime.RefreshAsync());
        Assert.Empty(events);
        Assert.Equal(LocalAiRuntimeState.Healthy, (await runtime.EnsureStartedAsync()).State);
        Assert.True(lifecycle.AutomaticRecoveryEnabled);
    }

    [Fact]
    public async Task CleanNotInstalledStartStillAllowsReadOnlyRefreshAfterInstallation()
    {
        using var temp = new TempDirectory("local-ai-clean-failure-");
        var paths = new LocalAiPaths(temp.Path);
        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        var lifecycle = new FakeLifecycle(events);
        await using var runtime = CreateRuntime(paths,
            new FakeProcessHost(platform, events, selectedPort: 28_767), platform, new FakeClient(events), lifecycle);
        Assert.Equal(LocalAiRuntimeState.NotInstalled, (await runtime.EnsureStartedAsync()).State);
        await PrepareInstallAsync(temp);
        events.Clear();
        Assert.Equal(LocalAiRuntimeState.Stopped, (await runtime.RefreshAsync()).State);
        Assert.Equal(LocalAiRuntimeState.Stopped, (await runtime.ResumeAsync()).State);
        Assert.Empty(events);
        Assert.False(lifecycle.AutomaticRecoveryEnabled);
    }

    [Fact]
    public async Task QueuedCrashAfterIncompleteStartDoesNotScheduleRecovery()
    {
        using var temp = new TempDirectory("local-ai-queued-crash-");
        var paths = await PrepareInstallAsync(temp);
        var manifest = await File.ReadAllTextAsync(paths.ManifestPath);
        var events = new SynchronizedEventLog();
        var delayCalls = 0;
        var platform = new FakePlatform
        {
            AfterDelay = () =>
            {
                Interlocked.Increment(ref delayCalls);
                File.WriteAllText(paths.ManifestPath, manifest);
            },
        };
        var lifecycle = new FakeLifecycle(events);
        var host = new FakeProcessHost(platform, events, selectedPort: 28_767);
        await using var runtime = CreateRuntime(paths, host, platform, new FakeClient(events), lifecycle);
        await runtime.EnsureStartedAsync();
        var crashObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.StateChanged += (_, args) =>
        {
            if (args.Snapshot.State == LocalAiRuntimeState.Failed &&
                args.Snapshot.Ownership == LocalAiOwnership.None)
                crashObserved.TrySetResult();
        };
        lifecycle.PrepareStartHandler = _ =>
        {
            File.Delete(paths.ManifestPath);
            var process = host.Process!;
            process.MarkExited();
            platform.Listeners.Clear();
            host.LastExitCallback!(new LocalAiManagedProcessExit(process.ProcessId, process.StartedAtUtc, 1));
            return Task.CompletedTask;
        };
        events.Clear();
        Assert.Equal(LocalAiRuntimeState.NotInstalled, (await runtime.EnsureStartedAsync()).State);
        await crashObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.DisposeAsync();
        Assert.Equal(0, delayCalls);
        Assert.DoesNotContain("start", events);
        Assert.DoesNotContain(events, item => item.StartsWith("publish:", StringComparison.Ordinal));
        Assert.False(lifecycle.AutomaticRecoveryEnabled);
    }

    private static LlamaServerRuntimeService CreateRuntime(
        LocalAiPaths paths,
        FakeProcessHost host,
        FakePlatform platform,
        ILlamaServerClient client,
        ILocalAiEndpointLifecycle lifecycle,
        TimeSpan? startupTimeout = null,
        int maxRestartAttempts = 2,
        TimeSpan? shutdownTimeout = null,
        ILocalAiModelFileVerifier? modelFileVerifier = null,
        Func<string>? getApiKey = null,
        Func<LocalAiResolvedInstall, int?>? getRecoveryPort = null,
        Func<string, string>? resolveChildProcessPath = null) => new(
            new LlamaServerRuntimeOptions
            {
                Paths = paths,
                EndpointLifecycle = lifecycle,
                GetApiKey = getApiKey,
                GetRecoveryPort = getRecoveryPort,
                ResolveChildProcessPath = resolveChildProcessPath ?? (static path => path),
                HealthPollInterval = TimeSpan.FromMilliseconds(1),
                StartupTimeout = startupTimeout ?? TimeSpan.FromSeconds(1),
                ShutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(10),
                RestartDelay = TimeSpan.Zero,
                MaxRestartAttempts = maxRestartAttempts,
            },
            NullLogger.Instance,
            host,
            platform,
            client,
            modelFileVerifier);

    private static async Task<LocalAiPaths> PrepareInstallAsync(TempDirectory temp)
    {
        var paths = new LocalAiPaths(temp.Path);
        LocalAiInstallManifest manifest = ValidManifest();
        string executable = paths.ResolveContainedPath(manifest.ExecutablePath, nameof(manifest.ExecutablePath));
        string model = paths.ResolveContainedPath(manifest.ModelPath, nameof(manifest.ModelPath));
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        await File.WriteAllTextAsync(executable, "test executable");
        await File.WriteAllTextAsync(
            Path.Combine(
                Path.GetDirectoryName(executable)!,
                LlamaRuntimeCatalog.ServerImplementationLibraryName),
            "test implementation library");
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

    private static LlamaServerRouterProbeResult ReadyProbe(string expectedModelPath) => new(
        true,
        LocalAiModelAvailabilityState.Verified,
        expectedModelPath,
        "The managed model is ready.");

    /// <summary>
    /// A managed Qwen3.5 9B install predates the profile-aware catalog and is no
    /// longer offered for new installs. Upgrading must not strand it: its own
    /// pinned receipt has to keep resolving and launching unchanged.
    /// </summary>
    [Fact]
    public async Task Router_LaunchesRetiredQwen9BInstallAfterUpgrade()
    {
        using var temp = new TempDirectory("local-ai-legacy-model-");
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        await store.SaveAsync(LegacyQwen9BManifest());
        JsonObject legacyJson = (JsonNode.Parse(await File.ReadAllTextAsync(paths.ManifestPath)) as JsonObject)!;
        legacyJson.Remove("keyCachePrecision");
        legacyJson.Remove("valueCachePrecision");
        legacyJson.Remove("draftKeyCachePrecision");
        legacyJson.Remove("draftValueCachePrecision");
        await File.WriteAllTextAsync(paths.ManifestPath, legacyJson.ToJsonString());

        LocalAiResolvedInstall saved = (await store.LoadAsync())!;
        LlamaServerRouterLaunchPlan launch = LlamaServerRouterConfiguration.Build(paths, saved);

        Assert.Equal(LocalModelCatalog.Qwen9BModelId, launch.ModelAlias);
        Assert.Contains("ctx-size = 262144", launch.PresetContent.Split(Environment.NewLine));
        Assert.Contains("cache-type-k = f16", launch.PresetContent.Split(Environment.NewLine));
        Assert.Contains("cache-type-v = f16", launch.PresetContent.Split(Environment.NewLine));
        Assert.Equal(
            $"llamacpp/{LocalModelCatalog.Qwen9BModelId}",
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(saved));
    }

    /// <summary>
    /// The retired entry is a compatibility shim only. It must never be offered,
    /// recommended, or selectable for a new install.
    /// </summary>
    [Fact]
    public void Catalog_DoesNotOfferRetiredQwen9BForNewInstalls()
    {
        Assert.DoesNotContain(
            LocalModelCatalog.Models,
            model => string.Equals(model.Id, LocalModelCatalog.Qwen9BModelId, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            LocalModelCatalog.ExplicitAlternatives,
            model => string.Equals(model.Id, LocalModelCatalog.Qwen9BModelId, StringComparison.OrdinalIgnoreCase));
        Assert.Null(LocalModelCatalog.Find(LocalModelCatalog.Qwen9BModelId));
        Assert.NotNull(LocalModelCatalog.FindInstalled(LocalModelCatalog.Qwen9BModelId));
        Assert.True(LocalModelCatalog.IsLegacy(LocalModelCatalog.Qwen9BModelId));
        Assert.False(LocalModelCatalog.IsLegacy(LocalModelCatalog.Qwen38_27BModelId));
    }

    /// <summary>
    /// Compatibility must not become silent remapping: a retired model receipt
    /// that claims a context or KV profile it could never have been installed
    /// with still has to fail receipt validation.
    /// </summary>
    [Fact]
    public async Task Router_RejectsRetiredQwen9BReceiptWithUnsupportedProfile()
    {
        using var temp = new TempDirectory("local-ai-legacy-profile-");
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        await store.SaveAsync(LegacyQwen9BManifest() with
        {
            KeyCachePrecision = KvCachePrecision.Q8_0,
            ValueCachePrecision = KvCachePrecision.Q8_0,
            DraftKeyCachePrecision = KvCachePrecision.Q8_0,
            DraftValueCachePrecision = KvCachePrecision.Q8_0,
        });

        LocalAiResolvedInstall saved = (await store.LoadAsync())!;

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => LlamaServerRouterConfiguration.Build(paths, saved));
        Assert.Contains("qualified catalog profile", error.Message, StringComparison.Ordinal);
    }

    private static LocalAiInstallManifest LegacyQwen9BManifest()
    {
        LlamaRuntimeVariant runtime = LlamaRuntimeCatalog.Find(
            System.Runtime.InteropServices.Architecture.Arm64)!;
        return ValidManifest() with
        {
            ModelCatalogId = LocalModelCatalog.Qwen9BModelId,
            ModelPath = Path.Combine("models", "Qwen3.5-9B-Q4_K_M.gguf"),
            ModelId = "unsloth/Qwen3.5-9B-MTP-GGUF@9716a636ee4bddc3fed678220b7a33dd2a4160ae",
            ModelAlias = LocalModelCatalog.Qwen9BModelId,
            ModelAsset = new LocalAiAssetReceipt
            {
                FileName = "Qwen3.5-9B-Q4_K_M.gguf",
                SourceUrl = "https://huggingface.co/unsloth/Qwen3.5-9B-MTP-GGUF/resolve/" +
                    "9716a636ee4bddc3fed678220b7a33dd2a4160ae/Qwen3.5-9B-Q4_K_M.gguf?download=true",
                SizeBytes = 5_868_826_976,
                Sha256 = "e8dd94817e95d6c0939102049d068418269978377b13616c4726235e232841fe",
            },
            RuntimeAssets = runtime.Artifacts.Select(artifact => new LocalAiAssetReceipt
            {
                FileName = Path.GetFileName(artifact.RelativePath),
                SourceUrl = artifact.DownloadUri.AbsoluteUri,
                SizeBytes = artifact.SizeBytes,
                Sha256 = artifact.Sha256.Value,
            }).ToImmutableArray(),
            ContextLength = LocalModelCatalog.NativeContextTokens,
            KeyCachePrecision = KvCachePrecision.F16,
            ValueCachePrecision = KvCachePrecision.F16,
            DraftKeyCachePrecision = KvCachePrecision.F16,
            DraftValueCachePrecision = KvCachePrecision.F16,
        };
    }

    /// <summary>
    /// A managed install recorded before the llama-server runtime bump must keep
    /// launching against its own pinned receipt. Updating the app must not strand an
    /// installed model until a separate setup repair runs.
    /// </summary>
    [Fact]
    public async Task Router_LaunchesRetiredRuntimeInstallAfterVersionBump()
    {
        using var temp = new TempDirectory("local-ai-legacy-runtime-");
        var paths = new LocalAiPaths(temp.Path);
        LlamaRuntimeVariant retired = LlamaRuntimeCatalog.FindInstalled("b10655-cuda13-arm64")!;
        LocalAiInstallManifest manifest = ValidManifest() with
        {
            EngineVersion = retired.ReleaseTag,
            RuntimeId = retired.Id,
            ExecutablePath = Path.Combine(
                "engines",
                $"llama-{retired.ReleaseTag}",
                LlamaRuntimeCatalog.ServerExecutableName),
            RuntimeAssets = retired.Artifacts.Select(artifact => new LocalAiAssetReceipt
            {
                FileName = Path.GetFileName(artifact.RelativePath),
                SourceUrl = artifact.DownloadUri.AbsoluteUri,
                SizeBytes = artifact.SizeBytes,
                Sha256 = artifact.Sha256.Value,
            }).ToImmutableArray(),
        };
        var store = new LocalAiManifestStore(paths);
        await store.SaveAsync(manifest);

        LocalAiResolvedInstall saved = (await store.LoadAsync())!;
        LlamaServerRouterLaunchPlan launch = LlamaServerRouterConfiguration.Build(paths, saved);

        Assert.NotEqual(LlamaRuntimeCatalog.ReleaseTag, retired.ReleaseTag);
        Assert.Equal("qwen3.6-35b-a3b-mtp-q4-k-m", launch.ModelAlias);
    }

    /// <summary>
    /// Schema-5 extra assets are loaded natively by llama-server from the shared,
    /// user-writable hub cache. One that no longer matches its pinned digest must
    /// stop startup, exactly like a tampered primary model does.
    /// </summary>
    [Fact]
    public async Task Startup_FailsWhenAnAdditionalModelAssetNoLongerMatchesItsReceipt()
    {
        using var temp = new TempDirectory("local-ai-tampered-asset-");
        LocalAiPaths paths = await PrepareInstallAsync(temp);
        var store = new LocalAiManifestStore(paths);
        LocalAiResolvedInstall installed = (await store.LoadAsync())!;
        string cacheRoot = temp.Combine("hf-cache");
        const string draftRepo = "z-lab/Qwen3.8-27B-DFlash2-GGUF";
        string draftRevision = new('c', 40);
        Assert.True(HuggingFaceHubCache.TryGetSnapshotPaths(
            cacheRoot, draftRepo, draftRevision, "draft.gguf",
            out string draftPath, out _, out string error), error);
        // The primary model's cached path must be the real hub-cache snapshot path
        // for its own repository and revision, or the receipt fails validation
        // before the additional-asset check under test is ever reached.
        Assert.True(HuggingFaceHubCache.TryGetSnapshotPaths(
            cacheRoot,
            "unsloth/Qwen3.6-35B-A3B-MTP-GGUF",
            "5bc3e238d916f48a861bac2f8a1990a0e9b7e98d",
            "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf",
            out string cachedPrimaryPath, out _, out error), error);
        Directory.CreateDirectory(Path.GetDirectoryName(cachedPrimaryPath)!);
        await File.WriteAllTextAsync(cachedPrimaryPath, "primary");
        Directory.CreateDirectory(Path.GetDirectoryName(draftPath)!);
        await File.WriteAllTextAsync(draftPath, "draft");
        LocalAiInstallManifest schemaFive = installed.Manifest with
        {
            SchemaVersion = LocalAiInstallManifest.AdditionalAssetsSchemaVersion,
            ModelCacheRoot = cacheRoot,
            CachedModelPath = cachedPrimaryPath,
            AdditionalModelAssets = ImmutableArray.Create(new LocalAiAssetReceipt
            {
                FileName = "draft.gguf",
                SourceUrl = $"https://huggingface.co/{draftRepo}/resolve/{draftRevision}/draft.gguf?download=true",
                SizeBytes = 1_143_006_816,
                Sha256 = new string('d', 64),
            }),
            AdditionalModelPaths = ImmutableArray.Create(draftPath),
        };

        var events = new SynchronizedEventLog();
        var platform = new FakePlatform();
        await using var runtime = CreateRuntime(
            paths,
            new FakeProcessHost(platform, events, selectedPort: 28_771),
            platform,
            new FakeClient(events),
            new FakeLifecycle(events),
            modelFileVerifier: new SelectiveModelFileVerifier(
                cachedPrimaryPath,
                rejectPath: draftPath));
        await store.SaveAsync(schemaFive);

        LocalAiRuntimeSnapshot snapshot = await runtime.EnsureStartedAsync();

        Assert.Equal(LocalAiRuntimeState.Failed, snapshot.State);
        Assert.Contains("draft.gguf", snapshot.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>Verifies the primary model but rejects one named additional asset.</summary>
    private sealed class SelectiveModelFileVerifier(string resolvedPath, string rejectPath)
        : ILocalAiModelFileVerifier
    {
        public Task<LocalAiVerifiedModelLease?> TryOpenAsync(
            string cacheRoot,
            string candidatePath,
            long expectedSizeBytes,
            Sha256Digest expectedSha256,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(candidatePath, rejectPath, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<LocalAiVerifiedModelLease?>(null);
            return Task.FromResult<LocalAiVerifiedModelLease?>(
                new LocalAiVerifiedModelLease(new MemoryStream(), resolvedPath));
        }
    }

    private static LocalAiInstallManifest ValidManifest()
    {
        LlamaRuntimeVariant runtime = LlamaRuntimeCatalog.Find(
            System.Runtime.InteropServices.Architecture.Arm64)!;
        return new LocalAiInstallManifest
        {
            EngineVersion = LlamaRuntimeCatalog.ReleaseTag,
            Architecture = "arm64",
            RuntimeId = runtime.Id,
            ModelCatalogId = "qwen3.6-35b-a3b-mtp-q4-k-m",
            SelectedGpuId = "GPU-01234567-89ab-cdef-0123-456789abcdef",
            ExecutablePath = Path.Combine(
                "engines",
                $"llama-{LlamaRuntimeCatalog.ReleaseTag}",
                LlamaRuntimeCatalog.ServerExecutableName),
            RuntimeAssets = runtime.Artifacts.Select(artifact => new LocalAiAssetReceipt
            {
                FileName = Path.GetFileName(artifact.RelativePath),
                SourceUrl = artifact.DownloadUri.AbsoluteUri,
                SizeBytes = artifact.SizeBytes,
                Sha256 = artifact.Sha256.Value,
            }).ToImmutableArray(),
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
            KeyCachePrecision = KvCachePrecision.Q8_0,
            ValueCachePrecision = KvCachePrecision.Q8_0,
            DraftKeyCachePrecision = KvCachePrecision.Q8_0,
            DraftValueCachePrecision = KvCachePrecision.Q8_0,
            InstalledAtUtc = DateTimeOffset.Parse("2026-08-18T12:00:00Z"),
        };
    }

    private sealed class SynchronizedEventLog : IReadOnlyCollection<string>
    {
        private readonly Lock _gate = new();
        private readonly List<string> _events = [];

        public int Count
        {
            get
            {
                lock (_gate)
                    return _events.Count;
            }
        }

        public void Add(string value)
        {
            lock (_gate)
                _events.Add(value);
        }

        public void Clear()
        {
            lock (_gate)
                _events.Clear();
        }

        public IEnumerator<string> GetEnumerator()
        {
            lock (_gate)
                return ((IEnumerable<string>)_events.ToArray()).GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class FakePlatform : ILlamaServerRuntimePlatform
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.Parse("2026-08-18T12:00:00Z");
        public List<WindowsTcpListenerInfo> Listeners { get; } = [];
        public bool Ipv4Complete { get; set; } = true;
        public Action? AfterCapture { get; init; }
        public Action? AfterDelay { get; init; }

        public WindowsTcpListenerSnapshotResult CaptureListeners()
        {
            WindowsTcpListenerSnapshotResult result =
                new([.. Listeners], Ipv4Complete, Ipv6Complete: true);
            AfterCapture?.Invoke();
            return result;
        }

        public bool YieldOnDelay { get; init; }

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (YieldOnDelay)
                await Task.Yield();
            AfterDelay?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            UtcNow += delay;
        }
    }

    private sealed class FakeProcessHost(
        FakePlatform platform,
        SynchronizedEventLog events,
        int selectedPort,
        TimeSpan? listenerStartOffset = null,
        IPAddress? listenerAddress = null) : ILocalAiManagedProcessHost
    {
        public LocalAiProcessStartSpec? LastSpec { get; private set; }
        public FakeProcess? Process { get; private set; }
        public Action<LocalAiManagedProcessExit>? LastExitCallback { get; private set; }

        public bool SuppressListener { get; init; }

        public bool ThrowOnStart { get; init; }

        public bool ImmediateExit { get; init; }

        public Exception? InitialStopException { get; init; }

        public Action<LocalAiProcessStartSpec>? BeforeStart { get; init; }

        public Task<ILocalAiManagedProcess> StartProcessAsync(
            LocalAiProcessStartSpec spec,
            Action<LocalAiManagedProcessExit> exited,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnStart)
                throw new IOException("The managed llama-server process could not be launched.");
            LastSpec = spec;
            BeforeStart?.Invoke(spec);
            events.Add("start");
            LastExitCallback = exited;
            Process = new FakeProcess(4201, platform.UtcNow, platform, events)
            {
                StopException = InitialStopException,
            };
            if (ImmediateExit)
            {
                Process.MarkExited();
                exited(new LocalAiManagedProcessExit(Process.ProcessId, Process.StartedAtUtc, 1));
                return Task.FromResult<ILocalAiManagedProcess>(Process);
            }
            if (SuppressListener)
                return Task.FromResult<ILocalAiManagedProcess>(Process);
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
        SynchronizedEventLog events) : ILocalAiManagedProcess
    {
        public int ProcessId { get; } = processId;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        public bool HasExited { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        public Action? AfterStop { get; set; }
        public Exception? StopException { get; set; }

        public void MarkExited() => HasExited = true;

        public Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("stop");
            StopCount++;
            if (StopException is not null)
                throw StopException;
            HasExited = true;
            platform.Listeners.Clear();
            AfterStop?.Invoke();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeClient(
        SynchronizedEventLog events,
        Func<string, int, LlamaServerRouterProbeResult>? probeFactory = null) : ILlamaServerClient
    {
        private int _probeCount;

        public List<int> ProbedPorts { get; } = [];

        public bool WaitForCancellation { get; init; }

        public TaskCompletionSource ProbeEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LlamaServerRouterProbeResult> ProbeManagedModelAsync(
            Uri endpoint,
            string modelAlias,
            string expectedModelPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeEntered.TrySetResult();
            if (WaitForCancellation)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            ProbedPorts.Add(endpoint.Port);
            events.Add($"probe:{endpoint.Port}");
            int probeNumber = ++_probeCount;
            return probeFactory?.Invoke(expectedModelPath, probeNumber) ?? new LlamaServerRouterProbeResult(
                true,
                LocalAiModelAvailabilityState.Verified,
                expectedModelPath,
                null);
        }

        public void Dispose() { }
    }

    private sealed class FakeModelFileVerifier(string resolvedPath) : ILocalAiModelFileVerifier
    {
        public Task<LocalAiVerifiedModelLease?> TryOpenAsync(
            string cacheRoot,
            string candidatePath,
            long expectedSizeBytes,
            Sha256Digest expectedSha256,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<LocalAiVerifiedModelLease?>(
                new LocalAiVerifiedModelLease(new MemoryStream(), resolvedPath));
        }
    }

    private sealed class FakeLifecycle(SynchronizedEventLog events) : ILocalAiEndpointLifecycle
    {
        public LocalAiNativeBindingStore? BindingStore { get; init; }
        public Func<CancellationToken, Task>? PrepareStartHandler { get; set; }
        public Task PrepareStartAsync(LocalAiResolvedInstall install, CancellationToken ct) =>
            PrepareStartHandler?.Invoke(ct) ?? Task.CompletedTask;
        public Func<CancellationToken, Task<LocalAiEndpointLifecycleResult>>? CompleteStartHandler { get; set; }
        public Task<LocalAiEndpointLifecycleResult> CompleteStartAsync(LocalAiResolvedInstall install, CancellationToken ct) =>
            CompleteStartHandler?.Invoke(ct) ?? Task.FromResult(LocalAiEndpointLifecycleResult.Ok());
        public bool AutomaticRecoveryEnabled { get; private set; } = true;
        public List<bool> RecoveryIntents { get; } = [];
        public Task SetAutomaticRecoveryEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecoveryIntents.Add(enabled);
            AutomaticRecoveryEnabled = enabled;
            if (BindingStore?.Load() is { } binding)
                BindingStore.Save(binding with { AutomaticRecoveryEnabled = enabled });
            return Task.CompletedTask;
        }

        public bool FailPublish { get; set; }
        public bool FailQuiesce { get; set; }
        public Exception? PublishException { get; set; }
        public Exception? QuiesceException { get; set; }
        public Func<LocalAiResolvedInstall, CancellationToken, Task<LocalAiEndpointLifecycleResult>>?
            PublishHandler { get; set; }
        public List<Uri?> QuiescedEndpoints { get; } = [];
        public Func<int, LocalAiQuiesceReason, CancellationToken, Task<LocalAiEndpointLifecycleResult>>?
            QuiesceHandler { get; set; }
        private int _quiesceCount;

        public Task<LocalAiEndpointLifecycleResult> QuiesceAsync(
            LocalAiResolvedInstall install,
            LocalAiQuiesceReason reason = LocalAiQuiesceReason.Teardown,
            CancellationToken cancellationToken = default)
        {
            events.Add($"quiesce:{reason}");
            QuiescedEndpoints.Add(install.Endpoint);
            int call = ++_quiesceCount;
            if (QuiesceHandler is not null)
                return QuiesceHandler(call, reason, cancellationToken);
            if (QuiesceException is not null)
                return Task.FromException<LocalAiEndpointLifecycleResult>(QuiesceException);
            return Task.FromResult(FailQuiesce
                ? LocalAiEndpointLifecycleResult.Failed("quiesce failed")
                : LocalAiEndpointLifecycleResult.Ok());
        }

        public Task<LocalAiEndpointLifecycleResult> PublishAsync(
            LocalAiResolvedInstall install,
            CancellationToken cancellationToken = default)
        {
            events.Add($"publish:{install.Endpoint!.Port}");
            if (PublishHandler is not null)
                return PublishHandler(install, cancellationToken);
            if (PublishException is not null)
                return Task.FromException<LocalAiEndpointLifecycleResult>(PublishException);
            return Task.FromResult(FailPublish
                ? LocalAiEndpointLifecycleResult.Failed("publish failed")
                : LocalAiEndpointLifecycleResult.Ok());
        }
    }

    public enum RefreshOwnershipLoss
    {
        Incomplete,
        Conflict,
        MissingEndpoint,
    }
}
