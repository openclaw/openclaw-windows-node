using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Shared;

namespace OpenClawTray.Presentation;

internal static class HubSearchCatalog
{
    internal static readonly (string Tag, string Path)[] Counterparts =
    [
        ("connection", "/settings/connection"), ("sessions", "/sessions"), ("skills", "/skills"),
        ("systems", "/settings/devices"),
        ("cron", "/automations"), ("config", "/settings/general"), ("usage", "/usage")
    ];

    internal static readonly (string Id, string Path, bool Admin)[] DashboardRoutes =
    [
        ("Approvals", "/settings/approvals", false), ("Secrets", "/settings/secrets", true),
        ("Models", "/settings/model-providers", false), ("Automation", "/settings/automation", true),
        ("Mcp", "/settings/mcp", true), ("Memory", "/settings/memory", false),
        ("Security", "/settings/security", true), ("Logs", "/logs", false),
        ("Systems", "/systems", false), ("Dashboards", "/dashboards", false),
        ("Plugins", "/plugins", false), ("Apps", "/apps", false), ("Meetings", "/meetings", false),
        ("Activity", "/activity", false), ("Portals", "/portals", false), ("Worktrees", "/worktrees", false)
    ];

    internal static bool SupportsDashboard(string? version) =>
        OpenClawReleaseVersion.TryParseStable(version, out var release) &&
        release.Year == 2026 && release.Month == 10 && release.Patch == 9;

    internal static bool SupportsSkillsDashboard(string? version) =>
        SupportsDashboard(version) ||
        OpenClawReleaseVersion.TryParseStable(version, out var release) &&
        release.Year == 2026 && release.Month == 11 && release.Patch == 1;

