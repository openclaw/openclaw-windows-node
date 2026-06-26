using OpenClaw.Shared;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OpenClawTray.Services;

internal enum TrayHealthSeverity
{
    Neutral,
    Ok,
    Caution,
    Critical,
}

internal sealed record TrayDashboardActiveSession(
    string Label,
    string Title,
    string? Detail,
    int ContextPercent);

/// <summary>
/// Pure, render-free computation for the tray dashboard glance.
/// </summary>
internal sealed record TrayDashboardSummary
{
    internal required TrayHealthSeverity Severity { get; init; }
    internal required string Headline { get; init; }
    internal required string? Endpoint { get; init; }
    internal required string? Heartbeat { get; init; }
    internal required string? MetricsLine { get; init; }
    internal required TrayDashboardActiveSession? ActiveSession { get; init; }

    internal bool HasActiveSession => ActiveSession is not null;
}

/// <summary>
/// Builds tray dashboard summary text from a menu snapshot.
/// </summary>
internal sealed class TrayDashboardSummaryBuilder
{
    private readonly TrayMenuSnapshot _snapshot;
    private readonly DateTime _nowUtc;

    internal TrayDashboardSummaryBuilder(TrayMenuSnapshot snapshot, DateTime? nowUtc = null)
    {
        _snapshot = snapshot;
        _nowUtc = nowUtc ?? DateTime.UtcNow;
    }

    internal TrayDashboardSummary Build()
    {
        var isConnected = _snapshot.CurrentStatus == ConnectionStatus.Connected;

        var (severity, headline) = ClassifyHealth();

        string? endpoint = null;
        if (!string.IsNullOrEmpty(_snapshot.GatewayUrl)
            && Uri.TryCreate(_snapshot.GatewayUrl, UriKind.Absolute, out var uri))
        {
            endpoint = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        }

        return new TrayDashboardSummary
        {
            Severity = severity,
            Headline = headline,
            Endpoint = endpoint,
            Heartbeat = isConnected ? BuildHeartbeat() : null,
            MetricsLine = BuildMetricsLine(isConnected),
            ActiveSession = BuildActiveSession(),
        };
    }

    private (TrayHealthSeverity, string) ClassifyHealth()
    {
        if (!string.IsNullOrEmpty(_snapshot.AuthFailureMessage))
            return (TrayHealthSeverity.Critical, "Authentication failed");

        var pending = (_snapshot.NodePairList?.Pending.Count ?? 0)
            + (_snapshot.DevicePairList?.Pending.Count ?? 0);
        if (pending > 0)
            return (TrayHealthSeverity.Caution, $"Pairing approval pending ({pending})");

        return _snapshot.CurrentStatus switch
        {
            ConnectionStatus.Connected => (TrayHealthSeverity.Ok, "Connected"),
            ConnectionStatus.Connecting => (TrayHealthSeverity.Caution, "Connecting…"),
            ConnectionStatus.Error => (TrayHealthSeverity.Critical, "Connection error"),
            _ => (TrayHealthSeverity.Neutral, "Disconnected"),
        };
    }

    private string? BuildHeartbeat()
    {
        if (_snapshot.LastUpdated is not { } updated)
            return null;

        var updatedUtc = updated.Kind == DateTimeKind.Utc ? updated : updated.ToUniversalTime();
        var age = _nowUtc - updatedUtc;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return $"Updated {FormatAge(age)}";
    }

