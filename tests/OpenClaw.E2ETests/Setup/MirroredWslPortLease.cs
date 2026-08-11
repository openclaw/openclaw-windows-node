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
    private static readonly TimeSpan NetshTimeout = TimeSpan.FromSeconds(20);

    public static WindowsTcpPortState Capture()
    {
        var dynamicRanges = new List<TcpPortRange>();
        var excludedRanges = new List<TcpPortRange>();

        CaptureFamily("ipv4", dynamicRanges, excludedRanges);
        if (Socket.OSSupportsIPv6)
            CaptureFamily("ipv6", dynamicRanges, excludedRanges);

        return new WindowsTcpPortState(
            dynamicRanges.Distinct().OrderBy(range => range.Start).ToArray(),
            excludedRanges.Distinct().OrderBy(range => range.Start).ToArray());
    }

    public bool IsBlocked(int port) =>
        DynamicRanges.Any(range => range.Contains(port)) ||
        ExcludedRanges.Any(range => range.Contains(port));

    private static void CaptureFamily(
        string family,
        List<TcpPortRange> dynamicRanges,
        List<TcpPortRange> excludedRanges)
    {
        dynamicRanges.Add(ParseDynamicRange(RunNetsh(
            "interface", family, "show", "dynamicport", "tcp")));
        excludedRanges.AddRange(ParseExcludedRanges(RunNetsh(
            "interface", family, "show", "excludedportrange", "protocol=tcp")));
    }

    internal static TcpPortRange ParseDynamicRange(string output)
    {
        var values = Regex.Matches(
                output,
                @"(?m)^\s*[^:\r\n]+:\s*(\d+)\s*$")
            .Select(match => int.Parse(match.Groups[1].Value))
            .ToArray();

        if (values.Length != 2 || values[1] <= 0)
        {
            throw new InvalidDataException(
                $"Could not parse Windows TCP dynamic port range:{Environment.NewLine}{output}");
        }

        var end = checked(values[0] + values[1] - 1);
        if (values[0] is < 1 or > 65_535 || end > 65_535)
            throw new InvalidDataException($"Windows reported an invalid TCP dynamic port range: {values[0]}-{end}.");

        return new TcpPortRange(values[0], end);
    }

    internal static IReadOnlyList<TcpPortRange> ParseExcludedRanges(string output)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var separatorIndices = lines
            .Select((line, index) => new { line, index })
            .Where(item => Regex.IsMatch(item.line, @"^\s*-+\s+-+\s*$"))
            .Select(item => item.index)
            .ToArray();
        if (separatorIndices.Length != 1)
        {
            throw new InvalidDataException(
                $"Expected exactly one Windows excluded TCP port table separator, found {separatorIndices.Length}:{Environment.NewLine}{output}");
        }

        var ranges = new List<TcpPortRange>();
        var separatorIndex = separatorIndices[0];
        for (var index = separatorIndex + 1; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0)
                continue;
            if (Regex.IsMatch(line, @"^\*\s*-"))
                break;

            var match = Regex.Match(line, @"^(\d+)\s+(\d+)(?:\s+\*)?$");
            if (!match.Success)
            {
                throw new InvalidDataException(
                    $"Unrecognized Windows excluded TCP port table row '{line}':{Environment.NewLine}{output}");
            }

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
        var systemDirectory = Environment.SystemDirectory;
        if (string.IsNullOrWhiteSpace(systemDirectory))
        {
            throw new InvalidOperationException(
                "Cannot resolve netsh.exe because Environment.SystemDirectory is unavailable.");
        }

        var netshPath = Path.Combine(systemDirectory, "netsh.exe");
        var commandContext = $"\"{netshPath}\" {string.Join(' ', arguments)}";
        var startInfo = new ProcessStartInfo
        {
            FileName = netshPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return RunProcessAsync(startInfo, NetshTimeout, commandContext).GetAwaiter().GetResult();
    }

    internal static async Task<string> RunProcessAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        string commandContext)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandContext);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start command: {commandContext}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = new CancellationTokenSource(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            var cleanupProblems = new List<string>();
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                    NotSupportedException or
                    System.ComponentModel.Win32Exception)
            {
                cleanupProblems.Add(
                    $"process-tree termination failed ({ex.GetType().Name}: {ex.Message})");
            }

            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(cleanupCts.Token);
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(cleanupCts.Token);
            }
            catch (OperationCanceledException) when (cleanupCts.IsCancellationRequested)
            {
                cleanupProblems.Add("process exit or stream drain did not complete within 5 seconds");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                cleanupProblems.Add(
                    $"process stream cleanup failed ({ex.GetType().Name}: {ex.Message})");
            }

            var cleanupContext = cleanupProblems.Count == 0
                ? ""
                : $" Cleanup: {string.Join("; ", cleanupProblems)}.";
            throw new TimeoutException(
                $"Command timed out after {timeout.TotalSeconds:0.###} seconds: {commandContext}.{cleanupContext}");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{commandContext} failed with exit code {process.ExitCode}: {stderr.Trim()}");
        }

        return stdout;
    }
}

internal sealed class MirroredWslPortLease : IDisposable
{
    public const int CandidateRangeStart = 20_000;
    public const int CandidateRangeEnd = 39_999;
    private const int CandidateCount = CandidateRangeEnd - CandidateRangeStart + 1;
    internal static IReadOnlyList<IPAddress> BindProbeAddresses { get; } =
        Socket.OSSupportsIPv6
            ? [IPAddress.Loopback, IPAddress.IPv6Loopback]
            : [IPAddress.Loopback];

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
            CanBindLoopbackExclusively,
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
        foreach (var candidate in candidates)
        {
            if (candidate is < CandidateRangeStart or > CandidateRangeEnd)
                throw new ArgumentOutOfRangeException(nameof(candidates), candidate, "Candidate is outside the WSL-safe range.");
            if (windowsPortState.IsBlocked(candidate))
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

    internal static bool CanBindLoopbackExclusively(int port)
    {
        var listeners = new List<TcpListener>(BindProbeAddresses.Count);
        try
        {
            foreach (var address in BindProbeAddresses)
            {
                var listener = new TcpListener(address, port);
                listeners.Add(listener);
                listener.Server.ExclusiveAddressUse = true;
                listener.Start();
            }

            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            foreach (var listener in listeners)
                listener.Stop();
        }
    }
}
