using OpenClaw.Shared;
using OpenClaw.Shared.Themes;
using OpenClawTray.Presentation;

namespace OpenClawTray.Theming;

/// <summary>One theme the Companion can offer: a catalog descriptor and, for non-built-ins, its definition.</summary>
internal sealed record GatewayThemeOption(GatewayThemeDescriptor Descriptor, GatewayThemeDefinition? Definition, string? GatewayId)
{
    public string Id => Descriptor.Id;
    public string Name => Descriptor.Name;
}

/// <summary>The theme choices available right now. <see cref="Changed"/> is raised on the UI thread.</summary>
internal interface IGatewayThemeCatalog
{
    IReadOnlyList<GatewayThemeOption> Options { get; }
    /// <summary>The theme the connected Gateway has selected for this connection, or null when unknown.</summary>
    GatewayThemeOption? GatewayCurrent { get; }
    /// <summary>The person's saved dashboard appearance, or null when unknown (no profile, older Gateway, failed read).</summary>
    GatewayAppearancePreferences? GatewayPreferences { get; }
    event EventHandler? Changed;
    Task RefreshAsync();
}

/// <summary>
/// Connection-scoped, read-only reader of the Gateway theme catalog (<c>themes.list</c> and
/// <c>themes.get</c>, operator.read, Gateway 2026.9+). The Companion's built-in table is always offered;
/// plugin and imported themes from the connected Gateway are appended while connected, and
/// <see cref="GatewayCurrent"/> reports the theme the Gateway has selected (<c>themes.list</c>'s
/// <c>theme</c>), and <see cref="GatewayPreferences"/> the person's saved accent (<c>users.prefs.get</c>).
/// It never calls <c>themes.set</c>, <c>themes.import</c> or <c>users.prefs.set</c>.
/// <example><code>
/// var catalog = new GatewayThemeCatalog(dispatcher);
/// catalog.SetConnection(client, gatewayId); // on every operator connection change
/// await catalog.RefreshAsync();             // e.g. when Settings opens
/// </code></example>
/// </summary>
internal sealed class GatewayThemeCatalog(IUiDispatcher dispatcher) : IGatewayThemeCatalog
{
    internal const string TimeoutEnvironmentVariable = "OPENCLAW_THEME_CATALOG_TIMEOUT_MS";
    internal const int DefaultTimeoutMs = 10_000;
    private const int MaxConcurrentDefinitionReads = 4;

    private readonly object _gate = new();
    private IOperatorGatewayClient? _client;
    private string? _gatewayId;
    private long? _epoch;
    private int _generation;
    private int _refreshSequence;
    private int _appliedSequence;
    private RefreshSlot _catalogRefresh = new();
    private RefreshSlot _preferencesRefresh = new();
    private IReadOnlyList<GatewayThemeOption> _gatewayOptions = [];
    private GatewayThemeOption? _gatewayCurrent;
    private GatewayAppearancePreferences? _gatewayPreferences;

    private sealed class RefreshSlot
    {
        public bool Pending;
        public TaskCompletionSource? Completion;
    }

    public event EventHandler? Changed;

    public GatewayAppearancePreferences? GatewayPreferences
    {
        get
        {
            lock (_gate)
                return _gatewayPreferences;
        }
    }

    public IReadOnlyList<GatewayThemeOption> Options
    {
        get
        {
            lock (_gate)
                return [.. BuiltinOptions, .. _gatewayOptions];
        }
    }

    public GatewayThemeOption? GatewayCurrent
    {
        get
        {
            lock (_gate)
                return _gatewayCurrent;
        }
    }

    private static IEnumerable<GatewayThemeOption> BuiltinOptions =>
        BuiltinGatewayThemes.All.Select(theme => new GatewayThemeOption(
            new GatewayThemeDescriptor(theme.Id, theme.Name, theme.Description, GatewayThemeSource.Builtin,
                [.. theme.Modes], null),
            null,
            null));

    /// <summary>
    /// Binds the catalog to a connection; a different client discards the previous Gateway's themes. The same
    /// client reconnecting (a new connection epoch) re-reads the catalog, since events sent while it was offline
    /// were missed.
    /// </summary>
    public void SetConnection(IOperatorGatewayClient? client, string? gatewayId)
    {
        var next = client is { IsConnectedToGateway: true } ? client : null;
        var epoch = next?.SessionMutationConnectionEpoch;
        IOperatorGatewayClient? previous;
        var changed = false;
        lock (_gate)
        {
            var sameBinding = ReferenceEquals(next, _client) && gatewayId == _gatewayId;
            if (sameBinding && epoch == _epoch)
                return;
            previous = _client;
            _client = next;
            _gatewayId = gatewayId;
            _epoch = epoch;
            // Older reads, including ones from the previous epoch, can no longer apply.
            _generation++;
            _catalogRefresh = new();
            _preferencesRefresh = new();
            if (!sameBinding)
            {
                changed = _gatewayOptions.Count > 0 || _gatewayCurrent is not null || _gatewayPreferences is not null;
                _gatewayOptions = [];
                _gatewayCurrent = null;
                _gatewayPreferences = null;
            }
        }
        if (!ReferenceEquals(previous, next))
        {
            if (previous is not null)
                previous.ThemeCatalogInvalidated -= OnThemeCatalogInvalidated;
            if (next is not null)
                next.ThemeCatalogInvalidated += OnThemeCatalogInvalidated;
        }
        if (changed)
            RaiseChanged();
        if (next is not null)
            _ = RefreshAsync();
    }

