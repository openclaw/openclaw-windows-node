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

    [Fact]
    public void BuildWireValue_SelectFallsBackToString_WhenNoOptionMatch()
    {
        var options = ReadOptions("""{"options":[{"label":"Alpha","value":"alpha"}]}""");

        // "beta" is not in the options list; should pass through as a plain string
        var wireValue = WizardAnswerBuilder.BuildWireValue("select", "beta", options);

        Assert.Equal("""{"value":"beta"}""", SerializeValue(wireValue));
    }

    [Fact]
    public void BuildWireValue_MultiselectSplitsCommaString_WhenOptionsEmpty()
    {
        var wireValue = WizardAnswerBuilder.BuildWireValue("multiselect", "opt-a,opt-b,opt-c", []);

        // No options list to resolve against, so falls back to SplitMultiSelect
        Assert.Equal("""{"value":["opt-a","opt-b","opt-c"]}""", SerializeValue(wireValue));
    }

    [Fact]
    public void BuildWireValue_MultiselectSplitsCommaString_WhenOptionNotInList()
    {
        var options = ReadOptions("""{"options":[{"label":"Alpha","value":"alpha"}]}""");

        // "gamma" is not in the options list, so TryResolveOptions returns false; falls back to split
        var wireValue = WizardAnswerBuilder.BuildWireValue("multiselect", "alpha,gamma", options);

        Assert.Equal("""{"value":["alpha","gamma"]}""", SerializeValue(wireValue));
    }

    [Fact]
    public void ReadOptions_ReturnsEmpty_ForNonObjectInput()
    {
        var arrayElement = ParseElement("""["a","b"]""");

        var options = WizardAnswerBuilder.ReadOptions(arrayElement);

        Assert.Empty(options);
    }

    [Fact]
    public void ReadOptions_ReturnsEmpty_WhenOptionsPropertyMissing()
    {
        var step = ParseElement("""{"stepType":"select","id":"step-1"}""");

        var options = WizardAnswerBuilder.ReadOptions(step);

        Assert.Empty(options);
    }

    [Fact]
    public void ReadOptions_ReturnsEmpty_WhenOptionsIsNotArray()
    {
        var step = ParseElement("""{"options":"not-an-array"}""");

        var options = WizardAnswerBuilder.ReadOptions(step);

        Assert.Empty(options);
    }

    [Fact]
    public void ValueKeys_ReturnsEmptyArray_ForEmptyStringElement()
    {
        // JSON empty string "" → ValueKey returns "" → ValueKeys returns []
        var element = ParseElement("\"\"");

        var keys = WizardAnswerBuilder.ValueKeys(element);

        Assert.Empty(keys);
    }

    [Fact]
    public void ValueKeys_ReturnsSingleElementArray_ForNonArrayValue()
    {
        var element = ParseElement("42");

        var keys = WizardAnswerBuilder.ValueKeys(element);

        Assert.Equal(["42"], keys);
    }

    [Fact]
    public void ValueKeys_ReturnsMultipleElements_ForJsonArray()
    {
        var element = ParseElement("""["a",42,true]""");

        var keys = WizardAnswerBuilder.ValueKeys(element);

        Assert.Equal(["a", "42", "true"], keys);
    }

    [Fact]
    public void ReadOptions_OptionWithoutValueUsesLabelAsKey()
    {
        var step = ParseElement("""
            {
              "options": [
                { "label": "My Label", "hint": "some hint" }
              ]
            }
            """);

        var options = WizardAnswerBuilder.ReadOptions(step);

        var option = Assert.Single(options);
        Assert.Equal("My Label", option.Value);
        Assert.Equal("My Label", option.Label);
        Assert.Equal("some hint", option.Hint);
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
