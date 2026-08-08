using OpenClaw.Shared;
using System.Net;

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
    public void TryMarkRestarting_RejectsUnknownTunnelGeneration()
    {
        using var service = new SshTunnelService(NullLogger.Instance);
        var tunnelExit = new SshTunnelExit(
            ExitCode: 42,
            Tunnel: new SshTunnelConfig("user", "host", 18789, 18789),
            Generation: 1);

        var accepted = service.TryMarkRestarting(tunnelExit);

        Assert.False(accepted);
        Assert.False(service.IsRestartPending(tunnelExit));
        Assert.Equal(TunnelStatus.NotConfigured, service.Status);
        Assert.Null(service.LastError);
    }

    [Theory]
    [InlineData(
        SshTunnelOwner.Settings,
        SshTunnelOwner.GatewayConnectionManager,
        SshTunnelOwner.GatewayConnectionManager)]
    [InlineData(
        SshTunnelOwner.GatewayConnectionManager,
        SshTunnelOwner.Settings,
        SshTunnelOwner.GatewayConnectionManager)]
    [InlineData(
        SshTunnelOwner.Settings,
        SshTunnelOwner.Settings,
        SshTunnelOwner.Settings)]
    public void ResolveOwnerForReuse_PreservesManagerOwnership(
        SshTunnelOwner currentOwner,
        SshTunnelOwner requestedOwner,
        SshTunnelOwner expectedOwner)
    {
        Assert.Equal(
            expectedOwner,
            SshTunnelService.ResolveOwnerForReuse(currentOwner, requestedOwner));
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

    [Fact]
    public async Task StopIfOwnedAsync_CancellationInterruptsOperationLockWait()
    {
        using var service = new SshTunnelService(NullLogger.Instance);
        var operationLockField = typeof(SshTunnelService).GetField(
            "_operationLock",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        var operationLock = Assert.IsType<object>(operationLockField?.GetValue(service));
        using var cts = new CancellationTokenSource();
        using var releaseLock = new ManualResetEventSlim();
        var lockEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lockHolder = Task.Run(() =>
        {
            lock (operationLock)
            {
                lockEntered.TrySetResult();
                releaseLock.Wait();
            }
        });
        await lockEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            var stop = Task.Run(
                async () => await service.StopIfOwnedAsync(
                    new SshTunnelConfig("user", "host", 18789, 45678),
                    ownershipGeneration: 1,
                    cts.Token));
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => stop.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            releaseLock.Set();
            await lockHolder.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void EnsurePortIsUnoccupied_RejectsExistingListener()
    {
        var snapshot = new WindowsTcpListenerSnapshotResult(
            [
                new WindowsTcpListenerInfo(
                    IPAddress.Loopback,
                    45678,
                    1234,
                    "other",
                    @"C:\other.exe",
                    DateTime.UtcNow)
            ],
            Ipv4Complete: true,
            Ipv6Complete: true);

        Assert.Throws<InvalidOperationException>(
            () => SshTunnelService.EnsurePortIsUnoccupied(snapshot, 45678));
    }

    [Fact]
    public void ListenerOwnershipChecks_IgnoreSamePortOnNonLoopbackAddress()
    {
        var startedAt = DateTime.UtcNow;
        var snapshot = new WindowsTcpListenerSnapshotResult(
            [
                new WindowsTcpListenerInfo(
                    IPAddress.Parse("192.168.10.20"),
                    45678,
                    9999,
                    "other",
                    @"C:\other.exe",
                    startedAt),
                new WindowsTcpListenerInfo(
                    IPAddress.Loopback,
                    45678,
                    4321,
                    "ssh",
                    @"C:\Windows\System32\OpenSSH\ssh.exe",
                    startedAt)
            ],
            Ipv4Complete: true,
            Ipv6Complete: true);

        Assert.True(SshTunnelService.ValidateListenerOwnership(
            snapshot,
            45678,
            4321,
            startedAt));

        var nonLoopbackOnly = snapshot with { Listeners = [snapshot.Listeners[0]] };
        SshTunnelService.EnsurePortIsUnoccupied(nonLoopbackOnly, 45678);
    }

    [Fact]
    public void EnsurePortIsUnoccupied_RejectsWildcardListener()
    {
        var snapshot = new WindowsTcpListenerSnapshotResult(
            [
                new WindowsTcpListenerInfo(
                    IPAddress.Any,
                    45678,
                    1234,
                    "other",
                    @"C:\other.exe",
                    DateTime.UtcNow)
            ],
            Ipv4Complete: true,
            Ipv6Complete: true);

        Assert.Throws<InvalidOperationException>(
            () => SshTunnelService.EnsurePortIsUnoccupied(snapshot, 45678));
    }

    [Fact]
    public void ValidateListenerOwnership_AcceptsOnlyExactLaunchedProcess()
    {
        var startedAt = DateTime.UtcNow;
        var owned = new WindowsTcpListenerSnapshotResult(
            [
                new WindowsTcpListenerInfo(
                    IPAddress.Loopback,
                    45678,
                    4321,
                    "ssh",
                    @"C:\Windows\System32\OpenSSH\ssh.exe",
                    startedAt)
            ],
            Ipv4Complete: true,
            Ipv6Complete: true);
        var unrelated = owned with
        {
            Listeners =
            [
                owned.Listeners[0] with { ProcessId = 9999 }
            ]
        };

        Assert.True(SshTunnelService.ValidateListenerOwnership(owned, 45678, 4321, startedAt));
        Assert.Throws<InvalidOperationException>(
            () => SshTunnelService.ValidateListenerOwnership(unrelated, 45678, 4321, startedAt));
    }

    [Fact]
    public void ListenerOwnershipChecks_FailClosedWhenSnapshotIsIncomplete()
    {
        var incomplete = new WindowsTcpListenerSnapshotResult(
            [],
            Ipv4Complete: true,
            Ipv6Complete: false);

        Assert.Throws<InvalidOperationException>(
            () => SshTunnelService.EnsurePortIsUnoccupied(incomplete, 45678));
        Assert.Throws<InvalidOperationException>(
            () => SshTunnelService.ValidateListenerOwnership(
                incomplete,
                45678,
                4321,
                DateTime.UtcNow));
    }
}