    // plugins.changed and users.prefs.changed: re-read, mirroring the Control UI's own theme catalog.
    private void OnThemeCatalogInvalidated(object? sender, string eventName)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _client))
                return;
        }
        _ = RefreshAsync();
    }

    /// <summary>
    /// Refreshes the catalog and preferences independently. Each merges requests that arrive during a read into
    /// one further read; a slow preference reply never delays a new selection, or vice versa.
    /// </summary>
    public Task RefreshAsync()
    {
        int generation;
        RefreshSlot catalog, preferences;
        lock (_gate)
        {
            generation = _generation;
            catalog = _catalogRefresh;
            preferences = _preferencesRefresh;
        }
        return Task.WhenAll(
            QueueRefresh(generation, preferences, RefreshPreferencesAsync),
            QueueRefresh(generation, catalog, RefreshOnceAsync));
    }

    private Task QueueRefresh(int generation, RefreshSlot slot, Func<int, Task<bool>> read)
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (generation != _generation)
                return Task.CompletedTask;
            if (slot.Completion is { } active)
            {
                slot.Pending = true;
                return active.Task;
            }
            completion = slot.Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = RunRefreshesAsync(generation, slot, completion, read);
        return completion.Task;
    }

    private async Task RunRefreshesAsync(int generation, RefreshSlot slot, TaskCompletionSource completion, Func<int, Task<bool>> read)
    {
        try
        {
            while (true)
            {
                var changed = await read(generation).ConfigureAwait(false);
                bool rerun;
                lock (_gate)
                {
                    rerun = generation == _generation && slot.Pending;
                    slot.Pending = false;
                    if (!rerun)
                        slot.Completion = null;
                }
                // The last read has released its slot before notifying observers. An invalidation now starts
                // a new read rather than joining a worker that has already decided to stop.
                if (changed)
                    RaiseChanged();
                if (!rerun)
                    break;
            }
            completion.SetResult();
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (slot.Completion == completion)
                {
                    slot.Completion = null;
                    slot.Pending = false;
                }
            }
            completion.SetException(ex);
        }
    }

    private bool IsCurrent(int generation)
    {
        lock (_gate)
            return generation == _generation;
    }

    private async Task<bool> RefreshOnceAsync(int generation)
    {
        IOperatorGatewayClient? client;
        string? gatewayId;
        int sequence;
        lock (_gate)
        {
            if (generation != _generation)
                return false;
            client = _client;
            gatewayId = _gatewayId;
            sequence = ++_refreshSequence;
        }
        if (client is not { IsConnectedToGateway: true } || !Supports(client))
            return false;

        GatewayThemeList list;
        IReadOnlyList<GatewayThemeOption> options;
        try
        {
            list = GatewayThemeProtocol.ParseList(await client.SendWizardRequestAsync(
                GatewayThemeProtocol.ListMethod, new { }, TimeoutMs()).ConfigureAwait(false));
            // Publish the Gateway's selection before the per-theme reads: following it must not wait on an unrelated
            // theme's definition. The options already offered stay until those reads finish.
            IReadOnlyList<GatewayThemeOption> known;
            lock (_gate)
                known = _gatewayOptions;
            if (!TryApply(generation, sequence, null, CurrentOption(list, gatewayId, known)))
                return false;
            RaiseChanged();
            options = await ReadDefinitionsAsync(client, list, gatewayId, generation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Keep the last good catalog; a failed read must not drop themes the user can see.
            Services.Logger.Warn($"[Themes] Gateway theme catalog unavailable ({ex.GetType().Name}).");
            return false;
        }
        return TryApply(generation, sequence, options, CurrentOption(list, gatewayId, options));
    }

    // A newer connection or a newer read (for example after a push event) wins over this one.
    private bool TryApply(int generation, int sequence, IReadOnlyList<GatewayThemeOption>? options, GatewayThemeOption? current)
    {
        lock (_gate)
        {
            if (generation != _generation || sequence < _appliedSequence)
                return false;
            _appliedSequence = sequence;
            if (options is not null)
                _gatewayOptions = options;
            _gatewayCurrent = current;
        }
        return true;
    }

    private async Task<bool> RefreshPreferencesAsync(int generation)
    {
        IOperatorGatewayClient? client;
        lock (_gate)
            client = generation == _generation ? _client : null;
        if (client is not { IsConnectedToGateway: true } || !Supports(client))
            return false;

        var preferences = await ReadPreferencesAsync(client).ConfigureAwait(false);
        lock (_gate)
        {
            if (generation != _generation || Equals(preferences, _gatewayPreferences))
                return false;
            _gatewayPreferences = preferences;
            return true;
        }
    }

    // Unknown (null) when the Gateway predates the method, the connection has no durable profile, or the read fails:
    // following keeps the last accent it saw rather than dropping it.
    private static async Task<GatewayAppearancePreferences?> ReadPreferencesAsync(IOperatorGatewayClient client)
    {
        if (!client.AdvertisedServerMethods.Contains(GatewayAppearancePreferences.Method, StringComparer.Ordinal))
            return null;
        try
        {
            var result = await client.SendWizardRequestAsync(
                GatewayAppearancePreferences.Method, GatewayAppearancePreferences.Request, TimeoutMs()).ConfigureAwait(false);
            if (GatewayAppearancePreferences.TryParse(result, out var preferences))
                return preferences;
            Services.Logger.Info("[Themes] The Gateway has no profile preferences for this connection.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Services.Logger.Warn($"[Themes] Gateway appearance preferences unavailable ({ex.GetType().Name}).");
        }
        return null;
    }

    internal static bool Supports(IOperatorGatewayClient client) =>
        client.AdvertisedServerMethods.Contains(GatewayThemeProtocol.ListMethod, StringComparer.Ordinal) &&
        client.GrantedOperatorScopes.Any(scope => scope is "operator.admin" or "operator.read" or "operator.write");

    // Concurrent, so one slow theme bounds a refresh at about a single timeout instead of adding one per theme; a few
    // at a time, so a Gateway with many imported themes does not get a burst of requests on one connection.
    private async Task<IReadOnlyList<GatewayThemeOption>> ReadDefinitionsAsync(
        IOperatorGatewayClient client, GatewayThemeList list, string? gatewayId, int generation)
    {
        var timeout = TimeoutMs();
        using var slots = new SemaphoreSlim(MaxConcurrentDefinitionReads);
        async Task<GatewayThemeOption?> ReadAsync(GatewayThemeDescriptor descriptor)
        {
            await slots.WaitAsync().ConfigureAwait(false);
            try
            {
                // A read queued before the connection changed would only be discarded; don't send it.
                if (!IsCurrent(generation))
                    return null;
                return await ReadDefinitionAsync(client, descriptor, gatewayId, timeout).ConfigureAwait(false);
            }
            finally
            {
                slots.Release();
            }
        }
        // Built-in palettes come from the Companion's table, which is always offered.
        var reads = list.Themes.Where(descriptor => descriptor.Source != GatewayThemeSource.Builtin).Select(ReadAsync);
        return [.. (await Task.WhenAll(reads).ConfigureAwait(false)).OfType<GatewayThemeOption>()];
    }

    private static async Task<GatewayThemeOption?> ReadDefinitionAsync(
        IOperatorGatewayClient client, GatewayThemeDescriptor descriptor, string? gatewayId, int timeout)
    {
        System.Text.Json.JsonElement result;
        try
        {
            result = await client.SendWizardRequestAsync(
                GatewayThemeProtocol.GetMethod, new { id = descriptor.Id }, timeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && client.IsConnectedToGateway)
        {
            // One unreadable theme (for example removed between list and get) must not hide the rest.
            Services.Logger.Warn($"[Themes] Skipped a Gateway theme whose definition could not be read ({ex.GetType().Name}).");
            return null;
        }
        try
        {
            if (!GatewayThemeProtocol.TryParseGet(result, out var read, out var definition) ||
                read.Id != descriptor.Id || definition is null)
                return null;
            // A theme whose text would be unreadable is not offered (it would not be applied either).
            if (ThemeLegibility.IsReadable(ThemePaletteExpander.Resolve(descriptor, definition)))
                return new GatewayThemeOption(descriptor, definition, gatewayId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A malformed reply rejects only this theme.
            Services.Logger.Warn($"[Themes] Skipped a Gateway theme whose definition could not be parsed ({ex.GetType().Name}).");
            return null;
        }
        Services.Logger.Warn("[Themes] Skipped a Gateway theme whose text contrast is below 3:1.");
        return null;
    }

    // themes.list carries the selected theme's definition; an offered theme's definition is the fallback.
    private static GatewayThemeOption? CurrentOption(
        GatewayThemeList list, string? gatewayId, IEnumerable<GatewayThemeOption> offered)
    {
        if (list.Current is not { } selected)
            return null;
        var definition = selected.Source == GatewayThemeSource.Builtin
            ? null
            : list.CurrentDefinition ?? offered.FirstOrDefault(option => option.Id == selected.Id)?.Definition;
        return new GatewayThemeOption(selected, definition, gatewayId);
    }

    private static int TimeoutMs() =>
        int.TryParse(Environment.GetEnvironmentVariable(TimeoutEnvironmentVariable), out var value) && value > 0
            ? value
            : DefaultTimeoutMs;

    private void RaiseChanged()
    {
        if (dispatcher.HasThreadAccess)
            Changed?.Invoke(this, EventArgs.Empty);
        else
            dispatcher.TryEnqueue(() => Changed?.Invoke(this, EventArgs.Empty));
    }
}
