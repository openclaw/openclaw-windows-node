using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared.ExecApprovals;

// Authoritative store for exec-approvals.json.
// Read path: ResolveReadOnly, LoadFile, EnsureFileAsync. Write path: ReplaceAsync, AddAllowlistEntryAsync, RecordAllowlistUseAsync.
public sealed class ExecApprovalsStore
{
    // KebabCaseLower covers all macOS enum values: deny, allowlist, full, off, on-miss, always,
    // allow-once, allow-always. CamelCase would fail for "on-miss" and "allow-once".
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.KebabCaseLower,
                allowIntegerValues: false),
        },
    };

    private readonly string _filePath;
    private readonly string? _legacyFilePath;
    private readonly IOpenClawLogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private enum LegacyMigrationStatus
    {
        NotNeeded,
        Migrated,
        Blocked,
    }

    private enum LoadFileStatus
    {
        Missing,
        Loaded,
        Invalid,
    }

    private readonly record struct LoadFileResult(
        LoadFileStatus Status,
        ExecApprovalsFile? File,
        string? Hash);

    public ExecApprovalsStore(string dataPath, IOpenClawLogger logger)
        : this(
            dataPath,
            logger,
            Environment.GetEnvironmentVariable("OPENCLAW_STATE_DIR"),
            Environment.GetEnvironmentVariable("OPENCLAW_HOME"),
            FirstUsablePathValue(
                Environment.GetEnvironmentVariable("HOME"),
                Environment.GetEnvironmentVariable("USERPROFILE"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
    {
    }

    internal ExecApprovalsStore(
        string dataPath,
        IOpenClawLogger logger,
        string? stateDirOverride,
        string? openClawHomeOverride = null,
        string? osHomeOverride = null)
    {
        _filePath = ResolveFilePath(
            dataPath,
            stateDirOverride,
            openClawHomeOverride,
            osHomeOverride);
        var legacyFilePath = Path.Combine(dataPath, "exec-approvals.json");
        _legacyFilePath = PathsEqual(_filePath, legacyFilePath) ? null : legacyFilePath;
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    // No side effects; does not create the file.
    public ExecApprovalsResolved ResolveReadOnly(string? agentId)
    {
        if (_legacyFilePath is not null)
        {
            var targetStatus = LoadFile().Status;
            var legacyStatus = LoadFile(_legacyFilePath).Status;
            if (targetStatus == LoadFileStatus.Missing
                && legacyStatus != LoadFileStatus.Missing)
            {
                return UnmigratedLegacyFallback(agentId);
            }
        }

        var result = LoadFile();
        return result.Status switch
        {
            LoadFileStatus.Loaded when result.File is not null =>
                ResolveFromFile(result.File, agentId),
            LoadFileStatus.Missing =>
                DefaultResolved(NormalizeAgentId(agentId)),
            _ =>
                FailClosedResolved(NormalizeAgentId(agentId)),
        };
    }

    // Adds a new allowlist entry for the agent. Best-effort: never throws.
    // Returns true if the entry is present after the call (added or already there),
    // false if the pattern was empty or the write was skipped/failed.
    // Pattern validation is non-empty only — parity with macOS.
    // Adds a hand-written, path-only rule. Deliberately carries no source and no
    // argument binding: it authorizes the executable whatever its arguments, which is
    // meaningful only because a human wrote it.
    public Task<bool> AddAllowlistEntryAsync(string? agentId, string pattern)
        => AddAllowlistEntryCoreAsync(agentId, pattern, argPattern: null, commandText: null, source: null);

    // Adds a rule generated from an Allow always decision. argPattern is required:
    // every reader ignores a generated rule that has no argument binding, so writing
    // one without a pattern would silently produce a rule that never matches.
    public Task<bool> AddAllowlistEntryAsync(
        string? agentId, string pattern, string? argPattern, string? commandText)
    {
        if (string.IsNullOrWhiteSpace(argPattern))
        {
            _logger.Warn("[EXEC-APPROVALS] AddAllowlistEntry skipped: generated entry requires an argPattern");
            return Task.FromResult(false);
        }
        return AddAllowlistEntryCoreAsync(
            agentId, pattern, argPattern, commandText, ExecAllowlistMatcher.AllowAlwaysSource);
    }

    // Dedup is keyed on (pattern, argPattern) so a bound rule and an unbound rule for
    // the same executable stay distinct records.
    private async Task<bool> AddAllowlistEntryCoreAsync(
        string? agentId, string pattern, string? argPattern, string? commandText, string? source)
    {
        var trimmed = pattern?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            _logger.Debug("[EXEC-APPROVALS] AddAllowlistEntry skipped: empty pattern");
            return false;
        }
        var normalizedArgPattern = string.IsNullOrWhiteSpace(argPattern) ? null : argPattern;
        var key = NormalizeAgentId(agentId);
        bool alreadyPresent = false;
        var wrote = await UpdateFileAsync(file =>
        {
            var agents = file.Agents!;
            if (!agents.TryGetValue(key, out var agent) || agent is null)
            {
                agent = new ExecApprovalsAgent();
                agents[key] = agent;
            }
            var allowlist = agent.Allowlist ??= [];
            // Dedup case-insensitive — consistent with NormalizeAllowlistEntries (OrdinalIgnoreCase HashSet).
            if (allowlist.Any(e => string.Equals(
                    e.Pattern?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.ArgPattern, normalizedArgPattern, StringComparison.Ordinal)))
            {
                alreadyPresent = true;
                return false;
            }
            allowlist.Add(new ExecAllowlistEntry
            {
                Id = Guid.NewGuid(),  // parity with macOS UUID()
                Pattern = trimmed,
                ArgPattern = normalizedArgPattern,
                CommandText = commandText,
                Source = source,
                // LastUsedAt intentionally absent: macOS addAllowlistEntry only sets identity.
                // RecordAllowlistUseAsync stamps it on first successful use.
            });
            return true;
        }).ConfigureAwait(false);
        return wrote || alreadyPresent;
    }

    // Updates lastUsed* metadata for every allowlist entry whose pattern matches.
    // Best-effort: never throws. No-op if the agent or pattern is not found.
    // Returns true if at least one entry was updated and saved; false otherwise.
    // Searches both the concrete agent bucket and the wildcard bucket ("*"),
    // because ResolveReadOnly merges wildcard entries into the resolved allowlist —
    // so a hit can be authorized by either source and metadata must follow.
    public Task<bool> RecordAllowlistUseAsync(
        string? agentId, string pattern, string? resolvedPath)
        => RecordAllowlistUseAsync(agentId, pattern, resolvedPath, lastUsedCommand: null);

    public Task<bool> RecordAllowlistUseAsync(
        string? agentId, string pattern, string? resolvedPath, string? lastUsedCommand)
        => RecordAllowlistUseAsync(agentId, pattern, resolvedPath, lastUsedCommand, entryId: null, argPattern: null);

    // Several entries can now share one pattern and be distinguished only by their
    // argument binding, so usage must be stamped on the entry that actually authorized
    // the run. entryId identifies it exactly; argPattern disambiguates legacy entries
    // written before ids were persisted. With neither, this falls back to the historical
    // pattern-wide stamp.
    public Task<bool> RecordAllowlistUseAsync(
        string? agentId,
        string pattern,
        string? resolvedPath,
        string? lastUsedCommand,
        Guid? entryId,
        string? argPattern)
    {
        if (string.IsNullOrEmpty(pattern)) return Task.FromResult(false);
        var key = NormalizeAgentId(agentId);
        var buckets = key == "*" ? new[] { "*" } : new[] { key, "*" };
        return UpdateFileAsync(file =>
        {
            var changed = false;
            foreach (var bucketKey in buckets)
            {
                if (!file.Agents!.TryGetValue(bucketKey, out var agent) || agent?.Allowlist is null)
                    continue;
                foreach (var entry in agent.Allowlist)
                {
                    if (!IsUsageTarget(entry, pattern, entryId, argPattern))
                        continue;
                    entry.LastUsedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    entry.LastResolvedPath = resolvedPath;  // Id, Pattern, ArgPattern preserved
                    if (lastUsedCommand is not null)
                        entry.LastUsedCommand = lastUsedCommand;
                    changed = true;
                }
            }
            return changed;
        });
    }

    private static bool IsUsageTarget(
        ExecAllowlistEntry entry, string pattern, Guid? entryId, string? argPattern)
    {
        // An id is unique across buckets, so it alone identifies the authorizing entry
        // even when the wildcard bucket merged a same-pattern rule into the resolution.
        if (entryId.HasValue && entry.Id.HasValue)
            return entry.Id.Value == entryId.Value;

        if (!string.Equals(entry.Pattern?.Trim(), pattern.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        // Only narrow by argument binding when the caller knew one; otherwise preserve
        // the historical behavior for callers that cannot supply it.
        if (argPattern is null)
            return true;

        return string.Equals(entry.ArgPattern ?? "", argPattern, StringComparison.Ordinal);
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

    public void MigrateLegacyFileIfNeeded() => TryMigrateLegacyFile();

    public async Task<ExecApprovalsSnapshot> GetSnapshotAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (TryMigrateLegacyFile() == LegacyMigrationStatus.Blocked)
                throw new IOException("Unmigrated exec approvals file is unreadable.");

            var result = LoadFile();
            if (result.Status == LoadFileStatus.Invalid)
                throw new IOException("Exec approvals file is malformed, unsupported, or untrusted.");

            if (result.Status == LoadFileStatus.Missing)
            {
                await SaveFileAsync(NewDefaultFile()).ConfigureAwait(false);
                result = LoadFile();
            }

            if (result.Status != LoadFileStatus.Loaded || result.File is null)
                throw new IOException("Exec approvals snapshot is unavailable.");

            return CreateSnapshot(result.File, exists: true, result.Hash!);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ExecApprovalsSnapshot?> ReplaceAsync(
        string baseHash,
        ExecApprovalsFile replacement,
        Func<ExecApprovalsFile, ExecApprovalsFile, string?>? deltaValidator = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseHash);
        ArgumentNullException.ThrowIfNull(replacement);

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (TryMigrateLegacyFile() == LegacyMigrationStatus.Blocked)
                throw new IOException("Unmigrated exec approvals file is unreadable.");

            var result = LoadFile();
            if (result.Status == LoadFileStatus.Invalid)
                throw new IOException("Exec approvals file is malformed, unsupported, or untrusted.");

            var currentHash = result.Hash ?? ComputeMissingHash();
            if (!string.Equals(baseHash.Trim(), currentHash, StringComparison.Ordinal))
                return null;

            var currentFile = result.File ?? NewDefaultFile();
            var validationError = deltaValidator?.Invoke(currentFile, replacement);
            if (!string.IsNullOrWhiteSpace(validationError))
                throw new ExecApprovalsValidationException(validationError);

            var currentSocket = result.File?.Socket;
            var normalized = Normalize(replacement);
            normalized.Version = 1;
            normalized.Defaults = WithResolvedDefaults(normalized.Defaults);
            normalized.Agents ??= [];
            normalized.Socket = MergeSocket(normalized.Socket, currentSocket);

            var savedHash = await SaveFileAsync(normalized).ConfigureAwait(false);
            return CreateSnapshot(normalized, exists: true, savedHash);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── File I/O ──────────────────────────────────────────────────────────────

    private LoadFileResult LoadFile()
        => LoadFile(_filePath);

    private LoadFileResult LoadFile(string filePath)
    {
        try
        {
            var attributes = File.GetAttributes(filePath);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                _logger.Warn("[EXEC-APPROVALS] exec-approvals.json path is a directory; applying default-deny");
                return new LoadFileResult(LoadFileStatus.Invalid, null, null);
            }
        }
        catch (FileNotFoundException)
        {
            return MissingOrUntrusted(filePath);
        }
        catch (DirectoryNotFoundException)
        {
            return MissingOrUntrusted(filePath);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[EXEC-APPROVALS] Failed to inspect exec-approvals.json ({ex.Message}); applying default-deny");
            return new LoadFileResult(LoadFileStatus.Invalid, null, null);
        }

        // Fail closed if a symlink/junction sits in the store path, or the file has a hard-link
        // alias: either could load or shadow a policy the node owner never authorized. Mirrors
        // macOS O_NOFOLLOW + nlink==1. Residual: this is a check-then-open (a racing swap between
        // the check and the File.ReadAllText below is not caught); fully closing that requires
        // opening once by handle with FILE_FLAG_OPEN_REPARSE_POINT and reading through it.
        if (!ExecApprovalsPathGuard.IsPathTrustworthy(filePath)
            || !ExecApprovalsPathGuard.HasSingleHardLink(filePath))
        {
            _logger.Warn("[EXEC-APPROVALS] exec-approvals.json path is not trustworthy (reparse point or hard link); applying default-deny");
            return new LoadFileResult(LoadFileStatus.Invalid, null, null);
        }
        try
        {
            var json = File.ReadAllText(filePath);
            var file = JsonSerializer.Deserialize<ExecApprovalsFile>(json, JsonOptions);
            if (file is null)
            {
                _logger.Warn("[EXEC-APPROVALS] exec-approvals.json deserialized to null; applying default-deny");
                return new LoadFileResult(LoadFileStatus.Invalid, null, null);
            }
            if (file.Version != 1)
            {
                var version = file.Version?.ToString() ?? "missing";
                _logger.Warn($"[EXEC-APPROVALS] exec-approvals.json has unsupported version {version}; applying default-deny");
                return new LoadFileResult(LoadFileStatus.Invalid, null, null);
            }
            return new LoadFileResult(
                LoadFileStatus.Loaded,
                Normalize(file),
                ComputeRawHash(json));
        }
        catch (JsonException ex)
        {
            _logger.Warn($"[EXEC-APPROVALS] exec-approvals.json is malformed ({ex.Message}); applying default-deny");
            return new LoadFileResult(LoadFileStatus.Invalid, null, null);
        }
        catch (Exception ex)
        {
            _logger.Warn($"[EXEC-APPROVALS] Failed to load exec-approvals.json ({ex.Message}); applying default-deny");
            return new LoadFileResult(LoadFileStatus.Invalid, null, null);
        }
    }

    private LoadFileResult MissingOrUntrusted(string filePath)
    {
        if (ExecApprovalsPathGuard.IsPathTrustworthy(filePath))
            return new LoadFileResult(LoadFileStatus.Missing, null, ComputeMissingHash());

        _logger.Warn("[EXEC-APPROVALS] missing exec-approvals.json path is not trustworthy; applying default-deny");
        return new LoadFileResult(LoadFileStatus.Invalid, null, null);
    }

    private async Task<ExecApprovalsFile> EnsureFileAsync()
    {
        if (TryMigrateLegacyFile() == LegacyMigrationStatus.Blocked)
            return UnmigratedLegacyFallbackFile();

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
            return UnmigratedLegacyFallbackFile();
        }

        // socket intentionally omitted in Windows v1.
        var newFile = NewDefaultFile();
        await SaveFileAsync(newFile).ConfigureAwait(false);
        _logger.Info($"[EXEC-APPROVALS] Created {_filePath}");
        return newFile;
    }

    private LegacyMigrationStatus TryMigrateLegacyFile()
    {
        if (_legacyFilePath is null)
            return LegacyMigrationStatus.NotNeeded;

        var targetResult = LoadFile();
        if (targetResult.Status == LoadFileStatus.Loaded)
            return LegacyMigrationStatus.NotNeeded;
        if (targetResult.Status == LoadFileStatus.Invalid)
            return LegacyMigrationStatus.Blocked;

        var legacyResult = LoadFile(_legacyFilePath);
        if (legacyResult.Status == LoadFileStatus.Missing)
            return LegacyMigrationStatus.NotNeeded;
        if (legacyResult.Status != LoadFileStatus.Loaded || legacyResult.File is null)
        {
            _logger.Warn($"[EXEC-APPROVALS] Legacy approvals at {_legacyFilePath} could not be migrated; applying default-deny without creating {_filePath}");
            return LegacyMigrationStatus.Blocked;
        }

        var targetDir = Path.GetDirectoryName(_filePath)!;
        var archivePath = NextArchivePath(_legacyFilePath);
        var tempPath = Path.Combine(targetDir, $".exec-approvals-migration-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(targetDir);
            var data = File.ReadAllBytes(_legacyFilePath);
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, _filePath);
            try
            {
                File.Move(_legacyFilePath, archivePath);
            }
            catch (Exception ex)
            {
                _logger.Warn($"[EXEC-APPROVALS] Migrated approvals to {_filePath}, but could not archive {_legacyFilePath} ({ex.Message})");
                return LegacyMigrationStatus.Migrated;
            }
            _logger.Info($"[EXEC-APPROVALS] Migrated {_legacyFilePath} to {_filePath}; archived source as {archivePath}");
            return LegacyMigrationStatus.Migrated;
        }
        catch (IOException) when (File.Exists(_filePath))
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            return LegacyMigrationStatus.NotNeeded;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            _logger.Warn($"[EXEC-APPROVALS] Failed to migrate {_legacyFilePath} to {_filePath} ({ex.Message}); applying default-deny without creating a replacement file");
            return LegacyMigrationStatus.Blocked;
        }
    }

    private static string NextArchivePath(string legacyFilePath)
    {
        var archivePath = $"{legacyFilePath}.migrated";
        return File.Exists(archivePath) ? $"{archivePath}-{Guid.NewGuid():N}" : archivePath;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    public static string ResolveFilePath(string dataPath)
        => ResolveFilePath(
            dataPath,
            Environment.GetEnvironmentVariable("OPENCLAW_STATE_DIR"),
            Environment.GetEnvironmentVariable("OPENCLAW_HOME"),
            FirstUsablePathValue(
                Environment.GetEnvironmentVariable("HOME"),
                Environment.GetEnvironmentVariable("USERPROFILE"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

    public static bool IsValidAllowlistPattern(string? pattern)
        => ExecAllowlistMatcher.IsValidPattern(pattern);

    internal static string ResolveFilePath(
        string dataPath,
        string? stateDirOverride,
        string? openClawHomeOverride,
        string? osHomeOverride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        var stateDir = string.IsNullOrWhiteSpace(stateDirOverride)
            ? dataPath
            : ResolveStateDirPath(stateDirOverride, openClawHomeOverride, osHomeOverride);
        return Path.Combine(stateDir, "exec-approvals.json");
    }

    private static string ResolveStateDirPath(
        string stateDirOverride,
        string? openClawHomeOverride,
        string? osHomeOverride)
    {
        var osHome = NormalizePathValue(osHomeOverride) ?? Environment.CurrentDirectory;
        var openClawHome = NormalizePathValue(openClawHomeOverride);
        var effectiveHome = openClawHome is null
            ? Path.GetFullPath(osHome)
            : Path.GetFullPath(ExpandHomePrefix(openClawHome, osHome));
        return Path.GetFullPath(ExpandHomePrefix(stateDirOverride.Trim(), effectiveHome));
    }

    private static string ExpandHomePrefix(string path, string home) =>
        path == "~"
            ? home
            : path.StartsWith($"~{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || path.StartsWith($"~{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
                ? Path.Combine(home, path[2..])
                : path;

    private static string? NormalizePathValue(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed is "undefined" or "null" ? null : trimmed;
    }

    private static string? FirstUsablePathValue(params string?[] values)
    {
        foreach (var value in values)
        {
            var normalized = NormalizePathValue(value);
            if (normalized is not null) return normalized;
        }
        return null;
    }

    private static ExecApprovalsFile UnmigratedLegacyFallbackFile() =>
        new()
        {
            Version = 1,
            Defaults = new ExecApprovalsDefaults
            {
                Security = ExecSecurity.Deny,
                Ask = ExecAsk.Always,
                AskFallback = ExecSecurity.Deny,
            },
            Agents = [],
        };

    private static ExecApprovalsResolved UnmigratedLegacyFallback(string? agentId) =>
        ResolveFromFile(UnmigratedLegacyFallbackFile(), agentId);

    private async Task<string> SaveFileAsync(ExecApprovalsFile file)
    {
        var dir = Path.GetDirectoryName(_filePath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        // Refuse to write through a redirected path: a symlink/junction in the store path
        // (O_NOFOLLOW analogue) or a hard-linked target (nlink==1 analogue) could divert the
        // policy file to an attacker-observable or attacker-controlled location.
        if (!ExecApprovalsPathGuard.IsPathTrustworthy(_filePath))
        {
            _logger.Error($"[EXEC-APPROVALS] Refusing to write {_filePath}: reparse point in store path");
            throw new IOException("exec-approvals store path is not trustworthy (reparse point)");
        }

        if (File.Exists(_filePath) && !ExecApprovalsPathGuard.HasSingleHardLink(_filePath))
        {
            _logger.Error($"[EXEC-APPROVALS] Refusing to write {_filePath}: target has multiple hard links");
            throw new IOException("exec-approvals store target has multiple hard links");
        }

        var tmp = Path.Combine(dir, $".exec-approvals-{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(file, JsonOptions);
            await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
            // Atomic replace on NTFS via MoveFileExW (MOVEFILE_REPLACE_EXISTING).
            File.Move(tmp, _filePath, overwrite: true);
            return ComputeRawHash(json);
        }
        catch (Exception ex)
        {
            _logger.Error($"[EXEC-APPROVALS] Failed to save {_filePath} ({ex.Message})");
            // slopwatch-ignore: SW003 Cleanup is best-effort; failure cannot improve caller state and the original outcome is preserved.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    private ExecApprovalsSnapshot CreateSnapshot(
        ExecApprovalsFile file,
        bool exists,
        string hash)
    {
        return new ExecApprovalsSnapshot(
            _filePath,
            exists,
            hash,
            new ExecApprovalsFile
            {
                Version = 1,
                Socket = file.Socket,
                Defaults = WithResolvedDefaults(file.Defaults),
                Agents = file.Agents ?? [],
            });
    }

    private static string ComputeRawHash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

    private static string ComputeMissingHash() =>
        $"missing:{ComputeRawHash(string.Empty)}";

    private static ExecApprovalsFile NewDefaultFile() =>
        new()
        {
            Version = 1,
            Defaults = WithResolvedDefaults(null),
            Agents = [],
        };

    private static ExecApprovalsDefaults WithResolvedDefaults(ExecApprovalsDefaults? defaults) =>
        new()
        {
            Security = defaults?.Security ?? ExecSecurity.Allowlist,
            Ask = defaults?.Ask ?? ExecAsk.OnMiss,
            AskFallback = defaults?.AskFallback ?? ExecSecurity.Deny,
            AutoAllowSkills = defaults?.AutoAllowSkills ?? false,
        };

    private static ExecApprovalsSocketConfig? MergeSocket(
        ExecApprovalsSocketConfig? replacement,
        ExecApprovalsSocketConfig? current)
    {
        var path = replacement?.Path ?? current?.Path;
        var token = replacement?.Token ?? current?.Token;
        return path is null && token is null
            ? null
            : new ExecApprovalsSocketConfig { Path = path, Token = token };
    }

    // Best-effort mutate-and-save. Serialized by the store lock.
    // Never throws. Refuses to overwrite a malformed file.
    // Returns true if the file was mutated and saved; false if the mutate was a no-op,
    // the file was malformed/invalid, or any I/O failure occurred.
    private async Task<bool> UpdateFileAsync(Func<ExecApprovalsFile, bool> mutate)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Migrate before any write: creating the target file here would permanently
            // block TryMigrateLegacyFile and silently orphan the legacy configuration.
            if (TryMigrateLegacyFile() == LegacyMigrationStatus.Blocked)
            {
                _logger.Warn("[EXEC-APPROVALS] Refusing to write exec-approvals.json: "
                    + "unmigrated legacy file is unreadable");
                return false;
            }

            var result = LoadFile();
            if (result.Status == LoadFileStatus.Invalid)
            {
                _logger.Warn("[EXEC-APPROVALS] Refusing to write exec-approvals.json: "
                    + "file is malformed or has an unsupported version");
                return false;
            }
            var file = result.Status == LoadFileStatus.Loaded && result.File is not null
                ? result.File
                : new ExecApprovalsFile { Version = 1, Agents = [] };
            file.Agents ??= new Dictionary<string, ExecApprovalsAgent>();

            if (!mutate(file)) return false; // no-op: nothing to persist

            await SaveFileAsync(file).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // Any failure (incl. transient IOException on the atomic move) degrades to a
            // logged warning. The atomic write guarantees the file on disk is never left corrupt.
            _logger.Warn($"[EXEC-APPROVALS] exec-approvals.json write failed "
                + $"({ex.Message}); side effect skipped");
            return false;
        }
        finally
        {
            _lock.Release();
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
    //   matcher, not here. The flag is preserved for API symmetry with macOS.
    //
    // Identity is (pattern, argPattern). Deduplicating on pattern alone would drop a
    // bound rule that shares an executable with an unbound one, and rebuilding an entry
    // without its argPattern or source would turn a rule bound to one command into a
    // rule for the whole executable. Both are silent authorization widening, so every
    // field is carried.
    internal static List<ExecAllowlistEntry> NormalizeAllowlistEntries(
        IEnumerable<ExecAllowlistEntry> entries, bool dropInvalid)
    {
        var seen = new HashSet<(string Pattern, string ArgPattern)>();
        var result = new List<ExecAllowlistEntry>();
        foreach (var entry in entries)
        {
            var pattern = entry.Pattern?.Trim();
            if (string.IsNullOrEmpty(pattern)) continue;
            if (!seen.Add((pattern.ToLowerInvariant(), entry.ArgPattern ?? "\u0000"))) continue;
            result.Add(pattern == entry.Pattern ? entry : new ExecAllowlistEntry
            {
                Id = entry.Id,
                Pattern = pattern,
                ArgPattern = entry.ArgPattern,
                CommandText = entry.CommandText,
                Source = entry.Source,
                LastUsedAt = entry.LastUsedAt,
                LastResolvedPath = entry.LastResolvedPath,
                LastUsedCommand = entry.LastUsedCommand,
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
        var security = agentEntry?.Security ?? wildcardEntry?.Security ?? defaults?.Security ?? ExecSecurity.Allowlist;
        var ask = agentEntry?.Ask ?? wildcardEntry?.Ask ?? defaults?.Ask ?? ExecAsk.OnMiss;
        var askFallback = agentEntry?.AskFallback ?? wildcardEntry?.AskFallback ?? defaults?.AskFallback ?? ExecSecurity.Deny;
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
                Security = ExecSecurity.Allowlist,
                Ask = ExecAsk.OnMiss,
                AskFallback = ExecSecurity.Deny,
                AutoAllowSkills = false,
            },
            Allowlist = [],
        };

    private static ExecApprovalsResolved FailClosedResolved(string agentId) =>
        new()
        {
            AgentId = agentId,
            Defaults = new ExecApprovalsResolvedDefaults
            {
                Security = ExecSecurity.Deny,
                Ask = ExecAsk.Always,
                AskFallback = ExecSecurity.Deny,
                AutoAllowSkills = false,
            },
            Allowlist = [],
        };

    // null/empty agentId → "main". Mirrors macOS. Evaluator does not need to know this.
    private static string NormalizeAgentId(string? agentId) =>
        string.IsNullOrWhiteSpace(agentId) ? "main" : agentId;
}

internal sealed class ExecApprovalsValidationException(string message) : Exception(message);
