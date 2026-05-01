namespace OpenClawTray.Onboarding.Services;

public sealed record ConnectionModeSelectionResult(
    ConnectionMode Mode,
    string Url,
    bool UpdateGatewayUrl,
    bool UseSshTunnel,
    bool ConnectionTested,
    string StatusMessage,
    string PairingDeviceId,
    bool ShowFields,
    bool UrlReadOnly);

public static class ConnectionPageModeSelector
{
    public const string DefaultLocalUrl = "ws://localhost:18789";
    public const string DevLocalUrl = "ws://localhost:19001";
    public const string WslUrl = "ws://wsl.localhost:18789";
    public const int DefaultSshTunnelPort = 18789;

    public static string GetInitialUrl(
        ConnectionMode mode,
        string settingsGatewayUrl,
        int sshTunnelLocalPort,
        Func<string> getDetectedLocalUrl)
    {
        return mode switch
        {
            ConnectionMode.Local => getDetectedLocalUrl(),
            ConnectionMode.Wsl => WslUrl,
            ConnectionMode.Ssh => $"ws://127.0.0.1:{Math.Max(1, sshTunnelLocalPort)}",
            ConnectionMode.Later => "",
            _ => settingsGatewayUrl
        };
    }

    public static bool ShouldShowConnectionFields(ConnectionMode mode) => mode != ConnectionMode.Later;

    public static bool IsGatewayUrlReadOnly(ConnectionMode mode) => mode == ConnectionMode.Ssh;

    public static ConnectionModeSelectionResult SelectMode(
        ConnectionMode mode,
        string currentUrl,
        string detectedLocalUrl,
        int sshTunnelLocalPort,
        string detectedStatusMessage,
        string laterStatusMessage)
    {
        var useSshTunnel = false;
        var updateGatewayUrl = false;
        var nextUrl = currentUrl;
        var statusMessage = "";

        switch (mode)
        {
            case ConnectionMode.Local:
                nextUrl = detectedLocalUrl;
                updateGatewayUrl = true;
                statusMessage = detectedLocalUrl != DefaultLocalUrl ? detectedStatusMessage : "";
                break;

            case ConnectionMode.Wsl:
                nextUrl = WslUrl;
                updateGatewayUrl = true;
                break;

            case ConnectionMode.Remote:
                break;

            case ConnectionMode.Ssh:
                var localPort = sshTunnelLocalPort > 0 ? sshTunnelLocalPort : DefaultSshTunnelPort;
                nextUrl = $"ws://127.0.0.1:{localPort}";
                updateGatewayUrl = true;
                useSshTunnel = true;
                break;

            case ConnectionMode.Later:
                statusMessage = laterStatusMessage;
                break;
        }

        return new ConnectionModeSelectionResult(
            mode,
            nextUrl,
            updateGatewayUrl,
            useSshTunnel,
            ConnectionTested: false,
            statusMessage,
            PairingDeviceId: "",
            ShouldShowConnectionFields(mode),
            IsGatewayUrlReadOnly(mode));
    }
}
