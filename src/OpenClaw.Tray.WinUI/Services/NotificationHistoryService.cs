using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenClawTray.Services;

/// <summary>
/// Stores notification history in memory with a configurable limit.
/// </summary>
public static class NotificationHistoryService
{
    private static readonly LinkedList<NotificationHistoryItem> _history = new();
    private static readonly object _lock = new();
    private const int MaxHistory = 100;

    public static void AddNotification(GatewayNotification notification)
    {
        lock (_lock)
        {
            _history.AddFirst(new NotificationHistoryItem
            {
                Timestamp = DateTime.Now,
                Title = notification.Title ?? "OpenClaw",
                Message = notification.Message ?? "",
                Category = notification.Category,
                ActionUrl = notification.ActionUrl
            });

            // Trim to max
            while (_history.Count > MaxHistory)
            {
                _history.RemoveLast();
            }
        }
    }

    public static IReadOnlyList<NotificationHistoryItem> GetHistory()
    {
        lock (_lock)
        {
            return _history.ToList();
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _history.Clear();
        }
    }
}

public class NotificationHistoryItem
{
    public DateTime Timestamp { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Category { get; set; }
    public string? ActionUrl { get; set; }
}

// Local notification model
public class GatewayNotification
{
    public string? Title { get; set; }
    public string? Message { get; set; }
    public string? Category { get; set; }
    public string? ActionUrl { get; set; }
}
