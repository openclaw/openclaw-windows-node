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

    [Fact]
    public void InitialState_IsNotConfiguredWithNoError()
    {
        using var service = new SshTunnelService(NullLogger.Instance);

        Assert.Equal(TunnelStatus.NotConfigured, service.Status);
        Assert.Null(service.LastError);
        Assert.False(service.IsActive);
        Assert.False(service.IsRunning);
        Assert.Null(service.LocalTunnelUrl);
    }

    [Fact]
    public void MarkRestarting_SetsRestartingStatusAndErrorMessage()
    {
        using var service = new SshTunnelService(NullLogger.Instance);

        service.MarkRestarting(exitCode: 42);

        Assert.Equal(TunnelStatus.Restarting, service.Status);
        Assert.NotNull(service.LastError);
        Assert.Contains("42", service.LastError);
    }

    [Fact]
    public void MarkRestarting_ErrorMessageContainsExitCode()
    {
        using var service = new SshTunnelService(NullLogger.Instance);

        service.MarkRestarting(exitCode: 255);

        Assert.Contains("255", service.LastError);
    }

    [Fact]
    public void Stop_FromNotConfigured_StatusRemainsNotConfigured()
    {
        // Stop() when no process has been started and state is NotConfigured
        // should not transition to Stopped.
        using var service = new SshTunnelService(NullLogger.Instance);

        service.Stop();

        Assert.Equal(TunnelStatus.NotConfigured, service.Status);
    }

    [Fact]
    public void Stop_FromRestarting_TransitionsToStopped()
    {
        using var service = new SshTunnelService(NullLogger.Instance);

        service.MarkRestarting(exitCode: 1);
        service.Stop();

        Assert.Equal(TunnelStatus.Stopped, service.Status);
    }

    [Fact]
    public void Stop_ClearsBrowserProxyPorts()
    {
        using var service = new SshTunnelService(NullLogger.Instance);

        // MarkRestarting keeps error state; Stop should clean proxy port tracking.
        service.MarkRestarting(exitCode: 1);
        service.Stop();

        Assert.Equal(0, service.CurrentBrowserProxyLocalPort);
        Assert.Equal(0, service.CurrentBrowserProxyRemotePort);
    }

    [Fact]
    public void LocalTunnelUrl_IsNullWhenNotRunning()
    {
        using var service = new SshTunnelService(NullLogger.Instance);

        Assert.Null(service.LocalTunnelUrl);
    }

    [Fact]
    public void CreateSnapshot_DefaultState_ReturnsNotConfiguredSnapshot()
    {
        using var service = new SshTunnelService(NullLogger.Instance);

        var snapshot = service.CreateSnapshot();

        Assert.Equal(TunnelStatus.NotConfigured, snapshot.Status);
        Assert.False(snapshot.IsRunning);
        Assert.Null(snapshot.LastError);
    }

    [Fact]
    public void CreateSnapshot_AfterMarkRestarting_CapturesErrorAndStatus()
    {
        using var service = new SshTunnelService(NullLogger.Instance);

        service.MarkRestarting(exitCode: 7);
        var snapshot = service.CreateSnapshot();

        Assert.Equal(TunnelStatus.Restarting, snapshot.Status);
        Assert.NotNull(snapshot.LastError);
        Assert.Contains("7", snapshot.LastError);
    }

    [Fact]
    public void ResetNotConfigured_FromStopped_RestoresNotConfigured()
    {
        using var service = new SshTunnelService(NullLogger.Instance);

        // Transition to Restarting then Stopped, then reset.
        service.MarkRestarting(exitCode: 1);
        service.Stop();
        Assert.Equal(TunnelStatus.Stopped, service.Status);

        service.ResetNotConfigured();

        Assert.Equal(TunnelStatus.NotConfigured, service.Status);
        Assert.Null(service.LastError);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var service = new SshTunnelService(NullLogger.Instance);

        var ex = Record.Exception(() => service.Dispose());

        Assert.Null(ex);
    }
}
