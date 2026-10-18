using System.Text.Json;

namespace OpenClaw.Shared;

public sealed record ClawHubSkillInstallResult(
    string Slug,
    string Version,
    string? Warning);

public static class ClawHubSkillManagementParser
{
    public static ClawHubSkillInstallResult ParseInstall(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("skills.install returned a non-object payload.");
        if (!payload.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("skills.install did not return a successful payload.");

        return new ClawHubSkillInstallResult(
            RequireString(payload, "slug"),
            RequireString(payload, "version"),
            GetOptionalString(payload, "warning"));
    }

    private static string RequireString(JsonElement payload, string name)
    {
        var value = GetOptionalString(payload, name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"skills.install is missing string '{name}'.");
        return value;
    }

    private static string? GetOptionalString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
