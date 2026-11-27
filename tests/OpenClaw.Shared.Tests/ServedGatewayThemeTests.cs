using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using OpenClaw.Shared.Themes;
using OpenClaw.TestSupport;
using OpenClaw.TestSupport.Themes;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>Runtime resolution of built-in theme palettes from the stylesheets the connected Gateway serves.</summary>
public class ServedGatewayThemeTests
{
    private const string BuildId = "2026.11.4-release-d843d6af39a4-2026-11-15T22-45-53.485Z-b18a3630";

    [Fact]
    public void ParseIndex_FollowsTheStylesheetsTheBrowserApplies()
    {
        // Shapes seen on real Gateways: hrefs rewritten to the mount, a renamed core bundle (2026.11.5),
        // a print-media stylesheet, route preloads as built, and links that must never be fetched.
        var html = $$"""
            <html data-openclaw-control-ui-build-id="{{BuildId}}"><head>
            <style>html{background:#0e1015}</style>
            <link rel="modulepreload" crossorigin href="/assets/control-ui-core-B5VXG_DQ.js">
            <link rel="preload" as="style" crossorigin href="/assets/control-ui-connection-D5QrEAFW.css">
            <link rel="stylesheet" crossorigin href="/assets/control-ui-connection-D5QrEAFW.css" media="print">
            <link rel="preload" as="style" href="./assets/chat-transcript-styles-C3GmB-WB.css">
            <link rel="stylesheet" href="/console/assets/control-ui-boot-chat-BdQ3q80_.css">
            <link rel="stylesheet" href="https://example.invalid/assets/evil.css">
            <link rel="stylesheet" href="/assets/../secrets.css">
            <link rel="icon" href="./favicon.svg">
            </head></html>
            """;

        var index = ServedThemeStylesheetReader.ParseIndex(html);

        Assert.Equal(BuildId, index.BuildId);
        Assert.Equal(
            ["inline", "/assets/control-ui-connection-D5QrEAFW.css", "./assets/chat-transcript-styles-C3GmB-WB.css",
             "/console/assets/control-ui-boot-chat-BdQ3q80_.css"],
            index.Stylesheets.Select(sheet => sheet.Href ?? "inline"));
    }

    [Fact]
    public async Task ReadAsync_ReadsLinkedStylesheetsAndEachThemeFile_AndReportsMissingThemes()
    {
        var files = new Dictionary<string, string>
        {
            [""] = $"""<html data-openclaw-control-ui-build-id="{BuildId}"><link rel="stylesheet" href="/assets/core.css"></html>""",
            ["/assets/core.css"] = ":root{--bg:#000}",
            ["themes/tide.css"] = ":root[data-theme=tide]{--bg:#111}",
        };
        var reader = Reader(files);
        var index = await ServedThemeStylesheetReader.ReadIndexAsync(reader.Read, CancellationToken.None);

        var sheets = await ServedThemeStylesheetReader.ReadAsync(reader.Read, index!, ["claw", "tide", "aurora"], CancellationToken.None);

        Assert.NotNull(sheets);
        Assert.Equal(BuildId, sheets.Key);
        Assert.Equal(["aurora"], sheets.MissingThemeIds.AsEnumerable());
        // claw's palette is the core styles, so there is no claw.css to request.
        Assert.DoesNotContain("themes/claw.css", reader.Requests);
        Assert.Equal([false, true], sheets.Stylesheets.Select(sheet => sheet.IsThemeSheet));
        var document = ServedThemeStylesheetReader.BuildProbeDocument(sheets);
        Assert.Contains("<style>:root{--bg:#000}</style><style data-openclaw-theme-sheet>:root[data-theme=tide]", document);
    }

    [Fact]
    public async Task ReadAsync_WithoutABuildId_KeysByContentSoARetuneIsANewBuild()
    {
        var files = new Dictionary<string, string>
        {
            [""] = """<html><link rel="stylesheet" href="./assets/core.css"></html>""",
            ["./assets/core.css"] = ":root{--bg:#000}",
            ["themes/tide.css"] = ":root[data-theme=tide]{--bg:#111}",
        };
        async Task<string> KeyAsync()
        {
            var reader = Reader(files);
            var index = await ServedThemeStylesheetReader.ReadIndexAsync(reader.Read, CancellationToken.None);
            return (await ServedThemeStylesheetReader.ReadAsync(reader.Read, index!, ["tide"], CancellationToken.None))!.Key;
        }

        var first = await KeyAsync();
        Assert.Equal(first, await KeyAsync());
        files["themes/tide.css"] = ":root[data-theme=tide]{--bg:#121212}";
        Assert.NotEqual(first, await KeyAsync());
        Assert.StartsWith("sha256:", first);
    }

