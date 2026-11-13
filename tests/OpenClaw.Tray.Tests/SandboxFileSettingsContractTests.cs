using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

public sealed class SandboxFileSettingsContractTests
{
    [Fact]
    public void SandboxPageKeepsPermissionsAndCleanupWithoutEditingTransferConfiguration()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var page = Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "SandboxPage.xaml");
        var document = XDocument.Load(page);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var names = document.Descendants().Select(e => (string?)e.Attribute(x + "Name")).ToArray();
        Assert.Contains("SandboxPermissionsControls", names);
        Assert.Contains("UploadCleanupWarning", names);
        Assert.DoesNotContain("TransferBudgetBox", names);
        Assert.DoesNotContain("UploadStoragePath", names);
        Assert.DoesNotContain(document.Descendants(), e => (string?)e.Attribute(x + "Uid") == "SandboxPage_SharedFilePolicy");
        Assert.DoesNotContain(document.Descendants(), e => (string?)e.Attribute(x + "Uid") == "SandboxPage_ScopePolicyDetails");
        var operations = Assert.Single(document.Descendants(),
            e => (string?)e.Attribute("AutomationProperties.AutomationId") == "SandboxContainedOperations");
        Assert.Contains(operations.Ancestors(), e => (string?)e.Attribute(x + "Name") == "ScopeExpander");

        // The page must not reset persisted hidden configuration during load or
        // ordinary permission edits. Runtime admission owns the effective limits.
        var source = File.ReadAllText(page + ".cs");
        Assert.DoesNotContain("SandboxMaxTransferBytes", source);
        Assert.DoesNotContain("SandboxUploadStagingPath", source);
        Assert.DoesNotContain("OnTransferBudgetChanged", source);
        Assert.DoesNotContain("OnChooseUploadParent", source);
    }
}
