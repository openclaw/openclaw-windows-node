namespace OpenClaw.SetupEngine.Tests;

public sealed class GatewayReleaseWorkflowContractTests
{
    [Fact]
    public void CandidateEvidence_RequiresExactPackageAndProvenanceBinding()
    {
        var root = RepositoryRoot();
        var script = File.ReadAllText(
            Path.Combine(root, "scripts", "Test-GatewayReleaseCandidate.ps1"));
        var workflow = File.ReadAllText(
            Path.Combine(root, ".github", "workflows", "gateway-release-candidate.yml"));

        Assert.Contains("$summary.packageBuildMatchesTag", script, StringComparison.Ordinal);
        Assert.Contains("$summary.npmProvenanceTagBound", script, StringComparison.Ordinal);
        Assert.Contains(
            "npm provenance source commit does not match the exact release tag commit.",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$AllowEmbeddedPolicyEvidence -and",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "-AllowEmbeddedPolicyEvidence",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Candidate evidence is discovery-only and cannot authorize product",
            workflow,
            StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") is { Length: > 0 } configured)
            return configured;

        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "openclaw-windows-node.slnx")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
