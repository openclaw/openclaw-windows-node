using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class HangCaptureReportTests
{
    [Fact] public void Formats_header_and_frames()
    {
        var text = HangCaptureReport.Format("ui-thread-stall", DateTimeOffset.UnixEpoch, new[] { "MainFrame", "ChildFrame" });
        Assert.Contains("openclaw-hang-capture/v1", text);
        Assert.Contains("trigger=ui-thread-stall", text);
        Assert.Contains("frames=2", text);
        Assert.Contains("MainFrame", text);
    }

    [Fact] public void Redacts_sensitive_lines()
    {
        var text = HangCaptureReport.Format("t", DateTimeOffset.UnixEpoch, new[] { "ok", "Authorization: Bearer ***" });
        Assert.Contains("[redacted]", text);
        Assert.DoesNotContain("Bearer", text);
        Assert.Equal(1, HangCaptureReport.RedactedLineCount(new[] { "ok", "my api_key=xyz" }));
    }

    [Fact] public void Respects_max_frames()
    {
        var text = HangCaptureReport.Format("t", DateTimeOffset.UnixEpoch, new[] { "a", "b", "c" }, maxFrames: 1);
        Assert.Contains("frames=1", text);
    }

    // --- U4 bounded-budget + safe-trigger repairs (fail on prior code) ---

    [Fact] public void Truncates_overlong_frame_to_per_frame_budget()
    {
        var text = HangCaptureReport.Format("t", DateTimeOffset.UnixEpoch, new[] { new string('A', 5000) });
        var frameLine = text.Split('\n').Single(l => l.Length > 0 && l.All(c => c == 'A'));
        Assert.Equal(HangCaptureReport.DefaultMaxCharsPerFrame, frameLine.Length);
    }

    [Fact] public void Stops_at_total_character_budget()
    {
        var frames = Enumerable.Range(0, 200).Select(_ => new string('B', 500)).ToArray();
        var text = HangCaptureReport.Format("t", DateTimeOffset.UnixEpoch, frames);
        var emitted = text.Split('\n').Where(l => l.Length > 0 && l.All(c => c == 'B')).Sum(l => l.Length);
        Assert.True(emitted <= HangCaptureReport.DefaultMaxTotalFrameChars, "emitted=" + emitted);
        Assert.True(emitted > 0);
    }

    [Fact] public void Normalizes_unknown_trigger_so_reason_cannot_leak()
    {
        var text = HangCaptureReport.Format("token=SUPERSECRET", DateTimeOffset.UnixEpoch, new[] { "ok" });
        Assert.Contains("trigger=unknown", text);
        Assert.DoesNotContain("SUPERSECRET", text);
    }

    [Fact] public void Keeps_known_trigger()
    {
        Assert.Equal("ui-freeze", HangCaptureReport.NormalizeTrigger("ui-freeze"));
        Assert.Equal("unknown", HangCaptureReport.NormalizeTrigger("something-else"));
    }

    [Fact] public void CollectFrames_stops_enumerating_after_max_frames()
    {
        var pulled = 0;
        IEnumerable<string> Source()
        {
            for (var i = 0; i < 1000; i++) { pulled++; yield return "frame-" + i; }
        }

        var collected = HangCaptureReport.CollectFrames(Source(), maxFrames: 3);
        Assert.Equal(3, collected.Count);
        Assert.Equal(3, pulled);
    }

    [Fact] public void CollectFrames_applies_per_line_and_total_budgets()
    {
        var collected = HangCaptureReport.CollectFrames(
            new[] { new string('C', 1000), "short" },
            maxFrames: 10,
            maxCharsPerFrame: 10,
            maxTotalChars: 12);
        Assert.Equal(10, collected[0].Length);
        Assert.Equal(2, collected[1].Length);
    }

    [Fact] public void WriteExclusive_never_overwrites_a_colliding_capture()
    {
        var dir = Directory.CreateTempSubdirectory("hangcap-").FullName;
        try
        {
            var when = DateTimeOffset.UnixEpoch;
            var first = HangCaptureReport.WriteExclusive(dir, "first", when);
            var second = HangCaptureReport.WriteExclusive(dir, "second", when);

            Assert.NotEqual(first, second);
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
            Assert.Equal("first", File.ReadAllText(first));
            Assert.Equal("second", File.ReadAllText(second));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WriteExclusive_final_collision_uses_exclusive_guid_name_never_overwrites()
    {
        var dir = Directory.CreateTempSubdirectory("hangcap-").FullName;
        try
        {
            var when = DateTimeOffset.UnixEpoch;
            var stamp = when.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");
            var existing = Path.Combine(dir, "hang-" + stamp + ".txt");
            File.WriteAllText(existing, "existing");

            // Bound the numeric candidates to one, which is already taken: the only correct
            // outcome is a NEW exclusively-created file, and the existing capture is kept.
            var created = HangCaptureReport.WriteExclusive(dir, "new", when, maxNameAttempts: 1);

            Assert.NotEqual(existing, created);
            Assert.True(File.Exists(created));
            Assert.Equal("existing", File.ReadAllText(existing));
            Assert.Equal("new", File.ReadAllText(created));
            Assert.Equal(2, Directory.GetFiles(dir).Length);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WriteExclusive_propagates_non_collision_creation_failure()
    {
        var dir = Directory.CreateTempSubdirectory("hangcap-").FullName;
        try
        {
            var when = DateTimeOffset.UnixEpoch;
            var stamp = when.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");
            // A directory (not a file) occupies the only candidate name: File.Exists is false,
            // so this is not a collision and must surface instead of being retried/ignored.
            var blockedPath = Path.Combine(dir, "hang-" + stamp + ".txt");
            Directory.CreateDirectory(blockedPath);

            var failure = Record.Exception(() => HangCaptureReport.WriteExclusive(dir, "x", when, maxNameAttempts: 1));
            // Windows reports a directory target as access denied; Unix reports an I/O error.
            // Both must propagate without being treated as a reusable filename collision.
            Assert.True(failure is IOException or UnauthorizedAccessException,
                "Expected a filesystem creation failure, got " + failure?.GetType().FullName);
            Assert.True(Directory.Exists(blockedPath));
            Assert.Empty(Directory.GetFiles(dir));
            Assert.Single(Directory.GetDirectories(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
