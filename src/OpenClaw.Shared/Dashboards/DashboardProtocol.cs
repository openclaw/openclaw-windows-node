using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared.Dashboards;

/// <summary>
/// Wire contract for the dashboards surface: <c>sessions.list</c> dashboard
/// rows, <c>board.get</c> snapshots, board events, and the Gateway hello's
/// trusted plugin widget kinds. Pure so parsing and scoping stay testable.
/// </summary>
public static partial class DashboardProtocol
{
    /// <summary>Upstream <c>SIDEBAR_SESSION_ROSTER_LIMIT</c>: rows per gallery page.</summary>
    public const int GalleryPageSize = 200;

    /// <summary>Upper bound on gallery pages fetched for one load (200 x 25 rows).</summary>
    public const int MaxGalleryPages = 25;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex WidgetNamePattern();

    [GeneratedRegex("^[a-z0-9-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex TabIdPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}:[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginKindPattern();

    public static bool IsValidWidgetName(string? name) => name is not null && WidgetNamePattern().IsMatch(name);

    public static bool IsValidTabId(string? tabId) => tabId is not null && TabIdPattern().IsMatch(tabId);

    /// <summary>
    /// Builds the upstream <c>dashboardSessionListQuery</c> wire params:
    /// dashboard rows of sessions that own a board, active and archived,
    /// excluding plugin dock conversations, optionally scoped to one agent.
    /// </summary>
    public static Dictionary<string, object?> BuildSessionListParams(string? agentId, int offset = 0, int limit = GalleryPageSize)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["rowMode"] = "dashboard",
            ["source"] = "dashboard",
            ["includeGlobal"] = true,
            ["includeUnknown"] = true,
            ["configuredAgentsOnly"] = true,
            ["excludeDock"] = true,
            ["hasBoard"] = true,
            ["archived"] = "all",
            ["limit"] = Math.Max(1, limit),
        };
        var normalizedAgentId = agentId?.Trim();
        if (!string.IsNullOrEmpty(normalizedAgentId))
            parameters["agentId"] = normalizedAgentId;
        if (offset > 0)
            parameters["offset"] = offset;
        return parameters;
    }

