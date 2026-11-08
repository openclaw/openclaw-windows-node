using System.Text.Json;
using OpenClaw.Shared;

namespace OpenClawTray.Presentation;

internal sealed record HubSearchContext(
    IOperatorGatewayClient? Client, string AgentId, string? GatewayVersion,
    IReadOnlyList<HubSearchItem> LocalItems, IReadOnlyList<SessionInfo> Sessions,
    int ProfileRevision = 0, bool IsActiveConnection = true)
{
    internal bool Connected => IsActiveConnection && Client is { IsConnectedToGateway: true, HasHandshakeSnapshot: true };
    internal string Access => string.Join(" ", (Client?.GrantedOperatorScopes ?? []).Order(StringComparer.Ordinal));
}

internal sealed record HubSearchStatus(HubSearchGroup Group, string State);

/// <summary>Window-owned search state. Publication and activation use the same query/connection fence.</summary>
internal sealed class HubSearchController(
    Func<HubSearchContext> capture, IUiDispatcher dispatcher, Action<string> reportFailure,
    TimeSpan? debounce = null) : IDisposable
{
    private const int DefaultDebounceMilliseconds = 200;
    private const int DefaultCacheMilliseconds = 30000;
    private readonly Dictionary<string, IReadOnlyList<HubSearchItem>> _remote = [];
    private readonly Dictionary<HubSearchGroup, string> _states = [];
    private readonly Dictionary<string, (DateTimeOffset Loaded, JsonElement Value)> _cache = [];
    private CancellationTokenSource? _pending;
    private HubSearchContext? _context;
    private long _handshake;
    private string _access = "";
    private bool _connected;
    private int _profileRevision;
    private readonly Dictionary<string, string> _unavailable = [];
    private bool _open;
    private object? _owner;
    private int _limit = HubSearch.GroupLimit;
    internal long Generation { get; private set; }
    internal string Query { get; private set; } = "";
    internal IReadOnlyList<HubSearchMatch> Results { get; private set; } = [];
    internal bool HasMoreResults { get; private set; }
    internal bool HasDashboardCompatibilityWarning => _context is { Connected: true } &&
        !HubSearchCatalog.SupportsDashboard(_context.GatewayVersion);
    internal IReadOnlyList<HubSearchStatus> Statuses => _states.Select(pair => new HubSearchStatus(pair.Key, pair.Value)).ToArray();
    internal event Action? Changed;

    internal void Open(object? owner = null)
    {
        _owner = owner ?? this;
        _open = true;
        _limit = HubSearch.GroupLimit;
        SetQuery("");
    }

    internal bool IsOwner(object owner) => _open && ReferenceEquals(_owner, owner);

    internal void Refresh()
    {
        if (!_open) return;
        var next = capture();
        if (!SameContext(next)) { SetQuery(Query); return; }
        _context = next;
        Publish();
    }

    internal void Retry() { if (_open) SetQuery(Query); }
    internal void ShowMore() { _limit += HubSearch.GroupLimit; Publish(); }

    internal void SetQuery(string query)
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = new();
        Query = query.Trim();
        Generation++;
        _remote.Clear();
        _states.Clear();
        var context = capture();
        if (!SameContext(context))
        {
            lock (_cache) _cache.Clear();
            lock (_unavailable) _unavailable.Clear();
        }
        _context = context;
        _handshake = context.Client?.ServerHandshakeGeneration ?? 0;
        _access = context.Access;
        _connected = context.Connected;
        _profileRevision = context.ProfileRevision;
        Publish();
        if (Query.Length == 0) return;
        if (!context.Connected)
        {
            _states[HubSearchGroup.Messages] = "Offline";
            Publish();
            return;
        }
        if (Query.Length > GatewaySearchProtocol.MaximumQueryLength)
        {
            _states[HubSearchGroup.Messages] = "TooLong";
            Publish();
            return;
        }
        foreach (var group in new[] { HubSearchGroup.Settings, HubSearchGroup.Agents, HubSearchGroup.Models,
            HubSearchGroup.Automations, HubSearchGroup.Skills })
            _states[group] = "Loading";
        if (Query.Length >= 2)
        {
            _states[HubSearchGroup.Chats] = "Loading";
            _states[HubSearchGroup.Messages] = "Loading";
        }
        Publish();
        _ = LoadAsync(context, Generation, Query, _pending.Token);
    }

    internal bool CanActivate(long generation, HubSearchItem item) =>
        _open && generation == Generation && _context is not null && SameContext(capture()) &&
        Results.Any(result => result.Item == item);

    internal void Close(object? owner = null)
    {
        if (owner is not null && !IsOwner(owner)) return;
        _open = false;
        _owner = null;
        Generation++;
        _pending?.Cancel();
        Results = [];
    }

    private bool SameContext(HubSearchContext next) =>
        _context is { } current && ReferenceEquals(current.Client, next.Client) &&
        current.AgentId == next.AgentId && _handshake == (next.Client?.ServerHandshakeGeneration ?? 0) &&
        _access == next.Access && _connected == next.Connected && _profileRevision == next.ProfileRevision;

    private void Publish()
    {
        if (!_open || _context is null) return;
        var matches = HubSearch.Find(_context.LocalItems.Concat(_remote.Values.SelectMany(items => items)), Query, int.MaxValue);
        Results = matches.GroupBy(match => match.Item.Group).SelectMany(group => group.Take(_limit)).ToArray();
        HasMoreResults = matches.Count > Results.Count;
        Changed?.Invoke();
    }

    private void Post(long generation, HubSearchContext context, Action action)
    {
        if (!dispatcher.TryEnqueue(() =>
        {
            if (!_open || generation != Generation || !SameContext(capture())) return;
            action();
            Publish();
        }))
            reportFailure("dispatcher-unavailable");
    }

    private async Task LoadAsync(HubSearchContext context, long generation, string query, CancellationToken ct)
    {
        try
        {
            await Task.Delay(debounce ?? TimeSpan.FromMilliseconds(Timing("OPENCLAW_SEARCH_DEBOUNCE_MS", DefaultDebounceMilliseconds, 0, 2000)), ct)
                .ConfigureAwait(false);
            var reads = new List<Task>
            {
                Catalog("config.schema", HubSearchGroup.Settings, null, true),
                Catalog("agents.list", HubSearchGroup.Agents, null, true),
                Catalog("models.list", HubSearchGroup.Models, null, true),
                Catalog("cron.list", HubSearchGroup.Automations,
                    new { includeDisabled = true, query, limit = 200, offset = 0, sortBy = "name", sortDir = "asc" }, false)
            };
            if (context.AgentId.Length > 0)
                reads.Add(Catalog("skills.status", HubSearchGroup.Skills, new { agentId = context.AgentId }, true));
            else
                Post(generation, context, () => _states[HubSearchGroup.Skills] = "SelectAgent");
            if (query.Length >= 2)
            {
                reads.Add(Sessions(false));
                reads.Add(Sessions(true));
            }
            await Task.WhenAll(reads).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }

        bool Available(string method, bool strict = false)
        {
            var methods = context.Client!.AdvertisedServerMethods;
            return (!strict && methods.Count == 0) || methods.Contains(method);
        }

        async Task Run(string method, HubSearchGroup group, Func<Task<IReadOnlyList<HubSearchItem>>> read)
        {
            string? unavailable;
            lock (_unavailable) unavailable = _unavailable.GetValueOrDefault(method);
            if (unavailable is not null)
            {
                Post(generation, context, () => _states[group] = unavailable);
                return;
            }
            if (!Available(method, method == "sessions.search"))
            {
                Post(generation, context, () => _states[group] = "Unavailable");
                return;
            }
            Post(generation, context, () => _states[group] = "Loading");
            try
            {
                var items = await read().ConfigureAwait(false);
                Post(generation, context, () =>
                {
                    _remote[method] = items;
                    if (_states.GetValueOrDefault(group) == "Loading") _states.Remove(group);
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or
                OperationCanceledException or NotSupportedException or InvalidDataException or JsonException)
            {
                var code = (ex as GatewayRequestException)?.Code;
                var state = ex is NotSupportedException || code is "METHOD_NOT_FOUND" or "UNKNOWN_METHOD" ? "Unavailable" :
                    code == "FORBIDDEN" || ex.Message.Contains("missing scope", StringComparison.OrdinalIgnoreCase) ? "Denied" :
                    ex.Message.Contains("unknown method", StringComparison.OrdinalIgnoreCase) ? "Unavailable" : "Failed";
                reportFailure($"{method}:{state}");
                Post(generation, context, () =>
                {
                    _states[group] = state;
                    if (state is "Denied" or "Unavailable")
                        lock (_unavailable) _unavailable[method] = state;
                });
            }
        }

        Task Sessions(bool messages) => Run(messages ? "sessions.search" : "sessions.list",
            messages ? HubSearchGroup.Messages : HubSearchGroup.Chats, async () =>
            {
                var result = await context.Client!.SearchSessionsAsync(query, messages, ct).ConfigureAwait(false);
                var rows = result.Sessions.Concat(context.Sessions)
                    .Concat(result.Hits.Select(hit => new SessionInfo { Key = hit.SessionKey }))
                    .DistinctBy(session => session.Key).ToArray();
                if (result.Indexing || result.Truncated || result.ArchivedExcluded > 0)
                    Post(generation, context, () => _states[messages ? HubSearchGroup.Messages : HubSearchGroup.Chats] = "Partial");
                var dashboardSupported = HubSearchCatalog.SupportsDashboard(context.GatewayVersion);
                if (!messages) return HubSearch.Sessions(result.Sessions, serverMatch: true, dashboardSupported: dashboardSupported).ToArray();
                var byKey = HubSearch.Sessions(rows, dashboardSupported: dashboardSupported)
                    .ToDictionary(item => item.Target.Value, StringComparer.Ordinal);
                return result.Hits.Where(hit => byKey.ContainsKey(hit.SessionKey)).Select(hit =>
                {
                    var item = byKey[hit.SessionKey];
                    var metadataMatches = HubSearch.Find([item], query).Count > 0;
                    var snippet = string.Join(" ", hit.Snippet.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    if (snippet.Length > 160)
                    {
                        var length = char.IsHighSurrogate(snippet[159]) ? 159 : 160;
                        snippet = snippet[..length] + "\u2026";
                    }
                    return item with
                    {
                        Group = metadataMatches ? HubSearchGroup.Chats : HubSearchGroup.Messages,
                        Subtitle = metadataMatches ? item.Subtitle : snippet, ServerMatch = true, Score = hit.Score
                    };
                }).ToArray();
            });

        Task Catalog(string method, HubSearchGroup group, object? parameters, bool cache) =>
            Run(method, group, async () =>
            {
                JsonElement payload;
                (DateTimeOffset Loaded, JsonElement Value) cached;
                lock (_cache)
                    cached = _cache.GetValueOrDefault(method);
                if (cache && cached.Value.ValueKind != JsonValueKind.Undefined &&
                    DateTimeOffset.UtcNow - cached.Loaded < TimeSpan.FromMilliseconds(
                        Timing("OPENCLAW_SEARCH_CACHE_TTL_MS", DefaultCacheMilliseconds, 0, 600000)))
                    payload = cached.Value;
                else
                {
                    payload = await context.Client!.ReadSearchCatalogAsync(method, parameters, ct).ConfigureAwait(false);
                    if (cache)
                        Post(generation, context, () => { lock (_cache) _cache[method] = (DateTimeOffset.UtcNow, payload); });
                }
                var supported = HubSearchCatalog.SupportsDashboard(context.GatewayVersion);
                if (group == HubSearchGroup.Models && !supported)
                    Post(generation, context, () => _states[group] = "Unavailable");
                if (method == "config.schema")
                {
                    var items = HubSearchCatalog.Config(payload, supported, out var partial);
                    if (partial) Post(generation, context, () => _states[group] = "Partial");
                    return items;
                }
                if (payload.TryGetProperty("hasMore", out var more) && more.ValueKind == JsonValueKind.True)
                    Post(generation, context, () => _states[group] = "Partial");
                return HubSearchCatalog.ParseCatalog(method, payload, context.AgentId, supported,
                    HubSearchCatalog.SupportsSkillsDashboard(context.GatewayVersion));
            });
    }

    private static int Timing(string name, int fallback, int min, int max) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? Math.Clamp(value, min, max) : fallback;

    public void Dispose()
    {
        Close();
        _pending?.Dispose();
        _pending = null;
        lock (_cache) _cache.Clear();
        Changed = null;
    }
}
