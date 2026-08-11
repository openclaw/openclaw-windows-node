using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

public sealed class PermissionsPageContractTests
{
    private static readonly XNamespace XNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void PermissionsPage_Xaml_PreservesCurrentSectionOrderAndMarkers()
    {
        var xaml = ReadPermissionsXaml();

        AssertInOrder(
            xaml,
            "PermissionsPage_Permissions",
            "NodeStatusCard",
            "PermissionsPage_Capabilities",
            "PermissionsVoiceSettingsCard",
            "PermissionsPage_Integrations",
            "PermissionsPage_TextBlock_14",
            "PermissionsPage_TextBlock_69",
            "PermissionsPage_WindowsPrivacy");
        Assert.Contains("AutomationProperties.AutomationId=\"PermissionsPageMarker\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"CapabilitiesNodeModeToggle\"", xaml);
    }

    [Fact]
    public void PermissionsPage_ExecPolicyControls_PreserveDefaultChoicesAndAllowOnlyRules()
    {
        var doc = XDocument.Load(GetPermissionsXamlPath());

        var combos = doc.Descendants()
            .Where(element => element.Name.LocalName == "ComboBox")
            .ToDictionary(
                element => element.Attribute("Name")?.Value ?? element.Attribute(XNs + "Name")?.Value ?? string.Empty,
                element => element.Elements().Where(child => child.Name.LocalName == "ComboBoxItem")
                    .Select(child => child.Attribute("Tag")?.Value)
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .ToArray(),
                StringComparer.Ordinal);

        Assert.Equal(new[] { "deny", "allow", "prompt" }, combos["DefaultActionCombo"]);
        Assert.DoesNotContain("NewRuleAction", combos.Keys);
        Assert.Contains("Action = \"allow\"", ReadPermissionsCodeBehind());
    }

    [Fact]
    public void App_MapsPermissionsPage_ToTransientViewModel()
    {
        var app = ReadSource("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");

        Assert.Contains("[typeof(Pages.PermissionsPage)] = typeof(PermissionsPageViewModel)", app);
    }

    [Fact]
    public void HubWindow_NoLongerInitializesPermissionsPageDirectly()
    {
        var hub = ReadSource("src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml.cs");

        Assert.DoesNotContain("case PermissionsPage permissions: permissions.Initialize();", hub);
    }

    [Fact]
    public void PermissionsPage_CodeBehind_UsesViewModelBoundary_AndAvoidsDirectWriters()
    {
        var source = ReadPermissionsCodeBehind();

        Assert.Contains("DataContextChanged += OnDataContextChanged;", source);
        Assert.DoesNotContain("CurrentApp.Settings.Save()", source);
        Assert.DoesNotContain("CurrentApp.Settings.EnableNodeMode", source);
        Assert.DoesNotContain("CurrentApp.Settings.EnableMcpServer", source);
        Assert.DoesNotContain("CurrentApp.ConnectionManager", source);
        Assert.DoesNotContain("Saved += OnSettingsSaved", source);
        Assert.DoesNotContain("StateChanged += OnConnectionStateChanged", source);
        Assert.DoesNotContain("ExecApprovalsStore.GetSnapshotAsync", source);
        Assert.DoesNotContain("ExecApprovalsStore.ReplaceAsync", source);
    }

    [Fact]
    public void PermissionsPage_RemoveRuleBindsStableRuleIdentityAndPreservesAutomationIndex()
    {
        var xaml = ReadPermissionsXaml();
        var source = ReadPermissionsCodeBehind();

        Assert.Contains("Tag=\"{Binding Rule}\"", xaml);
        Assert.DoesNotContain("Tag=\"{Binding Index}\"", xaml);
        Assert.Contains("Rule = rule", source);
        Assert.Contains("button.Tag is PermissionsExecApprovalRule rule", source);
        Assert.Contains("RemoveExecApprovalRuleAsync(rule)", source);
        Assert.Contains("RemoveExecPolicyRuleButton_{index}", source);
        Assert.DoesNotContain("RemoveExecApprovalRuleAtAsync", source);
    }

    [Fact]
    public void PermissionsPageViewModel_StaysWinUiAndAppFree()
    {
        var source = ReadSource("src", "OpenClaw.Tray.WinUI", "Presentation", "PermissionsPageViewModel.cs");

        foreach (var banned in new[]
        {
            "Microsoft.UI",
            "Application.Current",
            "SettingsManager",
            "CurrentApp",
            " File.",
            " Directory.",
            " Path.",
            "Process.",
            "Clipboard",
        })
        {
            Assert.DoesNotContain(banned, source);
        }
    }

    [Fact]
    public void PermissionsPage_UsesDataContextChangedLikeSettingsPage()
    {
        var permissions = ReadPermissionsCodeBehind();
        var settings = ReadSource("src", "OpenClaw.Tray.WinUI", "Pages", "SettingsPage.xaml.cs");

        Assert.Contains("DataContextChanged += OnDataContextChanged;", permissions);
        Assert.Contains("DataContextChanged += OnDataContextChanged;", settings);
    }

    private static string GetPermissionsXamlPath() =>
        Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Pages", "PermissionsPage.xaml");

    private static string ReadPermissionsXaml() => File.ReadAllText(GetPermissionsXamlPath());

    private static string ReadPermissionsCodeBehind() => ReadSource(
        "src",
        "OpenClaw.Tray.WinUI",
        "Pages",
        "PermissionsPage.xaml.cs");

    private static string ReadSource(params string[] relativePathParts) =>
        File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(relativePathParts).ToArray()));

    private static void AssertInOrder(string source, params string[] expectedSnippets)
    {
        var cursor = 0;
        foreach (var snippet in expectedSnippets)
        {
            var index = source.IndexOf(snippet, cursor, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Expected to find '{snippet}' after position {cursor}.");
            cursor = index + snippet.Length;
        }
    }
}
