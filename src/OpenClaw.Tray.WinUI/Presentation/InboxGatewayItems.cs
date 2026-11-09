// Gateway-sourced Inbox items: pending approvals, failed or overdue automations
// and model-provider accounts that need attention. Pure builders that mirror the
// web Control UI's sidebar attention rules (ui/src/components/sidebar-attention-items.ts)
// so InboxGatewaySource can recompute them from the latest Gateway replies.
// WinUI-free; text from the Gateway is untrusted display data and is never logged.
//
// Usage:
//   var items = new List<InboxGatewayItem>();
//   items.AddRange(approvals.Select(InboxGatewayItems.FromApproval));
//   items.AddRange(InboxGatewayItems.BuildCron(cronList, schedulerEnabled, nowMs));
//   items.AddRange(InboxGatewayItems.BuildModelAuth(authStatus, nowMs));
//   var inbox = InboxProjection.Build(notifications, items);

using OpenClaw.Shared;
using OpenClaw.Shared.ExecApprovals;
using System.Text.Json;

namespace OpenClawTray.Presentation;

internal enum InboxGatewayItemKind
{
    Approval,
    AutomationFailed,
    AutomationOverdue,
    ModelAuth,
}

/// <summary>How a model-provider account failed, which selects its copy and repair action.</summary>
internal enum InboxModelAuthRepair { Review, SignIn, ApiKey }

internal sealed record InboxModelAuthAttention(
    string Provider,
    string DisplayName,
    string Status,
    InboxModelAuthRepair Repair,
    string? Scope);

