using System.Collections.Immutable;
using OpenClaw.Shared;
using OpenClaw.Shared.Themes;
using OpenClawTray.Presentation;

namespace OpenClawTray.Theming;

/// <summary>Built-in palettes that can change while the Companion runs. <see cref="Changed"/> is raised on the UI thread.</summary>
internal interface IGatewayBuiltinThemes : IBuiltinThemeSource
{
    event EventHandler? Changed;

    /// <summary>The built-ins to offer: the Gateway's renderable ones in its order and with its names, then table-only ones.</summary>
    IReadOnlyList<ResolvedTheme> Available { get; }
}

/// <summary>Runs <see cref="ThemeProbe"/> over a probe document and returns its JSON result.</summary>
internal interface IBuiltinThemeProbe
{
    Task<string> ResolveAsync(string document, IReadOnlyList<string> themeIds, CancellationToken cancellationToken);
}

/// <summary>
/// The Companion's built-in theme source: the connected Gateway's own palettes first, then the bundled table, then (for
/// an id neither styles) nothing, which renders as the Windows default. On each connection it reads the stylesheets the
/// Gateway's Control UI serves (<see cref="ServedThemeStylesheetReader"/>), resolves them with <see cref="ThemeProbe"/>
/// in an offscreen WebView2, and caches the result per Gateway build, so a reconnect to the same build or a start while
/// it is offline applies those palettes without another probe. Names and branding follow the Gateway's
/// <c>themes.list</c> descriptors once <see cref="IGatewayThemeCatalog"/> has them.
/// <para>
/// Failure is never fatal and never partial: a disabled or unreachable Control UI, a missing WebView2 runtime, a
/// timeout or an invalid result leaves the table in effect for this connection. One resolution runs per connection
/// generation; a newer connection cancels and supersedes it, and nothing from an older one is published. There is no
/// retry within a connection: the next connection, or a catalog naming a built-in not yet resolved, tries again.
/// </para>
/// <example><code>
/// var builtins = new GatewayBuiltinThemeResolver(dispatcher, catalog, new WebViewBuiltinThemeProbe(dispatcher), cache);
/// builtins.Changed += (_, _) => ApplyTheme();
/// builtins.SetConnection(client, gatewayId); // with every catalog.SetConnection
/// var theme = AppearanceResolver.ResolveTheme(settings.Appearance, builtins);
/// </code></example>
/// </summary>
internal sealed class GatewayBuiltinThemeResolver : IGatewayBuiltinThemes, IDisposable
{
    internal const string TimeoutEnvironmentVariable = "OPENCLAW_THEME_RESOLVE_TIMEOUT_MS";
    /// <summary>Fetch and probe together. A cold probe took 1.8–3.6 s on a loaded host; reads over a slow link can add more.</summary>
    internal const int DefaultTimeoutMs = 30_000;

    private readonly IUiDispatcher _dispatcher;
    private readonly IGatewayThemeCatalog _catalog;
    private readonly IBuiltinThemeProbe _probe;
    private readonly IServedBuiltinThemeCache _cache;
    private readonly object _gate = new();
    private IOperatorGatewayClient? _client;
    private string? _gatewayId;
    private long? _epoch;
    private int _generation;
    private int _reconcileSequence;
    private CancellationTokenSource? _running;
    // The probe behind Current, kept for rebuilding with newer descriptors; null while Current came from another session.
    private ServedThemeCacheEntry? _entry;
    private bool _resolvedThisConnection;
    private ServedBuiltinThemeSet? _current;

    public GatewayBuiltinThemeResolver(
        IUiDispatcher dispatcher, IGatewayThemeCatalog catalog, IBuiltinThemeProbe probe, IServedBuiltinThemeCache cache)
    {
        _dispatcher = dispatcher;
        _catalog = catalog;
        _probe = probe;
        _cache = cache;
        // Until the Gateway is reachable, the last Gateway's palettes are the best guess for it.
        _current = Safe(() => _cache.TryLoadLast()?.ToSet());
        _catalog.Changed += OnCatalogChanged;
    }

    /// <summary>
    /// The last Gateway's cached palettes over the table, or null when none are cached. App resolves its startup theme
    /// with this before the resolver exists, because that first application fixes control geometry for the session.
    /// </summary>
    internal static IBuiltinThemeSource? LastCached(IServedBuiltinThemeCache cache) =>
        Safe(() => cache.TryLoadLast()?.ToSet()) is { } set ? set.Over(BuiltinGatewayThemes.Table) : null;

