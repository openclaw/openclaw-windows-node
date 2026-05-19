using System;
using System.IO;
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
/// 3. The UpdateSettings block has the documented OnLaunch / Force /
///    AutomaticBackgroundTask values (silent regression here would change
///    user-visible behavior).
/// 4. The in-app service points at the same hosted URL the release pipeline
///    publishes (drift here would split-brain installs that polled the
///    "stable" URL against installs that polled the "in-app" URL).
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
    [InlineData("{{MSIX_X64_URI}}")]
    [InlineData("{{MSIX_ARM64_URI}}")]
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
    public void Template_HasDocumentedUpdateSettings()
    {
        var doc = XDocument.Parse(LoadTemplate());
        XNamespace ns = "http://schemas.microsoft.com/appx/appinstaller/2018";

        var onLaunch = doc.Descendants(ns + "OnLaunch").SingleOrDefault();
        Assert.NotNull(onLaunch);
        Assert.Equal("24",   (string?)onLaunch!.Attribute("HoursBetweenUpdateChecks"));
        Assert.Equal("true", (string?)onLaunch.Attribute("ShowPrompt"));
        Assert.Equal("false", (string?)onLaunch.Attribute("UpdateBlocksActivation"));

        Assert.Contains(doc.Descendants(ns + "ForceUpdateFromAnyVersion"),
            e => e.Value == "true");
        Assert.Contains(doc.Descendants(ns + "AutomaticBackgroundTask"), _ => true);
    }

    [Fact]
    public void Template_MainBundleNameMatchesProductionPackageIdentity()
    {
        // The MainBundle Name must equal Package.appxmanifest Identity Name
        // (after CI patches it to OpenClaw.Companion). Drift here = Windows
        // refuses to apply the update because it sees a different package.
        var doc = XDocument.Parse(LoadTemplate());
        XNamespace ns = "http://schemas.microsoft.com/appx/appinstaller/2018";
        var mainBundle = doc.Descendants(ns + "MainBundle").Single();
        Assert.Equal("OpenClaw.Companion", (string?)mainBundle.Attribute("Name"));
    }

    [Fact]
    public void InAppService_PointsAtSameStableUrlAsReleaseChannel()
    {
        // The CI release job copies the rendered file to "latest.appinstaller"
        // and the README documents the gh-pages URL. The in-app
        // AppInstallerUpdateService MUST poll that same URL; otherwise the
        // in-app "Check for updates" button and the OS background poll see
        // different versions.
        var service = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Services", "AppInstallerUpdateService.cs"));

        Assert.Contains("https://openclaw.github.io/openclaw-windows-node/latest.appinstaller", service);

        // And the same URL must appear in the CI workflow (the render step) so
        // the rendered file points at the same stable URL it is hosted at.
        var ci = File.ReadAllText(Path.Combine(GetRepositoryRoot(), ".github", "workflows", "ci.yml"));
        Assert.Contains("https://openclaw.github.io/openclaw-windows-node/latest.appinstaller", ci);
    }
}
