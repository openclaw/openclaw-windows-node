using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Shared.Capabilities;
using OpenClaw.Shared.Mcp;
using OpenClaw.Shared.Widgets;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class WidgetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openclaw-widget-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Observation = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static WidgetSnapshot Mercury => new("mercury", "Mercury retrograde", "Yes",
        "According to the selected source.", "https://ismercuryinretrograde.com/", Observation);
    private static readonly WidgetCopy Copy = new("Tracker", "Create a tracker.", "Tracker", "Show", "Stale", "Source");
    private const string PixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a0l8AAAAASUVORK5CYII=";

    [Fact]
    public void Store_RecoversIndependentSnapshotsAndRejectsLateOrConflictingWrites()
    {
        var store = new WidgetSnapshotStore(_directory);
        store.Upsert(Mercury);
        store.Upsert(Mercury);
        store.Upsert(Mercury with { Id = "another", Value = "No" });
        var recovered = new WidgetSnapshotStore(_directory).List();
        Assert.Equal(2, recovered.Count);
        Assert.Equal("No", recovered.Single(s => s.Id == "another").Value);
        Assert.Equal("Yes", recovered.Single(s => s.Id == "mercury").Value);
        Assert.Throws<ArgumentException>(() => store.Upsert(Mercury with { ObservedAt = Observation.AddSeconds(-1) }));
        Assert.Throws<ArgumentException>(() => store.Upsert(Mercury with { Value = "No" }));
        Assert.True(store.Delete("mercury"));
        Assert.False(store.Delete("mercury"));
        Assert.Equal("another", Assert.Single(store.List()).Id);
    }

    [Theory]
    [InlineData("../outside", "https://example.com/")]
    [InlineData("CON", "https://example.com/")]
    [InlineData("con", "https://example.com/")]
    [InlineData("lpt1", "https://example.com/")]
    [InlineData("mercury", "javascript:alert(1)")]
    [InlineData("mercury", "http://example.com/")]
    [InlineData("mercury", "https://secret@example.com/")]
    [InlineData("mercury", "https://127.0.0.1/")]
    public void Store_RejectsUnsafeIdentifiersAndSourceLinks(string id, string url) =>
        Assert.Throws<ArgumentException>(() => new WidgetSnapshotStore(_directory).Upsert(Mercury with { Id = id, SourceUrl = url }));

    [Fact]
    public void Store_RejectsMissingFieldsAndOversizedData()
    {
        Assert.Throws<ArgumentException>(() => WidgetSnapshotStore.Validate(Mercury with { Title = null! }));
        Assert.Throws<ArgumentException>(() => WidgetSnapshotStore.Validate(Mercury with { Detail = new string('x', 601) }));
        Assert.Throws<ArgumentException>(() => WidgetSnapshotStore.Validate(Mercury with { ObservedAt = default }));
        Assert.Throws<ArgumentException>(() => WidgetSnapshotStore.Validate(Mercury with { StaleAfterMinutes = 0 }));
        Assert.Throws<JsonException>(() => WidgetSnapshotStore.Parse(JsonSerializer.SerializeToElement(new { id = "mercury", arbitraryScript = "bad" })));
    }

    [Fact]
    public async Task Mcp_DiscoversAndInvokesProductionWidgetCapabilityWithoutClaimingDisplay()
    {
        var enabled = true;
        var capability = new WidgetCapability(NullLogger.Instance, new(_directory), isEnabled: () => enabled);
        var bridge = new McpToolBridge(() => new[] { capability });
        var listed = await bridge.HandleRequestAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        using (var doc = JsonDocument.Parse(listed!))
        {
            Assert.Contains(doc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray(),
                t => t.GetProperty("name").GetString() == "widgets.upsert");
        }
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 2, method = "tools/call",
            @params = new { name = "widgets.upsert", arguments = Mercury }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var result = await bridge.HandleRequestAsync(request);
        Assert.Contains("saved", result!);
        Assert.Equal(Mercury, Assert.Single(new WidgetSnapshotStore(_directory).List()));
        var response = await capability.ExecuteAsync(new() { Command = "widgets.list" });
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(response.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.True(payload.RootElement.GetProperty("requiresUserPin").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("host").GetProperty("supported").GetBoolean());

        enabled = false;
        Assert.Empty(capability.Commands);
        Assert.False((await capability.ExecuteAsync(new() { Command = "widgets.delete",
            Args = JsonSerializer.SerializeToElement(new { id = "mercury" }) })).Ok);
        Assert.Single(new WidgetSnapshotStore(_directory).List());
    }

    [Fact]
    public void Card_IsDataOnlyAndMarksAnExpiredObservationWithoutChangingItsValue()
    {
        var hostile = Mercury with { Title = "${hostileTitle}", Value = "${malicious}", Detail = "\"}] <script>alert(1)</script>" };
        var card = WidgetCard.Render(hostile, [hostile, Mercury with { Id = "second" }], Copy, Observation.AddHours(2));
        Assert.DoesNotContain("malicious", card.Template);
        Assert.DoesNotContain("hostileTitle", card.Template);
        using var data = JsonDocument.Parse(card.Data);
        Assert.Equal("${malicious}", data.RootElement.GetProperty("value").GetString());
        Assert.StartsWith("Stale ", data.RootElement.GetProperty("freshness").GetString());
        using var template = JsonDocument.Parse(card.Template);
        Assert.Equal("1.6", template.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void Binding_IsPerInstanceAndNeverSilentlySwitchesADeletedTracker()
    {
        Assert.Equal("mercury", WidgetCard.ResolveTracker(null, [Mercury]));
        Assert.Null(WidgetCard.ResolveTracker(null, [Mercury, Mercury with { Id = "second" }]));
        Assert.Equal("missing", WidgetCard.ResolveTracker("v1:missing", [Mercury]));
        Assert.Equal("second", WidgetCard.ResolveTracker("v1:second", [Mercury]));
    }

    private static WidgetPresentation Presentation(string body = """[{"type":"TextBlock","text":"${title}"}]""",
        string? state = null) => new(JsonDocument.Parse(
            """{"type":"AdaptiveCard","version":"1.6","body":""" + body + "}").RootElement.Clone(),
            InitialState: state is null ? null : JsonDocument.Parse(state).RootElement.Clone());

    [Fact]
    public void GeneratedCard_PreservesNativeElementsPropertiesBindingsAndReadOnlyFacts()
    {
        var cardDefinition = JsonDocument.Parse("""
            {
              "type":"AdaptiveCard","version":"1.6","header":{"text":"Space weather","iconUrl":"https://example.com/icon.webp"},
              "backgroundImage":{"url":"${$root.content.artwork}","fillMode":"Cover","horizontalAlignment":"Center"},
              "verticalContentAlignment":"Center","rtl":false,
              "body":[
                {"type":"RichTextBlock","inlines":[{"type":"TextRun","text":"${title}","weight":"Bolder","color":"Accent"}]},
                {"type":"Container","style":"emphasis","items":[
                  {"type":"TextBlock","text":"${value}","size":"ExtraLarge","$when":"${$host.widgetSize != 'small'}"},
                  {"type":"ImageSet","imageSize":"Small","images":[{"type":"Image","url":"https://example.net/artwork?size=200","altText":"Mercury"}]}
                ]},
                {"type":"FactSet","facts":[{"title":"Orbit","value":"88 days"}]},
                {"type":"Table","columns":[{"width":1}],"rows":[{"type":"TableRow","cells":[
                  {"type":"TableCell","items":[{"type":"TextBlock","text":"${content.label}"}]}
                ]}]},
                {"type":"Input.ChoiceSet","id":"units","choices":[{"title":"Celsius","value":"C"}]},
                {"type":"TextBlock","text":"${state.count}","$when":"${$host.hostTheme == 'dark'}"}
              ],
              "actions":[{"type":"Action.Execute","title":"Count","verb":"widget.increment","data":{"key":"count"},
                "$when":"${$host.widgetSize != 'small'}"},{"type":"Action.OpenUrl","title":"Learn","url":"https://example.org/learn"}]
            }
            """).RootElement.Clone();
        var presentation = new WidgetPresentation(cardDefinition,
            JsonSerializer.SerializeToElement(new { artwork = "https://cdn.example.org/mercury.webp?width=400", label = "${notExecuted}" }),
            JsonSerializer.SerializeToElement(new { count = 0, units = "C" }));
        var snapshot = Mercury with { Presentation = presentation };
        var card = WidgetCard.Render(snapshot, [snapshot], Copy, Observation.AddHours(3));
        using var template = JsonDocument.Parse(card.Template);
        using var data = JsonDocument.Parse(card.Data);
        var root = template.RootElement;
        Assert.Equal("Space weather", root.GetProperty("header").GetProperty("text").GetString());
        Assert.Equal("Cover", root.GetProperty("backgroundImage").GetProperty("fillMode").GetString());
        Assert.Equal("RichTextBlock", root.GetProperty("body")[0].GetProperty("type").GetString());
        Assert.Equal("FactSet", root.GetProperty("body")[2].GetProperty("type").GetString());
        Assert.Equal("Table", root.GetProperty("body")[3].GetProperty("type").GetString());
        Assert.Equal("${$host.isUserContextAuthenticated && ($host.widgetSize != 'small')}",
            root.GetProperty("body")[6].GetProperty("actions")[0].GetProperty("$when").GetString());
        Assert.Equal("${notExecuted}", data.RootElement.GetProperty("content").GetProperty("label").GetString());
        Assert.Equal(0, data.RootElement.GetProperty("state").GetProperty("count").GetInt32());
        Assert.Equal("Yes", data.RootElement.GetProperty("value").GetString());
        Assert.Equal("Stale 2026-01-01 12:00:00Z", data.RootElement.GetProperty("freshness").GetString());
        var observation = root.GetProperty("body")[7].GetProperty("items")[0];
        Assert.Equal("openclaw-observation", observation.GetProperty("id").GetString());
        Assert.Equal("${title}: ${value} | ${freshness}", observation.GetProperty("text").GetString());
        Assert.False(observation.TryGetProperty("$when", out _));
        Assert.Equal("https://example.org/learn",
            root.GetProperty("body")[6].GetProperty("actions")[1].GetProperty("url").GetString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Card_AnchorsObservationAndCenteredSourceTogetherAtBottomWithoutDivider(bool authored, bool multipleTrackers)
    {
        var snapshot = Mercury with { Presentation = authored ? Presentation() : null };
        WidgetSnapshot[] snapshots = multipleTrackers ? [snapshot, Mercury with { Id = "second" }] : [snapshot];
        var payload = WidgetCard.Render(snapshot, snapshots, Copy, Observation);
        using var template = JsonDocument.Parse(payload.Template);
        var root = template.RootElement;
        Assert.False(root.TryGetProperty("actions", out _));
        var body = root.GetProperty("body");
        var footer = body.EnumerateArray().Last();
        Assert.Equal("openclaw-source", footer.GetProperty("id").GetString());
        Assert.Equal("stretch", footer.GetProperty("height").GetString());
        Assert.False(footer.TryGetProperty("minHeight", out _));
        Assert.Equal("Bottom", footer.GetProperty("verticalContentAlignment").GetString());
        Assert.Equal("Small", footer.GetProperty("spacing").GetString());
        var items = footer.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        var link = items[1];
        Assert.Equal("RichTextBlock", link.GetProperty("type").GetString());
        Assert.Equal("Center", link.GetProperty("horizontalAlignment").GetString());
        Assert.Equal("Small", link.GetProperty("spacing").GetString());
        var run = Assert.Single(link.GetProperty("inlines").EnumerateArray());
        Assert.Equal("Source", run.GetProperty("text").GetString());
        Assert.Equal("Small", run.GetProperty("size").GetString());
        Assert.Equal("Action.OpenUrl", run.GetProperty("selectAction").GetProperty("type").GetString());
        Assert.Equal("${sourceUrl}", run.GetProperty("selectAction").GetProperty("url").GetString());
        var observation = items[0];
        Assert.Equal("openclaw-observation", observation.GetProperty("id").GetString());
        Assert.Equal("${title}: ${value} | ${freshness}", observation.GetProperty("text").GetString());
        Assert.Equal("Small", observation.GetProperty("size").GetString());
        Assert.Equal("Small", observation.GetProperty("spacing").GetString());
        Assert.True(observation.GetProperty("wrap").GetBoolean());
        Assert.False(observation.TryGetProperty("separator", out _));
        Assert.False(observation.TryGetProperty("$when", out _));
        if (multipleTrackers)
        {
            var selection = body[body.GetArrayLength() - 2];
            Assert.Equal("ActionSet", selection.GetProperty("type").GetString());
            Assert.Equal(WidgetCard.SelectVerb, selection.GetProperty("actions")[0].GetProperty("verb").GetString());
            Assert.Equal("${$host.isUserContextAuthenticated}",
                selection.GetProperty("actions")[0].GetProperty("$when").GetString());
        }
    }

    [Fact]
    public void Card_DefaultSmallLayoutOmitsOptionalRowsButKeepsSourceFacts()
    {
        var payload = WidgetCard.Render(Mercury, [Mercury], Copy, Observation);
        using var template = JsonDocument.Parse(payload.Template);
        var body = template.RootElement.GetProperty("body");
        Assert.Equal("${$host.widgetSize != 'small'}", body[0].GetProperty("$when").GetString());
        Assert.Equal("${if($host.widgetSize == 'small', 'Medium', 'ExtraLarge')}",
            body[1].GetProperty("size").GetString());
        Assert.Equal("${$host.widgetSize != 'small'}", body[2].GetProperty("$when").GetString());
        Assert.Equal("openclaw-source", body[3].GetProperty("id").GetString());
        var observation = body[3].GetProperty("items")[0];
        Assert.Equal("${title}: ${value} | ${freshness}", observation.GetProperty("text").GetString());
        Assert.False(observation.TryGetProperty("$when", out _));
    }

    [Fact]
    public void Card_GuardsNestedActionsAndKeepsSettingsAndSizeThemeExpressions()
    {
        var presentation = Presentation("""
            [{"type":"Container","$when":"${$host.isSettingsPayload}","items":[
              {"type":"Input.Toggle","id":"enabled","title":"Show detail","value":"${state.enabled}"},
              {"type":"ActionSet","actions":[{"type":"Action.Execute","title":"Save","verb":"widget.set"}]}
            ]},
            {"type":"TextBlock","text":"${detail}","$when":"${!$host.isSettingsPayload && state.enabled}"}]
            """, """{"enabled":true}""");
        var snapshot = Mercury with { Presentation = presentation };
        var card = WidgetCard.Render(snapshot, [snapshot], Copy, Observation);
        using var template = JsonDocument.Parse(card.Template);
        var body = template.RootElement.GetProperty("body");
        Assert.Equal("${$host.isSettingsPayload}", body[0].GetProperty("$when").GetString());
        Assert.Equal("${$host.isUserContextAuthenticated}",
            body[0].GetProperty("items")[1].GetProperty("actions")[0].GetProperty("$when").GetString());
        Assert.Equal("${!$host.isSettingsPayload && state.enabled}", body[1].GetProperty("$when").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-300)]
    [InlineData(330)]
    public void Store_AllowsRestylingWithoutInventingFreshnessButStillRejectsConflictingFacts(int offsetMinutes)
    {
        var store = new WidgetSnapshotStore(_directory);
        store.Upsert(Mercury);
        var styled = Mercury with { ObservedAt = Observation.ToOffset(TimeSpan.FromMinutes(offsetMinutes)),
            Presentation = Presentation() };
        store.Upsert(styled);
        var saved = Assert.Single(new WidgetSnapshotStore(_directory).List());
        Assert.Equal(Observation, saved.ObservedAt);
        Assert.Equal("AdaptiveCard", saved.Presentation!.Card.GetProperty("type").GetString());
        store.Upsert(saved);
        Assert.Throws<ArgumentException>(() => store.Upsert(styled with { Value = "No" }));
        Assert.Throws<ArgumentException>(() => store.Upsert(styled with { ObservedAt = Observation.AddSeconds(-1) }));
        store.Upsert(Mercury);
        Assert.Null(Assert.Single(store.List()).Presentation);
    }

    [Theory]
    [InlineData("https://localhost/mercury.png")]
    [InlineData("https://127.0.0.1/mercury.png")]
    [InlineData("https://secret@science.nasa.gov/mercury.png")]
    [InlineData("https://science.nasa.gov:8443/mercury.png")]
    [InlineData("http://science.nasa.gov/mercury.png")]
    [InlineData("file:///C:/secret.png")]
    [InlineData("data:image/svg+xml,bad")]
    [InlineData("https://router.local/mercury.png")]
    [InlineData("https://host.internal./mercury.png")]
    [InlineData("${state.image}")]
    [InlineData("${content.image}")]
    [InlineData("openclaw-asset:../secret")]
    [InlineData("openclaw-asset:mercury-bright?file=secret")]
    [InlineData("openclaw-asset:unknown")]
    [InlineData("ms-appx:///Assets/secret.png")]
    public void Presentation_RejectsLocalComputedOrCredentialBearingResourceAddresses(string url)
    {
        var card = JsonSerializer.SerializeToElement(new
        { type = "AdaptiveCard", version = "1.6", body = new[] { new { type = "Image", url, altText = "Mercury" } } });
        Assert.Throws<ArgumentException>(() => new WidgetPresentation(card).Validate());
    }

    [Theory]
    [InlineData("portrait")]
    [InlineData("status-muted")]
    [InlineData("backdrop")]
    public void Card_ExpandsAgentNamedArtworkAndBoundBackgroundsWithoutMutatingTextOrSavedAssets(string name)
    {
        var asset = "openclaw-asset:" + name;
        var presentation = new WidgetPresentation(JsonSerializer.SerializeToElement(new
        {
            type = "AdaptiveCard", version = "1.5",
            backgroundImage = new { url = "${$root.content.artwork}" },
            actions = new[] { new { type = "Action.OpenUrl", title = "Read", url = "https://example.com/", iconUrl = asset } },
            body = new object[] { new { type = "Image", url = asset }, new { type = "TextBlock", text = asset } }
        }), JsonSerializer.SerializeToElement(new { artwork = asset }),
            Assets: JsonSerializer.SerializeToElement(new Dictionary<string, object> { [name] = new { base64 = PixelPng } }));
        var snapshot = Mercury with { Presentation = presentation };
        var payload = WidgetCard.Render(snapshot, [snapshot], Copy, Observation);
        using var card = JsonDocument.Parse(payload.Template);
        var url = card.RootElement.GetProperty("body")[0].GetProperty("url").GetString()!;
        Assert.StartsWith("data:image/png;base64,", url);
        var png = Convert.FromBase64String(url["data:image/png;base64,".Length..]);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8));
        Assert.Equal(Convert.FromBase64String(PixelPng), png);
        Assert.Equal(1, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(1, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
        Assert.Equal(url, card.RootElement.GetProperty("backgroundImage").GetProperty("url").GetString());
        var action = card.RootElement.GetProperty("body")[2].GetProperty("actions")[0];
        Assert.Equal(url, action.GetProperty("iconUrl").GetString());
        Assert.Equal("https://example.com/", action.GetProperty("url").GetString());
        Assert.Equal(asset, card.RootElement.GetProperty("body")[1].GetProperty("text").GetString());
        Assert.Equal(asset, presentation.Data!.Value.GetProperty("artwork").GetString());
        using var data = JsonDocument.Parse(payload.Data);
        Assert.Equal(asset, data.RootElement.GetProperty("content").GetProperty("artwork").GetString());
        var store = new WidgetSnapshotStore(_directory);
        store.Upsert(snapshot);
        var recovered = Assert.Single(new WidgetSnapshotStore(_directory).List());
        Assert.Equal(PixelPng, recovered.Presentation!.Assets!.Value.GetProperty(name).GetProperty("base64").GetString());
    }

    [Theory]
    [InlineData("openclaw-asset:backdrop")]
    [InlineData("${$root.content.artwork}")]
    public void Presentation_SuppliedArtworkNeverBecomesNavigation(string url)
    {
        var presentation = new WidgetPresentation(JsonSerializer.SerializeToElement(new
        {
            type = "AdaptiveCard", version = "1.5", body = Array.Empty<object>(),
            actions = new[] { new { type = "Action.OpenUrl", title = "Source", url } }
        }), JsonSerializer.SerializeToElement(new { artwork = "openclaw-asset:backdrop" }),
            Assets: JsonSerializer.SerializeToElement(new { backdrop = new { base64 = PixelPng } }));
        Assert.Throws<ArgumentException>(() => presentation.Validate());
    }

    [Theory]
    [InlineData("not base64")]
    [InlineData("PHN2Zz48L3N2Zz4=")]
    [InlineData("")]
    public void Presentation_RejectsNonPngArtwork(string base64)
    {
        var presentation = new WidgetPresentation(JsonSerializer.SerializeToElement(new
        {
            type = "AdaptiveCard", version = "1.5",
            body = new[] { new { type = "Image", url = "openclaw-asset:portrait" } }
        }), Assets: JsonSerializer.SerializeToElement(new { portrait = new { base64 } }));
        Assert.Throws<ArgumentException>(() => presentation.Validate());
    }

    [Fact]
    public void Presentation_BoundsDecodedArtworkAndImageDimensions()
    {
        var card = JsonSerializer.SerializeToElement(new { type = "AdaptiveCard", version = "1.5", body = Array.Empty<object>() });
        var oversized = new WidgetPresentation(card,
            Assets: JsonSerializer.SerializeToElement(new { portrait = new { base64 = Convert.ToBase64String(new byte[262145]) } }));
        Assert.Contains("decoded bytes", Assert.Throws<ArgumentException>(() => oversized.Validate()).Message);
        var png = Convert.FromBase64String(PixelPng);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), 4097);
        var dimensions = new WidgetPresentation(card,
            Assets: JsonSerializer.SerializeToElement(new { portrait = new { base64 = Convert.ToBase64String(png) } }));
        Assert.Contains("4096 pixels", Assert.Throws<ArgumentException>(() => dimensions.Validate()).Message);
        var unknown = new WidgetPresentation(card,
            Assets: JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["../private"] = new { base64 = PixelPng } }));
        Assert.Throws<ArgumentException>(() => unknown.Validate());
    }

    [Fact]
    public void Store_RejectsImageAmplificationBeforeReplacingLastGoodSnapshot()
    {
        var headerOnlyPng = new byte[200000];
        var pixel = Convert.FromBase64String(PixelPng);
        pixel.CopyTo(headerOnlyPng, 0);
        pixel.AsSpan(pixel.Length - 12).CopyTo(headerOnlyPng.AsSpan(headerOnlyPng.Length - 12));
        var assets = JsonSerializer.SerializeToElement(new { portrait = new { base64 = Convert.ToBase64String(headerOnlyPng) } });
        var single = new WidgetPresentation(JsonSerializer.SerializeToElement(new
        {
            type = "AdaptiveCard", version = "1.5", body = new[] { new { type = "Image", url = "openclaw-asset:portrait" } }
        }), Assets: assets);
        single.Validate();
        var amplified = single with { Card = JsonSerializer.SerializeToElement(new
        {
            type = "AdaptiveCard", version = "1.5",
            body = Enumerable.Range(0, 5).Select(_ => new { type = "Image", url = "openclaw-asset:portrait" }).ToArray()
        }) };
        var store = new WidgetSnapshotStore(_directory);
        store.Upsert(Mercury);
        Assert.Contains("Expanded widget artwork", Assert.Throws<ArgumentException>(
            () => store.Upsert(Mercury with { Presentation = amplified })).Message);
        Assert.Null(Assert.Single(new WidgetSnapshotStore(_directory).List()).Presentation);
    }

    [Theory]
    [InlineData("""[{"type":"ActionSet","actions":[{"type":"Action.Execute","verb":"system.run"}]}]""")]
    [InlineData("""[{"type":"TextBlock","text":"Title","$when":false}]""")]
    [InlineData("""[{"type":"TextBlock","text":"Title","id":"openclaw-observation"}]""")]
    [InlineData("""[{"type":"ActionSet","actions":[{"type":"Action.ToggleVisibility","targetElements":["openclaw-observation"]}]}]""")]
    [InlineData("""[{"type":"TextBlock","text":"Title","text":"hidden"}]""")]
    [InlineData("""[{"type":"Image","url":"${$root.content.missing}"}]""")]
    [InlineData("""[{"type":"Media","sources":[{"mimeType":"video/mp4","url":"file:///secret"}]}]""")]
    public async Task Mcp_RejectsInvalidPresentationWithoutReplacingLastGoodSnapshot(string body)
    {
        var store = new WidgetSnapshotStore(_directory);
        store.Upsert(Mercury);
        var capability = new WidgetCapability(NullLogger.Instance, store);
        var response = await capability.ExecuteAsync(new()
        {
            Command = "widgets.upsert",
            Args = JsonSerializer.SerializeToElement(Mercury with
            {
                Presentation = Presentation(body)
            })
        });
        Assert.False(response.Ok);
        Assert.Equal(Mercury, Assert.Single(store.List()));
    }

    [Fact]
    public void Presentation_BoundsObjectsDepthAndPayloadWithoutArbitraryImageOrLayoutLimits()
    {
        var nodes = Enumerable.Repeat<object>(new { type = "Image", url = "https://example.com/image.webp?width=100" }, 255).ToList();
        Presentation(JsonSerializer.Serialize(nodes)).Validate();
        nodes.Add(nodes[0]);
        Assert.Throws<ArgumentException>(() => Presentation(JsonSerializer.Serialize(nodes)).Validate());
        object nested = new object[] { new { type = "TextBlock", text = "${value}" } };
        for (var level = 0; level < 13; level++)
            nested = new[] { new { type = "Container", items = nested } };
        Assert.Throws<ArgumentException>(() => Presentation(JsonSerializer.Serialize(nested)).Validate());
        Assert.Throws<ArgumentException>(() => Presentation(
            JsonSerializer.Serialize(new[] { new { type = "TextBlock", text = new string('x', 65_536) } })).Validate());
    }

    [Theory]
    [InlineData("""{"type":"AdaptiveCard","version":"2.0","body":[]}""")]
    [InlineData("""{"type":"AdaptiveCard","version":"1.6","body":[],"$when":"${false}"}""")]
    [InlineData("""{"type":"AdaptiveCard","version":"1.6","body":[],"$data":"${content}"}""")]
    [InlineData("""{"type":"AdaptiveCard","version":"1.6","body":[],"metadata":{"webUrl":"https://example.com"}}""")]
    [InlineData("""{"type":"AdaptiveCard","version":"1.6","body":[],"authentication":{}}""")]
    [InlineData("""{"type":"AdaptiveCard","version":"1.6","body":[],"refresh":{"action":{"type":"Action.Execute","verb":"widget.increment"}}}""")]
    public void Presentation_RejectsUnsupportedRootExecutionAndObservationOverrides(string card) =>
        Assert.Throws<ArgumentException>(() => new WidgetPresentation(JsonDocument.Parse(card).RootElement.Clone()).Validate());

    [Fact]
    public void NativeInteractions_PersistPerInstanceWithoutChangingObservationsOrOtherInstances()
    {
        var snapshot = Mercury with { Presentation = Presentation(state: """{"count":0,"enabled":false,"units":"C"}""") };
        var first = WidgetInstanceState.Parse("v1:mercury", [snapshot]);
        var second = WidgetInstanceState.Parse(null, [snapshot]);
        first = first.Apply(snapshot.Presentation, "widget.increment", JsonSerializer.SerializeToElement(new { key = "count", amount = 2 }));
        first = first.Apply(snapshot.Presentation, "widget.toggle", JsonSerializer.SerializeToElement(new { key = "enabled" }));
        first = first.Apply(snapshot.Presentation, "widget.set", JsonSerializer.SerializeToElement(new
        { units = "F", enabled = "true", title = "Forged", value = "No", observedAt = "later" }));
        var recovered = WidgetInstanceState.Parse(first.Serialize(), [snapshot]);
        Assert.Equal(2, recovered.Values["count"]!.GetValue<double>());
        Assert.True(recovered.Values["enabled"]!.GetValue<bool>());
        Assert.Equal("F", recovered.Values["units"]!.GetValue<string>());
        Assert.False(recovered.Values.ContainsKey("title"));
        Assert.Equal(0, second.Values["count"]!.GetValue<int>());
        Assert.Equal("Yes", snapshot.Value);
        Assert.Equal(Observation, snapshot.ObservedAt);
        var payload = WidgetCard.Render(snapshot, [snapshot], Copy, Observation.AddHours(3), recovered.Values);
        using var data = JsonDocument.Parse(payload.Data);
        Assert.Equal("Yes", data.RootElement.GetProperty("value").GetString());
        Assert.Equal(2, data.RootElement.GetProperty("state").GetProperty("count").GetDouble());
        var reset = recovered.Apply(snapshot.Presentation, "widget.reset", JsonSerializer.SerializeToElement(new { }));
        Assert.Equal(0, reset.Values["count"]!.GetValue<int>());
        Assert.False(reset.Values["enabled"]!.GetValue<bool>());
        Assert.Equal("F", recovered.Values["units"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("system.run", "{}")]
    [InlineData("widget.increment", """{"key":"value"}""")]
    [InlineData("widget.set", """{"count":{"nested":"not a scalar"}}""")]
    [InlineData("widget.set", """{"count":"invalid"}""")]
    public void NativeInteractions_RejectUndeclaredCommandsAndInvalidStateWithoutMutation(string verb, string input)
    {
        var presentation = Presentation(state: """{"count":0}""");
        var state = new WidgetInstanceState("mercury", WidgetInstanceState.Initial(presentation));
        Assert.Throws<ArgumentException>(() => state.Apply(presentation, verb, JsonDocument.Parse(input).RootElement));
        Assert.Equal(0, state.Values["count"]!.GetValue<int>());
    }

    [Fact]
    public void NativeInteractions_UseNewDesignDefaultsAndDoNotResurrectRemovedState()
    {
        var first = Mercury with { Presentation = Presentation(state: """{"count":2,"obsolete":true,"optional":null}""") };
        var state = WidgetInstanceState.Parse(null, [first]);
        state = state.Apply(first.Presentation, "widget.set", JsonSerializer.SerializeToElement(new { optional = "saved" }));
        var restyled = first with { Presentation = Presentation(state: """{"count":0,"units":"C","optional":null}""") };
        var reconciled = WidgetInstanceState.Parse(state.Serialize(), [restyled]);
        Assert.Equal(2, reconciled.Values["count"]!.GetValue<int>());
        Assert.Equal("C", reconciled.Values["units"]!.GetValue<string>());
        Assert.Equal("saved", reconciled.Values["optional"]!.GetValue<string>());
        Assert.False(reconciled.Values.ContainsKey("obsolete"));
    }

    [Theory]
    [InlineData("native", false)]
    [InlineData("minimal", false)]
    [InlineData("cosmic", false)]
    [InlineData("mystic", false)]
    [InlineData("native", true)]
    [InlineData("minimal", true)]
    [InlineData("cosmic", true)]
    [InlineData("mystic", true)]
    public void Store_MigratesSavedPresetToCardButRejectsPresetsInNewRequests(string theme, bool nullBody)
    {
        var store = new WidgetSnapshotStore(_directory);
        store.Upsert(Mercury);
        var path = Path.Combine(_directory, "widgets", "mercury.json");
        var legacy = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        legacy["presentation"] = new JsonObject { ["theme"] = theme };
        if (nullBody)
            legacy["presentation"]!["body"] = null;
        File.WriteAllText(path, legacy.ToJsonString());
        var migrated = Assert.Single(store.List());
        Assert.Equal("Yes", migrated.Value);
        Assert.Equal(Observation, migrated.ObservedAt);
        migrated.Presentation!.Validate();
        var nativeCard = WidgetCard.Render(migrated, [migrated], Copy, Observation);
        if (theme is "cosmic" or "mystic") Assert.Contains("mercury-messenger-globe", nativeCard.Template);
        Assert.Throws<JsonException>(() => WidgetSnapshotStore.Parse(JsonSerializer.SerializeToElement(legacy)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