/// <summary>
/// One Gateway fact that needs the user. <see cref="Id"/> is stable for the same
/// fact and changes when a new episode should resurface after a dismissal.
/// </summary>
internal sealed record InboxGatewayItem(
    string Id,
    InboxGatewayItemKind Kind,
    string Label,
    DateTimeOffset ArrivedAt)
{
    public GatewayPendingApproval? Approval { get; init; }
    public InboxModelAuthAttention? ModelAuth { get; init; }
    /// <summary>Failure text reported by the Gateway for a failed automation.</summary>
    public string? Detail { get; init; }
    /// <summary>Planned run that an overdue automation missed.</summary>
    public DateTimeOffset? DueAt { get; init; }
    /// <summary>
    /// Local-clock deadline of a pending approval, fixed when it arrived so a Gateway with a
    /// skewed clock cannot expire it early or keep it forever.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

internal static class InboxGatewayItems
{
    public const string IdPrefix = "gateway:";
    /// <summary>Upstream grace before an enabled, idle automation counts as overdue.</summary>
    public const long CronOverdueGraceMs = 300_000;

    private static readonly string[] AuthStatusPriority = ["expired", "missing", "expiring", "ok", "static"];

    public static bool IsGatewayItemId(string? id) =>
        id?.StartsWith(IdPrefix, StringComparison.Ordinal) == true;

    /// <summary>One-line command preview for a row's state line, capped like the web Control UI.</summary>
    public static string CompactCommand(string command)
    {
        var singleLine = string.Join(' ', DisplayCommand(command).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (singleLine.Length <= 64)
            return singleLine;
        var length = 61;
        if (char.IsHighSurrogate(singleLine[length - 1]))
            length--;
        return string.Concat(singleLine.AsSpan(0, length), "…");
    }

    /// <summary>
    /// Shared secret-redacted, bounded review text. Ordinary multiline layout is preserved;
    /// other invisible characters are escaped. Truncation/suppression is explicit, never a raw fallback.
    /// </summary>
    public static string DisplayCommand(string? command) => string.IsNullOrEmpty(command)
        ? ""
        : ExecApprovalCommandDisplaySanitizer.SanitizeCommandForReview(command);

    /// <summary>Remaining approval time as minutes:seconds ("4:52"), or null once expired.</summary>
    public static string? FormatCountdown(long expiresAtMs, long nowMs)
    {
        if (expiresAtMs <= nowMs)
            return null;
        var totalSeconds = (expiresAtMs - nowMs + 999) / 1000;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{totalSeconds / 60}:{totalSeconds % 60:00}");
    }

    public static InboxGatewayItem FromApproval(GatewayPendingApproval approval, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(approval);
        return new($"{IdPrefix}approval:{approval.Id}", InboxGatewayItemKind.Approval,
            DisplayCommand(approval.Title ?? approval.Command), FromMs(approval.CreatedAtMs))
        {
            Approval = approval,
            ExpiresAt = expiresAt,
        };
    }

    /// <summary>
    /// Local deadline for an approval that arrived at <paramref name="arrivedAt"/>: the Gateway's
    /// own lifetime (expiry minus creation) measured on this machine's clock. A non-positive
    /// lifetime means the request is already expired.
    /// </summary>
    public static DateTimeOffset LocalDeadline(GatewayPendingApproval approval, DateTimeOffset arrivedAt)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var lifetimeMs = (decimal)approval.ExpiresAtMs - approval.CreatedAtMs;
        return lifetimeMs <= 0
            ? arrivedAt
            : GatewayUnixTime.Add(arrivedAt, (long)Math.Min(lifetimeMs, long.MaxValue));
    }

    /// <summary>Failed and overdue automations from a <c>cron.list</c> reply, newest first per kind.</summary>
    public static IReadOnlyList<InboxGatewayItem> BuildCron(JsonElement? cronList, bool? schedulerEnabled, long nowMs)
    {
        if (cronList is not { ValueKind: JsonValueKind.Object } list ||
            !list.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Array)
            return [];

        var failed = new List<InboxGatewayItem>();
        var overdue = new List<InboxGatewayItem>();
        foreach (var job in jobs.EnumerateArray())
        {
            if (ReadString(job, "id") is not { } id)
                continue;
            var label = ReadString(job, "name") ?? id;
            var enabled = !job.TryGetProperty("enabled", out var enabledValue) || enabledValue.ValueKind != JsonValueKind.False;
            var state = job.TryGetProperty("state", out var stateValue) && stateValue.ValueKind == JsonValueKind.Object
                ? stateValue
                : default;
            var updatedAtMs = ReadInt64(job, "updatedAtMs");

            if (IsActiveFailure(enabled, state))
            {
                var at = ReadInt64(state, "lastRunAtMs") ?? updatedAtMs ?? nowMs;
                failed.Add(new($"{IdPrefix}cronFailed:{id}", InboxGatewayItemKind.AutomationFailed, label, FromMs(at))
                {
                    Detail = ReadString(state, "lastError"),
                });
            }
            if (ReadInt64(state, "nextRunAtMs") is { } nextRunAtMs &&
                schedulerEnabled != false && enabled &&
                // Any recorded run start means the job is running, whatever its value.
                !(state.ValueKind == JsonValueKind.Object &&
                    state.TryGetProperty("runningAtMs", out var running) && running.ValueKind == JsonValueKind.Number) &&
                nowMs > nextRunAtMs + CronOverdueGraceMs)
            {
                // A later overdue episode must resurface after its planned run changes.
                overdue.Add(new($"{IdPrefix}cronOverdue:{id}@{nextRunAtMs}", InboxGatewayItemKind.AutomationOverdue,
                    label, FromMs(nextRunAtMs))
                {
                    DueAt = FromMs(nextRunAtMs),
                });
            }
        }

        return failed.OrderByDescending(item => item.ArrivedAt)
            .Concat(overdue.OrderByDescending(item => item.ArrivedAt))
            .ToList();
    }

    /// <summary>
    /// Monitored model-provider accounts whose credentials expired or are missing, from a
    /// <c>models.authStatus</c> reply. API-key-only providers have no expiry to monitor.
    /// </summary>
    public static IReadOnlyList<InboxGatewayItem> BuildModelAuth(JsonElement? authStatus, long nowMs)
    {
        if (authStatus is not { ValueKind: JsonValueKind.Object } status ||
            !status.TryGetProperty("providers", out var providers) || providers.ValueKind != JsonValueKind.Array)
            return [];

        var snapshotMs = ReadInt64(status, "ts") ?? nowMs;
        // Credential-runtime aliases share one effective provider; its worst status speaks for all of them.
        var groups = providers.EnumerateArray()
            .Where(provider => ReadString(provider, "provider") is not null)
            .GroupBy(provider => CanonicalProviderId(ReadString(provider, "provider")!), StringComparer.Ordinal);

        var items = new List<InboxGatewayItem>();
        foreach (var group in groups.OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var providerId = group.Key;
            var provider = group.OrderBy(candidate => Priority(ReadString(candidate, "status"))).First();
            var providerStatus = ReadString(provider, "status");
            if (providerStatus is not ("expired" or "missing"))
                continue;

            var profiles = group.SelectMany(ReadProfiles).ToList();
            var monitored = providerStatus == "missing" ||
                profiles.Any(profile => profile.Type is "oauth" or "token");
            if (!monitored)
                continue;

            var candidates = profiles.Where(profile => profile.ReasonCode != "setup_inactive").ToList();
            var failedProfile = candidates.FirstOrDefault(profile => profile.Status is "expired" or "missing");
            // Mixed credential types cannot identify the failing auth method.
            var profileMatch = failedProfile is not null && candidates.All(profile => profile.Type == failedProfile.Type)
                ? failedProfile
                : null;
            var repair = profileMatch?.Type switch
            {
                "oauth" => InboxModelAuthRepair.SignIn,
                "api_key" => InboxModelAuthRepair.ApiKey,
                _ => InboxModelAuthRepair.Review,
            };
            var expiredAtMs = provider.TryGetProperty("expiry", out var expiry) ? ReadInt64(expiry, "at") : null;
            // Status snapshots refresh, so a known expiry keeps the row's arrival stable.
            var arrivedAtMs = expiredAtMs is { } at && at <= nowMs ? at : snapshotMs;
            var displayName = ReadString(provider, "displayName") ?? providerId;
            items.Add(new($"{IdPrefix}modelAuth:{providerId}", InboxGatewayItemKind.ModelAuth, displayName, FromMs(arrivedAtMs))
            {
                ModelAuth = new(providerId, displayName, providerStatus, repair, profileMatch?.ProfileId),
            });
        }
        return items;
    }

    /// <summary>
    /// Local time at which a <c>models.authStatus</c> snapshot read at <paramref name="requestedAt"/>
    /// should be refreshed because a credential reaches its expiry, or null when none expires later.
    /// Gateway times are mapped onto the local clock through the snapshot's <c>ts</c>.
    /// </summary>
    public static DateTimeOffset? NextModelAuthRefreshAt(JsonElement? authStatus, DateTimeOffset requestedAt)
    {
        if (authStatus is not { ValueKind: JsonValueKind.Object } status ||
            !status.TryGetProperty("providers", out var providers) || providers.ValueKind != JsonValueKind.Array)
            return null;

        var snapshotMs = ReadInt64(status, "ts") ?? requestedAt.ToUnixTimeMilliseconds();
        long? next = null;
        foreach (var provider in providers.EnumerateArray())
        {
            var credentials = new List<JsonElement> { provider };
            if (provider.ValueKind == JsonValueKind.Object &&
                provider.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
                credentials.AddRange(profiles.EnumerateArray());
            foreach (var credential in credentials)
            {
                if (credential.ValueKind != JsonValueKind.Object ||
                    !credential.TryGetProperty("expiry", out var expiry) ||
                    ReadInt64(expiry, "at") is not { } at || at <= snapshotMs)
                    continue;
                if (next is null || at < next)
                    next = at;
            }
        }
        return next is { } expiresAt ? GatewayUnixTime.Add(requestedAt, expiresAt - snapshotMs) : null;
    }

    // Mirrors the web UI's provider-usage aliases (src/infra/provider-usage.shared.ts).
    private static string CanonicalProviderId(string provider)
    {
        var normalized = provider.Trim().ToLowerInvariant();
        return normalized switch
        {
            "claude-cli" => "anthropic",
            "minimax-portal" or "minimax-cn" or "minimax-portal-cn" => "minimax",
            _ => normalized,
        };
    }

    // The web UI treats an auto-disabled job as an escalated failure, and an operator-paused job as not actionable.
    private static bool IsActiveFailure(bool enabled, JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object)
            return false;
        if (state.TryGetProperty("autoDisabled", out var autoDisabled) && autoDisabled.ValueKind == JsonValueKind.True)
            return true;
        var lastStatus = ReadString(state, "lastRunStatus") ?? ReadString(state, "lastStatus");
        return enabled && string.Equals(lastStatus, "error", StringComparison.Ordinal);
    }

    private static int Priority(string? status)
    {
        var index = Array.IndexOf(AuthStatusPriority, status);
        return index < 0 ? AuthStatusPriority.Length : index;
    }

    private sealed record AuthProfile(string? ProfileId, string? Type, string? Status, string? ReasonCode);

    private static List<AuthProfile> ReadProfiles(JsonElement provider)
    {
        if (!provider.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array)
            return [];
        return profiles.EnumerateArray()
            .Where(profile => profile.ValueKind == JsonValueKind.Object)
            .Select(profile => new AuthProfile(
                ReadString(profile, "profileId"),
                ReadString(profile, "type"),
                ReadString(profile, "status"),
                ReadString(profile, "reasonCode")))
            .ToList();
    }

    private static DateTimeOffset FromMs(long ms) => GatewayUnixTime.ToDateTimeOffset(ms);

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } text &&
        !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    // Every number read here is an epoch-ms time; one outside the representable range counts as missing.
    private static long? ReadInt64(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Number)
            return null;
        if (!value.TryGetInt64(out var ms))
        {
            var number = value.GetDouble();
            if (number is < long.MinValue or >= long.MaxValue)
                return null;
            ms = (long)number;
        }
        return GatewayUnixTime.IsValid(ms) ? ms : null;
    }
}