    [Fact]
    public async Task Reader_FailsClosedWhenTheControlUiIsNotServedOrIncomplete()
    {
        // Disabled Control UI or another mount: no page at all.
        Assert.Null(await ServedThemeStylesheetReader.ReadIndexAsync(Reader([]).Read, CancellationToken.None));
        // A proxy's sign-in page is HTML, but not the Control UI.
        var signIn = Reader(new() { [""] = "<html><style>body{}</style><form></form></html>" });
        Assert.Null(await ServedThemeStylesheetReader.ReadIndexAsync(signIn.Read, CancellationToken.None));
        // A linked core stylesheet that cannot be read leaves nothing to resolve.
        var broken = Reader(new() { [""] = """<link rel="stylesheet" href="/assets/core.css">""" });
        var index = await ServedThemeStylesheetReader.ReadIndexAsync(broken.Read, CancellationToken.None);
        Assert.Null(await ServedThemeStylesheetReader.ReadAsync(broken.Read, index!, ["tide"], CancellationToken.None));
    }

    [Theory]
    [InlineData(ControlUiThemeRecording.Stable)]
    [InlineData(ControlUiThemeRecording.Older)]
    [InlineData(ControlUiThemeRecording.Newer)]
    public async Task Reader_ReadsWhatARealGatewayServed(string name)
    {
        var recording = ControlUiThemeRecording.Load(name);
        var index = await ServedThemeStylesheetReader.ReadIndexAsync(recording.Read, CancellationToken.None);
        var sheets = await ServedThemeStylesheetReader.ReadAsync(recording.Read, index!, recording.ThemeIds, CancellationToken.None);

        Assert.NotNull(sheets);
        Assert.Equal(recording.BuildId, sheets.Key);
        Assert.Empty(sheets.MissingThemeIds);
        Assert.Equal(recording.ThemeIds.Count - 1, sheets.Stylesheets.Count(sheet => sheet.IsThemeSheet));
        Assert.Contains(sheets.Stylesheets, sheet => !sheet.IsThemeSheet && sheet.Css.Contains("--bg-hover", StringComparison.Ordinal));
    }

    [Fact]
    public void ProbeParse_KeepsOnlyCompleteValidThemes()
    {
        var valid = Palette();
        var json = Probe(new()
        {
            ["tide"] = (valid, valid),
            // A Gateway stylesheet can put anything in a font variable; a path would be loaded as a font file.
            ["paths"] = (Palette(body: "\"\\\\\\\\attacker\\\\share\\\\font.ttf#Evil\", sans-serif"), valid),
            ["urls"] = (Palette(body: "\"ms-appx:///Assets/x.ttf#X\""), valid),
            ["oversized"] = (Palette(radius: 1000), valid),
            ["unresolved"] = (Palette(canvas: null), valid),
            // A translucent window surface would let the system backdrop show through a solid theme.
            ["translucent"] = (Palette(canvas: "#10151b80"), valid),
            ["unstyled"] = (valid, valid),
        }, failures: ["unstyled/light/unstyled"]);

        var result = ThemeProbe.Parse(json);

        Assert.Equal(["tide"], result.Palettes.Keys);
        Assert.Equal(ThemeColor.FromHex("#10151b"), result.Palettes["tide"].Dark.Canvas);
        Assert.Equal(ThemeColorMode.Dark, result.Palettes["tide"].Dark.Mode);
        Assert.Equal(new ThemeRadii(6, 10, 1), result.Palettes["tide"].Light.Radii);
        Assert.Equal("IBM Plex Sans", result.Palettes["tide"].Light.BodyFont.Primary);
        Assert.Contains("unstyled/light/unstyled", result.Failures);
        Assert.Contains("paths/invalid", result.Failures);
        Assert.Contains("translucent/invalid", result.Failures);
    }

