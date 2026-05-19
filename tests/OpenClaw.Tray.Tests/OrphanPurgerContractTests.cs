using System;
using System.IO;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pin the contract of the orphan-purger CLI without taking a compile-time
/// dependency on the WinNode CLI assembly. Real WSL / registry / file-system
/// probing is integration-test territory; the assertions here lock down the
/// public surface (orphan-kind constants, prefix, exit-code policy) so the
/// recovery CLI flag stays stable for the support recipe in
/// docs/uninstall-msix.md.
///
/// Source-text assertions follow the same pattern as the historical
/// InstallerIssAssertionTests (now removed with Inno sunset) — Tray.Tests
/// is net10.0 and cannot transitively load the CLI's internal types, so we
/// pin the contract by reading the source.
/// </summary>
public sealed class OrphanPurgerContractTests
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

    private static string LoadOrphanPurgerSource() =>
        File.ReadAllText(Path.Combine(
            GetRepositoryRoot(), "src", "OpenClaw.WinNode.Cli", "OrphanPurger.cs"));

    [Fact]
    public void OrphanWslDistroPrefix_IsTheOpenclawPrefix()
    {
        // The local-gateway flow names every distro it installs with an
        // openclaw- prefix. Drift here means a real orphan goes undetected
        // (we silently miss what we promised to clean) — pin it. Retained
        // for backward compat after the 2026-05 case-insensitive fix added
        // OrphanWslDistroPatterns.
        Assert.Contains("OrphanWslDistroPrefix = \"openclaw-\"", LoadOrphanPurgerSource());
    }

    [Fact]
    public void WslDistroDetection_IsCaseInsensitive_AndCatchesLegacyOpenClawGateway()
    {
        // Regression: during MSIX-E2E manual test prep we found Mike's box
        // had an OpenClawGateway (PascalCase, no dash) distro installed by
        // the historical local-gateway flow. The original "openclaw-"
        // case-sensitive prefix would silently miss it, meaning a user who
        // ran --purge-wsl-orphans would be told "no orphans" while a 2.6 GB
        // .vhdx orphan was still on disk. Pin the case-insensitive substring
        // strategy so future refactors can't reintroduce the bug.
        var src = LoadOrphanPurgerSource();
        Assert.Contains("OrphanWslDistroPatterns", src);
        Assert.Contains("StringComparison.OrdinalIgnoreCase", src);
        Assert.Contains("OpenClawGateway", src); // documented in the remarks
    }

    [Fact]
    public void UriSchemeDetection_CoversBothCaseVariants()
    {
        // Same source as the WSL bug: the registry holds both
        // HKCU\Software\Classes\openclaw AND HKCU\Software\Classes\OpenClaw
        // simultaneously on some boxes; we have to enumerate both.
        var src = LoadOrphanPurgerSource();
        Assert.Contains(@"Software\Classes\openclaw", src);
        Assert.Contains(@"Software\Classes\OpenClaw", src);
        Assert.Contains("OrphanUriSchemeKeys", src);
    }

    [Theory]
    [InlineData("\"wsl-distro\"",          "WSL distro orphans")]
    [InlineData("\"appdata-folder\"",      "%APPDATA% orphans")]
    [InlineData("\"localappdata-folder\"", "%LOCALAPPDATA% orphans")]
    [InlineData("\"registry-uri-scheme\"", "openclaw:// URI scheme registration")]
    [InlineData("\"registry-run-key\"",    "HKCU Run autostart entry")]
    public void OrphanKinds_AreAllReported(string kindLiteral, string reason)
    {
        // Every kind we promise to detect in docs/uninstall-msix.md must show
        // up as a Kind on an OrphanItem somewhere in the source. If you remove
        // one, update the doc table in the same change.
        var src = LoadOrphanPurgerSource();
        Assert.True(src.Contains(kindLiteral),
            $"Missing orphan kind {kindLiteral} (covers: {reason})");
    }

    [Fact]
    public void ExitCodePolicy_IsDocumented()
    {
        // The exit-code mapping is the contract scripts and support docs key
        // off. The wording is set in source comments; if the meanings change,
        // re-check docs/uninstall-msix.md AND scripts/test-msix-install.ps1.
        var src = LoadOrphanPurgerSource();
        Assert.Contains("if (failed.Count > 0) return 2;", src);
        Assert.Contains("if (!confirmDestructive && orphans.Count > 0) return 1;", src);
        Assert.Contains("return 0;", src);
    }

    [Fact]
    public void DryRunIsTheDefault()
    {
        // Pin: --purge-wsl-orphans without --confirm-destructive must NOT
        // delete anything. Inverting this would surprise a support user who
        // ran the diagnostic to "see what's there".
        var src = LoadOrphanPurgerSource();
        Assert.Contains("if (confirmDestructive)", src);
    }
}

