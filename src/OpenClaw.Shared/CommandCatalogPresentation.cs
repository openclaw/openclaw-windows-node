using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClaw.Shared;

// ── Command catalog presentation helpers ──
//
// The wire DTOs (GatewayCommand / GatewayCommandArg / CommandCatalog /
// CommandCatalogQuery) and the gateway request API (ListCommandsAsync) live in
// GatewayProtocolModels.cs + OpenClawGatewayClient.Protocol.cs. This file adds
// only UI-facing presentation logic on top of those DTOs:
//   • display / insertion helpers for a single GatewayCommand
//   • ranked search + category grouping that the chat command palette needs
//     (CommandCatalogQuery does boolean filtering only - no ranking or
//     grouped output).
// Nothing here duplicates a protocol DTO.

/// <summary>Source/display/insertion presentation helpers for gateway commands.</summary>
public static class GatewayCommandPresentation
{
    /// <summary>
    /// Best slash/native string to show as the command's primary label. Prefers
    /// the native name, then the first text alias, then a slash-prefixed name.
    /// </summary>
    public static string DisplayName(this GatewayCommand command)
    {
        if (command is null) return "";
        if (!string.IsNullOrWhiteSpace(command.NativeName)) return Normalize(command.NativeName!);
        var alias = command.TextAliases?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        if (!string.IsNullOrWhiteSpace(alias)) return Normalize(alias!);
        return Normalize(command.Name);
    }

    /// <summary>Short, capitalized label for the command source ("native"→"Native").</summary>
    public static string SourceLabel(this GatewayCommand command)
    {
        var s = command?.Source;
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s!.Trim();
        return char.ToUpperInvariant(t[0]) + (t.Length > 1 ? t[1..] : "");
    }

    /// <summary>True when the command needs argument input before it can run.</summary>
    public static bool RequiresArgs(this GatewayCommand command)
    {
        if (command is null) return false;
        return command.AcceptsArgs || (command.Args?.Any(a => a.Required) ?? false);
    }

    /// <summary>
    /// Text to insert into the composer when the command is chosen. Commands
    /// that take arguments get a trailing space so the user can immediately type
    /// the value (we never inject placeholder text, which would be sent verbatim).
    /// </summary>
    public static string BuildInsertionText(this GatewayCommand command)
    {
        var token = command.DisplayName();
        return command.RequiresArgs() ? token + " " : token;
    }

    /// <summary>Inline argument template (e.g. "&lt;message&gt; [level]"), or "" when none.</summary>
    public static string ArgTemplate(this GatewayCommand command)
    {
        if (command?.Args is null || command.Args.Count == 0) return "";
        return string.Join(" ", command.Args
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .Select(a => a.Required ? $"<{a.Name}>" : $"[{a.Name}]"));
    }

    /// <summary>Static choice count on the first arg (for the "N options" badge); 0 when dynamic/none.</summary>
    public static int OptionCount(this GatewayCommand command)
    {
        var first = command?.Args?.FirstOrDefault();
        return first is { IsDynamic: false } ? first.Choices.Count : 0;
    }

    /// <summary>Static choices on the first declared arg (empty when dynamic or none).</summary>
    public static IReadOnlyList<GatewayCommandArgChoice> FirstArgChoices(this GatewayCommand command)
    {
        var first = command?.Args?.FirstOrDefault();
        return first is { IsDynamic: false } ? first.Choices : Array.Empty<GatewayCommandArgChoice>();
    }

    /// <summary>Composer text for a chosen arg value: "/name value".</summary>
    public static string BuildArgInsertionText(this GatewayCommand command, string value) =>
        command.DisplayName() + " " + (value ?? "").Trim();

    /// <summary>True when <paramref name="name"/> (slash-stripped) matches this command's name/native/alias.</summary>
    public static bool MatchesName(this GatewayCommand command, string name)
    {
        if (command is null || string.IsNullOrWhiteSpace(name)) return false;
        var n = name.Trim().TrimStart('/');
        bool Eq(string? a) => !string.IsNullOrWhiteSpace(a) &&
            string.Equals(a!.Trim().TrimStart('/'), n, StringComparison.OrdinalIgnoreCase);
        if (Eq(command.NativeName) || Eq(command.Name)) return true;
        return command.TextAliases?.Any(Eq) ?? false;
    }

