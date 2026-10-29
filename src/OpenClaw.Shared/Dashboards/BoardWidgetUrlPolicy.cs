namespace OpenClaw.Shared.Dashboards;

/// <summary>
/// Origin rules for board widget content, mirroring upstream
/// <c>ui/src/components/sandbox-host.ts</c>, <c>board-widget-sandbox-host.ts</c>
/// and <c>src/boards/board-website.ts</c>. Every URL a widget host loads or
/// fetches passes through here first.
/// </summary>
public static class BoardWidgetUrlPolicy
{
    public const string WidgetPathPrefix = "/__openclaw__/board/";
    public const string SandboxPath = "/mcp-app-sandbox";
    public const int MaxWebsiteUrlLength = 2048;

    /// <summary>
    /// HTTP(S) origin of the active Gateway from its WebSocket URL
    /// (<c>ws</c> to <c>http</c>, <c>wss</c> to <c>https</c>).
    /// </summary>
    public static Uri? TryGetGatewayHttpOrigin(string? gatewayUrl)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl) || !Uri.TryCreate(gatewayUrl.Trim(), UriKind.Absolute, out var uri))
            return null;
        var scheme = uri.Scheme.ToLowerInvariant() switch
        {
            "ws" or "http" => Uri.UriSchemeHttp,
            "wss" or "https" => Uri.UriSchemeHttps,
            _ => null,
        };
        if (scheme is null || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            return null;
        return OriginOf(new UriBuilder(uri) { Scheme = scheme, Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri);
    }

    /// <summary>
    /// Resolves a widget's <c>frameUrl</c> against the Gateway origin and
    /// requires the ticketed board document route on that exact origin.
    /// </summary>
    public static bool TryResolveFrameUrl(string? frameUrl, Uri gatewayOrigin, out Uri resolved)
    {
        resolved = null!;
        if (string.IsNullOrWhiteSpace(frameUrl) || !Uri.TryCreate(gatewayOrigin, frameUrl.Trim(), out var candidate))
            return false;
        if (!SameOrigin(candidate, gatewayOrigin) ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !candidate.AbsolutePath.StartsWith(WidgetPathPrefix, StringComparison.Ordinal) ||
            !candidate.AbsolutePath.EndsWith("/index.html", StringComparison.Ordinal) ||
            !HasQueryParameter(candidate, "bt"))
            return false;
        resolved = candidate;
        return true;
    }

    /// <summary>
    /// Resolves the sandbox proxy URL exactly like upstream
    /// <c>resolveSandboxHostUrl</c>: the configured sandbox origin, else the
    /// Gateway origin with <paramref name="sandboxPort"/>; never the Gateway or
    /// host origin; only the <c>/mcp-app-sandbox</c> path.
    /// </summary>
    public static bool TryResolveSandboxUrl(
        string? sandboxUrl,
        int? sandboxPort,
        string? sandboxOrigin,
        Uri gatewayOrigin,
        Uri hostOrigin,
        out Uri resolved)
    {
        resolved = null!;
        if (string.IsNullOrWhiteSpace(sandboxUrl) || sandboxPort is not (>= 1 and <= 65535))
            return false;

        Uri baseUri;
        if (!string.IsNullOrWhiteSpace(sandboxOrigin))
        {
            if (!Uri.TryCreate(sandboxOrigin.Trim(), UriKind.Absolute, out var configured) ||
                !string.IsNullOrEmpty(configured.UserInfo) ||
                !string.Equals(OriginString(configured), sandboxOrigin.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                return false;
            baseUri = OriginOf(configured);
        }
        else
        {
            baseUri = new UriBuilder(gatewayOrigin) { Port = sandboxPort.Value, Path = "/", Query = "", Fragment = "" }.Uri;
        }

        if (baseUri.Scheme is not ("http" or "https") ||
            IsTransportDowngrade(baseUri, gatewayOrigin) ||
            SameOrigin(baseUri, gatewayOrigin) ||
            SameOrigin(baseUri, hostOrigin) ||
            !Uri.TryCreate(baseUri, sandboxUrl.Trim(), out var candidate) ||
            !SameOrigin(candidate, baseUri) ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            candidate.AbsolutePath != SandboxPath)
            return false;
        resolved = candidate;
        return true;
    }

    /// <summary>
    /// An HTTPS Gateway must not hand widget documents to a plain-HTTP sandbox
    /// on another machine: an on-path attacker could replace the proxy and
    /// receive the document. Loopback stays allowed because it never leaves
    /// the host; an HTTP Gateway already serves documents over HTTP.
    /// </summary>
    private static bool IsTransportDowngrade(Uri sandbox, Uri gatewayOrigin) =>
        gatewayOrigin.Scheme == Uri.UriSchemeHttps &&
        sandbox.Scheme != Uri.UriSchemeHttps &&
        !IsLoopbackHost(sandbox.Host);

    /// <summary>
    /// True when a remote Gateway did not configure a dedicated sandbox origin;
    /// the derived port is often unreachable behind a proxy or tunnel, so
    /// repeated authorization failures should name <c>mcp.apps.sandboxOrigin</c>.
    /// </summary>
    public static bool SandboxOriginRequired(BoardWidget widget, Uri resolvedSandbox) =>
        string.IsNullOrWhiteSpace(widget.SandboxOrigin) && !IsLoopbackHost(resolvedSandbox.Host);

    public static bool IsLoopbackHost(string? host)
    {
        var value = host?.Trim().Trim('[', ']');
        if (string.IsNullOrEmpty(value))
            return false;
        if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return System.Net.IPAddress.TryParse(value, out var address) && System.Net.IPAddress.IsLoopback(address);
    }

    /// <summary>
    /// Validates <c>session:website</c> props: a public HTTPS URL of at most 2048
    /// characters without credentials. A website on the Gateway's host is not
    /// embedded because cookies are scoped to hosts, not ports.
    /// </summary>
    public static bool TryParseWebsite(string? url, Uri? gatewayOrigin, out Uri website, out bool sameHostAsGateway)
    {
        website = null!;
        sameHostAsGateway = false;
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxWebsiteUrlLength ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var candidate) ||
            !IsPublicHttpsUrl(candidate) ||
            candidate.AbsoluteUri.Length > MaxWebsiteUrlLength)
            return false;
        website = candidate;
        sameHostAsGateway = gatewayOrigin is not null &&
            string.Equals(candidate.IdnHost, gatewayOrigin.IdnHost, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    /// <summary>
    /// Navigation rule for embedded websites, checked for the first load and
    /// every later navigation or redirect: HTTPS, no credentials, and a public
    /// host. Literal loopback, private, link-local, CGNAT and unspecified
    /// addresses and single-label intranet names are refused so a widget
    /// cannot steer the user's machine at local services.
    /// </summary>
    public static bool IsPublicHttpsUrl(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(url.Host) || !string.IsNullOrEmpty(url.UserInfo))
            return false;
        return !IsPrivateNetworkHost(url.IdnHost);
    }

    /// <summary>
    /// Subresource rule for embedded websites: HTTP(S) requests from documents,
    /// frames and workers to a private-network host are refused. Non-network
    /// schemes such as <c>data:</c> and <c>blob:</c> pass. WebSocket schemes are
    /// classified too, though WebView2 does not surface their handshakes.
    /// </summary>
    public static bool IsBlockedWebsiteRequest(Uri? url) =>
        url is not null && url.IsAbsoluteUri &&
        url.Scheme is "http" or "https" or "ws" or "wss" &&
        IsPrivateNetworkHost(url.IdnHost);

    /// <summary>
    /// True for literal loopback, private, link-local, CGNAT, unique-local and
    /// unspecified addresses, <c>localhost</c> names, and single-label intranet
    /// names, after removing IPv6 brackets and a terminal DNS dot. Names that
    /// resolve to private addresses through DNS are not detected here.
    /// </summary>
    public static bool IsPrivateNetworkHost(string? host)
    {
        var value = host?.Trim().Trim('[', ']').TrimEnd('.');
        if (string.IsNullOrEmpty(value))
            return true;
        if (IsLoopbackHost(value) || CanvasUrlSafety.IsPrivateOrLoopbackHost(value))
            return true;
        return !System.Net.IPAddress.TryParse(value, out _) && !value.Contains('.', StringComparison.Ordinal);
    }

    /// <summary>Absolute HTTP(S) link without credentials, safe to hand to the default browser.</summary>
    public static bool IsExternalHttpLink(string? url, out Uri link)
    {
        link = null!;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var candidate) ||
            candidate.Scheme is not ("http" or "https") || string.IsNullOrEmpty(candidate.Host) ||
            !string.IsNullOrEmpty(candidate.UserInfo))
            return false;
        link = candidate;
        return true;
    }

    public static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    /// <summary>Serialized origin (<c>scheme://host[:port]</c>) as used by <c>postMessage</c>.</summary>
    public static string OriginString(Uri uri)
    {
        // IdnHost drops the brackets an IPv6 literal needs inside an authority; Host keeps them.
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;
        return (uri.IsDefaultPort ? $"{uri.Scheme}://{host}" : $"{uri.Scheme}://{host}:{uri.Port}").ToLowerInvariant();
    }

    public static Uri OriginOf(Uri uri) => new(OriginString(uri) + "/");

    private static bool HasQueryParameter(Uri uri, string name)
    {
        var query = uri.Query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = separator < 0 ? part : part[..separator];
            if (string.Equals(Uri.UnescapeDataString(key), name, StringComparison.Ordinal) && separator >= 0 && separator < part.Length - 1)
                return true;
        }
        return false;
    }
}
