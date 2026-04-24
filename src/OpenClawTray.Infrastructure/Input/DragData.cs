using System.Diagnostics;
using global::Windows.ApplicationModel.DataTransfer;
using global::Windows.Foundation;
using global::Windows.Storage;
using global::Windows.Storage.Streams;

namespace OpenClawTray.Infrastructure.Input;

/// <summary>
/// The drag payload. Supports typed-payload round-trip (Phase 6a), standard WinUI
/// formats (text / URI / HTML / RTF / files / bitmap — Phase 6b eager + lazy), and
/// custom format identifiers. Same-process transfers stash the in-memory <see cref="DragData"/>
/// via a per-drag GUID in <c>DataPackage.Properties</c>; eager standard formats are
/// also written directly to the <see cref="DataPackage"/> so cross-process drops
/// (Notepad, Word, File Explorer, …) can consume them natively. Lazy providers are
/// adapted via <see cref="DataPackage.SetDataProvider"/> and invoked on demand.
/// </summary>
public sealed class DragData
{
    internal const string ProcIdFormatId = "reactor/proc-id";
    internal const string TransferIdFormatId = "reactor/transfer-id";

    /// <summary>
    /// One entry per format id. Holds either an eager resolved value, a sync provider,
    /// or an async provider. At most one of the three is populated at a time.
    /// </summary>
    internal sealed class FormatEntry
    {
        public object? EagerValue;
        public Func<object?>? SyncProvider;
        public Func<CancellationToken, Task<object?>>? AsyncProvider;
        public bool HasEager => SyncProvider is null && AsyncProvider is null && EagerValue is not null;

        public async Task<object?> ResolveAsync(CancellationToken ct)
        {
            if (EagerValue is not null) return EagerValue;
            if (AsyncProvider is not null) return await AsyncProvider(ct).ConfigureAwait(false);
            if (SyncProvider is not null) return SyncProvider();
            return null;
        }

        public object? ResolveSync()
        {
            if (EagerValue is not null) return EagerValue;
            if (SyncProvider is not null) return SyncProvider();
            // Async provider — we don't block here; caller falls through to the async accessor.
            return null;
        }
    }

    private readonly Dictionary<string, FormatEntry> _formatEntries = new(StringComparer.Ordinal);
    private readonly int _originProcessId = Process.GetCurrentProcess().Id;

    /// <summary>Create an empty <see cref="DragData"/>. Use <c>With…</c> methods to populate formats.</summary>
    public DragData() { }

    /// <summary>Convenience factory — creates a <see cref="DragData"/> pre-populated with plain text.</summary>
    public static DragData Text(string text) => new DragData().WithText(text);

    /// <summary>Origin process id — stored so cross-process drops can be detected/rejected.</summary>
    public int OriginProcessId => _originProcessId;

    /// <summary>Format identifiers this <see cref="DragData"/> advertises.</summary>
    public IReadOnlyCollection<string> AvailableFormats
    {
        get
        {
            var set = new HashSet<string>(_formatEntries.Keys, StringComparer.Ordinal)
            {
                ProcIdFormatId,
            };
            return set;
        }
    }

    /// <summary>Returns true when the specified format id is present.</summary>
    public bool HasFormat(string formatId) =>
        formatId == ProcIdFormatId || _formatEntries.ContainsKey(formatId);

    // ══════════════════════════════════════════════════════════════════
    //  Typed-payload API (Phase 6a)
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Factory for a typed-payload-only <see cref="DragData"/>.</summary>
    public static DragData Typed<T>(T payload) => new DragData().WithTypedPayload(payload);

    /// <summary>Attaches a typed payload under the canonical typed-format key for <typeparamref name="T"/>.</summary>
    public DragData WithTypedPayload<T>(T payload)
    {
        SetEager(TypedFormatId<T>(), payload);
        return this;
    }

    /// <summary>Attempts to retrieve a typed payload. Returns false if the format isn't present
    /// or the stored payload can't be cast to <typeparamref name="T"/>.</summary>
    public bool TryGetTypedPayload<T>(out T payload)
    {
        if (_formatEntries.TryGetValue(TypedFormatId<T>(), out var entry)
            && entry.ResolveSync() is T cast)
        {
            payload = cast;
            return true;
        }
        payload = default!;
        return false;
    }

