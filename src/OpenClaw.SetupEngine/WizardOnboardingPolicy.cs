using System.Text.Json;

namespace OpenClaw.SetupEngine;

public enum WizardOnboardingAction { Show, Acknowledge, Answer, Finish }

public sealed record WizardOnboardingDecision(WizardOnboardingAction Action, string? Answer = null);

/// <summary>
/// Shared Companion onboarding defaults. Match audited prompt kinds and labels,
/// never random step IDs or the position of an option. Unknown prompts stay visible.
/// </summary>
public static class WizardOnboardingPolicy
{
    private static readonly HashSet<string> InformationalTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Existing config detected", "QuickStart", "Model check", "How channels work",
        "Web search", "Skills status", "Skill status", "Gateway",
    };

    public static WizardOnboardingDecision Evaluate(JsonElement step)
    {
        var show = new WizardOnboardingDecision(WizardOnboardingAction.Show);
        if (step.ValueKind != JsonValueKind.Object ||
            (step.TryGetProperty("sensitive", out var sensitive) && sensitive.ValueKind == JsonValueKind.True))
            return show;

        var type = Text(step, "type");
        var title = Text(step, "title");
        var message = Text(step, "message");
        if (type == "note")
        {
            if (title.Equals("Optional apps", StringComparison.OrdinalIgnoreCase))
                return new(WizardOnboardingAction.Finish);
            return InformationalTitles.Contains(title)
                ? new(WizardOnboardingAction.Acknowledge)
                : show;
        }

        if (type == "confirm" &&
            (message.StartsWith("Configure skills now", StringComparison.OrdinalIgnoreCase) ||
             message.StartsWith("Install Gateway service", StringComparison.OrdinalIgnoreCase)))
            return new(WizardOnboardingAction.Answer, "false");

        var options = WizardAnswerBuilder.ReadOptions(step);
        if (type == "multiselect" &&
            message.StartsWith("Install missing skill dependencies", StringComparison.OrdinalIgnoreCase))
            return new(WizardOnboardingAction.Answer,
                options.Any(option => option.Value == "__skip__") ? "__skip__" : "[]");

        if (type != "select")
            return show;
        if (message.Equals("Onboarding mode", StringComparison.OrdinalIgnoreCase) ||
            message.Equals("Setup mode", StringComparison.OrdinalIgnoreCase))
            return Choose(options, "keep-model", "quickstart");
        if (message.Equals("Config handling", StringComparison.OrdinalIgnoreCase))
            return Choose(options, "keep");
        if (message.StartsWith("Select channel", StringComparison.OrdinalIgnoreCase) ||
            message.Equals("Search provider", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("Gateway service already installed", StringComparison.OrdinalIgnoreCase))
            return Choose(options, "skip", "__skip__");
        return show;
    }

    private static WizardOnboardingDecision Choose(IReadOnlyList<WizardOptionValue> options, params string[] values)
    {
        foreach (var value in values)
            if (options.Any(option => option.Value == value))
                return new(WizardOnboardingAction.Answer, value);
        return new(WizardOnboardingAction.Show);
    }

    private static string Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()!.Trim()
            : "";
}
