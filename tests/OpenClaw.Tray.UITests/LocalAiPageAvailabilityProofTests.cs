using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClawTray.Pages;
using OpenClawTray.Presentation;
using OpenClawTray.Services;
using static OpenClaw.Tray.UITests.TestSupport;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class LocalAiPageAvailabilityProofTests
{
    private readonly UIThreadFixture _ui;
    private readonly ITestOutputHelper _output;

    public LocalAiPageAvailabilityProofTests(UIThreadFixture ui, ITestOutputHelper output)
    {
        _ui = ui;
        _output = output;
    }

    [Fact]
    public async Task FailedHardwareProbe_KeepsHealthyManagedRuntimeControlsEnabled()
    {
        await _ui.ResetContainerAsync();

        var availabilityKnown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LocalAiPageViewModel? viewModel = null;
        string? renderedProof = null;

        try
        {
            await _ui.RunOnUIAsync(() =>
            {
                var runtime = new FakeLocalAiRuntime(CreateHealthyLoadedSnapshot());
                var gatewayHost = new FakePermissionsPageRuntimeHost
                {
                    ConnectionSnapshot = GatewayConnectionSnapshot.Idle with
                    {
                        OperatorState = RoleConnectionState.Connected,
                    },
                };
                var gatewaySource = new PermissionsPageRuntimeSource(gatewayHost);
                viewModel = new LocalAiPageViewModel(
                    runtime,
                    gatewaySource,
                    new FakeAppCommands(),
                    new DispatcherAdapter(_ui.Dispatcher),
                    new UnknownHardwareProbe());
                viewModel.PropertyChanged += (_, _) =>
                {
                    if (viewModel.IsAvailabilityKnown)
                        availabilityKnown.TrySetResult();
                };

                var page = new LocalAiPage { DataContext = viewModel };
                _ui.Container.Children.Add(page);
                _ui.Container.UpdateLayout();
                viewModel.Activate(null);
            });

            await availabilityKnown.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await _ui.YieldToRenderAsync();

            await _ui.RunOnUIAsync(() =>
            {
                var page = Assert.IsType<LocalAiPage>(Assert.Single(_ui.Container.Children));
                var root = Assert.IsAssignableFrom<DependencyObject>(page.Content);

                Assert.False(viewModel!.IsLocalAiAvailable);
                Assert.False(viewModel.IsSetupAvailable);

                var warning = FindByAutomationId<InfoBar>(root, "LocalAiUnavailableInfoBar");
                Assert.Equal(Visibility.Visible, warning.Visibility);
                Assert.True(warning.IsOpen);

                Assert.False(FindByAutomationId<Button>(root, "LocalAiStart").IsEnabled);
                Assert.True(FindByAutomationId<Button>(root, "LocalAiStop").IsEnabled);
                Assert.True(FindByAutomationId<Button>(root, "LocalAiRestart").IsEnabled);
                Assert.True(FindByAutomationId<Button>(root, "LocalAiOpenLogs").IsEnabled);
                Assert.True(FindByAutomationId<Button>(root, "LocalAiOpenChat").IsEnabled);

                var retrySetup = FindByAutomationId<Button>(root, "LocalAiRetrySetup");
                Assert.False(retrySetup.IsEnabled);
                Assert.Equal(Visibility.Collapsed, retrySetup.Visibility);

                Assert.True(FindByAutomationId<ContentControl>(root, "LocalAiEngineCard").IsEnabled);
                Assert.True(FindByAutomationId<ContentControl>(root, "LocalAiModelCard").IsEnabled);
                Assert.True(FindByAutomationId<ContentControl>(root, "LocalAiGatewayCard").IsEnabled);

                renderedProof = JsonSerializer.Serialize(new
                {
                    state = "hardware-unavailable-installed-runtime-healthy",
                    warning = new { automationId = "LocalAiUnavailableInfoBar", visible = true, open = true },
                    controls = new
                    {
                        startEnabled = false,
                        stopEnabled = true,
                        restartEnabled = true,
                        openLogsEnabled = true,
                        openChatEnabled = true,
                        retrySetupEnabled = false,
                        retrySetupVisible = false,
                    },
                    cards = new { engineEnabled = true, modelEnabled = true, gatewayEnabled = true },
                });
            });

            _output.WriteLine($"rendered-ui-proof={renderedProof}");
        }
        finally
        {
            await _ui.RunOnUIAsync(() =>
            {
                viewModel?.Dispose();
                _ui.Container.Children.Clear();
                _ui.Container.UpdateLayout();
            });
        }
    }

    private static T FindByAutomationId<T>(DependencyObject root, string automationId)
        where T : DependencyObject =>
        Assert.Single(
            FindLogical<T>(root),
            element => string.Equals(
                AutomationProperties.GetAutomationId(element),
                automationId,
                StringComparison.Ordinal));

    private static LocalAiRuntimeSnapshot CreateHealthyLoadedSnapshot()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string modelId = LocalModelCatalog.Models[0].Id;
        return new LocalAiRuntimeSnapshot(
            LocalAiRuntimeState.Healthy,
            LocalAiOwnership.CompanionManaged,
            new Uri("http://127.0.0.1:18080"),
            "test",
            modelId,
            new LocalAiModelEvidence(
                LocalAiModelAvailabilityState.Loaded,
                now,
                new string('0', 64),
                sizeBytes: 1,
                serverModelId: modelId),
            ProcessId: 1234,
            ProcessStartedAtUtc: now,
            Detail: null,
            UpdatedAtUtc: now);
    }

    private sealed class DispatcherAdapter(DispatcherQueue dispatcher) : IUiDispatcher
    {
        public bool HasThreadAccess => dispatcher.HasThreadAccess;
        public bool TryEnqueue(Action action) => dispatcher.TryEnqueue(() => action());
    }

    private sealed class UnknownHardwareProbe : IHostHardwareProbe
    {
        public HostHardwareInfo Probe() => HostHardwareInfo.Unknown;
    }

    private sealed class FakeLocalAiRuntime(LocalAiRuntimeSnapshot snapshot) : ILocalAiRuntime
    {
        public LocalAiRuntimeSnapshot Snapshot { get; } = snapshot;
        public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);
        public Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);
        public Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);
        public Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePermissionsPageRuntimeHost : IPermissionsPageRuntimeHost
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public GatewayConnectionSnapshot ConnectionSnapshot { get; init; } = GatewayConnectionSnapshot.Idle;
        public GatewayNodeInfo[] Nodes => [];
        public string? LocalNodeDeviceId => null;
        public JsonElement? GatewayConfig => null;
        public string? McpStartupError => null;
        public string McpEndpoint => "http://127.0.0.1:8765/mcp";
        public bool IsMcpTokenReady => false;
        public int McpServedCapabilityCount => 0;
        public PermissionsVoiceSetupRequirement VoiceSetupRequirement => PermissionsVoiceSetupRequirement.None;
    }

    private sealed class FakeAppCommands : IAppCommands
    {
        public void OpenDashboard(string? path = null) { }
        public void Navigate(string pageTag) { }
        public void Reconnect() { }
        public void Disconnect() { }
        public void ShowVoiceOverlay() { }
        public void ShowChat() { }
        public void CheckForUpdates() { }
        public void ShowOnboarding() { }
        public void OpenLocalAiLogs() { }
        public void ShowGatewayWizard() { }
        public void ShowConnectionStatus() { }
        public void NotifySettingsSaved() { }
        public Task<bool> ApplyAutoStart(SettingsWriteOrigin origin, bool autoStart) => Task.FromResult(true);
        public Task<bool> ResendOpenTelemetryProbeAsync() => Task.FromResult(true);
    }
}
