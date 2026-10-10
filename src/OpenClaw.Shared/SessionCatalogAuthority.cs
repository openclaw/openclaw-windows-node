using System.Text.Json;

namespace OpenClaw.Shared;

/// <summary>
/// U2/catalog companion reliability (page-vs-total authority + consistency gates).
///
/// Installed Gateway sessions.list contract (session-utils-list-Cjeh9QIy.mjs):
///   count      -> rows returned in THIS window (page size, not the inventory total)
///   totalCount -> FILTERED inventory total
///   hasMore    -> nextOffset &lt; filtered.length
///   nextOffset -> offset + rows, or NULL when the page set is done
///
/// An absent-keys deletion may only happen on a verified complete inventory window.
/// Completeness is trusted only when the declared metadata AGREES with the rows actually
/// returned and admitted. A present positive nextOffset (even with hasMore false), a
/// declared count/total that disagrees, an admitted-count mismatch, or present-but-invalid
/// paging metadata all withhold deletion authority.
///
/// Field-specific nullability: null nextOffset is valid TERMINAL metadata (page set done).
/// Null count/totalCount/offset are NOT accepted - they remain present-but-invalid.
/// </summary>
internal enum SessionCatalogRefreshKind
{
    /// No usable paging metadata: completeness is not proven.
    Unknown = 0,
    /// A verified complete inventory window (metadata agrees with observed rows).
    CompleteInventory = 1,
    /// A partial/inconsistent window: merge rows, preserve held keys.
    PartialPage = 2
}

internal readonly struct SessionCatalogRefresh
{
    public SessionCatalogRefreshKind Kind { get; init; }

    /// <summary>Rows actually observed in the returned payload (never a declared count).</summary>
    public int ObservedRows { get; init; }

    public int? DeclaredCount { get; init; }
    public int? TotalCount { get; init; }
    public bool HasMore { get; init; }
    public bool HasMorePresent { get; init; }
    public int? NextOffset { get; init; }
    public int? Offset { get; init; }
    public bool HasInvalidPagingMetadata { get; init; }

    /// <summary>True only when absent held keys may be removed.</summary>
    public bool AllowsDeletionOfAbsentKeys => Kind == SessionCatalogRefreshKind.CompleteInventory;
}

internal static class SessionCatalogAuthority
{
    /// <param name="admittedKeyCount">
    /// Keys the caller actually admitted from this payload (-1 when unknown). Used to detect
    /// a window whose metadata claims more rows than we could admit.
    /// </param>
    internal static SessionCatalogRefresh Read(JsonElement envelope, int admittedKeyCount = -1)
    {
        if (envelope.ValueKind == JsonValueKind.Array)
        {
            var rows = envelope.GetArrayLength();
            var arrayKind = rows == 0
                ? SessionCatalogRefreshKind.Unknown
                : admittedKeyCount >= 0 && admittedKeyCount != rows
                    ? SessionCatalogRefreshKind.PartialPage
                    : SessionCatalogRefreshKind.CompleteInventory;
            return new SessionCatalogRefresh { ObservedRows = rows, Kind = arrayKind };
        }

        if (envelope.ValueKind != JsonValueKind.Object)
            return new SessionCatalogRefresh { Kind = SessionCatalogRefreshKind.Unknown };

        var hasInner = envelope.TryGetProperty("sessions", out var inner) && inner.ValueKind == JsonValueKind.Object;
        var innerObj = hasInner ? inner : default;
        var scope = hasInner ? inner : envelope;

        var observedRows = CountRows(envelope, innerObj);

        var invalid = false;
        // nextOffset may be null (valid terminal); count/totalCount/offset may NOT.
        var declaredCount = ReadPagingInt(scope, envelope, "count", allowNull: false, ref invalid);
        var totalCount = ReadPagingInt(scope, envelope, "totalCount", allowNull: false, ref invalid);
        var nextOffset = ReadPagingInt(scope, envelope, "nextOffset", allowNull: true, ref invalid);
        var offset = ReadPagingInt(scope, envelope, "offset", allowNull: false, ref invalid);
        var hasMore = ReadPagingBool(scope, envelope, "hasMore", ref invalid, out var hasMorePresent);

        var kind = Classify(observedRows, admittedKeyCount, declaredCount, totalCount, hasMore, nextOffset, offset, invalid);

        return new SessionCatalogRefresh
        {
            Kind = kind,
            ObservedRows = observedRows,
            DeclaredCount = declaredCount,
            TotalCount = totalCount,
            HasMore = hasMore,
            HasMorePresent = hasMorePresent,
            NextOffset = nextOffset,
            Offset = offset,
            HasInvalidPagingMetadata = invalid
        };
    }

