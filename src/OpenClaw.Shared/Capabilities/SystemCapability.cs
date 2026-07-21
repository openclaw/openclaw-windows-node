using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared.ExecApprovals;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared.Capabilities;

/// <summary>
/// System capability - notifications, exec (future), etc.
/// </summary>
public class SystemCapability : NodeCapabilityBase
{
    public override string Category => "system";

    private const int DefaultRunTimeoutMs = 30_000;
    private const int MaxRunTimeoutMs = 600_000; // 10 minutes

    private static readonly string[] _commandsWithRun = new[]
    {
        "system.notify",
        "system.run",
        "system.run.prepare",
        "system.which",
        "system.execApprovals.get",
        "system.execApprovals.set"
    };

    private static readonly string[] _commandsWithoutRun = new[]
    {
        "system.notify",
        "system.which",
        "system.execApprovals.get",
        "system.execApprovals.set"
    };

    private static readonly string[] DangerousAllowPatternFragments =
    [
        "remove-item",
        "rm ",
        "del ",
        "erase ",
        "rd ",
        "rmdir ",
        "format-",
        "stop-computer",
        "restart-computer",
        "shutdown",
        "invoke-webrequest",
        "invoke-restmethod",
        "invoke-expression",
        "iex ",
        "invoke-command",
        "icm ",
        "start-process",
        "set-executionpolicy",
        "reg ",
        "net ",
        // Living-off-the-land binaries: native tools whose purpose here is code execution or remote
        // download-and-run — the same intent already blocked for the PowerShell downloaders
        // (invoke-webrequest/-restmethod) and process spawning (start-process), but in native-binary
        // form the fragments above miss. A remote .set must not be able to whitelist one and invoke
        // it (mshta/regsvr32/rundll32 run remote script; certutil/bitsadmin/curl/wget download). A
        // denylist cannot be exhaustive against the LOLBAS set — the local Permissions UI, not a
        // remote caller, is the place to allow anything broader than the read-only defaults.
        "mshta",
        "rundll32",
        "regsvr32",
        "regsvcs",
        "regasm",
        "installutil",
        "msbuild",
        "wmic",
        "certutil",
        "bitsadmin",
        "curl",
        "wget"
    ];
    
    private readonly bool _includeRunCommands;

    public override IReadOnlyList<string> Commands =>
        _includeRunCommands ? _commandsWithRun : _commandsWithoutRun;

    // Event to let UI handle the actual notification display
    public event EventHandler<SystemNotifyArgs>? NotifyRequested;

    /// <summary>
    /// Fired when policy denies an exec request non-interactively (no native
    /// prompt is shown — e.g. default action is Deny, or matched rule action
    /// is Deny). Lets UI surfaces (chat) render a denial card that would
    /// otherwise be invisible to the user. Not raised when the user clicks
    /// Deny in the native prompt — that path already raises
    /// <see cref="ExecApprovalPromptService.Decided"/>.
    /// </summary>
    public event EventHandler<ExecApprovalPromptDecidedEventArgs>? PolicyAutoDecided;
    
    // Command runner for system.run (swappable: local, docker, wsl)
    private ICommandRunner? _commandRunner;

    // Exec approval policy (optional - if null, all commands are allowed)
    private ExecApprovalPolicy? _approvalPolicy;
    private IExecApprovalPromptHandler? _promptHandler;

    // V2 exec approval handler (null = legacy path; inert until explicitly set)
    private IExecApprovalV2Handler? _v2Handler;
    
    /// <param name="logger">Capability logger.</param>
    /// <param name="includeRunCommands">
    /// When false, <c>system.run</c> and <c>system.run.prepare</c> are dropped
    /// from <see cref="Commands"/> and <see cref="ExecuteAsync"/> rejects them
    /// with a clear error before any V2/legacy dispatch. The rest of the
    /// <c>system</c> category (notify/which/execApprovals.get/set) is
    /// unaffected. Wired from the tray "Run system tools" permission toggle
    /// via <c>NodeCapabilityGating.ShouldRegisterSystemRun</c>.
    /// </param>
    public SystemCapability(IOpenClawLogger logger, bool includeRunCommands = true) : base(logger)
    {
        _includeRunCommands = includeRunCommands;
    }
    
    /// <summary>
    /// Set the command runner implementation (local, docker, wsl, etc.)
    /// </summary>
    public void SetCommandRunner(ICommandRunner runner)
    {
        _commandRunner = runner;
    }
    
    /// <summary>
    /// Set the exec approval policy. When set, system.run checks approval before executing.
    /// </summary>
    public void SetApprovalPolicy(ExecApprovalPolicy policy)
    {
        _approvalPolicy = policy;
    }

    public void SetPromptHandler(IExecApprovalPromptHandler promptHandler)
    {
        _promptHandler = promptHandler;
    }

    /// <summary>
    /// Install a V2 exec approval handler. When set, system.run routes to the V2 path
    /// instead of the legacy path. The V2 path is inert until this is called.
    /// </summary>
    public void SetV2Handler(IExecApprovalV2Handler handler)
    {
        _v2Handler = handler;
    }
    
    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        // "Run system tools" kill switch — applied before V2/legacy dispatch
        // so stale gateway allowlists and cached MCP clients still see the
        // capability as disabled when the user turned it off.
        if (!_includeRunCommands &&
            (request.Command == "system.run" || request.Command == "system.run.prepare"))
        {
            Logger.Info($"[system.run] rejected: 'Run system tools' is disabled (command={request.Command})");
            return ErrorWithDiagnostic(
                "system.run is disabled by user setting (Permissions → Run system tools).",
                NodeToolErrorCategory.PermissionDenied);
        }

