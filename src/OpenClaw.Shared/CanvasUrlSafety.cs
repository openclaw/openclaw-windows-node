using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace OpenClaw.Shared;

/// <summary>
/// Shared, host-normalizing private/loopback network check for canvas URL loads.
///
/// The canvas WebView must not be pointed at localhost / LAN / link-local / CGNAT / Tailscale
/// (100.64/10) / cloud-metadata hosts by a remote caller (SSRF). A regex over the literal
/// dotted-decimal form is not enough: a browser/OS resolver also accepts an IPv4 address written
/// as a single decimal integer (2130706433), hex (0x7f000001), octal (0177.0.0.1), or a short
/// 1-3 part form, and IPv6 has its own loopback/ULA/mapped forms. This normalizes the host the way
/// the resolver would, then range-checks it — so every encoding of an internal address is blocked,
/// not just the canonical spelling. (DNS names that RESOLVE to a private address — rebinding — are a
/// residual this synchronous check cannot cover; the sound complement is a connection-time address
/// filter, as OpenClaw's A2UI MediaResolver already does.)
/// </summary>
public static class CanvasUrlSafety
{
    /// <summary>True when <paramref name="host"/> is (any encoding of) a loopback/private/
    /// link-local/CGNAT/unique-local/unspecified address that a remote caller must not reach.</summary>
    public static bool IsPrivateOrLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        host = host.Trim();

        // IPv6 literal may arrive bracketed from a URL authority.
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
            host = host[1..^1];

        if (TryParseIPv4Numeric(host, out var numeric))
            return IsBlockedAddress(numeric!);
        if (IPAddress.TryParse(host, out var parsed))
            return IsBlockedAddress(parsed);

        return false;
    }

    private static bool IsBlockedAddress(IPAddress addr)
    {
        if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (addr.IsIPv4MappedToIPv6)
                return IsBlockedAddress(addr.MapToIPv4());
            if (IPAddress.IsLoopback(addr))          // ::1
                return true;
            var b6 = addr.GetAddressBytes();
            if (b6[0] == 0xfd || b6[0] == 0xfc)      // fc00::/7 unique-local
                return true;
            if (b6[0] == 0xfe && (b6[1] & 0xc0) == 0x80) // fe80::/10 link-local
                return true;
            if (addr.Equals(IPAddress.IPv6Any))      // ::
                return true;
            return false;
        }

        if (addr.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var b = addr.GetAddressBytes();
        return b[0] == 0                                             // 0.0.0.0/8 (incl. 0.0.0.0 -> localhost)
            || b[0] == 127                                          // 127.0.0.0/8 loopback
            || b[0] == 10                                           // 10.0.0.0/8
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)            // 172.16.0.0/12
            || (b[0] == 192 && b[1] == 168)                        // 192.168.0.0/16
            || (b[0] == 169 && b[1] == 254)                        // 169.254.0.0/16 link-local + metadata
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);         // 100.64.0.0/10 CGNAT / Tailscale
    }

    /// <summary>inet_aton-style parse: dotted (1-4 parts) with each part in decimal, hex (0x…), or
    /// octal (leading 0), plus a single bare integer. Mirrors what the OS/WebView resolver accepts,
    /// so encoded forms of an internal IP are caught.</summary>
    private static bool TryParseIPv4Numeric(string host, out IPAddress? addr)
    {
        addr = null;
        var parts = host.Split('.');
        if (parts.Length is < 1 or > 4)
            return false;

        Span<uint> nums = stackalloc uint[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!TryParseUIntPart(parts[i], out nums[i]))
                return false;
        }

        // Each leading part is one byte; the final part fills the remaining low bytes (inet_aton).
        uint value;
        switch (parts.Length)
        {
            case 1:
                value = nums[0];
                break;
            case 2:
                if (nums[0] > 0xFF || nums[1] > 0xFFFFFF) return false;
                value = (nums[0] << 24) | nums[1];
                break;
            case 3:
                if (nums[0] > 0xFF || nums[1] > 0xFF || nums[2] > 0xFFFF) return false;
                value = (nums[0] << 24) | (nums[1] << 16) | nums[2];
                break;
            default:
                if (nums[0] > 0xFF || nums[1] > 0xFF || nums[2] > 0xFF || nums[3] > 0xFF) return false;
                value = (nums[0] << 24) | (nums[1] << 16) | (nums[2] << 8) | nums[3];
                break;
        }

        addr = new IPAddress(new[]
        {
            (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
        });
        return true;
    }

    private static bool TryParseUIntPart(string part, out uint value)
    {
        value = 0;
        if (part.Length == 0)
            return false;

        if (part.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(part.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
                   && part.Length > 2;

        if (part.Length > 1 && part[0] == '0')
        {
            // Octal.
            uint acc = 0;
            foreach (var c in part)
            {
                if (c is < '0' or > '7') return false;
                acc = (acc * 8) + (uint)(c - '0');
            }
            value = acc;
            return true;
        }

        return uint.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
