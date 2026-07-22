using Xunit;

namespace OpenClaw.E2ETests.Setup;

public sealed class GatewayE2EPackageSpecTests
{
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
    }

    [Fact]
    public void Resolve_UsesLkgOnlyWhenExplicitlySelected()
    {
        const string lkg = "2026.7.1";
        Assert.Equal(lkg, GatewayE2EPackageSpec.Resolve("lkg", null, lkg));
        Assert.Equal(lkg, GatewayE2EPackageSpec.Resolve(" LKG ", "", lkg));
    }

    [Fact]
    public void Resolve_AcceptsBuiltPackageUrl()
    {
        const string packageSpec = "http://127.0.0.1:38677/openclaw-candidate.tgz";
        Assert.Equal(packageSpec, GatewayE2EPackageSpec.Resolve("candidate", packageSpec, "2026.7.1"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("other", null)]
    [InlineData("candidate", null)]
    [InlineData("candidate", "")]
    [InlineData("lkg", "https://example.test/openclaw-candidate.tgz")]
    public void Resolve_RejectsAmbiguousSourceOrSpec(string? source, string? packageSpec)
    {
        Assert.Throws<InvalidOperationException>(() =>
            GatewayE2EPackageSpec.Resolve(source, packageSpec, "2026.7.1"));
    }

    [Theory]
    [InlineData("2026.7.2")]
    [InlineData("file:///tmp/openclaw-candidate.tgz")]
    [InlineData("https://user:secret@example.test/openclaw-candidate.tgz")]
    [InlineData("https://example.test/openclaw-candidate.zip")]
    [InlineData("https://example.test/openclaw-candidate.tgz\n--dangerous")]
    public void Resolve_RejectsUnsafeOrNonPackageOverrides(string packageSpec)
    {
        Assert.Throws<InvalidOperationException>(() =>
            GatewayE2EPackageSpec.Resolve("candidate", packageSpec, "2026.7.1"));
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
