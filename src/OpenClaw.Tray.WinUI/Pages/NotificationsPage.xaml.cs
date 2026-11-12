using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using OpenClawTray.Helpers;
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
        // The heading is inside InboxView's namescope. Name the page from that same
        // localized resource; its enabled Settings/tabs remain the focus destinations even when empty.
        AutomationProperties.SetName(this, LocalizationHelper.GetString("Inbox_Title.Text"));
        // Each navigation creates a new page, so its Unloaded ends this page's Inbox binding.
        Unloaded += (_, _) => Inbox.Unbind();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new NotificationsPageAutomationPeer(this);

    // Keep a named native pane without adding an empty-page tab stop ahead of Inbox controls.
    private sealed class NotificationsPageAutomationPeer(NotificationsPage owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetClassNameCore() => nameof(NotificationsPage);
    }

    /// <summary>
    /// Binds the Inbox. Without host commands (the Settings companion), routes use the
    /// app-wide navigation commands.
    /// </summary>
    internal void Initialize(InboxFeed? inbox, InboxViewCommands? commands = null)
    {
        if (inbox is null)
        {
            Inbox.Unbind();
            return;
        }

        Inbox.Initialize(inbox, commands ?? new InboxViewCommands(
            OpenGatewayRoute,
            ((IAppCommands)CurrentApp).TryNavigateAsync,
            () => ((IAppCommands)CurrentApp).Navigate(InboxRoutes.NotificationSettings),
            () => ((IAppCommands)CurrentApp).OpenDashboard(),
            AskOpenClaw: AskOpenClaw));
    }

    private static void AskOpenClaw(string question)
    {
        if (InboxChatPrompt.TrySend(CurrentApp.ChatProvider, CurrentApp.GatewayClient, question, out var sessionKey))
            OpenGatewayRoute(AppNotificationActionRoutes.Chat(sessionKey));
        else
            ((IAppCommands)CurrentApp).Navigate("connection");
    }

    private static void OpenGatewayRoute(string route)
    {
        var isChat = AppNotificationActionRoutes.TryGetChatSessionKey(route, out var sessionKey);
        AsyncEventHandlerGuard.Run(
            () => ((IAppCommands)CurrentApp).TryNavigateAsync(isChat ? "chat" : route, sessionKey),
            new AppLogger(), nameof(OpenGatewayRoute));
    }
}