    [Fact]
    public void ServedSet_TakesNamesAndBrandingFromTheGatewayCatalog()
    {
        var probe = ThemeProbe.Parse(Probe(new()
        {
            ["claw"] = (Palette(), Palette()),
            ["phosphor"] = (Palette(), Palette()),
            ["aurora"] = (Palette(), Palette()),
            ["unreadable"] = (Palette(text: "#10151b"), Palette()),
        }));
        // 2026.9.7 sent no branding fields: the dashboard then shows upstream defaults (the claw) even for Phosphor.
        GatewayThemeDescriptor[] catalog =
        [
            Descriptor("aurora", "Aurora"),
            Descriptor("phosphor", "Phosphor"),
            Descriptor("claw", "Claw"),
            Descriptor("unreadable", "Unreadable"),
            Descriptor("unprobed", "Unprobed"),
        ];

        var set = ServedBuiltinThemeSet.Build("build-1", probe, catalog);

        Assert.True(set.HasGatewayDescriptors);
        Assert.Equal(["aurora", "phosphor", "claw"], set.Themes.Select(theme => theme.Id));
        Assert.Equal(ThemeBranding.Default, set.Themes[1].Branding);
        Assert.True(set.TryGet("aurora", out var aurora));
        Assert.Equal("Aurora", aurora.Name);

        // Without a catalog, names and branding come from the table and unknown ids are not offered.
        var fallback = ServedBuiltinThemeSet.Build("build-1", probe, descriptors: null);
        Assert.Equal(["claw", "phosphor"], fallback.Themes.Select(theme => theme.Id));
        Assert.True(BuiltinGatewayThemes.TryGet("phosphor", out var table));
        Assert.Equal(table.Branding, fallback.Themes[1].Branding);
        Assert.Equal(ThemeColor.FromHex("#10151b"), fallback.Themes[1].Dark!.Canvas);
    }

    [Fact]
    public void AppearanceResolver_PrefersTheGatewayPalettes_FallsBackToTheTable_AndRendersUnknownIdsAsWindowsDefault()
    {
        var probe = ThemeProbe.Parse(Probe(new() { ["tide"] = (Palette(), Palette()), ["aurora"] = (Palette(), Palette()) }));
        var source = ServedBuiltinThemeSet.Build("build-1", probe, [Descriptor("tide", "Tide"), Descriptor("aurora", "Aurora")])
            .Over(BuiltinGatewayThemes.Table);
        var following = new AppearanceSettings { FollowGatewayTheme = true };

        Assert.Equal(ThemeColor.FromHex("#10151b"), AppearanceResolver.Resolve(Builtin("tide"), source)!.Dark!.Canvas);
        Assert.True(BuiltinGatewayThemes.TryGet("dash", out var dash));
        Assert.Equal(dash, AppearanceResolver.Resolve(Builtin("dash"), source));

        // A built-in only the Gateway's stylesheets style can be followed; with the table alone it cannot.
        var followed = AppearanceResolver.Follow(following, Descriptor("aurora", "Aurora"), null, "gw", source);
        Assert.Equal("aurora", AppearanceResolver.ResolveTheme(followed, source)?.Id);
        Assert.Same(following, AppearanceResolver.Follow(following, Descriptor("aurora", "Aurora"), null, "gw"));

        // An id nothing styles renders as the Windows default rather than as another theme's palette.
        Assert.Same(following, AppearanceResolver.Follow(following, Descriptor("nosuch", "Nope"), null, "gw", source));
        Assert.Null(AppearanceResolver.ResolveTheme(new AppearanceSettings { Theme = Builtin("nosuch") }, source));
    }

