namespace OpenClaw.SetupEngine.Tests;

public class SetupWizardRunnerDeviceIdTests
{
    [Fact]
    public void ShouldReplaceOperatorDeviceId_WizardIdentity_ReplacesPopulatedOperatorId()
    {
        Assert.True(SetupWizardRunner.ShouldReplaceOperatorDeviceId(
            usingWizardIdentity: true,
            currentOperatorDeviceId: "older-operator-id"));
    }

    [Fact]
    public void ShouldReplaceOperatorDeviceId_StoredDeviceToken_KeepsPopulatedOperatorId()
    {
        Assert.False(SetupWizardRunner.ShouldReplaceOperatorDeviceId(
            usingWizardIdentity: false,
            currentOperatorDeviceId: "stored-operator-id"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ShouldReplaceOperatorDeviceId_StoredDeviceToken_ReplacesMissingOperatorId(string? currentOperatorDeviceId)
    {
        Assert.True(SetupWizardRunner.ShouldReplaceOperatorDeviceId(
            usingWizardIdentity: false,
            currentOperatorDeviceId));
    }
}
