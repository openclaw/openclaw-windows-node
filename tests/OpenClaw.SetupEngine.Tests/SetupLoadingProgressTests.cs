using System.Text.Json;

namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupLoadingProgressTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void PipelineRoleNotOptionalLocalAiSelectsConsolidatedGroup(bool nativeAcquisition, bool recoveryOnly, bool consolidated)
    {
        var config = new SetupConfig { NativeLocalAiAcquisition = nativeAcquisition };
        config.LocalAi.Enabled = true;
        var group = SetupLoadingProgress.PipelineGroup(config.NativeLocalAiAcquisition, recoveryOnly);
        if (consolidated) Assert.Equal(SetupLoadingGroup.LocalAi, group);
        else Assert.Null(group);
    }

    [Fact]
    public void AutomaticLocalAiOwnerTransferPreservesGroupAndRejectsOldProgress()
    {
        using var loading = new SetupLoadingProgress();
        using var acquisition = loading.Begin(SetupLoadingGroup.LocalAi, SetupLoadingStep.CheckArtifacts);
        acquisition.ReportActivity("Downloading selected model");
        var bytes = new SetupLoadingMeasurement("model.gguf", 512, 1024, true);
        acquisition.ReportDetail(bytes);
        Assert.Equal(bytes, loading.Current!.Detail);
        using var runtime = loading.Begin(SetupLoadingGroup.LocalAi, SetupLoadingStep.PrepareLocalAi);
        acquisition.ReportDetail(bytes with { Completed = 1024 });
        acquisition.ReportActivity("obsolete artifact callback");
        acquisition.Dispose();
        Assert.True(runtime.IsCurrent);
        Assert.Equal(SetupLoadingGroup.LocalAi, loading.Current!.Group);
        Assert.Equal(SetupLoadingStep.PrepareLocalAi, loading.Current.Step);
        Assert.Null(loading.Current.Detail);
        foreach (var step in new[] { SetupLoadingStep.StartLocalAi, SetupLoadingStep.PublishProvider, SetupLoadingStep.VerifyModel })
        {
            runtime.Report(step);
            Assert.Equal(SetupLoadingGroup.LocalAi, loading.Current!.Group);
            Assert.Equal(step, loading.Current.Step);
        }
        using var finishing = loading.Begin(SetupLoadingGroup.Finishing, SetupLoadingStep.Drain);
        runtime.Report(SetupLoadingStep.StartLocalAi);
        Assert.Equal(SetupLoadingGroup.Finishing, loading.Current!.Group);
        loading.Dispose();
        finishing.Report(SetupLoadingStep.VerifyModel);
        Assert.Null(loading.Current);
    }

    [Fact]
    public void DownloadCallbacksCannotOverwriteTheNextOperationInTheSameOwner()
    {
        using var loading = new SetupLoadingProgress();
        using var scope = loading.Begin(SetupLoadingGroup.LocalAi, SetupLoadingStep.AcquireArtifacts);
        scope.ReportActivity("Download", "download");
        scope.ReportDetail(new("model", 10, 20, true), "download");
        Assert.NotNull(loading.Current!.Detail);
        scope.ReportActivity("Check files", "verify");
        scope.ReportDetail(new("late model", 20, 20, true), "download");
        Assert.Null(loading.Current.Detail);
        Assert.Equal("Check files", loading.Current.Activity);
    }

    [Fact]
    public async Task VerificationReportsActualSubstepsBeforeEachPausedRead()
    {
        var proof = new GatewayAiSetupCompletion(SetupCompletionIntent.Dashboard, "gateway", new string('A', 64),
            "provider/model", "main", 1, IdentityBinding: new string('B', 64), SessionKey: "agent:main:main");
        using var loading = new SetupLoadingProgress();
        using var scope = loading.Begin(SetupLoadingGroup.Finishing, SetupLoadingStep.ConnectGateway);
        var transport = new PausedTransport(proof);
        var verifying = SetupNativeReadyBinding.VerifyAsync(transport, proof, default, progress: scope);
        Assert.Equal(SetupLoadingStep.CheckConfiguration, loading.Current!.Step);
        Assert.False(verifying.IsCompleted);
        transport.ConfigBefore.SetResult(JsonSerializer.SerializeToElement(new { hash = "revision", valid = true }));
        await transport.ModelEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SetupLoadingStep.VerifyModel, loading.Current.Step);
        transport.Model.SetResult(JsonSerializer.SerializeToElement(new { ok = true, modelRef = proof.ModelRef, latencyMs = 1 }));
        await transport.ConfigAfterEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SetupLoadingStep.CheckConfiguration, loading.Current.Step);
        transport.ConfigAfter.SetResult(JsonSerializer.SerializeToElement(new { hash = "revision", valid = true }));
        var ready = await verifying;
        Assert.Equal(proof.ModelRef, ready.Proof.ModelRef);
        Assert.Equal(1, transport.ModelCalls);
        Assert.Equal(SetupLoadingGroup.Finishing, loading.Current.Group);
    }

    [Fact]
    public async Task DiscoveryReportsBeforeAwaitWithoutAddingAnActivation()
    {
        using var loading = new SetupLoadingProgress();
        using var scope = loading.Begin(SetupLoadingGroup.GatewayPreparation, SetupLoadingStep.ConnectGateway);
        var proof = new GatewayAiSetupCompletion(SetupCompletionIntent.Dashboard, "gateway", new string('A', 64),
            "provider/model", "main", 1, IdentityBinding: new string('B', 64), SessionKey: "agent:main:main");
        var transport = new PausedTransport(proof);
        var owner = new Owner();
        var preparing = GatewayAiPreparation.PrepareAsync(transport, owner, default, scope);
        Assert.Equal(SetupLoadingStep.DiscoverChoices, loading.Current!.Step);
        Assert.False(preparing.IsCompleted);
        transport.Discovery.SetResult(JsonSerializer.SerializeToElement(new
        {
            candidates = Array.Empty<object>(), manualProviders = Array.Empty<object>(), workspace = "fixture",
            configuredModel = proof.ModelRef, setupComplete = true
        }));
        await using var prepared = await preparing;
        Assert.NotNull(prepared.Client.Detection);
        Assert.Equal(0, transport.ModelCalls);
    }

    private sealed class Owner : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PausedTransport(GatewayAiSetupCompletion proof) : IGatewayAiSetupTransport
    {
        public GatewayAiSetupRoute Route => new(proof.GatewayId, proof.AgentId, "fixture",
            proof.EndpointBinding, proof.IdentityBinding, proof.SessionKey);
        public long Generation => 1;
        public bool IsConnected => true;
        public IReadOnlyCollection<string> Methods => ["openclaw.setup.detect", "openclaw.setup.verify", "openclaw.setup.activate"];
        public IReadOnlyCollection<string> OperatorScopes => ["operator.admin"];
        public TaskCompletionSource<JsonElement> ConfigBefore { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<JsonElement> ConfigAfter { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<JsonElement> Model { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<JsonElement> Discovery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ModelEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ConfigAfterEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ModelCalls { get; private set; }
        private int _configCalls;
        public Task<JsonElement> RequestAsync(string method, object parameters, int timeoutMs, CancellationToken ct)
        {
            if (method == "openclaw.setup.detect") return Discovery.Task.WaitAsync(ct);
            if (method == "openclaw.setup.verify")
            {
                ModelCalls++;
                ModelEntered.SetResult();
                return Model.Task.WaitAsync(ct);
            }
            if (method == "config.get")
            {
                if (++_configCalls == 1) return ConfigBefore.Task.WaitAsync(ct);
                ConfigAfterEntered.SetResult();
                return ConfigAfter.Task.WaitAsync(ct);
            }
            throw new InvalidOperationException("Progress must not invoke additional work.");
        }
    }
}
