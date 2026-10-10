using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenClaw.Tray.WinUI.Diagnostics;

/// <summary>
/// U4 companion reliability: writes a local, credential-free hang report so a freeze
/// leaves evidence that can be correlated with companion and gateway logs. Bounded
/// collection, trigger sanitization and exclusive file creation live in the shared
/// <see cref="OpenClaw.Shared.HangCaptureReport"/> helper so they are unit-testable and
/// identical across consumers. This never restarts the gateway, the companion or any
/// migration worker and never reads configuration or credentials.
/// </summary>
internal static class CompanionHangCapture
{
    public static string Write(string directory, string trigger, IEnumerable<string> frames, DateTimeOffset nowUtc)
    {
        var bounded = OpenClaw.Shared.HangCaptureReport.CollectFrames(frames);
        var text = OpenClaw.Shared.HangCaptureReport.Format(trigger, nowUtc, bounded);
        return OpenClaw.Shared.HangCaptureReport.WriteExclusive(directory, text, nowUtc);
    }
}
