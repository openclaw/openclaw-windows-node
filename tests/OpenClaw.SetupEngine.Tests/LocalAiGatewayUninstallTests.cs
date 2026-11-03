using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiGatewayUninstallTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recovery_ProbesPhysicalModelPathWithoutChangingReceipt(bool endpointHealthy)
    {
        using var temp = new TempDirectory("local-ai-recovery-path-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path);
        string physicalModelPath = temp.Combine("physical", "model.gguf");
        using var client = new RecordingRecoveryClient { IsHealthy = endpointHealthy };
        using var cancellation = new CancellationTokenSource();

        bool healthy = await PreserveLocalAiRecoveryGatewayStep.ProbeOriginalEndpointAsync(
            original, cancellation.Token, client, path =>
            {
                Assert.Equal(original.ModelPath, path);
                return physicalModelPath;
            });

        Assert.Equal(endpointHealthy, healthy);
        Assert.Equal(physicalModelPath, client.ExpectedModelPath);
        Assert.Equal(original.Endpoint, client.Endpoint);
        Assert.Equal(original.Manifest.ModelAlias, client.ModelAlias);
        Assert.Equal(cancellation.Token, client.CancellationToken);
        LocalAiResolvedInstall saved = (await new LocalAiManifestStore(new LocalAiPaths(temp.Path)).LoadAsync())!;
        Assert.Equal(original.ModelPath, saved.ModelPath);
        Assert.Equal(JsonSerializer.Serialize(original.Manifest), JsonSerializer.Serialize(saved.Manifest));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_ResolutionFailureDoesNotProbeLogicalPath(bool accessDenied)
    {
        using var temp = new TempDirectory("local-ai-recovery-path-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path);
        using var client = new RecordingRecoveryClient();

        bool healthy = await PreserveLocalAiRecoveryGatewayStep.ProbeOriginalEndpointAsync(
            original, CancellationToken.None, client, _ => accessDenied
                ? throw new UnauthorizedAccessException("Cannot open model path.")
                : throw new IOException("Cannot resolve model path."));

        Assert.False(healthy);
        Assert.Null(client.Endpoint);
    }

    [Fact]
    public async Task Recovery_NoOriginalEndpointDoesNotResolveOrProbe()
    {
        using var temp = new TempDirectory("local-ai-recovery-path-");
        LocalAiResolvedInstall original = (await SaveManifestAsync(temp.Path)) with { Endpoint = null };
        using var client = new RecordingRecoveryClient();

        bool healthy = await PreserveLocalAiRecoveryGatewayStep.ProbeOriginalEndpointAsync(
            original, CancellationToken.None, client,
            _ => throw new InvalidOperationException("No model path should be resolved."));

        Assert.True(healthy);
        Assert.Null(client.Endpoint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_CancellationDoesNotResolveOrProbe(bool hasEndpoint)
    {
        using var temp = new TempDirectory("local-ai-recovery-path-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path);
        if (!hasEndpoint)
            original = original with { Endpoint = null };
        using var client = new RecordingRecoveryClient();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PreserveLocalAiRecoveryGatewayStep.ProbeOriginalEndpointAsync(
                original, cancellation.Token, client,
                _ => throw new InvalidOperationException("No model path should be resolved.")));

        Assert.Null(client.Endpoint);
    }

    private sealed class RecordingRecoveryClient : ILlamaServerClient
    {
        public bool IsHealthy { get; init; } = true;
        public string? ExpectedModelPath { get; private set; }
        public Uri? Endpoint { get; private set; }
        public string? ModelAlias { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public Task<LlamaServerRouterProbeResult> ProbeManagedModelAsync(
            Uri endpoint, string modelAlias, string expectedModelPath, CancellationToken cancellationToken = default)
        {
            Endpoint = endpoint;
            ModelAlias = modelAlias;
            ExpectedModelPath = expectedModelPath;
            CancellationToken = cancellationToken;
            return Task.FromResult(new LlamaServerRouterProbeResult(
                IsHealthy, LocalAiModelAvailabilityState.Verified, expectedModelPath, null));
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task Repair_RollbackRestoresFallbackAfterRetainedEndpointCycle()
    {
        using var temp = new TempDirectory("local-ai-gateway-repair-");
        LocalAiResolvedInstall install = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        string managedPrimary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
        var commands = new GatewayStateCommandRunner(providerJson: null, managedPrimary);
        SetupContext context = CreateContext(temp.Path, commands);
        context.LocalAiResolvedInstall = install;
        context.LocalAiEligibility = LocalInferenceEligibility.Evaluate(CreateSparkHardware());

        StepResult result = await new ConfigureLocalAiGatewayStep()
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal(LocalAiGatewayProviderDefinition.BuildProviderJson(install), commands.ProviderJson);
        Assert.Equal(managedPrimary, commands.PrimaryJson);
        LocalAiResolvedInstall repaired = (await new LocalAiManifestStore(new LocalAiPaths(temp.Path)).LoadAsync())!;
        Assert.Equal("openai/gpt-5", repaired.Manifest.GatewayFallbackModel);

        await new ConfigureLocalAiGatewayStep().RollbackAsync(context, CancellationToken.None);

        Assert.Null(commands.ProviderJson);
        Assert.Equal(JsonSerializer.Serialize("openai/gpt-5"), commands.PrimaryJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Configure_BorrowedRuntimeRetainsDiscoveredFallbackThroughTeardownAndFreshStart(bool dispose)
    {
        using var temp = new TempDirectory("local-ai-fallback-handback-");
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        await LocalAiSetupRuntimeFixture.PrepareInstallAsync(paths);
        var fixture = new LocalAiSetupRuntimeFixture();
        await using var runtime = fixture.CreateRuntime(paths);
        Assert.Equal(LocalAiRuntimeState.Healthy, (await runtime.RestartForSetupAsync()).State);
        var started = (await store.LoadAsync())!;
        Assert.Null(started.Manifest.GatewayFallbackModel);
        var commands = new GatewayStateCommandRunner(null, JsonSerializer.Serialize("openai/gpt-5"));
        var context = CreateRecoveryContext(temp.Path, commands);
        context.LocalAiResolvedInstall = started;
        context.LocalAiRuntime = runtime;
        context.LocalAiRuntimeBorrowed = true;

        var configured = await new ConfigureLocalAiGatewayStep().ExecuteAsync(context, default);

        Assert.Equal(StepOutcome.Success, configured.Outcome);
        Assert.False(runtime.Snapshot.GatewayRouteRequiresResolution);
        Assert.Equal("openai/gpt-5", (await store.LoadAsync())!.Manifest.GatewayFallbackModel);
        if (dispose) await runtime.DisposeAsync();
        else Assert.Equal(LocalAiRuntimeState.Stopped, (await runtime.StopAsync()).State);
        Assert.Equal("openai/gpt-5", Assert.Single(fixture.Withdrawn).Manifest.GatewayFallbackModel);
        await runtime.DisposeAsync();

        var freshFixture = new LocalAiSetupRuntimeFixture();
        await using var fresh = freshFixture.CreateRuntime(paths);
        Assert.Equal(LocalAiRuntimeState.Healthy, (await fresh.EnsureStartedAsync()).State);
        Assert.Equal("openai/gpt-5", Assert.Single(freshFixture.Published).Manifest.GatewayFallbackModel);
        await fresh.StopAsync();
        Assert.Equal("openai/gpt-5", Assert.Single(freshFixture.Withdrawn).Manifest.GatewayFallbackModel);
    }

    [Fact]
    public async Task Acknowledge_ReconcilesEndpointDriftAndReleasesSetupOwnership()
    {
        using var temp = new TempDirectory("local-ai-handback-endpoint-");
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        await LocalAiSetupRuntimeFixture.PrepareInstallAsync(paths);
        var fixture = new LocalAiSetupRuntimeFixture();
        await using var runtime = fixture.CreateRuntime(paths);
        var healthy = await runtime.RestartForSetupAsync();
        Assert.Equal(LocalAiRuntimeState.Healthy, healthy.State);
        Assert.True(healthy.GatewayRouteRequiresResolution);
        var started = (await store.LoadAsync())!;
        const string driftedEndpoint = "http://127.0.0.1:28770/v1";
        Assert.NotEqual(driftedEndpoint, healthy.Endpoint!.AbsoluteUri);
        await store.SaveAsync(started.Manifest with { Endpoint = driftedEndpoint });
        Assert.Equal(driftedEndpoint, (await store.LoadAsync())!.Manifest.Endpoint);

        var acknowledged = await runtime.AcknowledgeSetupGatewayRouteAsync();

        Assert.Equal(LocalAiRuntimeState.Healthy, acknowledged.State);
        Assert.Equal(healthy.Endpoint, acknowledged.Endpoint);
        Assert.False(acknowledged.GatewayRouteRequiresResolution);
        Assert.False(runtime.Snapshot.GatewayRouteRequiresResolution);
        var reconciled = (await store.LoadAsync())!;
        Assert.Equal(healthy.Endpoint, reconciled.Endpoint);
        Assert.Equal(JsonSerializer.Serialize(started.Manifest), JsonSerializer.Serialize(reconciled.Manifest));
        Assert.Equal(LocalAiRuntimeState.Stopped, (await runtime.StopAsync()).State);
        var withdrawn = Assert.Single(fixture.Withdrawn);
        Assert.Equal(healthy.Endpoint, withdrawn.Endpoint);
        Assert.Equal(JsonSerializer.Serialize(reconciled.Manifest), JsonSerializer.Serialize(withdrawn.Manifest));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("model")]
    [InlineData("provenance")]
    public async Task Acknowledge_RejectsChangedReceiptWithoutReleasingSetupOwnership(string change)
    {
        using var temp = new TempDirectory("local-ai-handback-fence-");
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        await LocalAiSetupRuntimeFixture.PrepareInstallAsync(paths);
        var fixture = new LocalAiSetupRuntimeFixture();
        await using var runtime = fixture.CreateRuntime(paths);
        Assert.Equal(LocalAiRuntimeState.Healthy, (await runtime.RestartForSetupAsync()).State);
        var started = (await store.LoadAsync())!;
        if (change == "missing") await store.DeleteAsync();
        else await store.SaveAsync(change switch
        {
            "model" => started.Manifest with
            {
                ModelPath = Path.Combine("models", "other", started.Manifest.ModelAsset.FileName),
            },
            _ => started.Manifest with { InstalledAtUtc = started.Manifest.InstalledAtUtc.AddDays(1) },
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.AcknowledgeSetupGatewayRouteAsync());

        Assert.True(runtime.Snapshot.GatewayRouteRequiresResolution);
        Assert.Equal(LocalAiRuntimeState.Stopped, (await runtime.StopForSetupAsync()).State);
        Assert.Empty(fixture.Withdrawn);
    }

    [Fact]
    public async Task Configure_AcknowledgementFailureCompensatesCommittedGatewayRoute()
    {
        using var temp = new TempDirectory("local-ai-gateway-ack-");
        LocalAiResolvedInstall install = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        string fallback = JsonSerializer.Serialize("openai/gpt-5");
        var commands = new GatewayStateCommandRunner(providerJson: null, fallback);
        SetupContext context = CreateContext(temp.Path, commands);
        context.LocalAiResolvedInstall = install;
        context.LocalAiEligibility = LocalInferenceEligibility.Evaluate(CreateSparkHardware());
        context.LocalAiRuntimeBorrowed = true;
        context.LocalAiRuntime = new AcknowledgementFailingRuntime();

        StepResult result = await new ConfigureLocalAiGatewayStep()
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Null(commands.ProviderJson);
        Assert.Equal(fallback, commands.PrimaryJson);
    }

    [Fact]
    public async Task Repair_RollbackUnsetsPrimaryAfterRetainedEndpointCycleWithoutFallback()
    {
        using var temp = new TempDirectory("local-ai-gateway-repair-");
        LocalAiResolvedInstall install = await SaveManifestAsync(temp.Path);
        string managedPrimary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
        var commands = new GatewayStateCommandRunner(providerJson: null, managedPrimary);
        SetupContext context = CreateContext(temp.Path, commands);
        context.LocalAiResolvedInstall = install;
        context.LocalAiEligibility = LocalInferenceEligibility.Evaluate(CreateSparkHardware());
        var step = new ConfigureLocalAiGatewayStep();

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);
        await step.RollbackAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Null(commands.ProviderJson);
        Assert.Null(commands.PrimaryJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshProcessUninstall_RemovesExactManagedProviderAndPrimary(bool pendingReplacement)
    {
        using var temp = new TempDirectory("local-ai-gateway-uninstall-");
        LocalAiResolvedInstall routed = await SaveManifestAsync(temp.Path);
        if (pendingReplacement)
        {
            LocalAiInstallManifest published = routed.Manifest with
            {
                ModelCatalogId = LocalModelCatalog.Qwen27BModelId,
                ModelAlias = LocalModelCatalog.Qwen27BModelId,
                Endpoint = "http://127.0.0.1:39876/v1",
                ReplacedManifest = routed.Manifest,
            };
            var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
            routed = store.ResolveAndValidate(published);
            await store.SaveAsync(published with
            {
                Endpoint = "http://127.0.0.1:39877/v1",
                PreviousEndpoints = [published.Endpoint!],
            });
        }
        string provider = LocalAiGatewayProviderDefinition.BuildProviderJson(routed);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(routed));
        var commands = new GatewayStateCommandRunner(provider, primary);
        SetupContext context = CreateContext(temp.Path, commands);
        context.IsUninstalling = true;

        await new ConfigureLocalAiGatewayStep().RollbackAsync(context, CancellationToken.None);

        Assert.Null(commands.ProviderJson);
        Assert.Null(commands.PrimaryJson);
        Assert.Contains(commands.WslCalls, command =>
            command.Contains("LOCAL_AI_GATEWAY_UNSET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FreshProcessUninstall_RemovesHistoricalManagedProviderWhenPrimaryIsMissing()
    {
        using var temp = new TempDirectory("local-ai-gateway-uninstall-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path);
        LocalAiInstallManifest pendingManifest = original.Manifest with
        {
            ModelCatalogId = LocalModelCatalog.Qwen27BModelId,
            ModelAlias = LocalModelCatalog.Qwen27BModelId,
            Endpoint = "http://127.0.0.1:39876/v1",
            ReplacedManifest = original.Manifest,
            PreviousEndpoints = [original.Manifest.Endpoint!],
        };
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(pendingManifest);
        LocalAiResolvedInstall pending = (await store.LoadAsync())!;
        LocalAiResolvedInstall historical = pending with
        {
            Manifest = pending.Manifest with { Endpoint = original.Manifest.Endpoint },
            Endpoint = original.Endpoint,
        };
        var commands = new GatewayStateCommandRunner(
            LocalAiGatewayProviderDefinition.BuildProviderJson(historical),
            primaryJson: null);
        SetupContext context = CreateContext(temp.Path, commands);
        context.IsUninstalling = true;

        await new ConfigureLocalAiGatewayStep().RollbackAsync(context, CancellationToken.None);

        Assert.Null(commands.ProviderJson);
        Assert.Null(commands.PrimaryJson);
    }

    [Fact]
    public async Task FreshProcessUninstall_RemovesPendingReplacementOnFixedPort()
    {
        using var temp = new TempDirectory("local-ai-gateway-uninstall-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path, requestedPort: 28765);
        LocalAiInstallManifest pendingManifest = original.Manifest with
        {
            ModelCatalogId = LocalModelCatalog.Qwen27BModelId,
            ModelAlias = LocalModelCatalog.Qwen27BModelId,
            ReplacedManifest = original.Manifest,
        };
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(pendingManifest);
        LocalAiResolvedInstall pending = (await store.LoadAsync())!;
        var commands = new GatewayStateCommandRunner(
            LocalAiGatewayProviderDefinition.BuildProviderJson(pending),
            JsonSerializer.Serialize(LocalAiGatewayProviderDefinition.BuildPrimaryModel(pending)));
        SetupContext context = CreateContext(temp.Path, commands);
        context.IsUninstalling = true;

        await new ConfigureLocalAiGatewayStep().RollbackAsync(context, CancellationToken.None);

        Assert.Null(commands.ProviderJson);
        Assert.Null(commands.PrimaryJson);
    }

    [Fact]
    public async Task FreshProcessUninstall_AcceptsCliRedactedManagedApiKey()
    {
        using var temp = new TempDirectory("local-ai-gateway-uninstall-");
        LocalAiResolvedInstall install = await SaveManifestAsync(temp.Path);
        string provider = LocalAiGatewayProviderDefinition.BuildProviderJson(install).Replace(
            "\"api\":\"openai-completions\",\"apiKey\":\"llama-local\"",
            $"\"apiKey\":\"{LocalAiGatewayProviderDefinition.CliRedactedApiKey}\",\"api\":\"openai-completions\"",
            StringComparison.Ordinal);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
        var commands = new GatewayStateCommandRunner(provider, primary);
        SetupContext context = CreateContext(temp.Path, commands);
        context.IsUninstalling = true;

        await new ConfigureLocalAiGatewayStep().RollbackAsync(context, CancellationToken.None);

        Assert.Null(commands.ProviderJson);
        Assert.Null(commands.PrimaryJson);
    }

    [Fact]
    public async Task FreshProcessUninstall_RestoresRecordedFallbackPrimary()
    {
        using var temp = new TempDirectory("local-ai-gateway-uninstall-");
        LocalAiResolvedInstall install = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        string provider = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
        var commands = new GatewayStateCommandRunner(provider, primary);
        SetupContext context = CreateContext(temp.Path, commands);
        context.IsUninstalling = true;

        await new ConfigureLocalAiGatewayStep().RollbackAsync(context, CancellationToken.None);

        Assert.Null(commands.ProviderJson);
        Assert.Equal(JsonSerializer.Serialize("openai/gpt-5"), commands.PrimaryJson);
        Assert.Contains(commands.WslCalls, command =>
            command.Contains("LOCAL_AI_PRIMARY_RESTORED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FreshProcessUninstall_PreservesDriftAndFailsClosed()
    {
        using var temp = new TempDirectory("local-ai-gateway-uninstall-");
        LocalAiResolvedInstall install = await SaveManifestAsync(temp.Path);
        string expectedProvider = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        string driftedProvider = expectedProvider.Replace(
            "http://127.0.0.1:28765/v1",
            "http://127.0.0.1:39876/v1",
            StringComparison.Ordinal);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
        var commands = new GatewayStateCommandRunner(driftedProvider, primary);
        SetupContext context = CreateContext(temp.Path, commands);
        context.IsUninstalling = true;

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ConfigureLocalAiGatewayStep().RollbackAsync(context, CancellationToken.None));

        Assert.Contains("preserving", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(driftedProvider, commands.ProviderJson);
        Assert.Equal(primary, commands.PrimaryJson);
        Assert.DoesNotContain(commands.WslCalls, command =>
            command.Contains("LOCAL_AI_GATEWAY_UNSET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FreshProcessUninstall_PreservesStateWhenSnapshotFails()
    {
        using var temp = new TempDirectory("local-ai-gateway-uninstall-");
        LocalAiResolvedInstall install = await SaveManifestAsync(temp.Path);
        string provider = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
        var commands = new GatewayStateCommandRunner(provider, primary) { FailCapture = true };
        SetupContext context = CreateContext(temp.Path, commands);
        context.IsUninstalling = true;

        await Assert.ThrowsAsync<IOException>(() =>
            new ConfigureLocalAiGatewayStep().RollbackAsync(context, CancellationToken.None));

        Assert.Equal(provider, commands.ProviderJson);
        Assert.Equal(primary, commands.PrimaryJson);
        Assert.DoesNotContain(commands.WslCalls, command =>
            command.Contains("LOCAL_AI_GATEWAY_UNSET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recovery_ReplacesExactManagedProviderAfterAutomaticPortChanges()
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        LocalAiInstallManifest publishedManifest = original.Manifest with
        {
            ModelCatalogId = LocalModelCatalog.Qwen27BModelId,
            ModelAlias = LocalModelCatalog.Qwen27BModelId,
            Endpoint = "http://127.0.0.1:39878/v1",
            ReplacedManifest = original.Manifest,
            PreviousEndpoints =
            [
                "http://127.0.0.1:39876/v1",
                "http://127.0.0.1:39877/v1",
            ],
        };
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(publishedManifest);
        LocalAiResolvedInstall published = (await store.LoadAsync())!;
        LocalAiResolvedInstall publishedRoute = published with
        {
            Manifest = published.Manifest with { Endpoint = published.Manifest.PreviousEndpoints!.Value[1] },
            Endpoint = new Uri(published.Manifest.PreviousEndpoints!.Value[1]),
        };
        string publishedProvider = LocalAiGatewayProviderDefinition.BuildProviderJson(publishedRoute);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(published));
        var commands = new GatewayStateCommandRunner(publishedProvider, primary);
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        context.LocalAiRecoveryOriginalInstall = original;
        context.LocalAiRecoveryPendingInstall = published;
        context.LocalAiRecoveryReceiptRollbackAllowed = true;
        LocalAiInstallManifest replacementManifest = publishedManifest with
        {
            Endpoint = "http://127.0.0.1:39879/v1",
            PreviousEndpoints = publishedManifest.PreviousEndpoints!.Value.Add(publishedManifest.Endpoint!),
        };
        await store.SaveAsync(replacementManifest);
        context.LocalAiResolvedInstall = (await store.LoadAsync())!;
        var step = new ConfigureLocalAiGatewayStep();

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);
        await step.RollbackAsync(context, CancellationToken.None);
        await new PreserveLocalAiRecoveryGatewayStep(
                (_, _) => Task.FromResult(StepResult.Ok("not needed")),
                (_, _) => Task.FromResult(true))
            .RollbackAsync(context, CancellationToken.None);
        await new PersistLocalAiManifestStep().RollbackAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.False(context.LocalAiRecoveryProviderTransition);
        LocalAiResolvedInstall restored = (await store.LoadAsync())!;
        Assert.Equal(publishedRoute.Manifest.Endpoint, restored.Manifest.Endpoint);
        Assert.Equal(published.Manifest.ModelCatalogId, restored.Manifest.ModelCatalogId);
        Assert.NotNull(restored.Manifest.ReplacedManifest);
        Assert.Equal(published.Manifest.PreviousEndpoints, restored.Manifest.PreviousEndpoints);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(
            commands.ProviderJson!,
            restored));
        Assert.Equal(
            JsonSerializer.Serialize(
                LocalAiGatewayProviderDefinition.BuildPrimaryModel(restored)),
            commands.PrimaryJson);
    }

    [Fact]
    public async Task Recovery_ReplacesAndRollsBackModelOnFixedPort()
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(
            temp.Path,
            fallbackModel: "openai/gpt-5",
            requestedPort: 28765);
        var commands = new GatewayStateCommandRunner(
            LocalAiGatewayProviderDefinition.BuildProviderJson(original),
            JsonSerializer.Serialize(LocalAiGatewayProviderDefinition.BuildPrimaryModel(original)));
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        context.LocalAiRecoveryOriginalInstall = original;
        context.LocalAiRecoveryReceiptRollbackAllowed = true;
        LocalAiInstallManifest replacementManifest = original.Manifest with
        {
            ModelCatalogId = LocalModelCatalog.Qwen27BModelId,
            ModelAlias = LocalModelCatalog.Qwen27BModelId,
            ReplacedManifest = original.Manifest,
        };
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(replacementManifest);
        LocalAiResolvedInstall replacement = (await store.LoadAsync())!;
        context.LocalAiResolvedInstall = replacement;
        var step = new ConfigureLocalAiGatewayStep();

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(
            commands.ProviderJson!,
            replacement));
        Assert.Equal(
            JsonSerializer.Serialize(LocalAiGatewayProviderDefinition.BuildPrimaryModel(replacement)),
            commands.PrimaryJson);

        await step.RollbackAsync(context, CancellationToken.None);

        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(
            commands.ProviderJson!,
            original));
        Assert.Equal(
            JsonSerializer.Serialize(LocalAiGatewayProviderDefinition.BuildPrimaryModel(original)),
            commands.PrimaryJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_ReplacementAcceptsProviderlessOriginalPrimary(bool retainedManagedPrimary)
    {
        const string fallback = "openai/gpt-5";
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(
            temp.Path,
            retainedManagedPrimary ? fallback : null);
        string primary = JsonSerializer.Serialize(retainedManagedPrimary
            ? LocalAiGatewayProviderDefinition.BuildPrimaryModel(original)
            : fallback);
        var commands = new GatewayStateCommandRunner(providerJson: null, primary);
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        context.LocalAiRecoveryOriginalInstall = original;
        LocalAiInstallManifest replacement = original.Manifest with
        {
            ModelCatalogId = LocalModelCatalog.Qwen27BModelId,
            ModelAlias = LocalModelCatalog.Qwen27BModelId,
            Endpoint = "http://127.0.0.1:39876/v1",
            ReplacedManifest = original.Manifest,
        };
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(replacement);
        context.LocalAiResolvedInstall = store.ResolveAndValidate(replacement);

        StepResult result = await new ConfigureLocalAiGatewayStep()
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal(fallback, (await store.LoadAsync())!.Manifest.GatewayFallbackModel);
    }

    [Fact]
    public async Task Recovery_DriftedProviderRejectsBeforeGatewayMutation()
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path);
        string driftedProvider = LocalAiGatewayProviderDefinition.BuildProviderJson(original)
            .Replace(
                "http://127.0.0.1:28765/v1",
                "http://127.0.0.1:45555/v1",
                StringComparison.Ordinal);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(original));
        var commands = new GatewayStateCommandRunner(driftedProvider, primary);
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        context.LocalAiRecoveryOriginalInstall = original;
        context.LocalAiRecoveryReceiptRollbackAllowed = true;
        LocalAiInstallManifest replacementManifest = ReplacementManifest(original);
        context.LocalAiResolvedInstall = new LocalAiResolvedInstall(
            replacementManifest,
            original.ExecutablePath,
            original.ModelPath,
            new Uri(replacementManifest.Endpoint!));

        StepResult result = await new ConfigureLocalAiGatewayStep()
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.Equal(driftedProvider, commands.ProviderJson);
        Assert.Equal(primary, commands.PrimaryJson);
        Assert.DoesNotContain(commands.WslCalls, command =>
            command.Contains("LOCAL_AI_GATEWAY_CONFIGURED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recovery_RollbackRestoresOriginalProviderAndReceipt()
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        string originalProvider = LocalAiGatewayProviderDefinition.BuildProviderJson(original);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(original));
        var commands = new GatewayStateCommandRunner(originalProvider, primary);
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        context.LocalAiRecoveryOriginalInstall = original;
        context.LocalAiRecoveryReceiptRollbackAllowed = true;
        LocalAiInstallManifest replacementManifest = ReplacementManifest(original);
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(replacementManifest);
        context.LocalAiResolvedInstall = store.ResolveAndValidate(replacementManifest);
        var step = new ConfigureLocalAiGatewayStep();

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);
        await step.RollbackAsync(context, CancellationToken.None);
        await new PreserveLocalAiRecoveryGatewayStep(
                (_, _) => Task.FromResult(StepResult.Ok("not needed")),
                (_, _) => Task.FromResult(true))
            .RollbackAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(
            commands.ProviderJson!,
            original));
        Assert.Equal(primary, commands.PrimaryJson);
        Assert.Equal(original.Endpoint, (await store.LoadAsync())!.Endpoint);
        Assert.False(context.LocalAiRecoveryProviderTransition);
    }

    [Fact]
    public async Task Recovery_RollbackPreservesEndpointCycleManagedPrimary()
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        string managedPrimary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(original));
        var commands = new GatewayStateCommandRunner(providerJson: null, managedPrimary);
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        context.LocalAiRecoveryOriginalInstall = original;
        context.LocalAiRecoveryReceiptRollbackAllowed = true;
        LocalAiInstallManifest replacementManifest = ReplacementManifest(original);
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(replacementManifest);
        context.LocalAiResolvedInstall = store.ResolveAndValidate(replacementManifest);
        var step = new ConfigureLocalAiGatewayStep();

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);
        await step.RollbackAsync(context, CancellationToken.None);
        await new PreserveLocalAiRecoveryGatewayStep(
                (_, _) => Task.FromResult(StepResult.Ok("not needed")),
                (_, _) => Task.FromResult(true))
            .RollbackAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Null(commands.ProviderJson);
        Assert.Equal(managedPrimary, commands.PrimaryJson);
        Assert.Equal(original.Endpoint, (await store.LoadAsync())!.Endpoint);
        Assert.False(context.LocalAiRecoveryProviderTransition);
    }

    [Fact]
    public async Task Recovery_FailedProviderSwitchRestoresOriginalReceipt()
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        string originalProvider = LocalAiGatewayProviderDefinition.BuildProviderJson(original);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(original));
        var commands = new GatewayStateCommandRunner(originalProvider, primary)
        {
            FailConfiguredBatchOnce = true,
        };
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        context.LocalAiRecoveryOriginalInstall = original;
        context.LocalAiRecoveryReceiptRollbackAllowed = true;
        LocalAiInstallManifest replacementManifest = ReplacementManifest(original);
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(replacementManifest);
        context.LocalAiResolvedInstall = store.ResolveAndValidate(replacementManifest);
        var step = new ConfigureLocalAiGatewayStep();

        StepResult result = await step.ExecuteAsync(context, CancellationToken.None);
        await step.RollbackAsync(context, CancellationToken.None);
        await new PreserveLocalAiRecoveryGatewayStep(
                (_, _) => Task.FromResult(StepResult.Ok("not needed")),
                (_, _) => Task.FromResult(true))
            .RollbackAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, result.Outcome);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(
            commands.ProviderJson!,
            original));
        Assert.Equal(primary, commands.PrimaryJson);
        Assert.Equal(original.Endpoint, (await store.LoadAsync())!.Endpoint);
        Assert.False(context.LocalAiRecoveryProviderTransition);
    }

    [Fact]
    public async Task Recovery_FailureBeforeProviderConfigurationRestoresOriginalReceipt()
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        var commands = new GatewayStateCommandRunner(
            providerJson: null,
            primaryJson: JsonSerializer.Serialize("openai/gpt-5"));
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        context.LocalAiRecoveryOriginalInstall = original;
        context.LocalAiRecoveryReceiptRollbackAllowed = true;
        LocalAiInstallManifest replacementManifest = ReplacementManifest(original);
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(replacementManifest);
        context.LocalAiResolvedInstall = store.ResolveAndValidate(replacementManifest);
        context.LocalAiRecoveryProviderTransition = true;
        context.LocalAiGatewayPriorState = new(
            ProviderExisted: false,
            ProviderJson: null,
            PrimaryModelExisted: true,
            PrimaryModelJson: JsonSerializer.Serialize("openai/gpt-5"));
        var step = new PreserveLocalAiRecoveryGatewayStep(
            (_, _) => Task.FromResult(StepResult.Ok("not needed")),
            (_, _) => Task.FromResult(true));

        await step.RollbackAsync(context, CancellationToken.None);

        Assert.Equal(original.Endpoint, (await store.LoadAsync())!.Endpoint);
        Assert.Equal(original.Endpoint, context.LocalAiResolvedInstall!.Endpoint);
        Assert.False(context.LocalAiRecoveryProviderTransition);
        Assert.Null(context.LocalAiGatewayPriorState);
    }

    [Fact]
    public async Task Recovery_RetryPreservesOriginalProviderRollbackBaseline()
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        string originalProvider = LocalAiGatewayProviderDefinition.BuildProviderJson(original);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(original));
        var commands = new GatewayStateCommandRunner(originalProvider, primary)
        {
            LoseConfiguredAcknowledgementOnce = true,
        };
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        context.LocalAiRecoveryOriginalInstall = original;
        context.LocalAiRecoveryReceiptRollbackAllowed = true;
        LocalAiInstallManifest replacementManifest = ReplacementManifest(original);
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(replacementManifest);
        context.LocalAiResolvedInstall = store.ResolveAndValidate(replacementManifest);
        var step = new ConfigureLocalAiGatewayStep();

        StepResult first = await step.ExecuteAsync(context, CancellationToken.None);
        StepResult second = await step.ExecuteAsync(context, CancellationToken.None);
        await step.RollbackAsync(context, CancellationToken.None);
        await new PreserveLocalAiRecoveryGatewayStep(
                (_, _) => Task.FromResult(StepResult.Ok("not needed")),
                (_, _) => Task.FromResult(true))
            .RollbackAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Failed, first.Outcome);
        Assert.Equal(StepOutcome.Success, second.Outcome);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(
            commands.ProviderJson!,
            original));
        Assert.Equal(primary, commands.PrimaryJson);
        Assert.Equal(original.Endpoint, (await store.LoadAsync())!.Endpoint);
        Assert.False(context.LocalAiRecoveryProviderTransition);
    }

    [Fact]
    public async Task Recovery_RemovedProviderRefusesRestoreButStillRestartsGatewayAndRetainsUncertainty()
    {
        using var temp = new TempDirectory("local-ai-removed-provider-");
        var original = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        var replacement = store.ResolveAndValidate(ReplacementManifest(original));
        await store.SaveAsync(replacement.Manifest);
        var primary = JsonSerializer.Serialize(LocalAiGatewayProviderDefinition.BuildPrimaryModel(original));
        // Another owner removed the provider after compensation, before the borrowed restart returned.
        var commands = new GatewayStateCommandRunner(null, primary);
        var context = CreateRecoveryContext(temp.Path, commands);
        context.LocalAiResolvedInstall = replacement;
        context.LocalAiRecoveryOriginalInstall = original;
        context.LocalAiRecoveryReceiptRollbackAllowed = true;
        context.LocalAiRecoveryProviderTransition = true;
        context.LocalAiRecoveryStoppedWsl = true;
        context.LocalAiGatewayPriorState = new(true,
            LocalAiGatewayProviderDefinition.BuildProviderJson(original), true, primary);
        context.LocalAiRuntimeBorrowed = true;
        context.LocalAiRuntime = new SettlementTrackingRuntime(store);
        int restarts = 0;
        var guard = new PreserveLocalAiRecoveryGatewayStep((_, _) =>
        {
            restarts++;
            return Task.FromResult(StepResult.Ok("gateway restarted"));
        }, (_, _) => throw new InvalidOperationException("A refused route must not reach the health probe."));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => guard.RollbackAsync(context, default));

        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Equal(1, restarts);
        Assert.False(context.LocalAiRecoveryStoppedWsl);
        Assert.True(context.LocalAiRecoveryRollbackUncertain);
        Assert.False(context.LocalAiRecoveryReceiptRollbackAllowed);
        Assert.False(context.LocalAiBorrowedRuntimeRestored);
        Assert.Null(commands.ProviderJson);
        Assert.Equal(primary, commands.PrimaryJson);
        Assert.Single(commands.WslCalls); // Capture only, never CAS or an unguarded rewrite.
    }

    [Fact]
    public async Task RestoreRecoveryRouteAsync_PreservesConcurrentGatewayChanges()
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        string originalProvider = LocalAiGatewayProviderDefinition.BuildProviderJson(original);
        string originalPrimary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(original));
        var prior = new LocalAiGatewayPriorState(
            ProviderExisted: true,
            ProviderJson: originalProvider,
            PrimaryModelExisted: true,
            PrimaryModelJson: originalPrimary);
        var commands = new GatewayStateCommandRunner(
            LocalAiGatewayProviderDefinition.BuildProviderJson(original),
            JsonSerializer.Serialize("openai/concurrent-model"));
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        LocalAiResolvedInstall moved = original with
        {
            Manifest = original.Manifest with { Endpoint = "http://127.0.0.1:28766/v1" },
            Endpoint = new Uri("http://127.0.0.1:28766/v1"),
        };

        bool restored = await ConfigureLocalAiGatewayStep.RestoreRecoveryRouteAsync(
            context,
            prior,
            original,
            moved,
            CancellationToken.None);

        Assert.False(restored);
        Assert.Equal(JsonSerializer.Serialize("openai/concurrent-model"), commands.PrimaryJson);
        Assert.DoesNotContain(
            commands.WslCalls,
            command => command.Contains("LOCAL_AI_GATEWAY_RESTORED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestoreRecoveryRouteAsync_PreservesCustomizedProviderWhenEndpointIsUnchanged()
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        string customizedProvider = LocalAiGatewayProviderDefinition.BuildProviderJson(original)
            .Replace("\"timeoutSeconds\":300", "\"timeoutSeconds\":301", StringComparison.Ordinal);
        string originalPrimary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(original));
        var prior = new LocalAiGatewayPriorState(true, customizedProvider, true, originalPrimary);
        var commands = new GatewayStateCommandRunner(customizedProvider, originalPrimary)
        {
            SupportsConditionalProviderSet = false,
        };
        SetupContext context = CreateRecoveryContext(temp.Path, commands);

        bool restored = await ConfigureLocalAiGatewayStep.RestoreRecoveryRouteAsync(
            context,
            prior,
            original,
            original,
            CancellationToken.None);

        Assert.True(restored);
        Assert.Equal(customizedProvider, commands.ProviderJson);
        Assert.DoesNotContain(
            commands.WslCalls,
            command => command.Contains("LOCAL_AI_GATEWAY_RESTORED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestoreRecoveryRouteAsync_ConditionallyMovesOwnedProviderEndpoint()
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        string originalProvider = LocalAiGatewayProviderDefinition.BuildProviderJson(original);
        string originalPrimary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(original));
        var prior = new LocalAiGatewayPriorState(
            ProviderExisted: true,
            ProviderJson: originalProvider,
            PrimaryModelExisted: true,
            PrimaryModelJson: originalPrimary);
        var commands = new GatewayStateCommandRunner(originalProvider, originalPrimary);
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        LocalAiResolvedInstall moved = original with
        {
            Manifest = original.Manifest with { Endpoint = "http://127.0.0.1:28766/v1" },
            Endpoint = new Uri("http://127.0.0.1:28766/v1"),
        };

        bool restored = await ConfigureLocalAiGatewayStep.RestoreRecoveryRouteAsync(
            context,
            prior,
            original,
            moved,
            CancellationToken.None);

        Assert.True(restored);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(commands.ProviderJson!, moved));
        Assert.Equal(originalPrimary, commands.PrimaryJson);
        Assert.Contains(
            commands.WslCalls,
            command => command.Contains("--expect-current-json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestoreRecoveryRouteAsync_PreservesPrimaryChangeRacingProviderCas()
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        string originalProvider = LocalAiGatewayProviderDefinition.BuildProviderJson(original);
        string originalPrimary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(original));
        string concurrentPrimary = JsonSerializer.Serialize("openai/concurrent-model");
        var prior = new LocalAiGatewayPriorState(true, originalProvider, true, originalPrimary);
        var commands = new GatewayStateCommandRunner(originalProvider, originalPrimary)
        {
            PrimaryJsonAfterConditionalProviderSet = concurrentPrimary,
        };
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        LocalAiResolvedInstall moved = original with
        {
            Manifest = original.Manifest with { Endpoint = "http://127.0.0.1:28766/v1" },
            Endpoint = new Uri("http://127.0.0.1:28766/v1"),
        };

        bool restored = await ConfigureLocalAiGatewayStep.RestoreRecoveryRouteAsync(
            context,
            prior,
            original,
            moved,
            CancellationToken.None);

        Assert.True(restored);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(commands.ProviderJson!, moved));
        Assert.Equal(concurrentPrimary, commands.PrimaryJson);
    }

    [Fact]
    public async Task RestoreRecoveryRouteAsync_RejectsLegacyGatewayCliWithoutConditionalWrites()
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        string originalProvider = LocalAiGatewayProviderDefinition.BuildProviderJson(original);
        string originalPrimary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(original));
        var prior = new LocalAiGatewayPriorState(true, originalProvider, true, originalPrimary);
        var commands = new GatewayStateCommandRunner(originalProvider, originalPrimary)
        {
            SupportsConditionalProviderSet = false,
        };
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        LocalAiResolvedInstall moved = original with
        {
            Manifest = original.Manifest with { Endpoint = "http://127.0.0.1:28766/v1" },
            Endpoint = new Uri("http://127.0.0.1:28766/v1"),
        };

        bool restored = await ConfigureLocalAiGatewayStep.RestoreRecoveryRouteAsync(
            context,
            prior,
            original,
            moved,
            CancellationToken.None);

        Assert.False(restored);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(commands.ProviderJson!, original));
        Assert.Equal(originalPrimary, commands.PrimaryJson);
        Assert.Contains(
            commands.WslCalls,
            command => command.Contains("LOCAL_AI_CONDITIONAL_SET_UNSUPPORTED", StringComparison.Ordinal));
        Assert.DoesNotContain(
            commands.WslCalls,
            command => command.Contains("OPENCLAW_LOCAL_AI_BATCH_B64", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_UpgradedRouteSettlementSurvivesRemainingRollback(
        bool priorRouteWasPending)
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        LocalAiInstallManifest oldPendingManifest = ReplacementManifest(original);
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        LocalAiResolvedInstall oldPending = store.ResolveAndValidate(oldPendingManifest);
        LocalAiInstallManifest upgradedOriginal = original.Manifest with
        {
            EngineVersion = "b11026",
            RuntimeId = "b11026-cuda13-arm64",
        };
        LocalAiInstallManifest upgradedReplacement = oldPendingManifest with
        {
            EngineVersion = upgradedOriginal.EngineVersion,
            RuntimeId = upgradedOriginal.RuntimeId,
            Endpoint = "http://127.0.0.1:39877/v1",
            ReplacedManifest = upgradedOriginal,
            PreviousEndpoints =
            [
                original.Endpoint!.AbsoluteUri,
                oldPending.Endpoint!.AbsoluteUri,
            ],
        };
        LocalAiInstallManifest upgradedPendingRoute = upgradedReplacement with
        {
            Endpoint = oldPending.Manifest.Endpoint,
        };
        await store.SaveAsync(upgradedReplacement);

        LocalAiResolvedInstall priorRoute = priorRouteWasPending ? oldPending : original;
        string priorProvider = LocalAiGatewayProviderDefinition.BuildProviderJson(priorRoute);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(priorRoute));
        var commands = new GatewayStateCommandRunner(priorProvider, primary);
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        context.Config.RollbackOnFailure = true;
        context.LocalAiRecoveryOriginalInstall = store.ResolveAndValidate(upgradedOriginal);
        context.LocalAiRecoveryPendingInstall = store.ResolveAndValidate(upgradedPendingRoute);
        context.LocalAiUpgradeOriginalInstall = oldPending;
        context.LocalAiResolvedInstall = store.ResolveAndValidate(upgradedReplacement);
        context.LocalAiRuntimeBorrowed = true;
        var runtime = new SettlementTrackingRuntime(store);
        context.LocalAiRuntime = runtime;
        context.LocalAiRuntimeInstall = new LlamaRuntimeInstallResult(
            Path.GetDirectoryName(context.LocalAiResolvedInstall.ExecutablePath)!,
            context.LocalAiResolvedInstall.ExecutablePath,
            LlamaRuntimeInstallDisposition.Installed,
            CreatedThisRun: true,
            VerifiedArchives: [],
            Rollback: null);
        var runtimeAcquirer = new TrackingRuntimeAcquirer();

        var configure = new ConfigureLocalAiGatewayStep();
        StepResult configured = await configure.ExecuteAsync(context, CancellationToken.None);
        await configure.RollbackAsync(context, CancellationToken.None);
        await new PreserveLocalAiRecoveryGatewayStep(
                (_, _) => Task.FromResult(StepResult.Ok("not needed")),
                (_, _) => Task.FromResult(true),
                (_, _, _, _, _) => Task.FromResult(true))
            .RollbackAsync(context, CancellationToken.None);
        await new PersistLocalAiManifestStep().RollbackAsync(context, CancellationToken.None);
        await new AcquireLocalAiRuntimeStep(runtimeAcquirer)
            .RollbackAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, configured.Outcome);
        Assert.Equal(1, runtime.RestartForSetupCalls);
        Assert.Equal(0, runtime.RestartForSetupRollbackCalls);
        Assert.Null(context.LocalAiUpgradeOriginalInstall);
        Assert.Null(context.LocalAiRecoveryOriginalInstall);
        LocalAiResolvedInstall restored = Assert.IsType<LocalAiResolvedInstall>(
            await store.LoadAsync());
        Assert.Equal(priorRoute.Endpoint, restored.Endpoint);
        Assert.Equal(priorRoute.Manifest.ModelCatalogId, restored.Manifest.ModelCatalogId);
        Assert.Equal(upgradedReplacement.RuntimeId, restored.Manifest.RuntimeId);
        if (priorRouteWasPending)
        {
            LocalAiInstallManifest restoredOriginal = Assert.IsType<LocalAiInstallManifest>(
                restored.Manifest.ReplacedManifest);
            Assert.Equal(upgradedOriginal.RuntimeId, restoredOriginal.RuntimeId);
            Assert.Equal(upgradedOriginal.ModelCatalogId, restoredOriginal.ModelCatalogId);
            Assert.Equal(upgradedOriginal.Endpoint, restoredOriginal.Endpoint);
        }
        else
        {
            Assert.Null(restored.Manifest.ReplacedManifest);
        }
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(
            commands.ProviderJson!,
            restored));
        Assert.Equal(primary, commands.PrimaryJson);
        Assert.Null(context.LocalAiRuntimeInstall);
        Assert.Equal(0, runtimeAcquirer.RemoveCalls);
        Assert.False(context.LocalAiRecoveryRollbackUncertain);
        Assert.False(context.LocalAiRecoveryProviderTransition);
    }

    [Fact]
    public async Task Recovery_FailedProviderCompensationKeepsReplacementReceipt()
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        string originalProvider = LocalAiGatewayProviderDefinition.BuildProviderJson(original);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(original));
        var commands = new GatewayStateCommandRunner(originalProvider, primary);
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        context.Config.RollbackOnFailure = true;
        context.LocalAiRecoveryOriginalInstall = original;
        context.LocalAiRecoveryReceiptRollbackAllowed = true;
        LocalAiInstallManifest replacementManifest = ReplacementManifest(original);
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(replacementManifest);
        context.LocalAiResolvedInstall = store.ResolveAndValidate(replacementManifest);
        var configure = new ConfigureLocalAiGatewayStep();
        StepResult configured = await configure.ExecuteAsync(context, CancellationToken.None);
        commands.FailRestoreBatchOnce = true;
        var pipeline = new SetupPipeline(
        [
            new PreserveLocalAiRecoveryGatewayStep((_, _) =>
                Task.FromResult(StepResult.Ok("not needed"))),
            new DelegatingRollbackStep("configured", configure.RollbackAsync),
            new DelegatingRollbackStep(
                "fail",
                (_, _) => Task.CompletedTask,
                (_, _) => Task.FromResult(StepResult.Fail("forced failure"))),
        ]);

        PipelineResult result = await pipeline.RunAsync(context);

        Assert.Equal(StepOutcome.Success, configured.Outcome);
        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(
            commands.ProviderJson!,
            context.LocalAiResolvedInstall));
        Assert.Equal(new Uri(replacementManifest.Endpoint!), (await store.LoadAsync())!.Endpoint);
        Assert.True(context.LocalAiRecoveryProviderTransition);
        Assert.False(context.LocalAiRecoveryReceiptRollbackAllowed);
        Assert.False(context.LocalAiRecoveryCleanupAllowed);
        Assert.True(context.LocalAiRecoveryGatewayConfigurationStartedThisRun);
    }

    [Fact]
    public async Task Recovery_LostRollbackAcknowledgementRestoresOriginalReceipt()
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        string originalProvider = LocalAiGatewayProviderDefinition.BuildProviderJson(original);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(original));
        var commands = new GatewayStateCommandRunner(originalProvider, primary);
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        context.Config.RollbackOnFailure = true;
        context.LocalAiRecoveryOriginalInstall = original;
        context.LocalAiRecoveryReceiptRollbackAllowed = true;
        LocalAiInstallManifest replacementManifest = ReplacementManifest(original);
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(replacementManifest);
        context.LocalAiResolvedInstall = store.ResolveAndValidate(replacementManifest);
        var configure = new ConfigureLocalAiGatewayStep();
        StepResult configured = await configure.ExecuteAsync(context, CancellationToken.None);
        commands.LoseRestoreAcknowledgementOnce = true;
        var pipeline = new SetupPipeline(
        [
            new PreserveLocalAiRecoveryGatewayStep(
                (_, _) => Task.FromResult(StepResult.Ok("not needed")),
                (_, _) => Task.FromResult(true)),
            new DelegatingRollbackStep("configured", configure.RollbackAsync),
            new DelegatingRollbackStep(
                "fail",
                (_, _) => Task.CompletedTask,
                (_, _) => Task.FromResult(StepResult.Fail("forced failure"))),
        ]);

        PipelineResult result = await pipeline.RunAsync(context);

        Assert.Equal(StepOutcome.Success, configured.Outcome);
        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(
            commands.ProviderJson!,
            original));
        Assert.Equal(primary, commands.PrimaryJson);
        Assert.Equal(original.Endpoint, (await store.LoadAsync())!.Endpoint);
        Assert.False(context.LocalAiRecoveryProviderTransition);
        Assert.False(context.LocalAiRecoveryReceiptRollbackAllowed);
    }

    [Fact]
    public async Task Recovery_RollbackCancellationKeepsReplacementReceipt()
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        string originalProvider = LocalAiGatewayProviderDefinition.BuildProviderJson(original);
        string primary = JsonSerializer.Serialize(
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(original));
        var commands = new GatewayStateCommandRunner(originalProvider, primary);
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        context.Config.RollbackOnFailure = true;
        context.LocalAiRecoveryOriginalInstall = original;
        context.LocalAiRecoveryReceiptRollbackAllowed = true;
        LocalAiInstallManifest replacementManifest = ReplacementManifest(original);
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(replacementManifest);
        context.LocalAiResolvedInstall = store.ResolveAndValidate(replacementManifest);
        var configure = new ConfigureLocalAiGatewayStep();
        StepResult configured = await configure.ExecuteAsync(context, CancellationToken.None);
        commands.ThrowOnNextCapture = true;
        var pipeline = new SetupPipeline(
        [
            new PreserveLocalAiRecoveryGatewayStep((_, _) =>
                Task.FromResult(StepResult.Ok("not needed"))),
            new DelegatingRollbackStep("configured", configure.RollbackAsync),
            new DelegatingRollbackStep(
                "fail",
                (_, _) => Task.CompletedTask,
                (_, _) => Task.FromResult(StepResult.Fail("forced failure"))),
        ]);

        PipelineResult result = await pipeline.RunAsync(context);

        Assert.Equal(StepOutcome.Success, configured.Outcome);
        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(
            commands.ProviderJson!,
            context.LocalAiResolvedInstall));
        Assert.Equal(new Uri(replacementManifest.Endpoint!), (await store.LoadAsync())!.Endpoint);
        Assert.True(context.LocalAiRecoveryProviderTransition);
        Assert.False(context.LocalAiRecoveryReceiptRollbackAllowed);
        Assert.False(context.LocalAiRecoveryCleanupAllowed);
    }

    [Fact]
    public async Task Recovery_ProviderCreationRollbackCancellationKeepsReplacementReceipt()
    {
        using var temp = new TempDirectory("local-ai-gateway-recovery-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        string fallback = JsonSerializer.Serialize("openai/gpt-5");
        var commands = new GatewayStateCommandRunner(providerJson: null, primaryJson: fallback);
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        context.Config.RollbackOnFailure = true;
        context.LocalAiRecoveryOriginalInstall = original;
        context.LocalAiRecoveryReceiptRollbackAllowed = true;
        LocalAiInstallManifest replacementManifest = ReplacementManifest(original);
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        await store.SaveAsync(replacementManifest);
        context.LocalAiResolvedInstall = store.ResolveAndValidate(replacementManifest);
        var configure = new ConfigureLocalAiGatewayStep();
        StepResult configured = await configure.ExecuteAsync(context, CancellationToken.None);
        commands.ThrowOnNextCapture = true;
        var pipeline = new SetupPipeline(
        [
            new PreserveLocalAiRecoveryGatewayStep((_, _) =>
                Task.FromResult(StepResult.Ok("not needed"))),
            new DelegatingRollbackStep("configured", configure.RollbackAsync),
            new DelegatingRollbackStep(
                "fail",
                (_, _) => Task.CompletedTask,
                (_, _) => Task.FromResult(StepResult.Fail("forced failure"))),
        ]);

        PipelineResult result = await pipeline.RunAsync(context);

        Assert.Equal(StepOutcome.Success, configured.Outcome);
        Assert.Equal(PipelineOutcome.Failed, result.Outcome);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(
            commands.ProviderJson!,
            context.LocalAiResolvedInstall));
        Assert.Equal(new Uri(replacementManifest.Endpoint!), (await store.LoadAsync())!.Endpoint);
        Assert.True(context.LocalAiRecoveryProviderTransition);
        Assert.False(context.LocalAiRecoveryReceiptRollbackAllowed);
        Assert.False(context.LocalAiRecoveryCleanupAllowed);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Recovery_ThirdModelAcceptsOnlyKnownOwnedRoutes(
        bool publishedPendingModel, bool legacyGateway, bool providerMissing)
    {
        using var temp = new TempDirectory("local-ai-forward-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path, "openai/gpt-5");
        var store = new LocalAiManifestStore(new LocalAiPaths(temp.Path));
        LocalAiResolvedInstall pending = store.ResolveAndValidate(WithCatalogModel(
            ReplacementManifest(original), LocalModelCatalog.Qwen27BModelId));
        LocalAiInstallManifest selected = WithCatalogModel(pending.Manifest, LocalModelCatalog.Qwen38_27BModelId) with
        {
            Endpoint = "http://127.0.0.1:39879/v1",
            PreviousEndpoints = null,
            RecoveryRoutes = [LocalAiManifestStore.WithoutRecoveryHints(pending.Manifest)],
        };
        await store.SaveAsync(selected);
        LocalAiResolvedInstall published = publishedPendingModel ? pending : original;
        var commands = new GatewayStateCommandRunner(
            providerMissing ? null : LocalAiGatewayProviderDefinition.BuildProviderJson(published),
            JsonSerializer.Serialize(LocalAiGatewayProviderDefinition.BuildPrimaryModel(published)))
        {
            SupportsConditionalProviderSet = !legacyGateway,
        };
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        // Reopen from durable state, not from the original process's context.
        context.LocalAiResolvedInstall = (await store.LoadAsync())!;
        context.LocalAiRecoveryPendingInstall = context.LocalAiResolvedInstall;
        context.LocalAiRecoveryOriginalInstall = store.ResolveAndValidate(context.LocalAiResolvedInstall.Manifest.ReplacedManifest!);
        StepResult result = await new ConfigureLocalAiGatewayStep().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal(JsonSerializer.Serialize("llamacpp/qwen3.8-27b-mtp-ud-q4-k-m"), commands.PrimaryJson);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(commands.ProviderJson!, context.LocalAiResolvedInstall));
        Assert.DoesNotContain(commands.WslCalls, command => command.Contains("--expect-current-json", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Configure_UnqualifiedOptionalHistoryDoesNotBlockExternalFallback(
        bool unknownCatalog, bool replacedManifest)
    {
        using var temp = new TempDirectory("local-ai-invalid-history-");
        LocalAiResolvedInstall original = await SaveManifestAsync(temp.Path);
        var paths = new LocalAiPaths(temp.Path);
        var store = new LocalAiManifestStore(paths);
        LocalAiInstallManifest historical = WithCatalogModel(original.Manifest, LocalModelCatalog.Qwen27BModelId);
        await store.SaveAsync(original.Manifest with
        {
            ReplacedManifest = replacedManifest ? historical : null,
            RecoveryRoutes = replacedManifest ? [] : [historical],
        });
        // Corrupt only the optional file, leaving its canonical byte/time binding valid.
        byte[] canonical = await File.ReadAllBytesAsync(paths.ManifestPath);
        string hintsPath = Path.Combine(paths.RootDirectory, "recovery-hints.json");
        JsonNode hints = JsonNode.Parse(await File.ReadAllTextAsync(hintsPath))!;
        JsonNode route = replacedManifest ? hints["replacedManifest"]! : hints["recoveryRoutes"]![0]!;
        route[unknownCatalog ? "modelCatalogId" : "modelAlias"] =
            unknownCatalog ? "unknown-historical-model" : "mismatched-alias";
        await File.WriteAllTextAsync(hintsPath, hints.ToJsonString());

        var commands = new GatewayStateCommandRunner(null, JsonSerializer.Serialize("openai/gpt-5"));
        SetupContext context = CreateRecoveryContext(temp.Path, commands);
        context.LocalAiResolvedInstall = (await new LocalAiManifestStore(paths).LoadAsync())!;
        context.LocalAiRecoveryOriginalInstall = context.LocalAiResolvedInstall.Manifest.ReplacedManifest is { } prior
            ? store.ResolveAndValidate(prior) : null;
        Assert.Equal(canonical, await File.ReadAllBytesAsync(paths.ManifestPath));

        StepResult result = await new ConfigureLocalAiGatewayStep().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepOutcome.Success, result.Outcome);
        Assert.Equal(JsonSerializer.Serialize("llamacpp/qwen3.6-35b-a3b-mtp-q4-k-m"), commands.PrimaryJson);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(commands.ProviderJson!, context.LocalAiResolvedInstall));
        LocalAiResolvedInstall saved = (await store.LoadAsync())!;
        Assert.Equal("openai/gpt-5", saved.Manifest.GatewayFallbackModel);
        Assert.Null(saved.Manifest.ReplacedManifest);
        Assert.Empty(saved.Manifest.RecoveryRoutes);
    }

    private static LocalAiInstallManifest WithCatalogModel(LocalAiInstallManifest manifest, string modelId)
    {
        LocalModelInfo model = LocalModelCatalog.FindInstalled(modelId)!;
        var source = (HuggingFaceRevisionSource)model.Weights.Source;
        LocalInferenceRunProfile profile = LocalModelCatalog.GetProfiles(model)[0];
        return manifest with
        {
            ModelCatalogId = model.Id,
            ModelAlias = model.Id,
            ModelId = $"{source.RepositoryId}@{source.RevisionSha}",
            ModelPath = Path.Combine("models", model.Weights.RelativePath),
            ModelAsset = new LocalAiAssetReceipt
            {
                FileName = Path.GetFileName(model.Weights.RelativePath),
                SourceUrl = model.Weights.DownloadUri.AbsoluteUri,
                SizeBytes = model.Weights.SizeBytes,
                Sha256 = model.Weights.Sha256.Value,
            },
            ContextLength = profile.ContextTokens,
            KeyCachePrecision = profile.KeyCachePrecision,
            ValueCachePrecision = profile.ValueCachePrecision,
            DraftKeyCachePrecision = profile.DraftKeyCachePrecision,
            DraftValueCachePrecision = profile.DraftValueCachePrecision,
        };
    }

    private static SetupContext CreateContext(string localDataDirectory, ICommandRunner commands)
    {
        var config = new SetupConfig { LocalAi = new LocalAiConfig { Enabled = true } };
        var logger = new SetupLogger(filePath: null);
        return new SetupContext(
            config,
            logger,
            new TransactionJournal(filePath: null),
            commands,
            CancellationToken.None,
            localDataDir: localDataDirectory);
    }

    private static LocalAiInstallManifest ReplacementManifest(LocalAiResolvedInstall original) =>
        original.Manifest with
        {
            ModelCatalogId = LocalModelCatalog.Qwen27BModelId,
            ModelAlias = LocalModelCatalog.Qwen27BModelId,
            Endpoint = "http://127.0.0.1:39876/v1",
            ReplacedManifest = original.Manifest,
            PreviousEndpoints = [original.Endpoint!.AbsoluteUri],
        };

    private static SetupContext CreateRecoveryContext(
        string localDataDirectory,
        ICommandRunner commands)
    {
        SetupContext context = CreateContext(localDataDirectory, commands);
        context.Config.LocalAi.Enabled = true;
        context.Config.LocalAiRecoveryGatewayId = "gateway-id";
        context.DistroName = "OpenClawGateway";
        context.LocalAiEligibility = LocalInferenceEligibility.Evaluate(CreateSparkHardware());
        return context;
    }

    private static HostHardwareInfo CreateSparkHardware() => new(
        Architecture.Arm64,
        128L * 1024 * 1024 * 1024,
        100L * 1024 * 1024 * 1024,
        [
            new GpuInfo(
                GpuVendor.Nvidia,
                "NVIDIA RTX Spark N1X (6144-core Blackwell RTX GPU)",
                GpuVisibleMemoryBytes: 48L * 1024 * 1024 * 1024,
                FreeGpuVisibleMemoryBytes: 40L * 1024 * 1024 * 1024,
                DriverVersion: "616.00",
                CudaMajorVersion: 13,
                StableId: "GPU-SPARK"),
        ],
        VulkanAvailable: false);

    private static async Task<LocalAiResolvedInstall> SaveManifestAsync(
        string localDataDirectory,
        string? fallbackModel = null,
        int requestedPort = 0)
    {
        var paths = new LocalAiPaths(localDataDirectory);
        const string revision = "5bc3e238d916f48a861bac2f8a1990a0e9b7e98d";
        var manifest = new LocalAiInstallManifest
        {
            EngineVersion = "b10488",
            Architecture = "arm64",
            HardwareProfileId = "rtx-spark-n1x",
            RuntimeId = "b10488-cuda13-arm64",
            ModelCatalogId = LocalModelCatalog.Qwen35BModelId,
            SelectedGpuId = "GPU-SPARK",
            ExecutablePath = Path.Combine("engines", "llama-b10488", "llama-server.exe"),
            RuntimeAssets =
            [
                new LocalAiAssetReceipt
                {
                    FileName = "llama-runtime.zip",
                    SourceUrl = "https://github.com/ggml-org/llama.cpp/releases/download/b10488/llama-runtime.zip",
                    SizeBytes = 1,
                    Sha256 = new string('a', 64),
                },
            ],
            ModelPath = Path.Combine("models", "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf"),
            ModelId = $"unsloth/Qwen3.6-35B-A3B-MTP-GGUF@{revision}",
            ModelAlias = LocalModelCatalog.Qwen35BModelId,
            ModelAsset = new LocalAiAssetReceipt
            {
                FileName = "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf",
                SourceUrl = $"https://huggingface.co/unsloth/Qwen3.6-35B-A3B-MTP-GGUF/resolve/{revision}/Qwen3.6-35B-A3B-UD-Q4_K_M.gguf?download=true",
                SizeBytes = 1,
                Sha256 = new string('b', 64),
            },
            RequestedPort = requestedPort,
            Endpoint = "http://127.0.0.1:28765/v1",
            GatewayFallbackModel = fallbackModel,
            ContextLength = LocalModelCatalog.NativeContextTokens,
        };
        var store = new LocalAiManifestStore(paths);
        await store.SaveAsync(manifest);
        return (await store.LoadAsync())!;
    }

    internal sealed class GatewayStateCommandRunner(
        string? providerJson,
        string? primaryJson) : ICommandRunner
    {
        private const string ProviderMarker = "OPENCLAW_LOCAL_AI_PROVIDER_B64=";
        private const string PrimaryMarker = "OPENCLAW_LOCAL_AI_PRIMARY_B64=";

        public string? ProviderJson { get; private set; } = providerJson;
        public string? PrimaryJson { get; private set; } = primaryJson;
        public bool FailCapture { get; init; }
        public bool FailConfiguredBatchOnce { get; set; }
        public bool LoseConfiguredAcknowledgementOnce { get; set; }
        public bool FailRestoreBatchOnce { get; set; }
        public bool LoseRestoreAcknowledgementOnce { get; set; }
        public bool ThrowOnNextCapture { get; set; }
        public bool SupportsConditionalProviderSet { get; set; } = true;
        public string? PrimaryJsonAfterConditionalProviderSet { get; set; }
        public List<string> WslCalls { get; } = [];

        public Task<CommandResult> RunAsync(
            string executable,
            string[] arguments,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null,
            string? stdinInput = null,
            CancellationToken ct = default,
            Stream? stdinStream = null) => throw new NotSupportedException();

        public Task<CommandResult> RunInWslAsync(
            string distroName,
            string command,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken ct = default,
            string? user = null,
            bool inputViaStdin = false)
        {
            ct.ThrowIfCancellationRequested();
            WslCalls.Add(command);
            if (environment is not null && environment.Count == 2 &&
                command.Contains("--expect-current-json", StringComparison.Ordinal))
            {
                if (!SupportsConditionalProviderSet)
                {
                    return Task.FromResult(new CommandResult(
                        42,
                        "LOCAL_AI_CONDITIONAL_SET_UNSUPPORTED",
                        "",
                        TimeSpan.Zero,
                        TimedOut: false));
                }
                string providerJson = Encoding.UTF8.GetString(Convert.FromBase64String(
                    environment["OPENCLAW_LOCAL_AI_PROVIDER_B64"]));
                string expectedProviderJson = Encoding.UTF8.GetString(Convert.FromBase64String(
                    environment["OPENCLAW_LOCAL_AI_EXPECTED_PROVIDER_B64"]));
                using JsonDocument currentProvider = JsonDocument.Parse(ProviderJson!);
                using JsonDocument expectedProvider = JsonDocument.Parse(expectedProviderJson);
                if (!JsonElement.DeepEquals(currentProvider.RootElement, expectedProvider.RootElement))
                {
                    return Task.FromResult(new CommandResult(
                        1,
                        "",
                        "gateway provider changed",
                        TimeSpan.Zero,
                        TimedOut: false));
                }
                ProviderJson = providerJson;
                PrimaryJson = PrimaryJsonAfterConditionalProviderSet ?? PrimaryJson;
                return Task.FromResult(new CommandResult(
                    0,
                    "LOCAL_AI_GATEWAY_RESTORED",
                    "",
                    TimeSpan.Zero,
                    TimedOut: false));
            }
            if (environment is not null && environment.Count == 1)
            {
                if (FailRestoreBatchOnce &&
                    command.Contains("LOCAL_AI_GATEWAY_RESTORED", StringComparison.Ordinal))
                {
                    FailRestoreBatchOnce = false;
                    return Task.FromResult(new CommandResult(
                        1,
                        "",
                        "gateway rollback failed",
                        TimeSpan.Zero,
                        TimedOut: false));
                }
                if (FailConfiguredBatchOnce &&
                    command.Contains("LOCAL_AI_GATEWAY_CONFIGURED", StringComparison.Ordinal))
                {
                    FailConfiguredBatchOnce = false;
                    return Task.FromResult(new CommandResult(
                        1,
                        "",
                        "gateway configuration failed",
                        TimeSpan.Zero,
                        TimedOut: false));
                }
                string encoded = Assert.Single(environment).Value;
                string batch = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                using JsonDocument document = JsonDocument.Parse(batch);
                foreach (JsonElement operation in document.RootElement.EnumerateArray())
                {
                    string path = operation.GetProperty("path").GetString()!;
                    string value = operation.GetProperty("value").GetRawText();
                    if (path == LocalAiGatewayProviderDefinition.ProviderPath)
                        ProviderJson = value;
                    else if (path == LocalAiGatewayProviderDefinition.PrimaryModelPath)
                        PrimaryJson = value;
                }
                if (LoseRestoreAcknowledgementOnce &&
                    command.Contains("LOCAL_AI_GATEWAY_RESTORED", StringComparison.Ordinal))
                {
                    LoseRestoreAcknowledgementOnce = false;
                    return Task.FromResult(new CommandResult(
                        1,
                        "",
                        "gateway rollback acknowledgement lost",
                        TimeSpan.Zero,
                        TimedOut: false));
                }
                if (LoseConfiguredAcknowledgementOnce &&
                    command.Contains("LOCAL_AI_GATEWAY_CONFIGURED", StringComparison.Ordinal))
                {
                    LoseConfiguredAcknowledgementOnce = false;
                    return Task.FromResult(new CommandResult(
                        1,
                        "",
                        "gateway configuration acknowledgement lost",
                        TimeSpan.Zero,
                        TimedOut: false));
                }
                string marker =
                    command.Contains("LOCAL_AI_GATEWAY_CONFIGURED", StringComparison.Ordinal)
                        ? "LOCAL_AI_GATEWAY_CONFIGURED"
                        : command.Contains("LOCAL_AI_GATEWAY_RESTORED", StringComparison.Ordinal)
                            ? "LOCAL_AI_GATEWAY_RESTORED"
                            : "LOCAL_AI_PRIMARY_RESTORED";
                return Task.FromResult(new CommandResult(
                    0,
                    marker,
                    "",
                    TimeSpan.Zero,
                    TimedOut: false));
            }
            if (command.Contains("LOCAL_AI_GATEWAY_UNSET", StringComparison.Ordinal) ||
                command.Contains("openclaw config unset", StringComparison.Ordinal))
            {
                if (command.Contains(LocalAiGatewayProviderDefinition.PrimaryModelPath, StringComparison.Ordinal))
                    PrimaryJson = null;
                if (command.Contains(LocalAiGatewayProviderDefinition.ProviderPath, StringComparison.Ordinal))
                    ProviderJson = null;
                return Task.FromResult(new CommandResult(
                    0,
                    "LOCAL_AI_GATEWAY_UNSET",
                    "",
                    TimeSpan.Zero,
                    TimedOut: false));
            }
            if (ThrowOnNextCapture)
            {
                ThrowOnNextCapture = false;
                throw new OperationCanceledException(ct);
            }
            if (FailCapture)
            {
                return Task.FromResult(new CommandResult(
                    1,
                    "",
                    "openclaw config get failed",
                    TimeSpan.Zero,
                    TimedOut: false));
            }

            string stdout =
                ProviderMarker + EncodeOrMissing(ProviderJson) + Environment.NewLine +
                PrimaryMarker + EncodeOrMissing(PrimaryJson) + Environment.NewLine;
            return Task.FromResult(new CommandResult(0, stdout, "", TimeSpan.Zero, TimedOut: false));
        }

        private static string EncodeOrMissing(string? value) => value is null
            ? "MISSING"
            : Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private sealed class AcknowledgementFailingRuntime : ILocalAiRuntime
    {
        public LocalAiRuntimeSnapshot Snapshot => LocalAiRuntimeSnapshot.Initial(
            new Uri("http://127.0.0.1:18800/v1"),
            DateTimeOffset.UtcNow);
        public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }
        public Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LocalAiRuntimeSnapshot> ResumeAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LocalAiRuntimeSnapshot> AcknowledgeSetupGatewayRouteAsync(
            CancellationToken cancellationToken = default) =>
            throw new IOException("acknowledgement failed");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SettlementTrackingRuntime(LocalAiManifestStore store) : ILocalAiRuntime
    {
        public int RestartForSetupCalls { get; private set; }
        public int RestartForSetupRollbackCalls { get; private set; }

        public LocalAiRuntimeSnapshot Snapshot { get; private set; } = LocalAiRuntimeSnapshot.Initial(
            new Uri("http://127.0.0.1:18800/v1"),
            DateTimeOffset.UtcNow);

        public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LocalAiRuntimeSnapshot> ResumeAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LocalAiRuntimeSnapshot> StopAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LocalAiRuntimeSnapshot> RestartAsync(
            CancellationToken cancellationToken = default) => LoadSnapshotAsync(cancellationToken);

        public async Task<LocalAiRuntimeSnapshot> RestartForSetupAsync(
            CancellationToken cancellationToken = default)
        {
            RestartForSetupCalls++;
            return await LoadSnapshotAsync(cancellationToken);
        }

        public Task<LocalAiRuntimeSnapshot> RefreshAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LocalAiRuntimeSnapshot> AcknowledgeSetupGatewayRouteAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);

        public async Task<LocalAiRuntimeSnapshot> RestartForSetupRollbackAsync(
            CancellationToken cancellationToken = default)
        {
            RestartForSetupRollbackCalls++;
            return await LoadSnapshotAsync(cancellationToken);
        }

        private async Task<LocalAiRuntimeSnapshot> LoadSnapshotAsync(
            CancellationToken cancellationToken)
        {
            LocalAiResolvedInstall restored = await store.LoadAsync(cancellationToken)
                ?? throw new InvalidDataException("The restored Local AI receipt is unavailable.");
            Snapshot = HealthySnapshot(restored);
            return Snapshot;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrackingRuntimeAcquirer : ILlamaRuntimeAcquirer
    {
        public int RemoveCalls { get; private set; }

        public Task<LlamaRuntimeInstallResult> InstallAsync(
            string localDataDirectory,
            LlamaRuntimeVariant runtime,
            IProgress<LocalAiArtifactInstallProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public void RemoveInstalledRuntime(
            string localDataDirectory,
            LlamaRuntimeInstallResult install) => RemoveCalls++;
    }

    private static LocalAiRuntimeSnapshot HealthySnapshot(LocalAiResolvedInstall install) => new(
        LocalAiRuntimeState.Healthy,
        LocalAiOwnership.CompanionManaged,
        install.Endpoint!,
        install.Manifest.EngineVersion,
        install.Manifest.ModelCatalogId,
        new LocalAiModelEvidence(
            LocalAiModelAvailabilityState.Verified,
            DateTimeOffset.UtcNow,
            install.Manifest.ModelAsset.Sha256,
            install.Manifest.ModelAsset.SizeBytes),
        42,
        DateTimeOffset.UtcNow,
        null,
        DateTimeOffset.UtcNow)
    {
        GatewayRouteRequiresResolution = false,
    };

    private sealed class DelegatingRollbackStep(
        string id,
        Func<SetupContext, CancellationToken, Task> rollback,
        Func<SetupContext, CancellationToken, Task<StepResult>>? execute = null) : SetupStep
    {
        public override string Id => id;
        public override string DisplayName => id;
        public override bool CanRetry => false;

        public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct) =>
            execute?.Invoke(ctx, ct) ?? Task.FromResult(StepResult.Ok());

        public override Task RollbackAsync(SetupContext ctx, CancellationToken ct) =>
            rollback(ctx, ct);
    }
}
