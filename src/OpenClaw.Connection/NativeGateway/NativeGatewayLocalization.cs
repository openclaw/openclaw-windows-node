// Localization indirection for user-facing native Gateway endpoint messages.
//
// OpenClaw.Connection has no WinUI resource access, so these messages resolve through a delegate
// that the app points at its resource lookup at startup. Hosts that never set it, such as tests
// and command-line tools, read the English text. Usage:
//   NativeGatewayLocalization.GetString = LocalizationHelper.GetString;   // App startup
//   string detail = NativeGatewayLocalization.Text(NativeGatewayLocalization.UnattestedListenersKey);
using System.Globalization;

namespace OpenClaw.Connection.NativeGateway;

/// <summary>
/// Resolves the user-facing reasons Companion gives when it cannot verify a native Gateway
/// endpoint. Each key has a resource in every Companion locale; a missing resource falls back to
/// the English text below.
/// </summary>
public static class NativeGatewayLocalization
{
    internal const string UnattestedListenersKey = "NativeGateway_UnattestedListeners";
    internal const string EndpointProbeUnansweredFormatKey = "NativeGateway_EndpointProbeUnanswered_Format";

    private static readonly Dictionary<string, string> s_english = new(StringComparer.Ordinal)
    {
        [UnattestedListenersKey] =
            "The Gateway is running on more than one port but did not attest which listeners belong to its " +
            "isolated session, so Companion did not send credentials. Gateway features such as MCP apps or a " +
            "plugin's local proxy open additional ports. Update the Gateway MSIX, then retry. Listener " +
            "attestation also requires Windows 11 26100.4770 or later.",
        [EndpointProbeUnansweredFormatKey] =
            "The Gateway is running, but its listener on port {0} did not answer Companion's Gateway protocol " +
            "check in time, so Companion did not send credentials. Retry in a moment.",
    };

    /// <summary>Resource lookup set by the app; it returns the key or an empty string when a resource is missing.</summary>
    public static Func<string, string>? GetString { get; set; }

    internal static string Text(string key)
    {
        string? localized = GetString?.Invoke(key);
        return string.IsNullOrEmpty(localized) || localized == key ? s_english[key] : localized;
    }

    internal static string Format(string key, params object[] args)
    {
        try
        {
            return string.Format(CultureInfo.InvariantCulture, Text(key), args);
        }
        catch (FormatException)
        {
            return string.Format(CultureInfo.InvariantCulture, s_english[key], args);
        }
    }
}
