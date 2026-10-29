using System.Net;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared.Dashboards;
using Xunit;

namespace OpenClaw.Shared.Tests.Dashboards;

public class DashboardGalleryPolicyTests
{
    private static readonly DashboardSessionRow Ops = Row("agent:main:dashboard:ops", "main", 300, new("ada", "Ada"), null, "Deploy finished");
    private static readonly DashboardSessionRow Release = Row("agent:main:dashboard:release", "main", 100, null, new("bot", "Release Bot"), "Store review");
    private static readonly DashboardSessionRow Study = Row("agent:research:dashboard:study", "research", 200, null, null, null);

    [Fact]
    public void Author_PrefersCreatorThenOwnerThenAgent()
    {
        Assert.Equal(new DashboardAuthor("ada", "Ada"), DashboardGalleryPolicy.ResolveAuthor(Ops, "main"));
        Assert.Equal(new DashboardAuthor("bot", "Release Bot"), DashboardGalleryPolicy.ResolveAuthor(Release, "main"));
        Assert.Equal(new DashboardAuthor("research", "research"), DashboardGalleryPolicy.ResolveAuthor(Study, "main"));
        Assert.Equal(new DashboardAuthor("main", "main"),
            DashboardGalleryPolicy.ResolveAuthor(Study with { Session = new SessionInfo { Key = "global" } }, "main"));
    }

    [Fact]
    public void Authors_AreDistinctAndOrderedByLabel()
    {
        var authors = DashboardGalleryPolicy.Authors([Release, Ops, Study, Ops], "main");
        Assert.Equal(["Ada", "Release Bot", "research"], authors.Select(author => author.Label));
    }

    [Fact]
    public void DefaultSort_IsMostRecentlyUpdated()
    {
        var rows = DashboardGalleryPolicy.Apply([Release, Study, Ops], new(), "main", Title);
        Assert.Equal([Ops, Study, Release], rows.ToArray());
    }

    [Fact]
    public void TitleSort_UsesDisplayTitles()
    {
        var rows = DashboardGalleryPolicy.Apply([Ops, Study, Release], new(Sort: DashboardGallerySort.Title), "main", Title);
        Assert.Equal(["ops", "release", "study"], rows.Select(Title));
    }

    [Theory]
    [InlineData("OPS", "agent:main:dashboard:ops")]
    [InlineData("release bot", "agent:main:dashboard:release")]
    [InlineData("deploy", "agent:main:dashboard:ops")]
    [InlineData("research:dashboard", "agent:research:dashboard:study")]
    public void Search_MatchesTitleAuthorPreviewAndKey(string query, string expectedKey)
    {
        var rows = DashboardGalleryPolicy.Apply([Ops, Release, Study], new(Query: query), "main", Title);
        Assert.Equal([expectedKey], rows.Select(row => row.Key));
    }

    [Fact]
    public void AuthorFilter_KeepsOnlyThatAuthor()
    {
        var rows = DashboardGalleryPolicy.Apply([Ops, Release, Study], new(AuthorId: "bot"), "main", Title);
        Assert.Equal([Release], rows.ToArray());
    }

    [Fact]
    public void AuthorInitial_IsUpperCaseFirstLetterOrQuestionMark()
    {
        Assert.Equal("A", new DashboardAuthor("a", "ada").Initial);
        Assert.Equal("?", new DashboardAuthor("a", " ").Initial);
    }

    private static string Title(DashboardSessionRow row) => row.Key.Split(':')[^1];

    private static DashboardSessionRow Row(string key, string agent, long updated, DashboardActor? created, DashboardActor? owner, string? preview) =>
        new()
        {
            Session = new SessionInfo { Key = key, AgentId = agent },
            UpdatedAtMs = updated,
            CreatedActor = created,
            OwnerActor = owner,
            LastMessagePreview = preview,
        };
}

