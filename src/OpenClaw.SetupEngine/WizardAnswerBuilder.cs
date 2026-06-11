using System.Text.Json;

namespace OpenClaw.SetupEngine;

public sealed record WizardOptionValue(string Value, string Label, string Hint, JsonElement RawValue);

public static class WizardAnswerBuilder
{
    public static IReadOnlyList<WizardOptionValue> ReadOptions(JsonElement step)
    {
        if (step.ValueKind != JsonValueKind.Object
            || !step.TryGetProperty("options", out var options)
            || options.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<WizardOptionValue>();
        foreach (var option in options.EnumerateArray())
        {
            result.Add(ReadOption(option));
        }

        return result;
    }

    public static string ValueKey(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null => "null",
        JsonValueKind.Undefined => string.Empty,
        _ => JsonSerializer.Serialize(value)
    };

    public static string[] ValueKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Select(ValueKey).ToArray();

        var key = ValueKey(value);
        return string.IsNullOrEmpty(key) ? [] : [key];
    }

    public static object BuildWireValue(string stepType, string answer, IReadOnlyList<WizardOptionValue> options)
    {
        if (stepType == "confirm" && bool.TryParse(answer, out var booleanAnswer))
            return booleanAnswer;

        if (stepType == "select" && TryFindOption(options, answer, out var option))
            return option.RawValue;

        if (stepType == "multiselect")
        {
            if (string.Equals(answer, "__skip__", StringComparison.Ordinal))
                return new[] { "__skip__" };

            if (TryResolveOptions(options, answer, out var selectedOptions))
                return selectedOptions.Select(selected => selected.RawValue).ToArray();

            return SplitMultiSelect(answer);
        }

        return answer;
    }

    public static bool TryFindOption(IReadOnlyList<WizardOptionValue> options, string answer, out WizardOptionValue option)
    {
        var exact = options.FirstOrDefault(candidate => string.Equals(candidate.Value, answer, StringComparison.Ordinal));
        if (exact is not null)
        {
            option = exact;
            return true;
        }

        if (!TryParseJson(answer, out var parsed))
        {
            option = null!;
            return false;
        }

        using (parsed)
        {
            return TryFindOption(options, parsed.RootElement, out option);
        }
    }

    public static bool TryResolveOptions(IReadOnlyList<WizardOptionValue> options, string answer, out WizardOptionValue[] selectedOptions)
    {
        if (TryParseJson(answer, out var parsed))
        {
            using (parsed)
            {
                if (parsed.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var selected = new List<WizardOptionValue>();
                    foreach (var item in parsed.RootElement.EnumerateArray())
                    {
                        if (!TryFindOption(options, item, out var option))
                        {
                            selectedOptions = [];
                            return false;
                        }

                        selected.Add(option);
                    }

                    selectedOptions = selected.ToArray();
                    return selectedOptions.Length > 0;
                }
            }
        }

        var values = SplitMultiSelect(answer);
        if (values.Length == 0)
        {
            selectedOptions = [];
            return false;
        }

        var resolved = new List<WizardOptionValue>(values.Length);
        foreach (var value in values)
        {
            if (!TryFindOption(options, value, out var option))
            {
                selectedOptions = [];
                return false;
            }

            resolved.Add(option);
        }

        selectedOptions = resolved.ToArray();
        return true;
    }

    public static string[] SplitMultiSelect(string stepInput) =>
        stepInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static WizardOptionValue ReadOption(JsonElement option)
    {
        if (option.ValueKind != JsonValueKind.Object)
        {
            var raw = option.Clone();
            var value = ValueKey(raw);
            return new(value, value, string.Empty, raw);
        }

        var label = option.TryGetProperty("label", out var labelProperty)
            ? DisplayText(labelProperty)
            : string.Empty;
        var hint = option.TryGetProperty("hint", out var hintProperty)
            ? DisplayText(hintProperty)
            : string.Empty;

        if (option.TryGetProperty("value", out var valueProperty))
        {
            var raw = valueProperty.Clone();
            var value = ValueKey(raw);
            return new(value, string.IsNullOrWhiteSpace(label) ? value : label, hint, raw);
        }

        var fallback = string.IsNullOrWhiteSpace(label) ? DisplayText(option) : label;
        return new(fallback, fallback, hint, JsonSerializer.SerializeToElement(fallback));
    }

    private static string DisplayText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => JsonSerializer.Serialize(element)
    };

    private static bool TryFindOption(IReadOnlyList<WizardOptionValue> options, JsonElement answer, out WizardOptionValue option)
    {
        var key = ValueKey(answer);
        var exact = options.FirstOrDefault(candidate => string.Equals(candidate.Value, key, StringComparison.Ordinal));
        if (exact is not null)
        {
            option = exact;
            return true;
        }

        var structural = options.FirstOrDefault(candidate => JsonElement.DeepEquals(candidate.RawValue, answer));
        if (structural is not null)
        {
            option = structural;
            return true;
        }

        option = null!;
        return false;
    }

    private static bool TryParseJson(string value, out JsonDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            document = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
