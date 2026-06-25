using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using OpenClawTray.A2UI.Rendering;
using OpenClawTray.A2UI.Protocol;
using OpenClawTray.A2UI.Theming;
using Xunit;
using static OpenClaw.Tray.UITests.TestSupport;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Store-level coverage for the v0.8 <c>valueArray</c> data-model value driving
/// the production <see cref="OpenClawTray.A2UI.DataModel.DataModelStore"/>.
///
/// These run on the UI thread because the store is dispatcher-affine. The parser
/// happy paths live in OpenClaw.Tray.Tests/A2UIDataModelArrayTests; here we pin
/// the bits that only the store enforces: array landing at a (base) path, and
/// the depth guard that bounds nested arrays the same way it bounds nested maps.
/// The depth guard is exercised by constructing <see cref="DataModelEntry"/>
/// objects directly — JSONL would hit System.Text.Json's MaxDepth first.
/// </summary>
[Collection(UICollection.Name)]
public sealed class A2UIDataModelStoreTests
{
    private readonly UIThreadFixture _ui;
    public A2UIDataModelStoreTests(UIThreadFixture ui) => _ui = ui;

    [Fact]
    public async Task ApplyDataModelUpdate_ValueArray_LandsAtBasePath()
    {
        await _ui.RunOnUIAsync(() =>
        {
            var harness = BuildHarness(_ui);
            var entry = new DataModelEntry
            {
                Key = "picked",
                ValueArray = new[]
                {
                    new DataModelEntry { Key = string.Empty, ValueString = "g" },
                    new DataModelEntry { Key = string.Empty, ValueString = "b" },
                },
            };
            harness.DataModel.ApplyDataModelUpdate("s", "/form", new[] { entry });

            var node = harness.DataModel.Read("s", "/form/picked");
            var arr = Assert.IsType<JsonArray>(node);
            Assert.Equal(new[] { "g", "b" }, arr.Select(n => n!.GetValue<string>()));
        });
    }

    [Fact]
    public async Task ApplyDataModelUpdate_ValueArray_NotifiesElementPathSubscribers()
    {
        await _ui.RunOnUIAsync(() =>
        {
            var harness = BuildHarness(_ui);
            var observable = harness.DataModel.GetOrCreate("s");
            var notifications = 0;
            using var subscription = observable.Subscribe("/codes/0", () => notifications++);

            harness.DataModel.ApplyDataModelUpdate("s", null, new[]
            {
                new DataModelEntry
                {
                    Key = "codes",
                    ValueArray = new[]
                    {
                        new DataModelEntry { Key = string.Empty, ValueString = "1234" },
                    },
                },
            });

            Assert.Equal(1, notifications);
            Assert.Equal("1234", observable.ReadString("/codes/0"));
        });
    }

    [Theory]
    [InlineData("/codes/01")]
    [InlineData("/codes/+1")]
    [InlineData("/codes/ 1")]
    public async Task Read_NonCanonicalArrayIndex_DoesNotResolve(string pointer)
    {
        await _ui.RunOnUIAsync(() =>
        {
            var harness = BuildHarness(_ui);
            harness.DataModel.ApplyDataModelUpdate("s", null, new[]
            {
                new DataModelEntry
                {
                    Key = "codes",
                    ValueArray = new[]
                    {
                        new DataModelEntry { Key = string.Empty, ValueString = "1234" },
                        new DataModelEntry { Key = string.Empty, ValueString = "5678" },
                    },
                },
            });

            Assert.Null(harness.DataModel.Read("s", pointer));
        });
    }

    [Theory]
    [InlineData("/codes/1")]
    [InlineData("/codes/01")]
    public async Task BuildActionContext_RedactsRegisteredSecretArrayElement_WhenParentPathIsRequested(string secretPath)
    {
        await _ui.RunOnUIAsync(() =>
        {
            var ctx = BuildRenderContext(
                JsonNode.Parse("""{ "codes": ["1234", "5678"] }""")!.AsObject(),
                secretPath);
            var source = BuildSourceComponent("""{ "dataBinding": ["/codes"] }""");
            var action = JsonNode.Parse("""
            {
                "context": [
                    { "key": "codes", "value": { "path": "/codes" } }
                ]
            }
            """);

            var result = ctx.BuildActionContext(source, action)!;

            var codes = Assert.IsType<JsonArray>(result["codes"]);
            Assert.Equal("1234", (string?)codes[0]);
            Assert.Equal("[REDACTED]", (string?)codes[1]);
        });
    }

