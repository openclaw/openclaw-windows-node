using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using OpenClaw.Connection;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.IntegrationTests;

// Fixture ports come from the shared dynamic range, so release is proven by listener ownership.
[Collection("Gateway fixture environment")]
public sealed class GatewayFixtureStartupTests
{
    [Fact]
    public async Task McpTokenRead_WaitsForWriterToPublishCompleteToken()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task<string> read;
            await using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await writer.WriteAsync("synthetic-fixture-token\n"u8.ToArray(), deadline.Token);
                await writer.FlushAsync(deadline.Token);
                read = GatewayFixtureRun.ReadPublishedMcpTokenAsync(path, deadline.Token);
                Assert.False(read.IsCompleted);
            }
            Assert.Equal("synthetic-fixture-token", await read);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task McpTokenRead_HonorsCancellationWhileLocked()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var stop = new CancellationTokenSource();
            await using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var read = GatewayFixtureRun.ReadPublishedMcpTokenAsync(path, stop.Token);
            Assert.False(read.IsCompleted);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task McpTokenRead_DoesNotHideMissingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            GatewayFixtureRun.ReadPublishedMcpTokenAsync(path, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserControlStartup_TransfersLiveResources(bool foreign)
    {
        var startup = await GatewayFixtureRun.StartGatewayAsync(
            "synthetic-startup-token",
            GatewayScenario.CreateBrowse(),
            enableNodeBrowserProxy: true,
            foreignBrowserControl: foreign,
            CancellationToken.None);
        Process? observedChild = null;
        var controlPort = startup.Gateway.Endpoint.Port + 2;
        var controlOwner = Environment.ProcessId;
        try
        {
            if (foreign)
            {
                Assert.NotNull(startup.Foreign);
                controlOwner = startup.Foreign.Id;
                observedChild = Process.GetProcessById(startup.Foreign.Id);
                _ = observedChild.SafeHandle;
                Assert.False(observedChild.HasExited);
                Assert.True(File.Exists(startup.ForeignCountFile));
                Console.WriteLine(
                    $"fixture-child pid={observedChild.Id} start={observedChild.StartTime.ToUniversalTime():O} " +
                    $"exe={startup.Foreign.StartInfo.FileName} " +
                    $"args={string.Join(" ", startup.Foreign.StartInfo.ArgumentList)} " +
                    $"controlPort={controlPort} gatewayPort={startup.Gateway.Endpoint.Port}");
            }
            else
            {
                Assert.NotNull(startup.Control);
            }
            using var occupied = new TcpListener(IPAddress.Loopback, controlPort);
            Assert.Throws<SocketException>(() => occupied.Start());
            Assert.True(IsListening(controlPort, controlOwner), "The transferred control listener is not owned by its run.");
        }
        finally
        {
            try
            {
                await GatewayFixtureRun.DisposeGatewayResourcesAsync(
                    startup.Gateway, startup.Control, startup.Foreign, startup.ForeignCountFile);
                if (observedChild is not null)
                    Assert.True(observedChild.HasExited);
                Assert.False(File.Exists(startup.ForeignCountFile));
            }
            finally { observedChild?.Dispose(); }
        }
        Assert.False(IsListening(controlPort, controlOwner), "Disposal left the control listener open.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForeignControlReadinessFailure_ReapsChildAndReleasesResources(bool cancel)
    {
        using var stop = new CancellationTokenSource();
        Process? observedChild = null;
        string? countFile = null;
        var controlPort = 0;
        var childId = 0;
        var reachedForcedFailure = false;
        try
        {
            var startup = GatewayFixtureRun.StartGatewayAsync(
                "synthetic-startup-token",
                GatewayScenario.CreateBrowse(),
                enableNodeBrowserProxy: true,
                foreignBrowserControl: true,
                stop.Token,
                async (child, path, cancellationToken) =>
                {
                    observedChild?.Dispose();
                    observedChild = null;
                    try
                    {
                        observedChild = Process.GetProcessById(child.Id);
                        _ = observedChild.SafeHandle;
                    }
                    catch (ArgumentException)
                    {
                        if (ExitedOnPortConflict(child))
                            return false;
                        throw;
                    }
                    childId = child.Id;
                    countFile = path;
                    controlPort = int.Parse(child.StartInfo.ArgumentList[3], CultureInfo.InvariantCulture);
                    Console.WriteLine(
                        $"fixture-child pid={child.Id} start={child.StartTime.ToUniversalTime():O} " +
                        $"exe={child.StartInfo.FileName} args={string.Join(" ", child.StartInfo.ArgumentList)} " +
                        $"controlPort={controlPort} gatewayPort={controlPort - 2} readyFile={path}");
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    while (!File.Exists(path))
                    {
                        if (ExitedOnPortConflict(child))
                            return false;
                        await Task.Delay(20, deadline.Token);
                    }
                    Assert.True(IsListening(controlPort, child.Id), "The foreign child does not own the control listener.");
                    Assert.True(IsListening(controlPort - 2, Environment.ProcessId), "This run does not own the gateway listener.");

                    reachedForcedFailure = true;
                    if (cancel)
                    {
                        stop.Cancel();
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    throw new InvalidDataException("Synthetic readiness failure.");
                });

            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
            else
                await Assert.ThrowsAsync<InvalidDataException>(() => startup);

            Assert.True(reachedForcedFailure, "Startup failed before the injected readiness failure.");
            Assert.NotNull(observedChild);
            Assert.True(observedChild.HasExited, "Startup returned before reaping its child.");
            Assert.False(File.Exists(countFile), "Startup left its readiness file behind.");
            Assert.False(IsListening(controlPort, childId), "Startup left the foreign control listener open.");
            Assert.False(IsListening(controlPort - 2, Environment.ProcessId), "Startup left the gateway listener open.");
        }
        finally
        {
            stop.Cancel();
            if (observedChild is not null)
            {
                try
                {
                    if (!observedChild.HasExited)
                    {
                        observedChild.Kill(entireProcessTree: true);
                        await observedChild.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
                finally { observedChild.Dispose(); }
            }
            if (countFile is not null)
                File.Delete(countFile);
        }
    }

    // Exit code 1 is the foreign child's bind conflict: another host process took the dynamic control
    // port first, so startup retries beside a new gateway. Any other early exit is a real failure.
    private static bool ExitedOnPortConflict(Process child)
    {
        if (!child.HasExited)
            return false;
        Assert.True(child.ExitCode == 1,
            $"Foreign listener pid={child.Id} exited with {child.ExitCode} before publishing readiness.");
        return true;
    }

    // Another host process may claim a released dynamic port at any time; a foreign exclusive
    // wildcard bind then fails a rebind with WSAEACCES. Only listeners owned by this run count.
    private static bool IsListening(int port, int processId)
    {
        var snapshot = WindowsTcpListenerSnapshot.Capture();
        Assert.True(snapshot.Ipv4Complete, "The IPv4 listener table could not be read.");
        return snapshot.Listeners.Any(listener =>
            listener.Port == port && listener.ProcessId == processId && IPAddress.IsLoopback(listener.Address));
    }
}
