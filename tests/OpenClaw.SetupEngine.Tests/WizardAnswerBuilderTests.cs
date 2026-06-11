using System.Text.Json;

namespace OpenClaw.SetupEngine.Tests;

public class WizardAnswerBuilderTests
{
    [Fact]
    public void ReadOptions_UsesCanonicalKeysForRawPrimitiveAndObjectValues()
    {
        var step = ParseElement("""
            {
              "options": [
                { "label": "Bool", "value": true },
                { "label": "Number", "value": 42 },
                { "label": "Object", "value": { "id": 1, "name": "matrix" } },
                "plain"
              ]
            }
            """);

        var options = WizardAnswerBuilder.ReadOptions(step);

        Assert.Collection(
            options,
            option => Assert.Equal("true", option.Value),
            option => Assert.Equal("42", option.Value),
            option => Assert.Equal("""{"id":1,"name":"matrix"}""", option.Value),
            option => Assert.Equal("plain", option.Value));
    }

    [Fact]
    public void BuildWireValue_SelectPreservesNumericOptionValue()
    {
        var options = ReadOptions("""{"options":[{"label":"Number","value":42}]}""");

        var wireValue = WizardAnswerBuilder.BuildWireValue("select", "42", options);

        Assert.Equal("""{"value":42}""", SerializeValue(wireValue));
    }

    [Fact]
    public void BuildWireValue_SelectPreservesBooleanOptionValue()
    {
        var options = ReadOptions("""{"options":[{"label":"Enabled","value":true}]}""");

        var wireValue = WizardAnswerBuilder.BuildWireValue("select", "true", options);

        Assert.Equal("""{"value":true}""", SerializeValue(wireValue));
    }

    [Fact]
    public void BuildWireValue_SelectPreservesObjectOptionValue()
    {
        var options = ReadOptions("""{"options":[{"label":"Matrix","value":{"id":1,"name":"matrix"}}]}""");

        var wireValue = WizardAnswerBuilder.BuildWireValue("select", """{"id":1,"name":"matrix"}""", options);

        Assert.Equal("""{"value":{"id":1,"name":"matrix"}}""", SerializeValue(wireValue));
    }

    [Fact]
    public void BuildWireValue_MultiselectPreservesRawOptionValues()
    {
        var options = ReadOptions("""
            {
              "options": [
                { "label": "Enabled", "value": true },
                { "label": "Number", "value": 42 },
                { "label": "Matrix", "value": { "id": 1 } }
              ]
            }
            """);

        var wireValue = WizardAnswerBuilder.BuildWireValue("multiselect", """[true,42,{"id":1}]""", options);

        Assert.Equal("""{"value":[true,42,{"id":1}]}""", SerializeValue(wireValue));
    }

    [Fact]
    public void BuildWireValue_ConfirmFalseStaysBooleanFalse()
    {
        var wireValue = WizardAnswerBuilder.BuildWireValue("confirm", "false", []);

        Assert.Equal("""{"value":false}""", SerializeValue(wireValue));
    }

    [Fact]
    public void BuildWireValue_NoteAckKeepsExistingStringShape()
    {
        var wireValue = WizardAnswerBuilder.BuildWireValue("note", "true", []);

        Assert.Equal("""{"value":"true"}""", SerializeValue(wireValue));
    }

    [Fact]
    public void BuildWireValue_MultiselectSkipKeepsSentinelStringArray()
    {
        var wireValue = WizardAnswerBuilder.BuildWireValue("multiselect", "__skip__", []);

        Assert.Equal("""{"value":["__skip__"]}""", SerializeValue(wireValue));
    }

    private static IReadOnlyList<WizardOptionValue> ReadOptions(string stepJson) =>
        WizardAnswerBuilder.ReadOptions(ParseElement(stepJson));

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string SerializeValue(object value) =>
        JsonSerializer.Serialize(new { value });
}
