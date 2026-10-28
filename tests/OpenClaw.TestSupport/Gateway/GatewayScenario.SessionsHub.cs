using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.TestSupport.Gateway;

/// <summary>
/// Synthetic Sessions page and Worktrees state for native hub proof. Rows,
/// groups, owners, and worktrees are fictional and memory-only; mutations change
/// only this in-memory catalog and never touch a real Gateway or repository.
/// </summary>
public sealed partial class GatewayScenario
{
    public const string SessionsHubName = "sessions-hub";
    public const string SessionsHubReadOnlyName = "sessions-hub-readonly";
    public const string HubPreservedSessionKey = "agent:main:hub-worktree-busy";
    public const string HubCloudSessionKey = "agent:research:hub-cloud-worker";
    public const string HubBusyWorktreeId = "wt-busy-snapshot";
    public const string HubIndexingQuery = "indexing";

    private static readonly string[] HubMethods =
    [
        "sessions.search", "sessions.groups.list", "sessions.groups.put", "sessions.groups.create", "sessions.groups.rename",
        "sessions.groups.delete", "sessions.assignOwner", "sessions.reclaim", "sessions.patch", "sessions.delete",
        "sessions.create", "sessions.reset", "sessions.compact", "users.list", "worktrees.list", "worktrees.branches",
        "worktrees.create", "worktrees.remove", "worktrees.restore", "worktrees.gc",
    ];

    private SessionsHubState? _hub;


    /// <param name="legacySessionsList">
    /// Rejects the <c>source</c>/<c>excludeDock</c> list params the way Gateways before
    /// 2026.10.5 do, so the client fallback stays covered.
    /// </param>
    public static GatewayScenario CreateSessionsHub(bool readOnly = false, bool legacySessionsList = false)
    {
        var browse = CreateBrowse();
        var now = DateTimeOffset.UtcNow;
        var hubSessions = HubSessionSeeds(now).Select((seed, index) => seed.Build(index)).ToArray();
        var reads = new Dictionary<string, JsonElement>(browse._reads, StringComparer.Ordinal)
        {
            ["agents.list"] = JsonSerializer.SerializeToElement(new
            {
                defaultId = "main", mainKey = "main", scope = "per-sender", selectionRequired = false,
                agents = new[]
                {
                    new { id = "main", name = "Fixture Main", identity = new { name = "Fixture Main", emoji = "🦞" } },
                    new { id = "research", name = "Fixture Research", identity = new { name = "Fixture Research", emoji = "🔬" } },
                },
            }),
        };
        var scenario = new GatewayScenario([.. browse._sessions, .. hubSessions.Select(s => s.Session)], reads,
            hubMethods: HubMethods, hubReadOnly: readOnly);
        scenario._hub = new SessionsHubState(scenario, readOnly, legacySessionsList, now,
            browse._sessions.Select(s => JsonNode.Parse(s.Row.GetRawText())!.AsObject())
                .Concat(hubSessions.Select(s => s.Row)));
        return scenario;
    }

    private sealed record HubSeed(
        string Key, string Title, string Agent, TimeSpan Age, string[] Texts,
        Action<JsonObject>? Customize = null)
    {
        public (Session Session, JsonObject Row) Build(int index)
        {
            var session = CreateSession(Key, Title, Agent, Agent == "research" ? "research" : "browse", 100 + index, Texts);
            var row = JsonNode.Parse(session.Row.GetRawText())!.AsObject();
            row["updatedAt"] = DateTimeOffset.UtcNow.Subtract(Age).ToUnixTimeMilliseconds();
            row["status"] = "done";
            row.Remove("thinkingLevel");
            Customize?.Invoke(row);
            return (session, row);
        }
    }

