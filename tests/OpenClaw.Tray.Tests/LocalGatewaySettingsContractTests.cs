using System.Xml.Linq;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

public sealed class LocalGatewaySettingsContractTests
{
    // Retire when mounted Settings tests can exercise package teardown without a live installation.
    [Fact]
    public void SettingsUsesExplicitOwnershipAndKeepsNativeRemovalOutOfWslUninstall()
    {
        var tray = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI");
        var page = File.ReadAllText(Path.Combine(tray, "Pages", "SettingsPage.xaml.cs"));
        Assert.Contains("LocalGatewaySettings.Classify(_localGatewayTarget)", page);
        Assert.DoesNotContain("LocalGatewayUrlClassifier.IsLocalGatewayUrl(settings.GatewayUrl)", page);
        Assert.Contains("LocalGatewaySettings.IsSameTarget(target, CurrentApp.Registry?.GetActive())", page);
        Assert.Contains("binding.AcquireAsync", page);
        Assert.Contains("if (binding.Exists)", page);
        var nativeRemovalStart = page.IndexOf("if (kind is LocalGatewayKind.Native or LocalGatewayKind.LegacyNative)", StringComparison.Ordinal);
        var wslRemovalStart = page.IndexOf("if (CurrentApp.ConnectionManager is { } wslManager)", nativeRemovalStart, StringComparison.Ordinal);
        var removalStart = page.IndexOf("private async Task OnRemoveGatewayAsync()", StringComparison.Ordinal);
        Assert.DoesNotContain("binding.Exists", page[removalStart..nativeRemovalStart]);
        Assert.Contains("binding.Exists", page[nativeRemovalStart..wslRemovalStart]);
        Assert.DoesNotContain("binding.Exists", page[wslRemovalStart..]);
        Assert.Contains("SetupRunLock.TryAcquire", page);
        Assert.Contains("RemoveNativeGatewayAsync(target", page);
        Assert.Contains("psi.ArgumentList.Add(\"--gateway-binding\")", page);
        Assert.Contains("_localGatewayKind == LocalGatewayKind.Wsl", page);
        Assert.DoesNotContain("SettingsPage_RemovingDistro", page);
        var connection = File.ReadAllText(Path.Combine(tray, "Pages", "ConnectionPage.xaml.cs"));
        Assert.DoesNotContain("managed WSL gateway's local address", connection);
        Assert.Contains("ConnectionPage_LocalConflictAddress", connection);
        var diagnostics = File.ReadAllText(Path.Combine(tray, "Pages", "DebugPage.xaml.cs"));
        Assert.Contains("LocalGatewaySettings.DisplayedGatewayUrl(", diagnostics);
        Assert.Contains("_registry.Changed += OnGatewayRegistryChanged", diagnostics);
        var cli = File.ReadAllText(Path.Combine(tray, "CliUninstallHandler.cs"));
        Assert.Contains("setupArgs.AddRange([\"--gateway-id\"", cli);
        var engine = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.SetupEngine", "Program.cs"));
        Assert.Contains("result = await new WslGatewayRemoval().RunAsync", engine);

        var window = File.ReadAllText(Path.Combine(tray, "Services", "WindowManager.cs"));
        var wizard = window[window.IndexOf("public async Task ShowGatewayWizardAsync()", StringComparison.Ordinal)..
            window.IndexOf("private void NotifyIncompleteNativeConnection()", StringComparison.Ordinal)];
        Assert.Contains("NativePackageFamilyName is not null", wizard);
        Assert.Contains("await ShowLocalAiSetupAsync();", wizard);
        Assert.Contains("existingWslGateway:", wizard);
        var setup = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs"));
        Assert.Contains("_existingWslSetup.RequireCurrent(registry.GetActive())", setup);
        Assert.Contains("ExpectedGatewayId: _existingWslSetup?.Record.Id", setup);
        var welcome = setup[setup.IndexOf("public void NavigateToWelcome(", StringComparison.Ordinal)..
            setup.IndexOf("internal async Task<NativeGatewayEligibility>", StringComparison.Ordinal)];
        Assert.Contains("ClearExistingWslSetup()", welcome);
        Assert.True(welcome.IndexOf("ResetLocalAiRecoveryMode()", StringComparison.Ordinal) <
            welcome.IndexOf("ClearExistingWslSetup()", StringComparison.Ordinal));
        Assert.Contains("_existingWslSetup?.Restore(_config)", setup);
        var clearPin = setup[setup.IndexOf("private void ClearExistingWslSetup()", StringComparison.Ordinal)..
            setup.IndexOf("public bool TryNavigateToGatewayInstalledMilestone()", StringComparison.Ordinal)];
        Assert.True(clearPin.IndexOf("ResetLocalAiRecoveryMode()", StringComparison.Ordinal) <
            clearPin.IndexOf("_existingWslSetup?.Restore(_config)", StringComparison.Ordinal));
        Assert.True(setup.IndexOf("AccessDraft = new SetupAccessDraft(_config)", StringComparison.Ordinal) <
            setup.IndexOf("_existingWslSetup?.Apply(_config)", StringComparison.Ordinal));
        var finalization = setup[setup.IndexOf("private async Task<StepResult> ApplyWindowsNodeContextCoreAsync()", StringComparison.Ordinal)..];
        Assert.True(finalization.IndexOf("UsesWslWorkspaceFinalization", StringComparison.Ordinal) <
            finalization.IndexOf("_existingWslSetup.RequireCurrent", StringComparison.Ordinal));
        Assert.True(wizard.IndexOf("await ShowLocalAiSetupAsync();", StringComparison.Ordinal) <
            wizard.IndexOf("await EnsureSetupWindowAsync(", StringComparison.Ordinal));
    }

    [Fact]
    public void NativeCopyDescribesPackageWideConsequencesAndPreservation()
    {
        var resources = XDocument.Load(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw"));
        string Text(string key) => resources.Descendants("data").Single(
            item => (string?)item.Attribute("name") == key).Element("value")!.Value;
        Assert.Contains("Other Companion profiles", Text("SettingsPage_RemoveNativeBody"));
        Assert.Contains("Store app", Text("SettingsPage_RemoveNativeBody"));
        Assert.DoesNotContain("WSL", Text("SettingsPage_SetupNativeDescription"));
        Assert.DoesNotContain("WSL", Text("SettingsPage_GatewayOnboardingDescription.Text"));
        Assert.DoesNotContain("distro", Text("SettingsPage_RemovingGateway"));
        foreach (var key in new[] { "ConnectionPage_LocalConflictAddress", "ConnectionPage_LocalConflictOwnership",
                     "ConnectionPage_LocalConflictRecovery", "Onboarding_LocalAi_NativeCheckingMessage", "SettingsPage_LegacyNativeAiMessage" })
            Assert.DoesNotContain("WSL", Text(key));
    }
}
