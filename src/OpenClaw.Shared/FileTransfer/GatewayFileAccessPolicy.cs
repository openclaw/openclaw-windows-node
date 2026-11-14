// Plans this node's entry in the Gateway file-transfer plugin policy
// (plugins.entries.file-transfer.config.nodes.<nodeId>) and any file commands missing from the Gateway's
// node command allowlist (gateway.nodes.commands.allow). The entry allows every path, so the Gateway
// forwards file requests and the Node Sandbox (MXC) on this PC decides what is actually accessible.
// Usage:
//   var displayName = GatewayFileAccessPolicy.NeedsDisplayName(config, nodeId) ? LookUpDisplayName() : null;
//   var plan = GatewayFileAccessPolicy.Plan(config, nodeId, displayName);
//   if (plan.Kind == GatewayFileAccessPlanKind.Patch)
//       // send config.patch with raw = plan.Patch and baseHash
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared.FileTransfer;

public enum GatewayFileAccessPlanKind { NoChange, Patch, MigrationRequired, UnsupportedPolicyVersion, InvalidNode }

public sealed record GatewayFileAccessPlan(GatewayFileAccessPlanKind Kind, JsonObject? Patch = null);

/// <summary>
/// Pure merge planning for the Gateway file-transfer policy. Companion adds <c>**</c> to this node's
/// <c>allowReadPaths</c> and <c>allowWritePaths</c> and any missing, non-denied file commands to the node
/// command allowlist. It removes nothing, so administrator rules, other nodes, standing grants and this
/// node's <c>ask</c>, <c>maxBytes</c> and <c>followSymlinks</c> are preserved.
/// </summary>
public static class GatewayFileAccessPolicy
{
    public const string PluginId = "file-transfer";
    public const int PolicyVersion = 2;
    // Matches every absolute Windows path the Gateway compares, including UNC and metacharacter names.
    public const string AllowAll = "**";
    private const string AllowRead = "allowReadPaths";
    private const string AllowWrite = "allowWritePaths";
    private const string Deny = "denyPaths";
    private const string LegacyAllowCommands = "allowCommands";
    private static readonly Regex NodeIdPattern = new("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// True when this node has no entry yet and another non-wildcard entry might be its display-name
    /// policy, so the caller must supply the Gateway's display name before planning.
    /// </summary>
    public static bool NeedsDisplayName(JsonElement config, string nodeId) =>
        Nodes(config) is { } nodes && Entry(nodes, nodeId) is null &&
        nodes.EnumerateObject().Any(e => e.Name != "*" && e.Value.ValueKind == JsonValueKind.Object);

    /// <summary>
    /// Plans a merge patch against a fresh <c>config.get</c> snapshot. The patch only adds <c>**</c> to this
    /// node's allow lists and never removes an entry: the Gateway refuses config writes that shrink a file
    /// by half, and existing rules are harmless beside <c>**</c>. The Gateway uses only the first matching
    /// entry (nodeId, display name, then <c>*</c>). A new nodeId entry would shadow the effective one, so
    /// it starts with a one-time copy of that entry's controls and denies; later edits to the display-name
    /// or <c>*</c> entry no longer apply to this node.
    /// </summary>
    public static GatewayFileAccessPlan Plan(JsonElement config, string nodeId, string? nodeDisplayName = null)
    {
        if (string.IsNullOrEmpty(nodeId) || !NodeIdPattern.IsMatch(nodeId))
            return new(GatewayFileAccessPlanKind.InvalidNode);
        var pluginConfig = Child(config, "plugins", "entries", PluginId, "config");
        JsonElement? policyVersion = pluginConfig is { } pc && pc.TryGetProperty("policyVersion", out var v) ? v : null;
        var versioned = policyVersion is { ValueKind: JsonValueKind.Number } number &&
            number.TryGetInt32(out var version) && version == PolicyVersion;
        // A different version belongs to a newer Gateway schema; rewriting it to ours could disable every node.
        if (policyVersion is not null && !versioned)
            return new(GatewayFileAccessPlanKind.UnsupportedPolicyVersion);
        var nodes = Nodes(config);
        // Unversioned positive rules stay inactive until reviewed; writing ours would not activate them.
        if (!versioned && nodes is { } all && all.EnumerateObject().Any(e => HasEntries(e.Value, AllowRead) || HasEntries(e.Value, AllowWrite)))
            return new(GatewayFileAccessPlanKind.MigrationRequired);

        var existing = Entry(nodes, nodeId);
        var nodePatch = new JsonObject();
        foreach (var name in new[] { AllowRead, AllowWrite })
        {
            var current = existing is { } e && e.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().ToList() : [];
            if (current.Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == AllowAll))
                continue;
            // Arrays merge by replacement, so the patch restates every existing entry before ours.
            nodePatch[name] = new JsonArray(current.Select(item => JsonNode.Parse(item.GetRawText()))
                .Append(JsonValue.Create(AllowAll)).ToArray());
        }
        // Once this node allows all, later admin edits to its entry or the command allowlist are left alone.
        if (nodePatch.Count == 0)
            return new(GatewayFileAccessPlanKind.NoChange);

        if (existing is null && (Entry(nodes, nodeDisplayName) ?? Entry(nodes, "*")) is { } inherited)
        {
            foreach (var control in new[] { "ask", "maxBytes", "followSymlinks", Deny })
            {
                if (inherited.TryGetProperty(control, out var value))
                    nodePatch[control] = JsonNode.Parse(value.GetRawText());
            }
        }
        var configPatch = new JsonObject { ["nodes"] = new JsonObject { [nodeId] = nodePatch } };
        if (!versioned)
            configPatch["policyVersion"] = PolicyVersion;
        var patch = new JsonObject
        {
            ["plugins"] = new JsonObject { ["entries"] = new JsonObject { [PluginId] = new JsonObject { ["config"] = configPatch } } },
        };
        if (PlanCommandAllowlist(config) is { } nodesPatch)
            patch["gateway"] = new JsonObject { ["nodes"] = nodesPatch };
        return new(GatewayFileAccessPlanKind.Patch, patch);
    }

