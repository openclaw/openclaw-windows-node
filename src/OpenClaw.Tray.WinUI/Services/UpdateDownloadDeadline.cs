using System.Diagnostics;
using System.Globalization;

namespace OpenClawTray.Services;

/// <summary>
/// Bounds one portable update download. Header arrival does not count as
/// progress, so a stall after the headers still ends at this deadline.
/// </summary>
internal static class UpdateDownloadDeadline
{
    /// <summary>Seconds override for <see cref="DefaultTimeout"/>. Unset or blank
    /// keeps the default. A positive integer of at most <c>int.MaxValue / 1000</c>
    /// is accepted. Any other value keeps the default.</summary>
    internal const string TimeoutEnvironmentVariable = "OPENCLAW_UPDATE_DOWNLOAD_TIMEOUT";

    /// <summary>Overall bound for one update download, including a body that
    /// sends headers and then stalls.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    internal static TimeSpan Resolve() =>
        Resolve(Environment.GetEnvironmentVariable(TimeoutEnvironmentVariable));

    internal static TimeSpan Resolve(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultTimeout;

        if (int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
            seconds > 0 &&
            seconds <= int.MaxValue / 1000)
            return TimeSpan.FromSeconds(seconds);

        Trace.TraceWarning(
            "Ignoring {0}='{1}': expected a positive integer of at most {2}s. Using the default {3}-minute update download timeout.",
            TimeoutEnvironmentVariable,
            raw,
            int.MaxValue / 1000,
            (int)DefaultTimeout.TotalMinutes);
        return DefaultTimeout;
    }
}
