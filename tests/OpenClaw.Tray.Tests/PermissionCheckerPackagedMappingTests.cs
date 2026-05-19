using System;
using System.IO;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pins the contract between WinRT's <c>AppCapabilityAccessStatus</c> (the
/// per-package consent state Windows surfaces for MSIX apps) and the onboarding
/// UI's <c>PermissionChecker.PermissionStatus</c>.
///
/// We cannot instantiate a real <c>AppCapability</c> from this xUnit process
/// — the WinRT factory throws E_FAIL outside an MSIX-launched host — and the
/// tray.Tests target is net10.0 (not net10.0-windows), so we cannot even import
/// the WinRT enum. We pin the contract as source-text assertions on the
/// production switch arms (see also the manifest assertions in
/// <c>MsixManifestAssertionTests</c>).
/// </summary>
public sealed class PermissionCheckerPackagedMappingTests
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

    private static string LoadPermissionCheckerSource() =>
        File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Onboarding", "Services", "PermissionChecker.cs"));

    [Fact]
    public void PackagedBranch_BranchesOnPackageHelper_ForCameraMicrophoneLocation()
    {
        // The three checks that have a packaged path MUST gate on PackageHelper.IsPackaged
        // and dispatch to AppCapability.Create("<capability>"). Anything else means a
        // packaged build is still reading the unpackaged registry / DeviceAccessInformation
        // surface, which returns wrong answers on MSIX (and was the original bug we are
        // closing here).
        var src = LoadPermissionCheckerSource();

        Assert.Contains("if (PackageHelper.IsPackaged)", src);
        Assert.Contains("CheckAppCapability(\"webcam\"", src);
        Assert.Contains("CheckAppCapability(\"microphone\"", src);
        Assert.Contains("CheckAppCapability(\"location\"", src);
    }

    [Fact]
    public void PackagedBranch_AppCapabilityFactoryUsesExpectedCapabilityNames()
    {
        // AppCapability.Create takes the capability name as a lowercase string that
        // must match the Name attribute declared in Package.appxmanifest. If either
        // side drifts the OS returns DeniedBySystem permanently and the user sees
        // a hopeless "denied by system" status with no recovery path.
        var src = LoadPermissionCheckerSource();

        Assert.Contains("AppCapability.Create(capabilityName)", src);
    }

    [Theory]
    [InlineData("AppCapabilityAccessStatus.Allowed",            "PermissionStatus.Granted", "Onboarding_Perm_Allowed")]
    [InlineData("AppCapabilityAccessStatus.UserPromptRequired", "PermissionStatus.Unknown", "Onboarding_Perm_NotDetermined")]
    [InlineData("AppCapabilityAccessStatus.DeniedByUser",       "PermissionStatus.Denied",  "Onboarding_Perm_DeniedUser")]
    [InlineData("AppCapabilityAccessStatus.DeniedBySystem",     "PermissionStatus.Denied",  "Onboarding_Perm_DeniedSystem")]
    public void Mapping_HasSwitchArmForKnownStatus(string statusToken, string mappedStatusToken, string labelKey)
    {
        // Pin every documented mapping arm. A regression here means the packaged
        // onboarding row would show a wrong status (or no status) for the
        // corresponding consent state.
        var src = LoadPermissionCheckerSource();

        Assert.Contains(statusToken, src);
        Assert.Contains(mappedStatusToken, src);
        Assert.Contains(labelKey, src);
    }

    [Fact]
    public void Mapping_HasSafeUnknownDefault()
    {
        // Pin the default arm. Silent drift (e.g. mapping the default to Granted)
        // would let a new SDK enum value bypass consent entirely.
        var src = LoadPermissionCheckerSource();

        Assert.Contains("_                                             => (PermissionStatus.Unknown, \"Onboarding_Perm_NotDetermined\")", src);
    }

    [Fact]
    public void SubscribeToAccessChanges_BranchesOnPackageHelper()
    {
        // The AccessChanged subscription has to use AppCapability.AccessChanged on
        // packaged. The DeviceAccessInformation event only fires on Win32 EXEs.
        var src = LoadPermissionCheckerSource();

        Assert.Contains("SubscribeToAccessChangesPackaged(onChanged)", src);
        Assert.Contains("AppCapability.Create(\"webcam\")", src);
        Assert.Contains("AppCapability.Create(\"microphone\")", src);
        Assert.Contains("AppCapability.Create(\"location\")", src);
        Assert.Contains(".AccessChanged += handler", src);
    }
}

