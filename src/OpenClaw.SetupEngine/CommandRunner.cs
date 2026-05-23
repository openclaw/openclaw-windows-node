using System.Diagnostics;
using System.Text;

namespace OpenClaw.SetupEngine;

// ─── Command Runner ───

public sealed record CommandResult(int ExitCode, string Stdout, string Stderr, TimeSpan Elapsed, bool TimedOut);

public sealed class CommandRunner
{
    private readonly SetupLogger _logger;
    private const int DrainTimeoutMs = 5000; // bounded drain for orphan WSL processes

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
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
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

        if (stdinInput != null)
        {
            await process.StandardInput.WriteAsync(stdinInput);
            process.StandardInput.Close();
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
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

        // Bounded drain: wait briefly for async output handlers to flush
        // WSL child processes can hold pipes open after parent exits
        if (!timedOut)
        {
            var drainDeadline = Environment.TickCount64 + DrainTimeoutMs;
            while (!process.HasExited && Environment.TickCount64 < drainDeadline)
                await Task.Delay(100, CancellationToken.None);
        }

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
        CancellationToken ct = default)
    {
        // Strip Windows \r to avoid bash "$'\r': command not found" errors
        command = command.Replace("\r", "");

        // Build wsl.exe arguments: -d <distro> -- <shell command>
        var args = new[] { "-d", distroName, "--", "bash", "-c", command };

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

        return RunAsync("wsl.exe", args, timeout, env, ct: ct);
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }
}
