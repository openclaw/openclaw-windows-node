using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace OpenClaw.TestSupport.Ui;

/// <summary>Fail-closed checks for the pinned lvt UIA response contract.</summary>
public static class LvtProtocol
{
    public static JsonElement RequireSuccess(string tool, CallToolResult result)
    {
        if (result.IsError == true)
            throw new InvalidOperationException($"lvt {tool} returned a tool error.");
        if (result.StructuredContent is not { } content)
            throw new InvalidDataException($"lvt {tool} did not return structured content.");
        var payload = content.Clone();
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"lvt {tool} returned a non-object response.");
        if (payload.TryGetProperty("ok", out var ok) && ok.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException($"lvt {tool} did not report success.");
        if (payload.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException($"lvt {tool} returned an error envelope.");
        RejectTruncated(payload);
        return payload;
    }

    public static JsonElement? UniqueElement(JsonElement response, string automationId)
    {
        RejectTruncated(response);
        if (response.GetProperty("tree").GetString() != "uia")
            throw new InvalidDataException("Expected a UIA search, not a visual-tree fallback.");
        var elements = response.GetProperty("elements");
        if (elements.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Expected an elements array.");
        if (elements.GetArrayLength() > 1)
            throw new InvalidDataException($"AutomationId '{automationId}' is ambiguous in this window.");
        if (elements.GetArrayLength() == 0) return null;
        var element = elements[0];
        if (Property(element, "AutomationId") != automationId)
            throw new InvalidDataException("lvt returned an element with a different AutomationId.");
        if (!element.TryGetProperty("ref", out var reference) || reference.ValueKind != JsonValueKind.String
            || reference.GetString() is not { Length: > 4 } text || !text.StartsWith("uia:", StringComparison.Ordinal))
            throw new InvalidDataException("Expected a qualified UIA reference.");
        if (!element.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(key.GetString()))
            throw new InvalidDataException("A durable UIA element key is required for actions after a refresh.");
        return element.Clone();
    }

    public static string Property(JsonElement element, string name)
    {
        if (!element.TryGetProperty("properties", out var properties)
            || !properties.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Required UIA property '{name}' is absent.");
        return value.GetString()!;
    }

    public static void RejectTruncated(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name.Equals("truncated", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("lvt returned an incomplete tree. Absence and uniqueness cannot be asserted.");
                RejectTruncated(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectTruncated(item);
        }
    }
}