    private static IEnumerable<HubSeed> HubSessionSeeds(DateTimeOffset now)
    {
        JsonObject Owner(string type, string id, string label) => new()
        {
            ["actor"] = new JsonObject { ["type"] = type, ["id"] = id, ["label"] = label },
        };
        yield return new("agent:main:hub-release-notes", "Draft the 3.2 release notes", "main", TimeSpan.FromMinutes(3),
            ["Collect the merged pull requests for release 3.2.", "Release notes drafted with five highlights."],
            row => { row["category"] = "Release"; row["status"] = "running"; row["hasActiveRun"] = true;
                row["pinned"] = true; row["pinnedAt"] = now.ToUnixTimeMilliseconds();
                row["owner"] = Owner("human", "fixture-owner", "Fixture Owner"); row["thinkingLevel"] = "high";
                row["color"] = "orange"; row["icon"] = "book"; });
        yield return new("agent:main:hub-auth-refactor", "Refactor the sign-in flow", "main", TimeSpan.FromMinutes(18),
            ["Split the token refresh into its own service.", "Refactor complete; tests pass locally."],
            row => { row["category"] = "Release"; row["unread"] = true;
                row["owner"] = Owner("human", "profile-ada", "Ada Lovelace");
                row["worktree"] = new JsonObject { ["id"] = "wt-auth", ["branch"] = "openclaw/auth-refactor", ["repoRoot"] = "C:/src/contoso-app" };
                row["reasoningLevel"] = "on"; row["fastMode"] = "auto"; row["color"] = "blue"; row["icon"] = "braces"; });
        yield return new("agent:research:hub-market-scan", "Summarize the market scan", "research", TimeSpan.FromMinutes(42),
            ["Find three competitors with offline sync.", "Found three; summary attached."],
            row => { row["category"] = "Research"; row["channel"] = "telegram"; row["chatType"] = "direct";
                row["owner"] = Owner("agent", "research", "Fixture Research"); row["verboseLevel"] = "full";
                row["color"] = "green"; row["icon"] = "coins"; });
        yield return new(HubCloudSessionKey, "Benchmark the vector index", "research", TimeSpan.FromMinutes(55),
            ["Run the vector index benchmark on a cloud worker.", "Benchmark is running remotely."],
            row => { row["category"] = "Research"; row["placement"] = new JsonObject { ["state"] = "active", ["generation"] = 2 };
                row["color"] = "purple"; row["icon"] = "monitor";
                row["status"] = "done"; row["hasActiveRun"] = false; });
        yield return new("agent:main:hub-deploy-failure", "Investigate the failed deploy", "main", TimeSpan.FromHours(3),
            ["Why did the staging deploy fail?", "The migration step timed out after 300 seconds."],
            row => { row["category"] = "Ops"; row["status"] = "failed"; row["lastRunError"] = "Migration timed out."; row["color"] = "red";
                row["owner"] = Owner("human", "fixture-owner", "Fixture Owner"); });
        yield return new("agent:main:hub-oncall-digest", "Weekly on-call digest", "main", TimeSpan.FromHours(20),
            ["Summarize this week's pages.", "Seven pages, two actionable."],
            row => { row["category"] = "Ops"; row["channel"] = "discord"; row["kind"] = "group"; row["chatType"] = "group";
                row["color"] = "pink"; row["icon"] = "kanban"; });
        yield return new("agent:main:cron:nightly-cleanup", "Nightly cleanup", "main", TimeSpan.FromHours(26),
            ["Nightly cleanup started.", "Removed 12 stale caches."],
            row => { row["classification"] = "cron"; row["isBackground"] = true; row["icon"] = "bot"; });
        yield return new("global", "Global session", "main", TimeSpan.FromDays(2),
            ["Global broadcast check.", "Broadcast delivered."],
            row => { row["kind"] = "global"; row.Remove("agentId"); });
        yield return new("unknown", "Unknown session", "main", TimeSpan.FromDays(3),
            ["Legacy import.", "Imported without routing metadata."],
            row => { row["kind"] = "unknown"; row.Remove("agentId"); });
        yield return new(HubPreservedSessionKey, "Prototype the export wizard", "main", TimeSpan.FromDays(4),
            ["Sketch the export wizard.", "Prototype parked; worktree still has edits."],
            row => { row["archived"] = true; row["archivedAt"] = now.AddDays(-1).ToUnixTimeMilliseconds(); row["archiveReason"] = "manual";
                row["worktree"] = new JsonObject { ["id"] = HubBusyWorktreeId, ["branch"] = "openclaw/export-wizard", ["repoRoot"] = "C:/src/contoso-app" }; });
        yield return new("agent:research:hub-archived-survey", "Survey archive", "research", TimeSpan.FromDays(9),
            ["Archive the customer survey notes.", "Survey notes archived."],
            row => { row["archived"] = true; row["archivedAt"] = now.AddDays(-2).ToUnixTimeMilliseconds(); row["archiveReason"] = "age-retention";
                row["category"] = "Research"; });
        yield return new("agent:main:hub-archived-spike", "Spike: offline cache", "main", TimeSpan.FromDays(12),
            ["Try an offline cache.", "Spike closed; not pursuing."],
            row => { row["archived"] = true; row["archivedAt"] = now.AddDays(-3).ToUnixTimeMilliseconds(); row["archiveReason"] = "manual"; });
        for (var i = 1; i <= 18; i++)
        {
            var n = i;
            yield return new($"agent:{(n % 3 == 0 ? "research" : "main")}:hub-task-{n:D2}", $"Backlog task {n:D2}",
                n % 3 == 0 ? "research" : "main", TimeSpan.FromDays(n % 6) + TimeSpan.FromHours(n),
                [$"Backlog task {n:D2}: triage the request.", $"Backlog task {n:D2} triaged."],
                row => { if (n % 4 == 0) row["category"] = "Ops"; if (n % 5 == 0) row["unread"] = true; });
        }
    }

