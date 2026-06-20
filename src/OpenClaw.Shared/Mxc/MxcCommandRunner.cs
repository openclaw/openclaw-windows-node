using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared.Mxc;

/// <summary>
/// Adapts the existing <see cref="ICommandRunner"/> seam so production
/// <c>system.run</c> invocations get sandboxed via MXC AppContainer.
/// Plugs into <c>SystemCapability.SetCommandRunner(...)</c> exactly where
/// <c>LocalCommandRunner</c> plugs in today.
/// </summary>
/// <remarks>
/// Honors <see cref="SettingsData.SystemRunSandboxEnabled"/>:
/// <list type="bullet">
/// <item><c>true</c> (default) — sandbox via MXC when available; fall back uncontained when MXC is unavailable.</item>
/// <item><c>true</c> with <see cref="SettingsData.SystemRunBlockHostFallbackWhenMxcUnavailable"/> set to <c>true</c> — deny when MXC is unavailable.</item>
/// <item><c>false</c> — bypass MXC; route through the host runner.</item>
/// </list>
/// </remarks>
public sealed class MxcCommandRunner : ICommandRunner
{
    public string Name => "mxc";
    private const string DefaultSandboxShell = "cmd";

    private readonly ISandboxExecutor _executor;
    private readonly ICommandRunner _hostFallback;
    private readonly Func<SettingsData> _settingsProvider;
    private readonly Func<string> _settingsDirectoryPathProvider;
    private readonly Func<bool> _isSandboxAvailable;
    private readonly Action? _invalidateAvailability;
    private readonly IOpenClawLogger _logger;

    public MxcCommandRunner(
        ISandboxExecutor executor,
        ICommandRunner hostFallback,
        Func<SettingsData> settingsProvider,
        Func<string> settingsDirectoryPathProvider,
        Func<bool> isSandboxAvailable,
        Action? invalidateAvailability = null,
        IOpenClawLogger? logger = null)
    {
        _executor = executor;
        _hostFallback = hostFallback;
        _settingsProvider = settingsProvider;
        _settingsDirectoryPathProvider = settingsDirectoryPathProvider;
        _isSandboxAvailable = isSandboxAvailable;
        _invalidateAvailability = invalidateAvailability;
        _logger = logger ?? NullLogger.Instance;
    }

    public string ResolveEffectiveShell(string? requestedShell)
    {
        var settings = _settingsProvider();
        if (!settings.SystemRunSandboxEnabled)
            return _hostFallback.ResolveEffectiveShell(requestedShell);

        if (!_isSandboxAvailable() && !settings.SystemRunBlockHostFallbackWhenMxcUnavailable)
            return _hostFallback.ResolveEffectiveShell(requestedShell);

        if (!string.IsNullOrWhiteSpace(requestedShell))
            return ResolveSandboxShell(requestedShell);

        return DefaultSandboxShell;
    }

