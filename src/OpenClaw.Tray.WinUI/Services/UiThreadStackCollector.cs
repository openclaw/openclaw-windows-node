using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace OpenClawTray.Services;

/// <summary>
/// EXACT identity of the UI thread to inspect: process id + process CREATION time + thread id.
/// <para>
/// <see cref="ProcessBirthTimeUtcTicks"/> is the RAW process-creation tick count as returned by
/// <c>GetProcessTimes</c> (100 ns units since 1601-01-01T00:00:00Z, i.e. FILETIME ticks) - it is compared
/// directly against the same raw value from the native API, and is NEVER interpreted as a .NET
/// <see cref="System.DateTime"/> tick (which counts from 0001-01-01). A recycled pid with a different raw
/// creation tick is a DIFFERENT process and must never be captured as if it were the original.
/// </para>
/// </summary>
internal sealed record UiThreadTarget(int ProcessId, long ProcessBirthTimeUtcTicks, int ThreadId)
{
    public string Describe() => $"pid={ProcessId} birth=filetime:{ProcessBirthTimeUtcTicks} tid={ThreadId}";
}

internal sealed record WaitChainNodeView(int ThreadId, int ProcessId, string State, string? Module, string? Function);

internal sealed record StackFrameView(string Module, string Function);

internal enum UiThreadCaptureStatus
{
    /// <summary>The target was revalidated and at least one bounded view was captured.</summary>
    Collected,
    /// <summary>The target could not be revalidated (recycled pid / thread not in that process / gone).</summary>
    IdentityMismatch,
    /// <summary>The target was revalidated but no view could be captured; see Notes for the receipt.</summary>
    Unavailable,
}

internal sealed record UiThreadStackReport(
    UiThreadCaptureStatus Status,
    string Target,
    IReadOnlyList<WaitChainNodeView> WaitChain,
    IReadOnlyList<StackFrameView> Frames,
    bool Truncated,
    IReadOnlyList<string> Notes);

/// <summary>
/// Source of bounded wait-chain / stack data for an exact UI-thread target. Implementations run EXTERNAL to the
/// target's own message loop: a frozen UI thread cannot service its own button, so capture must not depend on the
/// frozen thread making progress. Sources must NEVER throw (the collector also catches).
/// </summary>
internal interface IUiThreadStackSource
{
    /// <summary>Confirms the target still exists with the SAME raw creation ticks and that the thread belongs to
    /// it, holding ONE opened identity for the whole capture. Returns false with a receipt on any mismatch.</summary>
    bool TryRevalidate(UiThreadTarget target, out string? failure);

    bool TryCaptureWaitChain(
        UiThreadTarget target, int maxNodes, TimeSpan deadline,
        out IReadOnlyList<WaitChainNodeView> nodes, out bool truncated, out string? failure);

    bool TryCaptureStack(
        UiThreadTarget target, int maxFrames, TimeSpan deadline,
        out IReadOnlyList<StackFrameView> frames, out bool truncated, out string? failure);
}

/// <summary>
/// Bounded UI-thread stack + wait-chain collector. Enforces FINITE HARD CAPS (a caller can never request an
/// unbounded native buffer), a BOUNDED end-to-end deadline shared across both captures, exact identity
/// revalidation first (PID-reuse guard), and sanitize+cap on EVERY branch (notes, target, state, module,
/// function, and the final rendered section). It NEVER throws: a failed/short/timeout capture becomes an explicit
/// receipt.
/// </summary>
internal sealed class UiThreadStackCollector
{
    /// <summary>Native Wait Chain Traversal hard maximum (WCT_MAX_NODE_COUNT).</summary>
    public const int HardMaxWaitNodes = 16;
    public const int HardMaxFrames = 64;
    public const int DefaultMaxWaitNodes = HardMaxWaitNodes;
    public const int DefaultMaxFrames = HardMaxFrames;
    internal const int MaxNameChars = 256;
    internal const int MaxNotes = 16;
    internal const int MaxSectionChars = 64 * 1024;
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MinDeadline = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan MaxDeadline = TimeSpan.FromSeconds(10);

    private readonly IUiThreadStackSource _source;
    private readonly int _maxWaitNodes;
    private readonly int _maxFrames;
    private readonly TimeSpan _deadline;

    public UiThreadStackCollector(
        IUiThreadStackSource source,
        int maxWaitNodes = DefaultMaxWaitNodes,
        int maxFrames = DefaultMaxFrames,
        TimeSpan? deadline = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        // FINITE HARD CAPS: a caller (e.g. int.MaxValue) can never enlarge the native buffer past the WCT limit.
        _maxWaitNodes = Math.Clamp(maxWaitNodes, 1, HardMaxWaitNodes);
        _maxFrames = Math.Clamp(maxFrames, 1, HardMaxFrames);
        _deadline = ClampDeadline(deadline);
    }

    /// <summary>Clamps a requested deadline into the bounded range [MinDeadline, MaxDeadline].</summary>
    public static TimeSpan ClampDeadline(TimeSpan? requested)
    {
        var value = requested ?? DefaultDeadline;
        return value < MinDeadline ? MinDeadline : value > MaxDeadline ? MaxDeadline : value;
    }

