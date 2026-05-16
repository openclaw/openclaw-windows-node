using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinClipboard = global::Windows.ApplicationModel.DataTransfer;

namespace OpenClaw.App.Pages.Settings;

public sealed partial class NodeSettingsPage : Page
{
    private bool _mcpTokenRevealed;

    private static string McpTokenPath =>
        System.IO.Path.Combine(Services.SettingsManager.SettingsDirectoryPath, "mcp-token.txt");

    public NodeSettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var s = App.Current.Settings;
        if (s == null) return;

        NodeModeToggle.IsOn = s.EnableNodeMode;
        NodeCanvasToggle.IsOn = s.NodeCanvasEnabled;
        NodeScreenToggle.IsOn = s.NodeScreenEnabled;
        NodeCameraToggle.IsOn = s.NodeCameraEnabled;
        NodeLocationToggle.IsOn = s.NodeLocationEnabled;
        NodeBrowserProxyToggle.IsOn = s.NodeBrowserProxyEnabled;
        McpToggle.IsOn = s.EnableMcpServer;

        McpEndpointBox.Text = "http://127.0.0.1:19100/mcp";
        UpdateMcpTokenDisplay();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var s = App.Current.Settings;
        if (s == null) return;

        s.EnableNodeMode = NodeModeToggle.IsOn;
        s.NodeCanvasEnabled = NodeCanvasToggle.IsOn;
        s.NodeScreenEnabled = NodeScreenToggle.IsOn;
        s.NodeCameraEnabled = NodeCameraToggle.IsOn;
        s.NodeLocationEnabled = NodeLocationToggle.IsOn;
        s.NodeBrowserProxyEnabled = NodeBrowserProxyToggle.IsOn;
        s.EnableMcpServer = McpToggle.IsOn;
        s.Save();
    }

    private void UpdateMcpTokenDisplay()
    {
        var token = OpenClaw.Shared.Mcp.McpAuthToken.TryLoad(McpTokenPath);
        if (token == null)
        {
            McpTokenBox.Text = "(not yet generated)";
            McpTokenRevealButton.IsEnabled = false;
            McpTokenCopyButton.IsEnabled = false;
            McpTokenResetButton.IsEnabled = true;
            McpTokenHintText.Text = $"Token stored at: {McpTokenPath}";
            McpStatusText.Text = "Token will be created on first MCP server start.";
            return;
        }
        McpTokenRevealButton.IsEnabled = true;
        McpTokenCopyButton.IsEnabled = true;
        McpTokenResetButton.IsEnabled = true;
        McpTokenBox.Text = _mcpTokenRevealed ? token : new string('•', token.Length);
        McpTokenRevealButton.Content = _mcpTokenRevealed ? "Hide" : "Reveal";
        McpTokenHintText.Text = $"Token stored at: {McpTokenPath}";
        McpStatusText.Text = McpToggle.IsOn ? "Enabled" : "Disabled";
    }

    private void OnRevealMcpToken(object sender, RoutedEventArgs e)
    {
        _mcpTokenRevealed = !_mcpTokenRevealed;
        UpdateMcpTokenDisplay();
    }

    private void OnCopyMcpToken(object sender, RoutedEventArgs e)
    {
        var token = OpenClaw.Shared.Mcp.McpAuthToken.TryLoad(McpTokenPath);
        if (string.IsNullOrEmpty(token)) return;
        try
        {
            var pkg = new WinClipboard.DataPackage();
            pkg.SetText(token);
            WinClipboard.Clipboard.SetContent(pkg);
            McpTokenHintText.Text = "Copied to clipboard.";
        }
        catch (Exception ex)
        {
            McpTokenHintText.Text = $"Failed to copy: {ex.Message}";
        }
    }

    private async void OnResetMcpToken(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Reset MCP Token",
            Content = "This will invalidate the current bearer token. All configured MCP clients will need to be updated with the new token.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        try
        {
            OpenClaw.Shared.Mcp.McpAuthToken.Reset(McpTokenPath);
            _mcpTokenRevealed = false;
            UpdateMcpTokenDisplay();
        }
        catch (Exception ex)
        {
            McpTokenHintText.Text = $"Reset failed: {ex.Message}";
        }
    }
}
