using System.Linq;
using System.Text.Json.Nodes;
using OpenClawTray.A2UI.Protocol;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Conformance tests for the v0.8 <c>dataModelUpdate.valueArray</c> typed value.
///
/// The protocol (docs/a2ui/protocol.md §2.2 and data-and-actions.md) lists
/// <c>valueArray</c> alongside <c>valueString/Number/Boolean/valueMap</c> as the
/// unambiguous way to seed an array into the data model — e.g. for
/// <c>MultipleChoice.selections</c>. These tests pin that the parser produces a
/// real <see cref="JsonArray"/> (it previously dropped the value entirely).
/// </summary>
public sealed class A2UIDataModelArrayTests
{
    /// <summary>Parse a single dataModelUpdate line and return the entry with the given key.</summary>
    private static DataModelEntry EntryFor(string jsonl, string key)
    {
        var msg = Assert.IsType<DataModelUpdateMessage>(A2UIMessageParser.ParseLine(jsonl));
        return msg.Contents.Single(c => c.Key == key);
    }

    [Fact]
    public void ValueArray_OfStrings_ParsesToJsonArray()
    {
        const string jsonl =
            """{"dataModelUpdate":{"surfaceId":"s","contents":[{"key":"tags","valueArray":[{"valueString":"admin"},{"valueString":"beta"}]}]}}""";

        var node = EntryFor(jsonl, "tags").ToJsonNode();

        var arr = Assert.IsType<JsonArray>(node);
        Assert.Equal(new[] { "admin", "beta" }, arr.Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void ValueArray_OfMixedScalars_PreservesTypes()
    {
        const string jsonl =
            """{"dataModelUpdate":{"surfaceId":"s","contents":[{"key":"mixed","valueArray":[{"valueString":"x"},{"valueNumber":7},{"valueBoolean":true}]}]}}""";

        var arr = Assert.IsType<JsonArray>(EntryFor(jsonl, "mixed").ToJsonNode());

        Assert.Equal(3, arr.Count);
        Assert.Equal("x", arr[0]!.GetValue<string>());
        Assert.Equal(7, arr[1]!.GetValue<double>());
        Assert.True(arr[2]!.GetValue<bool>());
    }

    [Fact]
    public void ValueArray_OfMaps_ProducesArrayOfObjects()
    {
        const string jsonl =
            """{"dataModelUpdate":{"surfaceId":"s","contents":[{"key":"rows","valueArray":[""" +
                """{"valueMap":[{"key":"id","valueNumber":1},{"key":"name","valueString":"Ada"}]},""" +
                """{"valueMap":[{"key":"id","valueNumber":2},{"key":"name","valueString":"Bob"}]}""" +
            """]}]}}""";

        var arr = Assert.IsType<JsonArray>(EntryFor(jsonl, "rows").ToJsonNode());

        Assert.Equal(2, arr.Count);
        var first = Assert.IsType<JsonObject>(arr[0]);
        Assert.Equal(1, first["id"]!.GetValue<double>());
        Assert.Equal("Ada", first["name"]!.GetValue<string>());
        var second = Assert.IsType<JsonObject>(arr[1]);
        Assert.Equal("Bob", second["name"]!.GetValue<string>());
    }

    [Fact]
    public void ValueArray_Nested_ProducesArrayOfArrays()
    {
        const string jsonl =
            """{"dataModelUpdate":{"surfaceId":"s","contents":[{"key":"grid","valueArray":[""" +
                """{"valueArray":[{"valueNumber":1},{"valueNumber":2}]},""" +
                """{"valueArray":[{"valueNumber":3}]}""" +
            """]}]}}""";

        var arr = Assert.IsType<JsonArray>(EntryFor(jsonl, "grid").ToJsonNode());

        var inner0 = Assert.IsType<JsonArray>(arr[0]);
        Assert.Equal(new double[] { 1, 2 }, inner0.Select(n => n!.GetValue<double>()));
        var inner1 = Assert.IsType<JsonArray>(arr[1]);
        Assert.Equal(new double[] { 3 }, inner1.Select(n => n!.GetValue<double>()));
    }

    [Fact]
    public void ValueArray_BarePrimitiveElements_AreTolerated()
    {
        // An agent may emit a JSON array of bare primitives rather than the
        // wrapped { "valueString": ... } shape. We round-trip those too.
        const string jsonl =
            """{"dataModelUpdate":{"surfaceId":"s","contents":[{"key":"vals","valueArray":["a",2,false]}]}}""";

        var arr = Assert.IsType<JsonArray>(EntryFor(jsonl, "vals").ToJsonNode());

        Assert.Equal(3, arr.Count);
        Assert.Equal("a", arr[0]!.GetValue<string>());
        Assert.Equal(2, arr[1]!.GetValue<double>());
        Assert.False(arr[2]!.GetValue<bool>());
    }

    [Fact]
    public void ValueArray_Empty_ProducesEmptyArray()
    {
        const string jsonl =
            """{"dataModelUpdate":{"surfaceId":"s","contents":[{"key":"empty","valueArray":[]}]}}""";

        var arr = Assert.IsType<JsonArray>(EntryFor(jsonl, "empty").ToJsonNode());
        Assert.Empty(arr);
    }

    [Fact]
    public void ValueArray_SelectionsShape_MatchesMultipleChoiceMultiRead()
    {
        // MultipleChoice (multi) reads selections as a JsonArray of strings; an
        // agent seeds the initial selection through valueArray. Pin the exact
        // shape MultipleChoice.ResolveMulti expects.
        const string jsonl =
            """{"dataModelUpdate":{"surfaceId":"s","path":"/form","contents":[{"key":"picked","valueArray":[{"valueString":"red"},{"valueString":"blue"}]}]}}""";

        var msg = Assert.IsType<DataModelUpdateMessage>(A2UIMessageParser.ParseLine(jsonl));
        Assert.Equal("/form", msg.Path);
        var arr = Assert.IsType<JsonArray>(msg.Contents.Single().ToJsonNode());
        Assert.Equal(new[] { "red", "blue" }, arr.Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void ValueArray_NullElement_PreservedAsNullSlot()
    {
        // A JSON null element keeps its position so index-sensitive consumers
        // don't see a shifted array.
        const string jsonl =
            """{"dataModelUpdate":{"surfaceId":"s","contents":[{"key":"vals","valueArray":[{"valueString":"a"},null,{"valueString":"b"}]}]}}""";

        var arr = Assert.IsType<JsonArray>(EntryFor(jsonl, "vals").ToJsonNode());

        Assert.Equal(3, arr.Count);
        Assert.Equal("a", arr[0]!.GetValue<string>());
        Assert.Null(arr[1]);
        Assert.Equal("b", arr[2]!.GetValue<string>());
    }

    [Fact]
    public void ValueArray_ValuelessObjectElement_PreservedAsNullSlot()
    {
        // A wrapped element carrying no recognized value also yields a null slot
        // — consistent with the bare-null case above.
        const string jsonl =
            """{"dataModelUpdate":{"surfaceId":"s","contents":[{"key":"vals","valueArray":[{},{"valueString":"b"}]}]}}""";

        var arr = Assert.IsType<JsonArray>(EntryFor(jsonl, "vals").ToJsonNode());

        Assert.Equal(2, arr.Count);
        Assert.Null(arr[0]);
        Assert.Equal("b", arr[1]!.GetValue<string>());
    }

    [Fact]
    public void ValueMap_StillParses_NoRegression()
    {
        const string jsonl =
            """{"dataModelUpdate":{"surfaceId":"s","contents":[{"key":"user","valueMap":[{"key":"first","valueString":"Ada"},{"key":"last","valueString":"Lovelace"}]}]}}""";

        var obj = Assert.IsType<JsonObject>(EntryFor(jsonl, "user").ToJsonNode());
        Assert.Equal("Ada", obj["first"]!.GetValue<string>());
        Assert.Equal("Lovelace", obj["last"]!.GetValue<string>());
    }

    [Fact]
    public void ScalarEntries_StillParse_NoRegression()
    {
        const string jsonl =
            """{"dataModelUpdate":{"surfaceId":"s","contents":[{"key":"name","valueString":"Ada"},{"key":"age","valueNumber":36},{"key":"active","valueBoolean":true}]}}""";

        var msg = Assert.IsType<DataModelUpdateMessage>(A2UIMessageParser.ParseLine(jsonl));
        Assert.Equal("Ada", msg.Contents.Single(c => c.Key == "name").ToJsonNode()!.GetValue<string>());
        Assert.Equal(36, msg.Contents.Single(c => c.Key == "age").ToJsonNode()!.GetValue<double>());
        Assert.True(msg.Contents.Single(c => c.Key == "active").ToJsonNode()!.GetValue<bool>());
    }
}