    private object? RespondSessionsHub(string method, JsonElement parameters)
    {
        if (_hub is null)
            return null;
        if (method is "sessions.list" or "sessions.subscribe" or "users.list" || HubMethods.Contains(method, StringComparer.Ordinal))
            return _hub.Respond(method, parameters);
        return null;
    }

    private sealed class SessionsHubState
    {
        private readonly object _sync = new();
        private readonly GatewayScenario _scenario;
        private readonly bool _readOnly;
        private readonly bool _legacySessionsList;
        private readonly DateTimeOffset _now;
        private readonly Dictionary<string, JsonObject> _rows = new(StringComparer.Ordinal);
        private readonly List<string> _keyOrder = [];
        private readonly List<string> _groups = ["Release", "Research", "Ops"];
        private readonly List<JsonObject> _worktrees = [];
        private readonly List<object> _profiles =
        [
            new { id = "fixture-owner", displayName = "Fixture Owner", emails = new[] { "owner@example.test" }, mergedInto = (string?)null },
            new { id = "profile-ada", displayName = "Ada Lovelace", emails = new[] { "ada@example.test" }, mergedInto = (string?)null },
            new { id = "profile-grace", displayName = "Grace Hopper", emails = new[] { "grace@example.test" }, mergedInto = (string?)null },
        ];
        private int _created;
        private int _gcPolls;

        public SessionsHubState(GatewayScenario scenario, bool readOnly, bool legacySessionsList, DateTimeOffset now, IEnumerable<JsonObject> rows)
        {
            _scenario = scenario;
            _readOnly = readOnly;
            _legacySessionsList = legacySessionsList;
            _now = now;
            foreach (var row in rows)
            {
                var key = row["key"]!.GetValue<string>();
                _rows[key] = row;
                _keyOrder.Add(key);
            }
            JsonObject Worktree(string id, string name, string branch, string ownerKind, string? ownerId, TimeSpan age, bool removed = false)
            {
                var record = new JsonObject
                {
                    ["id"] = id, ["name"] = name, ["repoFingerprint"] = "0123456789abcdef", ["repoRoot"] = "C:/src/contoso-app",
                    ["path"] = $"C:/src/.openclaw-worktrees/{name}", ["branch"] = branch, ["baseRef"] = "main",
                    ["ownerKind"] = ownerKind, ["createdAt"] = now.Subtract(age).AddDays(-1).ToUnixTimeMilliseconds(),
                    ["lastActiveAt"] = now.Subtract(age).ToUnixTimeMilliseconds(),
                };
                if (ownerId is not null) record["ownerId"] = ownerId;
                if (removed) record["removedAt"] = now.Subtract(age).ToUnixTimeMilliseconds();
                return record;
            }
            _worktrees.Add(Worktree("wt-auth", "auth-refactor", "openclaw/auth-refactor", "session", "agent:main:hub-auth-refactor", TimeSpan.FromMinutes(18)));
            _worktrees.Add(Worktree(HubBusyWorktreeId, "export-wizard", "openclaw/export-wizard", "session", HubPreservedSessionKey, TimeSpan.FromDays(4)));
            _worktrees.Add(Worktree("wt-board", "board-triage", "openclaw/board-triage", "workboard", "board-7", TimeSpan.FromHours(6)));
            _worktrees.Add(Worktree("wt-manual", "perf-experiment", "perf-experiment", "manual", null, TimeSpan.FromDays(2)));
            _worktrees.Add(Worktree("wt-removed", "old-spike", "openclaw/old-spike", "manual", null, TimeSpan.FromDays(8), removed: true));
        }

