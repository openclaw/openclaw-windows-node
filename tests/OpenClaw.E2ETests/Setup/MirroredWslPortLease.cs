using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace OpenClaw.E2ETests.Setup;

internal readonly record struct TcpPortRange(int Start, int End)
{
    public bool Contains(int port) => port >= Start && port <= End;
}

internal sealed record WindowsTcpPortState(
    IReadOnlyList<TcpPortRange> DynamicRanges,
    IReadOnlyList<TcpPortRange> ExcludedRanges)
{
    public static WindowsTcpPortState Capture()
    {
        var dynamicRanges = new List<TcpPortRange>();
        var excludedRanges = new List<TcpPortRange>();

        foreach (var family in new[] { "ipv4", "ipv6" })
        {
            dynamicRanges.Add(ParseDynamicRange(RunNetsh(
                "interface", family, "show", "dynamicport", "tcp")));
            excludedRanges.AddRange(ParseExcludedRanges(RunNetsh(
                "interface", family, "show", "excludedportrange", "protocol=tcp")));
        }

        return new WindowsTcpPortState(
            dynamicRanges.Distinct().OrderBy(range => range.Start).ToArray(),
            excludedRanges.Distinct().OrderBy(range => range.Start).ToArray());
    }

    internal static TcpPortRange ParseDynamicRange(string output)
    {
        var values = Regex.Matches(
                output,
                @"(?m)^\s*[^:\r\n]+:\s*(\d+)\s*$")
            .Select(match => int.Parse(match.Groups[1].Value))
            .ToArray();

        if (values.Length != 2 || values[1] <= 0)
            throw new InvalidDataException($"Could not parse Windows TCP dynamic port range:{Environment.NewLine}{output}");

        var end = checked(values[0] + values[1] - 1);
        if (values[0] is < 1 or > 65_535 || end > 65_535)
            throw new InvalidDataException($"Windows reported an invalid TCP dynamic port range: {values[0]}-{end}.");

        return new TcpPortRange(values[0], end);
    }

    internal static IReadOnlyList<TcpPortRange> ParseExcludedRanges(string output)
    {
        var ranges = new List<TcpPortRange>();
        foreach (Match match in Regex.Matches(
                     output,
                     @"(?m)^\s*(\d+)\s+(\d+)(?:\s+\*)?\s*$"))
        {
            var start = int.Parse(match.Groups[1].Value);
            var end = int.Parse(match.Groups[2].Value);
            if (start is < 1 or > 65_535 || end < start || end > 65_535)
                throw new InvalidDataException($"Windows reported an invalid excluded TCP port range: {start}-{end}.");
            ranges.Add(new TcpPortRange(start, end));
        }

        return ranges;
    }

    private static string RunNetsh(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start netsh.exe while inspecting Windows TCP port ranges.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"netsh.exe {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {stderr.Trim()}");
        }

        return stdout;
    }
}

internal sealed class MirroredWslPortLease : IDisposable
{
    public const int CandidateRangeStart = 20_000;
    public const int CandidateRangeEnd = 39_999;
    private const int CandidateCount = CandidateRangeEnd - CandidateRangeStart + 1;

    private FileStream? _leaseStream;

    private MirroredWslPortLease(
        int port,
        FileStream leaseStream,
        WindowsTcpPortState windowsPortState)
    {
        Port = port;
        _leaseStream = leaseStream;
        WindowsPortState = windowsPortState;
    }

    public int Port { get; }
    public WindowsTcpPortState WindowsPortState { get; }

    public static MirroredWslPortLease Acquire()
    {
        var windowsPortState = WindowsTcpPortState.Capture();
        var startOffset = RandomNumberGenerator.GetInt32(CandidateCount);
        var leaseDirectory = Path.Combine(Path.GetTempPath(), "OpenClaw.E2E.WslPortLeases");
        return Acquire(
            CreateCandidateSequence(startOffset),
            windowsPortState,
            CanBindExclusively,
            leaseDirectory);
    }

    internal static IEnumerable<int> CreateCandidateSequence(int startOffset)
    {
        if (startOffset is < 0 or >= CandidateCount)
            throw new ArgumentOutOfRangeException(nameof(startOffset));

        for (var offset = 0; offset < CandidateCount; offset++)
            yield return CandidateRangeStart + ((startOffset + offset) % CandidateCount);
    }

    internal static MirroredWslPortLease Acquire(
        IEnumerable<int> candidates,
        WindowsTcpPortState windowsPortState,
        Func<int, bool> canBind,
        string leaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(windowsPortState);
        ArgumentNullException.ThrowIfNull(canBind);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseDirectory);

        Directory.CreateDirectory(leaseDirectory);
        var blockedRanges = windowsPortState.DynamicRanges
            .Concat(windowsPortState.ExcludedRanges)
            .ToArray();

        foreach (var candidate in candidates)
        {
            if (candidate is < CandidateRangeStart or > CandidateRangeEnd)
                throw new ArgumentOutOfRangeException(nameof(candidates), candidate, "Candidate is outside the WSL-safe range.");
            if (blockedRanges.Any(range => range.Contains(candidate)))
                continue;

            FileStream? leaseStream;
            try
            {
                leaseStream = new FileStream(
                    Path.Combine(leaseDirectory, $"{candidate}.lock"),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException)
            {
                continue;
            }

            try
            {
                if (!canBind(candidate))
                {
                    leaseStream.Dispose();
                    continue;
                }

                return new MirroredWslPortLease(candidate, leaseStream, windowsPortState);
            }
            catch
            {
                leaseStream.Dispose();
                throw;
            }
        }

        throw new InvalidOperationException(
            $"Could not allocate a mirrored-WSL-safe Gateway port in {CandidateRangeStart}-{CandidateRangeEnd}. " +
            $"Windows reported {windowsPortState.DynamicRanges.Count} dynamic and " +
            $"{windowsPortState.ExcludedRanges.Count} excluded TCP ranges.");
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _leaseStream, null)?.Dispose();
    }

    private static bool CanBindExclusively(int port)
    {
        TcpListener? ipv4 = null;
        TcpListener? ipv6 = null;
        try
        {
            ipv4 = new TcpListener(IPAddress.Any, port);
            ipv4.Server.ExclusiveAddressUse = true;
            ipv4.Start();

            if (Socket.OSSupportsIPv6)
            {
                ipv6 = new TcpListener(IPAddress.IPv6Any, port);
                ipv6.Server.DualMode = false;
                ipv6.Server.ExclusiveAddressUse = true;
                ipv6.Start();
            }

            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            ipv6?.Stop();
            ipv4?.Stop();
        }
    }
}