public class BoardGridLayoutTests
{
    [Fact]
    public void Layout_PlacesItemsFirstFitInCanonicalOrder()
    {
        var rects = BoardGridLayout.Layout(
        [
            new BoardGridItem("b", 6, 2, 1),
            new BoardGridItem("a", 8, 4, 0),
            new BoardGridItem("c", 4, 2, 2),
        ]);

        Assert.Equal(
            [new BoardGridRect("a", 0, 0, 8, 4), new BoardGridRect("b", 0, 4, 6, 2), new BoardGridRect("c", 8, 0, 4, 2)],
            rects.ToArray());
        Assert.Equal(6, BoardGridLayout.RowCount(rects));
    }

    [Fact]
    public void Layout_StacksFullWidthItemsAndClampsSpans()
    {
        var rects = BoardGridLayout.Layout([new BoardGridItem("a", 40, 3, 0), new BoardGridItem("b", 12, 0, 1)]);
        Assert.Equal([new BoardGridRect("a", 0, 0, 12, 3), new BoardGridRect("b", 0, 3, 12, 1)], rects.ToArray());
    }

    [Fact]
    public void EffectiveRows_FitAutoHeightHtmlContentOnly()
    {
        var html = TestWidgets.Html("chart");
        Assert.Equal(4, BoardGridLayout.EffectiveRows(html, null));
        // 300px content + 40px title row + 2px border + 24px card inset = 366px => ceil(378 / 68) = 6 rows.
        Assert.Equal(6, BoardGridLayout.EffectiveRows(html, 300));
        Assert.Equal(BoardGridLayout.AutoMinRows, BoardGridLayout.EffectiveRows(html, 10));
        Assert.Equal(20, BoardGridLayout.EffectiveRows(html, 100_000));
        Assert.Equal(4, BoardGridLayout.EffectiveRows(html with { HeightMode = BoardWidgetHeightMode.Fixed }, 300));
        Assert.Equal(4, BoardGridLayout.EffectiveRows(TestWidgets.Plugin("r", "session:report"), 300));
    }
}

public class BoardReportParserTests
{
    [Fact]
    public void Parse_ReadsEveryBlockType()
    {
        var report = BoardReportParser.Parse(Json("""
            { "blocks": [
              { "type": "text", "title": "Summary", "text": "All good." },
              { "type": "metrics", "items": [ { "label": "Closed", "value": "12", "detail": "+3" } ] },
              { "type": "table", "columns": ["A", "B"], "rows": [["1", "2"]] },
              { "type": "chart", "style": "line", "points": [ { "label": "Mon", "value": 3.5 } ] },
              { "type": "links", "items": [ { "label": "Runbook", "url": "https://example.com/r" } ] }
            ] }
            """));

        Assert.Collection(report.Blocks,
            block => Assert.Equal("All good.", Assert.IsType<BoardReportTextBlock>(block).Text),
            block => Assert.Equal("12", Assert.IsType<BoardReportMetricsBlock>(block).Items[0].Value),
            block => Assert.Equal("2", Assert.IsType<BoardReportTableBlock>(block).Rows[0][1]),
            block => Assert.True(Assert.IsType<BoardReportChartBlock>(block).Line),
            block => Assert.Equal("https://example.com/r", Assert.IsType<BoardReportLinksBlock>(block).Items[0].Url.AbsoluteUri));
    }

    [Theory]
    [InlineData("""{ "blocks": [] }""")]
    [InlineData("""{ "blocks": [ { "type": "html", "html": "<script>" } ] }""")]
    [InlineData("""{ "blocks": [ { "type": "text", "text": "x", "onclick": "y" } ] }""")]
    [InlineData("""{ "blocks": [ { "type": "table", "columns": ["A", "B"], "rows": [["only one"]] } ] }""")]
    [InlineData("""{ "blocks": [ { "type": "links", "items": [ { "label": "x", "url": "javascript:alert(1)" } ] } ] }""")]
    [InlineData("""{ "blocks": [ { "type": "chart", "points": [ { "label": "x", "value": "1" } ] } ] }""")]
    [InlineData("""{ "blocks": [ { "type": "text", "text": "x" } ], "script": "y" }""")]
    public void Parse_RejectsExecutableOrMalformedContent(string json) =>
        Assert.Throws<FormatException>(() => BoardReportParser.Parse(Json(json)));