        return request.Command switch
        {
            "system.notify" => await HandleNotifyAsync(request),
            "system.run" => await HandleRunAsync(request),
            "system.run.prepare" => HandleRunPrepare(request),
            "system.which" => HandleWhich(request),
            "system.execApprovals.get" => HandleExecApprovalsGet(),
            "system.execApprovals.set" => HandleExecApprovalsSet(request),
            _ => Error($"Unknown command: {request.Command}")
        };
    }
    
    private Task<NodeInvokeResponse> HandleNotifyAsync(NodeInvokeRequest request)
    {
        var title = GetStringArg(request.Args, "title", "OpenClaw");
        var body = GetStringArg(request.Args, "body", "");
        var subtitle = GetStringArg(request.Args, "subtitle");
        var sound = GetBoolArg(request.Args, "sound", true);
        
        Logger.Info($"system.notify: {title} - {body}");
        
        // Raise event for UI to handle
        NotifyRequested?.Invoke(this, new SystemNotifyArgs
        {
            Title = title ?? "OpenClaw",
            Body = body ?? "",
            Subtitle = subtitle,
            PlaySound = sound
        });
        
        return Task.FromResult(Success(new { sent = true }));
    }
    
    private NodeInvokeResponse HandleWhich(NodeInvokeRequest request)
    {
        var bins = GetStringArrayArg(request.Args, "bins");

        if (bins.Length == 0)
            return Error("Missing bins parameter");

        var found = new Dictionary<string, string>();
        foreach (var bin in bins)
        {
            var resolved = ResolveExecutable(bin);
            if (resolved != null)
                found[bin] = resolved;
        }

        Logger.Info($"system.which: queried {bins.Length} bins, found {found.Count}");
        return Success(new { bins = found });
    }
    
    /// <summary>
    /// Resolve an executable name to its full path by searching PATH directories.
    /// Matches OpenClaw upstream behavior: rejects paths with separators, checks PATHEXT on Windows.
    /// </summary>
    internal static string? ResolveExecutable(string bin)
    {
        // Reject anything that looks like a path
        if (bin.Contains('/') || bin.Contains('\\'))
            return null;
        
        var extensions = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var pathext = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM";
            foreach (var e in pathext.Split(';', StringSplitOptions.RemoveEmptyEntries))
                extensions.Add(e.ToLowerInvariant());
        }
        else
        {
            extensions.Add("");
        }
        
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var dir in dirs)
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, bin + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        
        return null;
    }
    
    private static string FormatExecCommand(string[] argv) => ShellQuoting.FormatExecCommand(argv);
    
    /// <summary>
    /// Parses a JSON "command" property as either a string array or a plain string.
    /// Returns the argv array (command as first element) or null if missing/invalid.
    /// </summary>
    private static string[]? TryParseArgv(System.Text.Json.JsonElement requestArgs)
    {
        if (requestArgs.ValueKind == System.Text.Json.JsonValueKind.Undefined ||
            !requestArgs.TryGetProperty("command", out var cmdEl))
            return null;

        if (cmdEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in cmdEl.EnumerateArray())
            {
                if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                    list.Add(item.GetString() ?? "");
            }
            return list.Count > 0 ? list.ToArray() : null;
        }
        
        if (cmdEl.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var command = cmdEl.GetString();
            return command != null ? new[] { command } : null;
        }
        
        return null;
    }

    /// <summary>
    /// Pre-flight for system.run: echoes back the execution plan without running anything.
    /// The gateway uses this to build its approval context before the actual run.
    /// </summary>
    private NodeInvokeResponse HandleRunPrepare(NodeInvokeRequest request)
    {
        var argv = TryParseArgv(request.Args);
        if (argv == null || argv.Length == 0 || string.IsNullOrWhiteSpace(argv[0]))
        {
            return Error("Missing command parameter");
        }
        
        var command = argv[0];
        var rawCommand = GetStringArg(request.Args, "rawCommand");
        var requestedShell = GetStringArg(request.Args, "shell");
        var effectiveShell = _commandRunner?.ResolveEffectiveShell(requestedShell)
            ?? ResolveDefaultEffectiveShell(requestedShell);
        var cwd = GetStringArg(request.Args, "cwd");
        var agentId = GetStringArg(request.Args, "agentId");
        var sessionKey = request.SessionKey ?? GetStringArg(request.Args, "sessionKey");
        
        Logger.Info(
            $"system.run.prepare: {rawCommand} (shell={effectiveShell}, requestedShell={requestedShell ?? "auto"}, cwd={cwd ?? "default"})");
        
        return Success(new
        {
            cmdText = rawCommand ?? FormatExecCommand(argv),
            plan = new
            {
                argv,
                cwd,
                rawCommand,
                requestedShell = string.IsNullOrWhiteSpace(requestedShell) ? null : requestedShell.Trim(),
                effectiveShell,
                agentId,
                sessionKey
            }
        });
    }

    private static string ResolveDefaultEffectiveShell(string? requestedShell)
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
    
    private async Task<NodeInvokeResponse> HandleRunAsync(NodeInvokeRequest request)
    {
        var correlationId = Guid.NewGuid().ToString("N")[..8];
        var v2Handler = _v2Handler;
        request.Telemetry?.SetApprovalPipeline(
            v2Handler != null
                ? NodeToolApprovalPipeline.V2
                : NodeToolApprovalPipeline.Legacy);

        // Routing seam (rail 2): select path, delegate — no approval logic here.
        if (v2Handler != null)
        {
            Logger.Info($"[system.run] corr={correlationId} path=v2");
            var approvalSpan = request.Telemetry?.StartChild(
                NodeToolInvocation.SystemRunAuthorizeSpanName,
                GetTelemetryParentContext(request));
            ExecApprovalV2Result v2Result;
            var approvalCategory = NodeToolErrorCategory.None;
            Type? approvalErrorType = null;
            if (_commandRunner is IDirectArgvSupportAwareCommandRunner argvAware
                && !argvAware.CanExecuteDirectArgv())
            {
                // Approved commands execute as a direct argv, which the active
                // sandbox transport cannot carry yet. Fail closed before any
                // evaluation or prompt so nothing gets approved that cannot
                // execute. The sandbox is never bypassed or disabled from here.
                v2Result = ExecApprovalV2Result.Unavailable(
                    "sandboxed system.run cannot execute the approved command form yet; " +
                    "keep the sandbox on and disable the new approvals path, or turn the sandbox off, to run commands");
            }
            else
            {
                try
                {
                    v2Result = await v2Handler.HandleAsync(request, correlationId);
                }
                catch (Exception ex)
                {
                    // Rail 1: no silent fallback — handler exceptions become typed denies.
                    Logger.Error($"[system.run] corr={correlationId} path=v2 handler threw", ex);
                    v2Result = ExecApprovalV2Result.ValidationFailed("Handler exception");
                    approvalErrorType = ex.GetType();
                }
            }

            approvalCategory = approvalErrorType == null
                ? MapV2ErrorCategory(v2Result.Code)
                : NodeToolErrorCategory.InternalFailure;
            Logger.Info($"[system.run] corr={correlationId} decision={v2Result.Code} reason={v2Result.Reason}");
            NodeToolInvocation.CompleteChild(
                approvalSpan,
                approvalCategory == NodeToolErrorCategory.None
                    ? NodeToolOutcome.Success
                    : NodeToolOutcome.Failure,
                approvalCategory,
                errorType: approvalErrorType);

            if (v2Result.IsAllow && v2Result.Execution is { } approvedExecution)
                return await RunApprovedAsync(approvedExecution, correlationId, request);

            // No fallback to legacy regardless of result code: any non-allow
            // outcome from the approval handler is a terminal, typed error.
            var response = Error($"exec-approvals-v2: {v2Result.Code} ({v2Result.Reason})");
            if (approvalCategory != NodeToolErrorCategory.None)
                response.Diagnostic = new NodeToolDiagnostic(approvalCategory);
            return response;
        }

        // Legacy path — untouched (rail 3).
        Logger.Info($"[system.run] corr={correlationId} path=legacy decision=legacy reason=legacy");

        if (_commandRunner == null)
        {
            return ErrorWithDiagnostic(
                "Command execution not available",
                NodeToolErrorCategory.CapabilityUnavailable);
        }
        
        // Per OpenClaw spec, "command" is an argv array (e.g. ["echo","Hello"]).
        // Also accept a plain string for backward compatibility.
        var argv = TryParseArgv(request.Args);
        string? command = argv?[0];
        string[]? args = argv?.Length > 1 ? argv[1..] : null;
        
        // When command is a string, also check for separate "args" array
        if (argv?.Length == 1 && request.Args.TryGetProperty("args", out var argsEl) &&
            argsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in argsEl.EnumerateArray())
            {
                if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                    list.Add(item.GetString() ?? "");
            }
            if (list.Count > 0)
                args = list.ToArray();
        }
        
        if (string.IsNullOrWhiteSpace(command))
        {
            return ErrorWithDiagnostic("Missing command parameter", NodeToolErrorCategory.InvalidRequest);
        }
        
        var shell = GetStringArg(request.Args, "shell");
        var cwd = GetStringArg(request.Args, "cwd");
        var sessionKey = request.SessionKey ?? GetStringArg(request.Args, "sessionKey");
        var timeoutMs = GetIntArg(request.Args, "timeoutMs",
            GetIntArg(request.Args, "timeout", DefaultRunTimeoutMs));
        // Clamp caller-supplied timeouts. timeoutMs <= 0 historically meant
        // "wait forever" inside LocalCommandRunner; that lets a wedged process
        // pin a handler slot indefinitely, so we coerce to the default. The
        // upper bound is generous but prevents a multi-day timeout request
        // from accidentally outliving the tray.
        if (timeoutMs <= 0) timeoutMs = DefaultRunTimeoutMs;
        if (timeoutMs > MaxRunTimeoutMs) timeoutMs = MaxRunTimeoutMs;
        
        // Parse env dict if present
        Dictionary<string, string>? env = null;
        if (request.Args.ValueKind != System.Text.Json.JsonValueKind.Undefined &&
            request.Args.TryGetProperty("env", out var envEl) &&
            envEl.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in envEl.EnumerateObject())
            {
                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    env[prop.Name] = prop.Value.GetString() ?? "";
            }
        }

        var envResult = ExecEnvSanitizer.Sanitize(env);
        if (envResult.Blocked.Length > 0)
        {
            var blockedNames = (string[])envResult.Blocked.Clone();
            Array.Sort(blockedNames, StringComparer.OrdinalIgnoreCase);
            var blockedList = string.Join(", ", blockedNames);
            Logger.Warn($"system.run DENIED: blocked environment overrides [{blockedList}]");
            return ErrorWithDiagnostic(
                $"Unsafe environment variable override blocked: {blockedList}",
                NodeToolErrorCategory.InvalidRequest);
        }
        env = envResult.Allowed;
        
        // Build the full command string for policy evaluation and logging.
        // When command arrives as an argv array, we must evaluate the entire
        // command line — not just argv[0] — so policy rules like "rm *" correctly
        // match "rm -rf /".
        var fullCommand = args != null
            ? FormatExecCommand([command!, ..args])
            : command;
        var requestedShell = string.IsNullOrWhiteSpace(shell) ? null : shell.Trim();
        var effectiveShell = _commandRunner.ResolveEffectiveShell(requestedShell);
        var approvedHostFallbackShell = _commandRunner is IHostFallbackAwareCommandRunner fallbackAwareRunner
            ? fallbackAwareRunner.ResolveHostFallbackShellForApproval(requestedShell, effectiveShell)
            : null;
        
        Logger.Info($"system.run: {fullCommand} (shell={effectiveShell}, requestedShell={shell ?? "auto"}, timeout={timeoutMs}ms)");
        
        // Check exec approval policy
        if (_approvalPolicy != null)
        {
            var approvalSpan = request.Telemetry?.StartChild(
                NodeToolInvocation.SystemRunAuthorizeSpanName,
                GetTelemetryParentContext(request));
            try
            {
                var approvalError = await EnsureCommandAndNestedTargetsApprovedAsync(
                    fullCommand,
                    effectiveShell,
                    sessionKey,
                    correlationId);
                if (approvalError != null)
                {
                    approvalError.Diagnostic = new NodeToolDiagnostic(NodeToolErrorCategory.ExecPolicyDenied);
                    NodeToolInvocation.CompleteChild(
                        approvalSpan,
                        NodeToolOutcome.Failure,
                        NodeToolErrorCategory.ExecPolicyDenied);
                    return approvalError;
                }

                if (!string.IsNullOrWhiteSpace(approvedHostFallbackShell)
                    && !string.Equals(approvedHostFallbackShell, effectiveShell, StringComparison.OrdinalIgnoreCase))
                {
                    approvalError = await EnsureCommandAndNestedTargetsApprovedAsync(
                        fullCommand,
                        approvedHostFallbackShell,
                        sessionKey,
                        correlationId);
                    if (approvalError != null)
                    {
                        approvalError.Diagnostic = new NodeToolDiagnostic(NodeToolErrorCategory.ExecPolicyDenied);
                        NodeToolInvocation.CompleteChild(
                            approvalSpan,
                            NodeToolOutcome.Failure,
                            NodeToolErrorCategory.ExecPolicyDenied);
                        return approvalError;
                    }
                }

                NodeToolInvocation.CompleteChild(approvalSpan, NodeToolOutcome.Success);
            }
            catch (Exception ex)
            {
                NodeToolInvocation.CompleteChild(
                    approvalSpan,
                    NodeToolOutcome.Failure,
                    NodeToolErrorCategory.InternalFailure,
                    errorType: ex.GetType());
                throw;
            }
        }
        
        var runSpan = request.Telemetry?.StartChild(
            NodeToolInvocation.SystemRunRunSpanName,
            GetTelemetryParentContext(request));
        try
        {
            var result = await _commandRunner.RunAsync(new CommandRequest
            {
                Command = command,
                Args = args,
                Shell = requestedShell,
                Cwd = cwd,
                TimeoutMs = timeoutMs,
                Env = env,
                ApprovedEffectiveShell = effectiveShell,
                ApprovedHostFallbackShell = approvedHostFallbackShell,
                Telemetry = request.Telemetry,
                TelemetryParentContext = runSpan?.Context ?? request.Telemetry?.Context ?? default
            });

            var executionMode = result.ExecutionMode ?? NodeToolExecutionMode.Host;
            var errorCategory = ClassifyCommandResult(result);
            if (result.SandboxDenialReason.HasValue)
                request.Telemetry?.SetSandboxDenialReason(result.SandboxDenialReason.Value);
            NodeToolInvocation.CompleteChild(
                runSpan,
                errorCategory == NodeToolErrorCategory.None
                    ? NodeToolOutcome.Success
                    : NodeToolOutcome.Failure,
                errorCategory,
                    executionMode,
                    sandboxDenialReason: result.SandboxDenialReason);

            var response = Success(new
            {
                stdout = result.Stdout,
                stderr = result.Stderr,
                exitCode = result.ExitCode,
                timedOut = result.TimedOut,
                success = result.ExitCode == 0 && !result.TimedOut,
                durationMs = result.DurationMs
            });
            if (errorCategory != NodeToolErrorCategory.None)
            {
                response.Diagnostic = new NodeToolDiagnostic(
                    errorCategory,
                    executionMode,
                    result.SandboxDenialReason);
            }
            return response;
        }
        catch (Exception ex)
        {
            Logger.Error("system.run failed", ex);
            NodeToolInvocation.CompleteChild(
                runSpan,
                NodeToolOutcome.Failure,
                NodeToolErrorCategory.InternalFailure,
                errorType: ex.GetType());
            var response = ErrorWithDiagnostic("Execution failed", NodeToolErrorCategory.InternalFailure);
            return response;
        }
    }

    private NodeInvokeResponse ErrorWithDiagnostic(
        string message,
        NodeToolErrorCategory errorCategory,
        NodeToolExecutionMode? executionMode = null)
    {
        var response = Error(message);
        response.Diagnostic = new NodeToolDiagnostic(errorCategory, executionMode);
        return response;
    }

    private static System.Diagnostics.ActivityContext? GetTelemetryParentContext(
        NodeInvokeRequest request) =>
        request.TelemetryParentContext != default
            ? request.TelemetryParentContext
            : request.Telemetry?.Context;

    private static NodeToolErrorCategory ClassifyCommandResult(CommandResult result)
    {
        if (result.ErrorCategory != NodeToolErrorCategory.None)
            return result.ErrorCategory;
        if (result.TimedOut)
            return NodeToolErrorCategory.Timeout;
        return result.ExitCode == 0
            ? NodeToolErrorCategory.None
            : NodeToolErrorCategory.CommandFailed;
    }

    private static NodeToolErrorCategory MapV2ErrorCategory(ExecApprovalV2Code code) =>
        code switch
        {
            ExecApprovalV2Code.SecurityDeny
                or ExecApprovalV2Code.AskDeny
                or ExecApprovalV2Code.AllowlistMiss
                or ExecApprovalV2Code.UserDenied => NodeToolErrorCategory.ExecPolicyDenied,
            ExecApprovalV2Code.ValidationFailed => NodeToolErrorCategory.InvalidRequest,
            ExecApprovalV2Code.ResolutionFailed => NodeToolErrorCategory.CommandUnavailable,
            ExecApprovalV2Code.Unavailable => NodeToolErrorCategory.CapabilityUnavailable,
            ExecApprovalV2Code.InternalError => NodeToolErrorCategory.InternalFailure,
            ExecApprovalV2Code.Allow => NodeToolErrorCategory.None,
            _ => NodeToolErrorCategory.InternalFailure
        };

    /// <summary>
    /// Execute a command the V2 approval handler allowed. The request is built
    /// from the approved payload only — validated argv plus sanitized env — so
    /// the process receives exactly what was approved, with no shell re-parsing
    /// and nothing re-derived from the raw request. The payload's constructor
    /// already clamps the timeout to the system.run maximum.
    /// </summary>
    private async Task<NodeInvokeResponse> RunApprovedAsync(
        ExecApprovedExecution execution,
        string correlationId,
        NodeInvokeRequest request)
    {
        var runSpan = request.Telemetry?.StartChild(
            NodeToolInvocation.SystemRunRunSpanName,
            GetTelemetryParentContext(request));
        if (_commandRunner == null)
        {
            NodeToolInvocation.CompleteChild(
                runSpan,
                NodeToolOutcome.Failure,
                NodeToolErrorCategory.CapabilityUnavailable);
            return ErrorWithDiagnostic(
                "Command execution not available",
                NodeToolErrorCategory.CapabilityUnavailable);
        }

        try
        {
            var commandRequest = execution.ToCommandRequest();
            commandRequest.Telemetry = request.Telemetry;
            commandRequest.TelemetryParentContext =
                runSpan?.Context ?? request.Telemetry?.Context ?? default;
            var result = await _commandRunner.RunAsync(commandRequest);
            Logger.Info($"[system.run] corr={correlationId} path=v2 executed exit={result.ExitCode} timedOut={result.TimedOut}");

            var executionMode = result.ExecutionMode ?? NodeToolExecutionMode.Host;
            var errorCategory = ClassifyCommandResult(result);
            if (result.SandboxDenialReason.HasValue)
                request.Telemetry?.SetSandboxDenialReason(result.SandboxDenialReason.Value);
            NodeToolInvocation.CompleteChild(
                runSpan,
                errorCategory == NodeToolErrorCategory.None
                    ? NodeToolOutcome.Success
                    : NodeToolOutcome.Failure,
                errorCategory,
                executionMode,
                sandboxDenialReason: result.SandboxDenialReason);

            var response = Success(new
            {
                stdout = result.Stdout,
                stderr = result.Stderr,
                exitCode = result.ExitCode,
                timedOut = result.TimedOut,
                success = result.ExitCode == 0 && !result.TimedOut,
                durationMs = result.DurationMs
            });
            if (errorCategory != NodeToolErrorCategory.None)
            {
                response.Diagnostic = new NodeToolDiagnostic(
                    errorCategory,
                    executionMode,
                    result.SandboxDenialReason);
            }
            return response;
        }
        catch (Exception ex)
        {
            Logger.Error($"[system.run] corr={correlationId} path=v2 execution failed", ex);
            NodeToolInvocation.CompleteChild(
                runSpan,
                NodeToolOutcome.Failure,
                NodeToolErrorCategory.InternalFailure,
                errorType: ex.GetType());
            return ErrorWithDiagnostic("Execution failed", NodeToolErrorCategory.InternalFailure);
        }
    }

    private async Task<ExecApprovalCheckResult> EnsureApprovedAsync(
        string command,
        string? shell,
        ExecApprovalResult approval,
        string? sessionKey,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        if (approval.Allowed)
            return new ExecApprovalCheckResult(true, null);

        if (approval.Action != ExecApprovalAction.Prompt || _promptHandler == null || _approvalPolicy == null)
        {
            RaisePolicyAutoDenied(command, shell, approval);
            return new ExecApprovalCheckResult(false, null);
        }

        var decision = await _promptHandler.RequestAsync(new ExecApprovalPromptRequest
        {
            Command = command,
            Shell = shell,
            MatchedPattern = approval.MatchedPattern,
            Reason = approval.Reason ?? "Command requires approval",
            SessionKey = sessionKey,
            CorrelationId = correlationId
        }, cancellationToken);

        if (decision.Kind == ExecApprovalPromptDecisionKind.Deny)
        {
            Logger.Warn($"system.run DENIED by prompt: {command} ({decision.Reason})");
            return new ExecApprovalCheckResult(false, decision.Kind);
        }

        if (decision.Kind == ExecApprovalPromptDecisionKind.AlwaysAllow)
        {
            if (CanPersistExactAllowRule(command))
            {
                _approvalPolicy.InsertRule(0, new ExecApprovalRule
                {
                    Pattern = command,
                    Action = ExecApprovalAction.Allow,
                    Shells = string.IsNullOrWhiteSpace(shell) ? null : [shell],
                    Description = "Approved from Windows tray prompt"
                });
                Logger.Info($"system.run prompt persisted exact allow rule: {command}");
            }
            else
            {
                Logger.Warn($"system.run prompt could not persist wildcard command; allowing once only: {command}");
            }
        }

        Logger.Info($"system.run APPROVED by prompt: {command} ({decision.Kind})");
        return new ExecApprovalCheckResult(true, decision.Kind);
    }

    private async Task<NodeInvokeResponse?> EnsureCommandAndNestedTargetsApprovedAsync(
        string fullCommand,
        string? shell,
        string? sessionKey,
        string correlationId)
    {
        if (_approvalPolicy == null)
            return null;

        var parseResult = ExecShellWrapperParser.Expand(fullCommand, shell);
        if (!string.IsNullOrWhiteSpace(parseResult.Error))
        {
            Logger.Warn($"system.run DENIED: {fullCommand} ({parseResult.Error})");
            return Error($"Command denied by exec policy: {parseResult.Error}");
        }

        var approval = _approvalPolicy.Evaluate(fullCommand, shell);
        var hasNestedTargets = parseResult.Targets.Count > 0;
        var hasExplicitOuterRule = !string.IsNullOrWhiteSpace(approval.MatchedPattern);
        var evaluateOuter =
            !hasNestedTargets ||
            hasExplicitOuterRule ||
            approval.Allowed ||
            approval.Action == ExecApprovalAction.Prompt;
        var approvalCheck = new ExecApprovalCheckResult(false, null);
        if (evaluateOuter)
        {
            approvalCheck = await EnsureApprovedAsync(fullCommand, shell, approval, sessionKey, correlationId);
            if (!approvalCheck.Allowed)
            {
                Logger.Warn($"system.run DENIED: {fullCommand} ({approval.Reason})");
                return Error($"Command denied by exec policy: {approval.Reason}");
            }
        }

        var outerApprovalCoversNestedTargets =
            approvalCheck.PromptDecisionKind != null ||
            IsExactAllowRuleForCommand(approval, fullCommand);

        // Gateway execution wraps Windows commands in cmd.exe. Only an
        // unmatched default deny may defer to exact parsed-payload rules;
        // explicit/default prompts still show and persist the full wrapper.
        foreach (var target in parseResult.Targets)
        {
            var innerApproval = _approvalPolicy.Evaluate(target.Command, target.Shell);
            if (outerApprovalCoversNestedTargets && !IsExplicitDeny(innerApproval))
            {
                if (!innerApproval.Allowed)
                {
                    Logger.Info(
                        $"system.run nested approval covered by approved wrapper: {target.Command} ({innerApproval.Reason})");
                }
                continue;
            }

            var innerApprovalCheck = await EnsureApprovedAsync(
                target.Command,
                target.Shell,
                innerApproval,
                sessionKey,
                correlationId);
            if (!innerApprovalCheck.Allowed)
            {
                Logger.Warn($"system.run DENIED: {target.Command} ({innerApproval.Reason})");
                return Error($"Command denied by exec policy: {innerApproval.Reason}");
            }

            if (!evaluateOuter &&
                innerApprovalCheck.PromptDecisionKind == null &&
                !IsExactAllowRuleForCommand(innerApproval, target.Command))
            {
                const string reason = "Wrapped commands require an exact allow rule";
                Logger.Warn($"system.run DENIED: {target.Command} ({reason})");
                return Error($"Command denied by exec policy: {reason}");
            }
        }

        return null;
    }

    private static bool CanPersistExactAllowRule(string command) =>
        !string.IsNullOrWhiteSpace(command) &&
        command.IndexOfAny(['*', '?']) < 0;

    private static bool IsExactAllowRuleForCommand(ExecApprovalResult approval, string command) =>
        approval.Allowed &&
        approval.Action == ExecApprovalAction.Allow &&
        !string.IsNullOrWhiteSpace(approval.MatchedPattern) &&
        approval.MatchedPattern.IndexOfAny(['*', '?']) < 0 &&
        string.Equals(approval.MatchedPattern, command, StringComparison.OrdinalIgnoreCase);

    private static bool IsExplicitDeny(ExecApprovalResult approval) =>
        !approval.Allowed &&
        approval.Action == ExecApprovalAction.Deny &&
        !string.IsNullOrWhiteSpace(approval.MatchedPattern);

    private readonly record struct ExecApprovalCheckResult(
        bool Allowed,
        ExecApprovalPromptDecisionKind? PromptDecisionKind);

    private void RaisePolicyAutoDenied(string command, string? shell, ExecApprovalResult approval)
    {
        var handler = PolicyAutoDecided;
        if (handler == null) return;
        try
        {
            var request = new ExecApprovalPromptRequest
            {
                Command = command,
                Shell = shell,
                MatchedPattern = approval.MatchedPattern,
                Reason = approval.Reason ?? "Command denied by policy"
            };
            var decision = ExecApprovalPromptDecision.Deny(approval.Reason ?? "Command denied by policy");
            handler(this, new ExecApprovalPromptDecidedEventArgs(
                request,
                decision,
                ExecApprovalPromptDecisionSource.PolicyAutoDeny));
        }
        catch (Exception ex)
        {
            Logger.Warn($"PolicyAutoDecided handler threw: {ex.Message}");
        }
    }
    
    private NodeInvokeResponse HandleExecApprovalsGet()
    {
        if (_approvalPolicy == null)
        {
            return Success(new { enabled = false, message = "No exec policy configured" });
        }
        
        var data = _approvalPolicy.GetPolicyData();
        var policyHash = _approvalPolicy.GetPolicyHash();
        var rules = data.Rules;
        var rulesSummary = new object[rules.Count];
        for (var i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            rulesSummary[i] = new
            {
                pattern = r.Pattern,
                action = r.Action.ToString().ToLowerInvariant(),
                shells = r.Shells,
                description = r.Description,
                enabled = r.Enabled
            };
        }

        return Success(new
        {
            enabled = true,
            hash = policyHash,
            baseHash = policyHash,
            defaultAction = data.DefaultAction.ToString().ToLowerInvariant(),
            constraints = new
            {
                baseHashRequired = true,
                defaultAllowAllowed = false,
                broadAllowRulesAllowed = false,
                dangerousAllowRulesAllowed = false
            },
            rules = rulesSummary
        });
    }
    
    private NodeInvokeResponse HandleExecApprovalsSet(NodeInvokeRequest request)
    {
        if (_approvalPolicy == null)
        {
            return Error("No exec policy configured");
        }
        
        try
        {
            var currentHash = _approvalPolicy.GetPolicyHash();
            if (!TryGetBaseHash(request.Args, out var baseHash))
            {
                Logger.Warn("execApprovals.set denied: baseHash is required");
                return Error("baseHash is required for exec approval policy updates. Refresh policy and retry.");
            }

            if (!HashesMatch(baseHash, currentHash))
            {
                Logger.Warn("execApprovals.set denied: stale baseHash");
                return Error("Exec approval policy changed since it was loaded. Refresh policy and retry.");
            }

            // Parse rules from args
            var rules = new List<ExecApprovalRule>();
            
            if (request.Args.ValueKind != System.Text.Json.JsonValueKind.Undefined &&
                request.Args.TryGetProperty("rules", out var rulesEl) &&
                rulesEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var ruleEl in rulesEl.EnumerateArray())
                {
                    var rule = new ExecApprovalRule();
                    
                    if (ruleEl.TryGetProperty("pattern", out var patEl) && patEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        rule.Pattern = patEl.GetString() ?? "*";
                    
                    if (ruleEl.TryGetProperty("action", out var actEl) && actEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var actStr = actEl.GetString() ?? "deny";
                        rule.Action = actStr.ToLowerInvariant() switch
                        {
                            "allow" => ExecApprovalAction.Allow,
                            "prompt" or "ask" => ExecApprovalAction.Prompt,
                            _ => ExecApprovalAction.Deny
                        };
                    }
                    
                    if (ruleEl.TryGetProperty("description", out var descEl) && descEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        rule.Description = descEl.GetString();
                    
                    if (ruleEl.TryGetProperty("enabled", out var enEl) && (enEl.ValueKind == System.Text.Json.JsonValueKind.True || enEl.ValueKind == System.Text.Json.JsonValueKind.False))
                        rule.Enabled = enEl.GetBoolean();
                    
                    if (ruleEl.TryGetProperty("shells", out var shellsEl) && shellsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        var shellsList = new List<string>(shellsEl.GetArrayLength());
                        foreach (var s in shellsEl.EnumerateArray())
                        {
                            if (s.ValueKind == System.Text.Json.JsonValueKind.String)
                                shellsList.Add(s.GetString() ?? "");
                        }
                        rule.Shells = shellsList.ToArray();
                    }
                    
                    rules.Add(rule);
                }
            }
            
            // Parse default action
            ExecApprovalAction? defaultAction = null;
            if (request.Args.TryGetProperty("defaultAction", out var defEl) && defEl.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var defStr = defEl.GetString() ?? "deny";
                defaultAction = defStr.ToLowerInvariant() switch
                {
                    "allow" => ExecApprovalAction.Allow,
                    "prompt" or "ask" => ExecApprovalAction.Prompt,
                    _ => ExecApprovalAction.Deny
                };
            }

            if (defaultAction == ExecApprovalAction.Allow)
            {
                Logger.Warn("execApprovals.set denied: default allow is not permitted");
                return Error("Default allow is not permitted for remote exec approval policy updates.");
            }

            var validationError = ValidateExecApprovalRules(rules);
            if (validationError != null)
            {
                Logger.Warn($"execApprovals.set denied: {validationError}");
                return Error(validationError);
            }
             
            _approvalPolicy.SetRules(rules, defaultAction);
            var newHash = _approvalPolicy.GetPolicyHash();
            Logger.Info($"Exec approval policy updated: {rules.Count} rules");
             
            return Success(new { updated = true, ruleCount = rules.Count, hash = newHash, baseHash = newHash });
        }
        catch (Exception ex)
        {
            Logger.Error("execApprovals.set failed", ex);
            return Error("Failed to update policy");
        }
    }

    private static string? ValidateExecApprovalRules(IEnumerable<ExecApprovalRule> rules)
    {
        foreach (var rule in rules)
        {
            if (rule.Action != ExecApprovalAction.Allow)
                continue;

            var pattern = rule.Pattern.Trim();
            if (string.IsNullOrWhiteSpace(pattern))
                return "Empty allow rule patterns are not permitted.";

            // Normalize to lowercase and collapse all whitespace runs (including tabs,
            // non-breaking spaces) to a single ASCII space so fragment checks cannot be
            // bypassed with alternate whitespace characters.
            var normalized = System.Text.RegularExpressions.Regex.Replace(
                pattern.ToLowerInvariant(), @"\s+", " ");

            // Catch all-wildcard patterns (e.g. *, **, ?*, * ?) that match any command.
            // Strip every wildcard character and whitespace; if nothing remains the pattern
            // is effectively "match everything" and must be blocked regardless of spelling.
            var nonWildcardContent = normalized.Replace("*", "").Replace("?", "").Trim();
            if (string.IsNullOrEmpty(nonWildcardContent))
                return $"Broad allow rule is not permitted: {pattern}";

            // Catch shell-prefixed blanket patterns that match all commands in a given shell
            // (e.g. "powershell *" allows every PowerShell command).
            if (normalized is "powershell *" or "pwsh *" or "cmd *" or "cmd.exe *")
                return $"Broad allow rule is not permitted: {pattern}";

            // Reject Allow rules whose pattern looks like an absolute file path.
            // A remote .set call should never be able to whitelist a specific binary
            // by path — that would be a two-step EoP (compromise MCP token → whitelist
            // attacker binary → invoke it). Legitimate rules name commands, not paths.
            // Parse the executable token first so quoted paths remain one token even
            // when the rule includes arguments.
            // Covers: drive-rooted paths (C:\, C:/), UNC/long-path (\\, //), and
            // forward-slash UNC namespace forms (//server/share, //?/C:/evil.exe).
            var executableToken = ExecCommandToken.ParseFirstToken(normalized);
            if (executableToken is null)
                return $"Allow rule must begin with a valid executable token: {pattern}";

            var pathToken = executableToken;
            if (pathToken.Length >= 3 &&
                ((char.IsLetter(pathToken[0]) && pathToken[1] == ':' && (pathToken[2] == '\\' || pathToken[2] == '/')) ||
                 pathToken.StartsWith(@"\\", StringComparison.Ordinal) ||
                 pathToken.StartsWith("//", StringComparison.Ordinal)))
                return $"Absolute path allow rule is not permitted: {pattern}";

            if (ExecCommandToken.IsIndirectCommandHost(executableToken))
                return $"Allow rules cannot target shell interpreters or command hosts: {pattern}";

            foreach (var dangerous in DangerousAllowPatternFragments)
            {
                if (normalized.Contains(dangerous, StringComparison.Ordinal))
                    return $"Dangerous allow rule is not permitted: {pattern}";

                // Also block stem+wildcard (e.g. "rm*" bypasses "rm " because the
                // fragment has a trailing space that the wildcard replaces).
                var stem = dangerous.TrimEnd();
                if (stem.Length < dangerous.Length &&
                    (normalized.Contains(stem + "*", StringComparison.Ordinal) ||
                     normalized.Contains(stem + "?", StringComparison.Ordinal)))
                {
                    return $"Dangerous allow rule is not permitted: {pattern}";
                }
            }

            // Finally: the executable (first whitespace-delimited token) must be a concrete literal. A
            // wildcard there lets a NON-dangerous pattern match ANY command (MatchesPattern globs
            // * -> .* over the whole command line), e.g. "*.*", "*e*", "*.exe", "c*" — the broad-allow
            // class the earlier shape checks miss. Runs after the dangerous-fragment check so a
            // dangerous stem keeps its specific message. Legit rules pin the command ("git *").
            if (executableToken.Contains('*') || executableToken.Contains('?'))
                return $"Allow rule must name a concrete command (no wildcard in the executable): {pattern}";
        }

        return null;
    }

    private static bool TryGetBaseHash(System.Text.Json.JsonElement args, out string baseHash)
    {
        baseHash = "";
        if (args.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            return false;

        if (args.TryGetProperty("baseHash", out var baseHashEl) &&
            baseHashEl.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            baseHash = baseHashEl.GetString() ?? "";
            return !string.IsNullOrWhiteSpace(baseHash);
        }

        if (args.TryGetProperty("base_hash", out var baseHashSnakeEl) &&
            baseHashSnakeEl.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            baseHash = baseHashSnakeEl.GetString() ?? "";
            return !string.IsNullOrWhiteSpace(baseHash);
        }

        return false;
    }

    private static bool HashesMatch(string candidate, string currentHash)
    {
        if (string.Equals(candidate, currentHash, StringComparison.OrdinalIgnoreCase))
            return true;

        const string prefix = "sha256:";
        if (currentHash.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate, currentHash[prefix.Length..], StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}

public class SystemNotifyArgs : EventArgs
{
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Subtitle { get; set; }
    public bool PlaySound { get; set; } = true;
}
