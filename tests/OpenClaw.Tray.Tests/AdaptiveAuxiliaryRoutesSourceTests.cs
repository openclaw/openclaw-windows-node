using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

public sealed class AdaptiveAuxiliaryRoutesSourceTests
{
    // Native pages cannot be mounted in Tray.Tests. Retire these narrow-measurement
    // source guards when native route tests cover populated rows and focused actions.
    [Fact]
    public void NotificationsConstrainTextAndStackActionsWithoutRebuildingItems()
    {
        var markup = Load("NotificationsPage");
        var content = Named(markup, "NotificationContent");
        Assert.Equal("Grid", content.Elements().First().Name.LocalName);
        Assert.Contains(content.Descendants(), element =>
            element.Attribute("Text")?.Value == "{Binding Metadata}" &&
            element.Attribute("TextWrapping")?.Value == "WrapWholeWords");
        Assert.Contains(markup.Descendants(), element =>
            element.Name.LocalName == "Setter" &&
            element.Attribute("Property")?.Value == "HorizontalContentAlignment" &&
            element.Attribute("Value")?.Value == "Stretch");
        Assert.Equal("OnNotificationLayoutSizeChanged",
            Named(markup, "NotificationActions").Parent!.Attribute("SizeChanged")?.Value);

        var source = Read("NotificationsPage");
        var resize = source[source.IndexOf("private void OnLayoutSizeChanged", StringComparison.Ordinal)..
            source.IndexOf("internal void Initialize", StringComparison.Ordinal)];
        Assert.Contains("Grid.SetRow(actions, compact ? 1 : 0)", resize);
        Assert.Contains("Grid.SetColumnSpan(content, compact ? 2 : 1)", resize);
        Assert.Contains("actions.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal", resize);
        Assert.Contains("e.NewSize.Width", resize);
        Assert.DoesNotMatch(@"Render\(|_notificationItems\.|\.Dismiss\(|ClearAll\(|\.Navigate\(|\.Focus\(", resize);
        Assert.Equal("OnNotificationActionClick",
            Named(markup, "NotificationActions").Elements().First().Attribute("Click")?.Value);
        Assert.Equal("{Binding DismissAutomationName}",
            Named(markup, "NotificationActions").Elements().Last().Attribute(
                "AutomationProperties.Name")?.Value);
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
