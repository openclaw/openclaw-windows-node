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

    private readonly bool _includeRunCommands;

    public override IReadOnlyList<string> Commands =>
        _includeRunCommands ? _commandsWithRun : _commandsWithoutRun;

    // Event to let UI handle the actual notification display
    public event EventHandler<SystemNotifyArgs>? NotifyRequested;

    // Command runner for system.run (swappable: local, docker, wsl)
    private ICommandRunner? _commandRunner;

    private ExecApprovalsStore? _approvalsStore;

    private IExecApprovalV2Handler _v2Handler = ExecApprovalV2NullHandler.Instance;
    
    /// <param name="logger">Capability logger.</param>
    /// <param name="includeRunCommands">
    /// When false, <c>system.run</c> and <c>system.run.prepare</c> are dropped
    /// from <see cref="Commands"/> and <see cref="ExecuteAsync"/> rejects them
    /// with a clear error before V2 dispatch. The rest of the
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
    
    public void SetApprovalsStore(ExecApprovalsStore approvalsStore)
    {
        _approvalsStore = approvalsStore;
    }

    /// <summary>
    /// Install the exec approval handler used by every system.run request.
    /// </summary>
    public void SetV2Handler(IExecApprovalV2Handler handler)
    {
        _v2Handler = handler;
    }
    
    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        // "Run system tools" kill switch — applied before approval dispatch
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
            "system.execApprovals.get" => await HandleExecApprovalsGetAsync(),
            "system.execApprovals.set" => await HandleExecApprovalsSetAsync(request),
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
    /// Pre-flight for system.run: validates and echoes back the canonical execution plan
    /// without running anything.
    /// The gateway uses this to build its approval context before the actual run.
    /// </summary>
    private NodeInvokeResponse HandleRunPrepare(NodeInvokeRequest request)
    {
        var validation = ExecApprovalV2InputValidator.Validate(request);
        if (!validation.IsValid)
            return Error($"Invalid system.run request: {validation.Error!.Reason}");

        var validated = validation.Request!;
        var argv = validated.Argv;
        var rawCommand = GetStringArg(request.Args, "rawCommand");
        var sessionKey = request.SessionKey ?? validated.SessionKey;

        Logger.Info(
            $"system.run.prepare: {rawCommand ?? FormatExecCommand(argv)} (cwd={validated.Cwd ?? "default"})");

        return Success(new
        {
            cmdText = rawCommand ?? FormatExecCommand(argv),
            plan = new
            {
                argv,
                cwd = validated.Cwd,
                rawCommand,
                agentId = validated.AgentId,
                sessionKey
            }
        });
    }
    
    private async Task<NodeInvokeResponse> HandleRunAsync(NodeInvokeRequest request)
    {
        var correlationId = Guid.NewGuid().ToString("N")[..8];
        var v2Handler = _v2Handler;
        request.Telemetry?.SetApprovalPipeline(NodeToolApprovalPipeline.V2);

        Logger.Info($"[system.run] corr={correlationId} path=v2");
        var approvalSpan = request.Telemetry?.StartChild(
            NodeToolInvocation.SystemRunAuthorizeSpanName,
            GetTelemetryParentContext(request));
        ExecApprovalV2Result v2Result;
        Type? approvalErrorType = null;
        if (_commandRunner is IDirectArgvSupportAwareCommandRunner argvAware
            && !argvAware.CanExecuteDirectArgv())
        {
            // Fail closed before evaluation when a runner explicitly reports
            // that it cannot preserve an approved direct argv.
            v2Result = ExecApprovalV2Result.Unavailable(
                "system.run cannot execute the approved direct argv with the active command runner");
        }
        else
        {
            try
            {
                v2Result = await v2Handler.HandleAsync(request, correlationId);
            }
            catch (Exception ex)
            {
                Logger.Error($"[system.run] corr={correlationId} path=v2 handler threw", ex);
                v2Result = ExecApprovalV2Result.ValidationFailed("Handler exception");
                approvalErrorType = ex.GetType();
            }
        }

        var approvalCategory = approvalErrorType == null
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
        {
            ExecApprovalRevalidationResult revalidation;
            try
            {
                revalidation = await v2Handler.RevalidateAsync(
                    approvedExecution,
                    correlationId);
            }
            catch (Exception ex)
            {
                Logger.Error(
                    $"[system.run] corr={correlationId} path=v2 policy revalidation threw",
                    ex);
                return ErrorWithDiagnostic(
                    "exec-approvals-v2: InternalError (policy-revalidation-failed)",
                    NodeToolErrorCategory.InternalFailure);
            }

            if (!revalidation.IsCurrent)
            {
                Logger.Warn(
                    $"[system.run] corr={correlationId} path=v2 execution denied " +
                    $"reason={revalidation.Reason}");
                return ErrorWithDiagnostic(
                    $"exec-approvals-v2: ValidationFailed ({revalidation.Reason})",
                    NodeToolErrorCategory.ExecPolicyDenied);
            }

            return await RunApprovedAsync(approvedExecution, correlationId, request);
        }

        var response = Error($"exec-approvals-v2: {v2Result.Code} ({v2Result.Reason})");
        if (approvalCategory != NodeToolErrorCategory.None)
            response.Diagnostic = new NodeToolDiagnostic(approvalCategory);
        return response;
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

    private async Task<NodeInvokeResponse> HandleExecApprovalsGetAsync()
    {
        if (_approvalsStore == null)
            return Error("No exec approvals store configured");

        try
        {
            var snapshot = await _approvalsStore.GetSnapshotAsync().ConfigureAwait(false);
            return Success(ToExecApprovalsPayload(snapshot));
        }
        catch (Exception ex)
        {
            Logger.Error("execApprovals.get failed", ex);
            return Error("Failed to read exec approvals");
        }
    }

    private async Task<NodeInvokeResponse> HandleExecApprovalsSetAsync(NodeInvokeRequest request)
    {
        if (_approvalsStore == null)
            return Error("No exec approvals store configured");

        try
        {
            if (!TryGetBaseHash(request.Args, out var baseHash))
            {
                Logger.Warn("execApprovals.set denied: baseHash is required");
                return Error("baseHash is required for exec approvals updates; reload and retry");
            }

            if (request.Args.ValueKind != System.Text.Json.JsonValueKind.Object
                || !request.Args.TryGetProperty("file", out var fileElement)
                || fileElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return Error("exec approvals file required");
            }

            var file = System.Text.Json.JsonSerializer.Deserialize<ExecApprovalsFile>(
                fileElement.GetRawText(),
                ExecApprovalsStore.JsonOptions);
            if (file is null || file.Version != 1)
                return Error("exec approvals file version 1 required");

            var snapshot = await _approvalsStore.ReplaceAsync(
                baseHash,
                file,
                ValidateExecApprovalsDelta).ConfigureAwait(false);
            if (snapshot is null)
            {
                Logger.Warn("execApprovals.set denied: stale baseHash");
                return Error("exec approvals changed; reload and retry");
            }

            Logger.Info($"Exec approvals updated: {snapshot.File.Agents?.Count ?? 0} agents");
            return Success(ToExecApprovalsPayload(snapshot));
        }
        catch (System.Text.Json.JsonException ex)
        {
            Logger.Warn($"execApprovals.set denied: invalid file ({ex.Message})");
            return Error("Invalid exec approvals file");
        }
        catch (ExecApprovalsValidationException ex)
        {
            Logger.Warn($"execApprovals.set denied: {ex.Message}");
            return Error(ex.Message);
        }
        catch (Exception ex)
        {
            Logger.Error("execApprovals.set failed", ex);
            return Error("Failed to update exec approvals");
        }
    }

    private static object ToExecApprovalsPayload(ExecApprovalsSnapshot snapshot)
    {
        var file = snapshot.File;
        var socketPath = file.Socket?.Path?.Trim();
        var redactedFile = new ExecApprovalsFile
        {
            Version = file.Version,
            Socket = string.IsNullOrWhiteSpace(socketPath)
                ? null
                : new ExecApprovalsSocketConfig { Path = socketPath },
            Defaults = file.Defaults,
            Agents = file.Agents,
        };
        return new
        {
            path = snapshot.Path,
            exists = snapshot.Exists,
            hash = snapshot.Hash,
            baseHash = snapshot.Hash,
            file = System.Text.Json.JsonSerializer.SerializeToElement(
                redactedFile,
                ExecApprovalsStore.JsonOptions),
        };
    }

    private static string? ValidateExecApprovalsDelta(
        ExecApprovalsFile current,
        ExecApprovalsFile desired)
    {
        var enumError = ValidateDefinedPolicyEnums(desired);
        if (enumError is not null)
            return enumError;

        foreach (var (agentId, agent) in desired.Agents ?? [])
        {
            if (string.IsNullOrWhiteSpace(agentId))
                return "Exec approval agent ids cannot be empty.";
            if (agent is null)
                return $"Exec approval agent '{agentId}' is invalid.";
        }

        var policyError = ValidateRemotePolicyMonotonicity(current, desired);
        if (policyError is not null)
            return policyError;

        foreach (var (agentId, agent) in desired.Agents ?? [])
        {

            ExecApprovalsAgent? currentAgent = null;
            current.Agents?.TryGetValue(agentId, out currentAgent);
            var currentPatterns = new HashSet<string>(
                (currentAgent?.Allowlist ?? [])
                    .Select(entry => entry.Pattern?.Trim())
                    .Where(pattern => !string.IsNullOrWhiteSpace(pattern))!,
                StringComparer.OrdinalIgnoreCase);

            foreach (var entry in agent.Allowlist ?? [])
            {
                var pattern = entry.Pattern?.Trim();
                if (string.IsNullOrWhiteSpace(pattern))
                    return "Empty allowlist patterns are not permitted.";
                if (!currentPatterns.Contains(pattern))
                {
                    return
                        $"Remote exec approval updates cannot add or change allowlist entries for agent '{agentId}'.";
                }
            }
        }

        return null;
    }

    private static string? ValidateDefinedPolicyEnums(ExecApprovalsFile file)
    {
        var error = ValidateDefinedPolicyEnums(file.Defaults, "defaults");
        if (error is not null)
            return error;

        foreach (var (agentId, agent) in file.Agents ?? [])
        {
            error = ValidateDefinedPolicyEnums(agent, $"agent '{agentId}'");
            if (error is not null)
                return error;
        }

        return null;
    }

    private static string? ValidateDefinedPolicyEnums(
        ExecApprovalsDefaults? policy,
        string scope)
    {
        if (policy is null)
            return null;
        if (policy.Security.HasValue
            && !Enum.IsDefined(typeof(ExecSecurity), policy.Security.Value))
        {
            return $"Invalid security value for {scope}.";
        }
        if (policy.Ask.HasValue
            && !Enum.IsDefined(typeof(ExecAsk), policy.Ask.Value))
        {
            return $"Invalid ask value for {scope}.";
        }
        if (policy.AskFallback.HasValue
            && !Enum.IsDefined(typeof(ExecSecurity), policy.AskFallback.Value))
        {
            return $"Invalid askFallback value for {scope}.";
        }
        return null;
    }

    private static string? ValidateDefinedPolicyEnums(
        ExecApprovalsAgent? policy,
        string scope)
    {
        if (policy is null)
            return $"Exec approval {scope} is invalid.";
        if (policy.Security.HasValue
            && !Enum.IsDefined(typeof(ExecSecurity), policy.Security.Value))
        {
            return $"Invalid security value for {scope}.";
        }
        if (policy.Ask.HasValue
            && !Enum.IsDefined(typeof(ExecAsk), policy.Ask.Value))
        {
            return $"Invalid ask value for {scope}.";
        }
        if (policy.AskFallback.HasValue
            && !Enum.IsDefined(typeof(ExecSecurity), policy.AskFallback.Value))
        {
            return $"Invalid askFallback value for {scope}.";
        }
        return null;
    }

    private static string? ValidateRemotePolicyMonotonicity(
        ExecApprovalsFile current,
        ExecApprovalsFile desired)
    {
        var error = ComparePolicies(
            ResolvePolicy(current, agentId: null, includeWildcard: false),
            ResolvePolicy(desired, agentId: null, includeWildcard: false),
            "defaults");
        if (error is not null)
            return error;

        error = ComparePolicies(
            ResolvePolicy(current, agentId: null, includeWildcard: true),
            ResolvePolicy(desired, agentId: null, includeWildcard: true),
            "agent '*'");
        if (error is not null)
            return error;

        var agentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in current.Agents?.Keys ?? (IEnumerable<string>)Array.Empty<string>())
        {
            if (id != "*")
                agentIds.Add(id);
        }
        foreach (var id in desired.Agents?.Keys ?? (IEnumerable<string>)Array.Empty<string>())
        {
            if (id != "*")
                agentIds.Add(id);
        }
        agentIds.Add("main");

        foreach (var agentId in agentIds)
        {
            error = ComparePolicies(
                ResolvePolicy(current, agentId, includeWildcard: true),
                ResolvePolicy(desired, agentId, includeWildcard: true),
                $"agent '{agentId}'");
            if (error is not null)
                return error;
        }

        return null;
    }

    private static string? ComparePolicies(
        RemoteEffectivePolicy current,
        RemoteEffectivePolicy desired,
        string scope)
    {
        if (desired.Security > current.Security)
            return $"Remote exec approval updates cannot make security less restrictive for {scope}.";
        if (desired.Ask < current.Ask)
            return $"Remote exec approval updates cannot make ask less restrictive for {scope}.";
        if (desired.AskFallback > current.AskFallback)
            return $"Remote exec approval updates cannot make askFallback less restrictive for {scope}.";
        if (!current.AutoAllowSkills && desired.AutoAllowSkills)
            return $"Remote exec approval updates cannot make policy less restrictive by enabling autoAllowSkills for {scope}.";
        return null;
    }

    private static RemoteEffectivePolicy ResolvePolicy(
        ExecApprovalsFile file,
        string? agentId,
        bool includeWildcard)
    {
        ExecApprovalsAgent? wildcard = null;
        ExecApprovalsAgent? agent = null;
        if (includeWildcard)
            file.Agents?.TryGetValue("*", out wildcard);
        if (agentId is not null)
            file.Agents?.TryGetValue(agentId, out agent);

        return new RemoteEffectivePolicy(
            agent?.Security
                ?? wildcard?.Security
                ?? file.Defaults?.Security
                ?? ExecSecurity.Allowlist,
            agent?.Ask
                ?? wildcard?.Ask
                ?? file.Defaults?.Ask
                ?? ExecAsk.OnMiss,
            agent?.AskFallback
                ?? wildcard?.AskFallback
                ?? file.Defaults?.AskFallback
                ?? ExecSecurity.Deny,
            agent?.AutoAllowSkills
                ?? wildcard?.AutoAllowSkills
                ?? file.Defaults?.AutoAllowSkills
                ?? false);
    }

    private readonly record struct RemoteEffectivePolicy(
        ExecSecurity Security,
        ExecAsk Ask,
        ExecSecurity AskFallback,
        bool AutoAllowSkills);

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
