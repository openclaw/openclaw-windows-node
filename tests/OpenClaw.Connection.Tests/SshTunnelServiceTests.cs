using OpenClaw.Shared;

namespace OpenClaw.Connection.Tests;

public sealed class SshTunnelServiceTests
{
    [Fact]
    public void ResetNotConfigured_ClearsStoppedTunnelErrorState()
    {
        using var service = new SshTunnelService(NullLogger.Instance);
        service.MarkRestarting(exitCode: 255);

        service.ResetNotConfigured();

        Assert.Equal(TunnelStatus.NotConfigured, service.Status);
        Assert.Null(service.LastError);
        Assert.False(service.IsActive);
    }
}