    // Released session-url-contract exactKey grammar. Never derive a shortened session identity.
    internal static string? SessionPath(string key, bool? isMain = null)
    {
        var parts = key.Split(':');
        if (parts.Length < 3 || parts[0] != "agent" || parts.Any(string.IsNullOrEmpty) ||
            !Regex.IsMatch(parts[1], "^[a-z0-9][a-z0-9_-]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            return null;
        var root = $"/chat/{Escape(parts[1])}";
        var rest = parts.Skip(2).ToArray();
        if (isMain == true || isMain is null && rest.Length == 1 && rest[0].Equals("main", StringComparison.OrdinalIgnoreCase))
            return root;
        if (rest.Length == 1 && (rest[0].ToLowerInvariant() is "main" or "global" or "boot" or "sessions" ||
            Regex.IsMatch(rest[0], "^(?:.*-)?[0-9a-f]{8,32}$", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)))
            return $"{root}/~key/{Escape(rest[0])}";
        return $"{root}/{string.Join("/", rest.Select(Escape))}";

        static string Escape(string value)
        {
            if (value == ".") return "~dot";
            if (value == "..") return "~dotdot";
            var encoded = Uri.EscapeDataString(value).Replace(".", "%2E");
            return encoded.StartsWith('~') ? "~" + encoded : encoded;
        }
    }

    internal static string? Counterpart(string tag, bool supported)
    {
        if (!supported) return null;
        if (tag.StartsWith("agent:", StringComparison.Ordinal))
        {
            var parts = tag.Split(':');
            return parts.Length > 2 && parts[2] == "cron" ? "/automations" :
                parts.Length == 2 ? $"/settings/agents/{Uri.EscapeDataString(parts[1]).Replace(".", "%2E")}" : null;
        }
        return Counterparts.FirstOrDefault(pair => pair.Tag == tag).Path;
    }

    internal static string? ContextualDashboardPath(
        WorkspaceDestination destination,
        string? currentSessionKey,
        bool? isMain,
        string? gatewayVersion)
    {
        var supported = SupportsDashboard(gatewayVersion);
        if (destination.Page == WorkspacePageId.Settings)
        {
            if (destination.CompanionTag is not { } tag)
                return null;
            if (tag.StartsWith("agent:", StringComparison.Ordinal) &&
                tag.IndexOf(':', "agent:".Length) < 0)
                return null;
            return Counterpart(tag, supported);
        }
        if (!supported || destination.Page != WorkspacePageId.Home)
            return null;

        var sessionKey = destination.SessionKey ?? currentSessionKey;
        return string.IsNullOrWhiteSpace(sessionKey) ? null : SessionPath(sessionKey, isMain);
    }

    internal static IEnumerable<HubSearchItem> Navigation(
        IEnumerable<HubCommand> commands, bool connected, bool dashboardSupported, Func<string, string> localize)
    {
        var destinations = commands.GroupBy(command =>
            command.Action is { Kind: HubCommandActionKind.Navigate, Value: { } tag }
                ? HubCommandAction.Navigate(HubPageRegistry.NormalizeTag(tag, HubPageRegistry.ParseAgentId(tag)))
                : command.Action);
        foreach (var destination in destinations)
        {
            var command = destination.First();
            if (destination.Key.Kind == HubCommandActionKind.Navigate && destination.Key.Value is { } tag)
            {
                if (!connected && HubPageRegistry.IsGatewayPageTag(tag)) continue;
                if (tag.StartsWith("agent:", StringComparison.Ordinal) && HubPageRegistry.ParseAgentId(tag).Length == 0) continue;
                var presentation = HubPageRegistry.SearchPresentation(tag);
                if (tag == "channels")
                {
                    yield return new($"dashboard:{tag}", HubSearchGroup.Dashboard, localize(presentation.TitleKey), command.Subtitle,
                        presentation.Icon, new(HubSearchTargetKind.Dashboard, tag), $"{tag} {command.Title}", tag);
                    continue;
                }
                yield return new($"page:{tag}", HubSearchGroup.Settings, localize(presentation.TitleKey), command.Subtitle,
                    presentation.Icon, new(HubSearchTargetKind.Page, tag),
                    string.Join(" ", destination.Select(alias => $"{alias.Action.Value} {alias.Title} {alias.Subtitle}")),
                    Counterpart(tag, dashboardSupported));
            }
            else if (command.Action.Kind == HubCommandActionKind.OpenDashboard && string.IsNullOrEmpty(command.Action.Value))
            {
                var presentation = HubPageRegistry.SearchPresentation("dashboard");
                yield return new("dashboard", HubSearchGroup.Dashboard, localize(presentation.TitleKey), command.Subtitle,
                    presentation.Icon, new(HubSearchTargetKind.Dashboard, ""), command.Title);
            }
        }
    }

    internal static IReadOnlyList<HubSearchItem> ParseCatalog(
        string method, JsonElement payload, string agentId, bool dashboardSupported, bool skillsDashboardSupported = false)
    {
        var (property, group) = method switch
        {
            "cron.list" => ("jobs", HubSearchGroup.Automations),
            "skills.status" => ("skills", HubSearchGroup.Skills),
            "agents.list" => ("agents", HubSearchGroup.Agents),
            "models.list" => ("models", HubSearchGroup.Models),
            _ => throw new ArgumentException("Not a row catalog.", nameof(method))
        };
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(property, out var rows) ||
            rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Invalid search catalog response.");
        var items = new List<HubSearchItem>();
        var skillsSupported = dashboardSupported || skillsDashboardSupported;
        foreach (var row in rows.EnumerateArray())
        {
            var id = Text(row, group == HubSearchGroup.Skills ? "skillKey" : "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var name = Text(row, "displayName") ?? Text(row, "name") ?? id;
            var agent = Text(row, "agentId") ?? agentId;
            var provider = Text(row, "provider");
            var detail = Text(row, "description") ?? provider ?? agent;
            var target = group switch
            {
                HubSearchGroup.Automations => new HubSearchTarget(HubSearchTargetKind.Page, agent.Length == 0 ? "cron" : $"agent:{agent}:cron", id),
                HubSearchGroup.Skills => new(HubSearchTargetKind.Dashboard, skillsSupported ? "/skills" : ""),
                HubSearchGroup.Agents => new(HubSearchTargetKind.Page, $"agent:{id}"),
                _ => new(HubSearchTargetKind.Dashboard, "/settings/model-providers")
            };
            if (group == HubSearchGroup.Models && !dashboardSupported) continue;
            if (group == HubSearchGroup.Agents && row.TryGetProperty("identity", out var identity))
                name = Text(identity, "name") ?? name;
            var dashboard = group switch
            {
                HubSearchGroup.Automations => "/automations",
                HubSearchGroup.Skills => "/skills",
                HubSearchGroup.Agents => $"/settings/agents/{Uri.EscapeDataString(id).Replace(".", "%2E")}",
                _ => "/settings/model-providers"
            };
            items.Add(new(
                JsonSerializer.Serialize(new[] { group.ToString(), group == HubSearchGroup.Skills ? agentId : provider, id }),
                group, name, detail, "\uE8A5", target,
                $"{id} {Text(row, "name")} {Text(row, "source")} {Text(row, "workspace")} {Text(row, "alias")} {provider} {Tags(row)}",
                (group == HubSearchGroup.Skills ? skillsSupported : dashboardSupported) ? dashboard : null));
        }
        return items;
    }

    internal static IReadOnlyList<HubSearchItem> Config(JsonElement payload, bool dashboardSupported, out bool partial)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("schema", out var schema) ||
            schema.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid configuration schema response.");
        payload.TryGetProperty("uiHints", out var hints);
        var items = new List<HubSearchItem>();
        var incomplete = false;
        Visit(schema, "", 0);
        partial = incomplete;
        return items;

        void Visit(JsonElement node, string path, int depth)
        {
            if (items.Count >= 5000 || depth > 32) { incomplete = true; return; }
            if (node.ValueKind != JsonValueKind.Object) return;
            var hint = hints.ValueKind == JsonValueKind.Object && hints.TryGetProperty(path, out var h) ? h : default;
            if (hint.ValueKind == JsonValueKind.Object && hint.TryGetProperty("sensitive", out var sensitive) &&
                sensitive.ValueKind == JsonValueKind.True) return;
            if (path.Length > 0)
                items.Add(new($"config:{path}", HubSearchGroup.Settings,
                    Text(hint, "label") ?? Text(node, "title") ?? path,
                    path, "\uE713", new(HubSearchTargetKind.Page, "config", path),
                    $"{path} {Text(hint, "help")} {Text(node, "description")} {Tags(hint)}",
                    dashboardSupported ? "/settings/general" : null));
            if (node.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
                foreach (var property in properties.EnumerateObject())
                    Visit(property.Value, path.Length == 0 ? property.Name : $"{path}.{property.Name}", depth + 1);
            if (node.TryGetProperty("oneOf", out _) || node.TryGetProperty("anyOf", out _) ||
                node.TryGetProperty("allOf", out _) || node.TryGetProperty("$ref", out _))
                incomplete = true;
        }
    }

    private static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String && property.GetString() is { } text &&
        !string.IsNullOrWhiteSpace(text) ? text : null;

    private static string Tags(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            ? string.Join(" ", tags.EnumerateArray().Where(tag => tag.ValueKind == JsonValueKind.String).Select(tag => tag.GetString()))
            : "";
}
