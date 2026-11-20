using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference.Catalog;
using OpenClawTray.Services;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.Tray.Tests;

public sealed class LocalAiGatewayProviderCoordinatorTests
{
    [Fact]
    public async Task RpcPublish_UsesOneGuardedPatchAndPreservesOtherProvidersAndAllowlistEntries()
    {
        var install = Install(28_765, "cloud/prior");
        var transport = new AtomicTransport("""
            {"models":{"providers":{"other":{"apiKey":"other-secret"}}},
             "agents":{"defaults":{"model":{"primary":"cloud/prior","fallbacks":["cloud/backup"]},
                                   "models":{"cloud/prior":{"alias":"Cloud"}}}}}
            """);
        var coordinator = new LocalAiGatewayProviderCoordinator(transport, () => "actual-local-secret", NullLogger.Instance);

        var result = await coordinator.PublishAsync(install);

        Assert.True(result.Success, result.Detail);
        Assert.Single(transport.Patches);
        Assert.Equal("other-secret", (string?)transport.Config["models"]?["providers"]?["other"]?["apiKey"]);
        Assert.Equal("actual-local-secret", (string?)transport.Config["models"]?["providers"]?["llamacpp"]?["apiKey"]);
        Assert.Equal("Cloud", (string?)transport.Config["agents"]?["defaults"]?["models"]?["cloud/prior"]?["alias"]);
        Assert.Equal("cloud/backup", (string?)transport.Config["agents"]?["defaults"]?["model"]?["fallbacks"]?[0]);
        Assert.NotNull(transport.Config["agents"]?["defaults"]?["models"]?[LocalAiGatewayProviderDefinition.BuildPrimaryModel(install)]);
        Assert.DoesNotContain("other-secret", transport.Patches[0].GetRawText());
        Assert.Empty(Assert.Single(transport.ReplacementPaths));
    }

