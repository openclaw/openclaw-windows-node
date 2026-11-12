using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.Shared.Widgets;

public sealed record WidgetCopy(string Title, string Empty, string Select, string Show, string Stale, string Source);
public sealed record WidgetCardPayload(string Template, string Data);

public static class WidgetCard
{
    public const string DefinitionId = "OpenClawTracker";
    public const string SelectVerb = "select-tracker";
    public const string StatePrefix = "v1:";
    public const int MaxExpandedTemplateBytes = 1_048_576;

    public static string? ResolveTracker(string? state, IReadOnlyList<WidgetSnapshot> snapshots) =>
        WidgetInstanceState.Parse(state, snapshots).TrackerId;

    public static WidgetCardPayload Render(
        WidgetSnapshot? snapshot,
        IReadOnlyList<WidgetSnapshot> snapshots,
        WidgetCopy copy,
        DateTimeOffset now,
        JsonObject? state = null)
    {
        snapshot?.Presentation?.Validate();
        var stale = snapshot is not null &&
            now - snapshot.ObservedAt >= TimeSpan.FromMinutes(snapshot.StaleAfterMinutes);
        var data = new Dictionary<string, object?>
        {
            ["title"] = snapshot?.Title ?? copy.Title,
            ["value"] = snapshot?.Value ?? "",
            ["detail"] = snapshot?.Detail ?? copy.Empty,
            ["observedAt"] = snapshot?.ObservedAt,
            ["freshness"] = snapshot is null ? "" :
                (stale ? copy.Stale + " " : "") + snapshot.ObservedAt.ToString("u"),
            ["sourceUrl"] = snapshot?.SourceUrl,
            ["content"] = snapshot?.Presentation?.Data,
            ["state"] = state ?? WidgetInstanceState.Initial(snapshot?.Presentation),
            ["trackers"] = snapshots.Select(s => new { id = s.Id, title = s.Title }).ToArray()
        };
        var card = snapshot?.Presentation?.CreateCard() ?? JsonNode.Parse("""
            {"type":"AdaptiveCard","version":"1.6","body":[
              {"type":"TextBlock","text":"${title}","weight":"Bolder","wrap":true,"$when":"${$host.widgetSize != 'small'}"},
              {"type":"TextBlock","text":"${value}","size":"${if($host.widgetSize == 'small', 'Medium', 'ExtraLarge')}","wrap":true},
              {"type":"TextBlock","text":"${detail}","wrap":true,"$when":"${$host.widgetSize != 'small'}"}
            ]}
            """)!.AsObject();
        GuardExecuteActions(card);
        WidgetArtwork.Expand(card, snapshot?.Presentation?.Data, WidgetArtwork.Read(snapshot?.Presentation?.Assets));
        var body = card["body"]!.AsArray();
        var observation = JsonSerializer.SerializeToNode(new
        {
            type = "TextBlock", id = "openclaw-observation",
            text = "${title}: ${value} | ${freshness}", size = "Small", spacing = "Small",
            isSubtle = true, wrap = true
        });
        var actions = card["actions"] as JsonArray ?? new JsonArray();
        card.Remove("actions");
        if (snapshots.Count > 1 || (snapshots.Count == 1 && snapshot is null))
        {
            body.Add(JsonSerializer.SerializeToNode(new
            {
                type = "Input.ChoiceSet", id = "openclaw-tracker", label = copy.Select, value = snapshot?.Id,
                choices = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["$data"] = "${trackers}", ["title"] = "${title}", ["value"] = "${id}"
                    }
                }
            }));
            body.Last()!["$when"] = "${$host.isUserContextAuthenticated}";
            var select = JsonSerializer.SerializeToNode(new
            {
                type = "Action.Execute", title = copy.Show, verb = SelectVerb, associatedInputs = "auto"
            })!.AsObject();
            select["$when"] = "${$host.isUserContextAuthenticated}";
            actions.Add(select);
        }
        if (actions.Count > 0)
            body.Add(new JsonObject { ["type"] = "ActionSet", ["actions"] = actions });
        if (snapshot is not null)
            body.Add(new JsonObject
            {
                ["type"] = "Container", ["id"] = "openclaw-source", ["spacing"] = "Small",
                ["height"] = "stretch", ["verticalContentAlignment"] = "Bottom",
                ["items"] = new JsonArray(observation, JsonSerializer.SerializeToNode(new
                {
                    type = "RichTextBlock", horizontalAlignment = "Center", spacing = "Small",
                    inlines = new[]
                    {
                        new
                        {
                            type = "TextRun", text = copy.Source, size = "Small",
                            selectAction = new { type = "Action.OpenUrl", title = copy.Source, url = "${sourceUrl}" }
                        }
                    }
                }))
            });
        else
            body.Add(observation);
        var template = card.ToJsonString();
        if (Encoding.UTF8.GetByteCount(template) > MaxExpandedTemplateBytes)
            throw new ArgumentException("Expanded widget template exceeds 1048576 bytes. Use fewer repeated image resources.");
        return new(template, JsonSerializer.Serialize(data));
    }

    // The host rejects Execute for unauthenticated contexts. Apply the guard to nested/select/refresh actions too.
    private static void GuardExecuteActions(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array) GuardExecuteActions(child);
        }
        else if (node is JsonObject element)
        {
            if (element["type"]?.GetValue<string>() == "Action.Execute")
            {
                var condition = element["$when"]?.GetValue<string>();
                element["$when"] = condition is null ? "${$host.isUserContextAuthenticated}" :
                    "${$host.isUserContextAuthenticated && (" + condition[2..^1] + ")}";
            }
            foreach (var property in element)
                if (property.Key != "data") GuardExecuteActions(property.Value);
        }
    }
}