    [Fact]
    public void Cache_ReusesAProbePerBuild_BoundsEntries_AndIgnoresAnythingItCannotTrust()
    {
        using var directory = new TempDirectory("served-theme-cache-");
        var path = Path.Combine(directory.Path, "Themes", "cache.json");
        var cache = new ServedBuiltinThemeCache(path);
        var probe = ThemeProbe.Parse(Probe(new() { ["tide"] = (Palette(), Palette()), ["phosphor"] = (Palette(), Palette()) }));
        var phosphor = Descriptor("phosphor", "Phosphor") with { Branding = ThemeBranding.Resolve("none", null, null) };

        cache.Save(new ServedThemeCacheEntry("build-1", probe, [Descriptor("tide", "Tide"), phosphor]));
        cache.Save(new ServedThemeCacheEntry("build-2", probe, null));

        var hit = cache.TryLoad("build-1");
        Assert.NotNull(hit);
        Assert.Equal(["tide", "phosphor"], hit.AttemptedIds);
        Assert.True(hit.ToSet().TryGet("phosphor", out var cached));
        Assert.False(cached.Branding.Mascot);
        Assert.Null(cache.TryLoad("build-2")!.Descriptors);
        Assert.Equal("build-2", cache.TryLoadLast()!.Key);

        for (var build = 3; build < 3 + ServedBuiltinThemeCache.MaxEntries; build++)
            cache.Save(new ServedThemeCacheEntry($"build-{build}", probe, null));
        Assert.Null(cache.TryLoad("build-1"));
        Assert.NotNull(cache.TryLoad($"build-{2 + ServedBuiltinThemeCache.MaxEntries}"));

        // A cache written by a different probe, or one that is damaged, only costs a fresh probe.
        var file = File.ReadAllText(path);
        File.WriteAllText(path, file.Replace(ThemeProbe.ScriptHash, "an-older-probe", StringComparison.Ordinal));
        Assert.Null(cache.TryLoadLast());
        File.WriteAllText(path, "{ not json");
        Assert.Null(cache.TryLoadLast());
        cache.Save(new ServedThemeCacheEntry("build-9", probe, null));
        Assert.NotNull(cache.TryLoad("build-9"));

        // Valid JSON with malformed entries: they are skipped, and saving still works.
        File.WriteAllText(path, "{\"schema\":2,\"probe\":\"" + ThemeProbe.ScriptHash + "\",\"last\":\"build-9\"," +
            "\"entries\":[0,\"x\",{\"key\":5},{\"key\":\"build-9\",\"seq\":\"one\",\"result\":[],\"failures\":{}}]}");
        Assert.Null(cache.TryLoadLast());
        cache.Save(new ServedThemeCacheEntry("build-10", probe, null));
        Assert.NotNull(cache.TryLoad("build-10"));

        // A repeated built-in id restores as one theme instead of failing the startup theme.
        cache.Save(new ServedThemeCacheEntry("build-11", probe, [Descriptor("tide", "Tide"), Descriptor("tide", "Tide again")]));
        var restored = cache.TryLoadLast()!.ToSet();
        Assert.Equal(["tide"], restored.Themes.Select(theme => theme.Id));
        Assert.Equal("Tide", restored.Themes[0].Name);
    }