    /// <summary>
    /// Gateways set up before the file commands shipped lack them in their node command allowlist, which
    /// setup writes only once. The allowlist extends platform defaults and <c>deny</c> always wins, so
    /// appending is safe; explicitly denied commands are left out. Returns null when nothing is missing.
    /// </summary>
    private static JsonObject? PlanCommandAllowlist(JsonElement config)
    {
        var gatewayNodes = Child(config, "gateway", "nodes");
        var commands = gatewayNodes is { } gn ? Child(gn, "commands") : null;
        JsonElement? modern = commands is { } c && c.TryGetProperty("allow", out var allow) && allow.ValueKind == JsonValueKind.Array ? allow : null;
        JsonElement? legacy = gatewayNodes is { } n && n.TryGetProperty(LegacyAllowCommands, out var old) && old.ValueKind == JsonValueKind.Array ? old : null;
        // Gateways older than the commands schema use the legacy key; never define both.
        var useLegacy = modern is null && legacy is not null;
        var current = (useLegacy ? legacy : modern)?.EnumerateArray().ToList() ?? [];
        var denied = commands is { } d && d.TryGetProperty("deny", out var deny) && deny.ValueKind == JsonValueKind.Array
            ? deny.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).ToHashSet(StringComparer.Ordinal)
            : [];
        var present = current.Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).ToHashSet(StringComparer.Ordinal);
        var missing = FileCommandParser.Commands.Where(command => !present.Contains(command) && !denied.Contains(command)).ToArray();
        if (missing.Length == 0)
            return null;
        // Arrays merge by replacement, so the patch restates every existing entry before ours.
        var array = new JsonArray(current.Select(item => JsonNode.Parse(item.GetRawText()))
            .Concat(missing.Select(command => (JsonNode?)JsonValue.Create(command))).ToArray());
        return useLegacy
            ? new JsonObject { [LegacyAllowCommands] = array }
            : new JsonObject { ["commands"] = new JsonObject { ["allow"] = array } };
    }


    private static JsonElement? Nodes(JsonElement config) =>
        Child(config, "plugins", "entries", PluginId, "config") is { } pluginConfig &&
        pluginConfig.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Object ? nodes : null;

    private static JsonElement? Entry(JsonElement? nodes, string? key) =>
        nodes is { } map && !string.IsNullOrEmpty(key) && map.TryGetProperty(key, out var entry) &&
        entry.ValueKind == JsonValueKind.Object ? entry : null;

    private static JsonElement? Child(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
                return null;
        }
        return element.ValueKind == JsonValueKind.Object ? element : null;
    }

    private static bool HasEntries(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0;
}
