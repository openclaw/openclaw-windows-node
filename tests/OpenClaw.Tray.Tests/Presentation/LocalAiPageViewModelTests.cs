using OpenClaw.Connection.LocalAi;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class LocalAiPageViewModelTests
{
    [Theory]
    [InlineData(LocalAiModelAvailabilityState.Verified)]
    [InlineData(LocalAiModelAvailabilityState.Loaded)]
    public void InstalledModel_OffersChangeModelThroughExistingSetupRoute(
        LocalAiModelAvailabilityState modelState)
    {
        using var harness = new LocalAiHarness(modelState);

        Assert.True(harness.ViewModel.CanChangeModel);
        Assert.True(harness.ViewModel.HasInstalledModel);
        Assert.False(harness.ViewModel.CanRetrySetup);

        Assert.True(harness.ViewModel.ChangeModel());

        Assert.Equal(0, harness.Commands.ShowGatewayWizardCount);
        Assert.Equal(1, harness.Commands.ShowOnboardingCount);
    }

    [Theory]
    [InlineData(LocalAiModelAvailabilityState.Unknown)]
    [InlineData(LocalAiModelAvailabilityState.NotInstalled)]
    public void MissingModel_KeepsRetrySetupAndDoesNotOfferChangeModel(
        LocalAiModelAvailabilityState modelState)
    {
        using var harness = new LocalAiHarness(modelState);

        Assert.False(harness.ViewModel.CanChangeModel);
        Assert.False(harness.ViewModel.HasInstalledModel);
        Assert.True(harness.ViewModel.CanRetrySetup);

        Assert.False(harness.ViewModel.ChangeModel());
        Assert.True(harness.ViewModel.RetrySetup());

        Assert.Equal(0, harness.Commands.ShowGatewayWizardCount);
        Assert.Equal(1, harness.Commands.ShowOnboardingCount);
    }

    [Fact]
    public async Task InstalledModel_RemainsVisibleButCannotOpenSetupDuringRuntimeAction()
    {
        using var harness = new LocalAiHarness(
            LocalAiModelAvailabilityState.Verified,
            LocalAiRuntimeState.Stopped);
        harness.Runtime.BlockStart();

        Task<bool> startTask = harness.ViewModel.StartAsync();

        Assert.True(harness.ViewModel.HasInstalledModel);
        Assert.False(harness.ViewModel.CanChangeModel);
        Assert.False(harness.ViewModel.ChangeModel());
        Assert.Equal(0, harness.Commands.ShowOnboardingCount);

        harness.Runtime.CompleteStart();
        Assert.True(await startTask);
    }

    [Fact]
    public void LocalAiPage_ChangeModelActionIsLocalizedAccessibleAndWired()
    {
        string root = TestRepositoryPaths.GetRepositoryRoot();
        string pageDirectory = Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages");
        string xaml = File.ReadAllText(Path.Combine(pageDirectory, "LocalAiPage.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(pageDirectory, "LocalAiPage.xaml.cs"));

        Assert.Contains("x:Uid=\"LocalAiPage_ChangeModelButton\"", xaml);
        Assert.Contains("x:Uid=\"LocalAiPage_ChangeModelDescription\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"LocalAiChangeModel\"", xaml);
        Assert.Contains("Click=\"OnChangeModel\"", xaml);
        Assert.Contains("_viewModel?.ChangeModel()", codeBehind);
    }

    private sealed class LocalAiHarness : IDisposable
    {
        private readonly FakeLocalAiRuntime _runtime;
        private readonly PermissionsPageRuntimeSource _gatewaySource;
        private readonly RecordingUiDispatcher _dispatcher;

        public LocalAiHarness(
            LocalAiModelAvailabilityState modelState,
            LocalAiRuntimeState runtimeState = LocalAiRuntimeState.Healthy)
        {
            _runtime = new FakeLocalAiRuntime(CreateSnapshot(modelState, runtimeState));
            var gatewayHost = new FakePermissionsPageRuntimeHost();
            _gatewaySource = new PermissionsPageRuntimeSource(gatewayHost);
            Commands = new FakeAppCommands();
            _dispatcher = new RecordingUiDispatcher();
            ViewModel = new LocalAiPageViewModel(_runtime, _gatewaySource, Commands, _dispatcher);
        }

        public FakeAppCommands Commands { get; }
        public FakeLocalAiRuntime Runtime => _runtime;
        public LocalAiPageViewModel ViewModel { get; }

        public void Dispose()
        {
            ViewModel.Dispose();
            Commands.Dispose();
            _gatewaySource.Dispose();
            _dispatcher.Dispose();
            _runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private static LocalAiRuntimeSnapshot CreateSnapshot(
            LocalAiModelAvailabilityState modelState,
            LocalAiRuntimeState runtimeState)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            LocalAiModelEvidence evidence = modelState switch
            {
                LocalAiModelAvailabilityState.Verified => new(
                    modelState,
                    now,
                    new string('a', 64),
                    1024),
                LocalAiModelAvailabilityState.Loaded => new(
                    modelState,
                    now,
                    new string('a', 64),
                    1024,
                    "test-model"),
                LocalAiModelAvailabilityState.NotInstalled => LocalAiModelEvidence.NotInstalled(now),
                _ => LocalAiModelEvidence.Unknown(now),
            };

            return new LocalAiRuntimeSnapshot(
                modelState is LocalAiModelAvailabilityState.Verified or LocalAiModelAvailabilityState.Loaded
                    ? runtimeState
                    : LocalAiRuntimeState.NotInstalled,
                modelState is LocalAiModelAvailabilityState.Verified or LocalAiModelAvailabilityState.Loaded
                    ? LocalAiOwnership.CompanionManaged
                    : LocalAiOwnership.None,
                new Uri("http://127.0.0.1:11983"),
                "test",
                "test-model",
                evidence,
                null,
                null,
                null,
                now);
        }
    }

    internal sealed class FakeLocalAiRuntime(LocalAiRuntimeSnapshot snapshot) : ILocalAiRuntime
    {
        private TaskCompletionSource<LocalAiRuntimeSnapshot>? _startCompletion;

        public LocalAiRuntimeSnapshot Snapshot { get; } = snapshot;

        public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public void BlockStart() =>
            _startCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CompleteStart() => _startCompletion?.TrySetResult(Snapshot);

        public Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default) =>
            _startCompletion?.Task ?? Task.FromResult(Snapshot);

        public Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
