using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared;

/// <summary>
/// A single rule in the exec approval policy.
/// Rules are evaluated top-to-bottom; first match wins.
/// </summary>
public class ExecApprovalRule
{
    /// <summary>Pattern to match against the command string (glob-style: * = any chars)</summary>
    public string Pattern { get; set; } = "*";
    
    /// <summary>Whether matching commands are allowed or denied</summary>
    public ExecApprovalAction Action { get; set; } = ExecApprovalAction.Deny;
    
    /// <summary>Optional: restrict to specific shells (null = all shells)</summary>
    public string[]? Shells { get; set; }
    
    /// <summary>Optional description for display</summary>
    public string? Description { get; set; }
    
    /// <summary>Whether this rule is enabled</summary>
    public bool Enabled { get; set; } = true;
}

public enum ExecApprovalAction
{
    Allow,
    Deny,
    Prompt
}

/// <summary>
/// Result of evaluating a command against the policy.
/// </summary>
public class ExecApprovalResult
{
    public bool Allowed { get; set; }
    public ExecApprovalAction Action { get; set; }
    public string? MatchedPattern { get; set; }
    public string? Reason { get; set; }
}

/// <summary>
/// Manages execution approval rules for system.run commands.
/// Rules are persisted to a JSON file and evaluated top-to-bottom (first match wins).
/// If no rules match, the default action applies (configurable, defaults to Deny).
/// </summary>
public class ExecApprovalPolicy
{
    private readonly IOpenClawLogger _logger;
    private readonly string _policyFilePath;
    private List<ExecApprovalRule> _rules = new();
    private ExecApprovalAction _defaultAction = ExecApprovalAction.Deny;
    
    // Compiled regex cache — ConcurrentDictionary for thread safety.
    // Pattern → compiled Regex mapping never changes for a given pattern string
    // (glob-to-regex conversion is deterministic), so no cache invalidation is needed.
    private static readonly ConcurrentDictionary<string, Regex> _regexCache = new(StringComparer.Ordinal);
    
    /// <summary>Current rules (read-only view)</summary>
    public IReadOnlyList<ExecApprovalRule> Rules => _rules.AsReadOnly();
    
    /// <summary>Action when no rules match</summary>
    public ExecApprovalAction DefaultAction
    {
        get => _defaultAction;
        set => _defaultAction = value;
    }
    
    public ExecApprovalPolicy(string dataPath, IOpenClawLogger logger)
    {
        _logger = logger;
        _policyFilePath = Path.Combine(dataPath, "exec-policy.json");
        Load();
    }
    
    /// <summary>
    /// Evaluate whether a command is allowed to execute.
    /// </summary>
    public ExecApprovalResult Evaluate(string command, string? shell = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new ExecApprovalResult
            {
                Allowed = false,
                Action = ExecApprovalAction.Deny,
                Reason = "Empty command"
            };
        }
        
        var shellSpan = (shell ?? "powershell").AsSpan();

        foreach (var rule in _rules)
        {
            if (!rule.Enabled) continue;
            
            // Check shell filter
            if (rule.Shells is { Length: > 0 })
            {
                var shellMatched = false;
                foreach (var s in rule.Shells)
                {
                    if (s.AsSpan().Equals(shellSpan, StringComparison.OrdinalIgnoreCase))
                    {
                        shellMatched = true;
                        break;
                    }
                }
                if (!shellMatched) continue;
            }
            
            // Check pattern match
            if (MatchesPattern(command, rule.Pattern))
            {
                var allowed = rule.Action == ExecApprovalAction.Allow;
                _logger.Info($"[EXEC-POLICY] {(allowed ? "ALLOW" : "DENY")}: '{command}' matched rule '{rule.Pattern}'");
                
                return new ExecApprovalResult
                {
                    Allowed = allowed,
                    Action = rule.Action,
                    MatchedPattern = rule.Pattern,
                    Reason = rule.Description ?? $"Matched rule: {rule.Pattern}"
                };
            }
        }
        
        // No rule matched - use default
        var defaultAllowed = _defaultAction == ExecApprovalAction.Allow;
        _logger.Info($"[EXEC-POLICY] DEFAULT {(_defaultAction)}: '{command}' (no rule matched)");
        