    [Fact]
    public async Task BuildActionContext_RedactsDenylistedDescendant_WhenParentPathIsRequested()
    {
        await _ui.RunOnUIAsync(() =>
        {
            var ctx = BuildRenderContext(
                JsonNode.Parse("""{ "profile": { "name": "alice", "password": "p@ss" } }""")!.AsObject());
            var source = BuildSourceComponent("""{ "dataBinding": ["/profile"] }""");
            var action = JsonNode.Parse("""
            {
                "context": [
                    { "key": "profile", "value": { "path": "/profile" } }
                ]
            }
            """);

            var result = ctx.BuildActionContext(source, action)!;

            var profile = Assert.IsType<JsonObject>(result["profile"]);
            Assert.Equal("alice", (string?)profile["name"]);
            Assert.Equal("[REDACTED]", (string?)profile["password"]);
        });
    }

    [Fact]
    public async Task ApplyDataModelUpdate_OverDeepValueArray_IsDroppedByDepthGuard()
    {
        await _ui.RunOnUIAsync(() =>
        {
            var harness = BuildHarness(_ui);

            // Shallow array survives; a >32-deep nest is rejected by the same
            // guard that bounds valueMap, so the path stays unset.
            harness.DataModel.ApplyDataModelUpdate("s", null, new[] { DeepArrayEntry("shallow", 4) });
            harness.DataModel.ApplyDataModelUpdate("s", null, new[] { DeepArrayEntry("tooDeep", 40) });

            Assert.IsType<JsonArray>(harness.DataModel.Read("s", "/shallow"));
            Assert.Null(harness.DataModel.Read("s", "/tooDeep"));
        });
    }

    [Fact]
    public async Task ApplyDataModelUpdate_OverDeepValueMap_StillDropped_NoRegression()
    {
        await _ui.RunOnUIAsync(() =>
        {
            var harness = BuildHarness(_ui);
            harness.DataModel.ApplyDataModelUpdate("s", null, new[] { DeepMapEntry("deepMap", 40) });
            Assert.Null(harness.DataModel.Read("s", "/deepMap"));
        });
    }

    /// <summary>Build an entry whose value is <paramref name="depth"/> nested valueArrays.</summary>
    private static DataModelEntry DeepArrayEntry(string key, int depth)
    {
        DataModelEntry inner = new() { Key = string.Empty, ValueString = "x" };
        for (int i = 0; i < depth; i++)
            inner = new DataModelEntry { Key = string.Empty, ValueArray = new[] { inner } };
        return new DataModelEntry { Key = key, ValueArray = new[] { inner } };
    }

    /// <summary>Build an entry whose value is <paramref name="depth"/> nested valueMaps.</summary>
    private static DataModelEntry DeepMapEntry(string key, int depth)
    {
        DataModelEntry inner = new() { Key = "leaf", ValueString = "x" };
        for (int i = 0; i < depth; i++)
            inner = new DataModelEntry { Key = "n", ValueMap = new[] { inner } };
        return new DataModelEntry { Key = key, ValueMap = new[] { inner } };
    }

    private RenderContext BuildRenderContext(JsonObject seed, string? secretPath = null)
    {
        var harness = BuildHarness(_ui);
        var observable = harness.DataModel.GetOrCreate("s", seed);
        var secretPaths = new HashSet<string>(StringComparer.Ordinal);
        var ctx = new RenderContext
        {
            SurfaceId = "s",
            DataModel = observable,
            Actions = harness.Actions,
            Theme = A2UITheme.Empty,
            BuildChild = _ => null,
            Subscriptions = new Dictionary<string, IDisposable>(),
            SecretPaths = secretPaths,
        };

        ctx.MarkSecretPath(secretPath);
        return ctx;
    }

    private static A2UIComponentDef BuildSourceComponent(string propertiesJson) =>
        new()
        {
            Id = "button",
            ComponentName = "Button",
            Properties = JsonNode.Parse(propertiesJson)!.AsObject(),
        };
}
