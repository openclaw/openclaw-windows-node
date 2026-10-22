using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

/// <summary>In-memory native connection input. Never serialize the setup code or token into setup config.</summary>
public sealed record SetupNativeConnectionRequest(
    string GatewayUrl = "",
    string? SetupCode = null,
    string? SharedToken = null,
    string? FriendlyName = null,
    SshTunnelConfig? SshTunnel = null,
    string? EditingGatewayId = null);

public sealed record SetupNativeConnectionResult(
    bool Success,
    bool GatewayCommitted = false,
    string? GatewayId = null,
    string? GatewayUrl = null,
    string? Error = null,
    bool RequiresAttention = false,
    string? EndpointBinding = null);

/// <summary>
/// Check is nonpersistent. Connect rechecks the current input and commits through the host's
/// transaction owner. A successful Connect is the explicit commit boundary, not wizard completion.
/// </summary>
public interface ISetupNativeConnectionHost
{
    Task<SetupNativeConnectionResult> CheckAsync(
        SetupNativeConnectionRequest request, CancellationToken cancellationToken);
    Task<SetupNativeConnectionResult> ConnectAsync(
        SetupNativeConnectionRequest request, CancellationToken cancellationToken);
    Task DiscardCheckAsync() => Task.CompletedTask;
}

public sealed record SetupNativeConnectionInput(
    string GatewayUrl, string? SharedToken, string? BootstrapToken);

/// <summary>Resolves the edited address without reinterpreting bootstrap credentials as shared tokens.</summary>
public static class SetupNativeConnectionInputResolver
{
    public static SetupNativeConnectionInput Resolve(SetupNativeConnectionRequest request)
    {
        var url = request.GatewayUrl.Trim();
        string? bootstrapToken = null;
        if (!string.IsNullOrWhiteSpace(request.SetupCode))
        {
            var decoded = SetupCodeDecoder.Decode(request.SetupCode);
            if (!decoded.Success)
                throw new ArgumentException(decoded.Error);
            if (!string.IsNullOrWhiteSpace(request.SharedToken))
                throw new ArgumentException("Use either a setup code or a shared token, not both.");
            if (decoded.Url is not null)
            {
                if (url.Length == 0)
                    url = decoded.Url;
                else if (!EquivalentAddress(url, decoded.Url))
                    throw new ArgumentException("The gateway address differs from the setup code. Use a code for the edited address.");
            }
            bootstrapToken = decoded.Token;
        }

        if (!GatewayUrlHelper.IsValidGatewayUrl(url))
            throw new ArgumentException(GatewayUrlHelper.ValidationMessage);
        var uri = new Uri(url);
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Use a gateway address without embedded credentials, query parameters, or a fragment.");

        if (request.SshTunnel is { } tunnel)
            _ = SshTunnelCommandLine.BuildArguments(tunnel.User, tunnel.Host, tunnel.RemotePort,
                tunnel.LocalPort, tunnel.IncludeBrowserProxyForward, tunnel.SshPort);

        return new(GatewayUrlHelper.NormalizeForWebSocket(url),
            string.IsNullOrWhiteSpace(request.SharedToken) ? null : request.SharedToken.Trim(),
            bootstrapToken);
    }

    private static bool EquivalentAddress(string left, string right) =>
        GatewayUrlHelper.IsValidGatewayUrl(left) &&
        GatewayUrlHelper.IsValidGatewayUrl(right) &&
        string.Equals(
            new Uri(GatewayUrlHelper.NormalizeForWebSocket(left)).AbsoluteUri,
            new Uri(GatewayUrlHelper.NormalizeForWebSocket(right)).AbsoluteUri,
            StringComparison.Ordinal);
}