    [Fact]
    public void Parse_EnforcesJsonBudget()
    {
        var text = new string('a', 3000);
        var json = $$"""{ "blocks": [ { "type": "text", "text": "{{text}}" }, { "type": "text", "text": "{{text}}" }, { "type": "text", "text": "{{text}}" } ] }""";
        Assert.Throws<FormatException>(() => BoardReportParser.Parse(Json(json)));
    }

    [Fact]
    public void Parse_RejectsMissingProps() => Assert.Throws<FormatException>(() => BoardReportParser.Parse(null));

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

public class BoardWidgetBodyPolicyTests
{
    private static readonly PluginWidgetKind[] Kinds =
    [
        new("session", "session:report", "Report"),
        new("session", "session:website", "Website"),
        new("session", "session:progress", "Session progress"),
    ];

    [Theory]
    [InlineData(BoardWidgetGrantState.Pending, BoardWidgetBodyKind.NeedsApproval)]
    [InlineData(BoardWidgetGrantState.Rejected, BoardWidgetBodyKind.Rejected)]
    [InlineData(BoardWidgetGrantState.None, BoardWidgetBodyKind.Frame)]
    [InlineData(BoardWidgetGrantState.Granted, BoardWidgetBodyKind.Frame)]
    public void Html_RequiresApprovalBeforeContent(BoardWidgetGrantState grant, BoardWidgetBodyKind expected) =>
        Assert.Equal(expected, BoardWidgetBodyPolicy.Resolve(TestWidgets.Html("w", grant), Kinds));

    [Fact]
    public void Html_WithoutTicketIsUnavailable() =>
        Assert.Equal(BoardWidgetBodyKind.Unavailable, BoardWidgetBodyPolicy.Resolve(TestWidgets.Html("w", ticket: false), Kinds));

    [Theory]
    [InlineData("session:report", BoardWidgetBodyKind.Report)]
    [InlineData("session:website", BoardWidgetBodyKind.Website)]
    [InlineData("session:progress", BoardWidgetBodyKind.UnsupportedCorePlugin)]
    [InlineData("browser:dashboard", BoardWidgetBodyKind.DisabledPlugin)]
    [InlineData("acme:timeline", BoardWidgetBodyKind.DisabledPlugin)]
    public void Plugins_RenderOnlyAdvertisedTrustedKinds(string kind, BoardWidgetBodyKind expected) =>
        Assert.Equal(expected, BoardWidgetBodyPolicy.Resolve(TestWidgets.Plugin("p", kind), Kinds));

    [Fact]
    public void Plugins_AreDisabledWhenGatewayDoesNotAdvertiseThem() =>
        Assert.Equal(BoardWidgetBodyKind.DisabledPlugin, BoardWidgetBodyPolicy.Resolve(TestWidgets.Plugin("p", "session:report"), []));