    /// <summary>Builds <c>board.get</c> params.</summary>
    public static Dictionary<string, object?> BuildBoardParams(BoardSessionTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target.SessionKey);
        var parameters = new Dictionary<string, object?> { ["sessionKey"] = target.SessionKey.Trim() };
        var agentId = target.AgentId?.Trim();
        if (!string.IsNullOrEmpty(agentId))
            parameters["agentId"] = agentId;
        return parameters;
    }

    /// <summary>
    /// Parses a dashboard <c>sessions.list</c> page. <paramref name="parseSession"/>
    /// projects the shared session facts (title inputs, agent, classification);
    /// this method adds the dashboard facts the gallery shows.
    /// </summary>
    public static DashboardSessionPage ParseSessionPage(JsonElement payload, Func<JsonElement, SessionInfo?> parseSession)
    {
        ArgumentNullException.ThrowIfNull(parseSession);
        if (payload.ValueKind != JsonValueKind.Object)
            return new DashboardSessionPage([], false, null, 0);

        var rows = ImmutableArray.CreateBuilder<DashboardSessionRow>();
        if (payload.TryGetProperty("sessions", out var sessions) && sessions.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sessions.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(ReadString(item, "key")))
                    continue;
                var session = parseSession(item);
                if (session is null || string.IsNullOrWhiteSpace(session.Key))
                    continue;
                rows.Add(new DashboardSessionRow
                {
                    Session = session,
                    Status = ReadString(item, "status"),
                    UpdatedAtMs = ReadLong(item, "updatedAt"),
                    LastMessagePreview = ReadString(item, "lastMessagePreview"),
                    CreatedActor = ReadActor(item, "createdActor"),
                    OwnerActor = item.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object
                        ? ReadActor(owner, "actor")
                        : null,
                    Archived = item.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True,
                });
            }
        }

        var hasMore = payload.TryGetProperty("hasMore", out var more) && more.ValueKind == JsonValueKind.True;
        var nextOffset = ReadLong(payload, "nextOffset") is { } next and >= 0 and <= int.MaxValue ? (int?)next : null;
        var count = ReadLong(payload, "count") is { } c and >= 0 and <= int.MaxValue ? (int)c : rows.Count;
        return new DashboardSessionPage(rows.ToImmutable(), hasMore, nextOffset, count);
    }

    /// <summary>
    /// Next gallery offset after <paramref name="page"/> was read at
    /// <paramref name="offset"/>, or null when the gallery is complete.
    /// Mirrors upstream: <c>nextOffset ?? count</c> while <c>hasMore</c>.
    /// </summary>
    public static int? NextPageOffset(DashboardSessionPage page, int offset)
    {
        if (!page.HasMore)
            return null;
        var next = page.NextOffset ?? offset + page.Count;
        return next > offset ? next : null;
    }

    /// <summary>
    /// Parses a <c>board.get</c>/<c>board.update</c> snapshot. Malformed tabs
    /// and widgets are dropped rather than failing the whole board; sizes are
    /// clamped to the protocol bounds.
    /// </summary>
    public static BoardSnapshot ParseBoardSnapshot(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The Gateway returned an invalid board snapshot.");
        var sessionKey = ReadString(payload, "sessionKey");
        if (string.IsNullOrWhiteSpace(sessionKey))
            throw new InvalidOperationException("The Gateway returned a board without a session key.");

        var tabs = ImmutableArray.CreateBuilder<BoardTab>();
        var seenTabs = new HashSet<string>(StringComparer.Ordinal);
        if (payload.TryGetProperty("tabs", out var tabArray) && tabArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in tabArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var tabId = ReadString(item, "tabId");
                if (!IsValidTabId(tabId) || !seenTabs.Add(tabId!))
                    continue;
                var title = ReadString(item, "title");
                tabs.Add(new BoardTab(
                    tabId!,
                    string.IsNullOrWhiteSpace(title) ? tabId! : title!,
                    (int)Math.Clamp(ReadLong(item, "position") ?? 0, 0, int.MaxValue),
                    ReadString(item, "chatDock") ?? "right"));
            }
        }

        var widgets = ImmutableArray.CreateBuilder<BoardWidget>();
        var seenWidgets = new HashSet<string>(StringComparer.Ordinal);
        if (payload.TryGetProperty("widgets", out var widgetArray) && widgetArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in widgetArray.EnumerateArray())
            {
                if (TryParseWidget(item) is { } widget && seenWidgets.Add(widget.Name))
                    widgets.Add(widget);
            }
        }

        return new BoardSnapshot(
            sessionKey!,
            (int)Math.Clamp(ReadLong(payload, "revision") ?? 0, 0, int.MaxValue),
            tabs.ToImmutable(),
            widgets.ToImmutable());
    }

    private static BoardWidget? TryParseWidget(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        var name = ReadString(item, "name");
        var tabId = ReadString(item, "tabId");
        if (!IsValidWidgetName(name) || !IsValidTabId(tabId))
            return null;
        BoardWidgetContentKind? kind = ReadString(item, "contentKind") switch
        {
            "html" => BoardWidgetContentKind.Html,
            "mcp-app" => BoardWidgetContentKind.McpApp,
            "plugin" => BoardWidgetContentKind.Plugin,
            _ => null,
        };
        if (kind is null)
            return null;
        var revision = ReadLong(item, "revision");
        if (revision is null or < 1 or > int.MaxValue)
            return null;
        var pluginKind = ReadString(item, "pluginKind");
        if (pluginKind is not null && !PluginKindPattern().IsMatch(pluginKind))
            pluginKind = null;
        var sandboxPort = ReadLong(item, "sandboxPort");
        var ttl = ReadLong(item, "viewTicketTtlMs");

        return new BoardWidget
        {
            Name = name!,
            TabId = tabId!,
            Title = ReadString(item, "title"),
            ContentKind = kind.Value,
            PluginKind = pluginKind,
            Props = item.TryGetProperty("props", out var props) && props.ValueKind == JsonValueKind.Object
                ? props.Clone()
                : null,
            Presentation = ReadString(item, "presentation") switch
            {
                "full-bleed" => BoardWidgetPresentation.FullBleed,
                "frameless" => BoardWidgetPresentation.Frameless,
                _ => BoardWidgetPresentation.Card,
            },
            HeightMode = ReadString(item, "heightMode") == "fixed" ? BoardWidgetHeightMode.Fixed : BoardWidgetHeightMode.Auto,
            SizeW = (int)Math.Clamp(ReadLong(item, "sizeW") ?? 1, 1, BoardGridLayout.Columns),
            SizeH = (int)Math.Clamp(ReadLong(item, "sizeH") ?? 1, 1, BoardGridLayout.MaxItemRows),
            Position = (int)Math.Clamp(ReadLong(item, "position") ?? 0, 0, int.MaxValue),
            GrantState = ReadString(item, "grantState") switch
            {
                "pending" => BoardWidgetGrantState.Pending,
                "granted" => BoardWidgetGrantState.Granted,
                "rejected" => BoardWidgetGrantState.Rejected,
                _ => BoardWidgetGrantState.None,
            },
            Revision = (int)revision.Value,
            InstanceId = ReadString(item, "instanceId"),
            DeclaredSummary = ReadStringArray(item, "declaredSummary"),
            Declared = item.TryGetProperty("declared", out var declared) && declared.ValueKind == JsonValueKind.Object
                ? new BoardWidgetDeclared(ReadStringArray(declared, "netOrigins"), ReadStringArray(declared, "tools"))
                : BoardWidgetDeclared.Empty,
            FrameUrl = ReadString(item, "frameUrl"),
            ViewTicket = ReadString(item, "viewTicket"),
            ViewTicketTtlMs = ttl is >= 1 and <= int.MaxValue ? (int)ttl.Value : null,
            ViewGeneration = ReadString(item, "viewGeneration"),
            SandboxUrl = ReadString(item, "sandboxUrl"),
            SandboxPort = sandboxPort is >= 1 and <= 65535 ? (int)sandboxPort.Value : null,
            SandboxOrigin = ReadString(item, "sandboxOrigin"),
            KindLabel = ReadString(item, "kindLabel"),
        };
    }

    /// <summary>Parses a <c>board.changed</c> event payload.</summary>
    public static BoardChangedEvent? ParseBoardChanged(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;
        var sessionKey = ReadString(payload, "sessionKey");
        if (string.IsNullOrWhiteSpace(sessionKey))
            return null;
        var widget = ReadString(payload, "widget");
        return new BoardChangedEvent(
            sessionKey,
            (int)Math.Clamp(ReadLong(payload, "revision") ?? 0, 0, int.MaxValue),
            IsValidWidgetName(widget) ? widget : null);
    }

    /// <summary>Parses a <c>board.command</c> event payload (<c>focus_tab</c> or <c>set_chat_dock</c>).</summary>
    public static BoardCommandEvent? ParseBoardCommand(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("command", out var command) ||
            command.ValueKind != JsonValueKind.Object)
            return null;
        var sessionKey = ReadString(payload, "sessionKey");
        var kind = ReadString(command, "kind");
        if (string.IsNullOrWhiteSpace(sessionKey))
            return null;
        return kind switch
        {
            "focus_tab" when IsValidTabId(ReadString(command, "tabId")) =>
                new BoardCommandEvent(sessionKey, kind, ReadString(command, "tabId"), null),
            "set_chat_dock" when ReadString(command, "dock") is "left" or "right" or "bottom" or "hidden" =>
                new BoardCommandEvent(sessionKey, kind, null, ReadString(command, "dock")),
            _ => null,
        };
    }

    /// <summary>Reads <c>controlUiWidgetKinds</c> from a hello-ok payload.</summary>
    public static ImmutableArray<PluginWidgetKind> ParsePluginWidgetKinds(JsonElement hello)
    {
        if (hello.ValueKind != JsonValueKind.Object ||
            !hello.TryGetProperty("controlUiWidgetKinds", out var kinds) ||
            kinds.ValueKind != JsonValueKind.Array)
            return [];
        var result = ImmutableArray.CreateBuilder<PluginWidgetKind>();
        foreach (var item in kinds.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            var pluginId = ReadString(item, "pluginId");
            var kind = ReadString(item, "kind");
            var label = ReadString(item, "label");
            if (!string.IsNullOrWhiteSpace(pluginId) && !string.IsNullOrWhiteSpace(kind))
                result.Add(new PluginWidgetKind(pluginId, kind, string.IsNullOrWhiteSpace(label) ? kind : label));
        }
        return result.ToImmutable();
    }

    private static DashboardActor? ReadActor(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var actor) || actor.ValueKind != JsonValueKind.Object)
            return null;
        var id = ReadString(actor, "id");
        var label = ReadString(actor, "label");
        return id is null && label is null ? null : new DashboardActor(id, label);
    }

    private static string? ReadString(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static long? ReadLong(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Number)
            return null;
        if (value.TryGetInt64(out var integer))
            return integer;
        return value.TryGetDouble(out var number) && double.IsFinite(number) ? (long)number : null;
    }

    private static ImmutableArray<string> ReadStringArray(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array)
            return [];
        return [.. values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            .Select(value => value.GetString()!)];
    }
}