        return new ExecApprovalResult
        {
            Allowed = defaultAllowed,
            Action = _defaultAction,
            Reason = "No matching rule; default policy applied"
        };
    }
    
    /// <summary>
    /// Add a rule to the policy. Persists to disk.
    /// </summary>
    public void AddRule(ExecApprovalRule rule)
    {
        _rules.Add(rule);
        Save();
    }
    
    /// <summary>
    /// Insert a rule at a specific index. Persists to disk.
    /// </summary>
    public void InsertRule(int index, ExecApprovalRule rule)
    {
        index = Math.Clamp(index, 0, _rules.Count);
        _rules.Insert(index, rule);
        Save();
    }
    
    /// <summary>
    /// Remove a rule by index. Persists to disk.
    /// </summary>
    public bool RemoveRule(int index)
    {
        if (index < 0 || index >= _rules.Count) return false;
        _rules.RemoveAt(index);
        Save();
        return true;
    }
    
    /// <summary>
    /// Replace all rules. Persists to disk.
    /// </summary>
    public void SetRules(IEnumerable<ExecApprovalRule> rules, ExecApprovalAction? defaultAction = null)
    {
        _rules = new List<ExecApprovalRule>(rules);
        if (defaultAction.HasValue) _defaultAction = defaultAction.Value;
        Save();
    }
    
    /// <summary>
    /// Get a serializable snapshot of the policy.
    /// </summary>
    public ExecPolicyData GetPolicyData()
    {
        return new ExecPolicyData
        {
            DefaultAction = _defaultAction,
            Rules = _rules.ToList()
        };
    }

    public string GetPolicyHash()
    {
        var json = JsonSerializer.Serialize(GetPolicyData(), _jsonOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return $"sha256:{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }
    
    /// <summary>
    /// Load policy from disk. Creates default policy if file doesn't exist.
    /// </summary>
    public void Load()
    {
        try
        {
            if (File.Exists(_policyFilePath))
            {
                var json = File.ReadAllText(_policyFilePath);
                var data = JsonSerializer.Deserialize<ExecPolicyData>(json, _jsonOptions);
                if (data != null)
                {
                    _rules = data.Rules ?? new List<ExecApprovalRule>();
                    _defaultAction = data.DefaultAction;
                    _logger.Info($"[EXEC-POLICY] Loaded {_rules.Count} rules from {_policyFilePath}");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"[EXEC-POLICY] Failed to load policy: {ex.Message}");
        }
        
        // Default policy: allow safe read-only commands, deny everything else
        _rules = CreateDefaultRules();
        _defaultAction = ExecApprovalAction.Deny;
        _logger.Info("[EXEC-POLICY] Using default policy");
        Save();
    }
    
    /// <summary>
    /// Save current policy to disk.
    /// </summary>
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_policyFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(GetPolicyData(), _jsonOptions);
            File.WriteAllText(_policyFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.Error($"[EXEC-POLICY] Failed to save: {ex.Message}");
        }
    }
    
    private static List<ExecApprovalRule> CreateDefaultRules()
    {
        return new List<ExecApprovalRule>
        {
            // Allow common read-only / diagnostic commands
            new() { Pattern = "echo *", Action = ExecApprovalAction.Allow, Description = "Echo commands" },
            new() { Pattern = "Get-*", Action = ExecApprovalAction.Allow, Shells = new[] { "powershell", "pwsh" }, Description = "PowerShell Get- cmdlets (read-only)" },
            new() { Pattern = "dir *", Action = ExecApprovalAction.Allow, Description = "Directory listing" },
            new() { Pattern = "hostname", Action = ExecApprovalAction.Allow, Description = "Hostname query" },
            new() { Pattern = "whoami", Action = ExecApprovalAction.Allow, Description = "Current user" },
            new() { Pattern = "systeminfo", Action = ExecApprovalAction.Allow, Description = "System info" },
            new() { Pattern = "ipconfig *", Action = ExecApprovalAction.Allow, Description = "Network config" },
            new() { Pattern = "ping *", Action = ExecApprovalAction.Allow, Description = "Ping" },
            new() { Pattern = "type *", Action = ExecApprovalAction.Allow, Shells = new[] { "cmd" }, Description = "Read file (cmd)" },
            new() { Pattern = "cat *", Action = ExecApprovalAction.Allow, Description = "Read file" },
            
            // Deny dangerous patterns explicitly
            new() { Pattern = "Remove-Item *", Action = ExecApprovalAction.Deny, Description = "Block file deletion" },
            new() { Pattern = "rm *", Action = ExecApprovalAction.Deny, Description = "Block rm" },
            new() { Pattern = "del *", Action = ExecApprovalAction.Deny, Description = "Block del" },
            new() { Pattern = "Format-*", Action = ExecApprovalAction.Deny, Description = "Block format commands" },
            new() { Pattern = "Stop-Computer*", Action = ExecApprovalAction.Deny, Description = "Block shutdown" },
            new() { Pattern = "Restart-Computer*", Action = ExecApprovalAction.Deny, Description = "Block restart" },
            new() { Pattern = "*Invoke-WebRequest*", Action = ExecApprovalAction.Deny, Description = "Block web downloads" },
            new() { Pattern = "*Start-Process*", Action = ExecApprovalAction.Deny, Description = "Block process launch" },
            new() { Pattern = "*reg *", Action = ExecApprovalAction.Deny, Description = "Block registry edits" },
            new() { Pattern = "shutdown*", Action = ExecApprovalAction.Deny, Description = "Block shutdown" },
            new() { Pattern = "net *", Action = ExecApprovalAction.Deny, Description = "Block net commands" },
        };
    }
    
    /// <summary>
    /// Glob-style pattern matching: * matches any chars, ? matches single char.
    /// Case-insensitive. Returns false on regex timeout (guards against ReDoS in
    /// user-supplied policy files) and denies the command as the safe default.
    /// </summary>
    internal bool MatchesPattern(string command, string pattern)
    {
        if (pattern == "*") return true;

        var regex = _regexCache.GetOrAdd(pattern, static p =>
        {
            var regexPattern = "^" + Regex.Escape(p)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        });

        try
        {
            return regex.IsMatch(command);
        }
        catch (RegexMatchTimeoutException)
        {
            _logger.Warn($"[EXEC-POLICY] Pattern match timed out for '{pattern}'; denying as safe default");
            return false;
        }
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>
/// Serializable policy data for persistence.
/// </summary>
public class ExecPolicyData
{
    public ExecApprovalAction DefaultAction { get; set; } = ExecApprovalAction.Deny;
    public List<ExecApprovalRule> Rules { get; set; } = new();
}
