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
    /// <summary>
    /// When set, execute this argv directly with no shell between policy and the
    /// process: FileName = Argv[0], the rest go through ProcessStartInfo.ArgumentList
    /// verbatim. MXC renders the approved argv without an implicit shell.
    /// </summary>
    public IReadOnlyList<string>? Argv { get; set; }

    /// <summary>Working directory</summary>
    public string? Cwd { get; set; }
    
    /// <summary>Timeout in milliseconds (0 = no timeout)</summary>
    public int TimeoutMs { get; set; }
    
    /// <summary>Additional environment variables</summary>
    public Dictionary<string, string>? Env { get; set; }

    internal NodeToolInvocation? Telemetry { get; set; }
    internal ActivityContext TelemetryParentContext { get; set; }
    internal string? ExpectedMxcPolicy { get; set; }
    internal Func<CancellationToken, ValueTask<ExecApprovals.ExecApprovalRevalidationResult>>? RevalidateApproval { get; set; }
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
/// Approved command execution seam. Production Windows system.run uses MXC only.
/// </summary>
public interface ICommandRunner
{
    /// <summary>Human-readable name of this runner (e.g., "local", "docker", "wsl")</summary>
    string Name { get; }

    /// <summary>Execute a command and return the result.</summary>
    Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default);
}
