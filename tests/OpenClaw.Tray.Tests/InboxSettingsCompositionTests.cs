// Protects the composition of Settings return focus with the shared Inbox namescope.
// Run with dotnet test tests/OpenClaw.Tray.Tests --filter FullyQualifiedName~InboxSettingsCompositionTests.
using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

/// <summary>Source/resource contracts complement the mounted Back to app mouse/Enter/Escape proof.</summary>
public sealed class InboxSettingsCompositionTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI" }.Concat(parts).ToArray()));

    [Fact]
    public void InboxPage_NamedPaneComposesTheChildNamescope_WithoutStealingControlFocus()
    {
        var page = XDocument.Parse(Read("Pages", "NotificationsPage.xaml")).Root!;
        Assert.Equal("NotificationsPage", (string?)page.Attribute("AutomationProperties.AutomationId"));
        Assert.Equal("False", (string?)page.Attribute("IsTabStop"));
        // The actual heading belongs to InboxView, not the page's XAML namescope.
        Assert.Null(page.Attribute("AutomationProperties.LabeledBy"));
        Assert.DoesNotContain(page.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "NotificationsHeading");
        var inbox = Assert.Single(page.Descendants(), element => element.Name.LocalName == "InboxView");
        Assert.Equal("Inbox", (string?)inbox.Attribute(Xaml + "Name"));
        Assert.Equal("False", (string?)inbox.Attribute("IsTabStop"));
        Assert.DoesNotContain(page.Descendants(), element => element.Name.LocalName == "ListView");

        var owner = Read("Pages", "NotificationsPage.xaml.cs");
        Assert.Contains("""AutomationProperties.SetName(this, LocalizationHelper.GetString("Inbox_Title.Text"));""", owner);
        Assert.Contains("protected override AutomationPeer OnCreateAutomationPeer()", owner);
        Assert.Contains("AutomationControlType.Pane", owner);
        Assert.Contains("Inbox.UsePageLayout();", owner);
        Assert.Contains("Inbox.Unbind();", owner);
        Assert.DoesNotContain("NotificationItemViewModel", owner);
        Assert.DoesNotContain("IsTabStop = !hasNotifications", owner);

        var view = XDocument.Parse(Read("Controls", "InboxView.xaml"));
        var heading = Assert.Single(view.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == "TitleText");
        Assert.Equal("Inbox_Title", (string?)heading.Attribute(Xaml + "Uid"));
        foreach (var id in new[] { "WorkspaceNotificationsSettings", "WorkspaceNotificationsTabs" })
        {
            var target = Assert.Single(view.Descendants(), element => (string?)element.Attribute("AutomationProperties.AutomationId") == id);
            Assert.NotEqual("False", (string?)target.Attribute("IsEnabled"));
            Assert.NotEqual("Collapsed", (string?)target.Attribute("Visibility"));
        }
        // These real children remain available even when there are no rows to dismiss.
        var code = Read("Controls", "InboxView.xaml.cs");
        Assert.DoesNotContain("SettingsButton.Visibility =", code);
        Assert.DoesNotContain("SettingsButton.IsEnabled =", code);
        Assert.DoesNotContain("TabsBar.Visibility =", code);
        Assert.DoesNotContain("TabsBar.IsEnabled =", code);
    }

    [Fact]
    public void InboxAndSettingsReturn_RequiredResourceKeysExist()
    {
        // Global localization tests enforce parity, unique keys and nonempty values
        // in every locale. Keep only the required-key inventory here, not translations.
        var resources = XDocument.Parse(Read("Strings", "en-us", "Resources.resw")).Root!;
        foreach (var key in new[] { "Inbox_Title.Text", "SettingsShell_BackToApp", "SettingsShell_BackToAppKeyHint",
            "SettingsShell_Title", "Inbox_TabAll", "Inbox_EmptyTitle.Text",
            "Inbox_SettingsButton.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name" })
            Assert.Contains(resources.Elements("data"), element => (string?)element.Attribute("name") == key);
    }
}