    /// <summary>Raised on the UI thread when <see cref="Current"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The Gateway palettes in effect, or null when the table is.</summary>
    public ServedBuiltinThemeSet? Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    /// <summary>The task of the resolution in progress, for tests.</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    public bool TryGet(string? id, out ResolvedTheme theme) =>
        Current is { } current
            ? current.Over(BuiltinGatewayThemes.Table).TryGet(id, out theme)
            : BuiltinGatewayThemes.Table.TryGet(id, out theme);

    public IReadOnlyList<ResolvedTheme> Available =>
        Current is { } current
            ? [.. current.Themes, .. BuiltinGatewayThemes.All.Where(theme => !current.TryGet(theme.Id, out _))]
            : BuiltinGatewayThemes.All;

    /// <summary>
    /// Binds to a connection. A new client, Gateway or connection epoch starts a resolution; disconnecting keeps the
    /// palettes in effect, so an offline Companion still looks like its Gateway.
    /// </summary>
    public void SetConnection(IOperatorGatewayClient? client, string? gatewayId)
    {
        var next = client is { IsConnectedToGateway: true } ? client : null;
        var epoch = next?.SessionMutationConnectionEpoch;
        CancellationTokenSource? cancel;
        CancellationToken token;
        int generation;
        lock (_gate)
        {
            if (ReferenceEquals(next, _client) && gatewayId == _gatewayId && epoch == _epoch)
                return;
            _client = next;
            _gatewayId = gatewayId;
            _epoch = epoch;
            generation = ++_generation;
            cancel = _running;
            _running = next is null ? null : new CancellationTokenSource();
            token = _running?.Token ?? CancellationToken.None;
            _resolvedThisConnection = false;
        }
        // Superseded work is cancelled, not disposed: it may still be linking to the token.
        cancel?.Cancel();
        if (next is not null)
            Pending = ResolveAsync(next, generation, RequestedIds(_catalog.GatewayBuiltins), token);
    }

    private static IReadOnlyList<string> TableIds => [.. BuiltinGatewayThemes.All.Select(theme => theme.Id)];

    private static IReadOnlyList<string> RequestedIds(IReadOnlyList<GatewayThemeDescriptor>? descriptors) =>
        [.. TableIds.Concat(descriptors?.Select(descriptor => descriptor.Id) ?? []).Distinct(StringComparer.Ordinal)];

