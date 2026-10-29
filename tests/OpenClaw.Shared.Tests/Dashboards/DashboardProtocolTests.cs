using System.Text.Json;
using OpenClaw.Shared.Dashboards;
using Xunit;

namespace OpenClaw.Shared.Tests.Dashboards;

public class DashboardProtocolTests
{
    [Fact]
    public void SessionListParams_MatchUpstreamDashboardQueryAndScopeToAgent()
    {
        var parameters = DashboardProtocol.BuildSessionListParams(" research ", offset: 200);

        Assert.Equal("dashboard", parameters["rowMode"]);
        Assert.Equal("dashboard", parameters["source"]);
        Assert.Equal(true, parameters["hasBoard"]);
        Assert.Equal(true, parameters["excludeDock"]);
        Assert.Equal("all", parameters["archived"]);
        Assert.Equal(true, parameters["includeGlobal"]);
        Assert.Equal(true, parameters["includeUnknown"]);
        Assert.Equal(true, parameters["configuredAgentsOnly"]);
        Assert.Equal(DashboardProtocol.GalleryPageSize, parameters["limit"]);
        Assert.Equal("research", parameters["agentId"]);
        Assert.Equal(200, parameters["offset"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void SessionListParams_OmitBlankAgentAndFirstOffset(string? agentId)
    {
        var parameters = DashboardProtocol.BuildSessionListParams(agentId);

        Assert.False(parameters.ContainsKey("agentId"));
        Assert.False(parameters.ContainsKey("offset"));
    }

    [Fact]
    public void SessionPage_ReadsDashboardFactsAndSkipsMalformedRows()
    {
        using var document = JsonDocument.Parse("""
            {
              "count": 3, "hasMore": true, "nextOffset": 3,
              "sessions": [
                { "key": "agent:main:dashboard:ops", "agentId": "main", "status": "running", "updatedAt": 1700,
                  "lastMessagePreview": "Deploy finished", "archived": true,
                  "createdActor": { "type": "human", "id": "ada", "label": "Ada" } },
                { "key": "agent:main:dashboard:two", "owner": { "actor": { "type": "agent", "id": "bot", "label": "Bot" } } },
                { "agentId": "main" },
                "not an object"
              ]
            }
            """);

        var page = DashboardProtocol.ParseSessionPage(document.RootElement, ParseSession);

        Assert.Equal(2, page.Rows.Length);
        var first = page.Rows[0];
        Assert.Equal("agent:main:dashboard:ops", first.Key);
        Assert.True(first.IsLive);
        Assert.True(first.Archived);
        Assert.Equal(1700, first.UpdatedAtMs);
        Assert.Equal("Deploy finished", first.LastMessagePreview);
        Assert.Equal(new DashboardActor("ada", "Ada"), first.CreatedActor);
        Assert.Null(page.Rows[1].CreatedActor);
        Assert.Equal(new DashboardActor("bot", "Bot"), page.Rows[1].OwnerActor);
        Assert.False(page.Rows[1].IsLive);
        Assert.True(page.HasMore);
        Assert.Equal(3, page.NextOffset);
    }

    [Fact]
    public void NextPageOffset_FollowsNextOffsetThenCountAndStopsWithoutProgress()
    {
        Assert.Equal(200, DashboardProtocol.NextPageOffset(new DashboardSessionPage([], true, 200, 200), 0));
        Assert.Equal(250, DashboardProtocol.NextPageOffset(new DashboardSessionPage([], true, null, 50), 200));
        Assert.Null(DashboardProtocol.NextPageOffset(new DashboardSessionPage([], false, 400, 200), 200));
        Assert.Null(DashboardProtocol.NextPageOffset(new DashboardSessionPage([], true, 100, 0), 100));
    }

    [Fact]
    public void BoardSnapshot_OrdersTabsDropsInvalidWidgetsAndClampsSizes()
    {
        using var document = JsonDocument.Parse("""
            {
              "sessionKey": "agent:main:dashboard:ops", "revision": 4,
              "tabs": [
                { "tabId": "second", "title": "Second", "position": 1, "chatDock": "left" },
                { "tabId": "first", "title": "First", "position": 0, "chatDock": "right" },
                { "tabId": "Bad Tab", "title": "Invalid", "position": 2 }
              ],
              "widgets": [
                { "name": "chart", "tabId": "first", "contentKind": "html", "sizeW": 40, "sizeH": 0, "position": 1,
                  "grantState": "granted", "revision": 2, "instanceId": "i-1", "presentation": "frameless",
                  "heightMode": "fixed", "declared": { "netOrigins": ["https://api.example.com"], "tools": ["cron.trigger:job"] },
                  "frameUrl": "/__openclaw__/board/k/chart/index.html?bt=t", "viewTicket": "t", "viewTicketTtlMs": 1200000,
                  "viewGeneration": "0123456789abcdef0123456789abcdef", "sandboxUrl": "/mcp-app-sandbox?v=1", "sandboxPort": 18790 },
                { "name": "report", "tabId": "first", "contentKind": "plugin", "pluginKind": "session:report",
                  "props": { "blocks": [] }, "sizeW": 6, "sizeH": 4, "position": 0, "grantState": "none", "revision": 1 },
                { "name": "Bad Name", "tabId": "first", "contentKind": "html", "sizeW": 1, "sizeH": 1, "position": 2, "revision": 1 },
                { "name": "registered", "tabId": "first", "contentKind": "registered", "sizeW": 1, "sizeH": 1, "position": 3, "revision": 1 },
                { "name": "no-revision", "tabId": "first", "contentKind": "html", "sizeW": 1, "sizeH": 1, "position": 4 },
                { "name": "chart", "tabId": "first", "contentKind": "html", "sizeW": 1, "sizeH": 1, "position": 5, "revision": 1 }
              ]
            }
            """);

        var board = DashboardProtocol.ParseBoardSnapshot(document.RootElement);

        Assert.Equal(["first", "second"], board.OrderedTabs.Select(tab => tab.TabId));
        Assert.Equal("first", board.DefaultTabId);
        Assert.Equal(["report", "chart"], board.WidgetsForTab("first").Select(widget => widget.Name));
        var chart = board.Widgets.Single(widget => widget.Name == "chart");
        Assert.Equal(12, chart.SizeW);
        Assert.Equal(1, chart.SizeH);
        Assert.Equal(BoardWidgetGrantState.Granted, chart.GrantState);
        Assert.Equal(BoardWidgetPresentation.Frameless, chart.Presentation);
        Assert.Equal(BoardWidgetHeightMode.Fixed, chart.HeightMode);
        Assert.Equal(["https://api.example.com"], chart.Declared.NetOrigins.ToArray());
        Assert.Equal(["cron.trigger:job"], chart.Declared.Tools.ToArray());
        Assert.Equal(18790, chart.SandboxPort);
        Assert.Equal(1_200_000, chart.ViewTicketTtlMs);
        var report = board.Widgets.Single(widget => widget.Name == "report");
        Assert.Equal("session", report.PluginId);
        Assert.Equal(JsonValueKind.Object, report.Props!.Value.ValueKind);
        Assert.Equal(BoardWidgetHeightMode.Auto, report.HeightMode);
    }

    [Fact]
    public void BoardSnapshot_RequiresSessionKey()
    {
        using var document = JsonDocument.Parse("""{ "revision": 1, "tabs": [], "widgets": [] }""");
        Assert.Throws<InvalidOperationException>(() => DashboardProtocol.ParseBoardSnapshot(document.RootElement));
    }

    [Fact]
    public void BoardEvents_ParseKnownShapesOnly()
    {
        using var changed = JsonDocument.Parse("""{ "sessionKey": "k", "revision": 9, "widget": "chart" }""");
        using var focus = JsonDocument.Parse("""{ "sessionKey": "k", "command": { "kind": "focus_tab", "tabId": "metrics" } }""");
        using var dock = JsonDocument.Parse("""{ "sessionKey": "k", "command": { "kind": "set_chat_dock", "dock": "hidden" } }""");
        using var invalid = JsonDocument.Parse("""{ "sessionKey": "k", "command": { "kind": "focus_tab", "tabId": "Bad Tab" } }""");

        Assert.Equal(new BoardChangedEvent("k", 9, "chart"), DashboardProtocol.ParseBoardChanged(changed.RootElement));
        Assert.Equal(new BoardCommandEvent("k", "focus_tab", "metrics", null), DashboardProtocol.ParseBoardCommand(focus.RootElement));
        Assert.Equal(new BoardCommandEvent("k", "set_chat_dock", null, "hidden"), DashboardProtocol.ParseBoardCommand(dock.RootElement));
        Assert.Null(DashboardProtocol.ParseBoardCommand(invalid.RootElement));
    }

    [Fact]
    public void PluginWidgetKinds_ReadFromHello()
    {
        using var hello = JsonDocument.Parse("""
            { "controlUiWidgetKinds": [ { "pluginId": "session", "kind": "session:report", "label": "Report" }, { "kind": "x:y" } ] }
            """);

        var kinds = DashboardProtocol.ParsePluginWidgetKinds(hello.RootElement);

        Assert.Equal([new PluginWidgetKind("session", "session:report", "Report")], kinds.ToArray());
    }

    private static SessionInfo? ParseSession(JsonElement item) =>
        new() { Key = item.GetProperty("key").GetString()!, AgentId = item.TryGetProperty("agentId", out var agent) ? agent.GetString() : null };
}
