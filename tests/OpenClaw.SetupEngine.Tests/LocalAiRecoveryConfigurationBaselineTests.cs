namespace OpenClaw.SetupEngine.Tests;

public sealed class LocalAiRecoveryConfigurationBaselineTests
{
    [Fact]
    public void Restore_BackToWelcomeReturnsSubsequentNormalSetupToOriginalValues() =>
        AssertRestoreReturnsOriginalValues();

    [Fact]
    public void Restore_GatewayInstalledMilestoneReturnsSubsequentNormalSetupToOriginalValues() =>
        AssertRestoreReturnsOriginalValues();

    private static void AssertRestoreReturnsOriginalValues()
    {
        var config = new SetupConfig
        {
            DistroName = "Original Distro",
            GatewayPort = 18789,
            GatewayUrl = "ws://127.0.0.1:18789",
            RollbackOnFailure = false,
            SkipWizard = false,
            LocalAi = new LocalAiConfig
            {
                Enabled = false,
                SelectedModelId = "original-model",
                Port = 18803,
            },
        };
        LocalAiRecoveryConfigurationBaseline baseline =
            LocalAiRecoveryConfigurationBaseline.Capture(config);
        config.DistroName = "Recovery Distro";
        config.GatewayPort = 28889;
        config.GatewayUrl = null;
        config.RollbackOnFailure = true;
        config.SkipWizard = true;
        config.LocalAi.Enabled = true;
        config.LocalAi.SelectedModelId = "recovery-model";
        config.LocalAi.Port = 28803;

        baseline.Restore(config);

        Assert.Equal("Original Distro", config.DistroName);
        Assert.Equal(18789, config.GatewayPort);
        Assert.Equal("ws://127.0.0.1:18789", config.GatewayUrl);
        Assert.False(config.RollbackOnFailure);
        Assert.False(config.SkipWizard);
        Assert.False(config.LocalAi.Enabled);
        Assert.Equal("original-model", config.LocalAi.SelectedModelId);
        Assert.Equal(18803, config.LocalAi.Port);
    }
}
