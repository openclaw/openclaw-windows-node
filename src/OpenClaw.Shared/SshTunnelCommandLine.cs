using System.Text;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared;

public static class SshTunnelCommandLine
{
    private static readonly Regex s_validSshUser = new(@"^[a-zA-Z0-9._-]+$", RegexOptions.Compiled);
    private static readonly Regex s_validSshHost = new(@"^[a-zA-Z0-9._-]+$", RegexOptions.Compiled);

    public static string BuildArguments(string user, string host, int remotePort, int localPort)
    {
        user = user.Trim();
        host = host.Trim();

        if (!s_validSshUser.IsMatch(user))
            throw new ArgumentException($"SSH user contains invalid characters: '{user}'", nameof(user));
        if (!s_validSshHost.IsMatch(host))
            throw new ArgumentException($"SSH host contains invalid characters: '{host}'", nameof(host));
        ValidatePort(remotePort, nameof(remotePort));
        ValidatePort(localPort, nameof(localPort));

        var sb = new StringBuilder();
        sb.Append("-o BatchMode=yes ");
        sb.Append("-o ExitOnForwardFailure=yes ");
        sb.Append("-o ServerAliveInterval=15 ");
        sb.Append("-o ServerAliveCountMax=3 ");
        sb.Append("-o TCPKeepAlive=yes ");
        sb.Append("-N ");
        sb.Append("-L ");
        sb.Append(localPort);
        sb.Append(":127.0.0.1:");
        sb.Append(remotePort);
        sb.Append(' ');
        sb.Append(user);
        sb.Append('@');
        sb.Append(host);
        return sb.ToString();
    }

    private static void ValidatePort(int port, string parameterName)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(parameterName, port, "SSH tunnel ports must be between 1 and 65535.");
    }
}
