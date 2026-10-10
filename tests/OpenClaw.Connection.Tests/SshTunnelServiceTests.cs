using OpenClaw.Shared;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

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
    public void AllowsDestination_PinsTheOriginalGateway()
    {
        const int port = 45681;
        DashboardForwardPortGuard.Release(port);
        DashboardForwardPortGuard.Hold(port);
        try
        {
            Assert.True(DashboardForwardPortGuard.AllowsDestination(port, "user", "gateway.example", 18789, 22));
            Assert.False(DashboardForwardPortGuard.AllowsDestination(port, "user", "other.example", 18789, 22));
            Assert.False(DashboardForwardPortGuard.AllowsDestination(port, "user", "gateway.example", 18790, 22));
            Assert.False(DashboardForwardPortGuard.AllowsDestination(port, "user", "gateway.example", 18789, 2222));
        }
        finally
        {
            DashboardForwardPortGuard.Release(port);
        }
    }

    [Fact]
    public void DashboardRestartOwner_PreservesManagerOwnership()
    {
        Assert.Equal(
            SshTunnelOwner.GatewayConnectionManager,
            SshTunnelService.DashboardRestartOwner(SshTunnelOwner.GatewayConnectionManager, runningOnRequestedPort: true));
        Assert.Equal(
            SshTunnelOwner.Settings,
            SshTunnelService.DashboardRestartOwner(SshTunnelOwner.GatewayConnectionManager, runningOnRequestedPort: false));
        Assert.Equal(
            SshTunnelOwner.Settings,
            SshTunnelService.DashboardRestartOwner(SshTunnelOwner.Settings, runningOnRequestedPort: true));
    }

    [Fact]
    public void Hold_ReservesBothLoopbackRoutes()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        DashboardForwardPortGuard.Hold(port);
        try
        {
            using var ipv4 = new TcpListener(IPAddress.Loopback, port);
            Assert.ThrowsAny<SocketException>(() => ipv4.Start());
            if (Socket.OSSupportsIPv6)
            {
                var ipv6 = new TcpListener(IPAddress.IPv6Loopback, port);
                ipv6.Server.DualMode = false;
                Assert.ThrowsAny<SocketException>(() => ipv6.Start());
            }

            Assert.Equal("127.0.0.1", SshTunnelService.LoopbackWebSocketHost(port));
            var confirmed = false;
            for (var attempt = 0; attempt < 20 && !confirmed; attempt++)
            {
                confirmed = SshTunnelService.TryConfirmRetainedPublicRoute(port);
                if (!confirmed)
                    Thread.Sleep(50);
            }

            Assert.True(confirmed);
        }
        finally
        {
            DashboardForwardPortGuard.Release(port);
        }
    }

    [Fact]
    public async Task EnsureSettingsOwnedForwardReadyAsync_ReleasesGuardWhenStartupFails()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var service = new SshTunnelService(NullLogger.Instance);
        try
        {
            var ready = await service.EnsureSettingsOwnedForwardReadyAsync(
                new SshTunnelConfig("user", "host", 18789, port, false, 0),
                CancellationToken.None);

            Assert.False(ready.Owned);
            Assert.False(DashboardForwardPortGuard.IsHolding(port));
        }
        finally
        {
            DashboardForwardPortGuard.Release(port);
        }
    }

    [Fact]
    public void RejectForeignForwardPort_AllowsTheDashboardGuard()
    {
        const int port = 45679;
        DashboardForwardPortGuard.Release(port);
        DashboardForwardPortGuard.Hold(port);
        try
        {
            SshTunnelService.RejectForeignForwardPort(port);
        }
        finally
        {
            DashboardForwardPortGuard.Release(port);
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
    public async Task EnsureSettingsOwnedForwardReadyAsync_RejectsForeignLocalListener()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var service = new SshTunnelService(NullLogger.Instance);

        var ready = await service.EnsureSettingsOwnedForwardReadyAsync(
            new SshTunnelConfig("user", "host", 18789, port),
            CancellationToken.None);

        Assert.False(ready.Owned);
        Assert.False(service.IsRunning);
        Assert.NotEqual(TunnelStatus.Up, service.Status);
        Assert.Contains(port.ToString(), service.LastError);
        Assert.Contains("already owned", service.LastError);
        Assert.False(service.IsSettingsOwnedForwardCurrent(service.OwnershipGeneration, port));
    }

    [Fact]
    public async Task EnsureSettingsOwnedForwardReadyAsync_RejectsForeignBrowserProxyListener()
    {
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        var proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
        var localPort = proxyPort - 2;
        using var service = new SshTunnelService(NullLogger.Instance);

        var ready = await service.EnsureSettingsOwnedForwardReadyAsync(
            new SshTunnelConfig(
                "user",
                "host",
                18789,
                localPort,
                IncludeBrowserProxyForward: true),
            CancellationToken.None);

        Assert.False(ready.Owned);
        Assert.False(service.IsRunning);
        Assert.NotEqual(TunnelStatus.Up, service.Status);
        Assert.NotNull(service.LastError);
        Assert.Contains("already owned", service.LastError);
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

    [Fact]
    public void EnsureStarted_WhileBrowserHandoffLeaseHeld_DoesNotReplaceTrackedProcess()
    {
        using var service = new SshTunnelService(NullLogger.Instance);
        var config = new SshTunnelConfig("user", "host", 18789, 45678);
        using var process = PlantRunningTunnel(
            service,
            config,
            SshTunnelOwner.Settings,
            generation: 4);
        Assert.True(service.TryEnterBrowserHandoff(4, config.LocalPort));

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => service.EnsureStarted("user", "host", 18789, 45679));

            Assert.Equal("SSH tunnel is held for a dashboard launch.", ex.Message);
            Assert.Same(process, TrackedProcess(service));
            Assert.Equal(4, service.OwnershipGeneration);
            Assert.True(service.IsRunning);
            Assert.Equal(TunnelStatus.Up, service.Status);
            Assert.Equal(config, service.ActiveConfig);
        }
        finally
        {
            service.ExitBrowserHandoff();
        }
    }

    [Fact]
    public async Task StopIfOwnedAsync_DuringBrowserHandoff_CancelsTheHoldAndStops()
    {
        using var service = new SshTunnelService(NullLogger.Instance);
        var config = new SshTunnelConfig("user", "host", 18789, 45678);
        using var process = PlantRunningTunnel(
            service,
            config,
            SshTunnelOwner.GatewayConnectionManager,
            generation: 4);
        Assert.True(service.TryEnterBrowserHandoff(4, config.LocalPort));

        var stopped = await service.StopIfOwnedAsync(config, ownershipGeneration: 4, CancellationToken.None);

        Assert.True(stopped);
        Assert.Equal(0, service.BrowserHandoffLeaseCount);
        Assert.False(service.IsBrowserHandoffOpen(1));
        Assert.Null(TrackedProcess(service));
        Assert.False(service.IsRunning);
        Assert.Equal(TunnelStatus.Stopped, service.Status);
        Assert.Throws<InvalidOperationException>(() => process.HasExited);
    }

    [Fact]
    public void ResetNotConfigured_DuringBrowserHandoff_CancelsTheHoldImmediately()
    {
        using var service = new SshTunnelService(NullLogger.Instance);
        var config = new SshTunnelConfig("user", "host", 18789, 45678);
        using var process = PlantRunningTunnel(
            service,
            config,
            SshTunnelOwner.Settings,
            generation: 4);
        SetPrivate(service, "<LastError>k__BackingField", "tunnel still up");
        Assert.True(service.TryEnterBrowserHandoff(4, config.LocalPort));
        Assert.True(service.TryEnterBrowserHandoff(4, config.LocalPort));

        service.ResetNotConfigured();

        Assert.Equal(0, service.BrowserHandoffLeaseCount);
        Assert.Null(TrackedProcess(service));
        Assert.Throws<InvalidOperationException>(() => process.HasExited);
        Assert.Equal(TunnelStatus.NotConfigured, service.Status);
        Assert.Null(service.LastError);
        Assert.False(service.IsRunning);
    }

    [Fact]
    public void Stop_AfterNavigationSubmitted_StopsSshAndKeepsThePort()
    {
        var port = 45678;
        DashboardForwardPortGuard.Release(port);
        using (var service = new SshTunnelService(NullLogger.Instance))
        {
            var config = new SshTunnelConfig("user", "host", 18789, port);
            using var process = PlantRunningTunnel(
                service,
                config,
                SshTunnelOwner.Settings,
                generation: 4);
            Assert.True(service.TryEnterBrowserHandoff(4, config.LocalPort, process.Id, out var handoffId));
            Assert.True(service.TryBeginDashboardNavigation(handoffId));
            Assert.True(service.CompleteDashboardNavigation(handoffId, opened: true, processId: null));

            service.Stop();

            Assert.False(service.HasDeferredStop);
            Assert.Equal(0, service.BrowserHandoffLeaseCount);
            Assert.False(service.IsRunning);
            Assert.True(DashboardForwardPortGuard.IsHolding(port));
            Assert.Throws<InvalidOperationException>(() => process.HasExited);

            service.Stop();

            Assert.True(DashboardForwardPortGuard.IsHolding(port));
        }

        DashboardForwardPortGuard.Release(port);
    }

    [Fact]
    public async Task Watch_DoesNotReleaseWhenTheActivationProcessHasExited()
    {
        var service = new SshTunnelService(NullLogger.Instance);
        var config = new SshTunnelConfig("user", "host", 18789, 45678);
        DashboardForwardPortGuard.Release(config.LocalPort);
        try
        {
        using var process = PlantRunningTunnel(
            service,
            config,
            SshTunnelOwner.Settings,
            generation: 4);
        using var shell = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        Assert.NotNull(shell);
        Assert.True(shell.WaitForExit(3_000));
        Assert.True(service.TryEnterBrowserHandoff(4, config.LocalPort, process.Id, out var handoffId));
        Assert.True(service.TryBeginDashboardNavigation(handoffId));
        Assert.True(service.CompleteDashboardNavigation(handoffId, opened: true, processId: shell.Id));

        var settled = await service.WatchBrowserHandoffConsumptionAsync(
            handoffId,
            config.LocalPort,
            TimeSpan.Zero,
            consumptionProbe: null);

        Assert.False(settled);
        Assert.Equal(1, service.BrowserHandoffLeaseCount);
        Assert.True(service.IsRunning);
        Assert.False(process.HasExited);
        service.ExitBrowserHandoff(handoffId);
        }
        finally
        {
            service.Dispose();
            DashboardForwardPortGuard.Release(config.LocalPort);
        }
    }

    [Fact]
    public async Task Watch_SubmittedNavigation_KeepsThePortWhenSshExits()
    {
        var service = new SshTunnelService(NullLogger.Instance);
        var config = new SshTunnelConfig("user", "host", 18789, 45678);
        DashboardForwardPortGuard.Release(config.LocalPort);
        try
        {
        using var process = PlantRunningTunnel(
            service,
            config,
            SshTunnelOwner.Settings,
            generation: 4);
        Assert.True(service.TryEnterBrowserHandoff(4, config.LocalPort, process.Id, out var handoffId));
        Assert.True(service.TryBeginDashboardNavigation(handoffId));
        Assert.True(service.CompleteDashboardNavigation(handoffId, opened: true, processId: null));
        process.Kill(entireProcessTree: true);
        Assert.True(process.WaitForExit(3_000));

        var settled = await service.WatchBrowserHandoffConsumptionAsync(
            handoffId,
            config.LocalPort,
            TimeSpan.Zero,
            consumptionProbe: null);

        Assert.True(settled);
        Assert.False(service.IsBrowserHandoffOpen(handoffId));
        Assert.Equal(0, service.BrowserHandoffLeaseCount);
        Assert.False(service.HasDeferredStop);
        Assert.True(DashboardForwardPortGuard.IsHolding(config.LocalPort));
        Assert.False(DashboardForwardPortGuard.AllowsDestination(
            config.LocalPort,
            config.User,
            config.Host,
            config.RemotePort,
            2222));
        service.Stop();
        Assert.True(DashboardForwardPortGuard.IsHolding(config.LocalPort));
        Assert.Equal(0, service.BrowserHandoffLeaseCount);
        service.ExitBrowserHandoff(handoffId);
        }
        finally
        {
            service.Dispose();
            DashboardForwardPortGuard.Release(config.LocalPort);
        }
    }

    [Fact]
    public void Stop_DuringBrowserHandoff_CancelsTheHoldAndStops()
    {
        using var service = new SshTunnelService(NullLogger.Instance);
        var config = new SshTunnelConfig("user", "host", 18789, 45678);
        using var process = PlantRunningTunnel(
            service,
            config,
            SshTunnelOwner.Settings,
            generation: 4);
        Assert.True(service.TryEnterBrowserHandoff(4, config.LocalPort));

        service.Stop();

        Assert.Equal(0, service.BrowserHandoffLeaseCount);
        Assert.False(service.HasDeferredStop);
        Assert.Null(TrackedProcess(service));
        Assert.Throws<InvalidOperationException>(() => process.HasExited);
        Assert.Equal(TunnelStatus.Stopped, service.Status);
        Assert.False(service.IsRunning);
    }

    [Fact]
    public void StopDuringBrowserHandoff_DoesNotWaitForBrowserTraffic()
    {
        using var service = new SshTunnelService(NullLogger.Instance);
        var config = new SshTunnelConfig("user", "host", 18789, 45678);
        using var process = PlantRunningTunnel(
            service,
            config,
            SshTunnelOwner.Settings,
            generation: 4);
        Assert.True(service.TryEnterBrowserHandoff(4, config.LocalPort));

        service.Stop();

        Assert.False(service.HasDeferredStop);
        Assert.Equal(0, service.BrowserHandoffLeaseCount);
        Assert.Null(TrackedProcess(service));
        Assert.Throws<InvalidOperationException>(() => process.HasExited);
        Assert.Equal(TunnelStatus.Stopped, service.Status);
    }

    [Fact]
    public void ReleaseBrowserHandoff_AfterStop_FindsNoLease()
    {
        using var service = new SshTunnelService(NullLogger.Instance);
        var config = new SshTunnelConfig("user", "host", 18789, 45678);
        using var process = PlantRunningTunnel(
            service,
            config,
            SshTunnelOwner.Settings,
            generation: 4);
        Assert.True(service.TryEnterBrowserHandoff(4, config.LocalPort));
        service.Stop();

        Assert.False(service.TryReleaseBrowserHandoffUnlessDeferred(config.LocalPort, out var watch));
        Assert.False(watch);
        Assert.Equal(0, service.BrowserHandoffLeaseCount);
        Assert.False(service.HasDeferredStop);
        Assert.Throws<InvalidOperationException>(() => process.HasExited);
        Assert.False(service.IsRunning);
    }

    [Fact]
    public void ReleaseBrowserHandoff_KeepsLeaseWhenNoStopIsPending()
    {
        using var service = new SshTunnelService(NullLogger.Instance);
        var config = new SshTunnelConfig("user", "host", 18789, 45678);
        using var process = PlantRunningTunnel(
            service,
            config,
            SshTunnelOwner.Settings,
            generation: 4);
        Assert.True(service.TryEnterBrowserHandoff(4, config.LocalPort));

        try
        {
            Assert.True(service.TryReleaseBrowserHandoffUnlessDeferred(config.LocalPort, out var watch));

            Assert.True(watch);
            Assert.Equal(1, service.BrowserHandoffLeaseCount);
            Assert.False(service.HasDeferredStop);
            Assert.False(process.HasExited);
            Assert.True(service.IsRunning);
        }
        finally
        {
            if (service.BrowserHandoffLeaseCount > 0)
                service.ExitBrowserHandoff();
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void TryEnterBrowserHandoff_RejectsReplacementProcess()
    {
        using var service = new SshTunnelService(NullLogger.Instance);
        var config = new SshTunnelConfig("user", "host", 18789, 45678);
        using var process = PlantRunningTunnel(
            service,
            config,
            SshTunnelOwner.Settings,
            generation: 4);
        var otherProcessId = process.Id == 1 ? 2 : 1;

        Assert.False(service.TryEnterBrowserHandoff(4, config.LocalPort, otherProcessId));
        Assert.Equal(0, service.BrowserHandoffLeaseCount);

        Assert.True(service.TryEnterBrowserHandoff(4, config.LocalPort, process.Id, out var handoffId));
        Assert.Equal(1, service.BrowserHandoffLeaseCount);
        service.NoteBrowserHandoffClient(handoffId, 88);
        Assert.Equal(88, service.HandoffBrowserProcessId(handoffId));
        service.NoteBrowserHandoffClient(handoffId, 0);
        Assert.Equal(88, service.HandoffBrowserProcessId(handoffId));

        service.ExitBrowserHandoff(handoffId);
        Assert.Null(service.HandoffBrowserProcessId(handoffId));
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);
    }

    [Fact]
    public void BrowserHandoff_SecondOpenKeepsTheFirstBrowserIdentity()
    {
        using var service = new SshTunnelService(NullLogger.Instance);
        var config = new SshTunnelConfig("user", "host", 18789, 45678);
        using var process = PlantRunningTunnel(
            service,
            config,
            SshTunnelOwner.Settings,
            generation: 4);
        Assert.True(service.TryEnterBrowserHandoff(4, config.LocalPort, process.Id, out var first));
        Assert.True(service.TryEnterBrowserHandoff(4, config.LocalPort, process.Id, out var second));

        service.NoteBrowserHandoffClient(first, 11);
        service.NoteBrowserHandoffClient(second, 22);

        Assert.Equal(11, service.HandoffBrowserProcessId(first));
        Assert.Equal(22, service.HandoffBrowserProcessId(second));
        Assert.Equal(2, service.BrowserHandoffLeaseCount);

        service.ExitBrowserHandoff(second);
        Assert.Equal(11, service.HandoffBrowserProcessId(first));
        Assert.Equal(1, service.BrowserHandoffLeaseCount);
        service.ExitBrowserHandoff(first);
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);
    }

    [Fact]
    public async Task WatchBrowserHandoff_TimesOutWithoutReleasingOwnedListener()
    {
        using var service = new SshTunnelService(NullLogger.Instance);
        var config = new SshTunnelConfig("user", "host", 18789, 45678);
        using var process = PlantRunningTunnel(
            service,
            config,
            SshTunnelOwner.Settings,
            generation: 4);
        Assert.True(service.TryEnterBrowserHandoff(4, config.LocalPort));

        try
        {
            var completed = await service.WatchBrowserHandoffConsumptionAsync(
                config.LocalPort,
                TimeSpan.Zero,
                static _ => false);

            Assert.False(completed);
            Assert.False(process.HasExited);
            Assert.Equal(1, service.BrowserHandoffLeaseCount);
            Assert.False(service.HasDeferredStop);
            Assert.True(service.IsRunning);
            Assert.Equal(TunnelStatus.Up, service.Status);
        }
        finally
        {
            if (service.BrowserHandoffLeaseCount > 0)
                service.ExitBrowserHandoff();
        }
    }

    [Fact]
    public async Task WatchBrowserHandoff_NullProbe_HoldsUntilTheSshProcessIsGone()
    {
        using var service = new SshTunnelService(NullLogger.Instance);
        var config = new SshTunnelConfig("user", "host", 18789, 45678);
        using var process = PlantRunningTunnel(
            service,
            config,
            SshTunnelOwner.Settings,
            generation: 4);
        Assert.True(service.TryEnterBrowserHandoff(4, config.LocalPort, process.Id, out var handoffId));

        var waiting = await service.WatchBrowserHandoffConsumptionAsync(
            handoffId,
            config.LocalPort,
            TimeSpan.Zero,
            consumptionProbe: null);
        Assert.False(waiting);
        Assert.Equal(1, service.BrowserHandoffLeaseCount);
        Assert.False(service.HasDeferredStop);
        Assert.True(service.IsRunning);

        process.Kill(entireProcessTree: true);
        Assert.True(process.WaitForExit(3_000));

        var settled = await service.WatchBrowserHandoffConsumptionAsync(
            handoffId,
            config.LocalPort,
            TimeSpan.FromSeconds(2),
            consumptionProbe: null);
        Assert.True(settled);
        Assert.False(service.IsBrowserHandoffOpen(handoffId));
        Assert.Equal(0, service.BrowserHandoffLeaseCount);
        Assert.False(service.HasDeferredStop);
        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task WatchBrowserHandoff_ProbeCompletesDeferredStop()
    {
        using var service = new SshTunnelService(NullLogger.Instance);
        var config = new SshTunnelConfig("user", "host", 18789, 45678);
        using var process = PlantRunningTunnel(
            service,
            config,
            SshTunnelOwner.Settings,
            generation: 4);
        Assert.True(service.TryEnterBrowserHandoff(4, config.LocalPort));
        service.Stop();

        try
        {
            var completed = await service.WatchBrowserHandoffConsumptionAsync(
                config.LocalPort,
                TimeSpan.FromSeconds(5),
                static _ => true);

            Assert.True(completed);
            Assert.Equal(0, service.BrowserHandoffLeaseCount);
            Assert.False(service.HasDeferredStop);
            Assert.Null(TrackedProcess(service));
            Assert.Throws<InvalidOperationException>(() => process.HasExited);
            Assert.Equal(TunnelStatus.Stopped, service.Status);
        }
        finally
        {
            if (service.BrowserHandoffLeaseCount > 0)
                service.ExitBrowserHandoff();
        }
    }

    private static Process PlantRunningTunnel(
        SshTunnelService service,
        SshTunnelConfig config,
        SshTunnelOwner owner,
        long generation)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /c ping -n 30 127.0.0.1",
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        Assert.True(process.Start());
        SetPrivate(service, "_process", process);
        SetPrivate(service, "_processStarted", true);
        SetPrivate(service, "_currentConfig", config);
        SetPrivate(service, "_currentOwner", owner);
        SetPrivate(
            service,
            "_lastSpec",
            $"{config.User}@{config.Host}:{config.SshPort}:{config.LocalPort}:{config.RemotePort}:browserProxy={config.IncludeBrowserProxyForward}");
        SetPrivate(service, "_lifecycleGeneration", generation);
        SetPrivate(service, "<Status>k__BackingField", TunnelStatus.Up);
        return process;
    }

    private static Process? TrackedProcess(SshTunnelService service) =>
        (Process?)GetPrivate(service, "_process");

    private static SshTunnelOwner CurrentOwner(SshTunnelService service) =>
        (SshTunnelOwner)GetPrivate(service, "_currentOwner")!;

    private static void SetPrivate(object target, string name, object? value) =>
        PrivateField(name).SetValue(target, value);

    private static object? GetPrivate(object target, string name) =>
        PrivateField(name).GetValue(target);

    private static FieldInfo PrivateField(string name)
    {
        var field = typeof(SshTunnelService).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field;
    }
}