    [Fact]
    public void Previews_AdmitOnlyHtmlAndDataOnlyReports()
    {
        Assert.True(BoardWidgetBodyPolicy.IsPreviewSafe(TestWidgets.Html("w"), Kinds));
        Assert.True(BoardWidgetBodyPolicy.IsPreviewSafe(TestWidgets.Plugin("r", "session:report"), Kinds));
        Assert.False(BoardWidgetBodyPolicy.IsPreviewSafe(TestWidgets.Plugin("s", "session:website"), Kinds));
        Assert.False(BoardWidgetBodyPolicy.IsPreviewSafe(TestWidgets.Plugin("r", "session:report"), []));
        Assert.False(BoardWidgetBodyPolicy.IsPreviewSafe(TestWidgets.Html("m") with { ContentKind = BoardWidgetContentKind.McpApp }, Kinds));
    }
}

public class BoardWidgetHostPageTests
{
    [Fact]
    public void Parse_AcceptsTheRelayProtocol()
    {
        Assert.IsType<BoardWidgetHostMessage.PageReady>(BoardWidgetHostPage.Parse("""{"channel":"ready","data":null}"""));
        Assert.Equal("http://127.0.0.1:1/mcp-app-sandbox#theme=dark",
            Assert.IsType<BoardWidgetHostMessage.ProxyReady>(BoardWidgetHostPage.Parse(
                """{"channel":"sandbox","data":{"method":"ui/notifications/sandbox-proxy-ready","params":{"sandboxUrl":"http://127.0.0.1:1/mcp-app-sandbox#theme=dark"}}}""")).SandboxUrl);
        Assert.Equal("r1", Assert.IsType<BoardWidgetHostMessage.ResourceLoaded>(BoardWidgetHostPage.Parse(
            """{"channel":"sandbox","data":{"method":"ui/notifications/sandbox-resource-loaded","params":{"renderId":"r1"}}}""")).RenderId);
        Assert.Equal(240, Assert.IsType<BoardWidgetHostMessage.ContentSize>(BoardWidgetHostPage.Parse(
            """{"channel":"sandbox","data":{"type":"openclaw:widget-size","height":240}}""")).Height);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"channel":"init","data":{}}""")]
    [InlineData("""{"channel":"sandbox","data":{"method":"tools/call","params":{}}}""")]
    [InlineData("""{"channel":"sandbox","data":{"type":"openclaw:widget-size","height":"500"}}""")]
    [InlineData("""{"channel":"sandbox","data":{"type":"openclaw:widget-size","height":-1}}""")]
    [InlineData("""{"channel":"sandbox","data":{"method":"ui/notifications/sandbox-proxy-ready","params":{}}}""")]
    [InlineData("""{"channel":"bridge","data":{"type":"x"}}""")]
    [InlineData("""["ready"]""")]
    public void Parse_RejectsUnknownOrMalformedMessages(string json) => Assert.Null(BoardWidgetHostPage.Parse(json));

    [Fact]
    public void Parse_RejectsOversizedMessagesAndClampsHeights()
    {
        Assert.Null(BoardWidgetHostPage.Parse("""{"channel":"sandbox","data":{"x":""" + "\"" + new string('a', BoardWidgetHostPage.MaxMessageChars) + "\"}}"));
        Assert.Equal(BoardWidgetHostPage.MaxContentHeight, Assert.IsType<BoardWidgetHostMessage.ContentSize>(
            BoardWidgetHostPage.Parse("""{"channel":"sandbox","data":{"type":"openclaw:widget-size","height":1e9}}""")).Height);
    }

    [Theory]
    [InlineData("http://openclaw-board-host.invalid/widget-host.html?r=3", true)]
    [InlineData("http://openclaw-board-host.invalid/widget-host.html", true)]
    [InlineData("http://openclaw-board-host.invalid/other.html", false)]
    [InlineData("https://openclaw-board-host.invalid/widget-host.html", false)]
    [InlineData("http://127.0.0.1:18790/mcp-app-sandbox", false)]
    [InlineData(null, false)]
    public void PageNavigation_IsOnlyTheRelayPage(string? uri, bool expected) =>
        Assert.Equal(expected, BoardWidgetHostPage.IsPageNavigation(uri));

    [Fact]
    public void ContentSecurityPolicy_AllowsOnlyNonceScriptAndSandboxFrame()
    {
        var csp = BoardWidgetHostPage.ContentSecurityPolicy("n1", "http://127.0.0.1:18790");
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("script-src 'nonce-n1'", csp);
        Assert.Contains("frame-src http://127.0.0.1:18790;", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.Contains("nonce=\"n1\"", BoardWidgetHostPage.BuildHtml("n1"));
    }

    [Fact]
    public void InitMessage_CarriesSandboxOriginAndScale()
    {
        using var init = JsonDocument.Parse(BoardWidgetHostPage.InitMessage(
            new Uri("http://127.0.0.1:18790/mcp-app-sandbox?v=1#theme=dark"), "Chart", scale: 0.25));
        Assert.Equal("init", init.RootElement.GetProperty("channel").GetString());
        Assert.Equal("http://127.0.0.1:18790", init.RootElement.GetProperty("sandboxOrigin").GetString());
        Assert.Equal(0.25, init.RootElement.GetProperty("scale").GetDouble());

        using var rescale = JsonDocument.Parse(BoardWidgetHostPage.ScaleMessage(0.4));
        Assert.Equal("scale", rescale.RootElement.GetProperty("channel").GetString());
        Assert.Equal(0.4, rescale.RootElement.GetProperty("scale").GetDouble());
        Assert.Contains("message.channel === \"scale\"", BoardWidgetHostPage.BuildHtml("n"));
    }
}

public class BoardAccessPolicyTests
{
    [Fact]
    public void Read_RequiresReadScopeAndAdvertisedBoardGet()
    {
        Assert.True(BoardAccessPolicy.CanRead(new FakeAccess(["operator.read"], ["board.get"])));
        Assert.True(BoardAccessPolicy.CanRead(new FakeAccess(["operator.write"], ["board.get"])));
        Assert.True(BoardAccessPolicy.CanRead(new FakeAccess([], [])));
        Assert.False(BoardAccessPolicy.CanRead(new FakeAccess(["operator.pairing"], ["board.get"])));
        Assert.False(BoardAccessPolicy.CanRead(new FakeAccess(["operator.read"], ["sessions.list"])));
    }

    private sealed class FakeAccess(string[] scopes, string[] methods) : IBoardGatewayClient
    {
        public bool IsConnectedToGateway => true;
        public IReadOnlyList<string> GrantedOperatorScopes => scopes;
        public System.Collections.Immutable.ImmutableArray<PluginWidgetKind> PluginWidgetKinds => [];
        public Uri? GatewayHttpOrigin => null;
        public IReadOnlyList<string> AdvertisedServerMethods => methods;
        public event EventHandler<BoardChangedEvent>? BoardChanged { add { } remove { } }
        public event EventHandler<BoardCommandEvent>? BoardCommandReceived { add { } remove { } }
        public Task<DashboardSessionPage?> ListDashboardSessionsAsync(string? agentId, int offset = 0, int timeoutMs = 15000) => throw new NotSupportedException();
        public Task<BoardSnapshot> GetBoardAsync(BoardSessionTarget target, int timeoutMs = 15000) => throw new NotSupportedException();
    }
}

public class BoardWidgetDocumentLoaderTests
{
    private static readonly Uri Url = new("http://127.0.0.1:18789/__openclaw__/board/k/w/index.html?bt=t");

    [Fact]
    public async Task Load_ReturnsDocumentBody()
    {
        using var loader = new BoardWidgetDocumentLoader(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<p>ok</p>") }));
        Assert.Equal("<p>ok</p>", await loader.LoadAsync(Url, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, BoardWidgetDocumentFailure.Unauthorized)]
    [InlineData(HttpStatusCode.RequestTimeout, BoardWidgetDocumentFailure.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, BoardWidgetDocumentFailure.Unavailable)]
    [InlineData(HttpStatusCode.NotFound, BoardWidgetDocumentFailure.Rejected)]
    [InlineData(HttpStatusCode.Redirect, BoardWidgetDocumentFailure.Rejected)]
    public async Task Load_MapsHttpFailures(HttpStatusCode status, BoardWidgetDocumentFailure expected)
    {
        using var loader = new BoardWidgetDocumentLoader(new StubHandler(_ => new HttpResponseMessage(status)));
        var error = await Assert.ThrowsAsync<BoardWidgetDocumentException>(() => loader.LoadAsync(Url, CancellationToken.None));
        Assert.Equal(expected, error.Failure);
    }

    [Fact]
    public async Task Load_RejectsDocumentsOverTheSizeLimit()
    {
        var body = new string('a', BoardWidgetDocumentLoader.MaxDocumentBytes + 1);
        using var loader = new BoardWidgetDocumentLoader(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body))) }));
        var error = await Assert.ThrowsAsync<BoardWidgetDocumentException>(() => loader.LoadAsync(Url, CancellationToken.None));
        Assert.Equal(BoardWidgetDocumentFailure.Rejected, error.Failure);
    }

    [Fact]
    public async Task Load_MapsDroppedConnectionDuringBodyReadToUnavailable()
    {
        using var loader = new BoardWidgetDocumentLoader(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream()) }));
        var error = await Assert.ThrowsAsync<BoardWidgetDocumentException>(() => loader.LoadAsync(Url, CancellationToken.None));
        Assert.Equal(BoardWidgetDocumentFailure.Unavailable, error.Failure);
    }

    private sealed class FailingStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("connection reset"));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException<int>(new IOException("connection reset"));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
