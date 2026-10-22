using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.IntegrationTests;

public sealed class GatewayFixtureStartupTests
{
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
        try
        {
            if (foreign)
            {
                Assert.NotNull(startup.Foreign);
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
        using var released = new TcpListener(IPAddress.Loopback, controlPort);
        released.Start();
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
                    observedChild = Process.GetProcessById(child.Id);
                    _ = observedChild.SafeHandle;
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
                        Assert.False(child.HasExited, "Foreign listener exited before publishing readiness.");
                        await Task.Delay(20, deadline.Token);
                    }

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

            Assert.NotNull(observedChild);
            Assert.True(observedChild.HasExited, "Startup returned before reaping its child.");
            Assert.False(File.Exists(countFile), "Startup left its readiness file behind.");
            using var control = new TcpListener(IPAddress.Loopback, controlPort);
            using var gateway = new TcpListener(IPAddress.Loopback, controlPort - 2);
            control.Start();
            gateway.Start();
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
}
