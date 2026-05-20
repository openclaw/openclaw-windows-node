using OpenClaw.Shared;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public class GatewayNavVisibilityDebouncePolicyTests
{
    [Fact]
    public void Connected_ShowsImmediately()
    {
        Assert.Equal(
            GatewayNavVisibilityDecision.ShowNow,
            GatewayNavVisibilityDebouncePolicy.GetDecision(ConnectionStatus.Connected, debounceDisconnected: true));
    }

    [Theory]
    [InlineData(ConnectionStatus.Disconnected)]
    [InlineData(ConnectionStatus.Connecting)]
    [InlineData(ConnectionStatus.Error)]
    public void NonConnected_WithDebounce_SchedulesHide(ConnectionStatus status)
    {
        Assert.Equal(
            GatewayNavVisibilityDecision.ScheduleHide,
            GatewayNavVisibilityDebouncePolicy.GetDecision(status, debounceDisconnected: true));
    }

    [Theory]
    [InlineData(ConnectionStatus.Disconnected)]
    [InlineData(ConnectionStatus.Connecting)]
    [InlineData(ConnectionStatus.Error)]
    public void NonConnected_WithoutDebounce_HidesImmediately(ConnectionStatus status)
    {
        Assert.Equal(
            GatewayNavVisibilityDecision.HideNow,
            GatewayNavVisibilityDebouncePolicy.GetDecision(status, debounceDisconnected: false));
    }

    [Fact]
    public void ReconnectedBeforeDelay_DoesNotHide()
    {
        Assert.False(GatewayNavVisibilityDebouncePolicy.ShouldHideAfterDelay(ConnectionStatus.Connected));
    }

    [Theory]
    [InlineData(ConnectionStatus.Disconnected)]
    [InlineData(ConnectionStatus.Connecting)]
    [InlineData(ConnectionStatus.Error)]
    public void StillDisconnectedAfterDelay_Hides(ConnectionStatus status)
    {
        Assert.True(GatewayNavVisibilityDebouncePolicy.ShouldHideAfterDelay(status));
    }
}
