using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared;

/// <summary>
/// Executes commands locally via Process.Start (pwsh.exe / powershell.exe / cmd.exe).
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

    public string ResolveEffectiveShell(string? requestedShell) => ResolveEffectiveShellName(requestedShell);
    
    public async Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default)
    {
        ExecutionPlan plan;
        try
        {
            plan = PlanExecution(request);
        }
        catch (ArgumentException ex)
        {
            // Fail-closed: an invalid approved argv (empty, relative, or batch
            // executable) must not fall back to a shell or crash the host.
            _logger.Error($"[EXEC] Rejected command: {ex.Message}");
            return new CommandResult { Stderr = ex.Message, ExitCode = -1 };
        }

        var psi = new ProcessStartInfo
        {
            FileName = plan.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (plan.IsDirectArgv)
        {
            // The approved argv reaches the process verbatim via ArgumentList, with
            // no shell re-parsing it. Log the arg count, not the values - args may
            // carry secrets.
            foreach (var arg in plan.ArgList!)
                psi.ArgumentList.Add(arg);
            _logger.Info($"[EXEC] {plan.FileName} ({plan.ArgList!.Count} args, direct argv)");
        }
        else
        {
            psi.Arguments = plan.Arguments;
            _logger.Info($"[EXEC] {plan.FileName} {plan.Arguments}");
        }
        
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
            catch (Exception drainEx) { _logger.Debug($"LocalCommandRunner: WaitForExit during output drain threw: {drainEx.Message}"); }
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
    
    /// <summary>
    /// Decides how the process is launched: direct-argv (no shell) when the request
    /// carries an approved <see cref="CommandRequest.Argv"/>, otherwise the legacy
    /// shell-wrapped path. Kept separate from <see cref="RunAsync"/> so the decision
    /// is unit-testable without spawning a process.
    /// </summary>
    internal static ExecutionPlan PlanExecution(CommandRequest request)
    {
        // Argv set (non-null) means the caller approved a direct-argv execution and
        // it takes precedence (ICommandRunner contract). An empty argv is a malformed
        // approved payload, not a request to fall back to the shell - fail closed.
        if (request.Argv is { } argv)
        {
            if (argv.Count == 0)
                throw new ArgumentException("Direct-argv mode requires a non-empty argv.", nameof(request));
            ValidateDirectExecutable(argv[0]);
            return ExecutionPlan.Direct(argv);
        }

        var (fileName, arguments) = BuildProcessArgs(request);
        return ExecutionPlan.Shell(fileName, arguments);
    }

    /// <summary>
    /// Fail-closed guard for direct-argv mode. The approved executable must be a
    /// fully-qualified path so Windows never guesses argv[0] from PATH/cwd (e.g. a
    /// "C:\Program.exe" hijack), and must not be a batch script: .bat/.cmd cannot
    /// run without cmd.exe, which re-parses arguments and breaks the verbatim-argv
    /// guarantee. Both are bugs in the approval layer, not recoverable states -
    /// throw rather than degrade to a shell.
    /// </summary>
    private static void ValidateDirectExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new ArgumentException("Direct-argv executable (argv[0]) must not be empty.", nameof(executable));

        if (!Path.IsPathFullyQualified(executable))
            throw new ArgumentException(
                $"Direct-argv executable must be a fully-qualified path, got: {executable}", nameof(executable));

        var ext = Path.GetExtension(executable);
        if (ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Direct-argv mode cannot guarantee argv fidelity for batch scripts: {executable}", nameof(executable));
    }

    internal static (string fileName, string arguments) BuildProcessArgs(CommandRequest request, string? pathEnvVar = null)
    {
        var defaultShell = string.IsNullOrWhiteSpace(request.Shell);
        var shell = ResolveEffectiveShellName(request.Shell, pathEnvVar);
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
        {
            var pwshPath = ResolveOnPath("pwsh.exe", pathEnvVar);
            if (pwshPath is not null || !defaultShell)
                return (pwshPath ?? "pwsh.exe", $"-NoProfile -NonInteractive -Command {command}");
        }

        return (ResolveWindowsPowerShellExe(), $"-NoProfile -NonInteractive -Command {command}");
    }

    internal static string ResolveEffectiveShellName(string? requestedShell)
        => ResolveEffectiveShellName(requestedShell, pathEnvVar: null);

    private static string ResolveEffectiveShellName(string? requestedShell, string? pathEnvVar)
    {
        if (!string.IsNullOrWhiteSpace(requestedShell))
        {
            return requestedShell.Trim().ToLowerInvariant() switch
            {
                "cmd" => "cmd",
                "pwsh" => "pwsh",
                "powershell" => "powershell",
                _ => "powershell",
            };
        }

        return "powershell";
    }

    private static string? ResolveOnPath(string executableName, string? pathEnvVar = null)
    {
        var path = pathEnvVar
            ?? Environment.GetEnvironmentVariable("PATH")
            ?? Environment.GetEnvironmentVariable("Path");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, executableName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static string ResolveWindowsPowerShellExe()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")
            ?? Environment.GetEnvironmentVariable("windir");
        return string.IsNullOrWhiteSpace(systemRoot)
            ? "powershell.exe"
            : Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    }
    
    /// <summary>
    /// How <see cref="LocalCommandRunner"/> will launch the process. Either direct-argv
    /// (<see cref="ArgList"/> non-null, no shell) or shell-wrapped (<see cref="Arguments"/>).
    /// </summary>
    internal sealed class ExecutionPlan
    {
        public string FileName { get; }

        /// <summary>Argv[1..] passed verbatim via ArgumentList. Non-null = direct-argv mode.</summary>
        public IReadOnlyList<string>? ArgList { get; }

        /// <summary>Single command-line string for the shell-wrapped legacy path.</summary>
        public string? Arguments { get; }

        public bool IsDirectArgv => ArgList is not null;

        private ExecutionPlan(string fileName, IReadOnlyList<string>? argList, string? arguments)
        {
            FileName = fileName;
            ArgList = argList;
            Arguments = arguments;
        }

        public static ExecutionPlan Direct(IReadOnlyList<string> argv)
        {
            // argv preserved verbatim (no trimming).
            var rest = new string[argv.Count - 1];
            for (var i = 1; i < argv.Count; i++)
                rest[i - 1] = argv[i];
            return new ExecutionPlan(argv[0], rest, null);
        }

        public static ExecutionPlan Shell(string fileName, string arguments)
            => new(fileName, null, arguments);
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
