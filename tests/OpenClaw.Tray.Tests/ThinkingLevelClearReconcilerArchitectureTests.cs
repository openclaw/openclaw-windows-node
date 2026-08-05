namespace OpenClaw.Tray.Tests;

public sealed class ThinkingLevelClearReconcilerArchitectureTests
{
    [Fact]
    public void Reconciler_DefinesUiFreeProviderFacingSeam()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var reconciler = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Chat",
            "ThinkingLevelClearReconciler.cs"));

        Assert.Contains("public sealed class ThinkingLevelClearReconciler", reconciler);
        Assert.Contains("public SnapshotResolution ApplyCorrelatedSnapshot", reconciler);
        Assert.Contains("public async Task<RefreshRequest?> RetryAfterFailureAsync", reconciler);
        Assert.Contains("public IReadOnlyList<RefreshRequest> OnConnectionChanged", reconciler);
        Assert.DoesNotContain("Microsoft.UI.Xaml", reconciler);
        Assert.DoesNotContain("OpenClawGatewayClient", reconciler);
    }
}
