using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace OpenClaw.E2ETests.Setup;

public sealed class MirroredWslPortLeaseTests
{
    [Fact]
    public void CandidateSequence_CoversOnlyConfiguredLowRangeWithoutDuplicates()
    {
        var candidates = MirroredWslPortLease.CreateCandidateSequence(137).ToArray();

        Assert.Equal(
            MirroredWslPortLease.CandidateRangeEnd - MirroredWslPortLease.CandidateRangeStart + 1,
            candidates.Length);
        Assert.Equal(candidates.Length, candidates.Distinct().Count());
        Assert.All(candidates, port => Assert.InRange(
            port,
            MirroredWslPortLease.CandidateRangeStart,
            MirroredWslPortLease.CandidateRangeEnd));
        Assert.Equal(MirroredWslPortLease.CandidateRangeStart + 137, candidates[0]);
    }

    [Fact]
    public void BindProbe_UsesOnlyLoopbackAddressesAndReleasesThePort()
    {
        Assert.Equal(IPAddress.Loopback, MirroredWslPortLease.BindProbeAddresses[0]);
        Assert.All(MirroredWslPortLease.BindProbeAddresses, address =>
        {
            Assert.True(IPAddress.IsLoopback(address));
            Assert.NotEqual(IPAddress.Any, address);
            Assert.NotEqual(IPAddress.IPv6Any, address);
        });

        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();

        Assert.True(MirroredWslPortLease.CanBindLoopbackExclusively(port));

        var rebound = new TcpListener(IPAddress.Loopback, port);
        rebound.Server.ExclusiveAddressUse = true;
        rebound.Start();
        rebound.Stop();
    }

    [Fact]
    public void BindProbe_RejectsIpv6LoopbackCollision()
    {
        if (!Socket.OSSupportsIPv6)
            return;

        var ipv6Reservation = new TcpListener(IPAddress.IPv6Loopback, 0);
        ipv6Reservation.Server.ExclusiveAddressUse = true;
        ipv6Reservation.Start();
        var port = ((IPEndPoint)ipv6Reservation.LocalEndpoint).Port;

        try
        {
            Assert.False(MirroredWslPortLease.CanBindLoopbackExclusively(port));
        }
        finally
        {
            ipv6Reservation.Stop();
        }

        Assert.True(MirroredWslPortLease.CanBindLoopbackExclusively(port));
    }

    [Fact]
    public void WindowsPortState_ParsesDynamicAndExcludedNetshRanges()
    {
        var dynamicRange = WindowsTcpPortState.ParseDynamicRange(
            """
            Protocol tcp Dynamic Port Range
            ---------------------------------
            Start Port      : 49152
            Number of Ports : 16384
            """);
        var excludedRanges = WindowsTcpPortState.ParseExcludedRanges(
            """
            Protocol tcp Port Exclusion Ranges

            Start Port    End Port
            ----------    --------
                 5357        5357
                50000       50059     *
                56755       56854
            """);

        Assert.Equal(new TcpPortRange(49_152, 65_535), dynamicRange);
        Assert.Equal(
            [
                new TcpPortRange(5_357, 5_357),
                new TcpPortRange(50_000, 50_059),
                new TcpPortRange(56_755, 56_854),
            ],
            excludedRanges);
    }

    [Fact]
    public void WindowsPortState_IsBlocked_IgnoresUnrelatedRangeChanges()
    {
        const int selectedPort = 36_696;
        var allocationState = new WindowsTcpPortState(
            [new TcpPortRange(49_152, 65_535)],
            [new TcpPortRange(5_357, 5_357)]);
        var laterState = new WindowsTcpPortState(
            [new TcpPortRange(49_152, 65_535)],
            [
                new TcpPortRange(5_357, 5_357),
                new TcpPortRange(40_683, 40_683),
            ]);

        Assert.False(allocationState.IsBlocked(selectedPort));
        Assert.False(laterState.IsBlocked(selectedPort));
        Assert.True(laterState.IsBlocked(40_683));
    }

    [Fact]
    public void ParseExcludedRanges_AcceptsRecognizedEmptyTable()
    {
        var ranges = WindowsTcpPortState.ParseExcludedRanges(
            """
            Protocol tcp Port Exclusion Ranges

            Start Port    End Port
            ----------    --------

            * - Administered port exclusions.
            """);

        Assert.Empty(ranges);
    }

    [Fact]
    public void ParseExcludedRanges_AcceptsLocalizedHeader()
    {
        var ranges = WindowsTcpPortState.ParseExcludedRanges(
            """
            Rangos de exclusión de puertos para tcp

            Puerto inicial    Puerto final
            --------------    ------------
                     5357             5357
                    50000            50059     *
            """);

        Assert.Equal(
            [
                new TcpPortRange(5_357, 5_357),
                new TcpPortRange(50_000, 50_059),
            ],
            ranges);
    }

    [Theory]
    [InlineData(
        """
        Protocol tcp Port Exclusion Ranges
        No port table was returned.
        """)]
    [InlineData(
        """
        Protocol tcp Port Exclusion Ranges

        Start Port    End Port
        ----------    --------
        unavailable
        """)]
    public void ParseExcludedRanges_RejectsUnrecognizedTableShape(string output)
    {
        Assert.Throws<InvalidDataException>(() =>
            WindowsTcpPortState.ParseExcludedRanges(output));
    }

    [Fact]
    public async Task RunProcessAsync_TimesOutAndIncludesCommandContext()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("ping -n 30 127.0.0.1 >nul");

        var stopwatch = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            WindowsTcpPortState.RunProcessAsync(
                startInfo,
                TimeSpan.FromMilliseconds(100),
                "timeout-test-command"));

        Assert.Contains("timeout-test-command", exception.Message, StringComparison.Ordinal);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Timed process runner did not return promptly: {stopwatch.Elapsed}.");
    }

    [Fact]
    public void Acquire_SkipsBlockedBusyAndAlreadyLeasedCandidates()
    {
        var leaseDirectory = Path.Combine(
            Path.GetTempPath(),
            $"openclaw-e2e-port-lease-test-{Guid.NewGuid():N}");
        var start = MirroredWslPortLease.CandidateRangeStart;
        var windowsPortState = new WindowsTcpPortState(
            [new TcpPortRange(start, start)],
            [new TcpPortRange(start + 1, start + 1)]);

        try
        {
            using var first = MirroredWslPortLease.Acquire(
                [start, start + 1, start + 2, start + 3],
                windowsPortState,
                port => port != start + 2,
                leaseDirectory);
            Assert.Equal(start + 3, first.Port);

            using var second = MirroredWslPortLease.Acquire(
                [start + 3, start + 4],
                new WindowsTcpPortState([], []),
                _ => true,
                leaseDirectory);
            Assert.Equal(start + 4, second.Port);

            first.Dispose();
            using var afterRelease = MirroredWslPortLease.Acquire(
                [start + 3],
                new WindowsTcpPortState([], []),
                _ => true,
                leaseDirectory);
            Assert.Equal(start + 3, afterRelease.Port);
        }
        finally
        {
            if (Directory.Exists(leaseDirectory))
                Directory.Delete(leaseDirectory, recursive: true);
        }
    }
}
