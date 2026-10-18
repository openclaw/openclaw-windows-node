using System.Collections.ObjectModel;
using System.Text.Json;

namespace OpenClaw.Shared;

public sealed record PluginCapabilityGroup(string Name, IReadOnlyList<string> Values);

public sealed record PluginInspectionInfo(
    string PluginId,
    string Name,
    string? Description,
    string? PackageName,
    string ReviewToken,
    IReadOnlyList<PluginCapabilityGroup> DeclaredCapabilities,
    IReadOnlyList<string> Grants,
    string? TrustDisposition,
    IReadOnlyList<string> TrustReasons);

public sealed record PluginInstallResult(
    bool RestartRequired,
    IReadOnlyList<string> Warnings);

public sealed record PluginInstallPolicyFinding(
    string RuleId,
    string Severity,
    string Message,
    string? File,
    int? Line,
    string? Evidence);

public sealed record PluginInstallPolicyWarning(
    string TargetName,
    string Reason,
    IReadOnlyList<PluginInstallPolicyFinding> Findings);

public static class PluginManagementParser
{
    public static bool TryParseInstallPolicyWarning(
        GatewayRequestException exception,
        out PluginInstallPolicyWarning? warning)
    {
        warning = null;
        if (exception.Details is not { ValueKind: JsonValueKind.Object } details ||
            GetOptionalString(details, "installPolicyCode") is not
                "install_policy_warning_acknowledgement_required" ||
            GetOptionalString(details, "targetType") is not "plugin" ||
            GetOptionalString(details, "requestMode") is not "install" ||
            GetOptionalString(details, "targetName") is not { } targetName ||
            GetOptionalString(details, "reason") is not { } reason)
        {
            return false;
        }

        var findings = new List<PluginInstallPolicyFinding>();
        if (details.TryGetProperty("findings", out var findingValues))
        {
            if (findingValues.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var findingValue in findingValues.EnumerateArray())
            {
                if (findingValue.ValueKind != JsonValueKind.Object ||
                    GetOptionalString(findingValue, "ruleId") is not { } ruleId ||
                    GetOptionalString(findingValue, "severity") is not { } severity ||
                    severity is not ("info" or "warn" or "critical") ||
                    GetOptionalString(findingValue, "message") is not { } message)
                {
                    return false;
                }

                int? line = null;
                if (findingValue.TryGetProperty("line", out var lineValue))
                {
                    if (lineValue.ValueKind != JsonValueKind.Number ||
                        !lineValue.TryGetInt32(out var parsedLine) ||
                        parsedLine <= 0)
                    {
                        return false;
                    }
                    line = parsedLine;
                }

                findings.Add(new PluginInstallPolicyFinding(
                    ruleId,
                    severity,
                    message,
                    GetOptionalString(findingValue, "file"),
                    line,
                    GetOptionalString(findingValue, "evidence")));
            }
        }

        warning = new PluginInstallPolicyWarning(
            targetName,
            reason,
            new ReadOnlyCollection<PluginInstallPolicyFinding>(findings));
        return true;
    }

    public static PluginInspectionInfo ParseInspection(JsonElement payload)
    {
        RequireSuccessfulObject(payload, "plugins.inspect");
        var plugin = RequireObject(payload, "plugin", "plugins.inspect");
        var declared = RequireObject(payload, "declared", "plugins.inspect");

        var pluginId = RequireString(plugin, "id", "plugins.inspect plugin");
        var name = RequireString(plugin, "name", "plugins.inspect plugin");
        var reviewToken = RequireString(payload, "reviewToken", "plugins.inspect");
        var description = GetOptionalString(plugin, "description");
        var packageName = payload.TryGetProperty("source", out var source) &&
                          source.ValueKind == JsonValueKind.Object
            ? GetOptionalString(source, "packageName")
            : null;

        var capabilityGroups = new List<PluginCapabilityGroup>();
        foreach (var property in declared.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException(
                    $"plugins.inspect declared capability '{property.Name}' must be an array.");

            var values = ReadStringArray(
                property.Value,
                $"plugins.inspect declared capability '{property.Name}'");
            if (values.Count > 0)
                capabilityGroups.Add(new PluginCapabilityGroup(property.Name, values));
        }

        var grantsElement = RequireObject(payload, "grants", "plugins.inspect");
        var grants = FlattenGrantValues(grantsElement);

        var trustDisposition = default(string);
        IReadOnlyList<string> trustReasons = [];
        if (payload.TryGetProperty("trust", out var trust) && trust.ValueKind == JsonValueKind.Object)
        {
            trustDisposition = GetOptionalString(trust, "disposition");
            if (trust.TryGetProperty("reasons", out var reasons) &&
                reasons.ValueKind == JsonValueKind.Array)
            {
                trustReasons = ReadStringArray(reasons, "plugins.inspect trust reasons");
            }
        }

        return new PluginInspectionInfo(
            pluginId,
            name,
            description,
            packageName,
            reviewToken,
            new ReadOnlyCollection<PluginCapabilityGroup>(capabilityGroups),
            grants,
            trustDisposition,
            trustReasons);
    }

    public static PluginInstallResult ParseInstall(JsonElement payload)
    {
        RequireSuccessfulObject(payload, "plugins.install");
        var restartRequired = payload.TryGetProperty("restartRequired", out var restart) &&
                              restart.ValueKind == JsonValueKind.True;
        IReadOnlyList<string> warnings = [];
        if (payload.TryGetProperty("warnings", out var warningValues))
        {
            if (warningValues.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("plugins.install warnings must be an array.");

            warnings = ReadStringArray(warningValues, "plugins.install warnings");
        }

        return new PluginInstallResult(restartRequired, warnings);
    }

    private static IReadOnlyList<string> FlattenGrantValues(JsonElement grants)
    {
        var values = new List<string>();
        FlattenGrantValues(grants, prefix: null, values);
        return new ReadOnlyCollection<string>(values);
    }

    private static void FlattenGrantValues(JsonElement element, string? prefix, List<string> values)
    {
        foreach (var property in element.EnumerateObject())
        {
            var path = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    FlattenGrantValues(property.Value, path, values);
                    break;
                case JsonValueKind.Array:
                    var items = ReadStringArray(property.Value, $"plugins.inspect grant '{path}'");
                    if (items.Count > 0)
                        values.Add($"{path}: {string.Join(", ", items)}");
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                case JsonValueKind.Number:
                case JsonValueKind.String:
                    values.Add($"{path}: {property.Value.ToString()}");
                    break;
            }
        }
    }

    private static void RequireSuccessfulObject(JsonElement payload, string method)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{method} returned a non-object payload.");
        if (!payload.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new InvalidDataException($"{method} did not return a successful payload.");
    }

    private static JsonElement RequireObject(JsonElement payload, string name, string context)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{context} is missing object '{name}'.");
        return value;
    }

    private static string RequireString(JsonElement payload, string name, string context)
    {
        var value = GetOptionalString(payload, name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"{context} is missing string '{name}'.");
        return value;
    }

    private static string? GetOptionalString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{context} must be an array.");

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new InvalidDataException($"{context} contains an invalid string.");
            values.Add(item.GetString()!);
        }

        return values;
    }
}
