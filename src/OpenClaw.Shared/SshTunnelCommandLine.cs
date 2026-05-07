using System.Text;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared;

public static class SshTunnelCommandLine
{
    private static readonly Regex s_validSshUser = new(@"^[a-zA-Z0-9._-]+$", RegexOptions.Compiled);
    private static readonly Regex s_validSshHost = new(@"^[a-zA-Z0-9._-]+$", RegexOptions.Compiled);

    // Fixed SSH options shared by every tunnel invocation.
    // Centralised here so the connection policy is visible and easy to review or adjust.
    private const string BaseOptions =
        "-o BatchMode=yes " +
        "-o ExitOnForwardFailure=yes " +
        "-o ServerAliveInterval=15 " +
        "-o ServerAliveCountMax=3 " +
        "-o TCPKeepAlive=yes " +
        "-N ";

    public static string BuildArguments(string user, string host, int remotePort, int localPort)
        => BuildArguments(user, host, remotePort, localPort, includeBrowserProxyForward: false);

    public static string BuildArguments(
        string user,
        string host,
        int remotePort,
        int localPort,
        bool includeBrowserProxyForward)
    {
        user = user.Trim();
        host = host.Trim();

        if (!s_validSshUser.IsMatch(user))
            throw new ArgumentException($"SSH user contains invalid characters: '{user}'", nameof(user));
        if (!s_validSshHost.IsMatch(host))
            throw new ArgumentException($"SSH host contains invalid characters: '{host}'", nameof(host));
        ValidatePort(remotePort, nameof(remotePort));
        ValidatePort(localPort, nameof(localPort));
        if (includeBrowserProxyForward)
        {
            ValidateBrowserProxyPort(remotePort, nameof(remotePort));
            ValidateBrowserProxyPort(localPort, nameof(localPort));
        }

        var sb = new StringBuilder(BaseOptions);
        AppendLocalForward(sb, localPort, remotePort);
        if (includeBrowserProxyForward)
            AppendLocalForward(sb, localPort + 2, remotePort + 2);
        sb.Append(user);
        sb.Append('@');
        sb.Append(host);
        return sb.ToString();
    }

    public static bool CanForwardBrowserProxyPort(int remotePort, int localPort) =>
        remotePort is >= 1 and <= 65533 &&
        localPort is >= 1 and <= 65533;

    private static void AppendLocalForward(StringBuilder sb, int localPort, int remotePort)
    {
        sb.Append("-L ");
        sb.Append(localPort);
        sb.Append(":127.0.0.1:");
        sb.Append(remotePort);
        sb.Append(' ');
    }

    private static void ValidatePort(int port, string parameterName)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(parameterName, port, "SSH tunnel ports must be between 1 and 65535.");
    }

    private static void ValidateBrowserProxyPort(int port, string parameterName)
    {
        if (port is > 65533)
            throw new ArgumentOutOfRangeException(parameterName, port, "Browser proxy SSH forwarding requires gateway ports at or below 65533.");
    }
}
