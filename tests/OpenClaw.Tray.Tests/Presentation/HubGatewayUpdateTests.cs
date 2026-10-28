using System.Xml.Linq;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class HubGatewayUpdateTests
{
    [Fact]
    public void FooterPlacesUpdateGatewayBetweenConnectionStatusAndDiagnostics()
    {
        var markup = XDocument.Parse(ReadSource("HubWindow.xaml"));
        var footer = markup.Descendants()
            .Single(element => element.Name.LocalName == "NavigationView.FooterMenuItems");
        var items = footer.Elements().ToArray();

        Assert.Equal(
            new[] { "status", "updategateway", "debug", "settings" },
            items.Select(element => (string?)element.Attribute("Tag") ?? "status"));
        Assert.Contains(items[0].Descendants(), element =>
            (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "FooterStatusBar");
        var update = Assert.Single(markup.Descendants(), element =>
            (string?)element.Attribute("Tag") == "updategateway");
        Assert.Same(items[1], update);
        Assert.Equal("False", (string?)update.Attribute("SelectsOnInvoked"));
        Assert.Equal("HubWindow_UpdateGateway",
            (string?)update.Attribute(XName.Get("Uid", "http://schemas.microsoft.com/winfx/2006/xaml")));
        Assert.Equal("SettingsNavUpdateGateway", (string?)update.Attribute("AutomationProperties.AutomationId"));
        var icon = Assert.Single(update.Descendants(), element => element.Name.LocalName == "SymbolIcon");
        Assert.Equal("Refresh", (string?)icon.Attribute("Symbol"));
    }

    // Retire these source wiring guards when the real Hub can be mounted in
    // cross-platform tests. They do not establish native click/focus proof.
    [Fact]
    public void InvocationUsesExistingDashboardActionWithoutChangingSelectionOrContent()
    {
        var source = ReadSource("HubWindow.xaml.cs");
        var invoked = source[source.IndexOf("private void NavView_ItemInvoked", StringComparison.Ordinal)..
            source.IndexOf("private void NavView_SelectionChanged", StringComparison.Ordinal)];
        var dispatch = source[source.IndexOf("private void ExecuteAction", StringComparison.Ordinal)..
            source.IndexOf("#region High Contrast icon fallback", StringComparison.Ordinal)];

        Assert.Contains("HubPageRegistry.GetSidebarAction(tag)", invoked);
        Assert.Contains("ExecuteAction(action);", invoked);
        Assert.DoesNotContain("NavigateInternal", invoked);
        Assert.DoesNotContain("SelectedItem =", invoked);
        Assert.DoesNotContain("ContentFrame", invoked);
        Assert.DoesNotContain("CheckForUpdates", invoked);
        Assert.Contains("case HubCommandActionKind.OpenDashboard:", dispatch);
        Assert.Contains("((IAppCommands)Application.Current).OpenDashboard(action.Value);", dispatch);
        Assert.Contains("ExecuteAction(cmd.Action);", source);
        Assert.Contains("ExecuteAction(first.Action);", source);
    }

    private static string ReadSource(string file) => File.ReadAllText(Path.Combine(
        TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Windows", file));
}
