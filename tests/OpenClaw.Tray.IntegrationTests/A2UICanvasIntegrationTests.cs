using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.Tray.IntegrationTests;

/// <summary>
/// End-to-end "agent presents a rich UI to the user" smoke test against a live
/// tray. Builds a v0.8 A2UI surface that exercises one component per catalog
/// category (containers, display, interactive), pushes it through MCP, asks the
/// running tray to introspect itself via canvas.a2ui.dump, mutates the data
/// model and re-introspects, then captures a real PNG of the rendered window.
///
/// Where the per-component UITests in OpenClaw.Tray.UITests verify each
/// renderer in isolation against a synthetic harness, this test proves the same
/// pipeline survives the full hop: MCP HTTP → bridge event → UI dispatcher →
/// A2UICanvasWindow.Push → A2UIRouter → SurfaceHost → live XAML tree, all
/// inside the shipped tray exe.
/// </summary>
public sealed class A2UICanvasIntegrationTests : IClassFixture<TrayAppFixture>
{
    private const string SurfaceId = "integration-rich";

    private readonly TrayAppFixture _fixture;

    public A2UICanvasIntegrationTests(TrayAppFixture fixture) => _fixture = fixture;

    [IntegrationFact]
    public async Task RichSurface_PushDumpAndSnapshot_RoundTripsThroughNativeRenderer()
    {
        // Other tests in the class fixture push their own minimal surfaces.
        // Reset first so we can assert exact surface counts deterministically.
        await _fixture.Client.CallToolExpectSuccessAsync("canvas.a2ui.reset");

        var (jsonl, componentIds) = BuildRichJsonl();

        // 1. Push the rich payload. Bridge returns success after enqueuing the
        //    UI work; the dispatcher applies parser → router → SurfaceHost in
        //    order before any subsequent dispatcher-bound call (dump, caps,
        //    snapshot) gets to run.
        using (var pushResp = await _fixture.Client.CallToolExpectSuccessAsync("canvas.a2ui.push", new { jsonl }))
        {
            Assert.True(pushResp.RootElement.GetProperty("pushed").GetBoolean());
        }

        // 2. canvas.caps should now report the native renderer with introspection on.
        using (var capsResp = await _fixture.Client.CallToolExpectSuccessAsync("canvas.caps"))
        {
            var caps = capsResp.RootElement;
            Assert.Equal("native", caps.GetProperty("renderer").GetString());
            Assert.True(caps.GetProperty("snapshot").GetBoolean());
            var a2ui = caps.GetProperty("a2ui");
            Assert.Equal("0.8", a2ui.GetProperty("version").GetString());
            Assert.True(a2ui.GetProperty("introspect").GetBoolean());
            Assert.True(a2ui.GetProperty("push").GetBoolean());
            Assert.True(a2ui.GetProperty("reset").GetBoolean());
        }

        // 3. canvas.a2ui.dump exposes the surface graph the renderer is driving.
        using (var dumpResp = await _fixture.Client.CallToolExpectSuccessAsync("canvas.a2ui.dump"))
        {
            var dump = dumpResp.RootElement;
            Assert.Equal("native", dump.GetProperty("renderer").GetString());
            Assert.Equal("0.8", dump.GetProperty("a2uiVersion").GetString());
            Assert.Equal(1, dump.GetProperty("surfaceCount").GetInt32());

            var surface = dump.GetProperty("surfaces").GetProperty(SurfaceId);
            Assert.Equal("rootCard", surface.GetProperty("root").GetString());

            var presentIds = surface.GetProperty("components").EnumerateArray()
                .Select(c => c.GetProperty("id").GetString()!)
                .ToHashSet();
            foreach (var id in componentIds)
            {
                Assert.True(presentIds.Contains(id), $"Expected component '{id}' in dump, got: {string.Join(",", presentIds)}");
            }

            // Every declared component should expose its componentName + properties payload.
            var rootCard = surface.GetProperty("components").EnumerateArray()
                .First(c => c.GetProperty("id").GetString() == "rootCard");
            Assert.Equal("Card", rootCard.GetProperty("componentName").GetString());
            Assert.Equal("mainCol", rootCard.GetProperty("properties").GetProperty("child").GetString());

            // Initial data model seed propagated through to the per-surface store.
            var dm = surface.GetProperty("dataModel");
            Assert.Equal("Hello, integration tests", dm.GetProperty("headline").GetString());
            Assert.False(dm.GetProperty("agreed").GetBoolean());
            Assert.Equal(20.0, dm.GetProperty("volume").GetDouble(), 3);
        }

        // 4. Mutate the data model — agent UIs do this constantly. A pure
        //    dataModelUpdate (no surfaceUpdate) must flow through bound
        //    properties without rebuilding the visual tree.
        var dataUpdate = new JsonObject
        {
            ["dataModelUpdate"] = new JsonObject
            {
                ["surfaceId"] = SurfaceId,
                ["contents"] = new JsonArray
                {
                    new JsonObject { ["key"] = "headline", ["valueString"] = "Sensor 4 back online" },
                    new JsonObject { ["key"] = "agreed",   ["valueBoolean"] = true },
                    new JsonObject { ["key"] = "volume",   ["valueNumber"] = 75.0 },
                },
            },
        }.ToJsonString();
        await _fixture.Client.CallToolExpectSuccessAsync("canvas.a2ui.push", new { jsonl = dataUpdate });

        using (var dumpAfter = await _fixture.Client.CallToolExpectSuccessAsync("canvas.a2ui.dump"))
        {
            var dm = dumpAfter.RootElement.GetProperty("surfaces").GetProperty(SurfaceId).GetProperty("dataModel");
            Assert.Equal("Sensor 4 back online", dm.GetProperty("headline").GetString());
            Assert.True(dm.GetProperty("agreed").GetBoolean());
            Assert.Equal(75.0, dm.GetProperty("volume").GetDouble(), 3);
        }

        // 5. Snapshot the rendered window. Window activation + first layout pass
        //    are racy under integration conditions (locked desktop, hidden
        //    session), so we accept either a valid PNG or a well-formed tool
        //    error. When the PNG arrives, validate the header — that's proof
        //    the native A2UI tree got far enough to rasterize.
        var (snapErr, snapText) = await _fixture.Client.CallToolAcceptingFailureAsync("canvas.snapshot", new
        {
            format = "png",
            maxWidth = 800,
        });
        if (!snapErr)
        {
            using var snapDoc = JsonDocument.Parse(snapText);
            var b64 = snapDoc.RootElement.GetProperty("base64").GetString();
            Assert.False(string.IsNullOrWhiteSpace(b64));
            var bytes = Convert.FromBase64String(b64!);
            // PNG signature: 89 50 4E 47 0D 0A 1A 0A
            Assert.True(bytes.Length > 8, $"Snapshot bytes too small: {bytes.Length}");
            Assert.Equal(0x89, bytes[0]);
            Assert.Equal((byte)'P', bytes[1]);
            Assert.Equal((byte)'N', bytes[2]);
            Assert.Equal((byte)'G', bytes[3]);
            Assert.Equal(0x0D, bytes[4]);
            Assert.Equal(0x0A, bytes[5]);
            Assert.Equal(0x1A, bytes[6]);
            Assert.Equal(0x0A, bytes[7]);
        }

        // 6. Reset clears everything; dump should report no surfaces.
        await _fixture.Client.CallToolExpectSuccessAsync("canvas.a2ui.reset");
        using (var dumpAfterReset = await _fixture.Client.CallToolExpectSuccessAsync("canvas.a2ui.dump"))
        {
            Assert.Equal(0, dumpAfterReset.RootElement.GetProperty("surfaceCount").GetInt32());
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a v0.8 surface that exercises one component per catalog category,
    /// plus an initial dataModelUpdate so bound widgets paint with sane values
    /// on the first frame. Returns the JSONL string and the list of component
    /// ids the caller can assert against the dump.
    /// </summary>
    private static (string Jsonl, string[] ComponentIds) BuildRichJsonl()
    {
        var componentIds = new[]
        {
            "rootCard", "mainCol",
            "title", "subtitle",
            "buttonRow", "btnSave", "btnCancel", "lblSave", "lblCancel",
            "agreeBox",
            "volumeSlider",
            "divider",
            "helpTabs", "tabOverview", "tabDetails",
            "footerIcon",
        };

        var components = new JsonArray
        {
            // Containers
            Component("rootCard", "Card", new JsonObject { ["child"] = "mainCol" }),
            Component("mainCol", "Column", new JsonObject
            {
                ["children"] = Children(
                    "title", "subtitle", "buttonRow",
                    "agreeBox", "volumeSlider",
                    "divider", "helpTabs", "footerIcon"),
            }),
            Component("buttonRow", "Row", new JsonObject { ["children"] = Children("btnSave", "btnCancel") }),
            Component("helpTabs", "Tabs", new JsonObject
            {
                ["tabItems"] = new JsonArray
                {
                    new JsonObject { ["title"] = Lit("Overview"), ["child"] = "tabOverview" },
                    new JsonObject { ["title"] = Lit("Details"),  ["child"] = "tabDetails" },
                },
            }),

            // Display
            Component("title", "Text", new JsonObject
            {
                ["text"] = Lit("OpenClaw Daily Briefing"),
                ["usageHint"] = "h1",
            }),
            Component("subtitle",    "Text",    new JsonObject { ["text"] = Path("/headline") }),
            Component("tabOverview", "Text",    new JsonObject { ["text"] = Lit("Two new alerts since yesterday.") }),
            Component("tabDetails",  "Text",    new JsonObject { ["text"] = Lit("Sensor 4 dropped offline at 03:14.") }),
            Component("lblSave",     "Text",    new JsonObject { ["text"] = Lit("Save") }),
            Component("lblCancel",   "Text",    new JsonObject { ["text"] = Lit("Cancel") }),
            Component("divider",     "Divider", new JsonObject { ["axis"] = "horizontal" }),
            Component("footerIcon",  "Icon",    new JsonObject { ["name"] = Lit("settings") }),

            // Interactive
            Component("btnSave",   "Button", new JsonObject { ["child"] = "lblSave",   ["action"] = new JsonObject { ["name"] = "save"   } }),
            Component("btnCancel", "Button", new JsonObject { ["child"] = "lblCancel", ["action"] = new JsonObject { ["name"] = "cancel" } }),
            Component("agreeBox", "CheckBox", new JsonObject
            {
                ["label"] = Lit("I have read the briefing"),
                ["value"] = Path("/agreed"),
            }),
            Component("volumeSlider", "Slider", new JsonObject
            {
                ["minValue"] = 0.0,
                ["maxValue"] = 100.0,
                ["value"] = Path("/volume"),
            }),
        };

        var styles = new JsonObject
        {
            ["primaryColor"] = "#FF6F61",
            ["radius"] = 8.0,
            ["spacing"] = 12.0,
        };

        var surfaceUpdate = new JsonObject
        {
            ["surfaceUpdate"] = new JsonObject
            {
                ["surfaceId"] = SurfaceId,
                ["components"] = components,
            },
        }.ToJsonString();

        var beginRendering = new JsonObject
        {
            ["beginRendering"] = new JsonObject
            {
                ["surfaceId"] = SurfaceId,
                ["root"] = "rootCard",
                ["styles"] = styles,
            },
        }.ToJsonString();

        var seedDataModel = new JsonObject
        {
            ["dataModelUpdate"] = new JsonObject
            {
                ["surfaceId"] = SurfaceId,
                ["contents"] = new JsonArray
                {
                    new JsonObject { ["key"] = "headline", ["valueString"]  = "Hello, integration tests" },
                    new JsonObject { ["key"] = "agreed",   ["valueBoolean"] = false },
                    new JsonObject { ["key"] = "volume",   ["valueNumber"]  = 20.0 },
                },
            },
        }.ToJsonString();

        var jsonl = surfaceUpdate + "\n" + beginRendering + "\n" + seedDataModel;
        return (jsonl, componentIds);
    }

    private static JsonObject Component(string id, string componentName, JsonObject? props = null) =>
        new()
        {
            ["id"] = id,
            ["component"] = new JsonObject { [componentName] = props ?? new JsonObject() },
        };

    private static JsonObject Lit(string s) => new() { ["literalString"] = s };
    private static JsonObject Path(string p) => new() { ["path"] = p };

    private static JsonObject Children(params string[] ids)
    {
        var arr = new JsonArray();
        foreach (var id in ids) arr.Add(JsonValue.Create(id));
        return new JsonObject { ["explicitList"] = arr };
    }
}
