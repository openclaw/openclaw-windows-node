using System.Text.Json;
using System.Threading.Channels;
using OpenClaw.Shared.Mxc;
using OpenClaw.Shared.Telemetry;

namespace OpenClaw.Shared.FileTransfer;

/// <summary>App-lifetime scheduling and private inventory only. All target scans and deletions execute in a newly admitted worker.</summary>
public sealed class UploadRetentionCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    private readonly IUploadRetentionOperations _operations;
    private readonly string _inventoryPath;
    private readonly IOpenClawLogger _logger;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, ScheduledRoot> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _blockedRoots = new(StringComparer.OrdinalIgnoreCase);
    private Task? _loop;
    private Timer? _timer;
    private bool _started;
    private bool _disposed;
    private volatile bool _inventoryValid = true;
    private sealed record ScheduledRoot(UploadRootIdentity? Identity, DateTimeOffset Due, int Uploads = 0);
    public string? BlockedReason { get; private set; }
    public event Action? Changed;

    public UploadRetentionCoordinator(IUploadRetentionOperations operations, string inventoryPath,
        IOpenClawLogger? logger = null)
    {
        _operations = operations;
        _inventoryPath = inventoryPath;
        _logger = logger ?? NullLogger.Instance;
        try
        {
            foreach (var root in ReadInventory())
                _roots.Add(root.Path, new(string.IsNullOrEmpty(root.Device) ? null : root, DateTimeOffset.MinValue));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            BlockedReason = "Upload cleanup inventory could not be recovered. Review the private inventory before retrying.";
            _inventoryValid = false;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
            _operations.RetentionChanged += OnRetentionChanged;
            _operations.UploadSettled += OnUploadSettled;
            _operations.UploadAdmission = AdmitUploadRoot;
            _timer = new Timer(_ => Refresh(), null, TickInterval, TickInterval);
            _loop = Task.Run(RunAsync);
        }
        if (BlockedReason is not null) PublishChanged();
        Refresh();
    }

    public void Refresh(bool recoverRoots = false)
    {
        if (recoverRoots)
        {
            lock (_gate)
                foreach (var path in _roots.Keys.ToArray())
                    _roots[path] = _roots[path] with { Due = DateTimeOffset.MinValue };
        }
        _wake.Writer.TryWrite(true);
    }

    private UploadRootIdentity? AdmitUploadRoot(string path)
    {
        var failed = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_inventoryValid) throw new FileCommandException("UPLOAD_CLEANUP_BLOCKED", BlockedReason!);
            if (_roots.TryGetValue(path, out var entry) && entry.Identity is not null)
            {
                _roots[path] = entry with { Uploads = entry.Uploads + 1 };
                return entry.Identity;
            }
            if (entry is null && _roots.Count >= FileLimits.RetainedRoots)
                throw new FileCommandException("UPLOAD_QUOTA", "Restore cleanup access to an old staging root before choosing another.");
            // Persist the exact location before mutation, including roots whose worker identity
            // has not yet been returned. Inventory never authorizes filesystem access.
            failed = !SaveInventory(new(new(path, "", ""), 1, 0, null));
            if (!failed)
                _roots[path] = new(null, DateTimeOffset.UtcNow + FileLimits.RetentionRetry, (entry?.Uploads ?? 0) + 1);
        }
        if (failed)
        {
            PublishChanged();
            throw new FileCommandException("UPLOAD_CLEANUP_BLOCKED", BlockedReason!);
        }
        return null;
    }

    private void OnRetentionChanged(UploadRetentionState state)
    {
        lock (_gate)
        {
            if (!_roots.ContainsKey(state.Root.Path) && _roots.Count >= FileLimits.RetainedRoots)
            {
                BlockedReason = "Upload cleanup has reached its remembered-root limit. Restore access to old staging roots.";
            }
            else
            {
                var uploads = _roots.TryGetValue(state.Root.Path, out var entry) ? entry.Uploads : 0;
                _roots[state.Root.Path] = new(state.Root, state.NextExpiry ?? DateTimeOffset.MaxValue, uploads);
                SaveInventory(uploads > 0 && state.Count == 0 ? state with { Count = 1 } : state);
            }
        }
        PublishChanged();
        Refresh();
    }

    private void OnUploadSettled(string path)
    {
        lock (_gate)
        {
            if (!_roots.TryGetValue(path, out var entry) || entry.Uploads == 0) return;
            _roots[path] = entry with { Uploads = entry.Uploads - 1, Due = DateTimeOffset.MinValue };
        }
        Refresh();
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var _ in _wake.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                if (!_inventoryValid) continue;
                try
                {
                    KeyValuePair<string, ScheduledRoot>[] due;
                    lock (_gate) due = _roots.Where(p => p.Value.Uploads == 0 && p.Value.Due <= DateTimeOffset.UtcNow).ToArray();
                    foreach (var (path, entry) in due)
                    {
                        var result = await _operations.RecoverUploadsAsync(path, entry.Identity, _stop.Token).ConfigureAwait(false);
                        lock (_gate)
                        {
                            if (!_roots.TryGetValue(path, out var current) || !ReferenceEquals(current, entry)) continue;
                            if (result.Outcome.Ok && result.Retention is { } state)
                            {
                                var saved = SaveInventory(state);
                                if (state.Count == 0 && saved) _roots.Remove(path);
                                else _roots[path] = new(state.Root, state.NextExpiry ?? DateTimeOffset.UtcNow + FileLimits.RetentionRetry);
                                if (saved)
                                {
                                    _blockedRoots.Remove(path);
                                    BlockedReason = _blockedRoots.Values.FirstOrDefault();
                                }
                            }
                            else
                            {
                                // An interrupted admission may have reserved a location before creation.
                                if (entry.Identity is null && result.Outcome.Code == "NOT_FOUND")
                                {
                                    if (SaveInventory(new(new(path, "", ""), 0, 0, null))) _roots.Remove(path);
                                }
                                else
                                {
                                    _roots[path] = entry with { Due = DateTimeOffset.UtcNow + FileLimits.RetentionRetry };
                                    BlockedReason = result.Outcome.Message ?? "Upload cleanup is blocked by the current policy.";
                                    _blockedRoots[path] = BlockedReason;
                                }
                            }
                        }
                        PublishChanged();
                    }
                }
                catch (Exception ex) when (ex is FileCommandException or NodeSandboxExecutionException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    lock (_gate)
                    {
                        BlockedReason = "Upload cleanup is blocked by policy or private-state access. Completed uploads must not be retried automatically.";
                        foreach (var key in _roots.Keys.ToArray())
                            if (_roots[key].Due <= DateTimeOffset.UtcNow)
                            {
                                _blockedRoots[key] = BlockedReason;
                                _roots[key] = _roots[key] with { Due = DateTimeOffset.UtcNow + FileLimits.RetentionRetry };
                            }
                    }
                    PublishChanged();
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void PublishChanged()
    {
        foreach (Action observer in Changed?.GetInvocationList() ?? [])
        {
            try { observer(); }
            catch (Exception ex)
            {
                _logger.Warn($"[files] operation=cleanup-notification outcome=failed error={ex.GetType().Name}");
            }
        }
    }

    private UploadRootIdentity[] ReadInventory()
    {
        if (!File.Exists(_inventoryPath)) return [];
        using var stream = File.OpenRead(_inventoryPath);
        if (stream.Length > FileLimits.MetadataBytes) throw new IOException("Upload root inventory is oversized.");
        var roots = JsonSerializer.Deserialize<UploadRootIdentity[]>(stream, FileJson.Options) ?? throw new JsonException();
        if (roots.Length > FileLimits.RetainedRoots ||
            roots.Any(r => r is null || string.IsNullOrEmpty(r.Path) || !Path.IsPathFullyQualified(r.Path) ||
                r.Device is null || r.Inode is null || string.IsNullOrEmpty(r.Device) != string.IsNullOrEmpty(r.Inode)) ||
            roots.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != roots.Length)
            throw new JsonException("Invalid upload root inventory.");
        return roots;
    }

    private bool SaveInventory(UploadRetentionState update)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_inventoryPath)!);
            using var lease = PersistenceFileLease.Acquire(_inventoryPath);
            var inventory = ReadInventory();
            if (inventory.Any(r => r.Path.Equals(update.Root.Path, StringComparison.OrdinalIgnoreCase) &&
                r.Device.Length > 0 && r != update.Root && update.Root.Device.Length > 0))
                throw new IOException("A remembered upload root identity changed.");
            var roots = inventory.Where(r => !r.Path.Equals(update.Root.Path, StringComparison.OrdinalIgnoreCase)).ToList();
            if (update.Count > 0)
                roots.Add(update.Root.Device.Length == 0
                    ? inventory.FirstOrDefault(r => r.Path.Equals(update.Root.Path, StringComparison.OrdinalIgnoreCase)) ?? update.Root
                    : update.Root);
            if (roots.Count > FileLimits.RetainedRoots) throw new IOException("Upload root inventory capacity is exhausted.");
            var pending = _inventoryPath + "." + Guid.NewGuid().ToString("N") + ".pending";
            try
            {
                using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, roots, FileJson.Options);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(pending, _inventoryPath, overwrite: true);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            BlockedReason = "Upload cleanup ownership could not be persisted. New uploads are blocked. Restore private-state access; do not retry completed uploads automatically.";
            _inventoryValid = false;
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_started)
            {
                _operations.RetentionChanged -= OnRetentionChanged;
                _operations.UploadSettled -= OnUploadSettled;
                _operations.UploadAdmission = null;
            }
        }
        if (_timer is not null) await _timer.DisposeAsync().ConfigureAwait(false);
        _stop.Cancel();
        _wake.Writer.TryComplete();
        try { if (_loop is not null) await _loop.ConfigureAwait(false); }
        finally { _stop.Dispose(); }
    }
}
