namespace OpenClaw.Tray.Tests;

public sealed class ReleaseSigningWorkflowTests
{
    [Fact]
    public void ReleaseWorkflow_SignsOnlyOpenClawOwnedPayloadExecutables()
    {
        var workflow = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), ".github", "workflows", "ci.yml"));

        Assert.DoesNotContain("azure/trusted-signing-action", workflow);
        Assert.DoesNotContain("AZURE_CLIENT_SECRET", workflow);
        Assert.Contains("environment: release-signing", workflow);
        Assert.Contains("id-token: write", workflow);
        Assert.Contains("uses: azure/artifact-signing-action@v2", workflow);
        Assert.Contains("endpoint: https://eus.codesigning.azure.net/", workflow);
        Assert.Contains("signing-account-name: openclaw", workflow);
        Assert.Contains("certificate-profile-name: openclaw", workflow);
        Assert.Contains("Stage x64 OpenClaw Executables for Signing", workflow);
        Assert.Contains(@"New-Item -ItemType HardLink -Path signing-input-x64\OpenClaw.Tray.WinUI.exe -Target artifacts\tray-win-x64\OpenClaw.Tray.WinUI.exe", workflow);
        Assert.DoesNotContain("signing-input-x64\\OpenClaw.SetupEngine.exe", workflow);
        Assert.DoesNotContain("signing-input-x64\\OpenClaw.SetupEngine.UI.exe", workflow);
        Assert.Contains("Sign x64 OpenClaw Executables", workflow);
        Assert.Contains("files-folder: signing-input-x64", workflow);
        Assert.Contains("Stage ARM64 OpenClaw Executables for Signing", workflow);
        Assert.Contains(@"New-Item -ItemType HardLink -Path signing-input-arm64\OpenClaw.Tray.WinUI.exe -Target artifacts\tray-win-arm64\OpenClaw.Tray.WinUI.exe", workflow);
        Assert.DoesNotContain("signing-input-arm64\\OpenClaw.SetupEngine.exe", workflow);
        Assert.DoesNotContain("signing-input-arm64\\OpenClaw.SetupEngine.UI.exe", workflow);
        Assert.Contains("Sign ARM64 OpenClaw Executables", workflow);
        Assert.Contains("files-folder: signing-input-arm64", workflow);
        Assert.Contains("files-folder-filter: exe", workflow);
        Assert.DoesNotContain("files-folder-recurse: true", workflow);
    }

    [Fact]
    public void ReleaseWorkflow_VerifiesExecutableSigningPolicy()
    {
        var workflow = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), ".github", "workflows", "ci.yml"));
        var verifier = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "scripts", "Test-ReleaseExecutableSignatures.ps1"));

        Assert.Contains("Test-ReleaseExecutableSignatures.ps1 -PayloadPath artifacts/tray-win-x64 -RequireSignedOpenClaw", workflow);
        Assert.Contains("Test-ReleaseExecutableSignatures.ps1 -PayloadPath artifacts/tray-win-arm64 -RequireSignedOpenClaw", workflow);
        Assert.Contains(@"^OpenClaw\.Tray\.WinUI\.exe$", verifier);
        Assert.DoesNotContain(@"^SetupEngine\\OpenClaw\.SetupEngine\.exe$", verifier);
        Assert.DoesNotContain(@"^SetupEngine\\OpenClaw\.SetupEngine\.UI\.exe$", verifier);
        Assert.Contains("SetupEngine\\OpenClaw.SetupEngine.exe should not be present", verifier);
        Assert.Contains("SetupEngine\\OpenClaw.SetupEngine.UI.exe should not be present", verifier);
        Assert.Contains(@"(^|\\)createdump\.exe$", verifier);
        Assert.Contains(@"(^|\\)RestartAgent\.exe$", verifier);
        Assert.Contains(@"^tools\\mxc\\[^\\]+\\wxc-exec\.exe$", verifier);
        Assert.Contains("Unknown executable in release payload", verifier);
    }

    [Fact]
    public void ReleaseWorkflow_BundlesAndVerifiesNativeRuntimeDependencies()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        var installer = File.ReadAllText(Path.Combine(root, "installer.iss"));
        var verifier = File.ReadAllText(Path.Combine(root, "scripts", "Test-ReleaseNativeDependencies.ps1"));
        var targets = File.ReadAllText(Path.Combine(root, "src", "Directory.Build.targets"));

        Assert.Contains("Test-ReleaseNativeDependencies.ps1 -PayloadPath publish -RequireAppLocalVCRuntime", workflow);
        Assert.Contains("Test-ReleaseNativeDependencies.ps1 -PayloadPath artifacts/tray-win-x64 -RequireAppLocalVCRuntime", workflow);
        Assert.Contains("Test-ReleaseNativeDependencies.ps1 -PayloadPath artifacts/tray-win-arm64 -RequireAppLocalVCRuntime -SkipNativeLoadProbe", workflow);
        Assert.Contains("https://aka.ms/vc14/vc_redist.x64.exe", workflow);
        Assert.Contains("https://aka.ms/vc14/vc_redist.arm64.exe", workflow);
        Assert.Contains("Get-AuthenticodeSignature -LiteralPath $redist.Path", workflow);
        Assert.Contains("O=Microsoft Corporation", workflow);
        Assert.Contains("-InstallerVCRedistPath vc_redist.x64.exe", workflow);
        Assert.Contains("publish-arm64 -RequireAppLocalVCRuntime -RequireInstallerVCRedist -InstallerVCRedistPath vc_redist.arm64.exe -SkipNativeLoadProbe", workflow);
        Assert.Contains("/DvcRedist=vc_redist.x64.exe", workflow);
        Assert.Contains("/DvcRedist=vc_redist.arm64.exe", workflow);
        Assert.DoesNotContain("copy vc_redist.x64.exe publish-x64", workflow);
        Assert.DoesNotContain("copy vc_redist.x64.exe publish-arm64", workflow);
        Assert.Contains("OpenClawTray-${{ needs.test.outputs.semVer }}-win-arm64.zip", workflow);
        Assert.Contains("AfterInstall: InstallVCRuntime", installer);
        Assert.Contains("Exec(", installer);
        Assert.Contains("ResultCode = 3010", installer);
        Assert.Contains("ShouldLaunchTray", installer);
        Assert.Contains("Skipping post-install tray launch", installer);
        Assert.DoesNotContain(@"Filename: ""{tmp}\vc_redist.exe""", installer);
        Assert.Contains("Get-AuthenticodeSignature -LiteralPath $File.FullName", verifier);
        Assert.Contains("Get-VCRuntimeFiles", verifier);
        Assert.Contains("vcruntime140.dll", verifier);
        Assert.DoesNotContain("libsodium.dll", verifier);
        Assert.Contains("OpenClawNativeDependencyProbe", verifier);
        Assert.Contains("Microsoft.ML.OnnxRuntime.dll", verifier);
        Assert.Contains("onnxruntime.dll", verifier);
        Assert.Contains("sherpa-onnx-c-api.dll", verifier);
        Assert.Contains("TTS native stack probe", verifier);
        Assert.Contains("SkipNativeLoadProbe", verifier);
        Assert.Contains("CopyOpenClawVCRuntimeToPublish", targets);
        Assert.Contains("ResolveOpenClawVCRuntimeFromVSInstall", targets);
        Assert.Contains("ResolveOpenClawVCRuntimeArm64FromVSInstall", targets);
        Assert.Contains("VCRuntimeMinVersion", verifier);
    }

    [Fact]
    public void ReleaseWorkflow_PausesMsixForAlpha()
    {
        var workflow = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), ".github", "workflows", "ci.yml"));

        Assert.Contains("if: false # Paused for alpha.4; ship Inno setup and portable ZIP artifacts only.", workflow);
        Assert.Contains("needs: [repo-hygiene, test, e2etests, build]", workflow);
        Assert.DoesNotContain("Download win-x64 MSIX artifact", workflow);
        Assert.DoesNotContain("Download win-arm64 MSIX artifact", workflow);
        Assert.DoesNotContain("Sign Release MSIX Packages", workflow);
        Assert.DoesNotContain(".msix", ExtractReleaseStep(workflow));
    }

    private static string ExtractReleaseStep(string workflow)
    {
        var start = workflow.IndexOf("    - name: Create Release", StringComparison.Ordinal);
        Assert.True(start >= 0, "Could not find Create Release step.");
        return workflow[start..];
    }

}
