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
//     (CommandCatalogQuery does boolean filtering only — no ranking or
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
/// Ranked search + category grouping over a set of gateway commands for the chat
/// command palette. Distinct from <see cref="CommandCatalogQuery"/> (a boolean
/// filter mirroring the gateway's server-side filtering) — this adds relevance
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
    /// Groups commands by category (falling back to source label, then "Other"),
    /// optionally filtered by the same search used in <see cref="Search"/>.
    /// Groups and their members are returned in a stable, display-friendly order.
    /// </summary>
    public IReadOnlyList<CommandCategoryGroup> GroupByCategory(string? query = null)
    {
        var matched = Search(query);
        return matched
            .GroupBy(CategoryKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CommandCategoryGroup(g.Key, Ordered(g).ToList()))
            .OrderBy(g => g.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