    /// <summary>Canonical format id used for a typed payload of <typeparamref name="T"/>.</summary>
    public static string TypedFormatId<T>() => "reactor/typed/" + typeof(T).FullName;

    // ══════════════════════════════════════════════════════════════════
    //  Standard formats — Text / Uri / Html / Rtf / Files / Bitmap
    //  Eager + lazy (sync + async). Each overload populates a single FormatEntry.
    // ══════════════════════════════════════════════════════════════════

    // Text
    public DragData WithText(string text) { SetEager(StandardDataFormats.Text, text); return this; }
    public DragData WithText(Func<string> provider) { SetSyncProvider(StandardDataFormats.Text, () => provider()); return this; }
    public DragData WithText(Func<CancellationToken, Task<string>> provider) { SetAsyncProvider(StandardDataFormats.Text, async ct => await provider(ct).ConfigureAwait(false)); return this; }

    // Uri (WebLink is the standard format; ApplicationLink is also valid — default to WebLink)
    public DragData WithUri(Uri uri) { SetEager(StandardDataFormats.WebLink, uri); return this; }
    public DragData WithUri(Func<Uri> provider) { SetSyncProvider(StandardDataFormats.WebLink, () => provider()); return this; }
    public DragData WithUri(Func<CancellationToken, Task<Uri>> provider) { SetAsyncProvider(StandardDataFormats.WebLink, async ct => await provider(ct).ConfigureAwait(false)); return this; }

    // Html
    public DragData WithHtml(string html) { SetEager(StandardDataFormats.Html, html); return this; }
    public DragData WithHtml(Func<string> provider) { SetSyncProvider(StandardDataFormats.Html, () => provider()); return this; }
    public DragData WithHtml(Func<CancellationToken, Task<string>> provider) { SetAsyncProvider(StandardDataFormats.Html, async ct => await provider(ct).ConfigureAwait(false)); return this; }

    // Rtf
    public DragData WithRtf(string rtf) { SetEager(StandardDataFormats.Rtf, rtf); return this; }
    public DragData WithRtf(Func<string> provider) { SetSyncProvider(StandardDataFormats.Rtf, () => provider()); return this; }
    public DragData WithRtf(Func<CancellationToken, Task<string>> provider) { SetAsyncProvider(StandardDataFormats.Rtf, async ct => await provider(ct).ConfigureAwait(false)); return this; }

    // Files (StorageItems)
    public DragData WithFiles(IEnumerable<IStorageItem> files)
    {
        SetEager(StandardDataFormats.StorageItems, files.ToArray());
        return this;
    }
    public DragData WithFiles(Func<IEnumerable<IStorageItem>> provider)
    {
        SetSyncProvider(StandardDataFormats.StorageItems, () => provider().ToArray());
        return this;
    }
    public DragData WithFiles(Func<CancellationToken, Task<IEnumerable<IStorageItem>>> provider)
    {
        SetAsyncProvider(StandardDataFormats.StorageItems, async ct =>
        {
            var items = await provider(ct).ConfigureAwait(false);
            return items.ToArray();
        });
        return this;
    }

    // Bitmap
    public DragData WithBitmap(RandomAccessStreamReference bitmap) { SetEager(StandardDataFormats.Bitmap, bitmap); return this; }
    public DragData WithBitmap(Func<RandomAccessStreamReference> provider) { SetSyncProvider(StandardDataFormats.Bitmap, () => provider()); return this; }
    public DragData WithBitmap(Func<CancellationToken, Task<RandomAccessStreamReference>> provider) { SetAsyncProvider(StandardDataFormats.Bitmap, async ct => await provider(ct).ConfigureAwait(false)); return this; }

    // Custom format id
    public DragData WithCustomFormat(string formatId, object payload) { SetEager(formatId, payload); return this; }
    public DragData WithCustomFormat(string formatId, Func<object> provider) { SetSyncProvider(formatId, () => provider()); return this; }
    public DragData WithCustomFormat(string formatId, Func<CancellationToken, Task<object>> provider) { SetAsyncProvider(formatId, async ct => await provider(ct).ConfigureAwait(false)); return this; }

