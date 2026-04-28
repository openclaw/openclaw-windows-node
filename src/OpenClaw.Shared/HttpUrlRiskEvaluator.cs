using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace OpenClaw.Shared;

public enum HttpUrlSecurityZone
{
    Unknown = -1,
    LocalMachine = 0,
    Intranet = 1,
    Trusted = 2,
    Internet = 3,
    Restricted = 4,
}

public sealed record HttpUrlRiskProfile(
    string CanonicalUrl,
    string CanonicalOrigin,
    string HostKey,
    HttpUrlSecurityZone Zone,
    bool RequiresConfirmation,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Centralized risk classifier for agent-supplied HTTP URLs. Callers should run
/// <see cref="HttpUrlValidator"/> first; this type decides whether an otherwise
/// valid URL needs user confirmation before browser navigation or media handoff.
/// </summary>
public static class HttpUrlRiskEvaluator
{
    public static HttpUrlRiskProfile Evaluate(string canonicalUrl)
    {
        if (!Uri.TryCreate(canonicalUrl, UriKind.Absolute, out var uri))
            throw new ArgumentException("URL must be an absolute URI", nameof(canonicalUrl));

        var reasons = new List<string>();
        var host = uri.Host;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            reasons.Add("URL does not use HTTPS");

        // Homograph defense: a Unicode hostname that round-trips to a different
        // ASCII (Punycode) form is suspicious — `аpple.com` (Cyrillic 'а') and
        // `apple.com` are visually identical but resolve differently. Always
        // surface the mismatch as a Reason so the prompt fires for IDN hosts.
        if (!string.Equals(uri.Host, uri.IdnHost, StringComparison.Ordinal))
            reasons.Add($"Hostname is internationalized; punycode form is '{uri.IdnHost}'");

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("Host is localhost");
        }
        else if (IPAddress.TryParse(host, out var ip))
        {
            reasons.Add("Host is an IP literal");
            AddAddressRiskReasons(ip, reasons);
        }
        else if (!host.Contains('.', StringComparison.Ordinal))
        {
            reasons.Add("Host has no dot and may resolve on the local intranet");
        }

        var zone = MapUrlToZone(canonicalUrl);
        switch (zone)
        {
            case HttpUrlSecurityZone.LocalMachine:
                reasons.Add("Windows classifies this URL as Local Machine zone");
                break;
            case HttpUrlSecurityZone.Intranet:
                reasons.Add("Windows classifies this URL as Intranet zone");
                break;
            case HttpUrlSecurityZone.Restricted:
                reasons.Add("Windows classifies this URL as Restricted zone");
                break;
        }

        var distinctReasons = reasons
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new HttpUrlRiskProfile(
            uri.AbsoluteUri,
            GetCanonicalOrigin(uri),
            uri.Authority.ToLowerInvariant(),
            zone,
            distinctReasons.Length > 0,
            distinctReasons);
    }

    public static bool IsPublicAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.IsIPv4MappedToIPv6) return IsPublicAddress(ip.MapToIPv4());

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0) return false;
            if (b[0] == 10) return false;
            if (b[0] == 100 && (b[1] & 0xC0) == 64) return false;
            if (b[0] == 127) return false;
            if (b[0] == 169 && b[1] == 254) return false;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
            if (b[0] == 192 && b[1] == 168) return false;
            if (b[0] >= 224) return false;
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Unspecified (::) — never a routable destination.
            if (ip.Equals(IPAddress.IPv6None)) return false;
            if (ip.IsIPv6LinkLocal) return false;
            if (ip.IsIPv6SiteLocal) return false;
            if (ip.IsIPv6Multicast) return false;
            var b = ip.GetAddressBytes();
            // Unique-local fc00::/7 (existing).
            if ((b[0] & 0xFE) == 0xFC) return false;
            // ::ffff:0:0/96 — IPv4-mapped (caught above by IsIPv4MappedToIPv6,
            // but defensively re-check).
            if (b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0 &&
                b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 &&
                b[8] == 0 && b[9] == 0 && b[10] == 0xFF && b[11] == 0xFF)
            {
                var mapped = new IPAddress(new[] { b[12], b[13], b[14], b[15] });
                return IsPublicAddress(mapped);
            }
            // ::/96 IPv4-compatible (deprecated; treat as non-public — never legit
            // for an agent-supplied URL).
            if (b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0 &&
                b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 &&
                b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0)
                return false;
            // 2001:db8::/32 — documentation prefix (RFC 3849).
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false;
            // 2001:0000::/32 — Teredo (relay tunneling; not a normal target).
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return false;
            // 100::/64 — discard-only address block (RFC 6666).
            if (b[0] == 0x01 && b[1] == 0x00 &&
                b[2] == 0 && b[3] == 0 && b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0)
                return false;
            // 2002::/16 — 6to4. Block: payload destination is unverifiable.
            if (b[0] == 0x20 && b[1] == 0x02) return false;
            return true;
        }

        return false;
    }

    private static void AddAddressRiskReasons(IPAddress ip, List<string> reasons)
    {
        if (IPAddress.IsLoopback(ip))
        {
            reasons.Add("Address is loopback");
            return;
        }

        if (!IsPublicAddress(ip))
            reasons.Add("Address is private, link-local, multicast, or reserved");
    }

    private static string GetCanonicalOrigin(Uri uri)
    {
        var origin = uri.GetLeftPart(UriPartial.Authority);
        return origin.EndsWith("/", StringComparison.Ordinal) ? origin : origin + "/";
    }

    private static HttpUrlSecurityZone MapUrlToZone(string url)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return HttpUrlSecurityZone.Unknown;

        IInternetSecurityManager? manager = null;
        try
        {
            var type = Type.GetTypeFromCLSID(
                new Guid("7b8a2d94-0ac9-11d1-896c-00c04fb6bfc4"),
                throwOnError: false);
            if (type == null)
                return HttpUrlSecurityZone.Unknown;

            manager = Activator.CreateInstance(type) as IInternetSecurityManager;
            if (manager == null)
                return HttpUrlSecurityZone.Unknown;

            var hr = manager.MapUrlToZone(url, out var zone, 0);
            if (hr != 0)
                return HttpUrlSecurityZone.Unknown;
            return Enum.IsDefined(typeof(HttpUrlSecurityZone), zone)
                ? (HttpUrlSecurityZone)zone
                : HttpUrlSecurityZone.Unknown;
        }
        catch
        {
            return HttpUrlSecurityZone.Unknown;
        }
        finally
        {
            if (manager != null)
            {
                try { Marshal.ReleaseComObject(manager); } catch { }
            }
        }
    }

    [ComImport]
    [Guid("79eac9ee-baf9-11ce-8c82-00aa004ba90b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInternetSecurityManager
    {
        [PreserveSig]
        int SetSecuritySite(IntPtr site);

        [PreserveSig]
        int GetSecuritySite(out IntPtr site);

        [PreserveSig]
        int MapUrlToZone(
            [MarshalAs(UnmanagedType.LPWStr)] string url,
            out int zone,
            int flags);
    }
}