    private async Task ResolveAsync(IOperatorGatewayClient client, int generation, IReadOnlyList<string> ids, CancellationToken connection)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(connection);
        budget.CancelAfter(TimeoutMs());
        ServedThemeCacheEntry? entry = null;
        string outcome;
        try
        {
            ControlUiTextReader read = client.ReadControlUiTextAsync;
            var index = await ServedThemeStylesheetReader.ReadIndexAsync(read, budget.Token).ConfigureAwait(false);
            if (index is null)
            {
                outcome = "the Gateway does not serve its Control UI here";
            }
            else if (index.BuildId is { } buildId && Cached(buildId, ids) is { } hit)
            {
                (entry, outcome) = (hit, "cached");
            }
            else if (await ServedThemeStylesheetReader.ReadAsync(read, index, ids, budget.Token).ConfigureAwait(false) is not { } sheets)
            {
                outcome = "a Control UI stylesheet is unavailable";
            }
            else if (Cached(sheets.Key, ids) is { } hashHit)
            {
                (entry, outcome) = (hashHit, "cached");
            }
            else
            {
                // The wait is bounded here too: a probe that never completes must not hold this connection's resolution open.
                var json = await _probe.ResolveAsync(ServedThemeStylesheetReader.BuildProbeDocument(sheets), ids, budget.Token)
                    .WaitAsync(budget.Token).ConfigureAwait(false);
                var probe = ThemeProbe.Parse(json);
                // A theme whose own stylesheet did not load would read the core styles' values, which can pass as styled
                // where core rules name it incidentally (for example its background artwork), so it falls back instead.
                if (!sheets.MissingThemeIds.IsEmpty)
                    probe = ThemeProbe.ParseResult(probe.Result,
                        [.. probe.Failures, .. sheets.MissingThemeIds.Select(id => $"{id}/stylesheet-unavailable")]);
                entry = new ServedThemeCacheEntry(sheets.Key, probe, Descriptors()) { Complete = sheets.MissingThemeIds.IsEmpty };
                outcome = probe.Failures.IsEmpty ? "resolved" : $"resolved, {probe.Failures.Length} unresolved token(s)";
                if (!entry.Complete)
                    outcome += $", not cached: {sheets.MissingThemeIds.Length} theme stylesheet(s) unavailable";
            }
        }
        catch (OperationCanceledException) when (connection.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Timeouts, a missing WebView2 runtime and malformed results all leave the table in effect.
            outcome = $"failed ({ex.GetType().Name})";
        }
        if (Publish(generation, entry, outcome))
            Reconcile();
    }

    private ServedThemeCacheEntry? Cached(string key, IReadOnlyList<string> ids) =>
        Safe(() => _cache.TryLoad(key)) is { } hit && ids.All(id => hit.AttemptedIds.Contains(id, StringComparer.Ordinal))
            ? hit with { Descriptors = Descriptors() ?? hit.Descriptors }
            : null;

    private ImmutableArray<GatewayThemeDescriptor>? Descriptors() =>
        _catalog.GatewayBuiltins is { } builtins ? [.. builtins] : null;

    private bool Publish(int generation, ServedThemeCacheEntry? entry, string outcome, int? reconcileSequence = null)
    {
        var set = entry?.ToSet();
        lock (_gate)
        {
            if (generation != _generation || (reconcileSequence.HasValue && reconcileSequence != _reconcileSequence))
                return false;
            _entry = entry;
            _current = set;
            _resolvedThisConnection = true;
            // Saved under the lock SetConnection takes to start a newer generation, so superseded work can never make
            // an older build the cache's last entry (the offline startup theme).
            if (entry is not null)
                Save(entry);
        }
        Services.Logger.Info(set is null
            ? $"[Themes] Built-in palettes from the bundled table: {outcome}."
            : $"[Themes] Built-in palettes from the Gateway ({set.Themes.Length} themes, {outcome}, build {set.Key}).");
        RaiseChanged();
        return true;
    }

    // Newer descriptors rename or rebrand built-ins, or name one this build has not been probed for. Runs on each catalog
    // change and after each resolution, so descriptors that arrive while a probe runs are reconciled when it finishes.
    private void OnCatalogChanged(object? sender, EventArgs e) => Reconcile();

    private void Reconcile()
    {
        IOperatorGatewayClient? client;
        ServedThemeCacheEntry? entry;
        int generation, sequence;
        lock (_gate)
        {
            client = _client;
            entry = _entry;
            generation = _generation;
            if (client is null || !_resolvedThisConnection || entry is null)
                return;
            // The post-probe worker and UI catalog notifications can reconcile the same connection concurrently.
            sequence = ++_reconcileSequence;
        }
        // Every attempted id, styled or not, is in the result, so this re-probes once per newly listed built-in.
        var descriptors = Descriptors();
        var ids = RequestedIds(descriptors);
        if (!ids.All(id => entry.AttemptedIds.Contains(id, StringComparer.Ordinal)))
        {
            CancellationToken token;
            lock (_gate)
            {
                if (generation != _generation || sequence != _reconcileSequence || _running is null || !_resolvedThisConnection)
                    return;
                _resolvedThisConnection = false;
                token = _running.Token;
            }
            Pending = ResolveAsync(client, generation, ids, token);
            return;
        }
        if (descriptors is null || SameDescriptors(descriptors.Value, entry.Descriptors))
            return;
        Publish(generation, entry with { Descriptors = descriptors }, "names and branding from the Gateway catalog", sequence);
    }

    private static bool SameDescriptors(
        ImmutableArray<GatewayThemeDescriptor> next,
        ImmutableArray<GatewayThemeDescriptor>? previous) =>
        previous is { } known && next.Length == known.Length && next.Zip(known).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name &&
            pair.First.Description == pair.Second.Description && pair.First.Branding == pair.Second.Branding);

    // The cache is optional: it can only skip work, so no failure in it may stop a resolution from publishing.
    private void Save(ServedThemeCacheEntry entry)
    {
        try
        {
            _cache.Save(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            Services.Logger.Warn($"[Themes] Could not cache the Gateway's built-in palettes ({ex.GetType().Name}).");
        }
    }

    private static T? Safe<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static int TimeoutMs() =>
        int.TryParse(Environment.GetEnvironmentVariable(TimeoutEnvironmentVariable), out var value) && value > 0
            ? value
            : DefaultTimeoutMs;

    private void RaiseChanged()
    {
        if (_dispatcher.HasThreadAccess)
            Changed?.Invoke(this, EventArgs.Empty);
        else
            _dispatcher.TryEnqueue(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    public void Dispose()
    {
        _catalog.Changed -= OnCatalogChanged;
        CancellationTokenSource? running;
        lock (_gate)
        {
            running = _running;
            _running = null;
            _generation++;
        }
        running?.Cancel();
    }
}