    // ══════════════════════════════════════════════════════════════════
    //  Target-side accessors (sync + async)
    // ══════════════════════════════════════════════════════════════════

    public bool TryGetText(out string value) => TryGetAs(StandardDataFormats.Text, out value!);
    public bool TryGetUri(out Uri value) => TryGetAs(StandardDataFormats.WebLink, out value!);
    public bool TryGetHtml(out string value) => TryGetAs(StandardDataFormats.Html, out value!);
    public bool TryGetRtf(out string value) => TryGetAs(StandardDataFormats.Rtf, out value!);
    public bool TryGetFiles(out IReadOnlyList<IStorageItem> value)
    {
        if (_formatEntries.TryGetValue(StandardDataFormats.StorageItems, out var entry))
        {
            var resolved = entry.ResolveSync();
            if (resolved is IStorageItem[] arr) { value = arr; return true; }
            if (resolved is IReadOnlyList<IStorageItem> list) { value = list; return true; }
            if (resolved is IEnumerable<IStorageItem> e) { value = e.ToArray(); return true; }
        }
        value = Array.Empty<IStorageItem>();
        return false;
    }
    public bool TryGetBitmap(out RandomAccessStreamReference value) => TryGetAs(StandardDataFormats.Bitmap, out value!);
    public bool TryGetCustomFormat<T>(string formatId, out T value)
    {
        if (_formatEntries.TryGetValue(formatId, out var entry)
            && entry.ResolveSync() is T cast)
        {
            value = cast;
            return true;
        }
        value = default!;
        return false;
    }

    public Task<string?> GetTextAsync(CancellationToken ct = default) => GetAsAsync<string>(StandardDataFormats.Text, ct);
    public Task<Uri?> GetUriAsync(CancellationToken ct = default) => GetAsAsync<Uri>(StandardDataFormats.WebLink, ct);
    public Task<string?> GetHtmlAsync(CancellationToken ct = default) => GetAsAsync<string>(StandardDataFormats.Html, ct);
    public Task<string?> GetRtfAsync(CancellationToken ct = default) => GetAsAsync<string>(StandardDataFormats.Rtf, ct);
    public async Task<IReadOnlyList<IStorageItem>> GetFilesAsync(CancellationToken ct = default)
    {
        if (!_formatEntries.TryGetValue(StandardDataFormats.StorageItems, out var entry))
            return Array.Empty<IStorageItem>();
        var resolved = await entry.ResolveAsync(ct).ConfigureAwait(false);
        return resolved switch
        {
            IStorageItem[] arr => arr,
            IReadOnlyList<IStorageItem> list => list,
            IEnumerable<IStorageItem> e => e.ToArray(),
            _ => Array.Empty<IStorageItem>(),
        };
    }
    public Task<RandomAccessStreamReference?> GetBitmapAsync(CancellationToken ct = default) =>
        GetAsAsync<RandomAccessStreamReference>(StandardDataFormats.Bitmap, ct);
    public async Task<T?> GetCustomFormatAsync<T>(string formatId, CancellationToken ct = default)
    {
        if (!_formatEntries.TryGetValue(formatId, out var entry)) return default;
        var v = await entry.ResolveAsync(ct).ConfigureAwait(false);
        return v is T cast ? cast : default;
    }

    // ══════════════════════════════════════════════════════════════════
    //  Internal storage helpers
    // ══════════════════════════════════════════════════════════════════

    private void SetEager(string formatId, object? value)
    {
        _formatEntries[formatId] = new FormatEntry { EagerValue = value };
    }

    private void SetSyncProvider(string formatId, Func<object?> provider)
    {
        _formatEntries[formatId] = new FormatEntry { SyncProvider = provider };
    }

    private void SetAsyncProvider(string formatId, Func<CancellationToken, Task<object?>> provider)
    {
        _formatEntries[formatId] = new FormatEntry { AsyncProvider = provider };
    }

    private bool TryGetAs<T>(string formatId, out T value)
    {
        if (_formatEntries.TryGetValue(formatId, out var entry)
            && entry.ResolveSync() is T cast)
        {
            value = cast;
            return true;
        }
        value = default!;
        return false;
    }

