using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared.ExecApprovals;

namespace OpenClaw.Shared.Capabilities;

/// <summary>
/// System capability - notifications, exec (future), etc.
/// </summary>
public class SystemCapability : NodeCapabilityBase
{
    public override string Category => "system";

    private const int DefaultRunTimeoutMs = 30_000;
    private const int MaxRunTimeoutMs = 600_000; // 10 minutes

    private static readonly string[] _commands = new[]
    {
        "system.notify",
        "system.run",
        "system.run.prepare",
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
        "start-process",
        "set-executionpolicy",
        "reg ",
        "net "
    ];
    
    public override IReadOnlyList<string> Commands => _commands;
    
    // Event to let UI handle the actual notification display
    public event EventHandler<SystemNotifyArgs>? NotifyRequested;
    
    // Command runner for system.run (swappable: local, docker, wsl)
    private ICommandRunner? _commandRunner;

    // Exec approval policy (optional - if null, all commands are allowed)
    private ExecApprovalPolicy? _approvalPolicy;
    private IExecApprovalPromptHandler? _promptHandler;

    // V2 exec approval handler (null = legacy path; inert until explicitly set)
    private IExecApprovalV2Handler? _v2Handler;
    
    public SystemCapability(IOpenClawLogger logger) : base(logger)
    {
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
        var cwd = GetStringArg(request.Args, "cwd");
        var agentId = GetStringArg(request.Args, "agentId");
        var sessionKey = GetStringArg(request.Args, "sessionKey");
        
        Logger.Info($"system.run.prepare: {rawCommand} (cwd={cwd ?? "default"})");
        
        return Success(new
        {
            cmdText = rawCommand ?? FormatExecCommand(argv),
            plan = new
            {
                argv,
                cwd,
                rawCommand,
                agentId,
                sessionKey
            }
        });
    }
    
    private async Task<NodeInvokeResponse> HandleRunAsync(NodeInvokeRequest request)
    {
        var correlationId = Guid.NewGuid().ToString("N")[..8];

        // Routing seam (rail 2): select path, delegate — no approval logic here.
        if (_v2Handler != null)
        {
            Logger.Info($"[system.run] corr={correlationId} path=v2");
            ExecApprovalV2Result v2Result;
            try
            {
                v2Result = await _v2Handler.HandleAsync(request, correlationId);
            }
            catch (Exception ex)
            {
                // Rail 1: no silent fallback — handler exceptions become typed denies.
                Logger.Error($"[system.run] corr={correlationId} path=v2 handler threw", ex);
                v2Result = ExecApprovalV2Result.ValidationFailed($"Handler exception: {ex.Message}");
            }

            Logger.Info($"[system.run] corr={correlationId} decision={v2Result.Code} reason={v2Result.Reason}");
            // Rail 1: no silent fallback to legacy regardless of result code.
            // In PR1 only ExecApprovalV2NullHandler exists (always unavailable); the real
            // coordinator that can produce an allow decision is wired in PR7/PR8.
            return Error($"exec-approvals-v2: {v2Result.Code} ({v2Result.Reason})");
        }

        // Legacy path — untouched (rail 3).
        Logger.Info($"[system.run] corr={correlationId} path=legacy decision=legacy reason=legacy");

        if (_commandRunner == null)
        {
            return Error("Command execution not available");
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
            return Error("Missing command parameter");
        }
        
        var shell = GetStringArg(request.Args, "shell");
        var cwd = GetStringArg(request.Args, "cwd");
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
            return Error($"Unsafe environment variable override blocked: {blockedList}");
        }
        env = envResult.Allowed;
        
        // Build the full command string for policy evaluation and logging.
        // When command arrives as an argv array, we must evaluate the entire
        // command line — not just argv[0] — so policy rules like "rm *" correctly
        // match "rm -rf /".
        var fullCommand = args != null
            ? FormatExecCommand([command!, ..args])
            : command;
        
        Logger.Info($"system.run: {fullCommand} (shell={shell ?? "auto"}, timeout={timeoutMs}ms)");
        
        // Check exec approval policy
        if (_approvalPolicy != null)
        {
            var approval = _approvalPolicy.Evaluate(fullCommand, shell);
            if (!await EnsureApprovedAsync(fullCommand, shell, approval))
            {
                Logger.Warn($"system.run DENIED: {fullCommand} ({approval.Reason})");
                return Error($"Command denied by exec policy: {approval.Reason}");
            }

            var parseResult = ExecShellWrapperParser.Expand(fullCommand, shell);
            if (!string.IsNullOrWhiteSpace(parseResult.Error))
            {
                Logger.Warn($"system.run DENIED: {fullCommand} ({parseResult.Error})");
                return Error($"Command denied by exec policy: {parseResult.Error}");
            }

            foreach (var target in parseResult.Targets)
            {
                var innerApproval = _approvalPolicy.Evaluate(target.Command, target.Shell);
                if (!await EnsureApprovedAsync(target.Command, target.Shell, innerApproval))
                {
                    Logger.Warn($"system.run DENIED: {target.Command} ({innerApproval.Reason})");
                    return Error($"Command denied by exec policy: {innerApproval.Reason}");
                }
            }
        }
        
        try
        {
            var result = await _commandRunner.RunAsync(new CommandRequest
            {
                Command = command,
                Args = args,
                Shell = shell,
                Cwd = cwd,
                TimeoutMs = timeoutMs,
                Env = env
            });
            
            return Success(new
            {
                stdout = result.Stdout,
                stderr = result.Stderr,
                exitCode = result.ExitCode,
                timedOut = result.TimedOut,
                durationMs = result.DurationMs
            });
        }
        catch (Exception ex)
        {
            Logger.Error("system.run failed", ex);
            return Error($"Execution failed: {ex.Message}");
        }
    }

    private async Task<bool> EnsureApprovedAsync(
        string command,
        string? shell,
        ExecApprovalResult approval,
        CancellationToken cancellationToken = default)
    {
        if (approval.Allowed)
            return true;

        if (approval.Action != ExecApprovalAction.Prompt || _promptHandler == null || _approvalPolicy == null)
            return false;

        var decision = await _promptHandler.RequestAsync(new ExecApprovalPromptRequest
        {
            Command = command,
            Shell = shell,
            MatchedPattern = approval.MatchedPattern,
            Reason = approval.Reason ?? "Command requires approval"
        }, cancellationToken);

        if (decision.Kind == ExecApprovalPromptDecisionKind.Deny)
        {
            Logger.Warn($"system.run DENIED by prompt: {command} ({decision.Reason})");
            return false;
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
        return true;
    }

    private static bool CanPersistExactAllowRule(string command) =>
        !string.IsNullOrWhiteSpace(command) &&
        command.IndexOfAny(['*', '?']) < 0;
    
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
                            "prompt" => ExecApprovalAction.Prompt,
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
                    "prompt" => ExecApprovalAction.Prompt,
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
            return Error($"Failed to update policy: {ex.Message}");
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

            var normalized = pattern.ToLowerInvariant();

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

            foreach (var dangerous in DangerousAllowPatternFragments)
            {
                if (normalized.Contains(dangerous, StringComparison.Ordinal))
                    return $"Dangerous allow rule is not permitted: {pattern}";
            }
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