    private static string Normalize(string value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return v;
        // Slash-style commands are the convention; only prefix bare identifiers
        // (don't double a leading slash, and leave already-prefixed values alone).
        return v[0] == '/' ? v : "/" + v;
    }
}

/// <summary>A named group of commands sharing a category, in display order.</summary>
public sealed record CommandCategoryGroup(string Category, IReadOnlyList<GatewayCommand> Commands);

/// <summary>
/// A grouped command palette: the display <see cref="Groups"/>; the same commands
/// <see cref="Flattened"/> in group/display order (the keyboard-navigation list);
/// and <see cref="DefaultSelectionIndex"/> - the index in <see cref="Flattened"/>
/// of the GLOBAL best search match, i.e. the row keyboard selection should default
/// to so that display grouping never demotes a strong later-bucket match behind a
/// weak earlier-bucket one for Enter/Tab.
/// </summary>
public sealed record GroupedPalette(
    IReadOnlyList<CommandCategoryGroup> Groups,
    IReadOnlyList<GatewayCommand> Flattened,
    int DefaultSelectionIndex);

/// <summary>
/// Ranked search + category grouping over a set of gateway commands for the chat
/// command palette. Distinct from <see cref="CommandCatalogQuery"/> (a boolean
/// filter mirroring the gateway's server-side filtering) - this adds relevance
/// ranking and grouped output the UI needs. UI-only; lives in OpenClaw.Shared so
/// it can be unit-tested directly.
/// </summary>
public sealed class ChatCommandCatalogView
{
    private readonly List<GatewayCommand> _commands;

    public ChatCommandCatalogView(IEnumerable<GatewayCommand>? commands)
    {
        _commands = (commands ?? Enumerable.Empty<GatewayCommand>())
            .Where(c => c is not null)
            .ToList();
    }

    public IReadOnlyList<GatewayCommand> Commands => _commands;
    public int Count => _commands.Count;

    /// <summary>
    /// Case-insensitive ranked search across display name, native name, text
    /// aliases, canonical name, description and category. A leading slash in the
    /// query is ignored so "/cl" and "cl" behave identically. An empty query
    /// returns the full catalog in display order.
    /// </summary>
    public IReadOnlyList<GatewayCommand> Search(string? query)
    {
        var q = (query ?? "").Trim();
        if (q.StartsWith("/", StringComparison.Ordinal)) q = q.TrimStart('/');
        q = q.Trim();

        if (q.Length == 0)
            return Ordered(_commands).ToList();

        var scored = new List<(GatewayCommand Cmd, int Score)>();
        foreach (var cmd in _commands)
        {
            var score = ScoreMatch(cmd, q);
            if (score > 0) scored.Add((cmd, score));
        }

        return scored
            .OrderByDescending(t => t.Score)
            .ThenBy(t => t.Cmd.DisplayName(), StringComparer.OrdinalIgnoreCase)
            .Select(t => t.Cmd)
            .ToList();
    }

    /// <summary>
    /// Groups commands by their raw category (falling back to source label, then
    /// "Other"), optionally filtered/ordered. See <see cref="GroupBy"/>.
    /// </summary>
    public IReadOnlyList<CommandCategoryGroup> GroupByCategory(
        string? query = null, IReadOnlyList<string>? categoryOrder = null)
        => GroupBy(CategoryKey, query, categoryOrder);

