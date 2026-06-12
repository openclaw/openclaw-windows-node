using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;

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
/// JsonConverter for <see cref="ExecApprovalAction"/> that emits/accepts the canonical
/// camelCase values ("allow", "deny", "prompt") but also accepts the legacy "ask" alias.
/// Older builds of the Permissions UI wrote "ask" for the Prompt action; without this
/// converter, deserialization would throw and the entire policy file (including any
/// user-authored rules) would be silently replaced with the default policy on load.
/// </summary>
internal sealed class ExecApprovalActionConverter : JsonConverter<ExecApprovalAction>
{
    public override ExecApprovalAction Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"Expected string for ExecApprovalAction, got {reader.TokenType}");

        var value = reader.GetString();
        return value?.ToLowerInvariant() switch
        {
            "allow" => ExecApprovalAction.Allow,
            "deny" => ExecApprovalAction.Deny,
            "prompt" or "ask" => ExecApprovalAction.Prompt,
            _ => throw new JsonException($"Unknown ExecApprovalAction value '{value}'")
        };
    }

    public override void Write(Utf8JsonWriter writer, ExecApprovalAction value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            ExecApprovalAction.Allow => "allow",
            ExecApprovalAction.Deny => "deny",
            ExecApprovalAction.Prompt => "prompt",
            _ => throw new JsonException($"Unknown ExecApprovalAction enum {value}")
        });
    }
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

    // Protects _rules, _defaultAction, and the file-signature fields below.
    // Evaluate() takes the lock just long enough to hot-reload (if needed) and
    // snapshot state into locals; pattern matching runs outside the lock.
    private readonly object _stateLock = new();

    // File-signature cache for non-destructive hot-reload. When the on-disk
    // file's (mtime, length) differs from the cached pair, Evaluate() reparses
    // the file into local variables and swaps them in only on success — never
    // calling the bootstrap fallback that would wipe user rules on a torn write.
    private DateTime _lastFileMtimeUtc;
    private long _lastFileLength = -1;

    // Compiled regex cache — ConcurrentDictionary for thread safety.
    // Pattern → compiled Regex mapping never changes for a given pattern string
    // (glob-to-regex conversion is deterministic), so no cache invalidation is needed.
    private static readonly ConcurrentDictionary<string, Regex> _regexCache = new(StringComparer.Ordinal);
    
    /// <summary>Current rules (read-only snapshot)</summary>
    public IReadOnlyList<ExecApprovalRule> Rules
    {
        get { lock (_stateLock) return _rules.ToList().AsReadOnly(); }
    }

    /// <summary>Action when no rules match</summary>
    public ExecApprovalAction DefaultAction
    {
        get { lock (_stateLock) return _defaultAction; }
        set { lock (_stateLock) _defaultAction = value; }
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

        // Snapshot policy state under lock (and hot-reload if the on-disk file
        // changed externally — e.g. the Permissions UI saved a new default).
        // Pattern matching runs outside the lock against the local snapshot.
        List<ExecApprovalRule> rulesSnapshot;
        ExecApprovalAction defaultActionSnapshot;
        lock (_stateLock)
        {
            TryHotReloadLocked();
            rulesSnapshot = _rules.ToList();
            defaultActionSnapshot = _defaultAction;
        }

        var shellSpan = (shell ?? "powershell").AsSpan();

        foreach (var rule in rulesSnapshot)
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
        var defaultAllowed = defaultActionSnapshot == ExecApprovalAction.Allow;
        _logger.Info($"[EXEC-POLICY] DEFAULT {(defaultActionSnapshot)}: '{command}' (no rule matched)");
        
        return new ExecApprovalResult
        {
            Allowed = defaultAllowed,
            Action = defaultActionSnapshot,
            Reason = "No matching rule; default policy applied"
        };
    }

    /// <summary>
    /// Non-destructive hot-reload: if the on-disk policy file's signature
    /// (mtime, length) differs from the cached pair, attempt to reparse it.
    /// On success, atomically swap rules + default action. On failure (file
    /// missing, partially written, corrupt JSON), log and keep the existing
    /// in-memory state — NEVER fall back to default rules, which would
    /// silently destroy the user's policy during a torn write.
    /// Caller must hold <see cref="_stateLock"/>.
    /// </summary>
    private void TryHotReloadLocked()
    {
        FileInfo info;
        try
        {
            info = new FileInfo(_policyFilePath);
            if (!info.Exists) return;
        }
        catch
        {
            return;
        }

        var mtime = info.LastWriteTimeUtc;
        var length = info.Length;
        if (mtime == _lastFileMtimeUtc && length == _lastFileLength) return;

        try
        {
            var json = File.ReadAllText(_policyFilePath);
            var data = JsonSerializer.Deserialize<ExecPolicyData>(json, _jsonOptions);
            if (data == null) return; // keep current state

            _rules = data.Rules ?? new List<ExecApprovalRule>();
            _defaultAction = data.DefaultAction;
            _lastFileMtimeUtc = mtime;
            _lastFileLength = length;
            _logger.Info($"[EXEC-POLICY] Hot-reloaded {_rules.Count} rules from {_policyFilePath} (defaultAction={_defaultAction})");
        }
        catch (Exception ex)
        {
            // Keep current in-memory policy. Do not bump the signature cache
            // so the next Evaluate() will retry once the writer finishes.
            _logger.Warn($"[EXEC-POLICY] Hot-reload skipped (file may be mid-write): {ex.Message}");
        }
    }
    
    /// <summary>
    /// Add a rule to the policy. Persists to disk.
    /// </summary>
    public void AddRule(ExecApprovalRule rule)
    {
        lock (_stateLock) _rules.Add(rule);
        Save();
    }
    
    /// <summary>
    /// Insert a rule at a specific index. Persists to disk.
    /// </summary>
    public void InsertRule(int index, ExecApprovalRule rule)
    {
        lock (_stateLock)
        {
            index = Math.Clamp(index, 0, _rules.Count);
            _rules.Insert(index, rule);
        }
        Save();
    }
    
    /// <summary>
    /// Remove a rule by index. Persists to disk.
    /// </summary>
    public bool RemoveRule(int index)
    {
        lock (_stateLock)
        {
            if (index < 0 || index >= _rules.Count) return false;
            _rules.RemoveAt(index);
        }
        Save();
        return true;
    }
    
    /// <summary>
    /// Replace all rules. Persists to disk.
    /// </summary>
    public void SetRules(IEnumerable<ExecApprovalRule> rules, ExecApprovalAction? defaultAction = null)
    {
        lock (_stateLock)
        {
            _rules = new List<ExecApprovalRule>(rules);
            if (defaultAction.HasValue) _defaultAction = defaultAction.Value;
        }
        Save();
    }
    
    /// <summary>
    /// Get a serializable snapshot of the policy.
    /// </summary>
    public ExecPolicyData GetPolicyData()
    {
        lock (_stateLock) return GetPolicyDataLocked();
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
                    lock (_stateLock)
                    {
                        _rules = data.Rules ?? new List<ExecApprovalRule>();
                        _defaultAction = data.DefaultAction;
                        UpdateFileSignatureLocked();
                    }
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
        lock (_stateLock)
        {
            _rules = CreateDefaultRules();
            _defaultAction = ExecApprovalAction.Deny;
        }
        _logger.Info("[EXEC-POLICY] Using default policy");
        Save();
    }
    
    /// <summary>
    /// Save current policy to disk atomically (write to .tmp, then replace).
    /// Updates the file-signature cache so the engine doesn't spuriously
    /// hot-reload its own writes.
    /// </summary>
    public void Save()
    {
        string? tmpPath = null;
        try
        {
            var dir = Path.GetDirectoryName(_policyFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            ExecPolicyData snapshot;
            lock (_stateLock)
            {
                snapshot = GetPolicyDataLocked();
            }
            var json = JsonSerializer.Serialize(snapshot, _jsonOptions);

            // Atomic write: serialize to a sibling .tmp first, then replace the
            // target in one move. Guards against torn writes that would let a
            // concurrent reader (or our own hot-reload) see partial JSON.
            tmpPath = $"{_policyFilePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmpPath, json);
            MoveFileWithRetry(tmpPath, _policyFilePath);
            tmpPath = null;

            lock (_stateLock)
            {
                UpdateFileSignatureLocked();
            }
        }
        catch (Exception ex)
        {
            TryDeleteTempFile(tmpPath);
            _logger.Error($"[EXEC-POLICY] Failed to save: {ex.Message}");
        }
    }

    private static void MoveFileWithRetry(string sourcePath, string destinationPath)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (IsTransientReplaceException(ex) && attempt < 20)
            {
                Thread.Sleep(5);
            }
        }
    }

    private static bool IsTransientReplaceException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;

    private static void TryDeleteTempFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup; the original save failure is reported by the caller.
        }
    }

    private void UpdateFileSignatureLocked()
    {
        try
        {
            var info = new FileInfo(_policyFilePath);
            if (info.Exists)
            {
                _lastFileMtimeUtc = info.LastWriteTimeUtc;
                _lastFileLength = info.Length;
            }
        }
        catch
        {
            // Best-effort. If we can't stat, leave cache as-is; next Evaluate
            // will attempt hot-reload (and either succeed or be a no-op).
        }
    }

    private ExecPolicyData GetPolicyDataLocked()
    {
        return new ExecPolicyData
        {
            DefaultAction = _defaultAction,
            Rules = _rules.ToList()
        };
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
        Converters = { new ExecApprovalActionConverter() },
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
