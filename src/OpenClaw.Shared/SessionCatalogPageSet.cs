using System;
using System.Collections.Generic;

namespace OpenClaw.Shared;

internal enum PageSetDecision
{
    Continue,
    Complete,
    Failed
}

/// <summary>
/// Bounded complete page-set acquisition for sessions.list.
///
/// Installed Gateway contract (session-utils-list-Cjeh9QIy.mjs): request opts.limit/opts.offset;
/// response count (window rows), totalCount (filtered inventory), hasMore, nextOffset
/// (offset + rows, or null when done). The Gateway emits NO server inventory revision / etag,
/// so a multi-page union cannot be proven to be a stable snapshot.
///
/// Consequence: only a SINGLE-page complete inventory may delete absent held keys. A multi-page
/// set merges rows and retains the held catalog (see AllowsDeletion). Bounds (pages/rows/bytes/
/// deadline) and continuation checks (advancing offsets, stable totals, no duplicates, valid
/// terminal) reject a bad acquisition so the caller preserves the prior catalog.
/// </summary>
internal sealed class SessionCatalogPageSet
{
    public const int DefaultPageSize = 200;
    public const int DefaultMaxPages = 20;
    public const int DefaultMaxRows = 2000;
    public const long DefaultMaxBytes = 2_000_000;
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(8);

    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly HashSet<int> _offsetsSeen = new();
    private readonly long _startedMs;
    private readonly long _deadlineMs;
    private readonly long _maxBytes;
    private readonly int _maxPages;
    private readonly int _maxRows;

    public SessionCatalogPageSet(
        string? scope,
        long generation,
        long nowMs,
        int maxPages = DefaultMaxPages,
        int maxRows = DefaultMaxRows,
        long maxBytes = DefaultMaxBytes,
        TimeSpan? deadline = null)
    {
        Scope = scope;
        Generation = generation;
        _startedMs = nowMs;
        _maxPages = maxPages;
        _maxRows = maxRows;
        _maxBytes = maxBytes;
        _deadlineMs = (long)(deadline ?? DefaultDeadline).TotalMilliseconds;
    }

    public string? Scope { get; }
    public long Generation { get; }
    public int Pages { get; private set; }
    public int Rows { get; private set; }
    public long Bytes { get; private set; }
    public int? ExpectedTotalCount { get; private set; }

    /// <summary>Offset of the page the caller should request next, or null when done.</summary>
    public int? NextOffset { get; private set; }

    public PageSetDecision Decision { get; private set; } = PageSetDecision.Continue;
    public string? Failure { get; private set; }

    /// <summary>Only a proven single-page complete inventory may remove absent held keys.</summary>
    public bool AllowsDeletion => Decision == PageSetDecision.Complete && Pages == 1;

    /// <summary>Feeds one received page and returns the next decision.</summary>
    public PageSetDecision Accept(
        SessionCatalogRefresh refresh,
        IReadOnlyCollection<string> admittedKeys,
        int requestedOffset,
        long rawBytes,
        long nowMs)
    {
        if (Decision != PageSetDecision.Continue) return Decision;
        if (nowMs - _startedMs > _deadlineMs) return Fail("acquisition deadline exceeded");
        if (Pages >= _maxPages) return Fail("max pages exceeded");
        if (Rows + admittedKeys.Count > _maxRows) return Fail("max rows exceeded");
        if (Bytes + rawBytes > _maxBytes) return Fail("max bytes exceeded");
        if (refresh.Kind == SessionCatalogRefreshKind.Unknown) return Fail("unqualified page");
        if (refresh.HasInvalidPagingMetadata) return Fail("invalid paging metadata");

        if (refresh.Offset is int responseOffset && responseOffset != requestedOffset) return Fail("unexpected page offset");
        if (!_offsetsSeen.Add(requestedOffset)) return Fail("repeated page offset");

        foreach (var key in admittedKeys)
        {
            if (!_keys.Add(key)) return Fail("duplicate key across pages");
        }

        // A page that declares itself complete must agree with the rows we admitted here.
        if (refresh.Kind == SessionCatalogRefreshKind.CompleteInventory && refresh.ObservedRows != admittedKeys.Count)
            return Fail("declared/admitted row count mismatch");

        if (refresh.TotalCount is int total)
        {
            if (ExpectedTotalCount is null) ExpectedTotalCount = total;
            else if (ExpectedTotalCount.Value != total) return Fail("changing totalCount");
        }

        Pages++;
        Rows += admittedKeys.Count;
        Bytes += rawBytes;

        if (!refresh.HasMore && refresh.NextOffset is null)
        {
            // A terminal page must not claim completeness with a contradictory cumulative count.
            if (ExpectedTotalCount is int expectedTotal && Rows != expectedTotal)
                return Fail("terminal cumulative rows do not match totalCount");
            Decision = PageSetDecision.Complete;
            NextOffset = null;
            return Decision;
        }

        if (refresh.NextOffset is int next && next > requestedOffset && !_offsetsSeen.Contains(next))
        {
            NextOffset = next;
            return PageSetDecision.Continue;
        }

        return Fail("malformed or nonadvancing continuation");
    }

    private PageSetDecision Fail(string reason)
    {
        Decision = PageSetDecision.Failed;
        Failure = reason;
        NextOffset = null;
        return Decision;
    }
}
