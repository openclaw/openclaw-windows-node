using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClawTray.Controls;

internal enum WorkspacePaneFooterLayout
{
    /// <summary>Account, Dashboard and bell in one row of an open pane.</summary>
    Expanded,
    /// <summary>Account, Dashboard and bell stacked as icons in a compact pane rail.</summary>
    Rail,
    /// <summary>Dashboard and bell only, for the title bar while the Chat pane is hidden.</summary>
    TitleBar,
}

/// <summary>
/// The one footer shared by the Chat pane, the Settings pane and the Chat title bar.
/// It owns layout and control application only; <c>WorkspaceWindow</c> owns the identity,
/// connection, notification and command state it applies to every instance.
/// </summary>
public sealed partial class WorkspacePaneFooter : UserControl
{
    private const double ButtonGap = 4;
    private const double TitleBarButtonHeight = 32;
    private readonly double _paneButtonHeight;
    private readonly FlyoutShowRequest _notificationsShow;
    private WorkspacePaneFooterLayout _layout = WorkspacePaneFooterLayout.Expanded;
    private string _automationIdPrefix = "Workspace";
    private InboxFeed? _inbox;
    private InboxViewCommands? _inboxCommands;
    private string _ownerName = "";
    private string _connectionLabel = "";
    private string? _updateText;

    /// <summary>Raised when the Dashboard button is invoked.</summary>
    internal event EventHandler? DashboardRequested;

    public WorkspacePaneFooter()
    {
        InitializeComponent();
        _notificationsShow = new FlyoutShowRequest(NotificationsFlyout, ReopenNotifications);
        _paneButtonHeight = DashboardButton.Height;
        ApplyAutomationIds();
        ApplyLayout();
    }

    /// <summary>Prefix for stable UI Automation ids: {prefix}Owner, {prefix}Dashboard, {prefix}Notifications.</summary>
    public string AutomationIdPrefix
    {
        get => _automationIdPrefix;
        set
        {
            _automationIdPrefix = value;
            ApplyAutomationIds();
        }
    }

    internal WorkspacePaneFooterLayout Layout
    {
        get => _layout;
        set
        {
            if (_layout == value) return;
            _layout = value;
            ApplyLayout();
        }
    }

    /// <summary>Anchor for flyouts that belong to the account button.</summary>
    internal FrameworkElement OwnerAnchor => OwnerButton;

    internal void SetOwnerMenu(FlyoutBase menu)
    {
        OwnerButton.Flyout = menu;
        ApplyOwnerMenuPlacement();
    }

    internal void ApplyIdentity(string name, string pictureName)
    {
        _ownerName = name;
        OwnerName.Text = name;
        OwnerPicture.DisplayName = pictureName;
        AutomationProperties.SetName(OwnerButton, name);
        ApplyOwnerToolTip();
    }

    internal void ApplyAvatar(Microsoft.UI.Xaml.Media.ImageSource? image) =>
        OwnerPicture.ProfilePicture = image;

    internal void ApplyConnection(string label, ConnectionStatusAccent accent)
    {
        _connectionLabel = label;
        OwnerDetail.Text = label;
        var style = BadgeStyle(accent);
        OwnerStatusDot.Style = style;
        OwnerAvatarStatusDot.Style = style;
        ApplyOwnerHelpText();
        ApplyOwnerToolTip();
    }

    /// <summary>Shows the avatar update dot; <paramref name="updateText"/> is appended to the account help text.</summary>
    internal void ApplyUpdateAvailable(string? updateText)
    {
        _updateText = updateText;
        OwnerUpdateDot.Visibility = string.IsNullOrEmpty(updateText) ? Visibility.Collapsed : Visibility.Visible;
        ApplyOwnerHelpText();
        ApplyOwnerToolTip();
    }

    private void ApplyOwnerHelpText() =>
        AutomationProperties.SetHelpText(OwnerButton, OwnerStatusText);

    private string OwnerStatusText =>
        string.IsNullOrEmpty(_updateText) ? _connectionLabel
        : string.IsNullOrEmpty(_connectionLabel) ? _updateText : $"{_connectionLabel}. {_updateText}";

    /// <summary>Applies the Inbox badge: hidden at zero, capped text, caution tone while an approval waits.</summary>
    internal void ApplyInboxBadge(InboxBadge badge, string helpText)
    {
        var attention = badge.Tone == InboxBadgeTone.Attention;
        NotificationsBadgeText.Text = badge.Text;
        NotificationsAttentionBadgeText.Text = badge.Text;
        NotificationsBadge.Visibility = badge.IsVisible && !attention ? Visibility.Visible : Visibility.Collapsed;
        NotificationsAttentionBadge.Visibility = badge.IsVisible && attention ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetHelpText(NotificationsButton, helpText);
    }

    /// <summary>Supplies the Inbox shown by the bell flyout and the window commands its rows use.</summary>
    internal void BindNotifications(InboxFeed inbox, InboxViewCommands commands)
    {
        _inbox = inbox;
        _inboxCommands = commands;
    }

    internal void HideFlyouts()
    {
        _notificationsShow.Cancel();
        NotificationsFlyout.Hide();
        OwnerButton.Flyout?.Hide();
    }

    internal void Close()
    {
        HideFlyouts();
        NotificationContent.Unbind();
        _inbox = null;
        _inboxCommands = null;
    }

