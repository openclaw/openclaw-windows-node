using System.Text.Json;

namespace OpenClawTray.Infrastructure.Hosting.Devtools;

/// <summary>
/// <c>reactor.logs</c> — drains the in-process log capture ring buffer. The
/// buffer is installed early in <c>TryRunDevtools</c> so startup output is
/// preserved even when an agent attaches late. Long-poll: pass
/// <c>waitMs &gt; 0</c> to block until new entries arrive or the timeout
/// elapses. Reload preserves the buffer (same process) so post-reload logs
/// line up with pre-reload context.
/// </summary>
internal static class DevtoolsLogsTool
{
    public static void Register(DevtoolsMcpServer server, Func<LogCaptureBuffer?> getBuffer)
    {
        server.Tools.Register(
            new McpToolDescriptor(
                Name: "logs",
                Description:
                    "Drains captured Console.Out, Console.Error, and Debug.WriteLine/Trace.WriteLine output. " +
                    "Returns entries with monotonic `seq`; for incremental reads pass the previous " +
                    "response's `nextSeq` directly as `since` (inclusive). " +
                    "Filters: `tail` (keep last N after filtering), `filter` (regex; falls back to substring), " +
                    "`source` (stdout|stderr|debug — Debug and Trace share one listener), `level`. " +
                    "Pass `waitMs > 0` to long-poll. `dropped` reports entries evicted by ring overflow.",
                InputSchema: new
                {
                    type = "object",
                    properties = new
                    {
                        since = new { type = "integer", description = "Return entries with seq >= since. Pass previous `nextSeq` to continue. Default 0 returns all." },
                        tail = new { type = "integer", description = "Keep only the last N entries after filtering." },
                        filter = new { type = "string", description = "Regex; falls back to substring match if invalid." },
                        source = new { type = "string", description = "stdout | stderr | debug (Debug and Trace both surface as `debug`)" },
                        level = new { type = "string", description = "Exact level match (case-insensitive). Reserved for future structured sinks." },
                        waitMs = new { type = "integer", description = "Max time to block waiting for new entries. 0 returns immediately." },
                    },
                    additionalProperties = false,
                }),
            @params => BuildPayload(getBuffer(), @params));
    }

    /// <summary>
    /// Core handler — public so tests can assert shape without spinning up an
    /// HTTP listener. Mirrors the <see cref="DevtoolsStateTool.BuildPayload"/>
    /// pattern.
    /// </summary>
    internal static object BuildPayload(LogCaptureBuffer? buf, JsonElement? @params)
    {
        if (buf is null)
            throw new McpToolException(
                "Log capture is disabled (--devtools-logs off).",
                JsonRpcErrorCodes.ToolExecution,
                new { code = "logs-disabled" });

        long since = DevtoolsTools.ReadLong(@params, "since") ?? 0;
        int? tail = DevtoolsTools.ReadInt(@params, "tail");
        string? filter = DevtoolsTools.ReadString(@params, "filter");
        string? sourceStr = DevtoolsTools.ReadString(@params, "source");
        string? level = DevtoolsTools.ReadString(@params, "level");
        int waitMs = DevtoolsTools.ReadInt(@params, "waitMs") ?? 0;

        LogSource? source = sourceStr?.ToLowerInvariant() switch
        {
            null or "" => null,
            "stdout" => LogSource.Stdout,
            "stderr" => LogSource.Stderr,
            // "trace" is accepted as an alias for "debug" because
            // Debug/Trace share one listener collection — we can't
            // distinguish them at the source.
            "debug" or "trace" => LogSource.Debug,
            _ => throw new McpToolException(
                $"Unknown source '{sourceStr}'. Use stdout, stderr, or debug.",
                JsonRpcErrorCodes.InvalidParams),
        };

        if (waitMs > 0)
        {
            // Clamp so a buggy agent can't pin an HTTP worker forever. We
            // never touch the UI thread here, so dispatcher-timeout is not
            // the guard.
            var clamped = Math.Min(waitMs, 30_000);
            buf.WaitForNewAsync(since, clamped).GetAwaiter().GetResult();
        }

        var result = buf.Query(sinceSeq: since, tail: tail, filterRegex: filter, source: source, level: level);

        return new
        {
            entries = result.Entries.Select(e => new
            {
                seq = e.Seq,
                ts = e.TimestampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                source = e.Source.ToString().ToLowerInvariant(),
                level = e.Level,
                threadId = e.ThreadId,
                text = e.Text,
            }).ToArray(),
            nextSeq = result.NextSeq,
            dropped = result.Dropped,
            capacityBytes = buf.CapacityBytes,
        };
    }
}