    [Fact]
    public async Task RpcPublish_MigratesOnlyLegacyModelArrayAndPreservesCredential()
    {
        var install = Install(28_765, "cloud/prior");
        var transport = new AtomicTransport("{}");
        transport.SetManaged(install);
        StripEffortMetadata((JsonObject)transport.Config["models"]!["providers"]!["llamacpp"]!["models"]![0]!);
        var oldProvider = transport.Config["models"]!["providers"]!["llamacpp"]!.ToJsonString();
        Assert.True(LocalAiGatewayProviderDefinition.MatchesLegacyProviderJson(oldProvider, install, "actual-local-secret"));
        Assert.False(LocalAiGatewayProviderDefinition.MatchesProviderJson(oldProvider, install, "actual-local-secret"));
        var coordinator = new LocalAiGatewayProviderCoordinator(transport, () => "actual-local-secret", NullLogger.Instance);

        var result = await coordinator.PublishAsync(install);

        Assert.True(result.Success, result.Detail);
        Assert.Single(transport.Patches);
        Assert.Equal([LocalAiGatewayProviderDefinition.ProviderModelsPath], Assert.Single(transport.ReplacementPaths));
        Assert.False(transport.Patches[0].GetProperty("models").GetProperty("providers")
            .GetProperty("llamacpp").TryGetProperty("apiKey", out _));
        Assert.Equal("actual-local-secret", (string?)transport.Config["models"]?["providers"]?["llamacpp"]?["apiKey"]);
        Assert.Equal("cloud/prior", install.Manifest.GatewayFallbackModel);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(
            transport.Config["models"]!["providers"]!["llamacpp"]!.ToJsonString(), install, "actual-local-secret"));
    }

    [Fact]
    public async Task RpcPublish_LegacyMigrationThatFailsReadbackKeepsTheUpgradedRouteAndReportsLocalizedFailure()
    {
        var install = Install(28_765, "cloud/prior");
        var transport = new AtomicTransport("{}") { DropThinkingLevelMapOnApply = true };
        transport.SetManaged(install);
        StripEffortMetadata((JsonObject)transport.Config["models"]!["providers"]!["llamacpp"]!["models"]![0]!);
        var coordinator = new LocalAiGatewayProviderCoordinator(
            transport, () => "actual-local-secret", NullLogger.Instance, key => $"localized:{key}");

        var result = await coordinator.PublishAsync(install);

        // The write landed but its readback does not match: report it, never retry or withdraw the route.
        Assert.False(result.Success);
        Assert.Equal("localized:LocalAi_GatewaySavedRouteVerification", result.Detail);
        Assert.Single(transport.Patches);
        Assert.Equal([LocalAiGatewayProviderDefinition.ProviderModelsPath], Assert.Single(transport.ReplacementPaths));
        Assert.Equal("actual-local-secret", (string?)transport.Config["models"]?["providers"]?["llamacpp"]?["apiKey"]);
        Assert.NotNull(transport.Config["models"]?["providers"]?["llamacpp"]?["models"]?[0]?["compat"]?["supportedReasoningEfforts"]);
        Assert.Equal(LocalAiGatewayProviderDefinition.BuildPrimaryModel(install),
            (string?)transport.Config["agents"]?["defaults"]?["model"]?["primary"]);
    }

    [Fact]
    public async Task WslPublish_WithdrawsLegacyProviderThenPublishesCurrentShape()
    {
        var install = Install(28_765);
        string managedPrimary = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
        string legacy = RedactApiKey(LegacyProviderJson(install));
        Assert.Contains(LocalAiGatewayProviderDefinition.CliRedactedApiKey, legacy);
        var commands = new FakeWslCommandRunner(legacy, managedPrimary)
        {
            ProviderAfterApply = RedactApiKey(LocalAiGatewayProviderDefinition.BuildProviderJson(install)),
            PrimaryAfterApply = managedPrimary,
        };

        var result = await CreateCoordinator(commands).PublishAsync(install);

        Assert.True(result.Success, result.Detail);
        int validation = commands.Calls.FindIndex(call => call.Contains("/bin/sh"));
        int unset = commands.Calls.FindIndex(call =>
            call.Contains("unset") && call.Contains(LocalAiGatewayProviderDefinition.ProviderPath));
        int publish = commands.FirstApplyIndex;
        int[] restarts = commands.Calls.Select((call, index) => (call, index))
            .Where(item => item.call.Contains("gateway") && item.call.Contains("restart"))
            .Select(item => item.index).ToArray();
        // The Gateway accepted the replacement before the working route was withdrawn.
        Assert.Contains("thinkingLevelMap", Assert.Single(commands.ValidatedBatches), StringComparison.Ordinal);
        Assert.InRange(validation, 0, unset - 1);
        Assert.InRange(unset, 0, publish - 1);
        Assert.Equal(2, restarts.Length);
        Assert.InRange(restarts[0], unset + 1, publish - 1);
        Assert.True(restarts[1] > publish, "The Gateway must activate the republished Local AI route.");
        Assert.DoesNotContain(commands.Calls, call => call.Contains(LocalAiGatewayProviderDefinition.PrimaryModelPath) &&
            !call.Contains("get"));
        Assert.DoesNotContain(commands.Calls, call => call.Contains(LocalAiGatewayProviderDefinition.ProviderModelsPath));

        // The fresh batch carries the canonical credential, never the redacted readback.
        string batch = DecodeBatch(commands.StandardInputs[publish]!);
        var written = JsonNode.Parse(batch)!.AsArray();
        JsonNode provider = written.Single(entry =>
            (string?)entry!["path"] == LocalAiGatewayProviderDefinition.ProviderPath)!["value"]!;
        Assert.Equal("llama-local", (string?)provider["apiKey"]);
        Assert.DoesNotContain(LocalAiGatewayProviderDefinition.CliRedactedApiKey, batch);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(provider.ToJsonString(), install));
        Assert.NotNull(provider["models"]![0]!["compat"]!["supportedReasoningEfforts"]);
        Assert.Equal(managedPrimary, commands.PrimaryModel);
        Assert.Equal(commands.ProviderJson, commands.ActiveProviderJson);
    }

    [Fact]
    public async Task WslPublish_LegacyWithdrawalFailureWritesNoNewProvider()
    {
        var install = Install(28_765);
        string managedPrimary = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
        string legacy = LegacyProviderJson(install);
        var commands = new FakeWslCommandRunner(legacy, managedPrimary)
        {
            IndeterminateRestartCalls = [1],
            ProviderAfterApply = LocalAiGatewayProviderDefinition.BuildProviderJson(install),
            PrimaryAfterApply = managedPrimary,
        };

        var result = await CreateCoordinator(commands).PublishAsync(install);

        Assert.False(result.Success);
        Assert.Equal(-1, commands.FirstApplyIndex);
        Assert.Single(commands.Calls, call => call.Contains("gateway") && call.Contains("restart"));
        Assert.Null(commands.ProviderJson);
        Assert.Equal(legacy, commands.ActiveProviderJson);
        Assert.Equal(managedPrimary, commands.PrimaryModel);
    }

    [Fact]
    public async Task WslPublish_CurrentRouteIsAcceptedWithoutChanges()
    {
        var install = Install(28_765);
        var commands = new FakeWslCommandRunner(LocalAiGatewayProviderDefinition.BuildProviderJson(install, "llama-local"),
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));

        var result = await CreateCoordinator(commands).PublishAsync(install);

        Assert.True(result.Success, result.Detail);
        Assert.DoesNotContain(commands.Calls, call => call.Contains("restart"));
        Assert.DoesNotContain(commands.Calls, call => call.Contains("set") || call.Contains("unset"));
    }

    [Fact]
    public async Task WslPublish_GatewayRejectingEffortMetadataKeepsLegacyRouteUntouched()
    {
        var install = Install(28_765);
        string managedPrimary = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
        string legacy = RedactApiKey(LegacyProviderJson(install));
        var commands = new FakeWslCommandRunner(legacy, managedPrimary) { RejectEffortMetadata = true };

        var result = await CreateCoordinator(commands).PublishAsync(install);

        // Gateway 2026.6.1 admits protocol 4 but rejects thinkingLevelMap:
        // the working pre-effort route is current there, so nothing is withdrawn.
        // The rejection is confirmed by a second dry-run before the fallback.
        Assert.True(result.Success, result.Detail);
        Assert.Equal(3, commands.ValidatedBatches.Count);
        Assert.Contains("thinkingLevelMap", commands.ValidatedBatches[0], StringComparison.Ordinal);
        Assert.Contains("thinkingLevelMap", commands.ValidatedBatches[1], StringComparison.Ordinal);
        Assert.DoesNotContain("thinkingLevelMap", commands.ValidatedBatches[2], StringComparison.Ordinal);
        Assert.DoesNotContain(commands.Calls, call => call.Contains("unset"));
        Assert.DoesNotContain(commands.Calls, call => call.Contains("restart"));
        Assert.Equal(-1, commands.FirstApplyIndex);
        Assert.Equal(legacy, commands.ProviderJson);
        Assert.Equal(legacy, commands.ActiveProviderJson);
        Assert.Equal(managedPrimary, commands.PrimaryModel);
    }

    [Fact]
    public async Task WslEndpointCycle_GatewayRejectingEffortMetadataRepublishesLegacyRoute()
    {
        var install = Install(28_765);
        var restarted = Install(28_766);
        string managedPrimary = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
        var commands = new FakeWslCommandRunner(RedactApiKey(LegacyProviderJson(install)), managedPrimary)
        {
            RejectEffortMetadata = true,
            ApplyWritesBatch = true,
        };
        var coordinator = CreateCoordinator(commands);

        // Normal runtime startup: withdraw the old endpoint, then publish the new one.
        var withdrawn = await coordinator.QuiesceAsync(install, LocalAiQuiesceReason.EndpointCycle);
        Assert.True(withdrawn.Success, withdrawn.Detail);
        Assert.Null(commands.ProviderJson);
        // Withdrawal never waits on the shape check.
        Assert.Empty(commands.ValidatedBatches);
        var published = await coordinator.PublishAsync(restarted);

        Assert.True(published.Success, published.Detail);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesLegacyProviderJson(commands.ProviderJson!, restarted));
        Assert.False(LocalAiGatewayProviderDefinition.MatchesProviderJson(commands.ProviderJson!, restarted));
        Assert.Equal(commands.ProviderJson, commands.ActiveProviderJson);
        Assert.Equal(managedPrimary, commands.PrimaryModel);
        // Publication decided the shape before its write.
        Assert.Equal(3, commands.ValidatedBatches.Count);
        Assert.All(commands.ValidationCallIndexes,
            index => Assert.InRange(index, 0, commands.FirstApplyIndex - 1));
    }

    [Fact]
    public async Task WslEndpointCycle_WithdrawalNeverWaitsOnTheShapeCheck()
    {
        var install = Install(28_765);
        string managedPrimary = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
        // Even a Gateway that would reject every provider: the runtime also
        // withdraws through an endpoint cycle to take the route off an untrusted listener.
        var commands = new FakeWslCommandRunner(LegacyProviderJson(install), managedPrimary) { RejectEveryProvider = true };

        var withdrawn = await CreateCoordinator(commands).QuiesceAsync(install, LocalAiQuiesceReason.EndpointCycle);

        Assert.True(withdrawn.Success, withdrawn.Detail);
        Assert.Empty(commands.ValidatedBatches);
        Assert.Null(commands.ProviderJson);
        Assert.Null(commands.ActiveProviderJson);
        Assert.Equal(managedPrimary, commands.PrimaryModel);
    }

    [Fact]
    public async Task WslPublish_GatewayRejectingBothShapesWritesNothing()
    {
        var install = Install(28_765);
        string managedPrimary = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
        string legacy = LegacyProviderJson(install);
        var commands = new FakeWslCommandRunner(legacy, managedPrimary) { RejectEveryProvider = true };
        var coordinator = new LocalAiGatewayProviderCoordinator(commands,
            new LocalAiGatewayDistroResolver(CreateRegistry(ManagedRecord("managed", "OpenClawGateway"))),
            NullLogger.Instance, key => $"localized:{key}");

        var published = await coordinator.PublishAsync(install);

        Assert.False(published.Success);
        Assert.Equal("localized:LocalAi_GatewayProviderRejected", published.Detail);
        Assert.Equal(3, commands.ValidatedBatches.Count);
        Assert.DoesNotContain(commands.Calls, call => call.Contains("unset"));
        Assert.DoesNotContain(commands.Calls, call => call.Contains("restart"));
        Assert.Equal(-1, commands.FirstApplyIndex);
        Assert.Equal(legacy, commands.ProviderJson);
        Assert.Equal(legacy, commands.ActiveProviderJson);
        Assert.Equal(managedPrimary, commands.PrimaryModel);
    }

    [Fact]
    public async Task WslPublish_OneFailedDryRunDoesNotDowngradeToTheLegacyShape()
    {
        var install = Install(28_765);
        string managedPrimary = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
        var commands = new FakeWslCommandRunner(RedactApiKey(LegacyProviderJson(install)), managedPrimary)
        {
            ApplyWritesBatch = true,
            FailedValidations = [0],
        };

        var result = await CreateCoordinator(commands).PublishAsync(install);

        // A Gateway that accepts effort metadata, with one unrelated CLI failure:
        // the repeated dry-run passes and the current shape is published.
        Assert.True(result.Success, result.Detail);
        Assert.Equal(2, commands.ValidatedBatches.Count);
        Assert.All(commands.ValidatedBatches,
            batch => Assert.Contains("thinkingLevelMap", batch, StringComparison.Ordinal));
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(commands.ProviderJson!, install));
        Assert.Equal(commands.ProviderJson, commands.ActiveProviderJson);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RpcPublish_WritesTheShapeTheGatewaySchemaAccepts(bool supportsEffort)
    {
        var install = Install(28_765, "cloud/prior");
        var transport = new AtomicTransport("""{"agents":{"defaults":{"model":{"primary":"cloud/prior"}}}}""")
        {
            SupportsEffortMetadata = supportsEffort,
        };
        var coordinator = new LocalAiGatewayProviderCoordinator(transport, () => "actual-local-secret", NullLogger.Instance);

        var result = await coordinator.PublishAsync(install);

        Assert.True(result.Success, result.Detail);
        Assert.Equal(1, transport.SchemaChecks);
        Assert.Single(transport.Patches);
        string provider = transport.Config["models"]!["providers"]!["llamacpp"]!.ToJsonString();
        Assert.Equal(supportsEffort,
            LocalAiGatewayProviderDefinition.MatchesProviderJson(provider, install, "actual-local-secret"));
        Assert.Equal(!supportsEffort,
            LocalAiGatewayProviderDefinition.MatchesLegacyProviderJson(provider, install, "actual-local-secret"));
        Assert.Equal(supportsEffort, transport.Patches[0].GetRawText().Contains("thinkingLevelMap", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RpcPublish_GatewayWithoutEffortSchemaKeepsLegacyProviderWithoutMigration()
    {
        var install = Install(28_765, "cloud/prior");
        var transport = new AtomicTransport("{}") { SupportsEffortMetadata = false };
        transport.SetManaged(install);
        StripEffortMetadata((JsonObject)transport.Config["models"]!["providers"]!["llamacpp"]!["models"]![0]!);
        string legacy = transport.Config.ToJsonString();
        var coordinator = new LocalAiGatewayProviderCoordinator(transport, () => "actual-local-secret", NullLogger.Instance);

        var result = await coordinator.PublishAsync(install);

        Assert.True(result.Success, result.Detail);
        Assert.Equal(1, transport.SchemaChecks);
        Assert.Empty(transport.Patches);
        Assert.Equal(legacy, transport.Config.ToJsonString());
    }

    [Fact]
    public async Task RpcEndpointCycle_WithdrawsWithoutSchemaCheckAndPublishWritesNothingWhenSchemaCannotBeRead()
    {
        var install = Install(28_765, "cloud/prior");
        var transport = new AtomicTransport("{}") { FailSchema = true };
        transport.SetManaged(install);
        StripEffortMetadata((JsonObject)transport.Config["models"]!["providers"]!["llamacpp"]!["models"]![0]!);

        // The runtime withdraws through an endpoint cycle and, as in production,
        // publishes through a separate coordinator instance.
        var cycle = await new LocalAiGatewayProviderCoordinator(
                transport, () => "actual-local-secret", NullLogger.Instance, key => $"localized:{key}")
            .QuiesceAsync(install, LocalAiQuiesceReason.EndpointCycle);

        Assert.True(cycle.Success, cycle.Detail);
        Assert.Equal(0, transport.SchemaChecks);
        Assert.Null(transport.Config["models"]?["providers"]?["llamacpp"]);
        string withdrawn = transport.Config.ToJsonString();
        int patches = transport.Patches.Count;

        var published = await new LocalAiGatewayProviderCoordinator(
                transport, () => "actual-local-secret", NullLogger.Instance, key => $"localized:{key}")
            .PublishAsync(install);

        Assert.False(published.Success);
        Assert.Equal("localized:LocalAi_GatewayProviderCheckFailed", published.Detail);
        Assert.DoesNotContain("sensitive", published.Detail);
        Assert.Equal(1, transport.SchemaChecks);
        Assert.Equal(patches, transport.Patches.Count);
        Assert.Equal(withdrawn, transport.Config.ToJsonString());
    }

    /// <summary>The exact provider shape published before effort metadata was added.</summary>
    private static string LegacyProviderJson(LocalAiResolvedInstall install, string apiKey = "llama-local")
    {
        var provider = JsonNode.Parse(LocalAiGatewayProviderDefinition.BuildProviderJson(install, apiKey))!.AsObject();
        StripEffortMetadata((JsonObject)provider["models"]![0]!);
        return provider.ToJsonString();
    }

    private static void StripEffortMetadata(JsonObject model)
    {
        var compat = (JsonObject)model["compat"]!;
        compat.Remove("supportsReasoningEffort");
        compat.Remove("supportedReasoningEfforts");
        compat.Remove("reasoningEffortMap");
        model.Remove("thinkingLevelMap");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RpcMutation_ConcurrentEditRejectsWholeChangeWithoutBlindRollback(bool quiesce)
    {
        var install = Install(28_765);
        var transport = new AtomicTransport("{}") { DriftOnApply = true };
        if (quiesce) transport.SetManaged(install);
        var coordinator = new LocalAiGatewayProviderCoordinator(transport, () => "actual-local-secret", NullLogger.Instance);

        var result = quiesce ? await coordinator.QuiesceAsync(install) : await coordinator.PublishAsync(install);

        Assert.False(result.Success);
        Assert.Single(transport.Patches);
        Assert.Equal("cloud/user-edit", (string?)transport.Config["agents"]?["defaults"]?["model"]?["primary"]);
        Assert.Contains("reconcile", result.Detail);
        Assert.DoesNotContain("secret", result.Detail);
        Assert.Equal(quiesce, transport.Config["models"]?["providers"]?["llamacpp"] is not null);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("cloud/prior", false)]
    [InlineData("cloud/prior", true)]
    public async Task RpcQuiesce_AtomicallyWithdrawsProviderAndConditionallyRestoresPrimary(string? fallback, bool cycle)
    {
        var install = Install(28_765, fallback);
        var transport = new AtomicTransport("{}");
        transport.SetManaged(install);
        var coordinator = new LocalAiGatewayProviderCoordinator(transport, () => "actual-local-secret", NullLogger.Instance);

        var result = await coordinator.QuiesceAsync(install,
            cycle ? LocalAiQuiesceReason.EndpointCycle : LocalAiQuiesceReason.Teardown);

        Assert.True(result.Success, result.Detail);
        Assert.Single(transport.Patches);
        Assert.Null(transport.Config["models"]?["providers"]?["llamacpp"]);
        Assert.Equal([LocalAiGatewayProviderDefinition.ProviderModelsPath],
            Assert.Single(transport.ReplacementPaths));
        Assert.Equal(cycle ? LocalAiGatewayProviderDefinition.BuildPrimaryModel(install) : fallback,
            (string?)transport.Config["agents"]?["defaults"]?["model"]?["primary"]);
    }

    [Theory]
    [InlineData("""{"agents":{"defaults":{"model":"cloud/legacy"}}}""")]
    [InlineData("""{"agents":{"defaults":{"models":[]}}}""")]
    [InlineData("""{"models":{"providers":{"llamacpp":{"baseUrl":"http://outside"}}}}""")]
    public async Task RpcPublish_InvalidOrExternallyOwnedConfigurationIsNeverPatched(string config)
    {
        var transport = new AtomicTransport(config);
        var coordinator = new LocalAiGatewayProviderCoordinator(transport, () => "actual-local-secret", NullLogger.Instance);

        var result = await coordinator.PublishAsync(Install(28_765));

        Assert.False(result.Success);
        Assert.Empty(transport.Patches);
    }

    [Fact]
    public async Task RpcPublish_UnknownReadOutcomeNeverMutatesOrLeaksRpcErrors()
    {
        var transport = new AtomicTransport("{}") { FailRead = true };
        var coordinator = new LocalAiGatewayProviderCoordinator(transport, () => "actual-local-secret", NullLogger.Instance);
        var result = await coordinator.PublishAsync(Install(28_765));
        Assert.False(result.Success);
        Assert.Empty(transport.Patches);
        Assert.DoesNotContain("sensitive", result.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RpcMutation_PersistedButUnavailableIsNotReplayedOrBlindlyRolledBack(bool quiesce)
    {
        var install = Install(28_765);
        var transport = new AtomicTransport("{}") { FailAfterApply = true };
        if (quiesce) transport.SetManaged(install);
        var coordinator = new LocalAiGatewayProviderCoordinator(transport, () => "actual-local-secret", NullLogger.Instance);

        var result = quiesce ? await coordinator.QuiesceAsync(install) : await coordinator.PublishAsync(install);

        Assert.False(result.Success);
        Assert.Contains("reconcile", result.Detail);
        Assert.Single(transport.Patches);
        Assert.Equal(!quiesce, transport.Config["models"]?["providers"]?["llamacpp"] is not null);
        Assert.Equal(quiesce ? null : LocalAiGatewayProviderDefinition.BuildPrimaryModel(install),
            (string?)transport.Config["agents"]?["defaults"]?["model"]?["primary"]);
    }

    private sealed class AtomicTransport(string config) : ILocalAiGatewayAtomicConfigurationTransport
    {
        public JsonObject Config { get; } = JsonNode.Parse(config)!.AsObject();
        public List<JsonElement> Patches { get; } = [];
        public List<string[]> ReplacementPaths { get; } = [];
        public bool DriftOnApply { get; init; }
        public bool FailRead { get; init; }
        public bool FailAfterApply { get; init; }
        public bool DropThinkingLevelMapOnApply { get; init; }
        /// <summary>False models a config.schema without thinkingLevelMap (Gateway 2026.6.1-2026.6.4).</summary>
        public bool SupportsEffortMetadata { get; init; } = true;
        public bool FailSchema { get; init; }
        public int SchemaChecks { get; private set; }
        private int _version;

        public Task<bool> SupportsEffortMetadataAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SchemaChecks++;
            if (FailSchema) throw new IOException("sensitive RPC response");
            return Task.FromResult(SupportsEffortMetadata);
        }

        public Task<LocalAiGatewayConfigurationSnapshot> CaptureAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (FailRead) throw new IOException("sensitive RPC response");
            return Task.FromResult(new LocalAiGatewayConfigurationSnapshot(
                JsonSerializer.SerializeToElement(Config), _version.ToString()));
        }

        public Task ApplyAsync(LocalAiGatewayConfigurationSnapshot expected, JsonElement patch, CancellationToken ct,
            IReadOnlyList<string>? replacePaths = null)
        {
            ct.ThrowIfCancellationRequested();
            Patches.Add(patch.Clone());
            ReplacementPaths.Add(replacePaths?.ToArray() ?? []);
            if (DriftOnApply)
            {
                Merge(Config, JsonNode.Parse("""{"agents":{"defaults":{"model":{"primary":"cloud/user-edit"}}}}""")!.AsObject());
                _version++;
            }
            if (expected.Hash != _version.ToString()) throw new InvalidOperationException("baseHash conflict sensitive RPC response");
            if (Config["models"]?["providers"]?["llamacpp"]?["models"] is JsonArray &&
                patch.TryGetProperty("models", out var models) &&
                models.TryGetProperty("providers", out var providers) &&
                providers.TryGetProperty("llamacpp", out _) &&
                replacePaths?.Contains(LocalAiGatewayProviderDefinition.ProviderModelsPath) != true)
                throw new InvalidOperationException("Destructive model array change requires its exact replacePaths entry.");
            Merge(Config, JsonNode.Parse(patch.GetRawText())!.AsObject());
            _version++;
            if (DropThinkingLevelMapOnApply &&
                Config["models"]?["providers"]?["llamacpp"]?["models"] is JsonArray written)
            {
                foreach (var model in written.OfType<JsonObject>()) model.Remove("thinkingLevelMap");
            }
            if (FailAfterApply) throw new IOException("UNAVAILABLE: configuration persisted but restart did not complete.");
            return Task.CompletedTask;
        }

        public void SetManaged(LocalAiResolvedInstall install) => Merge(Config, new JsonObject
        {
            ["models"] = new JsonObject { ["providers"] = new JsonObject
                { ["llamacpp"] = JsonNode.Parse(LocalAiGatewayProviderDefinition.BuildProviderJson(install, "actual-local-secret")) } },
            ["agents"] = new JsonObject { ["defaults"] = new JsonObject
                { ["model"] = new JsonObject { ["primary"] = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install) } } },
        });

        private static void Merge(JsonObject target, JsonObject patch)
        {
            foreach (var pair in patch)
            {
                if (pair.Value is null) target.Remove(pair.Key);
                else if (pair.Value is JsonObject nested && target[pair.Key] is JsonObject current) Merge(current, nested);
                else target[pair.Key] = pair.Value.DeepClone();
            }
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Publish_UncertainTransportReadNeverMutates(bool timedOut, bool indeterminate)
    {
        var transport = new UncertainTransport(timedOut, indeterminate);
        var coordinator = new LocalAiGatewayProviderCoordinator(transport, NullLogger.Instance);

        var result = await coordinator.PublishAsync(Install(28_765));

        Assert.False(result.Success);
        Assert.Contains("reliably", result.Detail);
        Assert.Equal(1, transport.Reads);
        Assert.Equal(0, transport.Mutations);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Quiesce_UncertainTransportReadNeverMutates(bool timedOut, bool indeterminate)
    {
        var transport = new UncertainTransport(timedOut, indeterminate);
        var coordinator = new LocalAiGatewayProviderCoordinator(transport, NullLogger.Instance);

        var result = await coordinator.QuiesceAsync(Install(28_765));

        Assert.False(result.Success);
        Assert.Equal(0, transport.Mutations);
    }

    private sealed class UncertainTransport(bool timedOut, bool indeterminate)
        : ILocalAiGatewayConfigurationTransport
    {
        public int Reads { get; private set; }
        public int Mutations { get; private set; }

        public Task<LocalAiGatewayCommandOutcome> RunAsync(
            IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            if (arguments is ["config", "get", _, "--json"])
                Reads++;
            else
                Mutations++;
            return Task.FromResult(new LocalAiGatewayCommandOutcome(
                new(0, "{}", "", timedOut, indeterminate), null));
        }

        public Task<LocalAiGatewayCommandOutcome> ApplyBatchAsync(
            string batch, CancellationToken cancellationToken)
        {
            Mutations++;
            return Task.FromResult(new LocalAiGatewayCommandOutcome(new(0, "", ""), null));
        }

        public Task<LocalAiGatewayCommandOutcome> ValidateBatchAsync(
            string batch, CancellationToken cancellationToken) =>
            Task.FromResult(new LocalAiGatewayCommandOutcome(new(0, "", "", timedOut, indeterminate), null));
    }

    [Fact]
    public async Task Quiesce_RemovesExactManagedRouteWhenNoFallbackExists()
    {
        LocalAiResolvedInstall install = Install(28_765);
        string expected = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        var commands = new FakeWslCommandRunner(
            expected,
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.True(result.Success);
        Assert.Null(commands.ProviderJson);
        Assert.Null(commands.PrimaryModel);
        Assert.Contains(commands.Calls, call => call.Contains("unset"));
    }

    [Fact]
    public async Task Quiesce_EndpointCycleRetainsManagedPrimaryWhenNoFallbackExists()
    {
        LocalAiResolvedInstall install = Install(28_765);
        string managedPrimary = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
        var commands = new FakeWslCommandRunner(
            LocalAiGatewayProviderDefinition.BuildProviderJson(install),
            managedPrimary);
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(
            install,
            LocalAiQuiesceReason.EndpointCycle);

        Assert.True(result.Success);
        Assert.Null(commands.ProviderJson);
        Assert.Equal(managedPrimary, commands.PrimaryModel);
        Assert.DoesNotContain(
            commands.Calls,
            call => call.Contains("unset") &&
                    call.Contains(LocalAiGatewayProviderDefinition.PrimaryModelPath));
    }

    [Fact]
    public async Task Quiesce_FailedEndpointCycleCanBeCompletedAsTeardown()
    {
        LocalAiResolvedInstall install = Install(28_765);
        string managedPrimary = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
        var commands = new FakeWslCommandRunner(
            LocalAiGatewayProviderDefinition.BuildProviderJson(install),
            managedPrimary)
        {
            FailedReadCalls = [2],
        };
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult interrupted = await coordinator.QuiesceAsync(
            install,
            LocalAiQuiesceReason.EndpointCycle);

        Assert.False(interrupted.Success);
        Assert.Null(commands.ProviderJson);
        Assert.Equal(managedPrimary, commands.PrimaryModel);

        LocalAiEndpointLifecycleResult teardown = await coordinator.QuiesceAsync(
            install,
            LocalAiQuiesceReason.Teardown);

        Assert.True(teardown.Success);
        Assert.Null(commands.ProviderJson);
        Assert.Null(commands.PrimaryModel);
    }

    [Fact]
    public async Task Quiesce_EndpointCycleRetainsManagedPrimaryWhenFallbackExists()
    {
        LocalAiResolvedInstall install = Install(28_770, "openai/gpt-5");
        string managedPrimary = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
        var commands = new FakeWslCommandRunner(
            LocalAiGatewayProviderDefinition.BuildProviderJson(install),
            managedPrimary);
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(
            install,
            LocalAiQuiesceReason.EndpointCycle);

        Assert.True(result.Success);
        Assert.Null(commands.ProviderJson);
        Assert.Equal(managedPrimary, commands.PrimaryModel);
    }

    [Fact]
    public async Task Quiesce_IndeterminateRestartKeepsActiveRouteUnresolved()
    {
        LocalAiResolvedInstall install = Install(28_770);
        string provider = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        var commands = new FakeWslCommandRunner(provider,
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install))
        {
            IndeterminateRestartCalls = [1],
        };
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.False(result.Success);
        Assert.Null(commands.ProviderJson);
        Assert.Equal(provider, commands.ActiveProviderJson);
        Assert.Contains("restart", result.Detail, StringComparison.OrdinalIgnoreCase);
        coordinator = CreateCoordinator(commands);
        Assert.True((await coordinator.QuiesceAsync(install)).Success);
        Assert.Null(commands.ActiveProviderJson);
        Assert.Equal(2, commands.Calls.Count(call => call.Contains("restart")));
        Assert.True((await coordinator.QuiesceAsync(install)).Success);
        Assert.Equal(3, commands.Calls.Count(call => call.Contains("restart")));
    }

    [Fact]
    public async Task Quiesce_TeardownRestoresRealPriorModel()
    {
        LocalAiResolvedInstall install = Install(28_770, "openai/gpt-5");
        var commands = new FakeWslCommandRunner(
            LocalAiGatewayProviderDefinition.BuildProviderJson(install),
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install))
        {
            PrimaryAfterApply = "openai/gpt-5",
        };
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(
            install,
            LocalAiQuiesceReason.Teardown);

        Assert.True(result.Success);
        Assert.Null(commands.ProviderJson);
        Assert.Equal("openai/gpt-5", commands.PrimaryModel);
    }

    [Fact]
    public async Task Publish_AcceptsPrimaryRetainedByAnEndpointCycle()
    {
        LocalAiResolvedInstall install = Install(28_765);
        string managedPrimary = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
        var commands = new FakeWslCommandRunner(providerJson: null, primaryModel: managedPrimary)
        {
            ProviderAfterApply = LocalAiGatewayProviderDefinition.BuildProviderJson(install),
            PrimaryAfterApply = managedPrimary,
        };
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.PublishAsync(install);

        Assert.True(result.Success);
        Assert.Equal(managedPrimary, commands.PrimaryModel);
    }

    [Fact]
    public async Task Publish_AcceptsPrimaryRetainedByEndpointCycleWithFallback()
    {
        LocalAiResolvedInstall install = Install(28_765, "openai/gpt-5");
        string managedPrimary = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
        var commands = new FakeWslCommandRunner(providerJson: null, primaryModel: managedPrimary)
        {
            ProviderAfterApply = LocalAiGatewayProviderDefinition.BuildProviderJson(install),
            PrimaryAfterApply = managedPrimary,
        };
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.PublishAsync(install);

        Assert.True(result.Success);
        Assert.Equal(managedPrimary, commands.PrimaryModel);
    }

    [Fact]
    public async Task Quiesce_AcceptsCliRedactedManagedApiKey()
    {
        LocalAiResolvedInstall install = Install(28_765);
        string observed = RedactApiKey(LocalAiGatewayProviderDefinition.BuildProviderJson(install));
        var commands = new FakeWslCommandRunner(
            observed,
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.True(result.Success);
        Assert.Null(commands.ProviderJson);
        Assert.Null(commands.PrimaryModel);
    }

    [Fact]
    public async Task Quiesce_PreservesProviderDriftAndFailsClosed()
    {
        LocalAiResolvedInstall install = Install(28_765);
        string drifted = LocalAiGatewayProviderDefinition.BuildProviderJson(install)
            .Replace("28765", "39876", StringComparison.Ordinal);
        var commands = new FakeWslCommandRunner(
            drifted,
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.False(result.Success);
        Assert.Equal(drifted, commands.ProviderJson);
        Assert.DoesNotContain(commands.Calls, call => call.Contains("unset"));
    }

    [Fact]
    public async Task Quiesce_NoProviderWithoutEndpoint_Succeeds()
    {
        LocalAiResolvedInstall install = InstallWithoutEndpoint(28_765);
        var commands = new FakeWslCommandRunner(providerJson: null);
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.True(result.Success);
        Assert.Null(commands.ProviderJson);
        Assert.Null(commands.PrimaryModel);
        Assert.DoesNotContain(commands.Calls, call => call.Contains("unset"));
        Assert.Single(commands.Calls, call => call.Contains("restart"));
    }

    [Theory]
    [InlineData(LocalAiQuiesceReason.Teardown)]
    [InlineData(LocalAiQuiesceReason.EndpointCycle)]
    public async Task Quiesce_EmptySavedRoutePreservesUnmanagedPrimaryAndConfirmsRunningGateway(LocalAiQuiesceReason reason)
    {
        var install = Install(28_765, "openai/gpt-5");
        var commands = new FakeWslCommandRunner(providerJson: null, primaryModel: "openai/gpt-5");
        var coordinator = CreateCoordinator(commands);

        Assert.True((await coordinator.QuiesceAsync(install, reason)).Success);
        Assert.True((await coordinator.QuiesceAsync(install, reason)).Success);
        Assert.Equal("openai/gpt-5", commands.PrimaryModel);
        Assert.DoesNotContain(commands.Calls, call => call.Contains("unset"));
        Assert.Equal(2, commands.Calls.Count(call => call.Contains("restart")));
    }

    [Fact]
    public async Task Quiesce_PreviouslyObservedActiveProviderStillRestartsAfterDiskWithdrawal()
    {
        var install = Install(28_765);
        var provider = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        var commands = new FakeWslCommandRunner(provider, LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
        var coordinator = CreateCoordinator(commands);
        Assert.True((await coordinator.PublishAsync(install)).Success);
        await commands.RunInDistroAsync("fixture", ["config", "unset", LocalAiGatewayProviderDefinition.ProviderPath]);
        Assert.Equal(provider, commands.ActiveProviderJson);

        Assert.True((await coordinator.QuiesceAsync(install, LocalAiQuiesceReason.EndpointCycle)).Success);
        Assert.Null(commands.ActiveProviderJson);
        Assert.Single(commands.Calls, call => call.Contains("restart"));
    }

    [Fact]
    public async Task Quiesce_ExistingProviderWithoutEndpoint_PreservesProviderAndFailsClosed()
    {
        LocalAiResolvedInstall runningInstall = Install(28_765);
        string provider = LocalAiGatewayProviderDefinition.BuildProviderJson(runningInstall);
        var commands = new FakeWslCommandRunner(
            provider,
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(runningInstall));
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(
            InstallWithoutEndpoint(28_765));

        Assert.False(result.Success);
        Assert.Contains("verified Local AI endpoint is required", result.Detail, StringComparison.Ordinal);
        Assert.Equal(provider, commands.ProviderJson);
        Assert.Equal(LocalAiGatewayProviderDefinition.BuildPrimaryModel(runningInstall), commands.PrimaryModel);
        Assert.DoesNotContain(commands.Calls, call => call.Contains("unset"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Quiesce_NoProviderWithUnqualifiedManagedModel_PreservesPrimaryAndFailsClosed(
        bool unknownCatalog)
    {
        LocalAiResolvedInstall valid = InstallWithoutEndpoint(28_765);
        LocalAiResolvedInstall tampered = valid with
        {
            Manifest = valid.Manifest with
            {
                ModelCatalogId = unknownCatalog ? "missing-model" : valid.Manifest.ModelCatalogId,
                ModelAlias = unknownCatalog ? valid.Manifest.ModelAlias : "tampered-model",
            },
        };
        string primary = $"llamacpp/{tampered.Manifest.ModelAlias}";
        var commands = new FakeWslCommandRunner(providerJson: null, primary);
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(tampered);

        Assert.False(result.Success);
        Assert.Contains("qualified", result.Detail, StringComparison.Ordinal);
        Assert.Null(commands.ProviderJson);
        Assert.Equal(primary, commands.PrimaryModel);
        Assert.DoesNotContain(commands.Calls, call => call.Contains("unset"));
        Assert.DoesNotContain(commands.Calls, call => call.Contains("/bin/sh"));
    }

    [Fact]
    public async Task Quiesce_NoProviderWithoutEndpoint_UnsetsManagedPrimary()
    {
        LocalAiResolvedInstall install = InstallWithoutEndpoint(28_765);
        var commands = new FakeWslCommandRunner(
            providerJson: null,
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install));
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.True(result.Success);
        Assert.Null(commands.ProviderJson);
        Assert.Null(commands.PrimaryModel);
        Assert.Contains(commands.Calls, call =>
            call.Contains("unset") &&
            call.Contains(LocalAiGatewayProviderDefinition.PrimaryModelPath));
    }

    [Fact]
    public async Task Quiesce_NoProviderWithoutEndpoint_RestoresFallbackPrimary()
    {
        LocalAiResolvedInstall install = InstallWithoutEndpoint(28_765, "openai/gpt-5");
        var commands = new FakeWslCommandRunner(
            providerJson: null,
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install))
        {
            PrimaryAfterApply = "openai/gpt-5",
        };
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.True(result.Success);
        Assert.Null(commands.ProviderJson);
        Assert.Equal("openai/gpt-5", commands.PrimaryModel);
        Assert.Contains(commands.Calls, call => call.Contains("/bin/sh"));
        Assert.DoesNotContain(commands.Calls, call =>
            call.Contains("unset") &&
            call.Contains(LocalAiGatewayProviderDefinition.ProviderPath));
    }

    [Fact]
    public async Task Publish_UsesVerifiedEndpointAndNonDefaultManagedDistro()
    {
        LocalAiResolvedInstall install = Install(28_766);
        string expected = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        var commands = new FakeWslCommandRunner(providerJson: null)
        {
            ProviderAfterApply = RedactApiKey(expected),
            PrimaryAfterApply = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install),
        };
        var coordinator = CreateCoordinator(commands, "CustomGateway");

        LocalAiEndpointLifecycleResult result = await coordinator.PublishAsync(install);

        Assert.True(result.Success);
        Assert.True(LocalAiGatewayProviderDefinition.MatchesProviderJson(commands.ProviderJson!, install));
        Assert.Equal(LocalAiGatewayProviderDefinition.BuildPrimaryModel(install), commands.PrimaryModel);
        // One dry-run-only schema check, then one dry-run-and-apply batch.
        Assert.Equal(2, commands.Calls.Count(call => call.Contains("/bin/sh")));
        string validation = Assert.IsType<string>(commands.StandardInputs[
            commands.Calls.FindIndex(call => call.Contains("/bin/sh"))]);
        Assert.EndsWith("openclaw config set --batch-file \"$batch_file\" --dry-run\n", validation, StringComparison.Ordinal);
        Assert.Contains("thinkingLevelMap", Assert.Single(commands.ValidatedBatches), StringComparison.Ordinal);
        IReadOnlyList<string> apply = commands.Calls[commands.FirstApplyIndex];
        Assert.Equal("-s", apply[^1]);
        Assert.DoesNotContain("-c", apply);
        Assert.DoesNotContain(apply, argument => argument.Contains("base64", StringComparison.Ordinal));
        string script = Assert.IsType<string>(commands.StandardInputs[commands.FirstApplyIndex]);
        Assert.StartsWith("set -e\n", script, StringComparison.Ordinal);
        Assert.Contains("batch_file=\"$(mktemp)\"\n", script, StringComparison.Ordinal);
        Assert.Contains("trap 'rm -f \"$batch_file\"' EXIT\n", script, StringComparison.Ordinal);
        Assert.Contains("| base64 -d > \"$batch_file\"\n", script, StringComparison.Ordinal);
        Assert.Contains(
            "openclaw config set --batch-file \"$batch_file\" --dry-run\n" +
            "openclaw config set --batch-file \"$batch_file\"\n",
            script, StringComparison.Ordinal);
        int publishIndex = commands.FirstApplyIndex;
        int restartIndex = commands.Calls.FindIndex(call => call.Contains("gateway") && call.Contains("restart"));
        Assert.True(restartIndex > publishIndex, "The Gateway must activate the newly published Local AI route.");
        Assert.DoesNotContain("/dev/stdin", script, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', script);
        Assert.All(commands.Distros, distro => Assert.Equal("CustomGateway", distro));
    }

    [Fact]
    public async Task Publish_VerificationFailureRemovesExactJustWrittenProvider()
    {
        LocalAiResolvedInstall install = Install(28_768);
        string expected = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        var commands = new FakeWslCommandRunner(providerJson: null)
        {
            ProviderAfterApply = expected,
            PrimaryAfterApply = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install),
            FailedReadCalls = [2],
        };
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.PublishAsync(install);

        Assert.False(result.Success);
        Assert.Contains("was removed", result.Detail, StringComparison.Ordinal);
        Assert.Null(commands.ProviderJson);
        Assert.Null(commands.PrimaryModel);
        Assert.Contains(commands.Calls, call => call.Contains("unset"));
    }

    [Fact]
    public async Task Publish_VerificationFailureSurfacesCleanupFailure()
    {
        LocalAiResolvedInstall install = Install(28_769);
        string expected = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        var commands = new FakeWslCommandRunner(providerJson: null)
        {
            ProviderAfterApply = expected,
            PrimaryAfterApply = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install),
            FailedReadCalls = [2, 3],
        };
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.PublishAsync(install);

        Assert.False(result.Success);
        Assert.Contains("Cleanup also failed", result.Detail, StringComparison.Ordinal);
        Assert.Equal(expected, commands.ProviderJson);
        Assert.Equal(LocalAiGatewayProviderDefinition.BuildPrimaryModel(install), commands.PrimaryModel);
        Assert.DoesNotContain(commands.Calls, call => call.Contains("unset"));
    }

    [Fact]
    public async Task Quiesce_DoesNotMistakeWslFailureForMissingProvider()
    {
        LocalAiResolvedInstall install = Install(28_767);
        var commands = new FakeWslCommandRunner(providerJson: null) { FailReads = true };
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Quiesce_RestoresFallbackBeforeRemovingProvider()
    {
        LocalAiResolvedInstall install = Install(28_770, "openai/gpt-5");
        var commands = new FakeWslCommandRunner(
            LocalAiGatewayProviderDefinition.BuildProviderJson(install),
            LocalAiGatewayProviderDefinition.BuildPrimaryModel(install))
        {
            PrimaryAfterApply = "openai/gpt-5",
        };
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.True(result.Success);
        Assert.Null(commands.ProviderJson);
        Assert.Equal("openai/gpt-5", commands.PrimaryModel);
        int restoreIndex = commands.Calls.FindIndex(call => call.Contains("/bin/sh"));
        int unsetIndex = commands.Calls.FindIndex(call =>
            call.Contains("unset") && call.Contains(LocalAiGatewayProviderDefinition.ProviderPath));
        Assert.True(restoreIndex >= 0 && unsetIndex > restoreIndex);
        Assert.Equal("-s", commands.Calls[restoreIndex][^1]);
        string restoreScript = Assert.IsType<string>(commands.StandardInputs[restoreIndex]);
        Assert.Contains("--batch-file \"$batch_file\" --dry-run", restoreScript, StringComparison.Ordinal);
        Assert.DoesNotContain("/dev/stdin", restoreScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_PreservesUnexpectedPrimaryAndFailsClosed()
    {
        LocalAiResolvedInstall install = Install(28_771, "openai/gpt-5");
        var commands = new FakeWslCommandRunner(providerJson: null, primaryModel: "anthropic/claude");
        var coordinator = CreateCoordinator(commands);

        LocalAiEndpointLifecycleResult result = await coordinator.PublishAsync(install);

        Assert.False(result.Success);
        Assert.Null(commands.ProviderJson);
        Assert.Equal("anthropic/claude", commands.PrimaryModel);
        Assert.DoesNotContain(commands.Calls, call => call.Contains("/bin/sh"));
    }

    [Fact]
    public async Task Publish_MissingManagedGatewayOwner_FailsWithoutWslCommand()
    {
        LocalAiResolvedInstall install = Install(28_772);
        var commands = new FakeWslCommandRunner(providerJson: null);
        var coordinator = CreateCoordinatorForRegistry(commands, CreateRegistry());

        LocalAiEndpointLifecycleResult result = await coordinator.PublishAsync(install);

        Assert.False(result.Success);
        Assert.Contains("No explicit setup-managed WSL gateway", result.Detail, StringComparison.Ordinal);
        Assert.Empty(commands.Calls);
        Assert.Empty(commands.Distros);
    }

    [Fact]
    public void LocalAiSetupRoute_UsesUniqueManagedOwnerEvenWhenItIsNotActive()
    {
        GatewayRecord owner = ManagedRecord("managed", "CustomGateway") with
        {
            Url = "ws://127.0.0.1:29999",
        };
        IReadOnlyList<GatewayRecord> owners =
            LocalAiGatewayDistroResolver.FindOwners([owner]);

        LocalAiSetupResolution resolution = LocalAiSetupRoutePolicy.Decide(
            owners,
            hasLocalGateway: true,
            localGatewayId: owner.Id,
            hasDistro: true,
            hasDistroDataDirectory: true,
            distroIsAppOwned: true,
            installedModelCatalogId: LocalModelCatalog.Qwen38_27BModelId,
            installedRequestedLocalAiPort: 28888);

        Assert.Equal(LocalAiSetupRoute.Recovery, resolution.Route);
        Assert.Equal("managed", resolution.RecoveryTarget?.GatewayId);
        Assert.Equal("CustomGateway", resolution.RecoveryTarget?.DistroName);
        Assert.Equal(29999, resolution.RecoveryTarget?.GatewayPort);
        Assert.Equal(LocalModelCatalog.Qwen38_27BModelId, resolution.RecoveryTarget?.ModelCatalogId);
        Assert.Equal(28888, resolution.RecoveryTarget?.RequestedLocalAiPort);
    }

    [Fact]
    public void LocalAiSetupRoute_AcceptsLegacyManagedOwner()
    {
        GatewayRecord owner = new()
        {
            Id = "legacy-managed",
            Url = "ws://localhost:18789",
            IsLocal = true,
            FriendlyName = "Local (LegacyGateway)",
        };
        IReadOnlyList<GatewayRecord> owners =
            LocalAiGatewayDistroResolver.FindOwners([owner]);

        LocalAiSetupResolution resolution = LocalAiSetupRoutePolicy.Decide(
            owners,
            hasLocalGateway: true,
            localGatewayId: owner.Id,
            hasDistro: true,
            hasDistroDataDirectory: true,
            distroIsAppOwned: true,
            installedModelCatalogId: LocalModelCatalog.Qwen38_27BModelId);

        Assert.Equal(LocalAiSetupRoute.Recovery, resolution.Route);
        Assert.Equal("LegacyGateway", resolution.RecoveryTarget?.DistroName);
    }

    [Theory]
    [InlineData("http://127.0.0.1:18789")]
    [InlineData("ws://127.0.0.1:18789/path")]
    [InlineData("ws://127.0.0.1:18789?query=1")]
    public void LocalAiSetupRoute_BlocksNonCanonicalManagedEndpoint(string url)
    {
        GatewayRecord owner = ManagedRecord("managed", "OpenClawGateway") with
        {
            Url = url,
        };

        LocalAiSetupResolution resolution = LocalAiSetupRoutePolicy.Decide(
            [owner],
            hasLocalGateway: true,
            localGatewayId: owner.Id,
            hasDistro: true,
            hasDistroDataDirectory: true,
            distroIsAppOwned: true,
            installedModelCatalogId: LocalModelCatalog.Qwen38_27BModelId);

        Assert.Equal(LocalAiSetupRoute.Blocked, resolution.Route);
    }

    [Fact]
    public void LocalAiSetupRoute_RecoversManagedGatewayWithoutAnInstalledModelReceipt()
    {
        GatewayRecord owner = ManagedRecord("managed", "OpenClawGateway");

        LocalAiSetupResolution resolution = LocalAiSetupRoutePolicy.Decide(
            [owner],
            hasLocalGateway: true,
            localGatewayId: owner.Id,
            hasDistro: true,
            hasDistroDataDirectory: true,
            distroIsAppOwned: true);

        Assert.Equal(LocalAiSetupRoute.Recovery, resolution.Route);
        Assert.Null(resolution.RecoveryTarget?.ModelCatalogId);
        Assert.Null(resolution.RecoveryTarget?.RequestedLocalAiPort);
    }

    [Fact]
    public void DistroResolver_ResolvesLegacyManagedOwner()
    {
        GatewayRecord owner = new()
        {
            Id = "legacy-managed",
            Url = "ws://localhost:18789",
            IsLocal = true,
            FriendlyName = "Local (LegacyGateway)",
        };
        var resolver = new LocalAiGatewayDistroResolver(CreateRegistry(owner));

        LocalAiGatewayDistroResolution resolution = resolver.Resolve();

        Assert.True(resolution.Success);
        Assert.Equal("LegacyGateway", resolution.DistroName);
    }

    [Fact]
    public void LocalAiSetupRoute_ProvisionsOnlyWhenManagedGatewayIsConclusivelyAbsent()
    {
        LocalAiSetupResolution resolution = LocalAiSetupRoutePolicy.Decide(
            owners: [],
            hasLocalGateway: false,
            localGatewayId: null,
            hasDistro: false,
            hasDistroDataDirectory: false,
            distroIsAppOwned: false);

        Assert.Equal(LocalAiSetupRoute.Provision, resolution.Route);
        Assert.Null(resolution.RecoveryTarget);
    }

    [Fact]
    public void LocalAiSetupRoute_BlocksAmbiguousOrStaleOwnership()
    {
        IReadOnlyList<GatewayRecord> ambiguousOwners =
        [
            ManagedRecord("managed-a", "GatewayA"),
            ManagedRecord("managed-b", "GatewayB"),
        ];
        Assert.Equal(
            LocalAiSetupRoute.Blocked,
            LocalAiSetupRoutePolicy.Decide(
                ambiguousOwners,
                hasLocalGateway: true,
                localGatewayId: "managed-a",
                hasDistro: true,
                hasDistroDataDirectory: true,
                distroIsAppOwned: true).Route);

        GatewayRecord staleOwner = ManagedRecord("managed", "OpenClawGateway");
        Assert.Equal(
            LocalAiSetupRoute.Blocked,
            LocalAiSetupRoutePolicy.Decide(
                [staleOwner],
                hasLocalGateway: true,
                localGatewayId: staleOwner.Id,
                hasDistro: false,
                hasDistroDataDirectory: true,
                distroIsAppOwned: true).Route);
    }

    [Fact]
    public async Task Publish_AmbiguousManagedGatewayOwners_FailsWithoutWslCommand()
    {
        LocalAiResolvedInstall install = Install(28_773);
        var commands = new FakeWslCommandRunner(providerJson: null);
        GatewayRegistry registry = CreateRegistry(
            ManagedRecord("managed-a", "GatewayA"),
            ManagedRecord("managed-b", "GatewayB"));
        var coordinator = CreateCoordinatorForRegistry(commands, registry);

        LocalAiEndpointLifecycleResult result = await coordinator.PublishAsync(install);

        Assert.False(result.Success);
        Assert.Contains("Multiple explicit setup-managed WSL gateways", result.Detail, StringComparison.Ordinal);
        Assert.Empty(commands.Calls);
        Assert.Empty(commands.Distros);
    }

    [Fact]
    public async Task Quiesce_RegistryUnavailable_FailsWithoutWslCommand()
    {
        LocalAiResolvedInstall install = Install(28_774);
        var commands = new FakeWslCommandRunner(providerJson: null);
        var coordinator = CreateCoordinatorForRegistry(commands, registry: null);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.False(result.Success);
        Assert.Contains("registry is unavailable", result.Detail, StringComparison.Ordinal);
        Assert.Empty(commands.Calls);
        Assert.Empty(commands.Distros);
    }

    [Fact]
    public async Task Quiesce_OwnerDriftsAfterInspection_BlocksFirstMutation()
    {
        LocalAiResolvedInstall install = Install(28_775);
        GatewayRegistry registry = CreateRegistry(ManagedRecord("managed", "CustomGateway"));
        string provider = LocalAiGatewayProviderDefinition.BuildProviderJson(install);
        string primary = LocalAiGatewayProviderDefinition.BuildPrimaryModel(install);
        var commands = new FakeWslCommandRunner(provider, primary)
        {
            CommandObserved = callCount =>
            {
                if (callCount == 2)
                {
                    registry.Update(
                        "managed",
                        record => record with { SetupManagedDistroName = "UnexpectedGateway" });
                }
            },
        };
        var coordinator = CreateCoordinatorForRegistry(commands, registry);

        LocalAiEndpointLifecycleResult result = await coordinator.QuiesceAsync(install);

        Assert.False(result.Success);
        Assert.Contains("owner changed", result.Detail, StringComparison.Ordinal);
        Assert.Equal(provider, commands.ProviderJson);
        Assert.Equal(primary, commands.PrimaryModel);
        Assert.Equal(2, commands.Calls.Count);
        Assert.DoesNotContain(commands.Calls, call => call.Contains("unset"));
    }

    private static LocalAiGatewayProviderCoordinator CreateCoordinator(
        FakeWslCommandRunner commands,
        string distroName = "OpenClawGateway") =>
        CreateCoordinatorForRegistry(
            commands,
            CreateRegistry(ManagedRecord("managed", distroName)));

    private static LocalAiGatewayProviderCoordinator CreateCoordinatorForRegistry(
        FakeWslCommandRunner commands,
        GatewayRegistry? registry) =>
        new(
            commands,
            new LocalAiGatewayDistroResolver(registry),
            NullLogger.Instance);

    private static GatewayRegistry CreateRegistry(params GatewayRecord[] records)
    {
        var registry = new GatewayRegistry(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        foreach (GatewayRecord record in records)
            registry.AddOrUpdate(record);
        return registry;
    }

    private static GatewayRecord ManagedRecord(string id, string distroName) => new()
    {
        Id = id,
        Url = "ws://localhost:18789",
        IsLocal = true,
        SetupManagedDistroName = distroName,
    };

    private static LocalAiResolvedInstall Install(int port, string? fallbackModel = null)
    {
        var endpoint = new Uri($"http://127.0.0.1:{port}/v1");
        var manifest = new LocalAiInstallManifest
        {
            EngineVersion = "b10488",
            Architecture = "arm64",
            HardwareProfileId = "rtx-spark-n1x",
            RuntimeId = "b10488-cuda13-arm64",
            ModelCatalogId = LocalModelCatalog.Qwen35BModelId,
            SelectedGpuId = "GPU-SPARK",
            ExecutablePath = "engines/llama-server.exe",
            RuntimeAssets = ImmutableArray<LocalAiAssetReceipt>.Empty,
            ModelPath = "models/model.gguf",
            ModelId = "owner/model@0123456789abcdef0123456789abcdef01234567",
            ModelAlias = LocalModelCatalog.Qwen35BModelId,
            ModelAsset = new LocalAiAssetReceipt
            {
                FileName = "model.gguf",
                SourceUrl = "https://huggingface.co/owner/model/resolve/0123456789abcdef0123456789abcdef01234567/model.gguf",
                SizeBytes = 1,
                Sha256 = new string('a', 64),
            },
            RequestedPort = 0,
            Endpoint = endpoint.AbsoluteUri,
            GatewayFallbackModel = fallbackModel,
            ContextLength = LocalModelCatalog.NativeContextTokens,
        };
        return new(manifest, "llama-server.exe", "model.gguf", endpoint);
    }

    private static LocalAiResolvedInstall InstallWithoutEndpoint(
        int port,
        string? fallbackModel = null)
    {
        LocalAiResolvedInstall install = Install(port, fallbackModel);
        return install with
        {
            Manifest = install.Manifest with { Endpoint = null },
            Endpoint = null,
        };
    }

    private static string DecodeBatch(string script)
    {
        const string prefix = "printf '%s' '";
        int start = script.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(
            script[start..script.IndexOf('\'', start)]));
    }

    private static string RedactApiKey(string value) => value.Replace(
        "\"api\":\"openai-completions\",\"apiKey\":\"llama-local\"",
        $"\"apiKey\":\"{LocalAiGatewayProviderDefinition.CliRedactedApiKey}\",\"api\":\"openai-completions\"",
        StringComparison.Ordinal);

    private sealed class FakeWslCommandRunner(string? providerJson, string? primaryModel = null) : IWslCommandRunner
    {
        public string? ProviderJson { get; private set; } = providerJson;
        public string? ActiveProviderJson { get; private set; } = providerJson;
        public string? PrimaryModel { get; private set; } = primaryModel;
        public string? ProviderAfterApply { get; init; }
        public string? PrimaryAfterApply { get; init; }
        public bool FailReads { get; init; }
        public HashSet<int> FailedReadCalls { get; init; } = [];
        public HashSet<int> IndeterminateRestartCalls { get; init; } = [];
        /// <summary>Models Gateway 2026.6.1-2026.6.4: protocol 4, but no thinkingLevelMap in its model schema.</summary>
        public bool RejectEffortMetadata { get; init; }
        public bool RejectEveryProvider { get; init; }
        /// <summary>Zero-based dry-run ordinals that fail once for a reason unrelated to the schema.</summary>
        public HashSet<int> FailedValidations { get; init; } = [];
        /// <summary>Persists the provider and primary from an applied batch instead of the preset values.</summary>
        public bool ApplyWritesBatch { get; init; }
        public Action<int>? CommandObserved { get; init; }
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public List<string?> StandardInputs { get; } = [];
        public List<string> Distros { get; } = [];
        public List<string> ValidatedBatches { get; } = [];
        public List<int> ValidationCallIndexes { get; } = [];
        public int FirstApplyIndex => StandardInputs.FindIndex(IsApplyScript);
        private int _readCalls;
        private int _restartCalls;

        private static bool IsApplyScript(string? script) =>
            script?.Contains("--batch-file \"$batch_file\"\n", StringComparison.Ordinal) == true;

        public Task<WslCommandResult> RunInDistroAsync(
            string name,
            IReadOnlyList<string> command,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null,
            string? standardInput = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Distros.Add(name);
            Calls.Add(command.ToArray());
            StandardInputs.Add(standardInput);
            CommandObserved?.Invoke(Calls.Count);
            bool providerRead = command.Contains("get") &&
                command.Contains(LocalAiGatewayProviderDefinition.ProviderPath);
            if (providerRead && (FailReads || FailedReadCalls.Contains(++_readCalls)))
                return Result(1, string.Empty, "wsl.exe failed");
            if (command.Contains("/bin/sh"))
            {
                string batch = DecodeBatch(standardInput!);
                bool rejected = batch.Contains(LocalAiGatewayProviderDefinition.ProviderPath, StringComparison.Ordinal) &&
                    (RejectEveryProvider ||
                        RejectEffortMetadata && batch.Contains("thinkingLevelMap", StringComparison.Ordinal));
                if (!IsApplyScript(standardInput))
                {
                    ValidatedBatches.Add(batch);
                    ValidationCallIndexes.Add(Calls.Count - 1);
                    if (FailedValidations.Contains(ValidatedBatches.Count - 1))
                        return Result(1, string.Empty, "config file is locked");
                    return rejected ? Result(1, string.Empty, "Unrecognized key: \"thinkingLevelMap\"") : Result(0, string.Empty);
                }
                if (rejected)
                    return Result(1, string.Empty, "Unrecognized key: \"thinkingLevelMap\"");
                if (ApplyWritesBatch)
                {
                    foreach (JsonNode? entry in JsonNode.Parse(batch)!.AsArray())
                    {
                        if ((string?)entry!["path"] == LocalAiGatewayProviderDefinition.ProviderPath)
                            ProviderJson = entry["value"]!.ToJsonString();
                        else if ((string?)entry["path"] == LocalAiGatewayProviderDefinition.PrimaryModelPath)
                            PrimaryModel = (string?)entry["value"];
                    }
                    return Result(0, string.Empty);
                }
                if (ProviderAfterApply is not null)
                    ProviderJson = ProviderAfterApply;
                PrimaryModel = PrimaryAfterApply;
                return Result(PrimaryModel is null ? 1 : 0, string.Empty);
            }
            if (command.Contains("gateway") && command.Contains("restart"))
            {
                if (IndeterminateRestartCalls.Contains(++_restartCalls))
                    return Task.FromResult(new WslCommandResult(-1, string.Empty, string.Empty,
                        TimedOut: true, OutcomeIndeterminate: true));
                ActiveProviderJson = ProviderJson;
                return Result(0, string.Empty);
            }
            if (command.Contains("unset"))
            {
                if (command.Contains(LocalAiGatewayProviderDefinition.ProviderPath))
                    ProviderJson = null;
                if (command.Contains(LocalAiGatewayProviderDefinition.PrimaryModelPath))
                    PrimaryModel = null;
                return Result(0, string.Empty);
            }
            if (command.Contains(LocalAiGatewayProviderDefinition.ProviderPath))
                return ProviderJson is null
                    ? Result(
                        1,
                        string.Empty,
                        $"Config path not found: {LocalAiGatewayProviderDefinition.ProviderPath}")
                    : Result(0, ProviderJson);
            if (command.Contains(LocalAiGatewayProviderDefinition.PrimaryModelPath))
                return PrimaryModel is null
                    ? Result(
                        1,
                        string.Empty,
                        $"Config path not found: {LocalAiGatewayProviderDefinition.PrimaryModelPath}")
                    : Result(0, JsonSerializer.Serialize(PrimaryModel));
            return Result(1, string.Empty);
        }

        public Task<WslCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null) => Result(1, string.Empty);

        public Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WslDistroInfo>>([]);

        public Task<WslCommandResult> TerminateDistroAsync(
            string name,
            CancellationToken cancellationToken = default) => Result(1, string.Empty);

        public Task<WslCommandResult> UnregisterDistroAsync(
            string name,
            CancellationToken cancellationToken = default) => Result(1, string.Empty);

        private static Task<WslCommandResult> Result(
            int exitCode,
            string stdout,
            string stderr = "") =>
            Task.FromResult(new WslCommandResult(exitCode, stdout, stderr));
    }
}
