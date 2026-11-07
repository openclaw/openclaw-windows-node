// <summary>
// Hosts the "Related settings" Dashboard links at the bottom of a Settings page.
// RelatedDashboardLinks owns which verified routes appear; this control only renders
// them and launches through IAppCommands.OpenDashboard, whose launcher owns credentials,
// tunnels and the visible route to Connection settings when a launch cannot proceed.
// Usage (XAML): <controls:RelatedSettingsSection PageTag="permissions"/>
// </summary>

using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Helpers;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClawTray.Controls;

public sealed partial class RelatedSettingsSection : UserControl
{
    private const string AdminScope = "operator.admin";
    private string _pageTag = "";
    private string? _renderedKey;
    private AppState? _appState;

    public RelatedSettingsSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Hub page tag whose related Dashboard links are shown.</summary>
    public string PageTag
    {
        get => _pageTag;
        set
        {
            _pageTag = value ?? "";
            if (IsLoaded) Refresh();
        }
    }

    private static App CurrentApp => (App)Application.Current;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        _appState = CurrentApp.AppState;
        if (_appState != null) _appState.PropertyChanged += OnAppStateChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_appState != null) _appState.PropertyChanged -= OnAppStateChanged;
        _appState = null;
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AppState.Status) or nameof(AppState.GatewaySelf)))
            return;
        if (DispatcherQueue.HasThreadAccess)
            Refresh();
        else
            DispatcherQueue.TryEnqueue(Refresh);
    }

    private void Refresh()
    {
        var canAdmin = CurrentApp.GatewayClient?.GrantedOperatorScopes.Contains(AdminScope) == true;
        var links = RelatedDashboardLinks.For(_pageTag, CurrentApp.AppState?.GatewaySelf?.ServerVersion, canAdmin);
        Visibility = links.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // GatewaySelf is replaced on every health update; keep the existing rows (and focus) when nothing changed.
        var key = string.Join("|", links.Select(link => link.Path));
        if (key == _renderedKey)
            return;
        _renderedKey = key;
        LinksPanel.Children.Clear();
        foreach (var link in links)
            LinksPanel.Children.Add(BuildCard(link));
    }

    private HyperlinkButton BuildCard(RelatedDashboardLink link)
    {
        var title = LocalizationHelper.GetString(link.TitleKey);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(FluentIconCatalog.Build(Glyph(link.Id), 14));
        content.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        var badge = new TextBlock
        {
            Text = LocalizationHelper.GetString("Search_Dashboard"),
            Style = (Style)Resources["RelatedSettingsBadgeTextStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        // The link name already says it opens in Dashboard; keep the visual cue out of the UIA tree.
        AutomationProperties.SetAccessibilityView(badge, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        content.Children.Add(badge);
        var external = FluentIconCatalog.Build(FluentIconCatalog.OpenInBrowser, 12);
        external.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(external);

        // Compact link rows keep the section secondary to the page's own settings.
        var row = new HyperlinkButton { Content = content, Padding = new Thickness(4, 4, 4, 4) };
        ToolTipService.SetToolTip(row, LocalizationHelper.GetString(link.DescriptionKey));
        AutomationProperties.SetHelpText(row, LocalizationHelper.GetString(link.DescriptionKey));
        AutomationProperties.SetAutomationId(row, $"RelatedSettings_{link.Id}");
        AutomationProperties.SetName(row, string.Format(CultureInfo.CurrentCulture,
            LocalizationHelper.GetString("RelatedSettings_OpensInDashboardFormat"), title));
        var path = link.Path;
        row.Click += (_, _) => ((IAppCommands)CurrentApp).OpenDashboard(path);
        return row;
    }
    private static string Glyph(string routeId) => routeId switch
    {
        "Approvals" => FluentIconCatalog.Check,
        "Secrets" => FluentIconCatalog.Key,
        "Security" => FluentIconCatalog.Permissions,
        "Automation" => FluentIconCatalog.Cron,
        "Activity" => FluentIconCatalog.AgentEvents,
        "Logs" => FluentIconCatalog.Document,
        "Systems" => FluentIconCatalog.Devices,
        _ => FluentIconCatalog.DashboardView,
    };
}
