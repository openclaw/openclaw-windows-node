using System.Diagnostics;
using System.Globalization;

namespace OpenClaw.SetupEngine;

/// <summary>Deadline for one packaged <c>openclaw</c> CLI call during native Gateway setup
/// (config validate/set, health, device pairing). Each call pays packaged Node/CLI startup,
/// not just the Gateway RPC.</summary>
internal static class NativeGatewayCliTimeout
{
    /// <summary>Environment variable overriding <see cref="DefaultTimeout"/>. Unset or blank
    /// silently keeps the default. A positive integer from 1 through 2147483 is seconds.
    /// Any other value warns and keeps the default. The name stays under the shared log
    /// sanitizer's 43-character base64url redaction so a warning keeps the variable name.</summary>
    internal const string TimeoutEnvironmentVariable = "OPENCLAW_SETUP_NATIVE_CLI_TIMEOUT";

    /// <summary>Default per-command bound. An isolated-session <c>config validate</c> on ARM64
    /// (package 2026.10.900.0) was observed taking about 131 seconds, past the former
    /// 2-minute cap; 4 minutes covers that with margin.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(4);

    /// <summary>Resolves the per-command deadline from <see cref="TimeoutEnvironmentVariable"/>.</summary>
    internal static TimeSpan ResolveTimeout()
    {
        var raw = Environment.GetEnvironmentVariable(TimeoutEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultTimeout;

        if (int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
            seconds > 0 &&
            seconds <= int.MaxValue / 1000)
            return TimeSpan.FromSeconds(seconds);

        Trace.TraceWarning(
            "Ignoring {0}='{1}': expected a positive integer of at most {2}s. Using the default {3}-minute native Gateway CLI timeout.",
            TimeoutEnvironmentVariable,
            raw,
            int.MaxValue / 1000,
            (int)DefaultTimeout.TotalMinutes);
        return DefaultTimeout;
    }
}
