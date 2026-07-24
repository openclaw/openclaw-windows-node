using Xunit;

namespace OpenClaw.E2ETests.Setup;

public sealed class GatewayE2EPackageSpecTests
{
    private const string FallbackLkg = "2099.1.2";
    private const string OfficialVersion = "2099.1.3-beta.4";
    private const string ComposedSha256 =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string ComposedHostDistro = "OpenClawE2EPackageHost-Composed";

    [Fact]
    public void PackageHostScript_LeavesWslInstallTargetAbsentUntilInstall()
    {
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "Start-E2EGatewayPackageHost.ps1"));
        var install = script.IndexOf("& wsl.exe --install", StringComparison.Ordinal);
        var preRegistrationMarker = script.IndexOf(
            "Write-OwnershipMarker -MarkerPath $preRegistrationMarkerPath",
            StringComparison.Ordinal);
        var registeredMarker = script.IndexOf(
            "Write-OwnershipMarker -MarkerPath $markerPath",
            StringComparison.Ordinal);

        Assert.True(install >= 0);
        Assert.True(preRegistrationMarker >= 0 && preRegistrationMarker < install);
        Assert.True(registeredMarker > install);
        Assert.DoesNotContain(
            "New-Item -ItemType Directory -Path $installLocationFull",
            script,
            StringComparison.Ordinal);
        Assert.Contains("sha256sum -- $hostPackagePath", script, StringComparison.Ordinal);
        Assert.Contains("chmod 0444 $hostPackagePath", script, StringComparison.Ordinal);
        Assert.Contains("$packageSpec = \"http://${hostAddress}:$Port/$hostPackageName\"", script, StringComparison.Ordinal);
        Assert.Contains("package_sha256=$actualSha256", script, StringComparison.Ordinal);
    }

    [Fact]
    public void FormalMxcValidator_VerifiesComposedDigestBeforeE2E()
    {
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "scripts", "validate-mxc-e2e.ps1"));
        var digestVerification = script.IndexOf(
            "Assert-ReviewedComposedGatewayPackage",
            script.IndexOf("try {", StringComparison.Ordinal),
            StringComparison.Ordinal);
        var e2eInvocation = script.IndexOf(
            "Run Gateway MXC E2E proofs",
            StringComparison.Ordinal);

        Assert.True(digestVerification >= 0 && digestVerification < e2eInvocation);
        Assert.Contains("OPENCLAW_E2E_GATEWAY_PACKAGE_SHA256", script, StringComparison.Ordinal);
        Assert.Contains("OPENCLAW_E2E_GATEWAY_PACKAGE_HOST_DISTRO", script, StringComparison.Ordinal);
        Assert.Contains("\"OPENCLAW_E2E_GATEWAY_VERSION\"", script, StringComparison.Ordinal);
        Assert.Contains(
            "Set-ProcessEnv -Name \"OPENCLAW_E2E_GATEWAY_VERSION\" -Value $null",
            script,
            StringComparison.Ordinal);
        Assert.Contains("openclaw-composed-$normalizedSha256.tgz", script, StringComparison.Ordinal);
        Assert.Contains("sha256sum -- $hostPackagePath", script, StringComparison.Ordinal);
        Assert.Contains("stat -c \"%a\" -- $hostPackagePath", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-WebRequest", script, StringComparison.Ordinal);
    }

    [Fact]
    public void E2EConfig_CarriesReviewedDigestIntoSetupEngine()
    {
        var fixture = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "tests", "OpenClaw.E2ETests", "Setup", "E2ESetupFixture.cs"));

        Assert.Contains(
            "var expectedGatewayPackageSha256 = GatewayE2EPackageSpec.ResolveExpectedSha256();",
            fixture,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExpectedPackageSha256 = expectedGatewayPackageSha256",
            fixture,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PackageHostScript_RejectsDigestMismatchBeforeWslMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"openclaw-package-host-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var packagePath = Path.Combine(root, "composed.tgz");
            File.WriteAllText(packagePath, "not-the-reviewed-composed-package");
            var distroName = "OpenClawE2EPackageHost-DigestMismatch";
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "pwsh.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[]
            {
                "-NoProfile", "-NonInteractive", "-File",
                Path.Combine(RepositoryRoot(), "scripts", "Start-E2EGatewayPackageHost.ps1"),
                "-PackagePath", packagePath,
                "-ExpectedSha256", new string('0', 64),
                "-DistroName", distroName,
                "-InstallLocation", Path.Combine(root, distroName),
                "-OwnershipToken", "digest-mismatch-test",
                "-GitHubOutput", Path.Combine(root, "github-output.txt"),
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = System.Diagnostics.Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start PowerShell.");
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains(
                "Gateway composed package SHA-256 mismatch",
                standardOutput + standardError,
                StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(root, distroName)));
            Assert.False(File.Exists(Path.Combine(root, "github-output.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_UsesLkgOnlyWhenExplicitlySelected()
    {
        Assert.Equal(FallbackLkg, GatewayE2EPackageSpec.Resolve(
            "lkg", null, null, null, null, FallbackLkg));
        Assert.Equal(FallbackLkg, GatewayE2EPackageSpec.Resolve(
            " LKG ", "", "", "", "", FallbackLkg));
    }

    [Fact]
    public void Resolve_UsesExactOfficialPrerelease()
    {
        Assert.Equal(OfficialVersion, GatewayE2EPackageSpec.Resolve(
            "official", null, null, null, OfficialVersion, FallbackLkg));
    }

    [Fact]
    public void Resolve_AcceptsBuiltPackageUrl()
    {
        const string packageSpec =
            "http://127.0.0.1:38677/openclaw-composed-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.tgz";
        Assert.Equal(packageSpec, GatewayE2EPackageSpec.Resolve(
            "composed", packageSpec, ComposedSha256, ComposedHostDistro, null, FallbackLkg));
    }

    [Fact]
    public void ResolveExpectedSha256_CarriesOnlyValidatedComposedDigest()
    {
        Assert.Equal(
            ComposedSha256,
            GatewayE2EPackageSpec.ResolveExpectedSha256(" composed ", ComposedSha256.ToUpperInvariant()));
        Assert.Null(GatewayE2EPackageSpec.ResolveExpectedSha256("official", null));
        Assert.Throws<InvalidOperationException>(() =>
            GatewayE2EPackageSpec.ResolveExpectedSha256("composed", "not-a-digest"));
    }

    [Fact]
    public void Resolve_RejectsContentAddressedUrlForDifferentDigest()
    {
        var differentDigest = new string('f', 64);
        var packageSpec = $"https://example.test/openclaw-composed-{differentDigest}.tgz";

        Assert.Throws<InvalidOperationException>(() =>
            GatewayE2EPackageSpec.Resolve(
                "composed", packageSpec, ComposedSha256, ComposedHostDistro, null, FallbackLkg));
    }

    [Theory]
    [InlineData(null, null, null, null, null)]
    [InlineData("", null, null, null, null)]
    [InlineData("other", null, null, null, null)]
    [InlineData("composed", null, ComposedSha256, ComposedHostDistro, null)]
    [InlineData("composed", "", ComposedSha256, ComposedHostDistro, null)]
    [InlineData("composed", "https://example.test/openclaw-composed.tgz", null, ComposedHostDistro, null)]
    [InlineData("composed", "https://example.test/openclaw-composed.tgz", "abc", ComposedHostDistro, null)]
    [InlineData("composed", "https://example.test/openclaw-composed.tgz", ComposedSha256, ComposedHostDistro, OfficialVersion)]
    [InlineData("composed", "https://example.test/openclaw-composed.tgz", ComposedSha256, null, null)]
    [InlineData("composed", "https://example.test/openclaw-composed.tgz", ComposedSha256, "Ubuntu", null)]
    [InlineData("official", "https://example.test/openclaw-composed.tgz", null, null, OfficialVersion)]
    [InlineData("official", null, ComposedSha256, null, OfficialVersion)]
    [InlineData("official", null, null, ComposedHostDistro, OfficialVersion)]
    [InlineData("official", null, null, null, null)]
    [InlineData("official", null, null, null, "beta")]
    [InlineData("official", null, null, null, "2099.1.3")]
    [InlineData("lkg", "https://example.test/openclaw-composed.tgz", null, null, null)]
    [InlineData("lkg", null, ComposedSha256, null, null)]
    [InlineData("lkg", null, null, ComposedHostDistro, null)]
    [InlineData("lkg", null, null, null, OfficialVersion)]
    public void Resolve_RejectsAmbiguousSourceOrSpec(
        string? source,
        string? packageSpec,
        string? sha256,
        string? hostDistro,
        string? version)
    {
        Assert.Throws<InvalidOperationException>(() =>
            GatewayE2EPackageSpec.Resolve(
                source, packageSpec, sha256, hostDistro, version, FallbackLkg));
    }

    [Theory]
    [InlineData("2026.7.2")]
    [InlineData("file:///tmp/openclaw-composed.tgz")]
    [InlineData("https://user:secret@example.test/openclaw-composed.tgz")]
    [InlineData("https://example.test/openclaw-composed.zip")]
    [InlineData("https://example.test/openclaw-composed.tgz\n--dangerous")]
    public void Resolve_RejectsUnsafeOrNonPackageOverrides(string packageSpec)
    {
        Assert.Throws<InvalidOperationException>(() =>
            GatewayE2EPackageSpec.Resolve(
                "composed", packageSpec, ComposedSha256, ComposedHostDistro, null, FallbackLkg));
    }

    private static string RepositoryRoot()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") is { Length: > 0 } configured)
        {
            return configured;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "openclaw-windows-node.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
