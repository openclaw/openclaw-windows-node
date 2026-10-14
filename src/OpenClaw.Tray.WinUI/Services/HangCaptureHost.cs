using System;
using System.IO;

namespace OpenClawTray.Services;

internal sealed record HangCaptureHostResult(int ExitCode, string? ReceiptPath, string Message, bool DrainConfirmed = true);

/// <summary>
/// ISOLATED diagnostic host entrypoint (real, user-reachable - not a test-only static). It parses EXACT-target
/// arguments (pid + raw process-creation FILETIME ticks + thread id), runs the bounded external capture through
/// CompanionHangCapture (its own bounded worker; no frozen-UI dependency) and writes a bounded, redacted,
/// exclusively-named local receipt. It never activates the installed app, opens a listener, or registers a
/// service/watchdog/schedule.
/// </summary>
internal static class HangCaptureHost
{
    public const string Usage =
        "usage: OpenClaw.HangCapture --pid <n> --birth <filetime-ticks> --thread <n> --dir <receipt-dir> " +
        "[--wait-nodes <1..16>] [--frames <1..64>] [--budget-ms <50..10000>]";

    public static HangCaptureHostResult Run(string[] args, IUiThreadStackSource source, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(output);
        if (!TryParse(args ?? Array.Empty<string>(), out var target, out var directory, out var waitNodes,
                out var frames, out var budgetMs, out var error))
        {
            output.WriteLine(error);
            output.WriteLine(Usage);
            return new HangCaptureHostResult(2, null, error ?? "invalid arguments");
        }

        var receipt = CompanionHangCapture.Run(
            source, target, directory, waitNodes, frames, TimeSpan.FromMilliseconds(budgetMs));
        // LIFETIME: do not return from Main while ANY capture worker drains. Outer collectors AND the native
        // physical workers both register with CaptureDrain, so no exact-target cleanup (a future suspend/resume)
        // is abandoned at exit. The grace is BOUNDED and process-local; a hung native syscall cannot be forced,
        // and the result is COMMUNICATED rather than assumed.
        // Foreground drain: covers capture workers AND outstanding exact-target RESUME obligations, so Main cannot
        // exit while the target may still be suspended (unlike a plain WaitForIdle success).
        var drainConfirmed = CaptureHostLifetime.DrainBeforeExit(CaptureDrain.DefaultGrace);

        var message = receipt.Written
            ? $"receipt written: {receipt.Path}"
            : $"receipt not written: {receipt.Failure}";
        if (!drainConfirmed)
            message += " (worker drain NOT confirmed within the bounded grace)";
        output.WriteLine(message);
        return new HangCaptureHostResult(receipt.Written ? 0 : 1, receipt.Path, message, drainConfirmed);
    }

    private static bool TryParse(
        string[] args, out UiThreadTarget target, out string directory, out int waitNodes, out int frames,
        out int budgetMs, out string? error)
    {
        target = new UiThreadTarget(0, 0, 0);
        directory = string.Empty;
        waitNodes = UiThreadStackCollector.DefaultMaxWaitNodes;
        frames = UiThreadStackCollector.DefaultMaxFrames;
        budgetMs = (int)UiThreadStackCollector.DefaultDeadline.TotalMilliseconds;
        error = null;

        int? pid = null; long? birth = null; int? thread = null;
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (i + 1 >= args.Length) { error = $"missing value for {key}"; return false; }
            var value = args[++i];
            switch (key)
            {
                case "--pid": if (!int.TryParse(value, out var p) || p <= 0) { error = "--pid must be a positive integer"; return false; } pid = p; break;
                case "--birth": if (!long.TryParse(value, out var b) || b < 0) { error = "--birth must be non-negative FILETIME ticks"; return false; } birth = b; break;
                case "--thread": if (!int.TryParse(value, out var th) || th <= 0) { error = "--thread must be a positive integer"; return false; } thread = th; break;
                case "--dir": if (string.IsNullOrWhiteSpace(value)) { error = "--dir must not be empty"; return false; } directory = value; break;
                case "--wait-nodes": if (!int.TryParse(value, out var wn) || wn < 1) { error = "--wait-nodes must be >= 1"; return false; } waitNodes = wn; break;
                case "--frames": if (!int.TryParse(value, out var fr) || fr < 1) { error = "--frames must be >= 1"; return false; } frames = fr; break;
                case "--budget-ms": if (!int.TryParse(value, out var bm) || bm < 1) { error = "--budget-ms must be >= 1"; return false; } budgetMs = bm; break;
                default: error = $"unknown argument {key}"; return false;
            }
        }

        if (pid is null || birth is null || thread is null) { error = "--pid, --birth and --thread are required"; return false; }
        if (string.IsNullOrWhiteSpace(directory)) { error = "--dir is required"; return false; }

        // The collector clamps these to the FINITE HARD CAPS; the host forwards the requested values.
        target = new UiThreadTarget(pid.Value, birth.Value, thread.Value);
        return true;
    }
}