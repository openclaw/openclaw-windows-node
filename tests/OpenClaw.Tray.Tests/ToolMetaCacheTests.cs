using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;
using System.Text.Json;
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

    [Fact]
    public async Task CacheToolMeta_ConcurrentAdds_FlushesCompleteValidJson()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        var bridge = new FakeBridge
        {
            History = new ChatHistoryInfo
            {
                SessionKey = "main",
                SessionId = "session-1"
            }
        };
        var provider = new OpenClawChatDataProvider(bridge, post: null, toolMetaCacheFilePath: cachePath);
        await provider.LoadHistoryAsync("main");

        Parallel.For(0, 100, i =>
            provider.CacheToolMeta("main", 1_000 + i, "bash", $"echo {i}"));

        await provider.DisposeAsync();

        var json = File.ReadAllText(cachePath);
        var cache = JsonSerializer.Deserialize<Dictionary<string, List<OpenClawChatDataProvider.CachedToolMeta>>>(json);

        Assert.NotNull(cache);
        Assert.True(cache!.TryGetValue("session-1", out var entries));
        Assert.Equal(100, entries!.Count);
        Assert.Empty(Directory.EnumerateFiles(tempDir.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task CacheToolMeta_PersistsReadableJsonWithoutUnicodeOrNewlineEscapes()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        var bridge = new FakeBridge
        {
            History = new ChatHistoryInfo
            {
                SessionKey = "main",
                SessionId = "session-1"
            }
        };
        var provider = new OpenClawChatDataProvider(bridge, post: null, toolMetaCacheFilePath: cachePath);
        await provider.LoadHistoryAsync("main");

        provider.CacheToolMeta(
            "main",
            1_000,
            "bash",
            "exec search \"duplicate\" -> {\"timestamp\":\"2025-01-01T00:00:00+00:00\",\"message\":\"line1\r\n      line2\"}");

        await provider.DisposeAsync();

        var json = File.ReadAllText(cachePath);
        var cache = JsonSerializer.Deserialize<Dictionary<string, List<OpenClawChatDataProvider.CachedToolMeta>>>(json);
        var entry = Assert.Single(cache!["session-1"]);

        Assert.DoesNotContain("\\u0022", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u002B", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\r\\n", json, StringComparison.Ordinal);
        Assert.Contains("+00:00", json, StringComparison.Ordinal);
        Assert.Contains("\\\"duplicate\\\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', entry.Label);
        Assert.DoesNotContain('\n', entry.Label);
        Assert.Contains("line1       line2", entry.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Constructor_DoesNotRewriteLegacyEscapedToolMetaCache()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        const string legacyJson = """
            {
              "session-1": [
                {
                  "Ts": 1000,
                  "ToolName": "bash",
                  "Label": "exec \u0022duplicate\u0022 at 2025-01-01T00:00:00\u002B00:00\r\n      next line"
                }
              ]
            }
            """;
        File.WriteAllText(cachePath, legacyJson);

        var provider = new OpenClawChatDataProvider(new FakeBridge(), post: null, toolMetaCacheFilePath: cachePath);
        await provider.DisposeAsync();

        var json = File.ReadAllText(cachePath);
        Assert.Equal(legacyJson, json);
        Assert.Contains("\\u0022", json, StringComparison.Ordinal);
        Assert.Contains("\\u002B", json, StringComparison.Ordinal);
        Assert.Contains("\\r\\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CacheToolMeta_WithoutSessionId_FallsBackToThreadKey()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        var provider = new OpenClawChatDataProvider(new FakeBridge(), post: null, toolMetaCacheFilePath: cachePath);

        provider.CacheToolMeta("main", 1_000, "bash", "echo after reset");

        await provider.DisposeAsync();

        var json = File.ReadAllText(cachePath);
        var cache = JsonSerializer.Deserialize<Dictionary<string, List<OpenClawChatDataProvider.CachedToolMeta>>>(json);

        Assert.NotNull(cache);
        Assert.True(cache!.TryGetValue("main", out var entries));
        var entry = Assert.Single(entries!);
        Assert.Equal("bash", entry.ToolName);
        Assert.Equal("echo after reset", entry.Label);
    }

    [Fact]
    public void TryMatch_NormalizesLegacyCachedNewlines()
    {
        var cache = new Queue<OpenClawChatDataProvider.CachedToolMeta>();
        cache.Enqueue(Meta(100, "bash\r\nname", "line1\r\n      \"line2\""));

        var result = OpenClawChatDataProvider.TryMatchCachedTool(cache, 200);

        Assert.Equal("bash name", result!.ToolName);
        Assert.Equal("line1       \"line2\"", result.Label);
    }

    [Fact]
    public async Task Reset_DoesNotReseedClearedSessionIdFromStaleSessionsList()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        var bridge = new FakeBridge
        {
            History = new ChatHistoryInfo
            {
                SessionKey = "main",
                SessionId = "old-session"
            }
        };
        var provider = new OpenClawChatDataProvider(bridge, post: null, toolMetaCacheFilePath: cachePath);
        await provider.LoadHistoryAsync("main");

        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });
        bridge.RaiseSessions(new[]
        {
            new SessionInfo { Key = "main", IsMain = true, SessionId = "old-session" }
        });
        provider.CacheToolMeta("main", 1_000, "bash", "echo after reset");

        await provider.DisposeAsync();

        var json = File.ReadAllText(cachePath);
        var cache = JsonSerializer.Deserialize<Dictionary<string, List<OpenClawChatDataProvider.CachedToolMeta>>>(json);

        Assert.NotNull(cache);
        Assert.False(cache!.ContainsKey("old-session"));
        Assert.True(cache.TryGetValue("main", out var entries));
        Assert.Equal("echo after reset", Assert.Single(entries!).Label);
    }

    [Fact]
    public async Task Reset_PersistsClearedToolMetaWhenCacheWasClean()
    {
        using var tempDir = new TempDirectory();
        var cachePath = Path.Combine(tempDir.DirectoryPath, "tool-metadata.json");
        const string initialJson = """
            {
              "old-session": [
                {
                  "Ts": 1000,
                  "ToolName": "bash",
                  "Label": "stale tool"
                }
              ]
            }
            """;
        File.WriteAllText(cachePath, initialJson);
        var bridge = new FakeBridge
        {
            History = new ChatHistoryInfo
            {
                SessionKey = "main",
                SessionId = "old-session"
            }
        };
        var provider = new OpenClawChatDataProvider(bridge, post: null, toolMetaCacheFilePath: cachePath);
        await provider.LoadHistoryAsync("main");

        bridge.RaiseSessionCommandCompleted(new SessionCommandResult
        {
            Method = "sessions.reset",
            Ok = true,
            Key = "main"
        });
        await provider.DisposeAsync();

        var json = File.ReadAllText(cachePath);
        var cache = JsonSerializer.Deserialize<Dictionary<string, List<OpenClawChatDataProvider.CachedToolMeta>>>(json);

        Assert.NotEqual(initialJson, json);
        Assert.NotNull(cache);
        Assert.DoesNotContain("old-session", cache!.Keys);
    }

    private sealed class FakeBridge : IChatGatewayBridge
    {
        public bool IsConnected { get; set; }
        public ConnectionStatus CurrentStatus { get; set; }
        public string? MainSessionKey { get; set; }
        public bool HasHandshakeSnapshot { get; set; }
        public ChatHistoryInfo History { get; set; } = new() { SessionKey = "main" };

        public SessionInfo[] GetSessionList() => Array.Empty<SessionInfo>();
        public ModelsListInfo? GetCurrentModelsList() => null;
        public void StartProactiveBootstrap() { }
        public Task<CommandCatalog> ListCommandsAsync(CommandCatalogQuery? query = null) => Task.FromResult(new CommandCatalog { IsSupported = true });
        public Task SendChatMessageAsync(string message, string? sessionKey, string? sessionId, IReadOnlyList<ChatAttachment>? attachments = null) => Task.CompletedTask;
        public Task<ChatSendResult> SendChatMessageForRunAsync(string message, string? sessionKey, string? sessionId, IReadOnlyList<ChatAttachment>? attachments = null) => Task.FromResult(new ChatSendResult());
        public Task PatchSessionModelAsync(string sessionKey, string model) => Task.CompletedTask;
        public Task ClearSessionModelAsync(string sessionKey) => Task.CompletedTask;
        public Task PatchSessionThinkingLevelAsync(string sessionKey, string thinkingLevel) => Task.CompletedTask;
        public Task<ChatHistoryInfo> RequestChatHistoryAsync(string? sessionKey) => Task.FromResult(History);
        public Task SendChatAbortAsync(string runId, string? sessionKey = null) => Task.CompletedTask;
        public Task ResolveExecApprovalAsync(string approvalId, string decision) => Task.CompletedTask;
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<SessionInfo[]>? SessionsUpdated;
        public event EventHandler<SessionCommandResult>? SessionCommandCompleted;
        public event EventHandler<ChatMessageInfo>? ChatMessageReceived;
        public event EventHandler<AgentEventInfo>? AgentEventReceived;
        public event EventHandler<ModelsListInfo>? ModelsListUpdated;
        public void RaiseStatus(ConnectionStatus status) => StatusChanged?.Invoke(this, status);
        public void RaiseSessions(SessionInfo[] sessions) => SessionsUpdated?.Invoke(this, sessions);
        public void RaiseSessionCommandCompleted(SessionCommandResult result) => SessionCommandCompleted?.Invoke(this, result);
        public void RaiseChat(ChatMessageInfo message) => ChatMessageReceived?.Invoke(this, message);
        public void RaiseAgent(AgentEventInfo evt) => AgentEventReceived?.Invoke(this, evt);
        public void RaiseModels(ModelsListInfo models) => ModelsListUpdated?.Invoke(this, models);
        public void Dispose() { }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "openclaw-tool-meta-" + Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(DirectoryPath);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(DirectoryPath))
                    Directory.Delete(DirectoryPath, recursive: true);
            }
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            catch
            {
                // Test cleanup is best-effort.
            }
        }
    }
}
