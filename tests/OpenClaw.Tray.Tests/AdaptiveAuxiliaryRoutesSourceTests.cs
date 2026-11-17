using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

public sealed class AdaptiveAuxiliaryRoutesSourceTests
{
    // Native pages cannot be mounted in Tray.Tests. Retire these narrow-measurement
    // source guards when native route tests cover populated rows and focused actions.
    [Fact]
    public void NotificationsUseSharedInboxWithConstrainedTextAndSeparateActions()
    {
        // Inbox replaced the page-local row builder. Keep the text/action geometry
        // guard at that owner; InboxSettingsCompositionTests covers page binding.
        var markup = XDocument.Load(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Controls", "InboxView.xaml"));
        var list = Named(markup, "ItemsList");
        Assert.Contains(list.Descendants(), element =>
            element.Name.LocalName == "Setter" &&
            element.Attribute("Property")?.Value == "HorizontalContentAlignment" &&
            element.Attribute("Value")?.Value == "Stretch");
        var details = list.Descendants().Single(element =>
            element.Attribute("Text")?.Value == "{Binding Details}");
        Assert.Equal("Wrap", details.Attribute("TextWrapping")?.Value);
        var metadata = list.Descendants().Single(element =>
            element.Attribute("Text")?.Value == "{Binding Metadata}");
        Assert.Equal("Wrap", metadata.Attribute("TextWrapping")?.Value);
        Assert.Equal("*", metadata.Parent!.Elements().First().Elements().First()
            .Attribute("Width")?.Value);

        var action = list.Descendants().Single(element =>
            element.Attribute("Click")?.Value == "OnActionClick");
        Assert.Equal("{Binding ActionLabel}", action.Attribute("Content")?.Value);
        Assert.Equal("{Binding}", action.Attribute("Tag")?.Value);
        Assert.NotSame(metadata.Parent, action.Parent);
        var dismiss = list.Descendants().Single(element =>
            element.Attribute("Click")?.Value == "OnDismissClick");
        Assert.Equal("{Binding DismissAutomationName}", dismiss.Attribute("AutomationProperties.Name")?.Value);
        Assert.Equal("1", dismiss.Attribute("Grid.Column")?.Value);
        Assert.Equal("Grid", dismiss.Parent!.Name.LocalName);
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
