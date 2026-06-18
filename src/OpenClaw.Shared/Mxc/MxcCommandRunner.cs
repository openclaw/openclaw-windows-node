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
/// <item><c>false</c> — bypass MXC; route through the host runner.</item>
/// </list>
/// </remarks>
public sealed class MxcCommandRunner : ICommandRunner
{
    public string Name => "mxc";

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

    public async Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default)
    {
        var settings = _settingsProvider();

        if (!settings.SystemRunSandboxEnabled)
        {
            _logger.Info("[mxc] sandbox=disabled; routing system.run through host runner");
            return await _hostFallback.RunAsync(request, ct);
        }

        // When MXC sandboxing isn't available on this host (e.g. Windows 10, an
        // older build whose wxc-exec --probe reports no usable isolation tier, or
        // missing wxc-exec.exe), fall back to the host runner so the agent can
        // still execute commands instead of being completely blocked. The Sandbox
        // page is read-only in that state and tells the user their commands are
        // running uncontained.
        if (!_isSandboxAvailable())
        {
            _logger.Warn(
                "[mxc] system.run UNCONTAINED: sandbox unavailable on this host. " +
                "Commands will run on the host without containment. " +
                "Update Windows to enable sandboxing.");
            return await _hostFallback.RunAsync(request, ct);
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

        var settingsDirectoryPath = _settingsDirectoryPathProvider();
        var policy = MxcPolicyBuilder.ForSystemRun(settings, settingsDirectoryPath);
        var argsJson = SerializeArgs(request);

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
            LogSandboxRequest(sandboxRequest, request, settings, settingsDirectoryPath, policy);
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
            // top-level !_isSandboxAvailable() branch will handle the fallback.
            // We also fall back to host execution for THIS call instead of
            // denying it, matching the policy from issue #494.
            _invalidateAvailability?.Invoke();

            _logger.Warn(
                $"[mxc] system.run UNCONTAINED (sandbox enabled but unavailable at runtime: {ex.Message}). " +
                "Falling back to host execution. Update Windows to enable sandboxing.");
            return await _hostFallback.RunAsync(request, ct);
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled (gateway disconnect, agent abort). Propagate so the
            // caller sees the cancellation rather than a fake "exited 0" response.
            throw;
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

    private static JsonElement SerializeArgs(CommandRequest request)
    {
        var payload = new
        {
            command = request.Command,
            shell = request.Shell ?? "powershell",
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
        SettingsData settings,
        string settingsDirectoryPath,
        SandboxPolicy policy)
    {
        var settingsJson = JsonSerializer.Serialize(ToSandboxSettingsDiagnostic(settings, settingsDirectoryPath), DiagnosticJson);
        var policyJson = JsonSerializer.Serialize(policy, DiagnosticJson);
        var message =
            "[mxc] system.run sandbox request " +
            $"executor={_executor.Name}; contained={_executor.IsContained}; " +
            $"sandboxSettingsJson={settingsJson}; " +
            $"shell={commandRequest.Shell ?? "powershell"}; " +
            $"commandLength={commandRequest.Command?.Length ?? 0}; " +
            $"cwd={(string.IsNullOrEmpty(commandRequest.Cwd) ? "<null>" : "<set>")}; " +
            $"envKeys=[{string.Join(",", commandRequest.Env?.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase) ?? Enumerable.Empty<string>())}]; " +
            $"timeoutMs={sandboxRequest.TimeoutMs}; maxOutputBytes={sandboxRequest.MaxOutputBytes?.ToString() ?? "<default>"}; " +
            $"policyJson={policyJson}";
        LogMxcDiagnostic(message);
    }

    private static object ToSandboxSettingsDiagnostic(SettingsData settings, string settingsDirectoryPath)
    {
        return new
        {
            systemRunSandboxEnabled = settings.SystemRunSandboxEnabled,
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