    private string? BuildMetricsLine(bool isConnected)
    {
        var parts = new List<string>(3);

        var nodesTotal = _snapshot.Nodes.Length;
        if (nodesTotal > 0)
        {
            var online = _snapshot.Nodes.Count(n => n.IsOnline);
            parts.Add($"{online}/{nodesTotal} {(nodesTotal == 1 ? "node" : "nodes")}");
        }

        var sessionCount = _snapshot.Sessions.Length;
        if (sessionCount > 0)
        {
            var active = _snapshot.Sessions.Count(
                s => string.Equals(s.Status, "active", StringComparison.OrdinalIgnoreCase));
            parts.Add(active > 0
                ? $"{sessionCount} {(sessionCount == 1 ? "session" : "sessions")} ({active} active)"
                : $"{sessionCount} {(sessionCount == 1 ? "session" : "sessions")}");
        }

        if (isConnected)
        {
            var usage = BuildUsageGlance();
            if (usage != null) parts.Add(usage);
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private string? BuildUsageGlance()
    {
        var cost = FirstPositiveCost(
            _snapshot.Usage?.CostUsd,
            _snapshot.UsageCost?.Totals.TotalCost);

        var totalTokens = FirstPositiveTokens(
            _snapshot.Usage?.TotalTokens,
            _snapshot.UsageCost?.Totals.TotalTokens,
            _snapshot.Sessions.Sum(SessionUsedTokens));

        if (cost <= 0 && totalTokens <= 0)
            return null;

        if (cost >= 0.005)
            return "$" + cost.ToString("F2", CultureInfo.InvariantCulture);
        return $"{FormatTokenCount(totalTokens)} tokens";
    }

    internal static double FirstPositiveCost(params double?[] candidates)
    {
        foreach (var c in candidates)
            if (c is > 0) return c.Value;
        return 0.0;
    }

    internal static long FirstPositiveTokens(params long?[] candidates)
    {
        foreach (var c in candidates)
            if (c is > 0) return c.Value;
        return 0L;
    }

    internal static long SessionUsedTokens(SessionInfo session) =>
        session.TotalTokens > 0 ? session.TotalTokens : session.InputTokens + session.OutputTokens;

    internal static SessionInfo? SelectActiveSession(IReadOnlyList<SessionInfo> sessions)
    {
        if (sessions == null || sessions.Count == 0)
            return null;

        static bool IsActive(SessionInfo s) =>
            string.Equals(s.Status, "active", StringComparison.OrdinalIgnoreCase);

        var activeMain = sessions.FirstOrDefault(s => s.IsMain && IsActive(s));
        if (activeMain != null) return activeMain;

        var activeRecent = sessions
            .Where(IsActive)
            .OrderByDescending(s => s.UpdatedAt ?? s.LastSeen)
            .FirstOrDefault();
        if (activeRecent != null) return activeRecent;

        var main = sessions.FirstOrDefault(s => s.IsMain);
        if (main != null) return main;

        return sessions.OrderByDescending(s => s.UpdatedAt ?? s.LastSeen).First();
    }

    private TrayDashboardActiveSession? BuildActiveSession()
    {
        var session = SelectActiveSession(_snapshot.Sessions);
        if (session == null)
            return null;

        var isActive = string.Equals(session.Status, "active", StringComparison.OrdinalIgnoreCase);
        var label = isActive ? "Active" : (session.IsMain ? "Main" : "Session");

        var title = !string.IsNullOrWhiteSpace(session.DisplayName)
            ? session.DisplayName!
            : (session.IsMain ? "Main session" : (string.IsNullOrEmpty(session.Key) ? "Session" : session.ShortKey));

        var usedTokens = SessionUsedTokens(session);
        var contextTokens = session.ContextTokens > 0 ? session.ContextTokens : 200_000;
        var pct = usedTokens > 0
            ? (int)Math.Round(Math.Min(100.0, (double)usedTokens / contextTokens * 100.0))
            : 0;

        // Detail carries only stable metadata; CurrentActivity can include
        // command/query/path/URL snippets and should stay out of the top-level
        // tray glance.
        var detailParts = new List<string>(1);
        if (!string.IsNullOrWhiteSpace(session.Model)) detailParts.Add(session.Model!);
        var detail = detailParts.Count == 0 ? null : string.Join(" · ", detailParts);

        return new TrayDashboardActiveSession(
            Label: label,
            Title: title,
            Detail: detail,
            ContextPercent: pct);
    }

    private static string FormatTokenCount(long n)
    {
        if (n >= 1_000_000) return $"{n / 1_000_000.0:F1}M";
        if (n >= 1_000) return $"{n / 1_000.0:F1}K";
        return n.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalSeconds < 60) return $"{(int)age.TotalSeconds}s ago";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

}
