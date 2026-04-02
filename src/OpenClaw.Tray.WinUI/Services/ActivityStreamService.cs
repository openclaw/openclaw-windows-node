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
    private const int MaxItems = 200;

    public static event EventHandler? Updated;

    public static void Add(
        string category,
        string title,
        string? details = null,
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
                Details = details ?? "",
                DashboardPath = dashboardPath,
                SessionKey = sessionKey,
                NodeId = nodeId
            });

            while (_items.Count > MaxItems)
            {
                _items.RemoveLast();
                Logger.Debug($"[ActivityStream] Trimmed oldest item (exceeded max {MaxItems})");
            }
        }

        var truncatedTitle = title.Length > 50 ? title[..50] + "…" : title;
        Logger.Info($"[ActivityStream] Item added: [{category}] {truncatedTitle}");

        Updated?.Invoke(null, EventArgs.Empty);
    }

    public static IReadOnlyList<ActivityStreamItem> GetItems(int maxItems = 200, string? category = null)
    {
        lock (_lock)
        {
            IEnumerable<ActivityStreamItem> query = _items;
            if (!string.IsNullOrWhiteSpace(category))
            {
                query = query.Where(item => string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase));
            }

            return query.Take(Math.Max(0, maxItems)).ToList();
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
        }

        Updated?.Invoke(null, EventArgs.Empty);
    }
}

public class ActivityStreamItem
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Category { get; set; } = "general";
    public string Title { get; set; } = "";
    public string Details { get; set; } = "";
    public string? DashboardPath { get; set; }
    public string? SessionKey { get; set; }
    public string? NodeId { get; set; }
}
