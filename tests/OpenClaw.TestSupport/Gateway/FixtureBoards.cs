using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.TestSupport.Gateway;

/// <summary>
/// Synthetic session dashboards for the <c>dashboards</c> fixture scenario:
/// dashboard <c>sessions.list</c> rows, <c>board.get</c> snapshots with
/// minted view tickets, ticketed widget documents on the Gateway origin, and
/// a sandbox proxy on a separate loopback port that follows the Gateway's
/// <c>/mcp-app-sandbox</c> handshake. No real board or capability is touched.
/// </summary>
public sealed class FixtureBoards
{
    public const string OpsSessionKey = "agent:main:dashboard:ops";
    public const string ReleaseSessionKey = "agent:main:dashboard:release";
    public const string ResearchSessionKey = "agent:research:dashboard:study";
    public const string EmptySessionKey = "agent:main:dashboard:empty";
    public const string BoardPathPrefix = "/__openclaw__/board/";
    public const string SandboxPath = "/mcp-app-sandbox";
    public const int TicketTtlMs = 20 * 60_000;

    public static readonly string[] Methods = ["board.get"];
    public static readonly string[] Events = ["board.changed", "board.command"];

    // Relative to the run so gallery cards show recent relative times; never part of the scenario hash.
    private static readonly long s_now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 60_000 * 60_000;
    private readonly ConcurrentDictionary<string, (string SessionKey, string Name)> _tickets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BoardDefinition> _boards;

    public FixtureBoards()
    {
        _boards = new Dictionary<string, BoardDefinition>(StringComparer.Ordinal)
        {
            [OpsSessionKey] = OpsBoard(),
            [ReleaseSessionKey] = ReleaseBoard(),
            [ResearchSessionKey] = ResearchBoard(),
            [EmptySessionKey] = new BoardDefinition(
                OpsRow(EmptySessionKey, "main", "Fixture: empty board", "fixture-owner", "Fixture Owner", s_now - 86_400_000, "idle", "Nothing pinned yet."),
                [new Tab("main", "Main", 0)], []),
        };
    }

    /// <summary>Loopback port of the sandbox proxy listener; set by the fixture server.</summary>
    public int SandboxPort { get; internal set; }

    public IReadOnlyCollection<string> SessionKeys => _boards.Keys;

    internal static object HelloWidgetKinds() => new object[]
    {
        new { pluginId = "session", kind = "session:report", label = "Report" },
        new { pluginId = "session", kind = "session:website", label = "Website" },
        new { pluginId = "session", kind = "session:progress", label = "Session progress" },
    };

    internal object ListSessions(JsonElement p)
    {
        GatewayScenario.ValidateProperties(p, "rowMode", "source", "includeGlobal", "includeUnknown", "configuredAgentsOnly",
            "excludeDock", "hasBoard", "archived", "limit", "offset", "agentId");
        var agent = GatewayScenario.OptionalString(p, "agentId");
        var limit = GatewayScenario.PositiveInt(p, "limit", int.MaxValue);
        var offset = p.TryGetProperty("offset", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : 0;
        var rows = _boards.Values.Select(board => board.Row)
            .Where(row => agent is null || row["agentId"]!.GetValue<string>() == agent)
            .OrderByDescending(row => row["updatedAt"]!.GetValue<long>())
            .ToArray();
        var page = rows.Skip(offset).Take(limit).Select(row => row.DeepClone()).ToArray();
        var hasMore = offset + page.Length < rows.Length;
        return new
        {
            ts = s_now,
            count = page.Length,
            totalCount = rows.Length,
            hasMore,
            nextOffset = hasMore ? offset + page.Length : (int?)null,
            sessions = page,
        };
    }

