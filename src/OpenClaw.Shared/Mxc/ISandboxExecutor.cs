using System.Text.Json;

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// Abstraction for executing a capability invocation inside containment.
/// Mirrors the <see cref="ICommandRunner"/> pattern that already exists for
/// system.run, broadened to cover any capability shape.
/// </summary>
/// <remarks>
/// Implementations:
/// <list type="bullet">
/// <item><see cref="OneShotAppContainerExecutor"/> — per-call AppContainer via Node + mxc-sdk.</item>
/// <item><c>HostFallbackExecutor</c> — when containment unavailable in BestEffort mode.</item>
/// </list>
/// All implementations are expected to throw <see cref="SandboxUnavailableException"/>
/// when they cannot serve the request because of a missing backend (e.g. unsupported
/// Windows build, missing wxc-exec.exe). Callers in fail-closed mode translate that
/// into a denied invocation; callers in best-effort mode swap to a host runner.
/// </remarks>
public interface ISandboxExecutor
{
    /// <summary>Stable identifier for telemetry and the activity-stream badge.</summary>
    /// <example>"mxc-oneshot-appc", "mxc-isosession-worker", "host-fallback"</example>
    string Name { get; }

    /// <summary>True if this executor enforces containment. False = host fallback path.</summary>
    bool IsContained { get; }

    /// <summary>Execute the request inside containment.</summary>
    /// <exception cref="SandboxUnavailableException">
    /// Thrown when the executor's backend cannot serve this request.
    /// </exception>
    Task<SandboxExecutionResult> ExecuteAsync(
        SandboxExecutionRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Capability invocation routed through an <see cref="ISandboxExecutor"/>.
/// Generic across capability shapes (shell exec, structured-data fetch, etc.).
/// </summary>
/// <param name="TimeoutMs">
/// Effective timeout in ms. Already capped by user-settings policy if applicable.
/// Pass &lt;= 0 to let the executor use its default.
/// </param>
/// <param name="MaxOutputBytes">
/// Maximum stdout/stderr the executor will return. Pass <c>null</c> to use the
/// executor's default (typically 4 MiB). The host capture cap and the bridge
/// cap (run-command.cjs) honor this value.
/// </param>
public sealed record SandboxExecutionRequest(
    string CapabilityCommand,
    JsonElement Args,
    SandboxPolicy Policy,
    int TimeoutMs,
    string? Cwd = null,
    IReadOnlyDictionary<string, string>? Env = null,
    long? MaxOutputBytes = null);

/// <summary>
/// Result of a sandboxed capability invocation. Mirrors <see cref="CommandResult"/>
/// for shell-shaped invocations, and adds <see cref="StructuredResult"/> for
/// capability-shaped invocations whose output is JSON.
/// </summary>
public sealed record SandboxExecutionResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    long DurationMs,
    string ContainmentTag,
    JsonElement? StructuredResult = null);

/// <summary>
/// Thrown by an <see cref="ISandboxExecutor"/> when its backend cannot serve a
/// request (e.g. unsupported Windows build, missing wxc-exec.exe, OS feature off).
/// Caller policy decides whether to fail-closed or fall back.
/// </summary>
public sealed class SandboxUnavailableException : Exception
{
    public SandboxUnavailableException(string reason) : base(reason) { }
    public SandboxUnavailableException(string reason, Exception inner) : base(reason, inner) { }
}