        public object Respond(string method, JsonElement p)
        {
            lock (_sync)
            {
                if (_readOnly && IsMutation(method))
                    throw new FixtureRequestException("FORBIDDEN", "missing scope: operator.write");
                return method switch
                {
                    "sessions.list" => List(p),
                    "sessions.subscribe" => new { ok = true },
                    "sessions.search" => Search(p),
                    "sessions.groups.list" => Groups(),
                    "sessions.groups.put" => PutGroups(p),
                    "sessions.groups.create" => CreateGroup(p),
                    "sessions.groups.rename" => RenameGroup(p),
                    "sessions.groups.delete" => DeleteGroup(p),
                    "sessions.patch" => Patch(p),
                    "sessions.delete" => Delete(p),
                    "sessions.assignOwner" => AssignOwner(p),
                    "sessions.reclaim" => Reclaim(p),
                    "sessions.create" => Create(p),
                    "sessions.reset" or "sessions.compact" => Lifecycle(method, p),
                    "users.list" => new { profiles = _profiles },
                    "worktrees.list" => new { worktrees = _worktrees.Select(w => w.DeepClone()).ToArray() },
                    "worktrees.branches" => Branches(p),
                    "worktrees.create" => CreateWorktree(p),
                    "worktrees.remove" => RemoveWorktree(p),
                    "worktrees.restore" => RestoreWorktree(p),
                    "worktrees.gc" => Gc(p),
                    _ => throw new FixtureRequestException("METHOD_NOT_FOUND", "Unknown fixture Gateway method.", unexpected: true),
                };
            }
        }

        private static bool IsMutation(string method) => method is not (
            "sessions.list" or "sessions.subscribe" or "sessions.search" or "sessions.groups.list" or "users.list" or "worktrees.list");

        private object List(JsonElement p)
        {
            if (_legacySessionsList)
            {
                foreach (var newer in new[] { "source", "excludeDock" })
                    if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty(newer, out _))
                        throw new FixtureRequestException("INVALID_REQUEST",
                            $"invalid sessions.list params: at root: unexpected property '{newer}'");
            }
            ValidateProperties(p, "agentId", "limit", "offset", "activeMinutes", "includeGlobal", "includeUnknown",
                "includeDerivedTitles", "includeLastMessage", "archived", "search", "source", "excludeDock");
            var rows = Filter(p).ToArray();
            var limit = PositiveInt(p, "limit", int.MaxValue);
            var offset = p.TryGetProperty("offset", out var o) && o.TryGetInt32(out var parsed) && parsed >= 0 ? parsed : 0;
            var page = rows.Skip(offset).Take(limit).Select(row => row.DeepClone()).ToArray();
            var next = offset + page.Length;
            var hasMore = next < rows.Length;
            return new
            {
                ts = _now.ToUnixTimeMilliseconds(), count = page.Length, totalCount = rows.Length,
                hasMore, nextOffset = hasMore ? next : (int?)null,
                defaults = new { modelProvider = "fixture", model = "browse", contextTokens = 128_000 },
                sessions = page,
            };
        }

