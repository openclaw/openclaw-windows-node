using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared.Telemetry;

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

    /// <summary>
    /// Optional effective shell that already passed shell-scoped approval.
    /// Dynamic runners must execute this shell, or a separately approved host
    /// fallback shell, so live settings cannot change the approved boundary.
    /// </summary>
    public string? ApprovedEffectiveShell { get; set; }

    /// <summary>
    /// Optional host fallback shell that has already passed shell-scoped approval.
    /// Sandboxed runners use this only when a compatibility fallback would execute
    /// a different host shell than the sandbox effective shell.
    /// </summary>
    public string? ApprovedHostFallbackShell { get; set; }

    internal NodeToolInvocation? Telemetry { get; set; }
    internal ActivityContext TelemetryParentContext { get; set; }
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
    public NodeToolExecutionMode? ExecutionMode { get; set; }
    public NodeToolErrorCategory ErrorCategory { get; set; }
    public NodeToolSandboxDenialReason? SandboxDenialReason { get; set; }
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
        if (string.IsNullOrWhiteSpace(requestedShell))
            return "powershell";

        return requestedShell.Trim().ToLowerInvariant() switch
        {
            "cmd" => "cmd",
            "pwsh" => "pwsh",
            "powershell" => "powershell",
            _ => "powershell",
        };
    }
    
    /// <summary>Execute a command and return the result.</summary>
    Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default);
}

/// <summary>
/// Optional contract for runners that may preserve compatibility through an
/// uncontained host fallback with a shell different from their sandbox shell.
/// </summary>
public interface IHostFallbackAwareCommandRunner : ICommandRunner
{
    /// <summary>
    /// Returns the host fallback shell that needs separate approval, or null when
    /// fallback cannot change the already-approved effective shell.
    /// </summary>
    string? ResolveHostFallbackShellForApproval(string? requestedShell, string effectiveShell);
}
