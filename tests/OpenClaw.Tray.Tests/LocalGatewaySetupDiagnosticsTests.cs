using OpenClawTray.Services.LocalGatewaySetup;

namespace OpenClaw.Tray.Tests;

public class LocalGatewaySetupDiagnosticsTests
{
    [Fact]
    public void Diagnostics_WritesFailureJsonlAndHumanSummary()
    {
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var service = new LocalGatewaySetupDiagnosticsService(temp.Path);
        var options = new LocalGatewaySetupOptions();
        var state = LocalGatewaySetupState.Create(options);
        state.RunId = "run123";
        state.InstallId = "install456";

        service.RunStarted(state, options);
        state.StartPhase(LocalGatewaySetupPhase.Preflight, "Checking your PC");
        service.PhaseStarted(state, LocalGatewaySetupPhase.Preflight, "Checking your PC");
        state.Block(
            "wsl_unavailable",
            "WSL is unavailable. bootstrapToken: secret-token",
            retryable: true,
            detail: "gateway-token=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        service.PhaseCompleted(state, LocalGatewaySetupPhase.Preflight, "Checking your PC", TimeSpan.FromMilliseconds(42));
        service.RunCompleted(state, TimeSpan.FromMilliseconds(50));

        var jsonl = File.ReadAllText(service.LatestTracePath!);
        var summary = File.ReadAllText(service.LatestSummaryPath!);

        Assert.Contains("\"schema_version\":1", jsonl);
        Assert.Contains("\"event\":\"phase_failed\"", jsonl);
        Assert.Contains("\"failure_code\":\"wsl_unavailable\"", jsonl);
        Assert.DoesNotContain("secret-token", jsonl);
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", jsonl);
        Assert.Contains("Outcome: FAILED", summary);
        Assert.Contains("Failed phase: Preflight", summary);
        Assert.Contains("Failure code: wsl_unavailable", summary);
        Assert.Contains("easy-setup-latest.jsonl", summary);
    }

    [Fact]
    public void Diagnostics_RedactsCommandArgumentsAndOutputOnDisk()
    {
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var service = new LocalGatewaySetupDiagnosticsService(temp.Path);
        var options = new LocalGatewaySetupOptions();
        var state = LocalGatewaySetupState.Create(options);
        state.RunId = "run-redact";
        state.InstallId = "install-redact";
        service.RunStarted(state, options);

        var secretHex = "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd";
        var commandId = service.CommandStarted(
            "openclaw",
            ["gateway", "status", "--token", "super-secret-token", "--password=super-secret-password"],
            TimeSpan.FromSeconds(5));
        service.CommandCompleted(
            commandId,
            "openclaw",
            ["gateway", "status", "--token", "super-secret-token", "--password=super-secret-password"],
            TimeSpan.FromMilliseconds(10),
            new WslCommandResult(
                1,
                $"bootstrapToken: secret-token\nraw token {secretHex}",
                "-----BEGIN PRIVATE KEY-----\nsecret\n-----END PRIVATE KEY-----"),
            timedOut: false);

        var jsonl = File.ReadAllText(service.LatestTracePath!);

        Assert.DoesNotContain("super-secret-token", jsonl);
        Assert.DoesNotContain("super-secret-password", jsonl);
        Assert.DoesNotContain("secret-token", jsonl);
        Assert.DoesNotContain(secretHex, jsonl);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", jsonl);
        Assert.Contains("<redacted>", jsonl);
    }

    [Fact]
    public async Task Engine_WritesRunAndPhaseDiagnostics_ForSuccessfulSetup()
    {
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var statePath = Path.Combine(temp.Path, "setup-state.json");
        var wsl = new LocalGatewaySetupTests.FakeWslCommandRunner();
        var provisioning = new FakeProvisioner();
        var diagnostics = new LocalGatewaySetupDiagnosticsService(temp.Path);
        var engine = new LocalGatewaySetupEngine(
            new LocalGatewaySetupOptions { InstanceInstallLocation = Path.Combine(temp.Path, "OpenClawGateway") },
            new LocalGatewaySetupStateStore(statePath),
            new LocalGatewayPreflightProbe(wsl, new LocalGatewaySetupTests.FixedPortProbe(available: true)),
            wsl,
            new LocalGatewaySetupTests.SuccessfulHealthProbe(),
            provisioning,
            provisioning,
            provisioning,
            wslInstanceInstaller: new WslStoreInstanceInstaller(wsl, createDirectory: _ => { }),
            wslInstanceConfigurator: new LocalGatewaySetupTests.FakeWslInstanceConfigurator(),
            openClawLinuxInstaller: new LocalGatewaySetupTests.FakeOpenClawLinuxInstaller(),
            gatewayConfigurationPreparer: new LocalGatewaySetupTests.FakeGatewayConfigurationPreparer(),
            gatewayServiceManager: new LocalGatewaySetupTests.FakeGatewayServiceManager(),
            diagnosticsSink: diagnostics);

        var state = await engine.RunLocalOnlyAsync();

        Assert.Equal(LocalGatewaySetupStatus.Complete, state.Status);
        var jsonl = File.ReadAllText(diagnostics.LatestTracePath!);
        Assert.Contains("\"event\":\"run_started\"", jsonl);
        Assert.Contains("\"event\":\"phase_started\"", jsonl);
        Assert.Contains("\"event\":\"phase_succeeded\"", jsonl);
        Assert.Contains("\"event\":\"run_completed\"", jsonl);
        Assert.Contains("\"phase\":\"CreateWslInstance\"", jsonl);
        Assert.Contains("Outcome: COMPLETE", File.ReadAllText(diagnostics.LatestSummaryPath!));
    }

    [Fact]
    public async Task LifecycleManager_WritesGatewayLifecycleFailureDiagnostics()
    {
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var diagnostics = new LocalGatewaySetupDiagnosticsService(temp.Path);
        var manager = new LocalGatewayLifecycleManager(
            new LocalGatewaySetupOptions(),
            new LocalGatewaySetupTests.FakeWslCommandRunner(),
            new LocalGatewaySetupTests.SuccessfulHealthProbe(),
            diagnosticsSink: diagnostics);

        var result = await manager.RemoveAsync(new LocalGatewayRemoveRequest(ConfirmRemove: false, ClearLocalCredentials: false));

        Assert.False(result.Success);
        var jsonl = File.ReadAllText(diagnostics.LatestTracePath!);
        var summary = File.ReadAllText(diagnostics.LatestSummaryPath!);
        Assert.Contains("\"event\":\"lifecycle_started\"", jsonl);
        Assert.Contains("\"event\":\"lifecycle_step_failed\"", jsonl);
        Assert.Contains("\"event\":\"lifecycle_failed\"", jsonl);
        Assert.Contains("\"failure_code\":\"confirmation_required\"", jsonl);
        Assert.Contains("Gateway lifecycle operation: remove", summary);
        Assert.Contains("Failure code: confirmation_required", summary);
    }

    private sealed class FakeProvisioner :
        IBootstrapTokenProvisioner, IOperatorPairingService, IWindowsTrayNodeProvisioner
    {
        public Task<ProvisioningResult> MintAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProvisioningResult(true));

        Task<ProvisioningResult> IOperatorPairingService.PairAsync(LocalGatewaySetupState state, CancellationToken cancellationToken) =>
            Task.FromResult(new ProvisioningResult(true));

        Task<ProvisioningResult> IWindowsTrayNodeProvisioner.CheckReadinessAsync(LocalGatewaySetupState state, CancellationToken cancellationToken) =>
            Task.FromResult(new ProvisioningResult(true));

        Task<ProvisioningResult> IWindowsTrayNodeProvisioner.PairAsync(LocalGatewaySetupState state, CancellationToken cancellationToken) =>
            Task.FromResult(new ProvisioningResult(true));
    }
}
