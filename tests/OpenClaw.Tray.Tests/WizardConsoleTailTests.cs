using System.Text.Json;
using OpenClaw.SetupEngine.UI;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Tests for <see cref="WizardConsoleTail.TryExtractConsoleMessage"/>, the
/// JSON-line filter used by the console-tail mitigation. Only lines emitted by
/// the root "openclaw" logger via console.log should be surfaced; everything
/// else is noise.
/// </summary>
public class WizardConsoleTailTests
{
    private static JsonElement Payload(long cursor, long size, params string[] lines)
        => PayloadFromFile("agent-wizard.log", cursor, size, lines);

    private static JsonElement PayloadFromFile(string file, long cursor, long size, params string[] lines)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            file, cursor, size, lines
        }));
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task GatewayReaderSendsBoundedLogsTailRequestsWithoutANullCursor()
    {
        var requests = new List<JsonElement>();
        var reader = WizardConsoleTail.CreateGatewayLogReader((method, parameters, timeout) =>
        {
            Assert.Equal("logs.tail", method);
            Assert.Equal(10_000, timeout);
            requests.Add(JsonSerializer.SerializeToElement(parameters));
            return Task.FromResult(Payload(100, 100));
        });

        await reader(null, CancellationToken.None);
        await reader(100, CancellationToken.None);

        Assert.False(requests[0].TryGetProperty("cursor", out _));
        Assert.Equal(1, requests[0].GetProperty("maxBytes").GetInt32());
        Assert.Equal(100, requests[1].GetProperty("cursor").GetInt64());
        Assert.Equal(128, requests[1].GetProperty("limit").GetInt32());
        Assert.Equal(64 * 1024, requests[1].GetProperty("maxBytes").GetInt32());
    }

    [Fact]
    public async Task IsolatedTailUsesAuthenticatedCursorAndOnlyDisplaysNewPluginConsoleLines()
    {
        string oauth = """{"_meta":{"name":"openclaw","path":{"method":"console.log"}},"message":"Open https://auth.example/authorize?client_id=fixture"}""";
        string unrelated = """{"_meta":{"name":"openclaw/auth","path":{"method":"console.log"}},"message":"not user-facing"}""";
        var requested = new List<long?>();
        var delivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tail = new WizardConsoleTail(gatewayLogTail: (cursor, _) =>
        {
            requested.Add(cursor);
            return Task.FromResult(cursor is null
                ? Payload(100, 100, oauth)
                : Payload(300, 300, unrelated, oauth));
        });

        await tail.StartGatewayAsync(message => delivered.TrySetResult(message),
            _ => { }, CancellationToken.None);
        string received = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tail.Stop();

        Assert.Contains("https://auth.example/authorize", received, StringComparison.Ordinal);
        Assert.Equal([null, 100], requested.Take(2));
    }

    [Fact]
    public void IsolatedTailMarksSkippedOutputAndRejectsMalformedCursors()
    {
        string oauth = """{"_meta":{"name":"openclaw","path":{"method":"console.log"}},"message":"OAuth URL available"}""";
        using JsonDocument gap = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            file = "agent-wizard.log",
            cursor = 250,
            size = 250,
            reset = true,
            truncated = true,
            skippedBytes = 47,
            lines = new[] { oauth }
        }));

        var batch = WizardConsoleTail.ParseGatewayLogTail(gap.RootElement, previousCursor: 100);

        Assert.True(batch.Skipped);
        Assert.Equal(250, batch.Cursor);
        Assert.Equal(["OAuth URL available"], batch.Messages);
        Assert.Throws<InvalidDataException>(() =>
            WizardConsoleTail.ParseGatewayLogTail(Payload(50, 50), previousCursor: 100));
        using JsonDocument invalid = JsonDocument.Parse(
            """{"file":"agent-wizard.log","cursor":"50","size":50,"lines":[]}""");
        Assert.Throws<InvalidDataException>(() =>
            WizardConsoleTail.ParseGatewayLogTail(invalid.RootElement, previousCursor: null));
        using JsonDocument invalidGap = JsonDocument.Parse(
            """{"file":"agent-wizard.log","cursor":50,"size":50,"reset":"yes","lines":[]}""");
        Assert.Throws<InvalidDataException>(() =>
            WizardConsoleTail.ParseGatewayLogTail(invalidGap.RootElement, previousCursor: 100));
    }

    [Fact]
    public async Task IsolatedTailReportsAVisibleGapBeforeLaterConsoleMessages()
    {
        string oauth = """{"_meta":{"name":"openclaw","path":{"method":"console.log"}},"message":"Open https://auth.example/authorize"}""";
        var messages = new List<string>();
        var issues = new List<GatewayLogTailIssue>();
        var receivedBoth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tail = new WizardConsoleTail(gatewayLogTail: (cursor, _) =>
        {
            if (cursor is null)
                return Task.FromResult(Payload(100, 100));
            using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                file = "agent-wizard.log",
                cursor = 250,
                size = 250,
                reset = true,
                truncated = true,
                skippedBytes = 47,
                lines = new[] { oauth }
            }));
            return Task.FromResult(document.RootElement.Clone());
        });

        await tail.StartGatewayAsync(
            message =>
            {
                messages.Add(message);
                if (issues.Count == 1)
                    receivedBoth.TrySetResult();
            },
            issue => issues.Add(issue),
            CancellationToken.None);
        await receivedBoth.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tail.Stop();

        Assert.Equal([GatewayLogTailIssue.Skipped], issues);
        Assert.Equal(["Open https://auth.example/authorize"], messages);
    }

    [Fact]
    public async Task IsolatedTailRejectsAChangedGatewayLogSource()
    {
        string oauth = """{"_meta":{"name":"openclaw","path":{"method":"console.log"}},"message":"Unrelated OAuth URL"}""";
        var issue = new TaskCompletionSource<GatewayLogTailIssue>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var tail = new WizardConsoleTail(gatewayLogTail: (cursor, _) =>
            Task.FromResult(cursor is null
                ? PayloadFromFile("first-agent.log", 100, 100)
                : PayloadFromFile("different-agent.log", 200, 200, oauth)));

        await tail.StartGatewayAsync(
            _ => throw new InvalidOperationException("A replaced log must not be projected."),
            value => issue.TrySetResult(value),
            CancellationToken.None);
        GatewayLogTailIssue result = await issue.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tail.Stop();

        Assert.Equal(GatewayLogTailIssue.Skipped, result);
    }

    [Fact]
    public async Task IsolatedTailDoesNotStartWslWhenLogRpcIsUnavailable()
    {
        using var tail = new WizardConsoleTail(gatewayLogTail: (_, _) =>
            Task.FromException<JsonElement>(new InvalidOperationException("operator.read unavailable")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tail.StartGatewayAsync(_ => { }, _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task IsolatedTailSurfacesPostStartRpcFailure()
    {
        var issue = new TaskCompletionSource<GatewayLogTailIssue>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var tail = new WizardConsoleTail(gatewayLogTail: (cursor, _) =>
            cursor is null
                ? Task.FromResult(Payload(100, 100))
                : Task.FromException<JsonElement>(new IOException("agent log unavailable")));

        await tail.StartGatewayAsync(
            _ => throw new InvalidOperationException("A failed RPC must not invent a console message."),
            value => issue.TrySetResult(value),
            CancellationToken.None);
        GatewayLogTailIssue result = await issue.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tail.Stop();

        Assert.Equal(GatewayLogTailIssue.Unavailable, result);
    }

    [Fact]
    public void ExtractsOAuthUrlFromUpstreamConsoleLogEntry()
    {
        // Verbatim shape of the gateway-side line that carries the OAuth URL
        // for the OpenAI Codex Browser path.
        var line = """{"0":"discarded","_meta":{"name":"openclaw","logLevelName":"INFO","path":{"method":"console.log"}},"message":"\nOpen this URL in your LOCAL browser:\n\nhttps://auth.openai.com/oauth/authorize?response_type=code&client_id=app_EMoamEEZ73f0CkXaXp7hrann"}""";

        var extracted = WizardConsoleTail.TryExtractConsoleMessage(line);

        Assert.NotNull(extracted);
        Assert.Contains("https://auth.openai.com/oauth/authorize", extracted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsolatedTailRetriesTransientFailureWithoutLosingItsCursor(bool reconnect)
    {
        var delivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cursors = new List<long?>();
        var issues = new List<GatewayLogTailIssue>();
        using var tail = new WizardConsoleTail(gatewayLogTail: (cursor, _) =>
        {
            cursors.Add(cursor);
            if (cursor is null)
                return Task.FromResult(Payload(100, 100));
            if (cursors.Count == 2)
                return Task.FromException<JsonElement>(reconnect
                    ? new InvalidOperationException("handshake pending")
                    : new TimeoutException("transient"));
            return Task.FromResult(Payload(200, 200,
                """{"_meta":{"name":"openclaw","path":{"method":"console.log"}},"message":"new OAuth instructions"}"""));
        });

        await tail.StartGatewayAsync(message => delivered.TrySetResult(message), issues.Add, default);
        Assert.Equal("new OAuth instructions", await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        tail.Stop();
        Assert.Equal(new long?[] { null, 100, 100 }, cursors);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task IsolatedTailRetriesInitialAnchorBeforeDisplayingOnlyNewConsoleOutput()
    {
        var delivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cursors = new List<long?>();
        var issues = new List<GatewayLogTailIssue>();
        using var tail = new WizardConsoleTail(gatewayLogTail: (cursor, _) =>
        {
            cursors.Add(cursor);
            if (cursors.Count == 1)
                return Task.FromException<JsonElement>(new TimeoutException("initial timeout"));
            return Task.FromResult(cursor is null
                ? Payload(100, 100,
                    """{"_meta":{"name":"openclaw","path":{"method":"console.log"}},"message":"stale instructions"}""")
                : Payload(200, 200,
                    """{"_meta":{"name":"openclaw","path":{"method":"console.log"}},"message":"new instructions"}"""));
        });

        await tail.StartGatewayAsync(message => delivered.TrySetResult(message), issues.Add, default);
        Assert.Equal("new instructions", await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        tail.Stop();
        Assert.Equal(new long?[] { null, null, 100 }, cursors);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task IsolatedTailBoundsInitialAnchorRetries()
    {
        int calls = 0;
        using var tail = new WizardConsoleTail(gatewayLogTail: (cursor, _) =>
        {
            Assert.Null(cursor);
            Interlocked.Increment(ref calls);
            return Task.FromException<JsonElement>(new TimeoutException("initial timeout"));
        });

        await Assert.ThrowsAsync<TimeoutException>(() =>
            tail.StartGatewayAsync(_ => Assert.Fail("No output before anchoring."),
                _ => Assert.Fail("Startup failures must reach the wizard recovery handler."), default));

        Assert.Equal(3, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task IsolatedTailCancelsInitialAnchorRetryWithoutAnotherRequest()
    {
        int calls = 0;
        using var cancellation = new CancellationTokenSource();
        using var tail = new WizardConsoleTail(gatewayLogTail: (_, _) =>
        {
            Interlocked.Increment(ref calls);
            cancellation.Cancel();
            return Task.FromException<JsonElement>(new TimeoutException("initial timeout"));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tail.StartGatewayAsync(_ => Assert.Fail("Cancelled tail emitted output."),
                _ => Assert.Fail("Cancellation is not a console failure."), cancellation.Token));

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task IsolatedTailBoundsRetriesAndKeepsFailureVisible()
    {
        var exhausted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var tail = new WizardConsoleTail(gatewayLogTail: (cursor, _) =>
        {
            Interlocked.Increment(ref calls);
            return cursor is null ? Task.FromResult(Payload(100, 100))
                : Task.FromException<JsonElement>(new IOException("unavailable"));
        });
        await tail.StartGatewayAsync(_ => Assert.Fail("No synthetic console output."), issue =>
        {
            Assert.Equal(GatewayLogTailIssue.Unavailable, issue);
            exhausted.TrySetResult();
        }, default);
        await exhausted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task StoppingDuringInitialReadCancelsWithoutDereferencingDisposedSource()
    {
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tail = new WizardConsoleTail(gatewayLogTail: (_, _) => response.Task);
        var start = tail.StartGatewayAsync(_ => Assert.Fail("Stopped tail emitted output."), _ => { }, default);
        tail.Stop();
        response.SetResult(Payload(100, 100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
    }

    [Fact]
    public void ExtractsCodexVersionFallbackMessage()
    {
        // Silent fallback during npm install of @openclaw/codex.
        var line = """{"_meta":{"name":"openclaw","logLevelName":"INFO","path":{"method":"console.log"}},"message":"Resolved @openclaw/codex to @openclaw/codex@2026.6.1, but that version is incompatible with this OpenClaw runtime; using newest compatible @openclaw/codex@2026.5.28"}""";

        var extracted = WizardConsoleTail.TryExtractConsoleMessage(line);

        Assert.NotNull(extracted);
        Assert.Contains("incompatible", extracted);
    }

    [Fact]
    public void StripsAnsiSequencesFromQrConsoleOutput()
    {
        var line = """
            {"_meta":{"name":"openclaw","logLevelName":"INFO","path":{"method":"console.log"}},"message":"\u001b[47m\u001b[30m██  ▄▄  ██\u001b[0m"}
            """;

        var extracted = WizardConsoleTail.TryExtractConsoleMessage(line);

        Assert.Equal("██  ▄▄  ██", extracted);
    }

    [Fact]
    public void PreservesUtf8QrBlockCharacters()
    {
        var line = """
            {"_meta":{"name":"openclaw","logLevelName":"INFO","path":{"method":"console.log"}},"message":"Open WhatsApp and scan:\n████ ▄▄ ████"}
            """;

        var extracted = WizardConsoleTail.TryExtractConsoleMessage(line);

        Assert.NotNull(extracted);
        Assert.Contains("████ ▄▄ ████", extracted);
    }

    [Fact]
    public void DetectsTerminalQrArt()
    {
        var qr = string.Join('\n', Enumerable.Repeat(" ███████  ▄▄▄  ▄▄▄      ▄  ▄  ▄▄   ▄    ▄  ▄▄   ▄▄▄ ", 12));

        Assert.True(WizardConsoleTail.LooksLikeTerminalQrArt(qr));
    }

    [Fact]
    public void DetectsTerminalQrArtWithSideBlockGlyphs()
    {
        var qr = string.Join('\n', Enumerable.Repeat("▌██  ▐▌ ▄▄ ▐▌ ██▐▌  ▀▀ ▐▌", 8));

        Assert.True(WizardConsoleTail.LooksLikeTerminalQrArt(qr));
    }

    [Fact]
    public void DoesNotTreatRegularMultilineConsoleOutputAsQrArt()
    {
        var message = """
            Waiting for WhatsApp connection...
            Open the WhatsApp app, go to Linked Devices, then scan this QR:
            Docs: https://docs.openclaw.ai/whatsapp
            """;

        Assert.False(WizardConsoleTail.LooksLikeTerminalQrArt(message));
    }

    [Fact]
    public void IgnoresStructuredSubsystemLogs()
    {
        // openclaw/auth, openclaw/ws, gateway/ws etc. write structured records
        // via Logger.info(); they go through the same log file but have a
        // different _meta.path.method (not console.log) and different name.
        var line = """{"_meta":{"name":"openclaw/auth","logLevelName":"INFO","path":{"method":"info"}},"message":"device token rotated"}""";

        Assert.Null(WizardConsoleTail.TryExtractConsoleMessage(line));
    }

    [Fact]
    public void IgnoresNonOpenclawNamedConsoleLog()
    {
        // Defense in depth: only the root openclaw logger should surface to the
        // wizard banner. A console.log from some other subsystem stays internal.
        var line = """{"_meta":{"name":"openclaw/ws","logLevelName":"INFO","path":{"method":"console.log"}},"message":"internal noise"}""";

        Assert.Null(WizardConsoleTail.TryExtractConsoleMessage(line));
    }

    [Fact]
    public void IgnoresMalformedJson()
    {
        Assert.Null(WizardConsoleTail.TryExtractConsoleMessage("{not json at all"));
        Assert.Null(WizardConsoleTail.TryExtractConsoleMessage("plain text line"));
    }

    [Fact]
    public void IgnoresNullEmptyOrWhitespace()
    {
        Assert.Null(WizardConsoleTail.TryExtractConsoleMessage(null));
        Assert.Null(WizardConsoleTail.TryExtractConsoleMessage(""));
        Assert.Null(WizardConsoleTail.TryExtractConsoleMessage("   "));
    }

    [Fact]
    public void IgnoresLineWithoutMessageField()
    {
        var line = """{"_meta":{"name":"openclaw","logLevelName":"INFO","path":{"method":"console.log"}}}""";

        Assert.Null(WizardConsoleTail.TryExtractConsoleMessage(line));
    }

    [Fact]
    public void IgnoresLineWithEmptyMessage()
    {
        var line = """{"_meta":{"name":"openclaw","logLevelName":"INFO","path":{"method":"console.log"}},"message":"   "}""";

        Assert.Null(WizardConsoleTail.TryExtractConsoleMessage(line));
    }

    [Fact]
    public void IgnoresLineMissingMeta()
    {
        // A log file from a different process or a corrupted line should never
        // crash the filter.
        var line = """{"message":"console.log without _meta"}""";

        Assert.Null(WizardConsoleTail.TryExtractConsoleMessage(line));
    }

    [Fact]
    public void CheapRejectionFastPathStillAcceptsValidLines()
    {
        // Sanity: the fast-path string check looks for "console.log". Ensure a
        // valid line passes it.
        var line = """{"_meta":{"name":"openclaw","logLevelName":"WARN","path":{"method":"console.log"}},"message":"install failed: npm ENOSPC"}""";

        var extracted = WizardConsoleTail.TryExtractConsoleMessage(line);

        Assert.Equal("install failed: npm ENOSPC", extracted);
    }
}
