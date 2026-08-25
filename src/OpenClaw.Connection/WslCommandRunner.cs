using OpenClaw.Shared;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace OpenClaw.Connection;

public sealed record WslCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}

public sealed record WslDistroInfo(string Name, string State, int Version);

public interface IWslCommandRunner
{
    Task<WslCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environment = null);

    Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default);

    Task<WslCommandResult> TerminateDistroAsync(string name, CancellationToken cancellationToken = default);

    Task<WslCommandResult> UnregisterDistroAsync(string name, CancellationToken cancellationToken = default);

    Task<WslCommandResult> RunInDistroAsync(
        string name, IReadOnlyList<string> command,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environment = null);

    Task<WslCommandResult> RunInDistroWithStandardInputAsync(
        string name,
        IReadOnlyList<string> command,
        string standardInput,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environment = null) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<WslCommandResult>(cancellationToken)
            : Task.FromResult(new WslCommandResult(
                -1,
                string.Empty,
                "This WSL command runner does not support standard input."));
}

/// <summary>
/// Lightweight WSL command runner for probing distro state.
/// Does not include diagnostics tee — use SetupEngine for full setup/teardown.
/// </summary>
public sealed class WslExeCommandRunner : IWslCommandRunner
{
    private readonly IOpenClawLogger _logger;
    private readonly TimeSpan _defaultTimeout;

    public WslExeCommandRunner(
        IOpenClawLogger? logger = null,
        TimeSpan? defaultTimeout = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["--list", "--verbose"], cancellationToken);
        return result.Success ? ParseDistroList(result.StandardOutput) : [];
    }

    public Task<WslCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environment = null) =>
        RunProcessAsync("wsl.exe", arguments, cancellationToken, environment);

    public Task<WslCommandResult> RunInDistroAsync(
        string name, IReadOnlyList<string> command,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var args = new List<string> { "-d", name, "--" };
        args.AddRange(command);
        return RunAsync(args, cancellationToken, environment);
    }

    public Task<WslCommandResult> RunInDistroWithStandardInputAsync(
        string name,
        IReadOnlyList<string> command,
        string standardInput,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var args = new List<string> { "-d", name, "--" };
        args.AddRange(command);
        return RunProcessAsync(
            "wsl.exe",
            args,
            cancellationToken,
            environment,
            standardInput);
    }

    public Task<WslCommandResult> TerminateDistroAsync(string name, CancellationToken cancellationToken = default) =>
        RunAsync(["--terminate", name], cancellationToken);

    public Task<WslCommandResult> UnregisterDistroAsync(string name, CancellationToken cancellationToken = default) =>
        RunAsync(["--unregister", name], cancellationToken);

    public static IReadOnlyList<WslDistroInfo> ParseDistroList(string output)
    {
        var distros = new List<WslDistroInfo>();
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Replace("\0", string.Empty).Trim();
            if (line.Length == 0 || line.StartsWith("NAME", StringComparison.OrdinalIgnoreCase))
                continue;

            if (line[0] == '*')
                line = line[1..].TrimStart();

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                continue;

            if (!int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
                continue;

            var state = parts[^2];
            var name = string.Join(" ", parts.Take(parts.Length - 2));
            if (!string.IsNullOrWhiteSpace(name))
                distros.Add(new WslDistroInfo(name, state, version));
        }

        return distros;
    }

    private async Task<WslCommandResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment,
        string? standardInput = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        if (environment is not null)
        {
            foreach (var kvp in environment)
                psi.Environment[kvp.Key] = kvp.Value;
        }

        _logger.Info($"[WSL] {fileName} {string.Join(" ", arguments)}");

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new WslCommandResult(-1, string.Empty, $"Failed to start wsl.exe: {ex.Message}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_defaultTimeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        var stdinTask = standardInput is null
            ? Task.CompletedTask
            : WriteStandardInputAsync(process, standardInput, timeoutCts.Token);

        bool timedOut = false;
        OperationCanceledException? cancellationException = null;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            // slopwatch-ignore: SW003 Shutdown cancellation or disposal is expected and the caller already preserves the safe state.
            try { process.Kill(entireProcessTree: true); } catch { }
        }
        catch (OperationCanceledException ex)
        {
            // slopwatch-ignore: SW003 Shutdown cancellation or disposal is expected and the caller already preserves the safe state.
            try { process.Kill(entireProcessTree: true); } catch { }
            cancellationException = ex;
        }

        if (timedOut || cancellationException is not null)
        {
            using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false); } catch { }
        }

        Exception? stdinFailure = null;
        string stdout, stderr;
        try { await stdinTask; } catch (Exception ex) { stdinFailure = ex; }
        try { stdout = await stdoutTask; } catch { stdout = string.Empty; }
        try { stderr = await stderrTask; } catch { stderr = string.Empty; }

        cancellationToken.ThrowIfCancellationRequested();
        if (cancellationException is not null)
            throw cancellationException;
        if (!timedOut && stdinFailure is not null)
            return new WslCommandResult(-1, stdout, "wsl.exe failed to receive standard input");

        return timedOut
            ? new WslCommandResult(-1, stdout, "wsl.exe timed out")
            : new WslCommandResult(process.ExitCode, stdout, stderr);
    }

    private static async Task WriteStandardInputAsync(
        Process process,
        string standardInput,
        CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally
        {
            process.StandardInput.Close();
        }
    }
}
