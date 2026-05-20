using OpenClawTray.Chat;
using Xunit;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Tests for the tool metadata cache matching logic used to recover tool
/// names/labels after gateway history flattening.
/// </summary>
public class ToolMetaCacheTests
{
    private static OpenClawChatDataProvider.CachedToolMeta Meta(long ts, string tool, string label) =>
        new() { Ts = ts, ToolName = tool, Label = label };

    // ── TryMatchCachedTool ──

    [Fact]
    public void TryMatch_NullCache_ReturnsNull()
    {
        Assert.Null(OpenClawChatDataProvider.TryMatchCachedTool(null, 1000));
    }

    [Fact]
    public void TryMatch_EmptyCache_ReturnsNull()
    {
        var cache = new Queue<OpenClawChatDataProvider.CachedToolMeta>();
        Assert.Null(OpenClawChatDataProvider.TryMatchCachedTool(cache, 1000));
    }

    [Fact]
    public void TryMatch_SingleEntry_DequeuesAndReturns()
    {
        var cache = new Queue<OpenClawChatDataProvider.CachedToolMeta>();
        cache.Enqueue(Meta(100, "bash", "ls -la"));

        var result = OpenClawChatDataProvider.TryMatchCachedTool(cache, 200);

        Assert.NotNull(result);
        Assert.Equal("bash", result!.ToolName);
        Assert.Equal("ls -la", result.Label);
        Assert.Empty(cache); // consumed
    }

    [Fact]
    public void TryMatch_SequentialOrder_MatchesByPosition()
    {
        var cache = new Queue<OpenClawChatDataProvider.CachedToolMeta>();
        cache.Enqueue(Meta(100, "bash", "first"));
        cache.Enqueue(Meta(200, "grep", "second"));
        cache.Enqueue(Meta(300, "view", "third"));

        // Each call should dequeue the next entry regardless of timestamp
        var r1 = OpenClawChatDataProvider.TryMatchCachedTool(cache, 500);
        var r2 = OpenClawChatDataProvider.TryMatchCachedTool(cache, 600);
        var r3 = OpenClawChatDataProvider.TryMatchCachedTool(cache, 700);

        Assert.Equal("bash", r1!.ToolName);
        Assert.Equal("grep", r2!.ToolName);
        Assert.Equal("view", r3!.ToolName);
        Assert.Empty(cache);
    }

    [Fact]
    public void TryMatch_MoreHistoryThanCache_ReturnsNullWhenExhausted()
    {
        var cache = new Queue<OpenClawChatDataProvider.CachedToolMeta>();
        cache.Enqueue(Meta(100, "bash", "only entry"));

        var r1 = OpenClawChatDataProvider.TryMatchCachedTool(cache, 200);
        var r2 = OpenClawChatDataProvider.TryMatchCachedTool(cache, 300);

        Assert.NotNull(r1);
        Assert.Null(r2); // exhausted
    }

    [Fact]
    public void TryMatch_CachedEntryFarAfterHistory_SkipsMatch()
    {
        // Cache entry is >5 minutes (300_000ms) after the history entry —
        // means this history tool result predates the cache.
        var cache = new Queue<OpenClawChatDataProvider.CachedToolMeta>();
        cache.Enqueue(Meta(500_000, "bash", "future entry"));

        var result = OpenClawChatDataProvider.TryMatchCachedTool(cache, 100_000);

        Assert.Null(result);
        Assert.Single(cache); // NOT consumed — entry stays for later
    }

    [Fact]
    public void TryMatch_CachedEntrySlightlyAfterHistory_StillMatches()
    {
        // Cache entry is <5 min after history — normal SSE delay, should match.
        var cache = new Queue<OpenClawChatDataProvider.CachedToolMeta>();
        cache.Enqueue(Meta(200_000, "bash", "recent entry"));

        var result = OpenClawChatDataProvider.TryMatchCachedTool(cache, 100_000);

        Assert.NotNull(result);
        Assert.Equal("bash", result!.ToolName);
    }

    [Fact]
    public void TryMatch_ZeroTimestamps_AlwaysMatch()
    {
        // When timestamps are 0, the guard is skipped — always dequeue.
        var cache = new Queue<OpenClawChatDataProvider.CachedToolMeta>();
        cache.Enqueue(Meta(0, "bash", "no timestamp"));

        var result = OpenClawChatDataProvider.TryMatchCachedTool(cache, 0);

        Assert.NotNull(result);
    }

    [Fact]
    public void TryMatch_RepeatedToolNames_PreservesOrder()
    {
        // Multiple entries with the same tool name should be matched in order.
        var cache = new Queue<OpenClawChatDataProvider.CachedToolMeta>();
        cache.Enqueue(Meta(100, "bash", "first bash"));
        cache.Enqueue(Meta(200, "bash", "second bash"));
        cache.Enqueue(Meta(300, "bash", "third bash"));

        var r1 = OpenClawChatDataProvider.TryMatchCachedTool(cache, 500);
        var r2 = OpenClawChatDataProvider.TryMatchCachedTool(cache, 600);

        Assert.Equal("first bash", r1!.Label);
        Assert.Equal("second bash", r2!.Label);
    }

    // ── Constants ──

    [Fact]
    public void SessionLimits_AreReasonable()
    {
        Assert.Equal(20, OpenClawChatDataProvider.MaxCachedSessions);
        Assert.Equal(500, OpenClawChatDataProvider.MaxToolEntriesPerSession);
    }
}
