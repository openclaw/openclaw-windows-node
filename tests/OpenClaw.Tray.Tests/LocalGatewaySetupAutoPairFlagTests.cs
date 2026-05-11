using OpenClaw.Shared;
using OpenClawTray.Services.LocalGatewaySetup;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Bug #2 (manual test 2026-05-05): the loopback gateway emits a transient
/// PairingStatus.Pending event during Phase 14 (PairWindowsTrayNode) before the
/// pending-approver auto-approves. App.OnPairingStatusChanged consults
/// LocalGatewaySetupEngine.IsAutoPairingWindowsNode to suppress the
/// "copy pairing command" toast for that blip only.
///
/// Closure conditions verified here:
///  - Bracket wraps the actual Phase 14 PairAsync call only (not the whole engine run).
///  - Suppression decision is Pending+autopair only (Paired/Rejected always pass through).
///  - Suppression is OFF before, ON during, OFF after the Phase 14 call.
///  - Manual ConnectionPage path is unaffected (it bypasses the event handler entirely
///    by calling App.ShowPairingPendingNotification directly — see ConnectionPage.cs:366).
/// </summary>
public class LocalGatewaySetupAutoPairFlagTests
{
    [Fact]
    public void IsAutoPairingWindowsNode_DefaultsToFalse()
    {
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var wsl = new LocalGatewaySetupTests.FakeWslCommandRunner();
        var provisioning = new FakeProvisioner();
        var engine = BuildEngine(temp, wsl, provisioning);

        Assert.False(engine.IsAutoPairingWindowsNode);
    }

    [Fact]
    public async Task IsAutoPairingWindowsNode_TrueOnlyDuringPhase14PairAsync()
    {
        // Capture the engine's flag at three observation points: during PairOperator
        // (Phase 12, must be false), during PairWindowsTrayNode (Phase 14, must be true),
        // and after RunLocalOnlyAsync returns (must be false). Proves the bracket scope
        // is exactly Phase 14 — not the whole RunLocalOnlyAsync run.
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var wsl = new LocalGatewaySetupTests.FakeWslCommandRunner();
        LocalGatewaySetupEngine? engineRef = null;
        var provisioning = new FakeProvisioner();
        provisioning.OperatorPairCallback = () =>
        {
            provisioning.FlagDuringOperatorPair = engineRef?.IsAutoPairingWindowsNode == true;
        };
        provisioning.WindowsNodePairCallback = () =>
        {
            provisioning.FlagDuringWindowsNodePair = engineRef?.IsAutoPairingWindowsNode == true;
        };

        var engine = BuildEngine(temp, wsl, provisioning);
        engineRef = engine;

        Assert.False(engine.IsAutoPairingWindowsNode); // before run

        var state = await engine.RunLocalOnlyAsync();

        Assert.Equal(LocalGatewaySetupStatus.Complete, state.Status);
        Assert.False(provisioning.FlagDuringOperatorPair); // Phase 12 not bracketed
        Assert.True(provisioning.FlagDuringWindowsNodePair); // Phase 14 IS bracketed
        Assert.False(engine.IsAutoPairingWindowsNode); // reset after run
    }

    [Fact]
    public async Task IsAutoPairingWindowsNode_ResetEvenIfPhase14Throws()
    {
        // Race-safety: even if PairAsync throws an exception, the finally must reset
        // the flag so a later unrelated Pending event is not silently swallowed.
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var wsl = new LocalGatewaySetupTests.FakeWslCommandRunner();
        var provisioning = new FakeProvisioner { ThrowFromWindowsNodePair = true };
        var engine = BuildEngine(temp, wsl, provisioning);

        await engine.RunLocalOnlyAsync();

        Assert.False(engine.IsAutoPairingWindowsNode);
    }

