using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.Controls;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClawTray.Pages;

/// <summary>Full-size Inbox. Rows, tabs and actions come from the shared <see cref="InboxView"/>.</summary>
public sealed partial class NotificationsPage : Page
{
    private static App CurrentApp => (App)Application.Current!;

    public NotificationsPage()
    {
        InitializeComponent();
        Inbox.UsePageLayout();
        // Each navigation creates a new page, so its Unloaded ends this page's Inbox binding.
        Unloaded += (_, _) => Inbox.Unbind();
    }

    /// <summary>
    /// Binds the Inbox. Without host commands (the Settings companion), routes use the
    /// app-wide navigation commands.
    /// </summary>
    internal void Initialize(AppNotificationService? notificationService, InboxViewCommands? commands = null)
    {
        if (notificationService is null)
        {
            Inbox.Unbind();
            return;
        }

        Inbox.Initialize(notificationService, commands ?? new InboxViewCommands(
            OpenAppRoute,
            () => ((IAppCommands)CurrentApp).Navigate(InboxRoutes.NotificationSettings)));
    }

    private static void OpenAppRoute(string route)
    {
        if (AppNotificationActionRoutes.TryGetChatSessionKey(route, out var sessionKey))
        {
            CurrentApp.PendingChatSessionKey = sessionKey;
            ((IAppCommands)CurrentApp).Navigate("chat");
            return;
        }

        ((IAppCommands)CurrentApp).Navigate(route);
    }
}
