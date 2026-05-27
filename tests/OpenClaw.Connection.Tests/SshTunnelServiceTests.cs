using OpenClaw.Shared;

namespace OpenClaw.Connection.Tests;

public sealed class SshTunnelServiceTests
{
    [Fact]
    public void ResetNotConfigured_ClearsStoppedTunnelErrorState()
    {
        using var service = new SshTunnelService(NullLogger.Instance);
        
        // Arrange: Set up error state
        service.MarkRestarting(exitCode: 255);
        Assert.NotEqual(TunnelStatus.NotConfigured, service.Status); // Verify we have error state
        Assert.NotNull(service.LastError); // Verify error was recorded

        // Act: Reset to NotConfigured
        service.ResetNotConfigured();

        // Assert: Verify full reset to clean NotConfigured state
        Assert.Equal(TunnelStatus.NotConfigured, service.Status);
        Assert.Null(service.LastError);
        Assert.False(service.IsActive);
    }
}