        private IEnumerable<JsonObject> Filter(JsonElement p)
        {
            var agent = OptionalString(p, "agentId");
            var search = p.TryGetProperty("search", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()?.Trim() : null;
            var activeMinutes = PositiveInt(p, "activeMinutes", int.MaxValue);
            var includeGlobal = OptionalBoolean(p, "includeGlobal") ?? false;
            var includeUnknown = OptionalBoolean(p, "includeUnknown") ?? false;
            var archived = p.TryGetProperty("archived", out var a)
                ? a.ValueKind == JsonValueKind.String && a.GetString() == "all" ? (bool?)null
                : a.ValueKind is JsonValueKind.True or JsonValueKind.False ? a.GetBoolean()
                : throw new FixtureRequestException("INVALID_PARAMS", "archived must be a boolean or \"all\".")
                : false;
            var cutoff = _now.AddMinutes(-Math.Min(activeMinutes, 60 * 24 * 3650)).ToUnixTimeMilliseconds();
            return _keyOrder.Where(_rows.ContainsKey).Select(key => _rows[key])
                .Where(row => agent is null || Str(row, "agentId") == agent || (Str(row, "agentId") is null && row["key"]!.GetValue<string>().StartsWith($"agent:{agent}:", StringComparison.Ordinal)))
                .Where(row => Str(row, "kind") != "global" || includeGlobal)
                .Where(row => Str(row, "kind") != "unknown" || includeUnknown)
                .Where(row => archived is null || (row["archived"]?.GetValue<bool>() ?? false) == archived)
                .Where(row => activeMinutes == int.MaxValue || (row["updatedAt"]?.GetValue<long>() ?? 0) >= cutoff)
                .Where(row => string.IsNullOrEmpty(search)
                    || new[] { Str(row, "key"), Str(row, "label"), Str(row, "displayName"), Str(row, "derivedTitle") }
                        .Any(value => value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true))
                .OrderByDescending(row => row["updatedAt"]?.GetValue<long>() ?? 0);
        }

        private object Search(JsonElement p)
        {
            ValidateProperties(p, "query", "limit", "scope", "agentId", "sessionKeys");
            var query = RequiredString(p, "query").Trim();
            var limit = Math.Min(PositiveInt(p, "limit", 25), 25);
            var scope = p.TryGetProperty("scope", out var sc) ? sc : JsonDocument.Parse("{}").RootElement;
            var scoped = Filter(scope).Select(row => row["key"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            var archivedHidden = !(scope.TryGetProperty("archived", out var a) && a.ValueKind != JsonValueKind.False);
            var hits = new List<object>();
            var excluded = 0;
            foreach (var session in _scenario._sessions)
            {
                if (!_rows.TryGetValue(session.Key, out var row)) continue;
                var matches = session.Texts.Select((text, index) => (text, index))
                    .Where(item => item.text.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 0) continue;
                if (!scoped.Contains(session.Key))
                {
                    if (archivedHidden && (row["archived"]?.GetValue<bool>() ?? false)) excluded++;
                    continue;
                }
                hits.AddRange(matches.Select(item => (object)new
                {
                    sessionKey = session.Key, sessionId = session.Id, messageId = $"{session.Id}-m{item.index + 1}",
                    role = item.index % 2 == 0 ? "user" : "assistant",
                    timestamp = row["updatedAt"]?.GetValue<long>() ?? 0,
                    snippet = item.text, score = 1.0,
                }));
            }
            var truncated = hits.Count > limit;
            var visible = hits.Take(limit).ToArray();
            var keys = visible.Select(hit => JsonSerializer.SerializeToElement(hit).GetProperty("sessionKey").GetString()!).ToHashSet();
            return new
            {
                results = visible,
                sessions = keys.Where(_rows.ContainsKey).Select(key => _rows[key].DeepClone()).ToArray(),
                indexing = query.Equals(HubIndexingQuery, StringComparison.OrdinalIgnoreCase) ? true : (bool?)null,
                truncated = truncated ? true : (bool?)null,
                archivedTranscriptsExcluded = excluded > 0 ? excluded : (int?)null,
            };
        }

        private object Groups() => new { groups = _groups.Select((name, position) => new { name, position }).ToArray() };

        private object PutGroups(JsonElement p)
        {
            ValidateProperties(p, "names");
            if (!p.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Array)
                throw new FixtureRequestException("INVALID_PARAMS", "names must be an array.");
            _groups.Clear();
            _groups.AddRange(names.EnumerateArray().Select(name => name.GetString()?.Trim()).OfType<string>()
                .Where(name => name.Length > 0).Distinct(StringComparer.Ordinal));
            return Changed(new { ok = true, groups = _groups.Select((name, position) => new { name, position }).ToArray() }, null);
        }

        private object CreateGroup(JsonElement p)
        {
            ValidateProperties(p, "name");
            var name = RequiredString(p, "name").Trim();
            var characters = 0;
            foreach (var rune in name.EnumerateRunes())
                characters++;
            if (characters is 0 or > 512)
                throw new FixtureRequestException("INVALID_PARAMS", "Group names must contain 1 to 512 characters.");
            if (!_groups.Contains(name, StringComparer.Ordinal))
                _groups.Add(name);
            return Changed(new { ok = true, groups = _groups.Select((n, position) => new { name = n, position }).ToArray() }, null);
        }

        private object RenameGroup(JsonElement p)
        {
            ValidateProperties(p, "name", "to");
            var name = RequiredString(p, "name");
            var to = RequiredString(p, "to").Trim();
            var index = _groups.IndexOf(name);
            if (index < 0) throw new FixtureRequestException("INVALID_PARAMS", "Unknown group.");
            _groups[index] = to;
            var updated = 0;
            foreach (var row in _rows.Values.Where(row => Str(row, "category") == name)) { row["category"] = to; updated++; }
            return Changed(new { ok = true, groups = _groups.Select((n, position) => new { name = n, position }).ToArray(), updatedSessions = updated }, null);
        }

        private object DeleteGroup(JsonElement p)
        {
            ValidateProperties(p, "name");
            var name = RequiredString(p, "name");
            _groups.Remove(name);
            var updated = 0;
            foreach (var row in _rows.Values.Where(row => Str(row, "category") == name)) { row.Remove("category"); updated++; }
            return Changed(new { ok = true, groups = _groups.Select((n, position) => new { name = n, position }).ToArray(), updatedSessions = updated }, null);
        }

        private object Patch(JsonElement p)
        {
            ValidateProperties(p, "key", "agentId", "expectedSessionId", "label", "pinned", "unread", "archived", "category",
                "color", "icon", "thinkingLevel", "reasoningLevel", "verboseLevel", "fastMode", "model", "expectedMarkedUnreadAt");
            var key = RequiredString(p, "key");
            var row = Find(key);
            if (key == MainSessionKey && OptionalBoolean(p, "archived") == true)
                throw new FixtureRequestException("INVALID_PARAMS", "The main fixture session cannot be archived.");
            foreach (var field in new[] { "label", "pinned", "unread", "archived", "category", "color", "icon", "thinkingLevel", "reasoningLevel", "verboseLevel", "fastMode", "model" })
            {
                if (!p.TryGetProperty(field, out var value)) continue;
                if (value.ValueKind == JsonValueKind.Null) row.Remove(field);
                else row[field] = JsonNode.Parse(value.GetRawText());
            }
            if (p.TryGetProperty("archived", out var archived))
            {
                if (archived.ValueKind == JsonValueKind.True) { row["archivedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); row["archiveReason"] = "manual"; }
                else { row.Remove("archivedAt"); row.Remove("archiveReason"); }
            }
            if (p.TryGetProperty("pinned", out var pinned))
            {
                if (pinned.ValueKind == JsonValueKind.True) row["pinnedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                else row.Remove("pinnedAt");
            }
            return Changed(new { ok = true, key, entry = row.DeepClone() }, key);
        }

        private object Delete(JsonElement p)
        {
            ValidateProperties(p, "key", "agentId", "deleteTranscript", "expectedSessionId", "archivedOnly");
            var key = RequiredString(p, "key");
            var row = Find(key);
            if (key == MainSessionKey)
                throw new FixtureRequestException("INVALID_PARAMS", "The main fixture session cannot be removed.");
            if (OptionalString(p, "expectedSessionId") is { } expected && expected != Str(row, "sessionId"))
                throw new FixtureRequestException("INVALID_PARAMS", "Session was replaced.");
            if (OptionalBoolean(p, "archivedOnly") == true && !(row["archived"]?.GetValue<bool>() ?? false))
                throw new FixtureRequestException("INVALID_PARAMS", "Session is not archived.");
            _rows.Remove(key);
            object? preserved = key == HubPreservedSessionKey
                ? new { id = HubBusyWorktreeId, branch = "openclaw/export-wizard", path = "C:/src/.openclaw-worktrees/export-wizard", reason = "busy" }
                : null;
            return Changed(new { ok = true, key, deleted = true, archived = Array.Empty<string>(), worktreePreserved = preserved }, key);
        }

        private object AssignOwner(JsonElement p)
        {
            ValidateProperties(p, "key", "agentId", "owner");
            var key = RequiredString(p, "key");
            var row = Find(key);
            if (!p.TryGetProperty("owner", out var owner) || owner.ValueKind != JsonValueKind.Object)
                throw new FixtureRequestException("INVALID_PARAMS", "owner is required.");
            ValidateProperties(owner, "type", "id");
            var type = RequiredString(owner, "type");
            var id = RequiredString(owner, "id");
            var label = type == "human"
                ? _profiles.Select(profile => JsonSerializer.SerializeToElement(profile))
                    .FirstOrDefault(profile => profile.GetProperty("id").GetString() == id) is { ValueKind: JsonValueKind.Object } match
                    ? match.GetProperty("displayName").GetString() : throw new FixtureRequestException("INVALID_PARAMS", "Unknown profile.")
                : id == "research" ? "Fixture Research" : "Fixture Main";
            var actor = new JsonObject { ["type"] = type, ["id"] = id, ["label"] = label };
            row["owner"] = new JsonObject { ["actor"] = actor, ["assignedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            return Changed(new { ok = true, key, owner = row["owner"]!.DeepClone() }, key);
        }

        private object Reclaim(JsonElement p)
        {
            ValidateProperties(p, "key", "agentId", "recoverToGateway");
            var key = RequiredString(p, "key");
            var row = Find(key);
            row["placement"] = new JsonObject { ["state"] = "reclaimed", ["generation"] = 3 };
            return Changed(new { ok = true, key, sessionId = Str(row, "sessionId"), placement = row["placement"]!.DeepClone() }, key);
        }

        private object Create(JsonElement p)
        {
            ValidateProperties(p, "parentSessionKey", "agentId", "fork", "forkFrom", "emitCommandHooks", "succeedsParent", "key");
            var parentKey = RequiredString(p, "parentSessionKey");
            var parent = Find(parentKey);
            var agent = Str(parent, "agentId") ?? "main";
            var key = $"agent:{agent}:hub-fork-{++_created}";
            var row = parent.DeepClone().AsObject();
            row["key"] = key;
            row["sessionId"] = $"00000000-0000-4000-9000-{_created:D12}";
            row["label"] = $"{Str(parent, "label")} (fork)";
            row["updatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            row.Remove("pinned"); row.Remove("pinnedAt"); row.Remove("archived");
            _rows[key] = row;
            _keyOrder.Insert(0, key);
            return Changed(new { ok = true, key, sessionId = Str(row, "sessionId") }, key);
        }

        private object Lifecycle(string method, JsonElement p)
        {
            ValidateProperties(p, "key", "agentId", "reason", "expectedSessionId", "maxLines");
            var key = RequiredString(p, "key");
            Find(key);
            return method == "sessions.compact"
                ? new { ok = true, key, compacted = false, reason = "already compact" }
                : (object)new { ok = true, key };
        }

        private static object Branches(JsonElement p)
        {
            ValidateProperties(p, "repoRoot", "includeRepositoryStatus");
            _ = RequiredString(p, "repoRoot");
            return new
            {
                branches = new[]
                {
                    new { name = "main", kind = "local" }, new { name = "release/3.2", kind = "local" },
                    new { name = "feature/offline-sync", kind = "local" }, new { name = "origin/main", kind = "remote" },
                },
                defaultBranch = "main", headBranch = "main", repositoryStatus = "git",
            };
        }

        private object CreateWorktree(JsonElement p)
        {
            ValidateProperties(p, "repoRoot", "name", "baseRef", "profiles", "expectedOwnerId", "expectedRepoIdentity");
            var repoRoot = RequiredString(p, "repoRoot");
            var name = OptionalString(p, "name") ?? $"worktree-{_worktrees.Count + 1}";
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]{0,63}$"))
                throw new FixtureRequestException("INVALID_PARAMS", "name must use lowercase letters, digits and hyphens.");
            if (_worktrees.Any(w => Str(w, "name") == name && w["removedAt"] is null))
                throw new FixtureRequestException("INVALID_PARAMS", "A worktree with that name already exists.");
            var record = new JsonObject
            {
                ["id"] = $"wt-created-{_worktrees.Count + 1}", ["name"] = name, ["repoFingerprint"] = "0123456789abcdef",
                ["repoRoot"] = repoRoot, ["path"] = $"C:/src/.openclaw-worktrees/{name}", ["branch"] = $"openclaw/{name}",
                ["baseRef"] = OptionalString(p, "baseRef") ?? "main", ["ownerKind"] = "manual",
                ["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["lastActiveAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            _worktrees.Add(record);
            return record.DeepClone();
        }

        private object RemoveWorktree(JsonElement p)
        {
            ValidateProperties(p, "id", "force", "ifLossless", "exactState", "expectedOwnerId");
            var record = FindWorktree(RequiredString(p, "id"));
            if (Str(record, "id") == HubBusyWorktreeId && OptionalBoolean(p, "force") != true)
                return new { removed = false, snapshotError = "The worktree has uncommitted changes and the safety snapshot failed." };
            record["removedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            record["snapshotRef"] = $"refs/openclaw/snapshots/{Str(record, "name")}";
            return new { removed = true, snapshotRef = Str(record, "snapshotRef") };
        }

        private object RestoreWorktree(JsonElement p)
        {
            ValidateProperties(p, "id", "recoverExactState", "expectedOwnerId");
            var record = FindWorktree(RequiredString(p, "id"));
            record.Remove("removedAt");
            record["lastActiveAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return record.DeepClone();
        }

        private object Gc(JsonElement p)
        {
            ValidateProperties(p, "jobId", "retryDeferred", "expectedOwnerId");
            var jobId = OptionalString(p, "jobId");
            if (jobId is null)
            {
                _gcPolls = 0;
                return new { jobId = "gc-fixture-1", state = "running", removed = Array.Empty<string>(), orphansDeleted = 0, snapshotsPruned = 0 };
            }
            if (jobId != "gc-fixture-1")
                throw new FixtureRequestException("INVALID_PARAMS", "Unknown maintenance job.");
            if (++_gcPolls < 2)
                return new { jobId, state = "running", removed = Array.Empty<string>(), orphansDeleted = 0, snapshotsPruned = 0 };
            return new
            {
                jobId, state = "completed", outcome = "completed", removed = Array.Empty<string>(), orphansDeleted = 1, snapshotsPruned = 2,
                issues = new[] { new { stage = "idle", outcome = "deferred", reason = "export-wizard is still in use." } },
            };
        }

        private JsonObject Find(string key) => _rows.TryGetValue(key, out var row) ? row
            : throw new FixtureRequestException("INVALID_PARAMS", "Unknown fixture session key.");

        private JsonObject FindWorktree(string id) => _worktrees.FirstOrDefault(w => Str(w, "id") == id)
            ?? throw new FixtureRequestException("INVALID_PARAMS", "Unknown worktree.");

        private static string? Str(JsonObject row, string name) =>
            row[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private static FixtureReply Changed(object payload, string? key) => new(payload,
        [
            new { type = "event", @event = "sessions.changed", payload = new { reason = "fixture", sessionKey = key } },
        ]);
    }
}
