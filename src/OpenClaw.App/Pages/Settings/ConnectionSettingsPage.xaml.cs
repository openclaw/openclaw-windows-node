using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OpenClaw.Shared;

namespace OpenClaw.App.Pages.Settings;

public sealed partial class ConnectionSettingsPage : Page
{
    private string _manualGatewayUrl = "";

    public ConnectionSettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var s = App.Current.Settings;
        if (s == null) return;

        SshTunnelToggle.IsOn = s.UseSshTunnel;
        SshUserBox.Text = s.SshTunnelUser ?? "";
        SshHostBox.Text = s.SshTunnelHost ?? "";
        SshRemotePortBox.Text = s.SshTunnelRemotePort.ToString();
        SshLocalPortBox.Text = s.SshTunnelLocalPort.ToString();
        _manualGatewayUrl = s.GatewayUrl ?? "";
        GatewayUrlBox.Text = s.GatewayUrl ?? "";
        UpdateSshTunnelVisibility();
        TokenBox.Password = s.Token ?? "";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var s = App.Current.Settings;
        if (s == null) return;

        s.UseSshTunnel = SshTunnelToggle.IsOn;
        s.SshTunnelUser = SshUserBox.Text.Trim();
        s.SshTunnelHost = SshHostBox.Text.Trim();
        if (int.TryParse(SshRemotePortBox.Text.Trim(), out var rp)) s.SshTunnelRemotePort = rp;
        if (int.TryParse(SshLocalPortBox.Text.Trim(), out var lp)) s.SshTunnelLocalPort = lp;
        if (!s.UseSshTunnel)
            s.GatewayUrl = GatewayUrlBox.Text.Trim();
        s.Token = TokenBox.Password;
        s.Save();
    }

    private void OnSshTunnelToggled(object sender, RoutedEventArgs e) => UpdateSshTunnelVisibility();

    private void UpdateSshTunnelVisibility()
    {
        var on = SshTunnelToggle.IsOn;
        SshTunnelPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        GatewayUrlBox.IsReadOnly = on;
        if (on)
        {
            _manualGatewayUrl = GatewayUrlBox.Text.Trim();
            var localPort = int.TryParse(SshLocalPortBox.Text.Trim(), out var p) ? p : 18789;
            GatewayUrlBox.Text = $"ws://127.0.0.1:{localPort}";
        }
        else if (GatewayUrlBox.Text.StartsWith("ws://127.0.0.1:", StringComparison.OrdinalIgnoreCase))
        {
            GatewayUrlBox.Text = _manualGatewayUrl;
        }
    }

    private void OnUseLocalGateway(object sender, RoutedEventArgs e)
    {
        SshTunnelToggle.IsOn = false;
        GatewayUrlBox.Text = "ws://127.0.0.1:18789";
        _manualGatewayUrl = GatewayUrlBox.Text;
        ConnectionStatusLabel.Text = "Local gateway selected.";
    }

    private void OnUseWslGateway(object sender, RoutedEventArgs e)
    {
        SshTunnelToggle.IsOn = false;
        GatewayUrlBox.Text = "ws://wsl.localhost:18789";
        _manualGatewayUrl = GatewayUrlBox.Text;
        ConnectionStatusLabel.Text = "WSL gateway selected.";
    }

    private void OnUseSshTunnel(object sender, RoutedEventArgs e)
    {
        SshTunnelToggle.IsOn = true;
        UpdateSshTunnelVisibility();
        ConnectionStatusLabel.Text = "SSH tunnel selected. Fill in SSH details, then test.";
    }

    private void OnUseRemoteGateway(object sender, RoutedEventArgs e)
    {
        SshTunnelToggle.IsOn = false;
        if (GatewayUrlBox.Text.StartsWith("ws://127.0.0.1:", StringComparison.OrdinalIgnoreCase) ||
            GatewayUrlBox.Text.StartsWith("ws://wsl.localhost:", StringComparison.OrdinalIgnoreCase))
            GatewayUrlBox.Text = "wss://host.tailnet.ts.net";
        _manualGatewayUrl = GatewayUrlBox.Text;
        ConnectionStatusLabel.Text = "Remote gateway selected. Prefer wss:// for Tailscale or LAN.";
    }

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        var gatewayUrl = GatewayUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            ConnectionStatusLabel.Text = "❌ Gateway URL is required";
            return;
        }

        ConnectionStatusLabel.Text = "Testing…";
        TestConnectionButton.IsEnabled = false;

        try
        {
            var testLogger = new OpenClaw.App.Services.AppLogger();
            var client = new OpenClawGatewayClient(gatewayUrl, TokenBox.Password.Trim(), testLogger);

            var tcs = new TaskCompletionSource<bool>();
            client.StatusChanged += (_, status) =>
            {
                if (status == ConnectionStatus.Connected)
                    tcs.TrySetResult(true);
                else if (status == ConnectionStatus.Error)
                    tcs.TrySetResult(false);
            };

            _ = client.ConnectAsync();
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            var connected = completedTask == tcs.Task && tcs.Task.Result;

            ConnectionStatusLabel.Text = connected ? "✅ Connected" : "❌ Connection failed or timed out";
            client.Dispose();
        }
        catch (Exception ex)
        {
            ConnectionStatusLabel.Text = $"❌ {ex.Message}";
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
        }
    }
}
