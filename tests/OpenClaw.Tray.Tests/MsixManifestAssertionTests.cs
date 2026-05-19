using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Structural assertions on the MSIX <c>Package.appxmanifest</c> files for the
/// tray and the CommandPalette extension. These pin contracts that govern what
/// the signed MSIX is allowed to claim about itself: capabilities, identity,
/// publisher and startup-task TaskId. Manifest drift breaks signing (publisher
/// mismatch), breaks privacy expectations (extra capabilities silently slipping
/// in), or breaks the in-app StartupTask wiring (TaskId drift).
///
/// CI patches Identity Name / Publisher / Version into both manifests before
/// build. The tests here cover the repo-source values plus the values CI is
/// expected to inject; we read the repo files directly because running tests
/// against the patched build output would require packaging tooling that the
/// unit-test target deliberately does not depend on.
/// </summary>
public sealed class MsixManifestAssertionTests
{
    private const string AppxFoundationNs = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private const string AppxUapNs = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private const string AppxUap5Ns = "http://schemas.microsoft.com/appx/manifest/uap/windows10/5";
    private const string AppxUap3Ns = "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";
    private const string AppxRescapNs = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

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

    private static XDocument LoadManifest(params string[] relativePathSegments)
    {
        var path = Path.Combine(new[] { GetRepositoryRoot() }.Concat(relativePathSegments).ToArray());
        return XDocument.Load(path);
    }

    // ---- Tray package ------------------------------------------------------

    private static XDocument LoadTrayManifest() =>
        LoadManifest("src", "OpenClaw.Tray.WinUI", "Package.appxmanifest");