    private static SessionCatalogRefreshKind Classify(
        int observedRows, int admittedKeyCount, int? declaredCount, int? totalCount,
        bool hasMore, int? nextOffset, int? offset, bool invalid)
    {
        // Present-but-invalid paging metadata must not silently become "absent" and grant deletion.
        if (invalid) return SessionCatalogRefreshKind.PartialPage;
        if (hasMore) return SessionCatalogRefreshKind.PartialPage;
        if (offset is > 0) return SessionCatalogRefreshKind.PartialPage;
        // A present positive nextOffset implies a pending page, even if hasMore says false.
        if (nextOffset is > 0) return SessionCatalogRefreshKind.PartialPage;
        // A declared count must agree with the rows actually returned.
        if (declaredCount.HasValue && declaredCount.Value != observedRows) return SessionCatalogRefreshKind.PartialPage;
        // The (filtered) inventory total must agree with the returned rows for a complete window.
        if (totalCount.HasValue && totalCount.Value != observedRows) return SessionCatalogRefreshKind.PartialPage;
        // When completeness is declared, the rows must also be admissible.
        if ((declaredCount.HasValue || totalCount.HasValue) && admittedKeyCount >= 0 && admittedKeyCount != observedRows)
            return SessionCatalogRefreshKind.PartialPage;
        if (totalCount.HasValue) return SessionCatalogRefreshKind.CompleteInventory;
        return declaredCount.HasValue || observedRows > 0
            ? SessionCatalogRefreshKind.CompleteInventory
            : SessionCatalogRefreshKind.Unknown;
    }

    private static int CountRows(JsonElement envelope, JsonElement inner)
    {
        if (inner.ValueKind == JsonValueKind.Array) return inner.GetArrayLength();
        if (inner.ValueKind == JsonValueKind.Object) return CountObjectRows(inner);
        if (envelope.TryGetProperty("sessions", out var rows))
        {
            if (rows.ValueKind == JsonValueKind.Array) return rows.GetArrayLength();
            if (rows.ValueKind == JsonValueKind.Object) return CountObjectRows(rows);
        }
        return 0;
    }

    private static int CountObjectRows(JsonElement obj)
    {
        var count = 0;
        foreach (var prop in obj.EnumerateObject())
        {
            if (IsMetaKey(prop.Name)) continue;
            count++;
        }
        return count;
    }

    internal static bool IsMetaKey(string name)
        => name is "recent" or "count" or "totalCount" or "hasMore" or "nextOffset" or "offset" or "path" or "defaults" or "ts";

    /// <summary>Reads a paging integer. Null is valid only where <paramref name="allowNull"/> is set.</summary>
    private static int? ReadPagingInt(JsonElement primary, JsonElement secondary, string name, bool allowNull, ref bool invalid)
    {
        if (TryGetProperty(primary, name, out var prop) || TryGetProperty(secondary, name, out prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var value) && value >= 0)
                return value;
            if (allowNull && prop.ValueKind == JsonValueKind.Null)
                return null;
            invalid = true;
            return null;
        }
        return null;
    }

    /// <summary>Reads a paging bool, marking present-but-invalid (non-bool, including null) values.</summary>
    private static bool ReadPagingBool(JsonElement primary, JsonElement secondary, string name, ref bool invalid, out bool present)
    {
        present = false;
        if (TryGetProperty(primary, name, out var prop) || TryGetProperty(secondary, name, out prop))
        {
            present = true;
            if (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False)
                return prop.GetBoolean();
            invalid = true;
            return false;
        }
        return false;
    }

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out value);
    }

    /// <summary>
    /// Non-mutating scan of a sessions.list payload that mirrors the client's row-admission
    /// rules. Used to validate a page (rows/keys/metadata) BEFORE any catalog mutation.
    /// </summary>
    internal static (int ObservedRows, List<string> AdmittedKeys) ScanPage(JsonElement envelope)
    {
        var keys = new List<string>();
        var rows = envelope;
        if (envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("sessions", out var inner))
            rows = inner;

        if (rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in rows.EnumerateArray())
            {
                var key = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String
                    ? (k.GetString() ?? "unknown")
                    : "unknown";
                keys.Add(key);
            }
            return (keys.Count, keys);
        }

        if (rows.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in rows.EnumerateObject())
            {
                var name = prop.Name;
                if (IsMetaKey(name)) continue;
                if (!name.Equals("global", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains(':') && !name.Contains("agent") && !name.Contains("session")) continue;
                var item = prop.Value;
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString() ?? "";
                    if (s.StartsWith("/", StringComparison.Ordinal) || s.Contains("/.", StringComparison.Ordinal)) continue;
                }
                else if (item.ValueKind == JsonValueKind.Number) continue;
                keys.Add(name);
            }
            return (keys.Count, keys);
        }

        return (0, keys);
    }
}
