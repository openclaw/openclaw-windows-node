using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Structural assertions on the AppInstaller template + the in-app update
/// service contract. The template is rendered by CI and parsed by Windows
/// AppInstaller; even a single attribute typo silently breaks auto-update
/// for every user who installed via that link, with no in-app surface to
/// notice. The tests here pin:
///
/// 1. The template is well-formed XML against the AppInstaller schema URI.
/// 2. The five placeholder tokens are present (so the CI render script's
///    substitution table is exhaustive).
/// 3. The UpdateSettings block stays quiet: AutomaticBackgroundTask only,
///    no OnLaunch UI and no downgrade rollback.
/// 4. The in-app service points at the same hosted URL the release pipeline
///    publishes (drift here would split-brain installs that polled the
///    stable architecture URL against installs that polled the in-app URL).
/// </summary>
public sealed class AppInstallerTemplateAssertionTests
{
    private static string GetRepositoryRoot()
    {
        var envRepoRoot = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(envRepoRoot) && Directory.Exists(envRepoRoot))
            return envRepoRoot;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if ((Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                 File.Exists(Path.Combine(directory.FullName, ".git"))) &&
                File.Exists(Path.Combine(directory.FullName, "README.md")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }

    private static string LoadTemplate() =>
        File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), "installer", "openclaw-companion.appinstaller.template"));

    [Fact]
    public void Template_IsWellFormedXml()
    {
        // Parse with placeholders intact — XML parsing tolerates {{TOKEN}} as
        // attribute *values* because they're just strings. If we added attribute
        // names with placeholders we'd have to render before parsing.
        var doc = XDocument.Parse(LoadTemplate());
        Assert.Equal("AppInstaller", doc.Root!.Name.LocalName);
        Assert.Equal("http://schemas.microsoft.com/appx/appinstaller/2018",
            doc.Root.Name.NamespaceName);
    }

    [Theory]
    [InlineData("{{VERSION}}")]
    [InlineData("{{PUBLISHER}}")]
    [InlineData("{{PROCESSOR_ARCHITECTURE}}")]
    [InlineData("{{MSIX_URI}}")]
    [InlineData("{{APPINSTALLER_URI}}")]
    public void Template_DeclaresExpectedPlaceholder(string token)
    {
        // scripts/render-appinstaller.ps1 substitutes exactly these five tokens.
        // If you add a new placeholder here, also add a -replace in the script
        // AND a CI step parameter. If you remove one, the renderer silently
        // ships the literal {{TOKEN}} string to AppInstaller which fails to parse.
        Assert.Contains(token, LoadTemplate());
    }

    [Fact]
    public void Template_UsesQuietBackgroundUpdateSettingsOnly()
    {
        var doc = XDocument.Parse(LoadTemplate());
        XNamespace ns = "http://schemas.microsoft.com/appx/appinstaller/2018";

        Assert.Empty(doc.Descendants(ns + "OnLaunch"));
        Assert.Empty(doc.Descendants(ns + "ForceUpdateFromAnyVersion"));

        var backgroundTasks = doc.Descendants(ns + "AutomaticBackgroundTask").ToArray();
        Assert.Single(backgroundTasks);
    }

    [Fact]
    public void Template_UsesArchitectureSpecificMainPackage()
    {
        var doc = XDocument.Parse(LoadTemplate());
        XNamespace ns = "http://schemas.microsoft.com/appx/appinstaller/2018";
        Assert.Empty(doc.Descendants(ns + "MainBundle"));

        var mainPackage = doc.Descendants(ns + "MainPackage").Single();
        Assert.Equal("OpenClaw.Companion", (string?)mainPackage.Attribute("Name"));
        Assert.Equal("{{PROCESSOR_ARCHITECTURE}}", (string?)mainPackage.Attribute("ProcessorArchitecture"));
        Assert.Equal("{{MSIX_URI}}", (string?)mainPackage.Attribute("Uri"));
    }

    [Fact]
    public void InAppService_PointsAtSameStableArchitectureUrlsAsReleaseChannel()
    {
        var service = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Services", "AppInstallerUpdateService.cs"));

        Assert.Contains("https://openclaw.github.io/openclaw-windows-node/openclaw-x64.appinstaller", service);
        Assert.Contains("https://openclaw.github.io/openclaw-windows-node/openclaw-arm64.appinstaller", service);

        var ci = File.ReadAllText(Path.Combine(GetRepositoryRoot(), ".github", "workflows", "ci.yml"));
        Assert.Contains("https://openclaw.github.io/openclaw-windows-node/openclaw-x64.appinstaller", ci);
        Assert.Contains("https://openclaw.github.io/openclaw-windows-node/openclaw-arm64.appinstaller", ci);
    }

    [Fact]
    public void InAppService_DoesNotForceShutdownByDefault()
    {
        var service = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Services", "AppInstallerUpdateService.cs"));
        var app = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "App.xaml.cs"));

        Assert.Contains("bool forceRestart = false", service);
        Assert.Contains("AddPackageByAppInstallerOptions.None", service);
        Assert.Contains("CheckForUpdateAsync()", app);
        Assert.DoesNotContain("TryApplyUpdateAsync()", app);
        Assert.DoesNotContain("TryApplyUpdateAsync(forceRestart: true", app);
    }

    [Fact]
    public void InAppService_DoesNotReportPackagesInUseAsNoUpdateAvailable()
    {
        var service = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Services", "AppInstallerUpdateService.cs"));
        var app = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "App.xaml.cs"));

        Assert.Contains("HResultPackagesInUse", service);
        Assert.Contains("UpdatePendingRestart", service);
        Assert.Contains("UpdatePendingRestart", app);
        Assert.DoesNotContain("0x80073D02 => new UpdateResult(UpdateOutcome.NoUpdateAvailable", service);
    }

    [Fact]
    public void InAppService_DoesNotReportMissingDeploymentHResultAsCurrent()
    {
        var service = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Services", "AppInstallerUpdateService.cs"));

        Assert.Contains("HResultPackageAlreadyExists => new UpdateResult(UpdateOutcome.NoUpdateAvailable", service);
        Assert.Contains("0 => new UpdateResult(UpdateOutcome.Failed", service);
        Assert.DoesNotContain("0 or HResultPackageAlreadyExists", service);
    }

    [Fact]
    public void ManualUpdateCheck_IsMetadataOnly()
    {
        var service = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Services", "AppInstallerUpdateService.cs"));
        var app = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "App.xaml.cs"));

        Assert.Contains("CheckForUpdateAsync", service);
        Assert.Contains("ParseAppInstallerVersion", service);
        Assert.Contains("ClassifyPublishedVersion", service);
        Assert.Contains("UpdateAvailable", service);
        Assert.Contains("AppInstallerUpdateService.CheckForUpdateAsync()", app);
        Assert.DoesNotContain("var outcome = await AppInstallerUpdateService.TryApplyUpdateAsync()", app);
    }

    [Fact]
    public void InAppService_UsesCurrentPackageVolumeBeforeDefaultFallback()
    {
        var service = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Services", "AppInstallerUpdateService.cs"));

        Assert.Contains("ResolveCurrentPackageVolume(manager)", service);
        Assert.Contains("Package.Current.InstalledLocation.Path", service);
        Assert.Contains("manager.FindPackageVolumes()", service);
        Assert.Contains("manager.GetDefaultPackageVolume()", service);
    }

    [Fact]
    public void ToastActivatorColdLaunch_DoesNotExitBeforeSingleInstanceGuard()
    {
        var app = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "App.xaml.cs"));

        Assert.Contains("SingleInstanceLaunchGuard.Acquire", app);
        Assert.DoesNotContain("Environment.Exit(0);", app);
    }

    [Fact]
    public void ProductionSettingsUi_DoesNotContainBlueLobsterTestMarker()
    {
        var hub = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml"));
        var settings = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Pages", "SettingsPage.xaml"));

        Assert.Contains("Assets/SidebarIcons/Settings.svg", hub);
        Assert.DoesNotContain("SettingsBlueLobster", hub);
        Assert.DoesNotContain("SettingsBlueLobster", settings);
        Assert.DoesNotContain("Blue lobster update test icon", settings);
        Assert.False(File.Exists(Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Assets", "SidebarIcons", "SettingsBlueLobster.svg")));
    }

    [Fact]
    public void HostingValidationScript_ChecksMimeLengthAndRange()
    {
        var script = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), "scripts", "validate-appinstaller-hosting.ps1"));

        Assert.Contains("application/appinstaller", script);
        Assert.Contains("application/msix", script);
        Assert.Contains("Scheme -ne 'https'", script);
        Assert.Contains("Content-Length", script);
        Assert.Contains("Range = 'bytes=0-0'", script);
        Assert.Contains("StatusCode -ne 206", script);
    }

    [Fact]
    public void AppInstallerUpdateSmokeScript_BindsSingleHttpListenerAndSelfChecks()
    {
        var script = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), "scripts", "test-appinstaller-update.ps1"));

        Assert.DoesNotContain("$listener = [System.Net.HttpListener]::new()", script);
        Assert.Contains("Invoke-WebRequest \"$baseUri/openclaw.appinstaller\"", script);
        Assert.Contains("$listenerJob.State -eq 'Failed'", script);
    }
}
