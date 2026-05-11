using System;

namespace OpenClawTray.Services;

/// <summary>
/// Builds the activity-stream "details" string for completed node invocations.
///
/// Extracted from App.OnNodeInvokeCompleted so the formatter can be unit-tested
/// without spinning up the WinUI App. Drives both the recent-activity menu and
/// <see cref="ActivityStreamService.BuildSupportBundle"/>.
///
/// **Privacy invariant:** for privacy-sensitive commands (mic / camera /
/// screen) a failed invocation never includes the underlying error text in
/// details, since support bundles can be shared off-device. Caller-supplied
/// args (e.g., language tag) and runtime details (audio/video stack errors)
/// stay in the local log only.
/// </summary>
internal static class NodeInvokeActivityFormatter
{
    public const string PrivacySensitive = "privacy-sensitive";
    public const string Exec = "exec";
    public const string Metadata = "metadata";

    // Also classify tts.* as privacy-sensitive: failed TTS calls can
    // surface ElevenLabs API key fragments (in 401 messages) or audio-stack
    // errors. Keeping the namespace under the same scrubbing rule as stt.*
    // means recent-activity / support-bundle entries record only the
    // privacy class + duration + sanitized "error" sentinel.
    public static string GetPrivacyClass(string command)
    {
        if (string.IsNullOrEmpty(command)) return Metadata;

        if (command.StartsWith("stt.", StringComparison.OrdinalIgnoreCase) ||
            command.StartsWith("tts.", StringComparison.OrdinalIgnoreCase))
        {
            return PrivacySensitive;
        }

        if (string.Equals(command, "screen.record", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "screen.snapshot", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "camera.snap", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "camera.clip", StringComparison.OrdinalIgnoreCase))
        {
            return PrivacySensitive;
        }

        if (command.StartsWith("system.run", StringComparison.OrdinalIgnoreCase))
        {
            return Exec;
        }

        return Metadata;
    }

    public static string BuildDetails(string command, bool ok, int durationMs, string? error)
    {
        var privacyClass = GetPrivacyClass(command);
        durationMs = Math.Max(0, durationMs);

        if (ok)
            return $"{privacyClass} · {durationMs} ms";

        if (string.Equals(privacyClass, PrivacySensitive, StringComparison.Ordinal))
        {
            // See class summary: never echo error text for privacy-sensitive
            // commands. Full detail stays in the local log.
            return $"{privacyClass} · {durationMs} ms · error";
        }

        return $"{privacyClass} · {durationMs} ms · {error ?? "unknown error"}";
    }
}
