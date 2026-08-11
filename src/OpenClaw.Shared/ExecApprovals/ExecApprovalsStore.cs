using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.ExecApprovals;

public sealed class ExecApprovalsStore : IExecApprovalsPresentationStore, IDisposable
{
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
    private readonly SemaphoreSlim _observeLock = new(1, 1);
    private readonly object _changeGate = new();

    private EventHandler<ExecApprovalsChangedEventArgs>? _changed;
    private FileSystemWatcher? _watcher;
    private string? _watcherObservedPath;
    private ExecApprovalsSnapshot? _lastValidPresentationSnapshot;
    private string? _lastObservedSignature;
    private bool _lastObservedWasFailure;
    private long _changeSequence;
    private bool _disposed;

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
        UntrustedPath,
        UnsupportedVersion,
        MalformedJson,
        ReadFailed,
    }

    private readonly record struct LoadFileResult(
        LoadFileStatus Status,
        ExecApprovalsFile? File,
        string Hash,
        int? Version,
        string Message)
    {
        public bool IsInvalid => Status is not LoadFileStatus.Missing and not LoadFileStatus.Loaded;
    }

    private readonly record struct EnsureFileResult(
        ExecApprovalsFile File,
        ExecApprovalsSnapshot? PersistedSnapshot);

    private readonly record struct ReadOnlySnapshotLoadResult(
        ExecApprovalsSnapshot? Snapshot,
        ExecApprovalsSnapshotFailure? Failure,
        string Signature);

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

    public event EventHandler<ExecApprovalsChangedEventArgs>? Changed
    {
        add
        {
            ThrowIfDisposed();
            if (value is null)
            {
                return;
            }

            lock (_changeGate)
            {
                ThrowIfDisposed();
                _changed += value;
                EnsureWatcherStartedNoLock();
            }
        }
        remove
        {
            if (value is null)
            {
                return;
            }

            lock (_changeGate)
            {
                _changed -= value;
                if (_changed is null)
                {
                    DisposeWatcherNoLock();
                    _lastObservedSignature = null;
                    _lastObservedWasFailure = false;
                }
            }
        }
    }

    public ExecApprovalsResolved ResolveReadOnly(string? agentId)
    {
        ThrowIfDisposed();

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
        ThrowIfDisposed();

        var trimmed = pattern?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            _logger.Debug("[EXEC-APPROVALS] AddAllowlistEntry skipped: empty pattern");
            return false;
        }
        var normalizedArgPattern = string.IsNullOrWhiteSpace(argPattern) ? null : argPattern;
        var key = NormalizeAgentId(agentId);
        var alreadyPresent = false;
        var wrote = await UpdateFileAsync(file =>
        {
            var agents = file.Agents!;
            if (!agents.TryGetValue(key, out var agent) || agent is null)
            {
                agent = new ExecApprovalsAgent();
                agents[key] = agent;
            }

            var allowlist = agent.Allowlist ??= [];
            if (allowlist.Any(e => string.Equals(
                    e.Pattern?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.ArgPattern, normalizedArgPattern, StringComparison.Ordinal)))
            {
                alreadyPresent = true;
                return false;
            }

            allowlist.Add(new ExecAllowlistEntry
            {
                Id = Guid.NewGuid(),
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
        ThrowIfDisposed();

        if (string.IsNullOrEmpty(pattern))
        {
            return Task.FromResult(false);
        }

        var key = NormalizeAgentId(agentId);
        var buckets = key == "*" ? ["*"] : new[] { key, "*" };
        return UpdateFileAsync(file =>
        {
            var changed = false;
            foreach (var bucketKey in buckets)
            {
                if (!file.Agents!.TryGetValue(bucketKey, out var agent) || agent?.Allowlist is null)
                {
                    continue;
                }

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
        ThrowIfDisposed();

        ExecApprovalsResolved resolved;
        ExecApprovalsChangedEventArgs? change = null;

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var result = await EnsureFileAsync().ConfigureAwait(false);
            resolved = ResolveFromFile(result.File, agentId);
            if (result.PersistedSnapshot is not null)
            {
                change = RecordManagedSnapshot(result.PersistedSnapshot, origin: null);
            }
        }
        finally
        {
            _lock.Release();
        }

        RaiseChanged(change);
        return resolved;
    }

    public void MigrateLegacyFileIfNeeded()
    {
        ThrowIfDisposed();

        if (TryMigrateLegacyFile() != LegacyMigrationStatus.Migrated)
        {
            return;
        }

        var readOnly = LoadReadOnlySnapshot();
        if (readOnly.Snapshot is not null && readOnly.Failure is null)
        {
            RaiseChanged(RecordManagedSnapshot(readOnly.Snapshot, origin: null));
        }
    }

    public async Task<ExecApprovalsSnapshot> GetSnapshotAsync()
    {
        ThrowIfDisposed();

        ExecApprovalsSnapshot snapshot;
        ExecApprovalsChangedEventArgs? change = null;

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (TryMigrateLegacyFile() == LegacyMigrationStatus.Blocked)
            {
                throw new IOException("Unmigrated exec approvals file is unreadable.");
            }

            var result = LoadFile();
            if (result.IsInvalid)
            {
                throw new IOException("Exec approvals file is malformed, unsupported, or untrusted.");
            }

            if (result.Status == LoadFileStatus.Missing)
            {
                var file = NewDefaultFile();
                var savedHash = await SaveFileAsync(file).ConfigureAwait(false);
                snapshot = CreateSnapshot(file, exists: true, savedHash);
                change = RecordManagedSnapshot(snapshot, origin: null);
            }
            else if (result.File is not null)
            {
                snapshot = CreateSnapshot(result.File, exists: true, result.Hash);
            }
            else
            {
                throw new IOException("Exec approvals snapshot is unavailable.");
            }
        }
        finally
        {
            _lock.Release();
        }

        RaiseChanged(change);
        return snapshot;
    }

    public Task<ExecApprovalsReadOnlySnapshotResult> GetSnapshotReadOnlyAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var readOnly = LoadReadOnlySnapshot();
        ExecApprovalsReadOnlySnapshotResult result;

        lock (_changeGate)
        {
            ThrowIfDisposed();
            if (_changed is null || _lastObservedSignature is null)
            {
                _lastObservedSignature = readOnly.Signature;
                _lastObservedWasFailure = readOnly.Failure is not null;
            }

            if (readOnly.Snapshot is not null)
            {
                _lastValidPresentationSnapshot = CloneSnapshot(readOnly.Snapshot);
                result = new ExecApprovalsReadOnlySnapshotResult(
                    CloneSnapshot(readOnly.Snapshot),
                    null,
                    null);
            }
            else
            {
                result = new ExecApprovalsReadOnlySnapshotResult(
                    null,
                    readOnly.Failure,
                    CloneSnapshotOrNull(_lastValidPresentationSnapshot));
            }
        }

        return Task.FromResult(result);
    }

    public ExecApprovalsWriterOrigin CreateWriterOrigin()
    {
        ThrowIfDisposed();
        return new ExecApprovalsWriterOrigin();
    }

    public Task<ExecApprovalsSnapshot?> ReplaceAsync(
        string baseHash,
        ExecApprovalsFile replacement,
        Func<ExecApprovalsFile, ExecApprovalsFile, string?>? deltaValidator = null)
        => ReplaceAsync(baseHash, replacement, origin: null, deltaValidator);

    public async Task<ExecApprovalsSnapshot?> ReplaceAsync(
        string baseHash,
        ExecApprovalsFile replacement,
        ExecApprovalsWriterOrigin? origin,
        Func<ExecApprovalsFile, ExecApprovalsFile, string?>? deltaValidator = null)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(baseHash);
        ArgumentNullException.ThrowIfNull(replacement);

        ExecApprovalsSnapshot? snapshot = null;
        ExecApprovalsChangedEventArgs? change = null;

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (TryMigrateLegacyFile() == LegacyMigrationStatus.Blocked)
            {
                throw new IOException("Unmigrated exec approvals file is unreadable.");
            }

            var result = LoadFile();
            if (result.IsInvalid)
            {
                throw new IOException("Exec approvals file is malformed, unsupported, or untrusted.");
            }

            var currentHash = result.Hash;
            if (!string.Equals(baseHash.Trim(), currentHash, StringComparison.Ordinal))
            {
                return null;
            }

            var currentFile = result.File ?? NewDefaultFile();
            var validationError = deltaValidator?.Invoke(currentFile, replacement);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                throw new ExecApprovalsValidationException(validationError);
            }

            var currentSocket = result.File?.Socket;
            var normalized = Normalize(replacement);
            normalized.Version = 1;
            normalized.Defaults = WithResolvedDefaults(normalized.Defaults);
            normalized.Agents ??= [];
            normalized.Socket = MergeSocket(normalized.Socket, currentSocket);

            var savedHash = await SaveFileAsync(normalized).ConfigureAwait(false);
            snapshot = CreateSnapshot(normalized, exists: true, savedHash);
            change = RecordManagedSnapshot(snapshot, origin);
        }
        finally
        {
            _lock.Release();
        }

        RaiseChanged(change);
        return snapshot;
    }

    public void Dispose()
    {
        lock (_changeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _changed = null;
            DisposeWatcherNoLock();
        }

        _observeLock.Dispose();
        _lock.Dispose();
    }

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
                return new LoadFileResult(
                    LoadFileStatus.UntrustedPath,
                    null,
                    ComputeFailureSignature("untrusted-path", Path.GetFullPath(filePath)),
                    Version: null,
                    "Exec approvals path is a directory.");
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
            return new LoadFileResult(
                LoadFileStatus.ReadFailed,
                null,
                ComputeFailureSignature("read-failed", $"{ex.GetType().FullName}:{ex.Message}"),
                Version: null,
                $"Failed to inspect exec approvals file: {ex.Message}");
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
            return new LoadFileResult(
                LoadFileStatus.UntrustedPath,
                null,
                ComputeFailureSignature("untrusted-path", Path.GetFullPath(filePath)),
                Version: null,
                "Exec approvals file path is not trustworthy.");
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var hash = ComputeRawHash(json);
            try
            {
                var file = JsonSerializer.Deserialize<ExecApprovalsFile>(json, JsonOptions);
                if (file is null)
                {
                    _logger.Warn("[EXEC-APPROVALS] exec-approvals.json deserialized to null; applying default-deny");
                    return new LoadFileResult(
                        LoadFileStatus.MalformedJson,
                        null,
                        hash,
                        Version: null,
                        "Exec approvals file deserialized to null.");
                }

                if (file.Version != 1)
                {
                    var version = file.Version?.ToString() ?? "missing";
                    _logger.Warn($"[EXEC-APPROVALS] exec-approvals.json has unsupported version {version}; applying default-deny");
                    return new LoadFileResult(
                        LoadFileStatus.UnsupportedVersion,
                        null,
                        hash,
                        file.Version,
                        $"Unsupported exec approvals version {version}.");
                }

                return new LoadFileResult(
                    LoadFileStatus.Loaded,
                    Normalize(file),
                    hash,
                    Version: 1,
                    string.Empty);
            }
            catch (JsonException ex)
            {
                _logger.Warn($"[EXEC-APPROVALS] exec-approvals.json is malformed ({ex.Message}); applying default-deny");
                return new LoadFileResult(
                    LoadFileStatus.MalformedJson,
                    null,
                    hash,
                    Version: null,
                    $"Exec approvals file is malformed: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"[EXEC-APPROVALS] Failed to load exec-approvals.json ({ex.Message}); applying default-deny");
            return new LoadFileResult(
                LoadFileStatus.ReadFailed,
                null,
                ComputeFailureSignature("read-failed", $"{ex.GetType().FullName}:{ex.Message}"),
                Version: null,
                $"Failed to load exec approvals file: {ex.Message}");
        }
    }

    private LoadFileResult MissingOrUntrusted(string filePath)
    {
        if (ExecApprovalsPathGuard.IsPathTrustworthy(filePath))
        {
            return new LoadFileResult(
                LoadFileStatus.Missing,
                null,
                ComputeMissingHash(),
                Version: null,
                string.Empty);
        }

        _logger.Warn("[EXEC-APPROVALS] missing exec-approvals.json path is not trustworthy; applying default-deny");
        return new LoadFileResult(
            LoadFileStatus.UntrustedPath,
            null,
            ComputeFailureSignature("untrusted-path", Path.GetFullPath(filePath)),
            Version: null,
            "Missing exec approvals file path is not trustworthy.");
    }

    private async Task<EnsureFileResult> EnsureFileAsync()
    {
        if (TryMigrateLegacyFile() == LegacyMigrationStatus.Blocked)
        {
            return new EnsureFileResult(UnmigratedLegacyFallbackFile(), PersistedSnapshot: null);
        }

        var result = LoadFile();
        if (result.Status == LoadFileStatus.Loaded && result.File is not null)
        {
            var file = result.File;
            if (file.Agents is null)
            {
                file = new ExecApprovalsFile
                {
                    Version = file.Version,
                    Socket = CloneSocket(file.Socket),
                    Defaults = CopyDefaults(file.Defaults),
                    Agents = [],
                };
                var savedHash = await SaveFileAsync(file).ConfigureAwait(false);
                return new EnsureFileResult(file, CreateSnapshot(file, exists: true, savedHash));
            }

            return new EnsureFileResult(file, PersistedSnapshot: null);
        }

        if (result.IsInvalid)
        {
            _logger.Warn($"[EXEC-APPROVALS] Preserving unreadable exec-approvals.json at {_filePath}; using empty in-memory store");
            return new EnsureFileResult(UnmigratedLegacyFallbackFile(), PersistedSnapshot: null);
        }

        var newFile = NewDefaultFile();
        var hash = await SaveFileAsync(newFile).ConfigureAwait(false);
        _logger.Info($"[EXEC-APPROVALS] Created {_filePath}");
        return new EnsureFileResult(newFile, CreateSnapshot(newFile, exists: true, hash));
    }

    private LegacyMigrationStatus TryMigrateLegacyFile()
    {
        if (_legacyFilePath is null)
        {
            return LegacyMigrationStatus.NotNeeded;
        }

        var targetResult = LoadFile();
        if (targetResult.Status == LoadFileStatus.Loaded)
            return LegacyMigrationStatus.NotNeeded;
        if (targetResult.IsInvalid)
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
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }

            return LegacyMigrationStatus.NotNeeded;
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }

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
            if (normalized is not null)
            {
                return normalized;
            }
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
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

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
            File.Move(tmp, _filePath, overwrite: true);
            return ComputeRawHash(json);
        }
        catch (Exception ex)
        {
            _logger.Error($"[EXEC-APPROVALS] Failed to save {_filePath} ({ex.Message})");
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
            }

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
            CloneFileForSnapshot(file));
    }

    private static string ComputeRawHash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

    private static string ComputeMissingHash() =>
        $"missing:{ComputeRawHash(string.Empty)}";

    private static string ComputeFailureSignature(string prefix, string value) =>
        $"{prefix}:{ComputeRawHash(value)}";

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
            : new ExecApprovalsSocketConfig
            {
                Path = path,
                Token = token,
            };
    }

    private async Task<bool> UpdateFileAsync(Func<ExecApprovalsFile, bool> mutate)
    {
        ExecApprovalsChangedEventArgs? change = null;
        var wrote = false;

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (TryMigrateLegacyFile() == LegacyMigrationStatus.Blocked)
            {
                _logger.Warn("[EXEC-APPROVALS] Refusing to write exec-approvals.json: unmigrated legacy file is unreadable");
                return false;
            }

            var result = LoadFile();
            if (result.IsInvalid)
            {
                _logger.Warn("[EXEC-APPROVALS] Refusing to write exec-approvals.json: file is malformed or has an unsupported version");
                return false;
            }

            var file = result.Status == LoadFileStatus.Loaded && result.File is not null
                ? result.File
                : new ExecApprovalsFile { Version = 1, Agents = [] };
            file.Agents ??= [];

            if (!mutate(file))
            {
                return false;
            }

            var savedHash = await SaveFileAsync(file).ConfigureAwait(false);
            change = RecordManagedSnapshot(CreateSnapshot(file, exists: true, savedHash), origin: null);
            wrote = true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"[EXEC-APPROVALS] exec-approvals.json write failed ({ex.Message}); side effect skipped");
            wrote = false;
        }
        finally
        {
            _lock.Release();
        }

        RaiseChanged(change);
        return wrote;
    }

    private static ExecApprovalsFile Normalize(ExecApprovalsFile file)
    {
        var socket = file.Socket is null ? null : NormalizeSocket(file.Socket);
        var defaults = CopyDefaults(file.Defaults);

        if (file.Agents is null)
        {
            return new ExecApprovalsFile
            {
                Version = 1,
                Socket = socket,
                Defaults = defaults,
                Agents = null,
            };
        }

        var agents = new Dictionary<string, ExecApprovalsAgent>(file.Agents);

        if (agents.TryGetValue("default", out var defaultAgent))
        {
            agents.Remove("default");
            agents["main"] = agents.TryGetValue("main", out var mainAgent)
                ? MergeAgent(defaultAgent, mainAgent)
                : defaultAgent;
        }

        foreach (var key in agents.Keys.ToList())
        {
            var agent = agents[key];
            if (agent.Allowlist is not null)
            {
                agents[key] = WithNormalizedAllowlist(agent, dropInvalid: false);
            }
        }

        return new ExecApprovalsFile
        {
            Version = 1,
            Socket = socket,
            Defaults = defaults,
            Agents = agents,
        };
    }

    private static ExecApprovalsDefaults? CopyDefaults(ExecApprovalsDefaults? defaults) =>
        defaults is null
            ? null
            : new ExecApprovalsDefaults
            {
                Security = defaults.Security,
                Ask = defaults.Ask,
                AskFallback = defaults.AskFallback,
                AutoAllowSkills = defaults.AutoAllowSkills,
            };

    private static ExecApprovalsSocketConfig? NormalizeSocket(ExecApprovalsSocketConfig socket)
    {
        var path = string.IsNullOrWhiteSpace(socket.Path) ? null : socket.Path.Trim();
        var token = string.IsNullOrWhiteSpace(socket.Token) ? null : socket.Token.Trim();
        return path is null && token is null
            ? null
            : new ExecApprovalsSocketConfig
            {
                Path = path,
                Token = token,
            };
    }

    private static ExecApprovalsAgent MergeAgent(ExecApprovalsAgent fallback, ExecApprovalsAgent winner)
    {
        var allowlist = new List<ExecAllowlistEntry>();
        if (fallback.Allowlist is not null)
        {
            allowlist.AddRange(fallback.Allowlist);
        }

        if (winner.Allowlist is not null)
        {
            allowlist.AddRange(winner.Allowlist);
        }

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
            Allowlist = NormalizeAllowlistEntries(agent.Allowlist!, dropInvalid) is { Count: > 0 } list ? list : null,
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
        IEnumerable<ExecAllowlistEntry> entries,
        bool dropInvalid)
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

        var combined = new List<ExecAllowlistEntry>();
        if (wildcardEntry?.Allowlist is not null)
        {
            combined.AddRange(wildcardEntry.Allowlist);
        }

        if (agentEntry?.Allowlist is not null)
        {
            combined.AddRange(agentEntry.Allowlist);
        }

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

    private ReadOnlySnapshotLoadResult LoadReadOnlySnapshot()
    {
        LoadFileResult result;
        if (_legacyFilePath is not null)
        {
            result = LoadFile();
            var legacyStatus = LoadFile(_legacyFilePath).Status;
            if (result.Status == LoadFileStatus.Missing
                && legacyStatus != LoadFileStatus.Missing)
            {
                var failure = new ExecApprovalsSnapshotFailure(
                    ExecApprovalsSnapshotFailureKind.LegacyMigrationRequired,
                    ComputeFailureSignature("legacy-migration-required", Path.GetFullPath(_legacyFilePath)),
                    Version: null,
                    "Legacy exec approvals must be migrated before a read-only snapshot is available.");
                return new ReadOnlySnapshotLoadResult(null, failure, failure.Hash);
            }
        }
        else
        {
            result = LoadFile();
        }

        if (result.Status == LoadFileStatus.Missing)
        {
            var snapshot = CreateSnapshot(NewDefaultFile(), exists: false, result.Hash);
            return new ReadOnlySnapshotLoadResult(snapshot, null, snapshot.Hash);
        }

        if (result.Status == LoadFileStatus.Loaded && result.File is not null)
        {
            var snapshot = CreateSnapshot(result.File, exists: true, result.Hash);
            return new ReadOnlySnapshotLoadResult(snapshot, null, snapshot.Hash);
        }

        var typedFailure = CreateFailure(result);
        return new ReadOnlySnapshotLoadResult(null, typedFailure, typedFailure.Hash);
    }

    private static ExecApprovalsSnapshotFailure CreateFailure(LoadFileResult result)
    {
        var kind = result.Status switch
        {
            LoadFileStatus.UntrustedPath => ExecApprovalsSnapshotFailureKind.UntrustedPath,
            LoadFileStatus.UnsupportedVersion => ExecApprovalsSnapshotFailureKind.UnsupportedVersion,
            LoadFileStatus.MalformedJson => ExecApprovalsSnapshotFailureKind.MalformedJson,
            LoadFileStatus.ReadFailed => ExecApprovalsSnapshotFailureKind.ReadFailed,
            _ => throw new InvalidOperationException($"Unsupported failure status {result.Status}."),
        };

        return new ExecApprovalsSnapshotFailure(kind, result.Hash, result.Version, result.Message);
    }

    private ExecApprovalsChangedEventArgs? RecordManagedSnapshot(
        ExecApprovalsSnapshot snapshot,
        ExecApprovalsWriterOrigin? origin)
    {
        lock (_changeGate)
        {
            if (_disposed)
            {
                return null;
            }

            var snapshotCopy = CloneSnapshot(snapshot);
            var kind = _lastObservedWasFailure
                ? ExecApprovalsChangeKind.SnapshotRecovered
                : ExecApprovalsChangeKind.SnapshotUpdated;

            _lastObservedWasFailure = false;
            _lastValidPresentationSnapshot = CloneSnapshot(snapshot);
            if (_changed is null)
            {
                _lastObservedSignature = null;
                return null;
            }

            _lastObservedSignature = snapshot.Hash;
            EnsureWatcherStartedNoLock();

            return new ExecApprovalsChangedEventArgs(
                ++_changeSequence,
                kind,
                snapshotCopy.Hash,
                snapshotCopy.File.Version,
                snapshotCopy,
                failure: null,
                lastValidSnapshot: null,
                origin);
        }
    }

    private void EnsureWatcherStartedNoLock()
    {
        while (!_disposed && _changed is not null && _watcher is null)
        {
            var targetDirectoryPath = Path.GetDirectoryName(_filePath)!;
            string watcherDirectoryPath;
            string observedPath;
            NotifyFilters notifyFilter;
            if (Directory.Exists(targetDirectoryPath))
            {
                watcherDirectoryPath = targetDirectoryPath;
                observedPath = _filePath;
                notifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size;
            }
            else
            {
                observedPath = targetDirectoryPath;
                while (true)
                {
                    var parentPath = Path.GetDirectoryName(observedPath);
                    if (string.IsNullOrWhiteSpace(parentPath))
                    {
                        return;
                    }

                    if (Directory.Exists(parentPath))
                    {
                        watcherDirectoryPath = parentPath;
                        notifyFilter = NotifyFilters.DirectoryName;
                        break;
                    }

                    observedPath = parentPath;
                }
            }

            try
            {
                _watcher = new FileSystemWatcher(watcherDirectoryPath, Path.GetFileName(observedPath))
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = notifyFilter,
                };
                _watcherObservedPath = observedPath;
                _watcher.Changed += OnWatcherChanged;
                _watcher.Created += OnWatcherChanged;
                _watcher.Deleted += OnWatcherChanged;
                _watcher.Renamed += OnWatcherRenamed;
                _watcher.Error += OnWatcherError;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                _logger.Warn($"[EXEC-APPROVALS] Failed to observe exec-approvals.json changes ({ex.Message})");
                DisposeWatcherNoLock();
                return;
            }

            if (PathsEqual(observedPath, _filePath) || !Directory.Exists(observedPath))
            {
                return;
            }

            DisposeWatcherNoLock();
        }
    }

    private void DisposeWatcherNoLock()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnWatcherChanged;
        _watcher.Created -= OnWatcherChanged;
        _watcher.Deleted -= OnWatcherChanged;
        _watcher.Renamed -= OnWatcherRenamed;
        _watcher.Error -= OnWatcherError;
        _watcher.Dispose();
        _watcher = null;
        _watcherObservedPath = null;
    }

    private void OnWatcherChanged(object sender, FileSystemEventArgs e)
    {
        HandleWatcherPathChange(e.FullPath, oldFullPath: null);
    }

    private void OnWatcherRenamed(object sender, RenamedEventArgs e)
    {
        HandleWatcherPathChange(e.FullPath, e.OldFullPath);
    }

    private void HandleWatcherPathChange(string fullPath, string? oldFullPath)
    {
        var shouldObserve = false;
        lock (_changeGate)
        {
            if (_disposed
                || _changed is null
                || _watcherObservedPath is null
                || (!PathsEqual(fullPath, _watcherObservedPath)
                    && (oldFullPath is null || !PathsEqual(oldFullPath, _watcherObservedPath))))
            {
                return;
            }

            if (!PathsEqual(_watcherObservedPath, _filePath))
            {
                DisposeWatcherNoLock();
                EnsureWatcherStartedNoLock();
            }

            shouldObserve = Directory.Exists(Path.GetDirectoryName(_filePath)!);
        }

        if (shouldObserve)
        {
            QueueWatcherObservation();
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var message = e.GetException()?.Message ?? "unknown watcher error";
        _logger.Warn($"[EXEC-APPROVALS] exec-approvals.json watcher failed ({message})");
        QueueWatcherObservation();
    }

    private void QueueWatcherObservation()
    {
        lock (_changeGate)
        {
            if (_disposed || _changed is null)
            {
                return;
            }
        }

        _ = Task.Run(ObserveExternalChangeAsync);
    }

    private async Task ObserveExternalChangeAsync()
    {
        var storeLockHeld = false;
        try
        {
            await _observeLock.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        ExecApprovalsChangedEventArgs? change = null;
        try
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            storeLockHeld = true;
            var readOnly = await LoadObservedSnapshotAsync().ConfigureAwait(false);
            lock (_changeGate)
            {
                if (_disposed || _changed is null)
                {
                    return;
                }

                if (string.Equals(_lastObservedSignature, readOnly.Signature, StringComparison.Ordinal))
                {
                    return;
                }

                _lastObservedSignature = readOnly.Signature;
                if (readOnly.Snapshot is not null)
                {
                    var snapshot = CloneSnapshot(readOnly.Snapshot);
                    var kind = _lastObservedWasFailure
                        ? ExecApprovalsChangeKind.SnapshotRecovered
                        : ExecApprovalsChangeKind.SnapshotUpdated;
                    _lastObservedWasFailure = false;
                    _lastValidPresentationSnapshot = CloneSnapshot(snapshot);
                    change = new ExecApprovalsChangedEventArgs(
                        ++_changeSequence,
                        kind,
                        snapshot.Hash,
                        snapshot.File.Version,
                        snapshot,
                        failure: null,
                        lastValidSnapshot: null,
                        origin: null);
                }
                else if (readOnly.Failure is not null)
                {
                    _lastObservedWasFailure = true;
                    change = new ExecApprovalsChangedEventArgs(
                        ++_changeSequence,
                        ExecApprovalsChangeKind.SnapshotInvalid,
                        readOnly.Failure.Hash,
                        readOnly.Failure.Version,
                        snapshot: null,
                        readOnly.Failure,
                        CloneSnapshotOrNull(_lastValidPresentationSnapshot),
                        origin: null);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"[EXEC-APPROVALS] Failed to observe exec-approvals.json changes ({ex.Message})");
        }
        finally
        {
            if (storeLockHeld)
            {
                try
                {
                    _lock.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            try
            {
                _observeLock.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        RaiseChanged(change);
    }

    private async Task<ReadOnlySnapshotLoadResult> LoadObservedSnapshotAsync()
    {
        var readOnly = LoadReadOnlySnapshot();
        for (var attempt = 1;
             attempt <= 3 && readOnly.Failure?.Kind == ExecApprovalsSnapshotFailureKind.ReadFailed;
             attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(attempt * 25)).ConfigureAwait(false);
            readOnly = LoadReadOnlySnapshot();
        }

        return readOnly;
    }

    private void RaiseChanged(ExecApprovalsChangedEventArgs? args)
    {
        if (args is null)
        {
            return;
        }

        Delegate[] handlers;
        lock (_changeGate)
        {
            handlers = _changed?.GetInvocationList() ?? [];
        }

        foreach (EventHandler<ExecApprovalsChangedEventArgs> handler in handlers)
        {
            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                _logger.Warn($"[EXEC-APPROVALS] Changed subscriber {handler.Method.DeclaringType?.Name}.{handler.Method.Name} threw ({ex.Message})");
            }
        }
    }

    private static ExecApprovalsSnapshot? CloneSnapshotOrNull(ExecApprovalsSnapshot? snapshot) =>
        snapshot is null ? null : CloneSnapshot(snapshot);

    private static ExecApprovalsSnapshot CloneSnapshot(ExecApprovalsSnapshot snapshot) =>
        new(snapshot.Path, snapshot.Exists, snapshot.Hash, CloneFileForSnapshot(snapshot.File));

    private static ExecApprovalsFile CloneFileForSnapshot(ExecApprovalsFile file) =>
        new()
        {
            Version = 1,
            Socket = CloneSocket(file.Socket),
            Defaults = WithResolvedDefaults(file.Defaults),
            Agents = CloneAgents(file.Agents) ?? [],
        };

    private static Dictionary<string, ExecApprovalsAgent>? CloneAgents(
        Dictionary<string, ExecApprovalsAgent>? agents)
    {
        if (agents is null)
        {
            return null;
        }

        return agents.ToDictionary(
            pair => pair.Key,
            pair => CloneAgent(pair.Value),
            StringComparer.Ordinal);
    }

    private static ExecApprovalsAgent CloneAgent(ExecApprovalsAgent agent) =>
        new()
        {
            Security = agent.Security,
            Ask = agent.Ask,
            AskFallback = agent.AskFallback,
            AutoAllowSkills = agent.AutoAllowSkills,
            Allowlist = agent.Allowlist?.Select(CloneAllowlistEntry).ToList(),
        };

    private static ExecAllowlistEntry CloneAllowlistEntry(ExecAllowlistEntry entry) =>
        new()
        {
            Id = entry.Id,
            Pattern = entry.Pattern,
            ArgPattern = entry.ArgPattern,
            CommandText = entry.CommandText,
            Source = entry.Source,
            LastUsedAt = entry.LastUsedAt,
            LastResolvedPath = entry.LastResolvedPath,
            LastUsedCommand = entry.LastUsedCommand,
        };

    private static ExecApprovalsSocketConfig? CloneSocket(ExecApprovalsSocketConfig? socket) =>
        socket is null
            ? null
            : new ExecApprovalsSocketConfig
            {
                Path = socket.Path,
                Token = socket.Token,
            };

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ExecApprovalsStore));
        }
    }
}

internal sealed class ExecApprovalsValidationException(string message) : Exception(message);
