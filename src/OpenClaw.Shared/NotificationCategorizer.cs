using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared;

/// <summary>
/// Layered notification categorization pipeline.
/// Order: structured metadata → user rules → keyword fallback → default.
/// </summary>
public class NotificationCategorizer
{
    private static readonly Dictionary<string, (string title, string type)> ChannelMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["calendar"] = ("📅 Calendar", "calendar"),
        ["email"] = ("📧 Email", "email"),
        ["ci"] = ("🔨 Build", "build"),
        ["build"] = ("🔨 Build", "build"),
        ["inventory"] = ("📦 Stock Alert", "stock"),
        ["stock"] = ("📦 Stock Alert", "stock"),
        ["health"] = ("🩸 Blood Sugar Alert", "health"),
        ["alerts"] = ("🚨 Urgent Alert", "urgent"),
    };

    private static readonly Dictionary<string, (string title, string type)> IntentMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["health"] = ("🩸 Blood Sugar Alert", "health"),
        ["urgent"] = ("🚨 Urgent Alert", "urgent"),
        ["alert"] = ("🚨 Urgent Alert", "urgent"),
        ["reminder"] = ("⏰ Reminder", "reminder"),
        ["email"] = ("📧 Email", "email"),
        ["calendar"] = ("📅 Calendar", "calendar"),
        ["build"] = ("🔨 Build", "build"),
        ["stock"] = ("📦 Stock Alert", "stock"),
        ["error"] = ("⚠️ Error", "error"),
    };

    private static readonly Dictionary<string, string> CategoryTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["health"] = "🩸 Blood Sugar Alert",
        ["urgent"] = "🚨 Urgent Alert",
        ["reminder"] = "⏰ Reminder",
        ["stock"] = "📦 Stock Alert",
        ["email"] = "📧 Email",
        ["calendar"] = "📅 Calendar",
        ["error"] = "⚠️ Error",
        ["build"] = "🔨 Build",
        ["info"] = "🤖 OpenClaw",
    };

    /// <summary>
    /// Classify a notification using the layered pipeline.
    /// </summary>
    public (string title, string type) Classify(OpenClawNotification notification, IReadOnlyList<UserNotificationRule>? userRules = null)
    {
        // 1. Structured metadata: Intent
        if (!string.IsNullOrEmpty(notification.Intent) && IntentMap.TryGetValue(notification.Intent, out var intentResult))
            return intentResult;

        // 2. Structured metadata: Channel
        if (!string.IsNullOrEmpty(notification.Channel) && ChannelMap.TryGetValue(notification.Channel, out var channelResult))
            return channelResult;

        // 3. User-defined rules (pattern match on title + message)
        if (userRules is { Count: > 0 })
        {
            var searchText = $"{notification.Title} {notification.Message}";
            foreach (var rule in userRules)
            {
                if (!rule.Enabled) continue;
                if (MatchesRule(searchText, rule))
                {
                    var cat = rule.Category.ToLowerInvariant();
                    var title = CategoryTitles.GetValueOrDefault(cat, "🤖 OpenClaw");
                    return (title, cat);
                }
            }
        }

        // 4. Legacy keyword fallback
        return ClassifyByKeywords(notification.Message);
    }

    /// <summary>
    /// Legacy keyword-based classification (backward compatible).
    /// </summary>
    public static (string title, string type) ClassifyByKeywords(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("blood sugar") || lower.Contains("glucose") ||
            lower.Contains("cgm") || lower.Contains("mg/dl"))
            return ("🩸 Blood Sugar Alert", "health");
        if (lower.Contains("urgent") || lower.Contains("critical") ||
            lower.Contains("emergency"))
            return ("🚨 Urgent Alert", "urgent");
        if (lower.Contains("reminder"))
            return ("⏰ Reminder", "reminder");
        if (lower.Contains("stock") || lower.Contains("in stock") ||
            lower.Contains("available now"))
            return ("📦 Stock Alert", "stock");
        if (lower.Contains("email") || lower.Contains("inbox") ||
            lower.Contains("gmail"))
            return ("📧 Email", "email");
        if (lower.Contains("calendar") || lower.Contains("meeting") ||
            lower.Contains("event"))
            return ("📅 Calendar", "calendar");
        if (lower.Contains("error") || lower.Contains("failed") ||
            lower.Contains("exception"))
            return ("⚠️ Error", "error");
        if (lower.Contains("build") || lower.Contains("ci ") ||
            lower.Contains("deploy"))
            return ("🔨 Build", "build");
        return ("🤖 OpenClaw", "info");
    }

    // Regex cache: avoids recompiling the same pattern on every notification.
    // The Regex instances are constructed with a 100ms match timeout to guard against ReDoS.
    private static readonly ConcurrentDictionary<string, Regex?> _regexCache = new(StringComparer.Ordinal);

    private static bool MatchesRule(string text, UserNotificationRule rule)
    {
        if (string.IsNullOrEmpty(rule.Pattern)) return false;

        if (rule.IsRegex)
        {
            var regex = _regexCache.GetOrAdd(rule.Pattern, static p =>
            {
                try
                {
                    return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
                }
                catch (RegexParseException)
                {
                    return null;
                }
            });

            if (regex == null) return false;

            try
            {
                return regex.IsMatch(text);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return text.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase);
    }
}
