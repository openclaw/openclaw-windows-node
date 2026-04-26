using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Capabilities;

/// <summary>
/// System capability - notifications, exec (future), etc.
/// </summary>
public class SystemCapability : NodeCapabilityBase
{
    public override string Category => "system";
    
    private static readonly string[] _commands = new[]
    {
        "system.notify",
        "system.run",
        "system.run.prepare",
        "system.which",
        "system.execApprovals.get",
        "system.execApprovals.set"
    };
    
    public override IReadOnlyList<string> Commands => _commands;
    
    // Event to let UI handle the actual notification display
    public event EventHandler<SystemNotifyArgs>? NotifyRequested;
    
    // Command runner for system.run (swappable: local, docker, wsl)
    private ICommandRunner? _commandRunner;
    
    // Exec approval policy (optional - if null, all commands are allowed)
    private ExecApprovalPolicy? _approvalPolicy;
    
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
            extensions.AddRange(pathext.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.ToLowerInvariant()));
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
            GetIntArg(request.Args, "timeout", 30000));
        
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
            var blockedList = string.Join(", ", envResult.Blocked.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
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
            if (!approval.Allowed)
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
                if (!innerApproval.Allowed)
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
    
    private NodeInvokeResponse HandleExecApprovalsGet()
    {
        if (_approvalPolicy == null)
        {
            return Success(new { enabled = false, message = "No exec policy configured" });
        }
        
        var data = _approvalPolicy.GetPolicyData();
        return Success(new
        {
            enabled = true,
            defaultAction = data.DefaultAction.ToString().ToLowerInvariant(),
            rules = data.Rules.Select(r => new
            {
                pattern = r.Pattern,
                action = r.Action.ToString().ToLowerInvariant(),
                shells = r.Shells,
                description = r.Description,
                enabled = r.Enabled
            }).ToArray()
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
                        rule.Shells = shellsEl.EnumerateArray()
                            .Where(s => s.ValueKind == System.Text.Json.JsonValueKind.String)
                            .Select(s => s.GetString() ?? "")
                            .ToArray();
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
            
            _approvalPolicy.SetRules(rules, defaultAction);
            Logger.Info($"Exec approval policy updated: {rules.Count} rules");
            
            return Success(new { updated = true, ruleCount = rules.Count });
        }
        catch (Exception ex)
        {
            Logger.Error("execApprovals.set failed", ex);
            return Error($"Failed to update policy: {ex.Message}");
        }
    }
}

public class SystemNotifyArgs : EventArgs
{
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Subtitle { get; set; }
    public bool PlaySound { get; set; } = true;
}