    public async Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default)
    {
        var settings = _settingsProvider();
        var effectiveShell = ResolveEffectiveShell(request.Shell);

        if (!settings.SystemRunSandboxEnabled)
        {
            _logger.Info("[mxc] sandbox=disabled; routing system.run through host runner");
            return await RunHostFallbackAsync(request, effectiveShell, ct);
        }

        // A direct-argv request reaching the sandbox cannot be honored: the sandbox
        // protocol only carries the legacy command/shell/args fields, so serializing
        // would silently run something other than the approved argv. Fail closed until
        // the sandbox transport carries argv faithfully. The host-fallback branches
        // above keep working because the host runner does honor Argv.
        if (request.Argv is not null)
        {
            _logger.Warn("[mxc] system.run BLOCKED: direct-argv request reached the sandbox, " +
                "which has no argv transport yet. Failing closed rather than running the legacy fields.");
            return new CommandResult
            {
                Stdout = string.Empty,
                Stderr = "Sandboxed system.run cannot execute a direct-argv command yet.",
                ExitCode = -1,
                TimedOut = false,
                DurationMs = 0,
            };
        }

        if (request.Env is { Count: > 0 })
        {
            const string message =
                "Sandboxed system.run does not currently support custom environment variables " +
                "with the Windows MXC 0.7 processcontainer backend. Remove env from the request " +
                "or explicitly disable sandboxing if uncontained host execution is acceptable.";
            _logger.Warn("[mxc] system.run denied: custom env is unsupported by MXC processcontainer");
            return new CommandResult
            {
                Stdout = string.Empty,
                Stderr = message,
                ExitCode = -1,
                TimedOut = false,
                DurationMs = 0,
            };
        }

        if (!_isSandboxAvailable())
        {
            if (settings.SystemRunBlockHostFallbackWhenMxcUnavailable)
                return DenySandboxUnavailable(
                    "Sandboxed system.run is enabled, but MXC is unavailable on this host and host fallback is blocked by settings. " +
                    "Update Windows or repair MXC, or disable strict fallback blocking if uncontained host execution is acceptable.",
                    "[mxc] system.run denied: sandbox unavailable and host fallback blocked by settings");

            // Compatibility default: keep pre-MXC host execution unless the
            // operator explicitly opts into strict sandbox-unavailable blocking.
            _logger.Warn(
                "[mxc] system.run UNCONTAINED: sandbox unavailable on this host; " +
                "routing through host runner for compatibility.");
            return await RunHostFallbackAsync(request, effectiveShell, ct);
        }

        if (request.Env is { Count: > 0 })
        {
            _invalidateAvailability?.Invoke();
            if (!_isSandboxAvailable())
            {
                if (settings.SystemRunBlockHostFallbackWhenMxcUnavailable)
                    return DenySandboxUnavailable(
                        "Sandboxed system.run is enabled, but MXC is unavailable on this host and host fallback is blocked by settings. " +
                        "Update Windows or repair MXC, or disable strict fallback blocking if uncontained host execution is acceptable.",
                        "[mxc] system.run denied: custom env requires host fallback, but sandbox is unavailable and host fallback is blocked by settings");

                _logger.Warn(
                    "[mxc] system.run UNCONTAINED: custom env is unsupported by MXC processcontainer " +
                    "and MXC is unavailable after re-probe; routing through host runner for compatibility.");
                var hostShell = ResolveHostFallbackShell(request.Shell);
                if (FallbackWouldChangeApprovedShell(request, effectiveShell, hostShell))
                    return DenyFallbackShellMismatch(effectiveShell, hostShell);

                return await RunHostFallbackAsync(request, hostShell, ct);
            }

            const string message =
                "Sandboxed system.run does not currently support custom environment variables " +
                "with the Windows MXC 0.7 processcontainer backend. Remove env from the request " +
                "or explicitly disable sandboxing if uncontained host execution is acceptable.";
            _logger.Warn("[mxc] system.run denied: custom env is unsupported by MXC processcontainer");
            return new CommandResult
            {
                Stdout = string.Empty,
                Stderr = message,
                ExitCode = -1,
                TimedOut = false,
                DurationMs = 0,
            };
        }

        var settingsDirectoryPath = _settingsDirectoryPathProvider();
        var policy = MxcPolicyBuilder.ForSystemRun(settings, settingsDirectoryPath);
        var argsJson = SerializeArgs(request, effectiveShell);

        // Compute the effective timeout: take the smaller of the agent-supplied
        // timeout (request.TimeoutMs) and the user's sandbox cap (policy.TimeoutMs).
        // A zero/null on either side means "no cap from that side".
        var effectiveTimeoutMs = CombineTimeouts(request.TimeoutMs, policy.TimeoutMs);

        var sandboxRequest = new SandboxExecutionRequest(
            CapabilityCommand: "system.run",
            Args: argsJson,
            Policy: policy,
            TimeoutMs: effectiveTimeoutMs,
            Cwd: request.Cwd,
            Env: request.Env,
            MaxOutputBytes: settings.SandboxMaxOutputBytes > 0
                ? settings.SandboxMaxOutputBytes
                : null);

        try
        {
            LogSandboxRequest(sandboxRequest, request, effectiveShell, settings, settingsDirectoryPath, policy);
            var sandboxed = await _executor.ExecuteAsync(sandboxRequest, ct);
            LogSandboxResult(sandboxed);
            return new CommandResult
            {
                Stdout = sandboxed.Stdout,
                Stderr = sandboxed.Stderr,
                ExitCode = sandboxed.ExitCode,
                TimedOut = sandboxed.TimedOut,
                DurationMs = sandboxed.DurationMs,
            };
        }
        catch (SandboxUnavailableException ex)
        {
            // Invalidate any cached availability — what we thought was available
            // turned out not to be at runtime. Next command re-probes and the
            // top-level !_isSandboxAvailable() branch will use the compatibility
            // fallback until MXC is available again.
            _invalidateAvailability?.Invoke();

            if (settings.SystemRunBlockHostFallbackWhenMxcUnavailable)
                return DenySandboxUnavailable(
                    "Sandboxed system.run is enabled, but MXC became unavailable at runtime and host fallback is blocked by settings: " +
                    $"{ex.Message}. Repair MXC or disable strict fallback blocking if uncontained host execution is acceptable.",
                    $"[mxc] system.run denied: sandbox became unavailable at runtime and host fallback is blocked by settings: {ex.Message}");

            _logger.Warn(
                $"[mxc] system.run UNCONTAINED: sandbox became unavailable at runtime ({ex.Message}); " +
                "routing through host runner for compatibility.");
            var hostShell = ResolveHostFallbackShell(request.Shell);
            if (FallbackWouldChangeApprovedShell(request, effectiveShell, hostShell))
                return DenyFallbackShellMismatch(effectiveShell, hostShell);

            return await RunHostFallbackAsync(request, hostShell, ct);
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled (gateway disconnect, agent abort). Propagate so the
            // caller sees the cancellation rather than a fake "exited 0" response.
            throw;
        }
        catch (NotSupportedException ex)
        {
            if (IsPowerShellUiUnsupported(ex))
            {
                if (settings.SystemRunBlockHostFallbackWhenMxcUnavailable)
                    return DenySandboxUnavailable(
                        "Sandboxed system.run cannot execute PowerShell-family shells with the current MXC UI-deny policy, " +
                        "and host fallback is blocked by settings. Retry with shell='cmd', disable strict fallback blocking, " +
                        "or explicitly disable sandboxing if uncontained host execution is acceptable.",
                        $"[mxc] system.run denied: PowerShell-family shell unsupported by MXC UI-deny policy and host fallback is blocked by settings: {ex.Message}");

                _logger.Warn(
                    $"[mxc] system.run UNCONTAINED: PowerShell-family shell unsupported by MXC UI-deny policy ({ex.Message}); " +
                    "routing through host runner for compatibility.");
                var hostShell = ResolveHostFallbackShell(request.Shell);
                if (FallbackWouldChangeApprovedShell(request, effectiveShell, hostShell))
                    return DenyFallbackShellMismatch(effectiveShell, hostShell);

                return await RunHostFallbackAsync(request, hostShell, ct);
            }

            _logger.Warn($"[mxc] system.run denied: unsupported sandbox request: {ex.Message}");
            return new CommandResult
            {
                Stdout = string.Empty,
                Stderr = ex.Message,
                ExitCode = -1,
                TimedOut = false,
                DurationMs = 0,
            };
        }
        catch (Exception ex)
        {
            // Fail closed for ANY other error (bridge crashed, JSON malformed, IO
            // failure on stdin). Returning a -1 CommandResult is what the agent
            // pipeline understands — letting the exception escape here can crash
            // the node loop and ultimately the tray.
            _logger.Warn($"[mxc] system.run sandbox execution failed: {ex.GetType().Name}: {ex.Message}");
            return new CommandResult
            {
                Stdout = string.Empty,
                Stderr =
                    "Sandboxed system.run failed with an unexpected error: " +
                    $"{ex.GetType().Name}: {ex.Message}",
                ExitCode = -1,
                TimedOut = false,
                DurationMs = 0,
            };
        }
    }

    private CommandResult DenySandboxUnavailable(string stderr, string logMessage)
    {
        _logger.Warn(logMessage);
        return new CommandResult
        {
            Stdout = string.Empty,
            Stderr = stderr,
            ExitCode = -1,
            TimedOut = false,
            DurationMs = 0,
        };
    }

    private Task<CommandResult> RunHostFallbackAsync(CommandRequest request, string effectiveShell, CancellationToken ct)
    {
        var fallbackRequest = new CommandRequest
        {
            Command = request.Command,
            Args = request.Args,
            Shell = effectiveShell,
            Cwd = request.Cwd,
            TimeoutMs = request.TimeoutMs,
            Env = request.Env,
        };
        return _hostFallback.RunAsync(fallbackRequest, ct);
    }

    private static string ResolveSandboxShell(string requestedShell) =>
        requestedShell.Trim().ToLowerInvariant() switch
        {
            "cmd" => "cmd",
            "pwsh" => "pwsh",
            "powershell" => "powershell",
            _ => "powershell",
        };

    private string ResolveHostFallbackShell(string? requestedShell) =>
        _hostFallback.ResolveEffectiveShell(requestedShell);

    private static bool IsPowerShellUiUnsupported(NotSupportedException ex) =>
        ex.Message.Contains("PowerShell-family shells require UI access", StringComparison.OrdinalIgnoreCase);

    private static bool FallbackWouldChangeApprovedShell(
        CommandRequest request,
        string approvedShell,
        string hostFallbackShell) =>
        string.IsNullOrWhiteSpace(request.Shell)
        && !string.Equals(approvedShell, hostFallbackShell, StringComparison.OrdinalIgnoreCase);

    private CommandResult DenyFallbackShellMismatch(string approvedShell, string hostFallbackShell)
    {
        var message =
            "Sandboxed system.run could not safely fall back to host execution because the " +
            $"pre-approved shell was '{approvedShell}' but host fallback would execute with " +
            $"'{hostFallbackShell}'. Retry with an explicit shell or after MXC availability " +
            "has been re-probed.";
        _logger.Warn("[mxc] system.run denied: host fallback shell would differ from approved shell");
        return new CommandResult
        {
            Stdout = string.Empty,
            Stderr = message,
            ExitCode = -1,
            TimedOut = false,
            DurationMs = 0,
        };
    }

    private static JsonElement SerializeArgs(CommandRequest request, string effectiveShell)
    {
        var payload = new
        {
            command = request.Command,
            shell = effectiveShell,
            args = request.Args ?? Array.Empty<string>(),
            cwd = request.Cwd,
            env = request.Env,
            timeoutMs = request.TimeoutMs,
        };
        var json = JsonSerializer.Serialize(payload);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private void LogSandboxRequest(
        SandboxExecutionRequest sandboxRequest,
        CommandRequest commandRequest,
        string effectiveShell,
        SettingsData settings,
        string settingsDirectoryPath,
        SandboxPolicy policy)
    {
        var envKeys = commandRequest.Env?.Keys
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();
        var message =
            "[mxc] system.run sandbox request " +
            $"executor={_executor.Name}; contained={_executor.IsContained}; " +
            $"sandboxSettings={{enabled={settings.SystemRunSandboxEnabled},blockHostFallbackWhenMxcUnavailable={settings.SystemRunBlockHostFallbackWhenMxcUnavailable}," +
            $"allowOutbound={settings.SystemRunAllowOutbound},clipboard={settings.SandboxClipboard},documents={settings.SandboxDocumentsAccess?.ToString() ?? "<null>"}," +
            $"downloads={settings.SandboxDownloadsAccess?.ToString() ?? "<null>"},desktop={settings.SandboxDesktopAccess?.ToString() ?? "<null>"}," +
            $"customFolderCount={settings.SandboxCustomFolders?.Count ?? 0},timeoutMs={settings.SandboxTimeoutMs},maxOutputBytes={settings.SandboxMaxOutputBytes}," +
            $"settingsDirectoryPath={(string.IsNullOrWhiteSpace(settingsDirectoryPath) ? "<null>" : "<set>")}}}; " +
            $"shell={effectiveShell}; requestedShell={(string.IsNullOrWhiteSpace(commandRequest.Shell) ? "<auto>" : "<set>")}; " +
            $"commandLength={commandRequest.Command?.Length ?? 0}; " +
            $"cwd={(string.IsNullOrEmpty(commandRequest.Cwd) ? "<null>" : "<set>")}; " +
            $"envKeys=[{string.Join(",", envKeys)}]; " +
            $"timeoutMs={sandboxRequest.TimeoutMs}; maxOutputBytes={sandboxRequest.MaxOutputBytes?.ToString() ?? "<default>"}; " +
            $"policy={{readonlyCount={policy.Filesystem?.ReadonlyPaths?.Count ?? 0},readwriteCount={policy.Filesystem?.ReadwritePaths?.Count ?? 0}," +
            $"deniedCount={policy.Filesystem?.DeniedPaths?.Count ?? 0},networkAllowOutbound={policy.Network?.AllowOutbound},uiAllowWindows={policy.Ui?.AllowWindows}," +
            $"clipboard={policy.Ui?.Clipboard},timeoutMs={policy.TimeoutMs?.ToString() ?? "<null>"}}}";
        LogMxcDiagnostic(message);

        if (string.Equals(Environment.GetEnvironmentVariable(DirectAppContainerExecutor.LogFullConfigEnvVar), "1", StringComparison.Ordinal))
        {
            var settingsJson = JsonSerializer.Serialize(ToSandboxSettingsDiagnostic(settings, settingsDirectoryPath), DiagnosticJson);
            var policyJson = JsonSerializer.Serialize(policy, DiagnosticJson);
            LogMxcDiagnostic(
                "[mxc] system.run sandbox request (full) " +
                $"sandboxSettingsJson={settingsJson}; policyJson={policyJson}");
        }
    }

    private static object ToSandboxSettingsDiagnostic(SettingsData settings, string settingsDirectoryPath)
    {
        return new
        {
            systemRunSandboxEnabled = settings.SystemRunSandboxEnabled,
            systemRunBlockHostFallbackWhenMxcUnavailable = settings.SystemRunBlockHostFallbackWhenMxcUnavailable,
            systemRunAllowOutbound = settings.SystemRunAllowOutbound,
            sandboxClipboard = settings.SandboxClipboard,
            sandboxDocumentsAccess = settings.SandboxDocumentsAccess,
            sandboxDownloadsAccess = settings.SandboxDownloadsAccess,
            sandboxDesktopAccess = settings.SandboxDesktopAccess,
            sandboxCustomFolders = settings.SandboxCustomFolders?.Select<SandboxCustomFolder, object>(f => new
            {
                path = f.Path,
                access = f.Access,
            }).ToArray() ?? Array.Empty<object>(),
            sandboxTimeoutMs = settings.SandboxTimeoutMs,
            sandboxMaxOutputBytes = settings.SandboxMaxOutputBytes,
            settingsDirectoryPath,
        };
    }

    private void LogSandboxResult(SandboxExecutionResult result)
    {
        LogMxcDiagnostic(
            "[mxc] system.run sandbox result " +
            $"exitCode={result.ExitCode}; timedOut={result.TimedOut}; durationMs={result.DurationMs}; " +
            $"containment={result.ContainmentTag}; stdoutChars={result.Stdout?.Length ?? 0}; " +
            $"stderrChars={result.Stderr?.Length ?? 0}; structured={result.StructuredResult.HasValue}");
    }

    private void LogMxcDiagnostic(string message)
    {
        _logger.Debug(message);
        Trace.WriteLine(message);
    }

    internal static int CombineTimeouts(int agentMs, int? policyMs)
    {
        // Treat <= 0 as "no cap on this side."
        var hasAgent = agentMs > 0;
        var hasPolicy = policyMs is > 0;
        if (hasAgent && hasPolicy) return Math.Min(agentMs, policyMs!.Value);
        if (hasAgent) return agentMs;
        if (hasPolicy) return policyMs!.Value;
        return 0;
    }

    private static readonly JsonSerializerOptions DiagnosticJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
        },
    };
}
