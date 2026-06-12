using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Structural assertions on the AppInstaller template and the publishing
/// infrastructure that renders it. The template is rendered by CI and parsed
/// by Windows AppInstaller; even a single attribute typo silently breaks
/// auto-update for every user who installed via the stable feed link, with
/// no in-app surface to notice.
///
/// We deliberately don't ship an in-app "Check for updates" affordance under
/// MSIX — Windows AppInstaller's AutomaticBackgroundTask handles polling at
/// the OS level. So these tests pin only the publishing-infrastructure
/// contract: template shape, bootstrap feed files, validation scripts, and
/// the feed-update workflow.
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
        // attribute *values* because they're just strings.
        var doc = XDocument.Parse(LoadTemplate());
        Assert.Equal("AppInstaller", doc.Root!.Name.LocalName);
        Assert.Equal("http://schemas.microsoft.com/appx/appinstaller/2018",
            doc.Root.Name.NamespaceName);
    }

    [Theory]
    [InlineData("{{VERSION}}")]
    [InlineData("{{PUBLISHER}}")]
    [InlineData("{{IDENTITY_NAME}}")]
    [InlineData("{{PROCESSOR_ARCHITECTURE}}")]
    [InlineData("{{MSIX_URI}}")]
    [InlineData("{{APPINSTALLER_URI}}")]
    public void Template_DeclaresExpectedPlaceholder(string token)
    {
        // scripts/render-appinstaller.ps1 substitutes exactly these tokens.
        // If you add a new placeholder here, also add a -Replace in the script
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
        Assert.Equal("{{IDENTITY_NAME}}", (string?)mainPackage.Attribute("Name"));
        Assert.Equal("{{PROCESSOR_ARCHITECTURE}}", (string?)mainPackage.Attribute("ProcessorArchitecture"));
        Assert.Equal("{{MSIX_URI}}", (string?)mainPackage.Attribute("Uri"));
    }

    [Fact]
    public void Template_HasNoDependenciesBlock()
    {
        // The MSIX is built with WindowsAppSDKSelfContained=true, so the
        // WindowsAppRuntime is bundled inside the .msix payload. The
        // AppInstaller XML therefore must NOT declare a <Dependencies> block —
        // a stale <Dependencies> block here would either fail-to-resolve at
        // install time, or worse, silently pull an extra framework package
        // the app doesn't need.
        var doc = XDocument.Parse(LoadTemplate());
        XNamespace ns = "http://schemas.microsoft.com/appx/appinstaller/2018";

        Assert.Empty(doc.Descendants(ns + "Dependencies"));
        Assert.Empty(doc.Descendants(ns + "Package"));
    }

    [Fact]
    public void StableFeedFiles_ExistAsBootstrapPlaceholders()
    {
        foreach (var (fileName, arch) in new[]
        {
            ("openclaw-x64.appinstaller", "x64"),
            ("openclaw-arm64.appinstaller", "arm64")
        })
        {
            var path = Path.Combine(GetRepositoryRoot(), "installer", "appinstaller", fileName);
            Assert.True(File.Exists(path), $"Missing stable feed bootstrap file: {fileName}");

            var doc = XDocument.Load(path);
            XNamespace ns = "http://schemas.microsoft.com/appx/appinstaller/2018";
            Assert.Equal("0.0.0.0", (string?)doc.Root!.Attribute("Version"));
            Assert.Equal($"https://raw.githubusercontent.com/openclaw/openclaw-windows-node/main/installer/appinstaller/{fileName}",
                (string?)doc.Root.Attribute("Uri"));

            var mainPackage = doc.Descendants(ns + "MainPackage").Single();
            Assert.Equal("OpenClaw.Companion", (string?)mainPackage.Attribute("Name"));
            Assert.Equal("0.0.0.0", (string?)mainPackage.Attribute("Version"));
            Assert.Equal(arch, (string?)mainPackage.Attribute("ProcessorArchitecture"));
        }
    }

    [Fact]
    public void HostingValidationScript_ChecksMimeLengthAndRange()
    {
        var script = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), "scripts", "validate-appinstaller-hosting.ps1"));

        Assert.Contains("application/appinstaller", script);
        Assert.Contains("application/msix", script);
        Assert.Contains("AppInstallerPath", script);
        Assert.Contains("AllowGitHubContentTypes", script);
        Assert.Contains("application/octet-stream", script);
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

        // The listener lives only inside Start-Job; the parent must not
        // open a second HttpListener on the same prefix.
        Assert.DoesNotContain("$listener = [System.Net.HttpListener]::new()", script);
        Assert.Contains("Invoke-WebRequest \"$baseUri/openclaw.appinstaller\"", script);
        Assert.Contains("$listenerJob.State -eq 'Failed'", script);
    }

    [Fact]
    public void FeedUpdateWorkflow_OpensMaintainerPrAndBlocksPrereleaseFeeds()
    {
        var workflow = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), ".github", "workflows", "appinstaller-feed-pr.yml"));

        Assert.Contains("workflow_dispatch", workflow);
        Assert.Contains("pull-requests: write", workflow);
        Assert.Contains("contents: write", workflow);
        Assert.Contains("Pre-release AppInstaller feed updates are blocked", workflow);
        Assert.Contains("installer\\appinstaller", workflow);
        Assert.Contains("openclaw-x64.appinstaller", workflow);
        Assert.Contains("openclaw-arm64.appinstaller", workflow);
        Assert.Contains("gh pr create", workflow);
        Assert.Contains("--base main", workflow);
        Assert.Contains("validate-appinstaller-hosting.ps1", workflow);
        Assert.Contains("-AllowGitHubContentTypes", workflow);
        Assert.Contains("OpenClaw.Companion_${version}_x64.msix", workflow);
        Assert.Contains("OpenClaw.Companion_${version}_arm64.msix", workflow);

        // MSIX is self-contained — the workflow must not fetch or pass a
        // separate WindowsAppRuntime asset.
        Assert.DoesNotContain("Microsoft.WindowsAppRuntime", workflow);
        Assert.DoesNotContain("WindowsAppRuntimeUri", workflow);

        // Stable feed only — no wildcard alpha/staging file globbing.
        Assert.DoesNotContain("OpenClaw.Companion_*_x64.msix", workflow);
        Assert.DoesNotContain("OpenClaw.Companion_*_arm64.msix", workflow);
    }
}
