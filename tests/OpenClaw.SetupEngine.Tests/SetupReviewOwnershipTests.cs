namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupReviewOwnershipTests
{
    [Fact]
    public void LocalAiReview_PreservesGenerationEligibilityAndPinnedRecovery()
    {
        var source = Read("LocalAiSetupControl.xaml.cs");
        Assert.Contains("LocalAiSetupAvailabilityCoordinator", source);
        Assert.Contains("CanApplyLocalAiAvailability(checking.Generation, setupWindow)", source);
        Assert.Contains("LocalInferenceEligibilityFailureCode.HardwareFactsIncomplete", source);
        Assert.Contains("TryApplyProbeFailure", source);
        Assert.Contains("if (_localAiRecoveryModelPinned)", source);
        Assert.Contains("LocalAiToggle.IsEnabled = !_localAiRecoveryOnly", source);
        Assert.Contains("_localAiAvailability.CancelCurrent()", source);
    }

    [Fact]
    public void TailscaleReview_PreservesBoundedReadOnlyProbeAndGenerationFence()
    {
        var source = Read("TailscaleSetupControl.xaml.cs");
        Assert.Contains("psi.ArgumentList.Add(\"status\")", source);
        Assert.Contains("psi.ArgumentList.Add(\"--json\")", source);
        Assert.Contains("BoundedProcessOutput.ReadAsync", source);
        Assert.Contains("TailscaleSetupPolicy.GetTailnetDnsSuffix(dnsName)", source);
        Assert.Contains("IsCurrentTailscaleStatusProbe(generation, cancellation)", source);
        Assert.DoesNotContain("psi.ArgumentList.Add(\"up\")", source);
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(
        RepositoryRoot(), "src", "OpenClaw.SetupEngine.UI", "Controls", name));

    private static string RepositoryRoot()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") is { Length: > 0 } root) return root;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "OpenClaw.SetupEngine.UI")))
                return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
