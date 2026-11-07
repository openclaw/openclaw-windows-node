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
using OpenClaw.SetupEngine.UI.Controls;
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
        // GatewaySelf is replaced on every health update; keep the existing cards (and focus) when nothing changed.
        var key = string.Join("|", links.Select(link => link.Path));
        if (key == _renderedKey)
            return;
        _renderedKey = key;
        LinksPanel.Children.Clear();
        foreach (var link in links)
            LinksPanel.Children.Add(BuildCard(link));
    }

    private SetupActionCard BuildCard(RelatedDashboardLink link)
    {
        var title = LocalizationHelper.GetString(link.TitleKey);
        var badge = new Border
        {
            Style = (Style)Resources["RelatedSettingsBadgeStyle"],
            Child = new TextBlock
            {
                Text = LocalizationHelper.GetString("Search_Dashboard"),
                Style = (Style)Resources["RelatedSettingsBadgeTextStyle"],
            },
        };
        AutomationProperties.SetAccessibilityView(badge, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        // AccessibilityView does not inherit; the card name already says it opens in Dashboard.
        AutomationProperties.SetAccessibilityView(badge.Child, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

        // SetupActionCard supplies the UIA Invoke provider the stock Toolkit card rejects.
        var card = new SetupActionCard
        {
            Header = title,
            Description = LocalizationHelper.GetString(link.DescriptionKey),
            HeaderIcon = FluentIconCatalog.Build(Glyph(link.Id)),
            ActionIcon = FluentIconCatalog.Build(FluentIconCatalog.OpenInBrowser),
            IsClickEnabled = true,
            Content = badge,
        };
        AutomationProperties.SetAutomationId(card, $"RelatedSettings_{link.Id}");
        AutomationProperties.SetName(card, string.Format(CultureInfo.CurrentCulture,
            LocalizationHelper.GetString("RelatedSettings_OpensInDashboardFormat"), title));
        var path = link.Path;
        card.InvokeRequested += (_, _) => ((IAppCommands)CurrentApp).OpenDashboard(path);
        return card;
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