    /// <summary>The connection badge style for an accent, shared with the account menu status item.</summary>
    internal Style BadgeStyle(ConnectionStatusAccent accent) => (Style)Resources[$"ConnectionBadge{accent}"];

    private void OnDashboardClick(object sender, RoutedEventArgs e) => DashboardRequested?.Invoke(this, EventArgs.Empty);

    private void OnNotificationsClick(object sender, RoutedEventArgs e)
    {
        // Button.Flyout handles ordinary opens, but WinUI drops them during its previous close.
        _notificationsShow.DeferUntilClosed();
    }

    private void ReopenNotifications()
    {
        if (_inbox is not null && NotificationsButton.IsLoaded && NotificationsButton.IsEnabled)
            _notificationsShow.ShowAt(NotificationsButton);
    }

    private void OnNotificationsOpening(object sender, object e)
    {
        if (_inbox is null || _inboxCommands is not { } commands) return;
        // Every Inbox command leaves the flyout before navigating.
        NotificationContent.Initialize(_inbox, new InboxViewCommands(
            route =>
            {
                NotificationsFlyout.Hide();
                commands.OpenGatewayRoute(route);
            },
            (route, sessionKey) =>
            {
                NotificationsFlyout.Hide();
                return commands.TryNavigateAsync(route, sessionKey);
            },
            () =>
            {
                NotificationsFlyout.Hide();
                commands.OpenSettings();
            },
            () =>
            {
                NotificationsFlyout.Hide();
                commands.OpenDashboard();
            },
            () =>
            {
                NotificationsFlyout.Hide();
                commands.OpenPage?.Invoke();
            },
            commands.AskOpenClaw is { } ask
                ? question =>
                {
                    NotificationsFlyout.Hide();
                    ask(question);
                }
                : null));
    }

    private void OnNotificationsClosed(object sender, object e) => NotificationContent.Unbind();

    private void ApplyAutomationIds()
    {
        AutomationProperties.SetAutomationId(OwnerButton, $"{_automationIdPrefix}Owner");
        AutomationProperties.SetAutomationId(DashboardButton, $"{_automationIdPrefix}Dashboard");
        AutomationProperties.SetAutomationId(NotificationsButton, $"{_automationIdPrefix}Notifications");
    }

    private void ApplyOwnerToolTip() =>
        ToolTipService.SetToolTip(OwnerButton, _layout == WorkspacePaneFooterLayout.Rail
            ? string.IsNullOrEmpty(OwnerStatusText) ? _ownerName : $"{_ownerName}\n{OwnerStatusText}"
            : null);

    private void ApplyLayout()
    {
        var rail = _layout == WorkspacePaneFooterLayout.Rail;
        var titleBar = _layout == WorkspacePaneFooterLayout.TitleBar;

        FooterGrid.Margin = titleBar ? new Thickness(0) : new Thickness(4, 8, 4, 8);
        FooterGrid.HorizontalAlignment = rail || titleBar ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        OwnerColumn.Width = rail || titleBar ? GridLength.Auto : new GridLength(1, GridUnitType.Star);

        OwnerButton.Visibility = titleBar ? Visibility.Collapsed : Visibility.Visible;
        OwnerButton.Width = rail ? 40 : double.NaN;
        // Leave room for the 24 DIP avatar, status dot and button borders in the 40 DIP rail.
        OwnerButton.Padding = rail ? new Thickness(4) : new Thickness(8);
        OwnerButton.HorizontalContentAlignment = rail ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        OwnerText.Visibility = rail ? Visibility.Collapsed : Visibility.Visible;
        OwnerContent.ColumnSpacing = rail ? 0 : 12;
        OwnerAvatarStatusDot.Visibility = rail ? Visibility.Visible : Visibility.Collapsed;

        Place(OwnerButton, 0, 0);
        Place(DashboardButton, rail ? 1 : 0, rail ? 0 : 1);
        Place(NotificationsButton, rail ? 2 : 0, rail ? 0 : 2);
        var gap = rail ? new Thickness(0, ButtonGap, 0, 0) : new Thickness(ButtonGap, 0, 0, 0);
        DashboardButton.Margin = titleBar ? new Thickness(0) : gap;
        NotificationsButton.Margin = gap;
        // The title bar keeps its 32 DIP compact height, so its buttons must fit inside it.
        DashboardButton.Height = titleBar ? TitleBarButtonHeight : _paneButtonHeight;
        NotificationsButton.Height = DashboardButton.Height;

        NotificationsFlyout.Placement = titleBar
            ? FlyoutPlacementMode.BottomEdgeAlignedRight
            : rail ? FlyoutPlacementMode.RightEdgeAlignedBottom : FlyoutPlacementMode.TopEdgeAlignedLeft;
        ApplyOwnerMenuPlacement();
        ApplyOwnerToolTip();
    }

    // A centered menu over the 40 DIP rail button would spill past the window's left edge.
    private void ApplyOwnerMenuPlacement()
    {
        if (OwnerButton.Flyout is { } menu)
            menu.Placement = _layout == WorkspacePaneFooterLayout.Rail
                ? FlyoutPlacementMode.RightEdgeAlignedBottom
                : FlyoutPlacementMode.Top;
    }

    private static void Place(FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
    }
}