    [Theory]
    [InlineData(PairingStatus.Pending, true, true)]   // autopair + Pending → suppress
    [InlineData(PairingStatus.Pending, false, false)] // no autopair + Pending → show (manual ConnectionPage scenario falls here if it ever did go through this path)
    [InlineData(PairingStatus.Paired, true, false)]   // autopair + Paired → never suppress confirmation
    [InlineData(PairingStatus.Rejected, true, false)] // autopair + Rejected → never suppress rejection
    [InlineData(PairingStatus.Paired, false, false)]
    [InlineData(PairingStatus.Rejected, false, false)]
    public async Task ShouldSuppressPairingPendingNotification_OnlyForPendingDuringAutoPair(
        PairingStatus status, bool flagOn, bool expectedSuppress)
    {
        using var temp = new LocalGatewaySetupTests.TempDirectory();
        var wsl = new LocalGatewaySetupTests.FakeWslCommandRunner();
        var provisioning = new FakeProvisioner();
        var engine = BuildEngine(temp, wsl, provisioning);

        if (flagOn)
        {
            // Drive the engine into the bracketed Phase 14 window and capture the
            // decision while the flag is observably true. The provisioner's callback
            // runs synchronously inside the await, so the flag is guaranteed live.
            bool? observedSuppress = null;
            provisioning.WindowsNodePairCallback = () =>
            {
                observedSuppress = LocalGatewaySetupEngine
                    .ShouldSuppressPairingPendingNotification(engine, status);
            };
            await engine.RunLocalOnlyAsync();
            Assert.NotNull(observedSuppress);
            Assert.Equal(expectedSuppress, observedSuppress!.Value);
        }
        else
        {
            // Flag is off (engine never ran or has finished); decision must be "show".
            Assert.Equal(
                expectedSuppress,
                LocalGatewaySetupEngine.ShouldSuppressPairingPendingNotification(engine, status));
        }
    }

    [Fact]
    public void ShouldSuppressPairingPendingNotification_NullEngine_NeverSuppresses()
    {
        // Manual ConnectionPage path: App may have no cached engine (user went straight
        // to Advanced flow). Decision helper must tolerate null and never suppress.
        Assert.False(LocalGatewaySetupEngine
            .ShouldSuppressPairingPendingNotification(null, PairingStatus.Pending));
        Assert.False(LocalGatewaySetupEngine
            .ShouldSuppressPairingPendingNotification(null, PairingStatus.Paired));
    }

    // --- helpers --------------------------------------------------------

    private static LocalGatewaySetupEngine BuildEngine(
        LocalGatewaySetupTests.TempDirectory temp,
        LocalGatewaySetupTests.FakeWslCommandRunner wsl,
        FakeProvisioner provisioning)
    {
        var statePath = System.IO.Path.Combine(temp.Path, "setup-state.json");
        var installLocation = System.IO.Path.Combine(temp.Path, "OpenClawGateway");
        return new LocalGatewaySetupEngine(
            new LocalGatewaySetupOptions { InstanceInstallLocation = installLocation },
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
            gatewayServiceManager: new LocalGatewaySetupTests.FakeGatewayServiceManager());
    }

    private sealed class FakeProvisioner :
        IBootstrapTokenProvisioner, IOperatorPairingService, IWindowsTrayNodeProvisioner
    {
        public bool ThrowFromWindowsNodePair { get; init; }
        public Action? OperatorPairCallback { get; set; }
        public Action? WindowsNodePairCallback { get; set; }
        public bool FlagDuringOperatorPair { get; set; }
        public bool FlagDuringWindowsNodePair { get; set; }

        public Task<ProvisioningResult> MintAsync(LocalGatewaySetupState state, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProvisioningResult(true));

        Task<ProvisioningResult> IOperatorPairingService.PairAsync(LocalGatewaySetupState state, CancellationToken cancellationToken)
        {
            OperatorPairCallback?.Invoke();
            return Task.FromResult(new ProvisioningResult(true));
        }

        Task<ProvisioningResult> IWindowsTrayNodeProvisioner.CheckReadinessAsync(LocalGatewaySetupState state, CancellationToken cancellationToken)
            => Task.FromResult(new ProvisioningResult(true));

        Task<ProvisioningResult> IWindowsTrayNodeProvisioner.PairAsync(LocalGatewaySetupState state, CancellationToken cancellationToken)
        {
            WindowsNodePairCallback?.Invoke();
            if (ThrowFromWindowsNodePair)
                throw new InvalidOperationException("simulated phase 14 failure");
            return Task.FromResult(new ProvisioningResult(true));
        }
    }
}
