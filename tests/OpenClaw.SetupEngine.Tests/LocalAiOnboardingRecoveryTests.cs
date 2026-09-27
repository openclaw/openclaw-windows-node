using OpenClaw.Connection;
using OpenClaw.Connection.LocalAi;
using OpenClaw.Shared.Inference;
using OpenClaw.Shared.Inference.Catalog;
using OpenClaw.TestSupport;
using System.Runtime.InteropServices;

namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiOnboardingRecoveryTests
{
    [Fact]
    public void FirstInstallWithoutReceipt_UsesOnlyNonDestructiveRecoverySteps()
    {
        var steps = SetupStepFactory.BuildLocalAiRecoverySteps();
        Assert.Contains(steps, step => step is ReconcileLocalAiInstallationStep);
        Assert.Contains(steps, step => step is AcquireLocalAiRuntimeStep);
        Assert.Contains(steps, step => step is AcquireLocalAiModelStep);
        Assert.Contains(steps, step => step is ConfigureLocalAiGatewayStep);
        Assert.DoesNotContain(steps, step => step is CreateWslInstanceStep or CleanupStaleDistroStep
            or CleanupStaleGatewayStep or InstallCliStep or InstallGatewayServiceStep
            or PairOperatorStep or PairNodeStep or MintBootstrapTokenStep);
        Assert.Equal("validate-local-ai-recovery-gateway", steps[1].Id);
        Assert.True(steps.FindIndex(step => step.Id == "revalidate-local-ai-recovery-gateway") <
            steps.FindIndex(step => step.Id == "configure-local-ai-gateway"));
    }

    [Fact]
    public async Task Reconcile_FirstInstallWithoutReceipt_DoesNotNeedAnOldInstallation()
    {
        using var directory = new TempDirectory();
        var hardware = new HostHardwareInfo(Architecture.X64, 128L << 30, 100L << 30,
            [new(GpuVendor.Nvidia, "Test GPU", 96L << 30, 80L << 30, DriverVersion: "615.0",
                CudaMajorVersion: 13, StableId: "GPU-test")], false);
        var eligibility = LocalInferenceEligibility.Evaluate(hardware);
        var reconciler = new LocalAiInstallReconciler(new NoRuntimeInspection(), new NoModelInspection());
        var result = await reconciler.ReconcileAsync(directory.Path, eligibility.Plan!, "GPU-test",
            CancellationToken.None, allowIncompleteInstallation: true);
        Assert.False(result.Reused);
        Assert.Null(result.OriginalInstall);
        Assert.Null(result.ResolvedInstall);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    private sealed class NoRuntimeInspection : ILlamaRuntimeInspector
    {
        public Task<LlamaRuntimeInspection> InspectAsync(string installDirectory, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No runtime exists to inspect.");
    }

    private sealed class NoModelInspection : ILocalAiModelFileVerifier
    {
        public Task<bool> VerifyActiveAsync(LocalAiResolvedInstall install, PinnedArtifact artifact,
            CancellationToken cancellationToken) => throw new InvalidOperationException("No model exists to inspect.");
        public Task<bool> VerifyLegacyCompatibilityAsync(LocalAiResolvedInstall install, LocalAiPaths paths,
            PinnedArtifact artifact, CancellationToken cancellationToken) => throw new InvalidOperationException("No model exists to inspect.");
    }

    [Theory]
    [InlineData("owned", "owned", true, true)]
    [InlineData("owned", "other", true, false)]
    [InlineData("owned", "owned", false, false)]
    public void RecoveryAdmission_RequiresExactOwnedGateway(
        string expected, string actual, bool owned, bool allowed)
    {
        var existing = new ExistingConfigDetector.ExistingConfig(
            true, actual, "ws://127.0.0.1:18789", true, true, owned,
            "OpenClawGateway", true, 0, []);
        Assert.Equal(allowed, LocalAiRecoveryPolicy.CanRecoverExistingGateway(
            existing, expected, "OpenClawGateway", "ws://localhost:18789"));
    }
}
