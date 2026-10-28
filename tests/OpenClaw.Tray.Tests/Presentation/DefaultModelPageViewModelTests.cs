using System.Text.Json;
using OpenClaw.Chat;
using OpenClaw.Connection.LocalAi;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class DefaultModelPageViewModelTests
{
    private static readonly ChatModelChoice Sonnet = new("claude-sonnet-5", "Claude Sonnet 5", "anthropic");
    private static readonly ChatModelChoice Gpt = new("gpt-6", "GPT-6", "openai");

    [Fact]
    public async Task CloudChoiceWhileLocalAiOwnsPrimary_RecordsFallbackThenStopsWithoutDirectWrite()
    {
        var gateway = new FakeGateway { State = State("llamacpp/qwen", localRoute: true) };
        var runtime = new RecordingRuntime(Installed(LocalAiRuntimeState.Healthy))
        {
            StopResult = Installed(LocalAiRuntimeState.Stopped) with { Ownership = LocalAiOwnership.None, GatewayRouteRequiresResolution = false },
        };
        using var viewModel = Create(gateway, runtime);
        Assert.Equal(DefaultModelPageViewModel.LocalAiOptionId, viewModel.CurrentOptionId);

        viewModel.SelectedOptionId = Gpt.SelectionId;
        Assert.True(await viewModel.ApplyAsync(), viewModel.Error);

        Assert.Equal(["fallback:openai/gpt-6", "stop"], runtime.Calls);
        Assert.Empty(gateway.PrimaryWrites);
        Assert.True(viewModel.StatusApplied);
    }

    [Fact]
    public async Task CloudChoiceWhileLocalAiStopped_WritesPrimaryAndKeepsFallbackInSync()
    {
        var gateway = new FakeGateway { State = State(Sonnet.SelectionId, localRoute: true) };
        var runtime = new RecordingRuntime(Installed(LocalAiRuntimeState.Stopped));
        using var viewModel = Create(gateway, runtime);

        viewModel.SelectedOptionId = Gpt.SelectionId;
        Assert.True(await viewModel.ApplyAsync(), viewModel.Error);

        Assert.Equal(["openai/gpt-6"], gateway.PrimaryWrites);
        Assert.Equal(["primary+fallback:openai/gpt-6"], runtime.Calls);
    }

    [Fact]
    public async Task CloudChoiceWhileNativeRouteUnconfirmed_SendsNoPrimaryWrite()
    {
        // A native publication saved but lost its reply: the runtime failed and the receipt is pending.
        var gateway = new FakeGateway { State = State("llamacpp/qwen", localRoute: true) };
        var runtime = new RecordingRuntime(Installed(LocalAiRuntimeState.Failed)) { PendingReceipt = true };
        using var viewModel = Create(gateway, runtime);

        viewModel.SelectedOptionId = Gpt.SelectionId;
        Assert.False(await viewModel.ApplyAsync());

        Assert.Contains("unconfirmed Gateway change", viewModel.Error);
        Assert.Empty(gateway.PrimaryWrites);
        Assert.False(viewModel.StatusApplied);
    }

    [Fact]
    public async Task MultiSlashModelWithLocalAiInstalled_IsRejectedWithoutAnyWrite()
    {
        var nested = new ChatModelChoice("meta/llama-5", "Llama 5", "openrouter");
        var gateway = new FakeGateway { State = State(Sonnet.SelectionId, localRoute: true, extra: nested) };
        var runtime = new RecordingRuntime(Installed(LocalAiRuntimeState.Stopped));
        using var viewModel = Create(gateway, runtime);

        viewModel.SelectedOptionId = nested.SelectionId;
        Assert.False(await viewModel.ApplyAsync());

        Assert.NotNull(viewModel.Error);
        Assert.Empty(gateway.PrimaryWrites);
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public async Task LocalAiChoice_RecordsCurrentCloudPrimaryAsFallbackThenStarts()
    {
        var gateway = new FakeGateway { State = State(Sonnet.SelectionId, localRoute: true) };
        var runtime = new RecordingRuntime(Installed(LocalAiRuntimeState.Stopped))
        {
            StartResult = Installed(LocalAiRuntimeState.Healthy) with { GatewayRouteRequiresResolution = false },
        };
        using var viewModel = Create(gateway, runtime);

        viewModel.SelectedOptionId = DefaultModelPageViewModel.LocalAiOptionId;
        Assert.True(await viewModel.ApplyAsync(), viewModel.Error);

        Assert.Equal(["fallback:anthropic/claude-sonnet-5", "start"], runtime.Calls);
        Assert.Empty(gateway.PrimaryWrites);
    }

    [Fact]
    public void Options_ExcludeGatewayLlamaCppEntriesAndOfferLocalAiOnlyWithOwnedRoute()
    {
        var llama = new ChatModelChoice("qwen", "Qwen", "llamacpp");
        var runtime = new RecordingRuntime(Installed(LocalAiRuntimeState.Stopped));
        using var owned = Create(new FakeGateway { State = State(Sonnet.SelectionId, localRoute: true, extra: llama) }, runtime);
        using var foreign = Create(new FakeGateway { State = State(Sonnet.SelectionId, localRoute: false, extra: llama) }, runtime);

        Assert.DoesNotContain(owned.Options, option => option.Id.StartsWith("llamacpp/", StringComparison.Ordinal));
        Assert.Single(owned.Options, option => option.IsLocalAi);
        Assert.DoesNotContain(foreign.Options, option => option.IsLocalAi);
    }

    [Theory]
    [InlineData(LocalAiRuntimeState.Healthy)]
    [InlineData(LocalAiRuntimeState.Stopped)]
    public async Task CloudChoiceOnGatewayThatDoesNotOwnLocalAi_WritesOnlyThatGateway(LocalAiRuntimeState localState)
    {
        // Local AI is bound to another Gateway; this Gateway's primary happens to be a llamacpp ref.
        var gateway = new FakeGateway { State = State("llamacpp/other", localRoute: false) };
        var runtime = new RecordingRuntime(Installed(localState));
        using var viewModel = Create(gateway, runtime);

        viewModel.SelectedOptionId = Gpt.SelectionId;
        Assert.True(await viewModel.ApplyAsync(), viewModel.Error);

        Assert.Equal(["openai/gpt-6"], gateway.PrimaryWrites);
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public async Task ChoiceWhoseProviderNeedsSignIn_IsListedButCannotBeApplied()
    {
        var needsAuth = new ChatModelChoice("o5", "o5", "openai", RequiresAuth: true);
        var gateway = new FakeGateway { State = State(Sonnet.SelectionId, localRoute: false, extra: needsAuth) };
        using var viewModel = Create(gateway, new RecordingRuntime(Installed(LocalAiRuntimeState.Stopped)));

        Assert.Contains(viewModel.Options, option => option.Id == needsAuth.SelectionId && option.RequiresAuth);
        viewModel.SelectedOptionId = needsAuth.SelectionId;

        Assert.False(viewModel.CanApply);
        Assert.False(await viewModel.ApplyAsync());
        Assert.Empty(gateway.PrimaryWrites);
    }

    [Fact]
    public void GatewayRefreshWithUnchangedModels_KeepsOptionsInstanceSoTheSelectorIsNotRebuilt()
    {
        var gateway = new FakeGateway { State = State(Sonnet.SelectionId, localRoute: false) };
        using var viewModel = Create(gateway, new RecordingRuntime(Installed(LocalAiRuntimeState.Stopped)));

        var first = viewModel.Options;
        gateway.State = State(Sonnet.SelectionId, localRoute: false);
        gateway.RaiseChanged();
        Assert.Same(first, viewModel.Options);

        // Same options but a new current option: item enablement depends on it, so rebuild.
        gateway.State = State(Gpt.SelectionId, localRoute: false);
        gateway.RaiseChanged();
        Assert.NotSame(first, viewModel.Options);
        Assert.Equal(Gpt.SelectionId, viewModel.CurrentOptionId);

        var second = viewModel.Options;
        gateway.State = State(Gpt.SelectionId, localRoute: false, extra: new ChatModelChoice("o5", "O5", "openai"));
        gateway.RaiseChanged();
        Assert.NotSame(second, viewModel.Options);
        Assert.Contains(viewModel.Options, option => option.DisplayName == "O5");
    }

    [Fact]
    public void Envelope_ReadsPrimaryAndPatchExtendsExistingAllowlistOnly()
    {
        using JsonDocument envelope = JsonDocument.Parse(
            """{"parsed":{"agents":{"defaults":{"model":{"primary":"anthropic/claude"},"models":{"anthropic/claude":{}}}}},"hash":"h1"}""");
        Assert.True(GatewayConfigEnvelope.TryRead(envelope.RootElement, out JsonElement config, out string? hash));
        Assert.Equal("h1", hash);
        Assert.Equal("anthropic/claude", GatewayConfigEnvelope.ReadPrimaryModel(config));

        string patch = GatewayConfigEnvelope.BuildPrimaryPatch(config, "openai/gpt-6").ToJsonString();
        Assert.Equal("""{"agents":{"defaults":{"model":{"primary":"openai/gpt-6"},"models":{"openai/gpt-6":{}}}}}""", patch);

        using JsonDocument noAllowlist = JsonDocument.Parse("""{"agents":{"defaults":{"model":"anthropic/claude"}}}""");
        Assert.Equal("anthropic/claude", GatewayConfigEnvelope.ReadPrimaryModel(noAllowlist.RootElement));
        Assert.Equal("""{"agents":{"defaults":{"model":{"primary":"openai/gpt-6"}}}}""",
            GatewayConfigEnvelope.BuildPrimaryPatch(noAllowlist.RootElement, "openai/gpt-6").ToJsonString());
    }

    private static DefaultModelPageViewModel Create(FakeGateway gateway, RecordingRuntime runtime)
    {
        var viewModel = new DefaultModelPageViewModel(gateway,
            new PermissionsPageRuntimeSource(new FakePermissionsPageRuntimeHost()), runtime,
            new FakeAppCommands(), new RecordingUiDispatcher(), new FakeModelProviderGateway());
        viewModel.Activate(null);
        return viewModel;
    }

    private static DefaultModelGatewayState State(string? primary, bool localRoute, ChatModelChoice? extra = null) =>
        new(true, true, extra is null ? [Sonnet, Gpt] : [Sonnet, Gpt, extra], primary, localRoute);

    private static LocalAiRuntimeSnapshot Installed(LocalAiRuntimeState state)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new LocalAiRuntimeSnapshot(
            state,
            state == LocalAiRuntimeState.Healthy ? LocalAiOwnership.CompanionManaged : LocalAiOwnership.None,
            new Uri("http://127.0.0.1:18080/v1"), "test", "qwen",
            new LocalAiModelEvidence(LocalAiModelAvailabilityState.Verified, now, new string('0', 64), 1),
            null, null, null, now);
    }

    private sealed class FakeGateway : IDefaultModelGatewayService
    {
        public event EventHandler? Changed;
        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
        public DefaultModelGatewayState State { get; set; } = DefaultModelGatewayState.Disconnected;
        public List<string> PrimaryWrites { get; } = [];
        public DefaultModelGatewayState Current => State;
        public Task RefreshAsync() => Task.CompletedTask;
        public Task SetPrimaryModelAsync(string modelRef)
        {
            PrimaryWrites.Add(modelRef);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRuntime(LocalAiRuntimeSnapshot snapshot) : ILocalAiRuntime
    {
        public List<string> Calls { get; } = [];
        public LocalAiRuntimeSnapshot? StopResult { get; init; }
        public LocalAiRuntimeSnapshot? StartResult { get; init; }
        public bool PendingReceipt { get; init; }
        public LocalAiRuntimeSnapshot Snapshot { get; private set; } = snapshot;
        public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged { add { } remove { } }

        public Task SetGatewayFallbackModelAsync(string? model, CancellationToken cancellationToken = default)
        {
            Calls.Add($"fallback:{model}");
            return Task.CompletedTask;
        }

        public async Task SetGatewayPrimaryAndFallbackModelAsync(string model, Func<CancellationToken, Task> writePrimary,
            CancellationToken cancellationToken = default)
        {
            if (PendingReceipt)
                throw new InvalidOperationException("Local AI has an unconfirmed Gateway change.");
            await writePrimary(cancellationToken);
            Calls.Add($"primary+fallback:{model}");
        }

        public Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("start");
            Snapshot = StartResult ?? Snapshot;
            return Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("stop");
            Snapshot = StopResult ?? Snapshot;
            return Task.FromResult(Snapshot);
        }

        public Task<LocalAiRuntimeSnapshot> ResumeAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
