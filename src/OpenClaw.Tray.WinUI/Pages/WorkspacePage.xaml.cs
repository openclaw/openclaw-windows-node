using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;

namespace OpenClawTray.Pages;

public sealed partial class WorkspacePage : Page
{
    private static App CurrentApp => (App)Microsoft.UI.Xaml.Application.Current;
    private AppState? _appState;
    private readonly Dictionary<string, TabViewItem> _fileTabs = new(StringComparer.OrdinalIgnoreCase);
    private bool _tabsPopulated;

    /// <summary>Set by HubWindow before <see cref="Initialize"/> to specify the active agent scope.</summary>
    public string AgentId { get; set; } = "main";
    public string CurrentAgentId => AgentId;

    public WorkspacePage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        };
    }

    public void Initialize()
    {
        _appState = CurrentApp.AppState;
        _appState.PropertyChanged += OnAppStateChanged;
        // Only request fresh data when no matching cache exists.
        var hasMatchingCache = _appState?.AgentFilesList.HasValue == true &&
            string.Equals(_appState?.AgentFilesListAgentId, AgentId, StringComparison.OrdinalIgnoreCase);
        var status = CurrentApp.AppState?.Status ?? OpenClaw.Shared.ConnectionStatus.Disconnected;
        if (CurrentApp.GatewayClient != null && status == OpenClaw.Shared.ConnectionStatus.Connected && !hasMatchingCache)
        {
            FallbackInfoBar.IsOpen = false;
            LoadingRing.IsActive = true;
            LoadingPanel.Visibility = Visibility.Visible;
            ClearTabs();
            _ = CurrentApp.GatewayClient.RequestAgentFilesListAsync(AgentId);
        }
        else if (CurrentApp.GatewayClient == null || status != OpenClaw.Shared.ConnectionStatus.Connected)
        {
            FallbackInfoBar.IsOpen = true;
            FallbackInfoBar.Message = "Connect to gateway to view workspace files.";
        }
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppState.AgentFilesList):
                if (_appState!.AgentFilesList.HasValue) UpdateAgentFilesList(_appState.AgentFilesList.Value);
                break;
            case nameof(AppState.AgentFileContent):
                if (_appState!.AgentFileContent.HasValue) UpdateAgentFileContent(_appState.AgentFileContent.Value);
                break;
        }
    }

    public void UpdateAgentFilesList(JsonElement data)
    {
        LoadingRing.IsActive = false;
        LoadingPanel.Visibility = Visibility.Collapsed;
        ClearTabs();

        if (data.TryGetProperty("workspace", out var workspaceEl))
        {
            var workspace = workspaceEl.GetString();
            if (!string.IsNullOrEmpty(workspace))
                WorkspacePathText.Text = workspace;
        }

        if (data.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var fileEl in filesEl.EnumerateArray())
            {
                var name = fileEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                long size = fileEl.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number ? sizeEl.GetInt64() : 0;
                bool exists = !fileEl.TryGetProperty("exists", out var existsEl) || existsEl.ValueKind != JsonValueKind.False;

                if (!string.IsNullOrEmpty(name) && exists)
                {
                    AddFileTab(name, size);
                }
            }
        }

        if (FileTabs.TabItems.Count == 0)
        {
            FallbackInfoBar.IsOpen = true;
            FallbackInfoBar.Message = "No workspace files found for this agent.";
            FileTabs.Visibility = Visibility.Collapsed;
        }
        else
        {
            FileTabs.Visibility = Visibility.Visible;
            FileTabs.SelectedIndex = 0;
            _tabsPopulated = true;
            // Explicitly fetch first tab content (SelectionChanged may not fire for programmatic index set)
            if (FileTabs.SelectedItem is TabViewItem firstTab && firstTab.Tag is string firstName && CurrentApp.GatewayClient != null)
                _ = CurrentApp.GatewayClient.RequestAgentFileGetAsync(AgentId, firstName);
        }
    }

    public void UpdateAgentFileContent(JsonElement data)
    {
        if (!data.TryGetProperty("file", out var fileEl)) return;

        var name = fileEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
        var content = fileEl.TryGetProperty("content", out var contentEl) ? contentEl.GetString() ?? "" : "";
        bool missing = fileEl.TryGetProperty("missing", out var missingEl) && missingEl.ValueKind == JsonValueKind.True;

        if (string.IsNullOrEmpty(name) || !_fileTabs.TryGetValue(name, out var tab)) return;

        var textBlock = new TextBlock
        {
            Text = missing ? "(file not found on disk)" : content,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(16)
        };

        var scrollViewer = new ScrollViewer
        {
            Content = textBlock,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        tab.Content = scrollViewer;

        // WinUI TabView doesn't visually refresh when Content changes on the selected tab.
        // Force re-render by cycling the selection.
        if (FileTabs.SelectedItem == tab)
        {
            FileTabs.SelectedItem = null;
            FileTabs.SelectedItem = tab;
        }
    }

    private void AddFileTab(string fileName, long size)
    {
        var header = fileName;
        if (size > 0) header += $" ({FormatSize(size)})";

        var tab = new TabViewItem
        {
            Header = header,
            IsClosable = false,
            Tag = fileName,
            Content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new ProgressRing { IsActive = true, Width = 24, Height = 24 },
                    new TextBlock
                    {
                        Text = "Loading content…",
                        Margin = new Thickness(0, 8, 0, 0),
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                    }
                }
            }
        };

        _fileTabs[fileName] = tab;
        FileTabs.TabItems.Add(tab);
    }

    private void ClearTabs()
    {
        FileTabs.TabItems.Clear();
        _fileTabs.Clear();
        _tabsPopulated = false;
        FileTabs.Visibility = Visibility.Collapsed;
    }

    private void FileTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Lazy load on tab select if content is still placeholder
        if (FileTabs.SelectedItem is TabViewItem tab && tab.Tag is string fileName &&
            tab.Content is StackPanel && CurrentApp.GatewayClient != null)
        {
            _ = CurrentApp.GatewayClient.RequestAgentFileGetAsync(AgentId, fileName);
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentApp.GatewayClient != null)
        {
            LoadingRing.IsActive = true;
            LoadingPanel.Visibility = Visibility.Visible;
            FallbackInfoBar.IsOpen = false;
            ClearTabs();
            _ = CurrentApp.GatewayClient.RequestAgentFilesListAsync(AgentId);
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}
