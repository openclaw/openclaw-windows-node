using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Shared.Telemetry;

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
/// <item><c>true</c> (default) - require MXC containment; deny if unavailable or lost at runtime.</item>
/// <item><c>false</c> — bypass MXC; route through the host runner.</item>
/// </list>
/// </remarks>
public sealed class MxcCommandRunner : IHostFallbackAwareCommandRunner, IDirectArgvSupportAwareCommandRunner
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

    public string ResolveEffectiveShell(string? requestedShell) =>
        ResolveEffectiveShell(requestedShell, _settingsProvider());

    private string ResolveEffectiveShell(string? requestedShell, SettingsData settings)
    {
        if (!settings.SystemRunSandboxEnabled)
            return _hostFallback.ResolveEffectiveShell(requestedShell);

        if (!string.IsNullOrWhiteSpace(requestedShell))
            return ResolveSandboxShell(requestedShell);

        return DefaultSandboxShell;
    }

    public string? ResolveHostFallbackShellForApproval(string? requestedShell, string effectiveShell) => null;

    /// <summary>
    /// Every active route preserves direct argv: host runners use ArgumentList,
    /// and MXC uses a CommandLineToArgvW-reversible process command line.
    /// </summary>
    public bool CanExecuteDirectArgv() => true;

    public async Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default)
    {
        var settings = _settingsProvider();
        var effectiveShell = request.Argv is null
            ? ResolveEffectiveShell(request.Shell, settings)
            : null;
        if (effectiveShell is not null
            && !TryValidateApprovedEffectiveShell(request, effectiveShell, out var approvalDeny))
            return approvalDeny!;

        if (!settings.SystemRunSandboxEnabled)
        {
            _logger.Info("[mxc] sandbox=disabled; routing system.run through host runner");
            return await RunHostFallbackAsync(request, effectiveShell, NodeToolExecutionMode.Host, ct);
        }

        // Custom env changes the execution boundary and is not enforced by MXC yet.
        if (request.Env is { Count: > 0 })
            return DenyCustomEnvUnsupported();

        if (!_executor.IsContained || !_isSandboxAvailable())
        {
            return DenySandboxUnavailable(
                "Node Sandbox is on, but containment is unavailable on this PC. The command was blocked. " +
                "Open Sandbox settings to check availability. Turn off Node Sandbox only if you accept running commands without containment.",
                "[mxc] system.run denied: sandbox unavailable; host fallback is blocked while Node Sandbox is on");
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
            var result = new CommandResult
            {
                Stdout = sandboxed.Stdout,
                Stderr = sandboxed.Stderr,
                ExitCode = sandboxed.ExitCode,
                TimedOut = sandboxed.TimedOut,
                DurationMs = sandboxed.DurationMs,
                ExecutionMode = NodeToolExecutionMode.Sandbox,
            };
            result.ErrorCategory = ClassifyProcessResult(result);
            return result;
        }
        catch (SandboxUnavailableException ex)
        {
            // Preserve the enabled preference and re-probe before a later command.
            _invalidateAvailability?.Invoke();

            return DenySandboxUnavailable(
                "Node Sandbox is on, but containment became unavailable. The command was blocked. " +
                "Open Sandbox settings to check availability. Turn off Node Sandbox only if you accept running commands without containment.",
                $"[mxc] system.run denied: sandbox became unavailable; host fallback is blocked while Node Sandbox is on: {ex.Message}");
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
                return DenySandboxUnavailable(
                    "Sandboxed system.run cannot execute PowerShell-family shells with the current MXC UI-deny policy. " +
                    "Enable 'Allow Windows UI APIs' in Node Sandbox settings, retry with shell='cmd', " +
                    "or disable sandboxing if uncontained host execution is acceptable.",
                    $"[mxc] system.run denied: PowerShell-family shell unsupported by MXC UI-deny policy: {ex.Message}");
            }

            _logger.Warn($"[mxc] system.run denied: unsupported sandbox request: {ex.Message}");
            return new CommandResult
            {
                Stdout = string.Empty,
                Stderr = ex.Message,
                ExitCode = -1,
                TimedOut = false,
                DurationMs = 0,
                ExecutionMode = NodeToolExecutionMode.Sandbox,
                ErrorCategory = NodeToolErrorCategory.SandboxDenied,
                SandboxDenialReason = NodeToolSandboxDenialReason.UnsupportedSandboxRequest,
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
                ExecutionMode = NodeToolExecutionMode.Sandbox,
                ErrorCategory = NodeToolErrorCategory.SandboxFailure,
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
            ExecutionMode = NodeToolExecutionMode.Sandbox,
            ErrorCategory = NodeToolErrorCategory.SandboxUnavailable,
        };
    }

    private async Task<CommandResult> RunHostFallbackAsync(
        CommandRequest request,
        string? effectiveShell,
        NodeToolExecutionMode executionMode,
        CancellationToken ct)
    {
        var fallbackRequest = new CommandRequest
        {
            Command = request.Command,
            Args = request.Args,
            Argv = request.Argv,
            Shell = effectiveShell,
            Cwd = request.Cwd,
            TimeoutMs = request.TimeoutMs,
            Env = request.Env,
            ApprovedEffectiveShell = request.ApprovedEffectiveShell,
            ApprovedHostFallbackShell = request.ApprovedHostFallbackShell,
            Telemetry = request.Telemetry,
            TelemetryParentContext = request.TelemetryParentContext,
        };
        var result = await _hostFallback.RunAsync(fallbackRequest, ct);
        result.ExecutionMode = executionMode;
        result.ErrorCategory = ClassifyProcessResult(result);
        return result;
    }

    private static string ResolveSandboxShell(string requestedShell) =>
        requestedShell.Trim().ToLowerInvariant() switch
        {
            "cmd" => "cmd",
            "pwsh" => "pwsh",
            "powershell" => "powershell",
            _ => "powershell",
        };

    private static bool IsPowerShellUiUnsupported(NotSupportedException ex) =>
        ex.Message.Contains("PowerShell-family shells require UI access", StringComparison.OrdinalIgnoreCase);

    private bool TryValidateApprovedEffectiveShell(
        CommandRequest request,
        string effectiveShell,
        out CommandResult? deny)
    {
        deny = null;
        if (string.IsNullOrWhiteSpace(request.ApprovedEffectiveShell)
            || string.Equals(request.ApprovedEffectiveShell, effectiveShell, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        deny = DenyEffectiveShellMismatch(request.ApprovedEffectiveShell!, effectiveShell);
        return false;
    }

    private CommandResult DenyEffectiveShellMismatch(string approvedShell, string effectiveShell)
    {
        var message =
            "Sandboxed system.run could not execute because the effective shell changed " +
            $"after approval. Approved shell was '{approvedShell}', but execution resolved " +
            $"'{effectiveShell}'. Retry so the command can be approved for the current shell.";
        _logger.Warn("[mxc] system.run denied: effective shell changed after approval");
        return new CommandResult
        {
            Stdout = string.Empty,
            Stderr = message,
            ExitCode = -1,
            TimedOut = false,
            DurationMs = 0,
            ExecutionMode = NodeToolExecutionMode.Sandbox,
            ErrorCategory = NodeToolErrorCategory.SandboxDenied,
            SandboxDenialReason = NodeToolSandboxDenialReason.EffectiveShellChanged,
        };
    }

    private CommandResult DenyCustomEnvUnsupported()
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
            ExecutionMode = NodeToolExecutionMode.Sandbox,
            ErrorCategory = NodeToolErrorCategory.SandboxDenied,
            SandboxDenialReason = NodeToolSandboxDenialReason.CustomEnvironmentUnsupported,
        };
    }

    private static NodeToolErrorCategory ClassifyProcessResult(CommandResult result)
    {
        if (result.ErrorCategory != NodeToolErrorCategory.None)
            return result.ErrorCategory;
        if (result.TimedOut)
            return NodeToolErrorCategory.Timeout;
        return result.ExitCode == 0
            ? NodeToolErrorCategory.None
            : NodeToolErrorCategory.CommandFailed;
    }

    private static JsonElement SerializeArgs(CommandRequest request, string? effectiveShell)
    {
        var payload = new
        {
            command = request.Command,
            shell = effectiveShell,
            args = request.Args ?? Array.Empty<string>(),
            argv = request.Argv,
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
        string? effectiveShell,
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
            $"sandboxSettings={{enabled={settings.SystemRunSandboxEnabled}," +
            $"allowOutbound={settings.SystemRunAllowOutbound},allowWindowsUi={settings.SystemRunAllowWindowsUi},clipboard={settings.SandboxClipboard},documents={settings.SandboxDocumentsAccess?.ToString() ?? "<null>"}," +
            $"downloads={settings.SandboxDownloadsAccess?.ToString() ?? "<null>"},desktop={settings.SandboxDesktopAccess?.ToString() ?? "<null>"}," +
            $"customFolderCount={settings.SandboxCustomFolders?.Count ?? 0},timeoutMs={settings.SandboxTimeoutMs},maxOutputBytes={settings.SandboxMaxOutputBytes}," +
            $"settingsDirectoryPath={(string.IsNullOrWhiteSpace(settingsDirectoryPath) ? "<null>" : "<set>")}}}; " +
            $"shell={effectiveShell ?? "<direct-argv>"}; requestedShell={(string.IsNullOrWhiteSpace(commandRequest.Shell) ? "<auto>" : "<set>")}; " +
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
            systemRunAllowOutbound = settings.SystemRunAllowOutbound,
            systemRunAllowWindowsUi = settings.SystemRunAllowWindowsUi,
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