    internal object GetBoard(JsonElement p)
    {
        GatewayScenario.ValidateProperties(p, "sessionKey", "agentId");
        var sessionKey = GatewayScenario.RequiredString(p, "sessionKey");
        if (!_boards.TryGetValue(sessionKey, out var board))
            throw new FixtureRequestException("NOT_FOUND", "board session not found");
        var widgets = board.Widgets.Select(widget =>
        {
            var node = JsonSerializer.SerializeToNode(widget.Wire)!.AsObject();
            if (widget.Document is not null && widget.GrantState is "none" or "granted")
            {
                var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                _tickets[ticket] = (sessionKey, widget.Name);
                node["frameUrl"] = $"{BoardPathPrefix}{Uri.EscapeDataString(sessionKey)}/{Uri.EscapeDataString(widget.Name)}/index.html?bt={ticket}";
                node["viewTicket"] = ticket;
                node["viewTicketTtlMs"] = TicketTtlMs;
                node["viewGeneration"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionKey + widget.Name)))[..32].ToLowerInvariant();
                node["sandboxUrl"] = SandboxPath + "?v=fixture";
                node["sandboxPort"] = SandboxPort;
            }
            return node;
        }).ToArray();
        return new
        {
            sessionKey,
            revision = board.Revision,
            tabs = board.Tabs.Select(tab => new { tabId = tab.Id, title = tab.Title, position = tab.Position, chatDock = "right" }).ToArray(),
            widgets,
        };
    }

    /// <summary>Serves a ticketed widget document; returns the HTTP status and body.</summary>
    internal (int Status, string Body) ServeWidget(string pathAndQuery)
    {
        var queryIndex = pathAndQuery.IndexOf('?');
        var path = queryIndex < 0 ? pathAndQuery : pathAndQuery[..queryIndex];
        var query = queryIndex < 0 ? "" : pathAndQuery[(queryIndex + 1)..];
        var ticket = query.Split('&').Select(part => part.Split('=', 2)).FirstOrDefault(part => part[0] == "bt") is { Length: 2 } pair
            ? Uri.UnescapeDataString(pair[1]) : null;
        var segments = path[BoardPathPrefix.Length..].Split('/');
        if (segments.Length != 3 || segments[2] != "index.html")
            return (404, "Not Found");
        if (ticket is null || !_tickets.TryGetValue(ticket, out var owner) ||
            owner.SessionKey != Uri.UnescapeDataString(segments[0]) || owner.Name != Uri.UnescapeDataString(segments[1]))
            return (401, "Unauthorized");
        var widget = _boards[owner.SessionKey].Widgets.First(candidate => candidate.Name == owner.Name);
        return widget.FailStatus is { } status ? (status, "Fixture widget failure") : (200, WrapWidget(widget.Title, widget.Document!));
    }

    /// <summary>
    /// Trusted sandbox proxy document. Mirrors the Gateway handshake: it binds
    /// its parent origin, reports <c>sandbox-proxy-ready</c> with its URL,
    /// renders each delivered document into a fresh
    /// <c>sandbox="allow-scripts allow-forms"</c> frame, and relays messages.
    /// </summary>
    internal static string SandboxDocument() => """
        <!doctype html>
        <meta charset="utf-8">
        <style>html,body{height:100%;margin:0;background:transparent}iframe{display:block;width:100%;height:100%;border:0;background:transparent}</style>
        <script>
          const initialTheme = new URLSearchParams(location.hash.slice(1)).get("theme");
          if (initialTheme === "light" || initialTheme === "dark") document.documentElement.style.colorScheme = initialTheme;
        </script>
        <body><script>
        (() => {
          if (window.self === window.top) throw new Error("invalid sandbox host");
          const parent = new URL(location.ancestorOrigins?.[0] ?? document.referrer);
          if (parent.protocol !== "http:" && parent.protocol !== "https:") throw new Error("invalid sandbox parent");
          const hostOrigin = parent.origin;
          const createInner = () => {
            const frame = document.createElement("iframe");
            frame.setAttribute("sandbox", "allow-scripts allow-forms");
            return frame;
          };
          let inner = createInner();
          document.body.appendChild(inner);
          window.addEventListener("message", (event) => {
            if (event.source === window.parent) {
              if (event.origin !== hostOrigin) return;
              const data = event.data;
              if (data?.method === "ui/notifications/sandbox-resource-ready") {
                const params = data.params ?? {};
                const next = createInner();
                next.addEventListener("load", () => {
                  if (inner !== next) return;
                  window.parent.postMessage({ jsonrpc: "2.0", method: "ui/notifications/sandbox-resource-loaded", params: { renderId: params.renderId } }, hostOrigin);
                }, { once: true });
                next.srcdoc = String(params.html ?? "");
                inner.replaceWith(next);
                inner = next;
                return;
              }
              const theme = data?.type === "openclaw:widget-theme" ? data.mode : undefined;
              if (theme === "light" || theme === "dark") document.documentElement.style.colorScheme = theme;
              inner.contentWindow?.postMessage(data, "*");
              return;
            }
            if (event.source === inner.contentWindow) {
              const port = event.ports && event.ports[0];
              window.parent.postMessage(event.data, hostOrigin, port ? [port] : []);
            }
          });
          window.parent.postMessage({ jsonrpc: "2.0", method: "ui/notifications/sandbox-proxy-ready", params: { sandboxUrl: location.href } }, hostOrigin);
        })();
        </script></body>
        """;

    internal static string SandboxContentSecurityPolicy() =>
        "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "connect-src 'none'; frame-src 'none'; object-src 'none'; form-action 'none'; base-uri 'self'; frame-ancestors http: https:";

    /// <summary>Wrapper equivalent to the Gateway widget shell: size reporting and host theme tokens.</summary>
    private static string WrapWidget(string title, string body) => $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><title>{{WebUtility.HtmlEncode(title)}}</title>
        <style>
        :root{color-scheme:light dark;--surface:#fafafa;--card:#fff;--text:#222;--text-strong:#111;--muted:#666;--border:#e5e5e5;--accent:#0067c0;--accent-fill:#0067c0;--accent-fg:#fff;--ok:#0f7b0f;--warn:#9d5d00;--danger:#c42b1c;--radius:8px;--font-body:"Segoe UI",sans-serif}
        html,body{margin:0;background:transparent}
        body{display:flow-root;font:14px/1.5 var(--font-body);color:var(--text)}
        h1,h2,h3{margin:0 0 8px;color:var(--text-strong);font-weight:600}h2{font-size:16px}
        .row{display:flex;gap:12px;flex-wrap:wrap}.tile{flex:1 1 120px;background:var(--elevated,var(--card));border:1px solid var(--border);border-radius:var(--radius);padding:10px 12px}
        .metric{font-size:24px;font-weight:600;color:var(--text-strong)}.muted{color:var(--muted);font-size:12px}
        .badge{display:inline-block;font-size:12px;padding:2px 10px;border-radius:999px;color:var(--accent-fg);background:var(--accent-fill)}
        .ok{color:var(--ok)}.warn{color:var(--warn)}.danger{color:var(--danger)}
        </style>
        <script>(() => {
          const post = window.parent.postMessage.bind(window.parent);
          let last = 0;
          const report = () => { const b = document.body; if (!b) return;
            const h = Math.ceil(Math.max(b.scrollHeight, b.offsetHeight, b.getBoundingClientRect().height));
            if (h && h !== last) { last = h; post({ type: "openclaw:widget-size", height: h }, "*"); } };
          addEventListener("load", report);
          addEventListener("DOMContentLoaded", () => new ResizeObserver(report).observe(document.body));
          addEventListener("message", (event) => {
            const data = event.data;
            if (data?.type !== "openclaw:widget-theme" || typeof data.tokens !== "object") return;
            document.documentElement.style.colorScheme = data.mode === "light" ? "light" : "dark";
            for (const [token, value] of Object.entries(data.tokens))
              if (typeof value === "string") document.documentElement.style.setProperty("--" + token, value);
          });
        })();</script>
        </head><body>{{body}}</body></html>
        """;

    private static JsonObject OpsRow(string key, string agentId, string title, string authorId, string authorLabel, long updatedAt, string status, string preview) =>
        new()
        {
            ["key"] = key,
            ["agentId"] = agentId,
            ["kind"] = "direct",
            ["label"] = title,
            ["displayName"] = title,
            ["status"] = status,
            ["updatedAt"] = updatedAt,
            ["archived"] = false,
            ["lastMessagePreview"] = preview,
            ["createdActor"] = new JsonObject { ["type"] = "human", ["id"] = authorId, ["label"] = authorLabel },
        };

    private static BoardDefinition OpsBoard()
    {
        const string statusCard = """
            <h2>Service health</h2>
            <div class="row">
              <div class="tile"><div class="muted">Uptime</div><div class="metric ok">99.97%</div></div>
              <div class="tile"><div class="muted">p95 latency</div><div class="metric">182 ms</div></div>
              <div class="tile"><div class="muted">Open incidents</div><div class="metric warn">2</div></div>
            </div>
            <p class="muted" style="margin-top:10px">Synthetic fixture data. <span class="badge">Live</span></p>
            """;
        const string deployChart = """
            <h2>Deployments this week</h2>
            <svg viewBox="0 0 600 140" width="100%" height="140" role="img" aria-label="Deployments per day">
              <polyline fill="none" stroke="var(--accent)" stroke-width="3" points="10,110 100,90 190,100 280,60 370,70 460,30 590,40"/>
              <g fill="var(--accent)"><circle cx="10" cy="110" r="4"/><circle cx="100" cy="90" r="4"/><circle cx="190" cy="100" r="4"/><circle cx="280" cy="60" r="4"/><circle cx="370" cy="70" r="4"/><circle cx="460" cy="30" r="4"/><circle cx="590" cy="40" r="4"/></g>
            </svg>
            <p class="muted">Mon to Sun. Rendered inside the isolated widget sandbox.</p>
            """;
        Widget[] widgets =
        [
            Widget.Html("status-card", "overview", "Service health", 6, 4, 0, statusCard),
            Widget.Plugin("weekly-report", "overview", "Weekly report", 6, 6, 1, "session:report", Report()),
            Widget.Html("deploy-chart", "overview", "Deployments", 12, 4, 2, deployChart),
            Widget.Html("needs-access", "overview", "Partner sync", 6, 4, 3, "<p>Sync status</p>", grantState: "pending",
                declared: new { netOrigins = new[] { "https://api.example.com" }, tools = new[] { "cron.trigger:nightly-sync" } }),
            Widget.Html("broken-widget", "overview", "Broken widget", 6, 4, 4, "<p>unused</p>", failStatus: 404),
            Widget.McpApp("pinned-app", "metrics", "Pinned MCP app", 6, 4, 0),
            Widget.Plugin("run-progress", "metrics", "Run progress", 6, 4, 1, "session:progress", new { sessionKey = "agent:main:main" }),
            Widget.Plugin("legacy-timeline", "metrics", "Legacy timeline", 6, 4, 2, "acme:timeline", new { }),
            Widget.Html("rejected-widget", "metrics", "Rejected widget", 6, 4, 3, "<p>unused</p>", grantState: "rejected",
                declared: new { netOrigins = new[] { "https://tracker.example.net" } }),
            Widget.Plugin("docs-site", "integrations", "Docs website", 12, 8, 0, "session:website", new { url = "https://example.com/" }),
        ];
        return new BoardDefinition(
            OpsRow(OpsSessionKey, "main", "Ops overview", "fixture-owner", "Fixture Owner", s_now - 120_000, "running", "Deploy finished; health is green."),
            [new Tab("overview", "Overview", 0), new Tab("metrics", "Metrics", 1), new Tab("integrations", "Integrations", 2)],
            widgets);
    }

    private static BoardDefinition ReleaseBoard()
    {
        const string checklist = """
            <h2>Release checklist</h2>
            <p class="ok">&#10003; Build signed</p><p class="ok">&#10003; Smoke tests</p><p class="warn">&#9679; Store submission pending</p>
            """;
        return new BoardDefinition(
            OpsRow(ReleaseSessionKey, "main", "Release tracker", "release-bot", "Release Bot", s_now - 3 * 3_600_000, "idle", "Store submission pending review."),
            [new Tab("main", "Release", 0)],
            [
                Widget.Html("checklist", "main", "Checklist", 4, 4, 0, checklist),
                Widget.Plugin("burndown", "main", "Burndown", 8, 5, 1, "session:report", new
                {
                    blocks = new object[]
                    {
                        new { type = "chart", title = "Open issues", style = "line", points = new[] { new { label = "W1", value = 42 }, new { label = "W2", value = 31 }, new { label = "W3", value = 18 }, new { label = "W4", value = 7 } } },
                    },
                }),
            ]);
    }

    private static BoardDefinition ResearchBoard()
    {
        const string notes = """
            <h2>Findings</h2><p>Three sources agree on the synthetic result.</p><p class="muted">Fixture research agent.</p>
            """;
        return new BoardDefinition(
            OpsRow(ResearchSessionKey, "research", "Research study", "fixture-research", "Fixture Research", s_now - 2 * 86_400_000, "idle", "Summary drafted."),
            [new Tab("main", "Study", 0)],
            [Widget.Html("findings", "main", "Findings", 12, 4, 0, notes)]);
    }

    private static object Report() => new
    {
        blocks = new object[]
        {
            new { type = "metrics", items = new object[] { new { label = "Tickets closed", value = "128", detail = "+12% week over week" }, new { label = "Escalations", value = "3" } } },
            new { type = "chart", title = "Tickets per day", style = "bar", points = new[] { new { label = "Mon", value = 18 }, new { label = "Tue", value = 22 }, new { label = "Wed", value = 15 }, new { label = "Thu", value = 27 }, new { label = "Fri", value = 21 } } },
            new { type = "table", title = "Top queues", columns = new[] { "Queue", "Open", "SLA" }, rows = new[] { new[] { "Billing", "12", "98%" }, new[] { "Platform", "7", "99%" } } },
            new { type = "links", title = "References", items = new object[] { new { label = "Runbook", url = "https://example.com/runbook", detail = "Synthetic link" } } },
        },
    };

    private sealed record Tab(string Id, string Title, int Position);

    private sealed record BoardDefinition(JsonObject Row, Tab[] Tabs, Widget[] Widgets)
    {
        public int Revision => 7;
    }

    private sealed record Widget(
        string Name, string TabId, string Title, string ContentKind, int SizeW, int SizeH, int Position,
        string GrantState, string? Document, int? FailStatus, object Wire, object? Declared)
    {
        public static Widget Html(string name, string tab, string title, int w, int h, int position, string document,
            string grantState = "none", object? declared = null, int? failStatus = null) =>
            new(name, tab, title, "html", w, h, position, grantState, document, failStatus,
                WireOf(name, tab, title, "html", w, h, position, grantState, null, null, declared), declared);

        public static Widget McpApp(string name, string tab, string title, int w, int h, int position) =>
            new(name, tab, title, "mcp-app", w, h, position, "none", null, null,
                WireOf(name, tab, title, "mcp-app", w, h, position, "none", null, null, null), null);

        public static Widget Plugin(string name, string tab, string title, int w, int h, int position, string kind, object props) =>
            new(name, tab, title, "plugin", w, h, position, "none", null, null,
                WireOf(name, tab, title, "plugin", w, h, position, "none", kind, props, null), null);

        private static Dictionary<string, object?> WireOf(string name, string tab, string title, string kind, int w, int h,
            int position, string grantState, string? pluginKind, object? props, object? declared)
        {
            var wire = new Dictionary<string, object?>
            {
                ["name"] = name, ["tabId"] = tab, ["title"] = title, ["contentKind"] = kind,
                ["sizeW"] = w, ["sizeH"] = h, ["position"] = position, ["grantState"] = grantState,
                ["revision"] = 1, ["instanceId"] = "fixture-" + name,
            };
            if (pluginKind is not null) wire["pluginKind"] = pluginKind;
            if (props is not null) wire["props"] = props;
            if (declared is not null) wire["declared"] = declared;
            return wire;
        }
    }
}
