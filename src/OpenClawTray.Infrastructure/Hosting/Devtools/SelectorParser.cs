using System.Text.RegularExpressions;

namespace OpenClawTray.Infrastructure.Hosting.Devtools;

/// <summary>
/// Parsed selector IR. One of:
///   NodeId              — <c>r:&lt;window&gt;/&lt;local&gt;</c>
///   AutomationId        — <c>#btn-inc</c>
///   AutomationName      — <c>[name='Increment']</c>
///   TypePath            — <c>Button</c>, <c>Button[2]</c>, <c>StackPanel &gt; Button</c>
///   ReactorSource       — <c>{component:'CounterDemo',line:42}</c>
/// Only the matching kind's fields are populated.
/// </summary>
internal sealed record SelectorIr(
    SelectorKind Kind,
    string? NodeId = null,
    string? AutomationId = null,
    string? AutomationName = null,
    IReadOnlyList<TypeStep>? TypePath = null,
    string? ReactorComponent = null,
    int? ReactorLine = null);

internal enum SelectorKind { NodeId, AutomationId, AutomationName, TypePath, ReactorSource }

internal sealed record TypeStep(string TypeName, int? Index);

/// <summary>
/// Pure parser for the MCP selector grammar (spec §11 "Selector resolution order").
/// Unit-testable without WinUI — turns a selector string into a structured IR that
/// the runtime resolver can walk against the live visual tree.
/// </summary>
internal static class SelectorParser
{
    private static readonly Regex NameRegex = new(@"^\[name=(?:'([^']*)'|""([^""]*)"")\]$", RegexOptions.Compiled);
    private static readonly Regex ReactorRegex = new(
        @"^\{\s*component\s*:\s*'([^']+)'\s*,\s*line\s*:\s*(\d+)\s*\}$",
        RegexOptions.Compiled);
    private static readonly Regex TypeStepRegex = new(
        @"^([A-Za-z_][A-Za-z0-9_]*)(?:\[(\d+)\])?$",
        RegexOptions.Compiled);

    public static SelectorIr Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new FormatException("Selector is empty.");

        var trimmed = input.Trim();

        if (trimmed.StartsWith("r:", StringComparison.Ordinal))
            return new SelectorIr(SelectorKind.NodeId, NodeId: trimmed);

        if (trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            var id = trimmed[1..];
            if (string.IsNullOrEmpty(id))
                throw new FormatException("AutomationId selector must have a non-empty id after '#'.");
            return new SelectorIr(SelectorKind.AutomationId, AutomationId: id);
        }

        var nameMatch = NameRegex.Match(trimmed);
        if (nameMatch.Success)
        {
            var name = nameMatch.Groups[1].Success ? nameMatch.Groups[1].Value : nameMatch.Groups[2].Value;
            return new SelectorIr(SelectorKind.AutomationName, AutomationName: name);
        }

        var reactorMatch = ReactorRegex.Match(trimmed);
        if (reactorMatch.Success)
        {
            return new SelectorIr(
                SelectorKind.ReactorSource,
                ReactorComponent: reactorMatch.Groups[1].Value,
                ReactorLine: int.Parse(reactorMatch.Groups[2].Value));
        }

        // Type path: `Button`, `Button[2]`, `StackPanel > Button`. `>` separates steps.
        var steps = trimmed.Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (steps.Length == 0)
            throw new FormatException($"Unrecognized selector: '{input}'.");

        var parsed = new List<TypeStep>(steps.Length);
        foreach (var step in steps)
        {
            var m = TypeStepRegex.Match(step);
            if (!m.Success)
                throw new FormatException($"Unrecognized type step: '{step}' in selector '{input}'.");
            int? index = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : null;
            parsed.Add(new TypeStep(m.Groups[1].Value, index));
        }
        return new SelectorIr(SelectorKind.TypePath, TypePath: parsed);
    }
}
