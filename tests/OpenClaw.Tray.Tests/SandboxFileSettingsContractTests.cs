using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

public sealed class SandboxFileSettingsContractTests
{
    [Fact]
    public void FailedBudgetSaveRestoresTheNativeControlBeforeRethrowingWithoutNotifyingSuccess()
    {
        // Keep this WinUI-only guard until the mounted NumberBox failure path is
        // covered by an isolated native UI fixture with a locked settings file.
        var source = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Pages", "SandboxPage.xaml.cs"));
        var start = source.IndexOf("private void OnTransferBudgetChanged(", StringComparison.Ordinal);
        var end = source.IndexOf("private void OnChooseUploadParent(", start, StringComparison.Ordinal);
        var handler = source[start..end];
        var save = handler.IndexOf("settings.UpdateAndSave(", StringComparison.Ordinal);
        var failure = handler.IndexOf("catch", save, StringComparison.Ordinal);
        Assert.True(failure > save, "A rejected save must restore the NumberBox.");
        var suppress = handler.IndexOf("_suppress = true;", failure, StringComparison.Ordinal);
        var restore = handler.IndexOf("sender.Value = settings.SandboxMaxTransferBytes /", failure, StringComparison.Ordinal);
        var unsuppress = handler.IndexOf("finally { _suppress = false; }", failure, StringComparison.Ordinal);
        var rethrow = handler.IndexOf("throw;", failure, StringComparison.Ordinal);
        var notify = handler.IndexOf("NotifySettingsSaved()", failure, StringComparison.Ordinal);
        Assert.True(suppress > failure && restore > suppress && unsuppress > restore);
        Assert.True(rethrow > unsuppress && notify > rethrow);
    }

    [Fact]
    public void StorageAndBudgetRemainOutsideTheUnsupportedWindowsPermissionContainer()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var document = XDocument.Load(Path.Combine(root, "src", "OpenClaw.Tray.WinUI", "Pages", "SandboxPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var permissionContainer = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "SandboxPermissionsControls");
        foreach (var name in new[] { "UploadStoragePath", "TransferBudgetBox" })
        {
            var control = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);
            Assert.DoesNotContain(permissionContainer, control.Ancestors());
        }
        var budget = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "TransferBudgetBox");
        Assert.Equal("1", (string?)budget.Attribute("Minimum"));
        Assert.Equal("1024", (string?)budget.Attribute("Maximum"));
        Assert.NotNull(budget.Attribute("AutomationProperties.LabeledBy"));
    }
}
