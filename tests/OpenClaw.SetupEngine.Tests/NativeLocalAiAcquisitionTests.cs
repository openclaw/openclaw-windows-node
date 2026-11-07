namespace OpenClaw.SetupEngine.Tests;

public sealed class NativeLocalAiAcquisitionTests
{
    [Fact]
    public void NativeAcquisitionUsesOnlySharedWindowsArtifactSteps()
    {
        var steps = SetupStepFactory.BuildNativeLocalAiAcquisitionSteps();
        Assert.Equal(
            [
                typeof(PreflightOsStep),
                typeof(PreflightLocalAiHardwareStep),
                typeof(ReconcileLocalAiInstallationStep),
                typeof(AcquireLocalAiRuntimeStep),
                typeof(AcquireLocalAiModelStep),
                typeof(PersistLocalAiManifestStep),
            ],
            steps.Select(step => step.GetType()));
    }

    [Fact]
    public void NativeAcquisitionNeverConfiguresOrStartsGatewayRuntimeOrInference()
    {
        var steps = SetupStepFactory.BuildNativeLocalAiAcquisitionSteps();
        Assert.DoesNotContain(steps, step => step is StartLocalAiRuntimeStep or
            VerifyLocalAiInferenceStep or ConfigureLocalAiGatewayStep or
            ConfigureLocalAiWslNetworkingStep or VerifyLocalAiWslStep or
            RestartGatewayStep or PreflightWslStep or EnsureWslPlatformStep or
            CreateWslInstanceStep or ConfigureWslInstanceStep or
            ValidateLocalAiRecoveryGatewayStep or PreserveLocalAiRecoveryGatewayStep);
    }

    [Fact]
    public void WslRecoveryRetainsItsExistingNetworkingAndGatewayVerification()
    {
        var steps = SetupStepFactory.BuildLocalAiRecoverySteps();
        Assert.Contains(steps, step => step is ConfigureLocalAiWslNetworkingStep);
        Assert.Contains(steps, step => step is VerifyLocalAiWslStep);
        Assert.Contains(steps, step => step is ConfigureLocalAiGatewayStep);
        Assert.IsType<RestartGatewayStep>(steps[^1]);
    }
}
