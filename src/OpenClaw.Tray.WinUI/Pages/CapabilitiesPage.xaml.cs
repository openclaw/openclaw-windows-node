using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;
using System.Collections.Generic;
using Windows.ApplicationModel.DataTransfer;

namespace OpenClawTray.Pages;

public sealed partial class CapabilitiesPage : Page
{
    private HubWindow? _hub;
    private bool _suppressMcpToggle;

    public CapabilitiesPage()
    {
        InitializeComponent();
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        HostnameText.Text = Environment.MachineName;

        BuildCapabilityToggles(hub);
        UpdateMcpStatus(hub);
        UpdateNodeStatus(hub);
    }

    private void BuildCapabilityToggles(HubWindow hub)
    {
        if (hub.Settings == null) return;
        var settings = hub.Settings;

        var capabilities = new (string Icon, string Label, bool Value, Action<bool> Setter)[]
        {
            ("🔌", "Node Mode", settings.EnableNodeMode, v => settings.EnableNodeMode = v),
            ("🌐", "Browser Control", settings.NodeBrowserProxyEnabled, v => settings.NodeBrowserProxyEnabled = v),
            ("📷", "Camera", settings.NodeCameraEnabled, v => settings.NodeCameraEnabled = v),
            ("🎨", "Canvas", settings.NodeCanvasEnabled, v => settings.NodeCanvasEnabled = v),
            ("🖥️", "Screen Capture", settings.NodeScreenEnabled, v => settings.NodeScreenEnabled = v),
            ("📍", "Location", settings.NodeLocationEnabled, v => settings.NodeLocationEnabled = v),
            ("🔊", "Text-to-Speech", settings.NodeTtsEnabled, v => settings.NodeTtsEnabled = v),
        };

        var items = new List<UIElement>();
        foreach (var (icon, label, value, setter) in capabilities)
        {
            var toggle = new ToggleSwitch
            {
                Header = $"{icon}  {label}",
                IsOn = value,
                MinWidth = 200
            };
            toggle.Toggled += (s, e) =>
            {
                setter(toggle.IsOn);
                settings.Save();
                hub.RaiseSettingsSaved();
                UpdateNodeStatus(hub);
            };
            items.Add(toggle);
        }

        CapabilityRepeater.ItemsSource = items;
    }

    private void UpdateNodeStatus(HubWindow hub)
    {
        var nodeEnabled = hub.Settings?.EnableNodeMode ?? false;
        var isConnected = hub.CurrentStatus == ConnectionStatus.Connected;

        if (!nodeEnabled)
        {
            NodeStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
            NodeStatusText.Text = "Node mode disabled";
            NodeDetailsText.Text = "Enable Node Mode to provide device capabilities to agents.";
        }
        else if (isConnected)
        {
            NodeStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LimeGreen);
            NodeStatusText.Text = "Node active";

            var caps = new List<string>();
            if (hub.Settings?.NodeBrowserProxyEnabled == true) caps.Add("browser");
            if (hub.Settings?.NodeCameraEnabled == true) caps.Add("camera");
            if (hub.Settings?.NodeCanvasEnabled == true) caps.Add("canvas");
            if (hub.Settings?.NodeScreenEnabled == true) caps.Add("screen");
            if (hub.Settings?.NodeLocationEnabled == true) caps.Add("location");
            if (hub.Settings?.NodeTtsEnabled == true) caps.Add("tts");
            NodeDetailsText.Text = caps.Count > 0
                ? $"Providing {caps.Count} capabilities: {string.Join(", ", caps)}"
                : "No capabilities enabled.";
        }
        else
        {
            NodeStatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);
            NodeStatusText.Text = "Node mode enabled, not connected";
            NodeDetailsText.Text = "Connect to a gateway to start providing device capabilities.";
        }
    }

    private void UpdateMcpStatus(HubWindow hub)
    {
        var settings = hub.Settings;
        if (settings == null) return;

        _suppressMcpToggle = true;
        McpToggle.IsOn = settings.EnableMcpServer;
        _suppressMcpToggle = false;
        McpDetailsPanel.Visibility = settings.EnableMcpServer ? Visibility.Visible : Visibility.Collapsed;
        McpEndpointText.Text = NodeService.McpServerUrl;

        if (settings.EnableMcpServer)
        {
            var tokenPath = NodeService.McpTokenPath;
            var tokenExists = System.IO.File.Exists(tokenPath);
            McpStatusText.Text = tokenExists ? "Server enabled — token ready" : "Server enabled — token will be created on next start";
        }
    }

    private void OnMcpToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressMcpToggle) return;
        if (_hub?.Settings == null) return;
        _hub.Settings.EnableMcpServer = McpToggle.IsOn;
        _hub.Settings.Save();
        _hub.RaiseSettingsSaved();
        UpdateMcpStatus(_hub);
    }

    private void OnCopyMcpToken(object sender, RoutedEventArgs e)
    {
        try
        {
            var tokenPath = NodeService.McpTokenPath;
            if (System.IO.File.Exists(tokenPath))
            {
                var token = System.IO.File.ReadAllText(tokenPath).Trim();
                var dp = new DataPackage();
                dp.SetText(token);
                Clipboard.SetContent(dp);
                McpStatusText.Text = "Token copied to clipboard";
            }
            else
            {
                McpStatusText.Text = "Token file not found — start the MCP server first";
            }
        }
        catch (Exception ex)
        {
            McpStatusText.Text = $"Failed to read token: {ex.Message}";
        }
    }

    private void OnCopyMcpUrl(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(NodeService.McpServerUrl);
        Clipboard.SetContent(dp);
        McpStatusText.Text = "URL copied to clipboard";
    }
}