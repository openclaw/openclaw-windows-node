using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.TestSupport;
using OpenClawTray.Pages;
using OpenClawTray.Presentation;
using OpenClawTray.Presentation.Adapters;
using OpenClawTray.Services;
using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class LocalAiHardwareProfileTests(UIThreadFixture ui, ITestOutputHelper output)
{
    [Theory]
    [InlineData(8, true)]
    [InlineData(6, false)]
    public async Task LocalAiPage_RendersHardwareFallbackOrUnavailable(int vramGiB, bool fits)
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var originalSize = ui.TestWindow.AppWindow.Size;
            var originalPosition = ui.TestWindow.AppWindow.Position;
            ui.TestWindow.AppWindow.Resize(new(1050, 1000));
            using var viewModel = new LocalAiPageViewModel(new InertRuntime(), new InertGateway(),
                new InertCommands(), new WinUIDispatcher(ui.Dispatcher), new HardwareProbe(vramGiB));
            var page = new LocalAiPage { DataContext = viewModel };
            ui.Container.Children.Add(page);
            try
            {
                viewModel.Activate();
                await TestSupport.WaitForRenderedConditionAsync(
                    () => page.IsLoaded && viewModel.IsAvailabilityKnown, "Local AI hardware eligibility");
                var fallback = Assert.IsType<TextBlock>(page.FindName("LocalAiProfileSelectionText"));
                var unavailable = Assert.IsType<InfoBar>(page.FindName("LocalAiUnavailableInfoBar"));
                Assert.Equal(fits ? Visibility.Visible : Visibility.Collapsed, fallback.Visibility);
                Assert.Equal(fits ? Visibility.Collapsed : Visibility.Visible, unavailable.Visibility);
                Assert.Equal(fits, viewModel.CanRetrySetup);
                if (fits)
                {
                    Assert.Contains("Setup can use", fallback.Text);
                    Assert.Contains("Bonsai 2", fallback.Text);
                    Assert.Contains("8,192", fallback.Text);
                }

                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_DIR")))
                {
                    ui.TestWindow.AppWindow.Move(new(40, 40));
                    OnboardingNativeProof.ActivateOwned(ui.TestWindow);
                    using var capture = await OnboardingNativeProof.CaptureAsync(ui.TestWindow,
                        $"local-ai-{vramGiB}gib", output,
                        [fits ? fallback.Text : unavailable.Title],
                        requiredContent: fits ? fallback : unavailable);
                }
            }
            finally
            {
                ui.Container.Children.Clear();
                ui.TestWindow.AppWindow.Resize(originalSize);
                ui.TestWindow.AppWindow.Move(originalPosition);
            }
        });
    }

    private sealed class HardwareProbe(int vramGiB) : IHostHardwareProbe
    {
        public HostHardwareInfo Probe() => new(Architecture.X64, 32L << 30, 24L << 30,
            [new(GpuVendor.Nvidia, "Synthetic RTX 4060", (long)vramGiB << 30, (long)vramGiB << 30,
                CudaMajorVersion: 13, StableId: "synthetic-profile-proof")], false,
            new(20L << 30, 20L << 30, true));
    }

    private sealed class InertRuntime : ILocalAiRuntime
    {
        public LocalAiRuntimeSnapshot Snapshot { get; } = LocalAiRuntimeSnapshot.Initial(
            new Uri("http://127.0.0.1:18080"), DateTimeOffset.UtcNow);
        public event EventHandler<LocalAiRuntimeSnapshotChangedEventArgs>? StateChanged { add { } remove { } }
        public Task<LocalAiRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task<LocalAiRuntimeSnapshot> EnsureStartedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalAiRuntimeSnapshot> ResumeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalAiRuntimeSnapshot> StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LocalAiRuntimeSnapshot> RestartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InertGateway : IPermissionsPageRuntimeSource
    {
        public event EventHandler<PermissionsRuntimeSourceChangedEventArgs>? Changed { add { } remove { } }
        public PermissionsRuntimeSourceSnapshot Current { get; } = new(GatewayConnectionSnapshot.Idle,
            null, "", false, 0, [], [], PermissionsGatewayAllowlistState.NoConfig, PermissionsVoiceSetupRequirement.None);
    }

    private sealed class InertCommands : IAppCommands
    {
        public void OpenDashboard(string? path = null) => throw new NotSupportedException();
        public void Navigate(string pageTag) => throw new NotSupportedException();
        public void Reconnect() => throw new NotSupportedException();
        public void Disconnect() => throw new NotSupportedException();
        public void ShowVoiceOverlay() => throw new NotSupportedException();
        public void ShowChat() => throw new NotSupportedException();
        public void CheckForUpdates() => throw new NotSupportedException();
        public void ShowOnboarding() => throw new NotSupportedException();
        public void ShowLocalAiSetup() => throw new NotSupportedException();
        public void ShowGatewayWizard() => throw new NotSupportedException();
        public void ShowConnectionStatus() => throw new NotSupportedException();
        public void NotifySettingsSaved() => throw new NotSupportedException();
        public Task<bool> ApplyAutoStart(SettingsWriteOrigin origin, bool autoStart) => throw new NotSupportedException();
        public Task<bool> ResendOpenTelemetryProbeAsync() => throw new NotSupportedException();
    }
}
