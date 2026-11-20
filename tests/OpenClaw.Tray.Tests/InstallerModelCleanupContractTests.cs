namespace OpenClaw.Tray.Tests;

// Inno admission and packaging are distinct from the native file-operation tests.
public sealed class InstallerModelCleanupContractTests
{
    private static string Installer => File.ReadAllText(
        Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "installer.iss")).Replace("\r\n", "\n");

    [Fact]
    public void ModelChoice_DefaultsToKeep_AndSilentUninstallCannotOptIn()
    {
        var source = Installer;
        var choice = source[source.IndexOf("function ChooseModelCleanup: Boolean;\nbegin", StringComparison.Ordinal)..];
        choice = choice[..choice.IndexOf("procedure RunSelectedModelCleanup;", StringComparison.Ordinal)];
        Assert.Contains("ModelCleanupSelection := 0;", choice);
        Assert.True(choice.IndexOf("ModelCleanupSelection := 0;", StringComparison.Ordinal)
            < choice.IndexOf("if UninstallSilent() then Exit;", StringComparison.Ordinal));
        Assert.DoesNotContain("RunModelHelper", choice);
        Assert.DoesNotContain("CheckCompletedStoreMigration", choice);
        Assert.Contains("Result := ShowModelReport(True);", choice);
        Assert.Contains("ModelRemoveCheck.Checked := False;", source);
        Assert.Contains("if Choosing and Result and ModelRemoveCheck.Checked then", source);
        Assert.Contains("ModelScanButton.OnClick := @CheckModelPreview;", source);
        Assert.Contains("CancelButton.OnClick := @CancelModelPreview;", source);
        Assert.Contains("CanClose := not ModelScanRunning;", source);
        Assert.Contains("if ModelScanCancelled then ModelDialog.ModalResult := mrCancel;", source);
        Assert.Contains("Result := ChooseModelCleanup;", source); // Cancel aborts admission.
    }

    [Fact]
    public void ModelRemoval_RechecksPreservation_AndDoesNotDependOnGatewayRemoval()
    {
        var source = Installer;
        var cleanup = source[source.IndexOf("procedure RunSelectedModelCleanup;", StringComparison.Ordinal)..];
        cleanup = cleanup[..cleanup.IndexOf("procedure EnsureLocalGatewayCleanupChoice;", StringComparison.Ordinal)];
        Assert.Contains("if ModelCleanupSelection = 0 then Exit;", cleanup);
        Assert.Contains("if MigrationOperationUnavailable then", cleanup);
        Assert.True(cleanup.IndexOf("CheckCompletedStoreMigration <> 0", StringComparison.Ordinal)
            < cleanup.IndexOf("RunModelHelper(ModelCleanupSelection", StringComparison.Ordinal));
        Assert.DoesNotContain("LocalGatewayCleanupRequested", cleanup);
        Assert.DoesNotContain("LocalGatewayCleanupSucceeded", cleanup);
        Assert.DoesNotContain("DelTree", cleanup);
        Assert.Contains("ShowModelReport(False);", source);
        // Reserved completion codes, not an independent selection file, carry the preview.
        Assert.Contains("if (Code < 32) or (Code > 47) then Exit;", source);
        Assert.Contains("Eligible := Code - 32;", source);
        Assert.DoesNotContain("model-selection.txt", source);
        Assert.DoesNotContain("-SelectionPath", source);
    }

    [Theory]
    [InlineData("scripts\\Uninstall-DownloadedModels.ps1", "Uninstall-DownloadedModels.ps1")]
    [InlineData("src\\OpenClaw.Shared\\Audio\\KokoroDownloadCatalog.cs", "KokoroDownloadCatalog.cs")]
    [InlineData("src\\OpenClaw.Shared\\Audio\\KokoroUninstallFiles.cs", "KokoroUninstallFiles.cs")]
    public void ModelHelper_ShipsAuthoritativeSources_AndStagesBeforePayloadRemoval(string sourcePath, string name)
    {
        var source = Installer;
        Assert.Contains($"Source: \"{sourcePath}\"; DestDir: \"{{app}}\"; Flags: ignoreversion", source);
        Assert.Contains($"ExpandConstant('{{tmp}}\\{name}')", source);
    }
}
