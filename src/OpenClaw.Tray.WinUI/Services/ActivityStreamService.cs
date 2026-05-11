using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Services;

/// <summary>
/// Stores recent tray activity (sessions, usage, nodes, notifications) for menu + flyout UX.
/// </summary>
public static class ActivityStreamService
{
    private static readonly LinkedList<ActivityStreamItem> _items = new();
    private static readonly object _lock = new();
    public const int MaxStoredItems = 400;

    public static event EventHandler? Updated;

    public static void Add(
        string category,
        string title,
        string? details = null,
        string? icon = null,
        string? dashboardPath = null,
        string? sessionKey = null,
        string? nodeId = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return;

        lock (_lock)
        {
            _items.AddFirst(new ActivityStreamItem
            {
                Timestamp = DateTime.Now,
                Category = string.IsNullOrWhiteSpace(category) ? "general" : category,
                Title = title,
                Icon = icon ?? "",
                Details = details ?? "",
                DashboardPath = dashboardPath,
                SessionKey = sessionKey,
                NodeId = nodeId
            });

            while (_items.Count > MaxStoredItems)
            {
                _items.RemoveLast();
                Logger.Debug($"[ActivityStream] Trimmed oldest item (exceeded max {MaxStoredItems})");
            }
        }

        var truncatedTitle = title.Length > 50 ? title[..50] + "…" : title;
        Logger.Info($"[ActivityStream] Item added: [{category}] {truncatedTitle}");

        Updated?.Invoke(null, EventArgs.Empty);
    }

    public static IReadOnlyList<ActivityStreamItem> GetItems(int maxItems = MaxStoredItems, string? category = null)
    {
        lock (_lock)
        {
            IEnumerable<ActivityStreamItem> query = _items;
            if (!string.IsNullOrWhiteSpace(category))
            {
                query = query.Where(item => CategoryMatches(item.Category, category));
            }

            return query.Take(Math.Max(0, maxItems)).ToList();
        }
    }

    public static string BuildSupportBundle(int maxItems = MaxStoredItems)
    {
        IReadOnlyList<ActivityStreamItem> snapshot;
        lock (_lock)
        {
            snapshot = _items.Take(Math.Max(0, maxItems)).ToList();
        }

        var lines = new List<string>
        {
            "OpenClaw Tray activity support bundle",
            $"Generated: {DateTimeOffset.Now:O}",
            $"Items: {snapshot.Count}",
            ""
        };

        foreach (var item in snapshot)
        {
            var details = string.IsNullOrWhiteSpace(item.Details)
                ? ""
                : $" | {item.Details}";
            var session = string.IsNullOrWhiteSpace(item.SessionKey)
                ? ""
                : $" | session={item.SessionKey}";
            var node = string.IsNullOrWhiteSpace(item.NodeId)
                ? ""
                : $" | node={ShortId(item.NodeId)}";
            lines.Add($"{item.Timestamp:O} [{item.Category}] {item.Title}{details}{session}{node}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
        }

        Updated?.Invoke(null, EventArgs.Empty);
    }

    private static bool CategoryMatches(string itemCategory, string requestedCategory)
    {
        if (string.Equals(requestedCategory, "all", StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(itemCategory, requestedCategory, StringComparison.OrdinalIgnoreCase) ||
               itemCategory.StartsWith(requestedCategory + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static string ShortId(string value) => value.Length <= 16 ? value : value[..16] + "...";
}

public class ActivityStreamItem
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Category { get; set; } = "general";
    public string Title { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Details { get; set; } = "";
    public string? DashboardPath { get; set; }
    public string? SessionKey { get; set; }
    public string? NodeId { get; set; }
}
