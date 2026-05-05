using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services.LocalGatewaySetup;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Locks down the per-engine-status mapping that <see cref="LocalSetupProgressPolicy"/>
/// pushes to <see cref="OnboardingState.NextButtonState"/>. See Phase 5 final
/// Next/Back-button policy in <c>.squad/decisions.md</c>.
/// </summary>
public class LocalSetupProgressPageNextButtonTests
{
    private static LocalGatewaySetupState CreateSnapshot() =>
        LocalGatewaySetupState.Create(new LocalGatewaySetupOptions());

    [Fact]
    public void NullSnapshot_MapsToHidden()
    {
        Assert.Equal(
            OnboardingNextButtonState.Hidden,
            LocalSetupProgressPolicy.MapStatusToNextButtonState(null, LocalGatewaySetupStatus.Pending));
    }

    [Fact]
    public void Pending_MapsToHidden()
    {
        Assert.Equal(
            OnboardingNextButtonState.Hidden,
            LocalSetupProgressPolicy.MapStatusToNextButtonState(CreateSnapshot(), LocalGatewaySetupStatus.Pending));
    }

    [Fact]
    public void Running_MapsToVisibleDisabled()
    {
        Assert.Equal(
            OnboardingNextButtonState.VisibleDisabled,
            LocalSetupProgressPolicy.MapStatusToNextButtonState(CreateSnapshot(), LocalGatewaySetupStatus.Running));
    }

    [Fact]
    public void Complete_MapsToVisibleEnabled()
    {
        Assert.Equal(
            OnboardingNextButtonState.VisibleEnabled,
            LocalSetupProgressPolicy.MapStatusToNextButtonState(CreateSnapshot(), LocalGatewaySetupStatus.Complete));
    }

    [Fact]
    public void FailedRetryable_MapsToVisibleDisabled()
    {
        Assert.Equal(
            OnboardingNextButtonState.VisibleDisabled,
            LocalSetupProgressPolicy.MapStatusToNextButtonState(CreateSnapshot(), LocalGatewaySetupStatus.FailedRetryable));
    }

    [Fact]
    public void FailedTerminal_MapsToVisibleDisabled()
    {
        Assert.Equal(
            OnboardingNextButtonState.VisibleDisabled,
            LocalSetupProgressPolicy.MapStatusToNextButtonState(CreateSnapshot(), LocalGatewaySetupStatus.FailedTerminal));
    }

    [Theory]
    [InlineData(LocalGatewaySetupStatus.RequiresAdmin)]
    [InlineData(LocalGatewaySetupStatus.RequiresRestart)]
    [InlineData(LocalGatewaySetupStatus.Blocked)]
    [InlineData(LocalGatewaySetupStatus.Cancelled)]
    public void OtherNonTerminalStatuses_MapToVisibleDisabled(LocalGatewaySetupStatus status)
    {
        Assert.Equal(
            OnboardingNextButtonState.VisibleDisabled,
            LocalSetupProgressPolicy.MapStatusToNextButtonState(CreateSnapshot(), status));
    }
}
