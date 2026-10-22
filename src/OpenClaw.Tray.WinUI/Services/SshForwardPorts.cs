namespace OpenClawTray.Services;

internal enum SshForwardPortParseResult
{
    Ok,
    RemoteInvalid,
    LocalInvalid,
}

/// <summary>
/// Parses the remote and local SSH forward ports. A blank, unparsed, or
/// out-of-range value is rejected. Callers must not substitute 18789 or 18790.
/// </summary>
internal static class SshForwardPorts
{
    internal static SshForwardPortParseResult TryParse(
        string? remoteText,
        string? localText,
        out int remotePort,
        out int localPort)
    {
        remotePort = 0;
        localPort = 0;
        if (!int.TryParse(remoteText, out remotePort) || remotePort is < 1 or > 65535)
            return SshForwardPortParseResult.RemoteInvalid;
        if (!int.TryParse(localText, out localPort) || localPort is < 1 or > 65535)
            return SshForwardPortParseResult.LocalInvalid;
        return SshForwardPortParseResult.Ok;
    }
}