    [Fact]
    public async Task Client_ReadsControlUiFilesWithoutCredentials_OnItsOriginAndMountOnly()
    {
        using var server = new LoopbackWebSocketServer();
        using var identity = new TempDirectory("control-ui-read-");
        await server.StartAsync();
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/themes/tide.css" => Text(HttpStatusCode.OK, ":root{}", "text/css"),
            "/themes/html.css" => Text(HttpStatusCode.OK, "<html>", "text/html"),
            "/themes/big.css" => Text(HttpStatusCode.OK, new string('a', 64), "text/css"),
            "/themes/moved.css" => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/login", UriKind.Relative) } },
            _ => Text(HttpStatusCode.NotFound, "", "text/plain"),
        });
        using var client = new OpenClawGatewayClient(server.WebSocketUrl, "gateway-token",
            identityPath: identity.Path, assistantMediaHandler: handler, assistantMediaAuthToken: "gateway-token");
        await client.ConnectAsync();
        MarkHandshakeReady(client);

        Assert.Equal(":root{}", await client.ReadControlUiTextAsync("themes/tide.css", "text/css", 1024));
        Assert.Null(await client.ReadControlUiTextAsync("themes/html.css", "text/css", 1024));
        Assert.Null(await client.ReadControlUiTextAsync("themes/big.css", "text/css", 16));
        Assert.Null(await client.ReadControlUiTextAsync("themes/moved.css", "text/css", 1024));
        Assert.Null(await client.ReadControlUiTextAsync("themes/missing.css", "text/css", 1024));
        Assert.Null(await client.ReadControlUiTextAsync("//example.invalid/x.css", "text/css", 1024));
        Assert.Null(await client.ReadControlUiTextAsync("../x.css", "text/css", 1024));

        Assert.Equal(5, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Equal(new Uri(server.WebSocketUrl).Port, request.RequestUri!.Port);
        });
    }

    [Theory]
    [InlineData("http://gw:18789/", "", "http://gw:18789/")]
    [InlineData("http://gw:18789/", "themes/tide.css", "http://gw:18789/themes/tide.css")]
    [InlineData("http://gw:18789/", "/assets/core.css", "http://gw:18789/assets/core.css")]
    [InlineData("https://gw/console", "themes/tide.css", "https://gw/console/themes/tide.css")]
    [InlineData("https://gw/console", "/console/assets/core.css", "https://gw/console/assets/core.css")]
    [InlineData("https://gw/console", "/assets/core.css", null)]
    [InlineData("http://gw:18789/", "http://other/themes/tide.css", null)]
    [InlineData("http://gw:18789/", "themes/tide.css?x=1", null)]
    public void ControlUiPaths_StayOnTheMount(string baseUri, string path, string? expected)
    {
        var resolved = OpenClawGatewayClient.TryResolveControlUiUri(new Uri(baseUri), path, out var uri);
        Assert.Equal(expected is not null, resolved);
        if (expected is not null)
            Assert.Equal(expected, uri.AbsoluteUri);
    }

    private static ThemeSelection Builtin(string id) => new() { Id = id, Name = id };

    private static GatewayThemeDescriptor Descriptor(string id, string name) =>
        new(id, name, $"{name} theme", GatewayThemeSource.Builtin, [ThemeColorMode.Light, ThemeColorMode.Dark], null);

    private static Dictionary<string, object?> Palette(
        string? canvas = "#10151b", string text = "#f2f6fa", string body = "\"IBM Plex Sans\", -apple-system, sans-serif",
        double radius = 6)
    {
        var palette = new Dictionary<string, object?>();
        foreach (var field in new[]
        {
            "Content", "Surface", "Card", "Popover", "Hover", "TextStrong", "Muted", "Border", "BorderStrong", "Accent",
            "AccentHover", "AccentForeground", "Primary", "PrimaryForeground", "Ring", "Destructive", "Danger", "Ok", "Warn",
            "Info", "ComposerSurface", "InlineCode", "Elevated", "Panel", "AccentSubtle",
        })
            palette[field] = field is "Muted" ? "#9dabb9" : field is "PrimaryForeground" ? "#0b1116" : field is "Primary" ? "#5ab6d8" : "#10151b";
        palette["Canvas"] = canvas;
        palette["Text"] = text;
        palette["RadiusSm"] = radius;
        palette["RadiusMd"] = 10;
        palette["CornerScale"] = 1;
        palette["Body"] = body;
        palette["Chat"] = body;
        palette["Mono"] = "\"JetBrains Mono\", monospace";
        return palette;
    }

    private static string Probe(Dictionary<string, (Dictionary<string, object?> Light, Dictionary<string, object?> Dark)> themes,
        string[]? failures = null) =>
        JsonSerializer.Serialize(new
        {
            result = themes.ToDictionary(theme => theme.Key, theme => new { light = theme.Value.Light, dark = theme.Value.Dark }),
            failures = failures ?? [],
        });

    private static FakeControlUi Reader(Dictionary<string, string> files) => new(files);

    private sealed class FakeControlUi(Dictionary<string, string> files)
    {
        public List<string> Requests { get; } = [];

        public Task<string?> Read(string path, string mediaType, int maxBytes, CancellationToken cancellationToken)
        {
            Requests.Add(path);
            return Task.FromResult(files.TryGetValue(path, out var text) ? text : null);
        }
    }

    private static HttpResponseMessage Text(HttpStatusCode status, string body, string mediaType)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };
        return response;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    // As OpenClawGatewayClientAssistantMediaTests does: the loopback server accepts the socket but never sends hello.
    private static void MarkHandshakeReady(OpenClawGatewayClient client)
    {
        var generation = (long)typeof(WebSocketClientBase)
            .GetProperty("CurrentConnectionGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
        foreach (var (name, value) in new (string, object)[]
        {
            ("_mainSessionKeyIsCanonical", true), ("_mainSessionKey", "agent:main:main"),
            ("_handshakeConnectionGeneration", generation), ("_hasHandshakeSnapshot", true),
        })
            typeof(OpenClawGatewayClient).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(client, value);
    }
}
