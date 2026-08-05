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

    [Fact]
    public void Provider_DelegatesThinkingLevelClearReconciliationState()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var provider = File.ReadAllText(Path.Combine(
            root,
            "src",
            "OpenClaw.Tray.WinUI",
            "Chat",
            "OpenClawChatDataProvider.cs"));

        Assert.Contains(
            "private readonly ThinkingLevelClearReconciler _thinkingLevelClearReconciler;",
            provider);
        Assert.Contains(
            "_thinkingLevelClearReconciler = new ThinkingLevelClearReconciler(",
            provider);
        Assert.Contains("_thinkingLevelClearReconciler.BeginClear(", provider);
        Assert.Contains("_thinkingLevelClearReconciler.BeginConcreteSelection(", provider);
        Assert.Contains("_thinkingLevelClearReconciler.ApplyCorrelatedSnapshot(", provider);
        Assert.Contains(".OnConnectionChanged(status == ConnectionStatus.Connected)", provider);
        Assert.Contains("_thinkingLevelClearReconciler.Dispose();", provider);

        Assert.DoesNotContain("PendingThinkingLevelClear", provider);
        Assert.DoesNotContain("ThinkingLevelReconciliation", provider);
        Assert.DoesNotContain("_thinkingLevelClearVersions", provider);
        Assert.DoesNotContain("MaxThinkingLevelRefreshAttempts", provider);
        Assert.DoesNotContain("ScheduleThinkingLevelRetryAsync", provider);
    }
}
