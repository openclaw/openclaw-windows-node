using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

public sealed class AdaptiveAuxiliaryRoutesSourceTests
{
    // Native pages cannot be mounted in Tray.Tests. Retire these narrow-measurement
    // source guards when native route tests cover populated rows and focused actions.
    [Fact]
    public void NotificationsUseTheSharedInboxWithoutReintroducingTheRetiredList()
    {
        var markup = Load("NotificationsPage");
        var inbox = Named(markup, "Inbox");
        Assert.Equal("InboxView", inbox.Name.LocalName);
        Assert.Equal("Stretch", inbox.Attribute("HorizontalAlignment")?.Value);
        Assert.Null(inbox.Attribute("Width"));
        Assert.DoesNotContain(markup.Descendants(), element => element.Name.LocalName == "ListView");

        var source = Read("NotificationsPage");
        Assert.Contains("Inbox.UsePageLayout();", source);
        Assert.DoesNotContain("_notificationItems", source);
        Assert.DoesNotContain("OnNotificationLayoutSizeChanged", source);

        var root = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Controls");
        var shared = XDocument.Load(Path.Combine(root, "InboxView.xaml"));
        Assert.Contains(shared.Descendants(), element =>
            element.Attribute("Text")?.Value == "{Binding Metadata}" &&
            element.Attribute("TextWrapping")?.Value == "Wrap");
        Assert.Contains(shared.Descendants(), element =>
            element.Name.LocalName == "Setter" &&
            element.Attribute("Property")?.Value == "HorizontalContentAlignment" &&
            element.Attribute("Value")?.Value == "Stretch");
        var adapter = File.ReadAllText(Path.Combine(root, "InboxView.xaml.cs"));
        Assert.Contains("RootGrid.Width = double.NaN;", adapter);
        Assert.Contains("RootGrid.MaxHeight = double.PositiveInfinity;", adapter);
    }

    [Fact]
    public void DiagnosticsToolbarsAdaptWithoutChangingTelemetryOrDetailState()
    {
        var markup = Load("DebugPage");
        Assert.Null(Named(markup, "ChatOverrideCombo").Attribute("MinWidth"));
        Assert.Equal("220", Named(markup, "ChatOverrideCombo").Attribute("MaxWidth")?.Value);
        Assert.Equal("Wrap", Named(markup, "DetailTitle").Attribute("TextWrapping")?.Value);
        var source = Read("DebugPage");
        var resize = source[source.IndexOf("private void OnLayoutSizeChanged", StringComparison.Ordinal)..
            source.IndexOf("public void Initialize", StringComparison.Ordinal)];
        Assert.Contains("e.NewSize.Width", resize);
        foreach (var name in new[] { "TelemetryProtocols", "TelemetryActions", "DetailActions" })
        {
            Assert.Equal("StackPanel", Named(markup, name).Name.LocalName);
            Assert.Contains(name + ".Orientation = orientation", resize);
        }
        Assert.DoesNotMatch(@"Settings|Endpoint\s*=|_detailMode\s*=|_detailGeneration|EnterDetailView|\.Focus\(", resize);
        Assert.Equal("OnDetailCopy", Named(markup, "DetailCopyButton").Attribute("Click")?.Value);
        Assert.Equal("OnSaveOpenTelemetryEndpoint",
            Named(markup, "SaveOpenTelemetryEndpointButton").Attribute("Click")?.Value);
    }

    private static XElement Named(XDocument markup, string name)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        return markup.Descendants().Single(element => element.Attribute(xaml + "Name")?.Value == name);
    }

    private static XDocument Load(string page) => XDocument.Load(Path.Combine(
        TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Pages", page + ".xaml"));

    private static string Read(string page) => File.ReadAllText(Path.Combine(
        TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Pages", page + ".xaml.cs"));
}