    public UiThreadStackReport Collect(UiThreadTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var startedAt = Stopwatch.GetTimestamp();

        bool revalidated;
        string? revalidationFailure;
        try
        {
            revalidated = _source.TryRevalidate(target, out revalidationFailure);
        }
        catch (Exception exception)
        {
            revalidated = false;
            revalidationFailure = $"identity revalidation threw {exception.GetType().Name}";
        }
        if (!revalidated)
        {
            // EVERY branch is sanitized AND capped, including this early identity receipt.
            var receipt = BoundNote(revalidationFailure ?? "target identity could not be revalidated");
            return new UiThreadStackReport(
                UiThreadCaptureStatus.IdentityMismatch, BoundNote(target.Describe()),
                Array.Empty<WaitChainNodeView>(), Array.Empty<StackFrameView>(), false,
                new[] { receipt });
        }

        var truncated = false;
        var notes = new List<string>();
        IReadOnlyList<WaitChainNodeView> waitChain = Array.Empty<WaitChainNodeView>();
        IReadOnlyList<StackFrameView> frames = Array.Empty<StackFrameView>();

        if (RemainingMilliseconds(startedAt) > 0)
        {
            try
            {
                if (_source.TryCaptureWaitChain(target, _maxWaitNodes, Remaining(startedAt), out var nodes, out var nodesTruncated, out var waitFailure))
                {
                    waitChain = RedactWaitChain(nodes.Take(_maxWaitNodes));
                    truncated |= nodesTruncated || nodes.Count > _maxWaitNodes;
                }
                else
                {
                    notes.Add($"wait chain unavailable: {waitFailure ?? "no detail"}");
                }
            }
            catch (Exception exception)
            {
                notes.Add($"wait chain capture failed: {exception.GetType().Name}");
            }
        }
        else
        {
            notes.Add("capture deadline exceeded before the wait chain");
        }

        // The SAME end-to-end deadline is shared: the stack gets only what is LEFT, not a fresh full budget.
        if (RemainingMilliseconds(startedAt) > 0)
        {
            try
            {
                if (_source.TryCaptureStack(target, _maxFrames, Remaining(startedAt), out var captured, out var framesTruncated, out var stackFailure))
                {
                    frames = RedactFrames(captured.Take(_maxFrames));
                    truncated |= framesTruncated || captured.Count > _maxFrames;
                }
                else
                {
                    notes.Add($"stack unavailable: {stackFailure ?? "no detail"}");
                }
            }
            catch (Exception exception)
            {
                notes.Add($"stack capture failed: {exception.GetType().Name}");
            }
        }
        else
        {
            notes.Add("capture deadline exceeded before the stack");
        }

        var status = waitChain.Count + frames.Count > 0
            ? UiThreadCaptureStatus.Collected
            : UiThreadCaptureStatus.Unavailable;
        return new UiThreadStackReport(
            status, BoundNote(target.Describe()), waitChain, frames, truncated, BoundNotes(notes));
    }

    private TimeSpan Remaining(long startedAt)
    {
        var remaining = _deadline - Stopwatch.GetElapsedTime(startedAt);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private double RemainingMilliseconds(long startedAt) => Remaining(startedAt).TotalMilliseconds;

    private static IReadOnlyList<string> BoundNotes(List<string> notes) =>
        notes.Take(MaxNotes).Select(BoundNote).ToArray();

    private static IReadOnlyList<WaitChainNodeView> RedactWaitChain(IEnumerable<WaitChainNodeView> nodes) =>
        nodes.Select(node => new WaitChainNodeView(
            node.ThreadId, node.ProcessId,
            BoundNote(node.State),
            node.Module is null ? null : BoundNote(node.Module),
            node.Function is null ? null : BoundNote(node.Function))).ToArray();

    private static IReadOnlyList<StackFrameView> RedactFrames(IEnumerable<StackFrameView> frames) =>
        frames.Select(frame => new StackFrameView(BoundNote(frame.Module), BoundNote(frame.Function))).ToArray();

    /// <summary>Sanitizes AND caps to <see cref="MaxNameChars"/> (truncate, sanitize, truncate again).</summary>
    private static string BoundNote(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var truncated = value.Length > MaxNameChars ? value[..MaxNameChars] : value;
        var sanitized = DiagnosticsExportSanitizer.SanitizeTextBlock(truncated);
        return sanitized.Length > MaxNameChars ? sanitized[..MaxNameChars] : sanitized;
    }

    /// <summary>Bounded, redacted section text for the diagnostics bundle (sanitized AND capped as a whole).</summary>
    public static string FormatSection(UiThreadStackReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.Append("Target: ").AppendLine(report.Target);
        builder.Append("Status: ").AppendLine(report.Status.ToString());
        builder.Append("Truncated: ").AppendLine(report.Truncated ? "yes" : "no");
        foreach (var note in report.Notes)
            builder.Append("Note: ").AppendLine(note);

        builder.Append("Wait chain (").Append(report.WaitChain.Count).AppendLine("):");
        foreach (var node in report.WaitChain)
        {
            builder.Append("  tid=").Append(node.ThreadId)
                .Append(" pid=").Append(node.ProcessId)
                .Append(" state=").Append(node.State)
                .Append(" module=").Append(node.Module ?? "-")
                .Append(" function=").AppendLine(node.Function ?? "-");
        }

        builder.Append("Stack frames (").Append(report.Frames.Count).AppendLine("):");
        foreach (var frame in report.Frames)
            builder.Append("  ").Append(frame.Module).Append('!').AppendLine(frame.Function);

        // Defense in depth: sanitize the WHOLE rendered section and cap it.
        var text = DiagnosticsExportSanitizer.SanitizeTextBlock(builder.ToString());
        return text.Length > MaxSectionChars
            ? text[..MaxSectionChars] + Environment.NewLine + "[truncated section]"
            : text;
    }
}