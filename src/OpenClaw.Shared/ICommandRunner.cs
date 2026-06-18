using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared;

/// <summary>
/// Request to execute a command. Passed to ICommandRunner implementations.
/// </summary>
public class CommandRequest
{
    /// <summary>The command to execute (e.g., "echo hello" or "Get-Process")</summary>
    public string Command { get; set; } = "";

    /// <summary>
    /// When set, execute this argv directly with no shell between policy and the
    /// process: FileName = Argv[0], the rest go through ProcessStartInfo.ArgumentList
    /// verbatim. Takes precedence over Command/Args/Shell, which are ignored.
    /// Null = legacy shell-wrapped path.
    /// </summary>
    public IReadOnlyList<string>? Argv { get; set; }

    /// <summary>Optional arguments array</summary>
    public string[]? Args { get; set; }
    
    /// <summary>Shell to use: "powershell", "pwsh", "cmd", or null for auto-detect</summary>
    public string? Shell { get; set; }
    
    /// <summary>Working directory</summary>
    public string? Cwd { get; set; }
    
    /// <summary>Timeout in milliseconds (0 = no timeout)</summary>
    public int TimeoutMs { get; set; }
    
    /// <summary>Additional environment variables</summary>
    public Dictionary<string, string>? Env { get; set; }
}

/// <summary>
/// Result of a command execution.
/// </summary>
public class CommandResult
{
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
    public int ExitCode { get; set; }
    public bool TimedOut { get; set; }
    public long DurationMs { get; set; }
}

/// <summary>
/// Abstraction for command execution. Implementations can be local (Process.Start),
/// sandboxed (Docker), WSL, or any other secure execution environment.
/// </summary>
public interface ICommandRunner
{
    /// <summary>Human-readable name of this runner (e.g., "local", "docker", "wsl")</summary>
    string Name { get; }

    /// <summary>
    /// Resolve the shell that will actually execute the request. Approval checks
    /// must use this value so shell-scoped rules cannot approve one shell while
    /// the runner executes another.
    /// </summary>
    string ResolveEffectiveShell(string? requestedShell)
    {
        return string.IsNullOrWhiteSpace(requestedShell)
            ? "powershell"
            : requestedShell.Trim();
    }
    
    /// <summary>Execute a command and return the result.</summary>
    Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default);
}