    [Fact]
    public void Tray_CapabilitySet_IsExactlyTheAuditedList()
    {
        // Privacy / security review pin: adding a capability silently bypasses the
        // capability-audit review and may also block sideload trust if the user
        // rejects the new prompt. If you need a new capability, update both this
        // test AND docs/SETUP.md in the same change so the privacy story stays
        // truthful.
        var doc = LoadTrayManifest();
        var caps = doc.Descendants(XName.Get("Capabilities", AppxFoundationNs)).Single();

        var capabilityNames = caps.Elements(XName.Get("Capability", AppxFoundationNs))
            .Select(e => (string?)e.Attribute("Name"))
            .Where(n => n != null)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        var deviceCapabilityNames = caps.Elements(XName.Get("DeviceCapability", AppxFoundationNs))
            .Select(e => (string?)e.Attribute("Name"))
            .Where(n => n != null)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        var rescapNames = caps.Elements(XName.Get("Capability", AppxRescapNs))
            .Select(e => (string?)e.Attribute("Name"))
            .Where(n => n != null)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "internetClient" }, capabilityNames);
        Assert.Equal(new[] { "location", "microphone", "webcam" }, deviceCapabilityNames);
        Assert.Equal(new[] { "runFullTrust" }, rescapNames);
    }

    [Fact]
    public void Tray_DeclaresOpenclawProtocolExtension()
    {
        var doc = LoadTrayManifest();
        var protocol = doc.Descendants(XName.Get("Protocol", AppxUapNs)).SingleOrDefault();
        Assert.NotNull(protocol);
        Assert.Equal("openclaw", (string?)protocol!.Attribute("Name"));
    }

    private const string AppxComNs = "http://schemas.microsoft.com/appx/manifest/com/windows10";
    private const string AppxDesktopNs = "http://schemas.microsoft.com/appx/manifest/desktop/windows10";

    [Fact]
    public void Tray_DeclaresStartupTaskExtensionMatchingAutoStartManager()
    {
        // The TaskId here MUST match AutoStartManager.StartupTaskId. If you rename
        // either, rename both — Windows StartupTask lookup is case-sensitive and
        // silently returns DisabledByPolicy on mismatch (no exception), which would
        // make the Settings toggle appear stuck off.
        var doc = LoadTrayManifest();
        var startupTask = doc.Descendants(XName.Get("StartupTask", AppxUap5Ns)).SingleOrDefault();
        Assert.NotNull(startupTask);
        Assert.Equal("OpenClawCompanionStartup", (string?)startupTask!.Attribute("TaskId"));
        Assert.Equal("false", (string?)startupTask.Attribute("Enabled"));
    }

    [Fact]
    public void Tray_DeclaresToastNotificationActivationExtension()
    {
        // Without windows.toastNotificationActivation, MSIX packaged apps do NOT
        // appear in Settings > Notifications until they fire a toast under package
        // identity (and even then it can be delayed by several minutes). The pair
        // of <com:Extension windows.comServer> + <desktop:Extension
        // windows.toastNotificationActivation> registers the activator CLSID and
        // makes the entry appear immediately on install. The two CLSIDs MUST
        // match each other; this test pins both halves of the contract.
        var doc = LoadTrayManifest();

        var comClass = doc.Descendants(XName.Get("Class", AppxComNs)).SingleOrDefault();
        Assert.NotNull(comClass);
        var comClassId = (string?)comClass!.Attribute("Id");

        var toastActivation = doc.Descendants(XName.Get("ToastNotificationActivation", AppxDesktopNs)).SingleOrDefault();
        Assert.NotNull(toastActivation);
        var toastClsid = (string?)toastActivation!.Attribute("ToastActivatorCLSID");

        Assert.False(string.IsNullOrEmpty(comClassId), "COM class Id missing from manifest");
        Assert.False(string.IsNullOrEmpty(toastClsid), "ToastActivatorCLSID missing from manifest");
        Assert.Equal(comClassId, toastClsid);

        // App.OnLaunched MUST short-circuit '-ToastActivator' or Windows-spawned
        // activator instances will fight the singleton mutex. Pin the early-exit.
        var appXamlCs = File.ReadAllText(Path.Combine(GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "App.xaml.cs"));
        Assert.Contains("\"-ToastActivator\"", appXamlCs);
    }

    [Fact]
    public void Tray_PrivacyListIcon_HasAllRequiredUnplatedTargetSizes()
    {
        // Settings > Privacy lists (Camera, Microphone, Location) render the per-app
        // icon at small sizes (16, 20, 24, 32, 48 px). When a fitting altform-unplated
        // variant is missing, Windows falls back to the plated tile with the manifest
        // BackgroundColor as fill — which appears as a system-accent (blue) square
        // behind our lobster. Reported by Mike during the MSIX-E2E manual test pass.
        //
        // Required minimum set covers the sizes Settings > Privacy is documented to
        // request. Add more if you observe additional blue-background fallbacks.
        var assetsDir = Path.Combine(GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Assets");

        foreach (var size in new[] { 16, 20, 24, 32, 44, 48, 256 })
        {
            var fileName = $"Square44x44Logo.targetsize-{size}_altform-unplated.png";
            Assert.True(File.Exists(Path.Combine(assetsDir, fileName)),
                $"Missing unplated icon variant '{fileName}'. Settings > Privacy will fall back to the plated tile, rendering with the manifest BackgroundColor.");
        }
    }

    [Fact]
    public void Tray_TargetDeviceFamily_IsDesktopOnly_OnSupportedFloor()
    {
        var doc = LoadTrayManifest();
        var families = doc.Descendants(XName.Get("TargetDeviceFamily", AppxFoundationNs))
            .Select(e => ((string?)e.Attribute("Name"), (string?)e.Attribute("MinVersion")))
            .ToArray();

        Assert.Single(families);
        Assert.Equal(("Windows.Desktop", "10.0.19041.0"), families[0]);
    }

    [Fact]
    public void Tray_Identity_PublisherStartsWithExpectedSubject()
    {
        // Publisher in the manifest must match the Azure Trusted Signing cert
        // subject EXACTLY at build time. CI does not patch Publisher (only Name /
        // Version), so any drift here ships as the published value.
        var doc = LoadTrayManifest();
        var identity = doc.Descendants(XName.Get("Identity", AppxFoundationNs)).Single();
        var publisher = (string?)identity.Attribute("Publisher");
        Assert.NotNull(publisher);
        Assert.StartsWith("CN=Scott Hanselman", publisher!);
    }

    [Fact]
    public void Tray_Identity_VersionIsFourPart()
    {
        // MSIX requires X.Y.Z.0 (4-part). CI re-patches this from the tag during
        // release, but the repo-source value must already be a valid 4-part so
        // local Release builds and ad-hoc msbuild invocations don't fail.
        var doc = LoadTrayManifest();
        var version = (string?)doc.Descendants(XName.Get("Identity", AppxFoundationNs)).Single().Attribute("Version");
        Assert.NotNull(version);
        var parts = version!.Split('.');
        Assert.Equal(4, parts.Length);
        Assert.All(parts, p => Assert.True(int.TryParse(p, out _), $"Version segment '{p}' is not an integer"));
        Assert.Equal("0", parts[3]);
    }

    // ---- CommandPalette package -------------------------------------------

    private static XDocument LoadCmdPalManifest() =>
        LoadManifest("src", "OpenClaw.CommandPalette", "Package.appxmanifest");

    [Fact]
    public void CmdPal_Identity_DoesNotShipMicrosoftPlaceholder()
    {
        // The VS extension template ships with Publisher=CN=Microsoft Corporation
        // and PublisherDisplayName="A Lone Developer". Both are recipes for an
        // unsigned-publisher install warning on user machines. CI patches them at
        // build time; the repo-source values must already be safe defaults.
        var doc = LoadCmdPalManifest();
        var identity = doc.Descendants(XName.Get("Identity", AppxFoundationNs)).Single();
        var publisher = (string?)identity.Attribute("Publisher");
        Assert.NotNull(publisher);
        Assert.DoesNotContain("Microsoft Corporation", publisher!);

        var publisherDisplay = (string?)doc.Descendants(XName.Get("PublisherDisplayName", AppxFoundationNs)).Single();
        Assert.NotEqual("A Lone Developer", publisherDisplay);
    }

    [Fact]
    public void CmdPal_Identity_NameIsNamespacedUnderTrayPackage()
    {
        // Both packages ship under the same publisher; namespacing the cmdpal
        // identity under the tray identity keeps the two visibly related in
        // Get-AppxPackage output and prevents accidental name collisions with
        // unrelated extensions in the user's package store.
        var doc = LoadCmdPalManifest();
        var identityName = (string?)doc.Descendants(XName.Get("Identity", AppxFoundationNs)).Single().Attribute("Name");
        Assert.NotNull(identityName);
        Assert.StartsWith("OpenClaw.Companion", identityName!);
    }

    [Fact]
    public void CmdPal_Identity_PublisherMatchesTrayPublisher()
    {
        // The same Azure Trusted Signing cert signs both packages; the manifests
        // must declare the same Publisher subject or signing fails with an opaque
        // "publisher mismatch" error.
        var tray = LoadTrayManifest();
        var cmdpal = LoadCmdPalManifest();
        var trayPublisher = (string?)tray.Descendants(XName.Get("Identity", AppxFoundationNs)).Single().Attribute("Publisher");
        var cmdpalPublisher = (string?)cmdpal.Descendants(XName.Get("Identity", AppxFoundationNs)).Single().Attribute("Publisher");
        Assert.Equal(trayPublisher, cmdpalPublisher);
    }

    [Fact]
    public void CmdPal_DeclaresCommandPaletteAppExtension()
    {
        var doc = LoadCmdPalManifest();
        var appExt = doc.Descendants(XName.Get("AppExtension", AppxUap3Ns)).SingleOrDefault();
        Assert.NotNull(appExt);
        Assert.Equal("com.microsoft.commandpalette", (string?)appExt!.Attribute("Name"));
    }

    [Fact]
    public void CmdPal_TargetDeviceFamily_IsDesktopOnly()
    {
        // The repo template included Windows.Universal which is meaningless for a
        // Win32 cmdpal extension and forces unnecessary universal-app validation
        // during signing.
        var doc = LoadCmdPalManifest();
        var families = doc.Descendants(XName.Get("TargetDeviceFamily", AppxFoundationNs))
            .Select(e => (string?)e.Attribute("Name"))
            .ToArray();
        Assert.Equal(new[] { "Windows.Desktop" }, families);
    }
}
