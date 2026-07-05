using System.Diagnostics;
using System.Text;

namespace OpenClaw.SetupEngine;

// ─── Command Runner ───

public sealed record CommandResult(int ExitCode, string Stdout, string Stderr, TimeSpan Elapsed, bool TimedOut);

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(
        string executable,
        string[] arguments,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null,
        string? stdinInput = null,
        CancellationToken ct = default);

    Task<CommandResult> RunInWslAsync(
        string distroName,
        string command,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default,
        string? user = null);
}

public sealed class CommandRunner : ICommandRunner
{
    private readonly SetupLogger _logger;
    private const int DrainTimeoutMs = 5000; // bounded drain for orphan WSL processes
    private const int MaxCapturedStreamChars = 1_048_576;

    public CommandRunner(SetupLogger logger) => _logger = logger;

    /// <summary>
    /// Run a process and capture output. Timeout kills the process.
    /// </summary>
    public async Task<CommandResult> RunAsync(
        string executable,
        string[] arguments,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null,
        string? stdinInput = null,
        CancellationToken ct = default)
    {
        _logger.CommandStarted(executable, arguments, timeout);
        var sw = Stopwatch.StartNew();

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinInput != null,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? ""
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        if (environment != null)
        {
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new BoundedOutputBuffer(MaxCapturedStreamChars);
        var stderr = new BoundedOutputBuffer(MaxCapturedStreamChars);
        var timedOut = false;

        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            sw.Stop();
            var errorResult = new CommandResult(-1, "", $"Failed to start process '{executable}': {ex.Message}", sw.Elapsed, false);
            _logger.CommandCompleted(executable, errorResult, sw.Elapsed);
            return errorResult;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            if (stdinInput != null)
            {
                try
                {
                    await process.StandardInput.WriteAsync(stdinInput.AsMemory(), timeoutCts.Token);
                    await process.StandardInput.FlushAsync(timeoutCts.Token);
                    process.StandardInput.Close();
                }
                catch (IOException) when (process.HasExited)
                {
                    // The child exited before consuming all input. Preserve its exit status/output.
                }
            }

            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout (not user cancellation)
            timedOut = true;
            TryKill(process);
        }
        catch (OperationCanceledException)
        {
            // User cancelled
            TryKill(process);
            throw;
        }

        // Flush async output handlers. WaitForExitAsync observes process exit, but the
        // OutputDataReceived/ErrorDataReceived callbacks can still be draining.
        if (!timedOut)
            process.WaitForExit(DrainTimeoutMs);

        sw.Stop();
        var result = new CommandResult(
            timedOut ? -1 : process.ExitCode,
            stdout.ToString(),
            stderr.ToString(),
            sw.Elapsed,
            timedOut);

        _logger.CommandCompleted(executable, result, sw.Elapsed);
        return result;
    }

    /// <summary>
    /// Run a command inside a WSL distro.
    /// </summary>
    public Task<CommandResult> RunInWslAsync(
        string distroName,
        string command,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken ct = default,
        string? user = null)
    {
        // Strip Windows \r to avoid bash "$'\r': command not found" errors
        command = command.Replace("\r", "");

        // Build wsl.exe arguments: -d <distro> [-u <user>] -- <shell command>
        var args = new List<string> { "-d", distroName };
        if (!string.IsNullOrWhiteSpace(user))
        {
            args.Add("-u");
            args.Add(user);
        }

        args.AddRange(["--", "bash", "-c", command]);

        // Pass WSL environment variables via WSLENV
        Dictionary<string, string>? env = null;
        if (environment is { Count: > 0 })
        {
            env = new Dictionary<string, string>(environment);
            var wslEnvKeys = string.Join(":", environment.Keys);
            env["WSLENV"] = env.TryGetValue("WSLENV", out var existing)
                ? $"{existing}:{wslEnvKeys}"
                : wslEnvKeys;
        }

        return RunAsync("wsl.exe", args.ToArray(), timeout, env, ct: ct);
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex)
        {
            // Best effort — process may already be exiting; no logger in this static helper.
            Trace.WriteLine($"CommandRunner.TryKill: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private sealed class BoundedOutputBuffer(int maxChars)
    {
        private readonly StringBuilder _builder = new();
        private readonly object _lock = new();
        private int _droppedChars;

        public void AppendLine(string line)
        {
            lock (_lock)
            {
                if (_builder.Length < maxChars)
                {
                    var remaining = maxChars - _builder.Length;
                    if (line.Length + Environment.NewLine.Length <= remaining)
                    {
                        _builder.AppendLine(line);
                        return;
                    }

                    if (remaining > 0)
                        _builder.Append(line[..Math.Min(line.Length, remaining)]);
                }

                _droppedChars += line.Length + Environment.NewLine.Length;
            }
        }

        public override string ToString()
        {
            lock (_lock)
            {
                if (_droppedChars == 0)
                    return _builder.ToString();

                return _builder.ToString() + Environment.NewLine + $"... [truncated {_droppedChars} chars]";
            }
        }
    }
}
