using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared.ExecApprovals;

// New store for exec-approvals.json. Separate from legacy ExecApprovalPolicy (exec-policy.json).
// PR4 scope: read path only. Write path (recordAllowlistUse) is added in PR9.
public sealed class ExecApprovalsStore
{
    // KebabCaseLower covers all macOS enum values: deny, allowlist, full, off, on-miss, always,
    // allow-once, allow-always. CamelCase would fail for "on-miss" and "allow-once".
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    private readonly string _filePath;
    private readonly IOpenClawLogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private enum LoadFileStatus
    {
        Missing,
        Loaded,
        Invalid,
    }

    private readonly record struct LoadFileResult(LoadFileStatus Status, ExecApprovalsFile? File);

    public ExecApprovalsStore(string dataPath, IOpenClawLogger logger)
    {
        _filePath = Path.Combine(dataPath, "exec-approvals.json");
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    // No side effects; does not create the file. Used by the evaluator (PR5).
    public ExecApprovalsResolved ResolveReadOnly(string? agentId)
    {
        var result = LoadFile();
        return result.Status != LoadFileStatus.Loaded || result.File is null
            ? DefaultResolved(NormalizeAgentId(agentId))
            : ResolveFromFile(result.File, agentId);
    }

    // Side-effecting resolve: creates the file if missing, initializes agents dict.
    // For startup / settings UI. Not used by the evaluator.
    public async Task<ExecApprovalsResolved> ResolveAsync(string? agentId)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var file = await EnsureFileAsync().ConfigureAwait(false);
            return ResolveFromFile(file, agentId);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── File I/O ──────────────────────────────────────────────────────────────

    private LoadFileResult LoadFile()
    {
        if (!File.Exists(_filePath)) return new LoadFileResult(LoadFileStatus.Missing, null);
        try
        {
            var json = File.ReadAllText(_filePath);
            var file = JsonSerializer.Deserialize<ExecApprovalsFile>(json, JsonOptions);
            if (file is null)
            {
                _logger.Warn("[EXEC-APPROVALS] exec-approvals.json deserialized to null; applying default-deny");
                return new LoadFileResult(LoadFileStatus.Invalid, null);
            }
            if (file.Version != 1)
            {
                var version = file.Version?.ToString() ?? "missing";
                _logger.Warn($"[EXEC-APPROVALS] exec-approvals.json has unsupported version {version}; applying default-deny");
                return new LoadFileResult(LoadFileStatus.Invalid, null);
            }
            return new LoadFileResult(LoadFileStatus.Loaded, Normalize(file));
        }
        catch (JsonException ex)
        {
            _logger.Warn($"[EXEC-APPROVALS] exec-approvals.json is malformed ({ex.Message}); applying default-deny");
            return new LoadFileResult(LoadFileStatus.Invalid, null);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[EXEC-APPROVALS] Failed to load exec-approvals.json ({ex.Message}); applying default-deny");
            return new LoadFileResult(LoadFileStatus.Invalid, null);
        }
    }

    private async Task<ExecApprovalsFile> EnsureFileAsync()
    {
        var result = LoadFile();
        if (result.Status == LoadFileStatus.Loaded && result.File is not null)
        {
            var file = result.File;
            if (file.Agents is null)
            {
                file = new ExecApprovalsFile
                {
                    Version = file.Version,
                    Socket = file.Socket,
                    Defaults = CopyDefaults(file.Defaults),
                    Agents = [],
                };
                await SaveFileAsync(file).ConfigureAwait(false);
            }
            return file;
        }

        if (result.Status == LoadFileStatus.Invalid)
        {
            _logger.Warn($"[EXEC-APPROVALS] Preserving unreadable exec-approvals.json at {_filePath}; using empty in-memory store");
            return new ExecApprovalsFile { Version = 1, Agents = [] };
        }

        // socket intentionally omitted in Windows v1 (research doc 02 decision 3).
        var newFile = new ExecApprovalsFile { Version = 1, Agents = [] };
        await SaveFileAsync(newFile).ConfigureAwait(false);
        _logger.Info($"[EXEC-APPROVALS] Created {_filePath}");
        return newFile;
    }

    private async Task SaveFileAsync(ExecApprovalsFile file)
    {
        var dir = Path.GetDirectoryName(_filePath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var tmp = Path.Combine(dir, $".exec-approvals-{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(file, JsonOptions);
            await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
            // Atomic replace on NTFS via MoveFileExW (MOVEFILE_REPLACE_EXISTING).
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Error($"[EXEC-APPROVALS] Failed to save {_filePath} ({ex.Message})");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    // ── Normalization ─────────────────────────────────────────────────────────

    private static ExecApprovalsFile Normalize(ExecApprovalsFile file)
    {
        // Trim socket fields; nullify if both are empty after trim.
        var socket = file.Socket is null ? null : NormalizeSocket(file.Socket);

        // Migrate agents["default"] → agents["main"]; "main" wins on conflicting fields.
        // Null agents stays null here — EnsureFileAsync is responsible for initialization.
        var defaults = CopyDefaults(file.Defaults);

        if (file.Agents is null)
            return new ExecApprovalsFile { Version = 1, Socket = socket, Defaults = defaults, Agents = null };

        var agents = new Dictionary<string, ExecApprovalsAgent>(file.Agents);

        if (agents.TryGetValue("default", out var defaultAgent))
        {
            agents.Remove("default");
            agents["main"] = agents.TryGetValue("main", out var mainAgent)
                ? MergeAgent(fallback: defaultAgent, winner: mainAgent)
                : defaultAgent;
        }

        // Normalize allowlist entries (dropInvalid: false — keep non-empty invalids).
        foreach (var key in agents.Keys.ToList())
        {
            var agent = agents[key];
            if (agent.Allowlist is not null)
                agents[key] = WithNormalizedAllowlist(agent, dropInvalid: false);
        }

        return new ExecApprovalsFile { Version = 1, Socket = socket, Defaults = defaults, Agents = agents };
    }

    private static ExecApprovalsDefaults? CopyDefaults(ExecApprovalsDefaults? d) =>
        d is null ? null : new ExecApprovalsDefaults
        {
            Security = d.Security,
            Ask = d.Ask,
            AskFallback = d.AskFallback,
            AutoAllowSkills = d.AutoAllowSkills,
        };

    private static ExecApprovalsSocketConfig? NormalizeSocket(ExecApprovalsSocketConfig s)
    {
        var path = string.IsNullOrWhiteSpace(s.Path) ? null : s.Path.Trim();
        var token = string.IsNullOrWhiteSpace(s.Token) ? null : s.Token.Trim();
        return (path is null && token is null) ? null : new ExecApprovalsSocketConfig { Path = path, Token = token };
    }

    // winner's non-null fields take precedence; allowlists are concatenated (fallback first).
    private static ExecApprovalsAgent MergeAgent(ExecApprovalsAgent fallback, ExecApprovalsAgent winner)
    {
        var allowlist = new List<ExecAllowlistEntry>();
        if (fallback.Allowlist is not null) allowlist.AddRange(fallback.Allowlist);
        if (winner.Allowlist is not null) allowlist.AddRange(winner.Allowlist);

        return new ExecApprovalsAgent
        {
            Security = winner.Security ?? fallback.Security,
            Ask = winner.Ask ?? fallback.Ask,
            AskFallback = winner.AskFallback ?? fallback.AskFallback,
            AutoAllowSkills = winner.AutoAllowSkills ?? fallback.AutoAllowSkills,
            Allowlist = allowlist.Count > 0 ? allowlist : null,
        };
    }

    private static ExecApprovalsAgent WithNormalizedAllowlist(ExecApprovalsAgent agent, bool dropInvalid) =>
        new()
        {
            Security = agent.Security,
            Ask = agent.Ask,
            AskFallback = agent.AskFallback,
            AutoAllowSkills = agent.AutoAllowSkills,
            Allowlist = NormalizeAllowlistEntries(agent.Allowlist!, dropInvalid)
                            is { Count: > 0 } list ? list : null,
        };

    // Mirrors macOS normalizeAllowlistEntries.
    // dropInvalid=false: discard only null/empty patterns; keep non-empty ones regardless of validity.
    // dropInvalid=true: same in v1 — pattern validity beyond non-empty is enforced by the allowlist
    //   matcher in PR5, not here. The flag is preserved for API symmetry with macOS.
    internal static List<ExecAllowlistEntry> NormalizeAllowlistEntries(
        IEnumerable<ExecAllowlistEntry> entries, bool dropInvalid)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ExecAllowlistEntry>();
        foreach (var entry in entries)
        {
            var pattern = entry.Pattern?.Trim();
            if (string.IsNullOrEmpty(pattern)) continue;
            if (!seen.Add(pattern)) continue;
            result.Add(pattern == entry.Pattern ? entry : new ExecAllowlistEntry
            {
                Id = entry.Id,
                Pattern = pattern,
                LastUsedAt = entry.LastUsedAt,
                LastUsedCommand = entry.LastUsedCommand,
                LastResolvedPath = entry.LastResolvedPath,
            });
        }
        return result;
    }

    // ── Cascade resolution ────────────────────────────────────────────────────

    private static ExecApprovalsResolved ResolveFromFile(ExecApprovalsFile file, string? agentId)
    {
        var id = NormalizeAgentId(agentId);
        var agents = file.Agents ?? new Dictionary<string, ExecApprovalsAgent>();
        agents.TryGetValue(id, out var agentEntry);
        agents.TryGetValue("*", out var wildcardEntry);
        var defaults = file.Defaults;

        // Cascade: agentEntry → wildcard → defaults → systemDefault
        var security = agentEntry?.Security ?? wildcardEntry?.Security ?? defaults?.Security ?? ExecSecurity.Deny;
        var ask = agentEntry?.Ask ?? wildcardEntry?.Ask ?? defaults?.Ask ?? ExecAsk.OnMiss;
        var askFallback = agentEntry?.AskFallback ?? wildcardEntry?.AskFallback ?? defaults?.AskFallback ?? ExecAsk.Deny;
        var autoAllowSkills = agentEntry?.AutoAllowSkills ?? wildcardEntry?.AutoAllowSkills ?? defaults?.AutoAllowSkills ?? false;

        // Allowlist: wildcard first, then agent; then normalize dropInvalid=true.
        var combined = new List<ExecAllowlistEntry>();
        if (wildcardEntry?.Allowlist is not null) combined.AddRange(wildcardEntry.Allowlist);
        if (agentEntry?.Allowlist is not null) combined.AddRange(agentEntry.Allowlist);

        return new ExecApprovalsResolved
        {
            AgentId = id,
            Defaults = new ExecApprovalsResolvedDefaults
            {
                Security = security,
                Ask = ask,
                AskFallback = askFallback,
                AutoAllowSkills = autoAllowSkills,
            },
            Allowlist = NormalizeAllowlistEntries(combined, dropInvalid: true),
            SocketToken = file.Socket?.Token,
        };
    }

    private static ExecApprovalsResolved DefaultResolved(string agentId) =>
        new()
        {
            AgentId = agentId,
            Defaults = new ExecApprovalsResolvedDefaults
            {
                Security = ExecSecurity.Deny,
                Ask = ExecAsk.OnMiss,
                AskFallback = ExecAsk.Deny,
                AutoAllowSkills = false,
            },
            Allowlist = [],
        };

    // null/empty agentId → "main". Mirrors macOS. Evaluator does not need to know this.
    private static string NormalizeAgentId(string? agentId) =>
        string.IsNullOrWhiteSpace(agentId) ? "main" : agentId;
}
