using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace OpenClaw.Shared;

/// <summary>
/// U4 companion reliability: formats a local, credential-free UI hang report so a
/// freeze leaves evidence that can be correlated with companion/gateway logs. This is
/// formatting only: it never restarts anything and never reads app configuration.
///
/// All collection is bounded up front: at most <see cref="DefaultMaxFrames"/> frames,
/// with per-frame and total character budgets enforced before any formatting or disk
/// write, so an unbounded or hostile enumerable can never be fully materialized.
/// </summary>
internal static class HangCaptureReport
{
    public const int DefaultMaxFrames = 40;
    public const int DefaultMaxCharsPerFrame = 512;
    public const int DefaultMaxTotalFrameChars = 16384;

    public const string UnknownTrigger = "unknown";

    private static readonly string[] SensitiveMarkers =
        { "token", "password", "secret", "authorization", "apikey", "api_key", "credential", "cookie" };

    // Only known, non-sensitive trigger reasons are emitted. Anything else - including a
    // caller-supplied reason that might embed a credential - collapses to "unknown".
    private static readonly HashSet<string> KnownTriggers = new(StringComparer.Ordinal)
    {
        "ui-thread-stall", "ui-freeze", "dispatcher-watchdog", "unresponsive", "manual", "unknown"
    };

    public static bool IsSensitiveLine(string? line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        foreach (var marker in SensitiveMarkers)
            if (line.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static int RedactedLineCount(IEnumerable<string>? frames)
        => frames is null ? 0 : frames.Count(f => IsSensitiveLine(f));

    /// <summary>Maps any caller-supplied reason to a known safe token.</summary>
    public static string NormalizeTrigger(string? trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger)) return UnknownTrigger;
        return KnownTriggers.Contains(trigger.Trim()) ? trigger.Trim() : UnknownTrigger;
    }

    /// <summary>
    /// Enumerates at most <paramref name="maxFrames"/> source items and enforces the
    /// per-frame and total character budgets. Stops pulling from the source as soon as a
    /// budget is met, so the whole sequence is never materialized.
    /// </summary>
    public static List<string> CollectFrames(
        IEnumerable<string>? frames,
        int maxFrames = DefaultMaxFrames,
        int maxCharsPerFrame = DefaultMaxCharsPerFrame,
        int maxTotalChars = DefaultMaxTotalFrameChars)
    {
        var result = new List<string>();
        if (frames is null) return result;
        var frameLimit = maxFrames < 0 ? 0 : maxFrames;
        var perLine = maxCharsPerFrame < 0 ? 0 : maxCharsPerFrame;
        var total = maxTotalChars < 0 ? 0 : maxTotalChars;
        if (frameLimit == 0 || total == 0) return result;

        var used = 0;
        using var enumerator = frames.GetEnumerator();
        // Check the budget BEFORE advancing so the source is never pulled beyond the cap.
        while (result.Count < frameLimit && used < total && enumerator.MoveNext())
        {
            var line = enumerator.Current ?? string.Empty;
            if (line.Length > perLine) line = line.Substring(0, perLine);
            var remaining = total - used;
            if (line.Length > remaining) line = line.Substring(0, remaining);
            result.Add(line);
            used += line.Length;
        }
        return result;
    }

    public static string Format(
        string trigger,
        DateTimeOffset whenUtc,
        IReadOnlyList<string> frames,
        int maxFrames = DefaultMaxFrames,
        int maxCharsPerFrame = DefaultMaxCharsPerFrame,
        int maxTotalChars = DefaultMaxTotalFrameChars)
    {
        var bounded = CollectFrames(frames, maxFrames, maxCharsPerFrame, maxTotalChars);
        var sb = new StringBuilder();
        sb.Append("openclaw-hang-capture/v1\n");
        sb.Append("trigger=").Append(NormalizeTrigger(trigger)).Append('\n');
        sb.Append("utc=").Append(whenUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("frames=").Append(bounded.Count).Append('\n');
        sb.Append("redacted=").Append(bounded.Count(IsSensitiveLine)).Append('\n');
        foreach (var line in bounded)
            sb.Append(IsSensitiveLine(line) ? "[redacted]" : line).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Writes a capture using exclusive creation under a second-resolution name so two
    /// captures in the same second never overwrite each other; an existing capture is
    /// always retained. Returns the path actually created.
    /// </summary>
    public static string WriteExclusive(string directory, string text, DateTimeOffset nowUtc, int maxNameAttempts = 1000)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("directory required", nameof(directory));
        Directory.CreateDirectory(directory);
        var stamp = nowUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var payload = text ?? string.Empty;
        var attempts = maxNameAttempts < 1 ? 1 : maxNameAttempts;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var name = attempt == 0 ? "hang-" + stamp + ".txt" : "hang-" + stamp + "-" + attempt.ToString("D3", CultureInfo.InvariantCulture) + ".txt";
            if (TryCreateExclusive(Path.Combine(directory, name), payload, out var created))
                return created;
        }

        // Every numeric candidate collided: fall back to a GUID name, still created exclusively.
        var guidName = "hang-" + stamp + "-" + Guid.NewGuid().ToString("N") + ".txt";
        if (TryCreateExclusive(Path.Combine(directory, guidName), payload, out var guidCreated))
            return guidCreated;

        throw new IOException("Could not create a unique hang capture in " + directory);
    }

    /// <summary>
    /// Creates <paramref name="path"/> exclusively and writes the payload.
    /// Returns false ONLY for a genuine name collision, in which case the existing capture
    /// is left untouched. Creation or write failures for any other reason propagate, and a
    /// partial capture left by a failed write is retained rather than silently retried.
    /// </summary>
    private static bool TryCreateExclusive(string path, string payload, out string createdPath)
    {
        createdPath = path;
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Name collision only: never overwrite an existing capture.
            return false;
        }

        // Creation succeeded. A write/flush failure now propagates (it is not reported as a
        // name collision) and the partial capture stays on disk for inspection.
        using (stream)
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(payload);
            writer.Flush();
        }
        return true;
    }
}
