using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared;

/// <summary>
/// Executes commands locally via Process.Start (pwsh.exe / cmd.exe).
/// This is the default runner. Swap with DockerCommandRunner, WslCommandRunner, etc.
/// </summary>
public class LocalCommandRunner : ICommandRunner
{
    private readonly IOpenClawLogger _logger;
    
    private const int OutputDrainTimeoutMs = 500;
    
    public string Name => "local";
    
    public LocalCommandRunner(IOpenClawLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }
    
    public async Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default)
    {
        var (fileName, arguments) = BuildProcessArgs(request);
        
        _logger.Info($"[EXEC] {fileName} {arguments}");
        
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        
        if (!string.IsNullOrEmpty(request.Cwd))
        {
            psi.WorkingDirectory = request.Cwd;
        }
        
        if (request.Env != null)
        {
            foreach (var (key, value) in request.Env)
            {
                psi.Environment[key] = value;
            }
        }
        
        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var outputLock = new object();
        
        process.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (outputLock) { stdoutBuilder.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (outputLock) { stderrBuilder.AppendLine(e.Data); } } };
        
        // Use the Exited event rather than WaitForExitAsync to detect process exit.
        // WaitForExitAsync (.NET 6+) internally calls WaitForExit() which blocks until
        // async stream reads reach EOF. When CLI tools communicate via local IPC (e.g.
        // Obsidian.com, docker), child processes may inherit the stdout pipe write handle,
        // preventing EOF and causing WaitForExitAsync to hang indefinitely.
        var exitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => exitTcs.TrySetResult(true);
        
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _logger.Error($"[EXEC] Failed to start process: {ex.Message}");
            return new CommandResult
            {
                Stderr = $"Failed to start: {ex.Message}",
                ExitCode = -1,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        
        // Handle the race where the process exits before or during Start()
        if (process.HasExited)
            exitTcs.TrySetResult(true);
        
        var timedOut = false;
        
        try
        {
            if (request.TimeoutMs > 0)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(request.TimeoutMs);
                
                try
                {
                    await exitTcs.Task.WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    timedOut = true;
                    _logger.Warn($"[EXEC] Process timed out after {request.TimeoutMs}ms");
                    KillProcess(process);
                }
            }
            else
            {
                await exitTcs.Task.WaitAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }
        
        // Drain remaining buffered output. After the process exits its data is already in
        // the pipe buffer; the async reader delivers it nearly instantly. We run WaitForExit()
        // on a background thread with a 500 ms deadline so we don't block forever if orphaned
        // child processes have inherited the pipe write handle and are still running.
        var drainTask = Task.Run(() =>
        {
            try { process.WaitForExit(); }
            catch { /* process may already be gone */ }
        });
        if (await Task.WhenAny(drainTask, Task.Delay(OutputDrainTimeoutMs, CancellationToken.None)) != drainTask)
        {
            _logger.Warn("[EXEC] Output drain timed out; child processes may hold the pipe open");
        }
        
        sw.Stop();
        
        string stdout, stderr;
        lock (outputLock)
        {
            stdout = stdoutBuilder.ToString().TrimEnd();
            stderr = stderrBuilder.ToString().TrimEnd();
        }
        
        var result = new CommandResult
        {
            Stdout = stdout,
            Stderr = stderr,
            ExitCode = timedOut ? -1 : process.ExitCode,
            TimedOut = timedOut,
            DurationMs = sw.ElapsedMilliseconds
        };
        
        _logger.Info($"[EXEC] Exit={result.ExitCode} Duration={result.DurationMs}ms TimedOut={timedOut} Stdout={result.Stdout.Length}chars Stderr={result.Stderr.Length}chars");
        
        return result;
    }
    
    private static (string fileName, string arguments) BuildProcessArgs(CommandRequest request)
    {
        var shell = request.Shell ?? "powershell";
        var command = request.Command;
        var isCmd = shell.Equals("cmd", StringComparison.OrdinalIgnoreCase);
        
        if (request.Args is { Length: > 0 })
        {
            var quoted = new string[request.Args.Length];
            for (var i = 0; i < request.Args.Length; i++)
                quoted[i] = ShellQuoting.QuoteForShell(request.Args[i], isCmd);
            command = command + " " + string.Join(" ", quoted);
        }
        
        if (isCmd)
            return ("cmd.exe", $"/C {command}");
        if (shell.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
            return ("pwsh.exe", $"-NoProfile -NonInteractive -Command {command}");
        return ("powershell.exe", $"-NoProfile -NonInteractive -Command {command}");
    }
    
    private void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"[EXEC] Failed to kill process: {ex.Message}");
        }
    }
}