    /// <summary>
    /// Groups commands using a caller-supplied category selector, optionally
    /// filtered by the same search used in <see cref="Search"/>. Members within a
    /// group preserve the relevance order produced by <see cref="Search"/>
    /// (alphabetical when the query is empty), so the top match stays first. When
    /// <paramref name="categoryOrder"/> is supplied, groups are ordered by that
    /// sequence first (unlisted categories sort last, alphabetically); otherwise
    /// groups are ordered alphabetically. The selector lets the palette group by a
    /// mapped bucket (e.g. Mac's Session/Model/Tools/Agents) rather than the raw
    /// wire category.
    /// </summary>
    public IReadOnlyList<CommandCategoryGroup> GroupBy(
        Func<GatewayCommand, string> categorySelector,
        string? query = null,
        IReadOnlyList<string>? categoryOrder = null)
    {
        var matched = Search(query);
        // GroupBy preserves source (relevance) order within each group; do NOT
        // re-sort the members - that would demote a strong match below a weaker
        // one and change the default Enter target.
        var groups = matched
            .GroupBy(categorySelector, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CommandCategoryGroup(g.Key, g.ToList()));

        if (categoryOrder is { Count: > 0 })
        {
            int Rank(string category)
            {
                for (int i = 0; i < categoryOrder.Count; i++)
                    if (string.Equals(categoryOrder[i], category, StringComparison.OrdinalIgnoreCase))
                        return i;
                return int.MaxValue;
            }

            return groups
                .OrderBy(g => Rank(g.Category))
                .ThenBy(g => g.Category, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return groups
            .OrderBy(g => g.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Groups commands for the chat command palette (visual grouping) and also
    /// computes <see cref="GroupedPalette.DefaultSelectionIndex"/> - the index,
    /// within the flattened group order, of the GLOBAL best <see cref="Search"/>
    /// match. Display grouping orders by bucket, which can render a strong
    /// later-bucket match after a weak earlier-bucket one; keyboard selection
    /// should default to this index (not 0) to preserve the flat-search Enter/Tab
    /// target.
    /// </summary>
    public GroupedPalette GroupForPalette(
        Func<GatewayCommand, string> categorySelector,
        string? query = null,
        IReadOnlyList<string>? categoryOrder = null)
    {
        var groups = GroupBy(categorySelector, query, categoryOrder);
        var flattened = groups.SelectMany(g => g.Commands).ToList();
        var top = Search(query).FirstOrDefault();
        var defaultIndex = top is null ? 0 : flattened.IndexOf(top);
        return new GroupedPalette(groups, flattened, defaultIndex < 0 ? 0 : defaultIndex);
    }

    private static string CategoryKey(GatewayCommand cmd)
    {
        if (!string.IsNullOrWhiteSpace(cmd.Category)) return cmd.Category!.Trim();
        var src = cmd.SourceLabel();
        if (!string.IsNullOrWhiteSpace(src)) return src;
        return "Other";
    }

    private static IEnumerable<GatewayCommand> Ordered(IEnumerable<GatewayCommand> source) =>
        source.OrderBy(c => c.DisplayName(), StringComparer.OrdinalIgnoreCase);

    private static int ScoreMatch(GatewayCommand cmd, string q)
    {
        int best = 0;

        void Consider(string? token, int exact, int prefix, int contains)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            var t = token!.TrimStart('/');
            if (t.Equals(q, StringComparison.OrdinalIgnoreCase)) best = Math.Max(best, exact);
            else if (t.StartsWith(q, StringComparison.OrdinalIgnoreCase)) best = Math.Max(best, prefix);
            else if (t.Contains(q, StringComparison.OrdinalIgnoreCase)) best = Math.Max(best, contains);
        }

        Consider(cmd.DisplayName(), 100, 80, 50);
        Consider(cmd.NativeName, 100, 80, 50);
        Consider(cmd.Name, 90, 70, 45);
        foreach (var alias in cmd.TextAliases ?? Array.Empty<string>())
            Consider(alias, 90, 70, 45);

        if (best == 0 && !string.IsNullOrWhiteSpace(cmd.Description) &&
            cmd.Description!.Contains(q, StringComparison.OrdinalIgnoreCase))
            best = 20;

        if (best == 0 && !string.IsNullOrWhiteSpace(cmd.Category) &&
            cmd.Category!.Contains(q, StringComparison.OrdinalIgnoreCase))
            best = 15;

        return best;
    }
}

/// <summary>
/// Mac/web parity for the slash command palette grouping (slash-commands.ts):
/// maps each command into one of four display buckets - Session, Model, Tools,
/// Agents - via per-command name overrides first, then a raw-category fallback
/// (session→Session, options→Model, management→Tools, everything else→Tools).
/// </summary>
public static class CommandCategories
{
    /// <summary>Bucket display order, mirroring Mac's CATEGORY_ORDER.</summary>
    public static readonly IReadOnlyList<string> DisplayOrder = new[]
    {
        "session", "model", "tools", "agents",
    };

    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["session"] = "Session",
        ["model"] = "Model",
        ["tools"] = "Tools",
        ["agents"] = "Agents",
    };

    // Per-command bucket overrides keyed by normalized command name. Mirrors
    // Mac's CATEGORY_OVERRIDES, applied before the raw-category fallback so e.g.
    // /usage (raw category "options") lands in Tools rather than Model. This is a
    // deliberate SUPERSET of Mac's map: it additionally pins export/kill/focus/
    // unfocus, which Mac leaves to its raw-category fallback.
    private static readonly Dictionary<string, string> BucketOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["help"] = "tools",
        ["commands"] = "tools",
        ["tools"] = "tools",
        ["skill"] = "tools",
        ["status"] = "tools",
        ["export_session"] = "tools",
        ["export"] = "tools",
        ["usage"] = "tools",
        ["tts"] = "tools",
        ["agents"] = "agents",
        ["subagents"] = "agents",
        ["kill"] = "agents",
        ["steer"] = "agents",
        ["redirect"] = "agents",
        ["session"] = "session",
        ["stop"] = "session",
        ["reset"] = "session",
        ["new"] = "session",
        ["compact"] = "session",
        ["focus"] = "session",
        ["unfocus"] = "session",
        ["model"] = "model",
        ["models"] = "model",
        ["think"] = "model",
        ["verbose"] = "model",
        ["fast"] = "model",
        ["reasoning"] = "model",
        ["elevated"] = "model",
        ["queue"] = "model",
    };

    /// <summary>
    /// Display bucket (session/model/tools/agents) for a command, mirroring Mac's
    /// mapCategory: name override first, then raw-category fallback.
    /// </summary>
    public static string Bucket(GatewayCommand command)
    {
        if (command is null) return "tools";
        var key = NormalizeKey(command);
        if (!string.IsNullOrEmpty(key) && BucketOverrides.TryGetValue(key, out var bucket))
            return bucket;

        return (command.Category?.Trim().ToLowerInvariant()) switch
        {
            "session" => "session",
            "options" => "model",
            "management" => "tools",
            _ => "tools",
        };
    }

    /// <summary>Friendly heading for a bucket key; Title-cases unknowns.</summary>
    public static string Label(string? bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket)) return "Other";
        var key = bucket!.Trim();
        if (Labels.TryGetValue(key, out var label)) return label;
        return char.ToUpperInvariant(key[0]) + (key.Length > 1 ? key[1..] : "");
    }

    // Normalizes a command name to an override key the way Mac's normalizeUiKey
    // does (lowercase, strip a leading slash, ':' '.' '-' → '_'). Mac keys off
    // command.key; the wire command carries no key here, and the catalog is
    // requested with text scope, so the text-surface Name is the closest analog -
    // fall back to the first text alias, then the native name.
    private static string NormalizeKey(GatewayCommand command)
    {
        var raw = !string.IsNullOrWhiteSpace(command.Name)
            ? command.Name
            : command.TextAliases?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        if (string.IsNullOrWhiteSpace(raw)) raw = command.NativeName;
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return "";
        if (raw[0] == '/') raw = raw[1..];
        raw = raw.ToLowerInvariant();
        var chars = raw.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] is ':' or '.' or '-') chars[i] = '_';
        return new string(chars);
    }
}
