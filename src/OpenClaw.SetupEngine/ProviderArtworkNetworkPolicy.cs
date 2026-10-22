using System.Net;
using System.Net.Sockets;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Artwork is untrusted display metadata, not a gateway request. DNS is checked inside
/// ConnectCallback and the checked address is passed directly to the socket. TLS remains
/// owned by SocketsHttpHandler, including SNI and normal hostname/certificate validation.
/// </summary>
internal static class ProviderArtworkNetworkPolicy
{
    public static bool TryGetUri(string? value, out Uri uri)
    {
        uri = null!;
        if (value is not { Length: > 0 and <= 2048 } ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Fragment) || parsed.Port != 443 ||
            !IsAllowedHost(parsed.IdnHost))
            return false;
        uri = parsed;
        return true;
    }

    internal static bool IsAllowedHost(string host)
    {
        host = host.TrimEnd('.');
        if (IPAddress.TryParse(host, out var ip))
            return IsPublicAddress(ip);
        if (Uri.CheckHostName(host) != UriHostNameType.Dns || !host.Contains('.'))
            return false;
        return !new[] { "localhost", "local", "internal", "home", "lan", "test", "invalid", "onion" }
            .Any(suffix => host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            return IsPublicAddress(ip.MapToIPv4());
        if (!HttpUrlRiskEvaluator.IsPublicAddress(ip))
            return false;
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            // Additional IANA special-use ranges not excluded by the general navigation policy.
            return !(bytes[0] == 192 && (bytes[1] == 0 && (bytes[2] == 0 || bytes[2] == 2) ||
                                         bytes[1] == 88 && bytes[2] == 99) ||
                     bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100) ||
                     bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        }
        // Permit only global-unicast allocation, excluding protocol assignments, 6to4,
        // documentation and scoped addresses. NAT64/translation and future allocations fail closed.
        return ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId == 0 &&
            (bytes[0] & 0xe0) == 0x20 &&
            !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] < 2) &&
            !(bytes[0] == 0x3f && (bytes[1] & 0xf0) == 0xf0);
    }

    internal static SocketsHttpHandler CreateHandler(
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>>? connect = null) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        MaxResponseHeadersLength = 16,
        MaxConnectionsPerServer = 4,
        ConnectTimeout = TimeSpan.FromSeconds(4),
        PooledConnectionLifetime = TimeSpan.Zero,
        ActivityHeadersPropagator = null,
        ConnectCallback = (context, ct) => ConnectPublicAsync(
            context.DnsEndPoint.Host, context.DnsEndPoint.Port,
            resolve ?? Dns.GetHostAddressesAsync, connect ?? ConnectSocketAsync, ct)
    };

    internal static async ValueTask<Stream> ConnectPublicAsync(
        string host, int port, Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>> connect, CancellationToken ct)
    {
        if (port != 443 || !IsAllowedHost(host))
            throw new ProviderArtworkAddressException();
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] :
            await resolve(host, ct).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Length > 32 || addresses.Any(ip => !IsPublicAddress(ip)))
            throw new ProviderArtworkAddressException();
        ct.ThrowIfCancellationRequested();
        return await connect(addresses[0], port, ct).ConfigureAwait(false);
    }

    private static async ValueTask<Stream> ConnectSocketAsync(IPAddress ip, int port, CancellationToken ct)
    {
        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(ip, port), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

internal sealed class ProviderArtworkAddressException() : HttpRequestException("Artwork address blocked.");