    private async Task<T?> GetAsAsync<T>(string formatId, CancellationToken ct)
    {
        if (!_formatEntries.TryGetValue(formatId, out var entry)) return default;
        var v = await entry.ResolveAsync(ct).ConfigureAwait(false);
        return v is T cast ? cast : default;
    }

    internal IReadOnlyDictionary<string, FormatEntry> FormatEntries => _formatEntries;

    // ══════════════════════════════════════════════════════════════════
    //  In-memory transfer registry (per-drag GUID → DragData).
    //  Same-process DnD stores the DragData here at DragStarting time; the target
    //  pulls it out via the GUID written into DataPackage.Properties[TransferIdFormatId].
    //  Entries are removed on DropCompleted (success or cancel).
    // ══════════════════════════════════════════════════════════════════

    // Strong references: a typed-only DragData (no FormatEntry) has no other live root
    // once DragStarting returns, so a WeakReference could be collected mid-drag. The
    // reconciler is responsible for calling Unregister in DropCompleted (success or cancel).
    private static readonly Dictionary<Guid, DragData> _transfers = new();
    private static readonly object _transfersLock = new();

    internal static Guid Register(DragData data)
    {
        var id = Guid.NewGuid();
        lock (_transfersLock)
            _transfers[id] = data;
        return id;
    }

    internal static DragData? Resolve(Guid id)
    {
        lock (_transfersLock)
        {
            return _transfers.TryGetValue(id, out var data) ? data : null;
        }
    }

    internal static void Unregister(Guid id)
    {
        lock (_transfersLock)
            _transfers.Remove(id);
    }

    // ══════════════════════════════════════════════════════════════════
    //  DataPackage sync — called by the reconciler at DragStarting time.
    //  Eager values are written directly to the package (cross-process ready);
    //  lazy values register a DataProviderHandler that resolves on demand.
    // ══════════════════════════════════════════════════════════════════

    internal void PopulatePackage(DataPackage package)
    {
        foreach (var (formatId, entry) in _formatEntries)
        {
            if (entry.EagerValue is not null)
            {
                WriteEagerToPackage(package, formatId, entry.EagerValue);
            }
            else if (entry.SyncProvider is not null || entry.AsyncProvider is not null)
            {
                var localEntry = entry;
                package.SetDataProvider(formatId, request =>
                {
                    var deferral = request.GetDeferral();
                    _ = global::System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            var resolved = await localEntry.ResolveAsync(default).ConfigureAwait(false);
                            if (resolved is not null)
                                WriteResolvedToRequest(request, formatId, resolved);
                        }
                        catch
                        {
                            // Swallow — the caller gets no data for this format; other formats unaffected.
                        }
                        finally
                        {
                            deferral.Complete();
                        }
                    });
                });
            }
        }
    }

    private static void WriteEagerToPackage(DataPackage package, string formatId, object value)
    {
        if (formatId == StandardDataFormats.Text && value is string s) { package.SetText(s); return; }
        if (formatId == StandardDataFormats.WebLink && value is Uri u) { package.SetWebLink(u); return; }
        if (formatId == StandardDataFormats.Html && value is string h) { package.SetHtmlFormat(h); return; }
        if (formatId == StandardDataFormats.Rtf && value is string r) { package.SetRtf(r); return; }
        if (formatId == StandardDataFormats.StorageItems && value is IEnumerable<IStorageItem> items)
        {
            package.SetStorageItems(items);
            return;
        }
        if (formatId == StandardDataFormats.Bitmap && value is RandomAccessStreamReference bmp)
        {
            package.SetBitmap(bmp);
            return;
        }
        // Custom or typed payload — carry a sentinel via Properties so cross-process targets
        // can at least see the format id. Same-process targets use the transfer registry.
        if (!package.Properties.ContainsKey(formatId))
            package.Properties[formatId] = formatId;
    }

    private static void WriteResolvedToRequest(DataProviderRequest request, string formatId, object value)
    {
        // DataProviderRequest.SetData is untyped — WinUI will coerce based on the format id.
        request.SetData(value);
    }
}
