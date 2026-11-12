using System;

namespace OpenClaw.Tray.Tests;

public sealed class A2UICanvasSurfaceLayoutContractTests
{
    private static string RepoRoot => TestRepositoryPaths.GetRepositoryRoot();

    [Fact]
    public void OneSurfaceLayout_ReleasesTheTabBeforeAdoptingTheScroller()
    {
        var code = File.ReadAllText(Path.Combine(
            RepoRoot,
            "src",
            "OpenClaw.Tray.WinUI",
            "Windows",
            "A2UICanvasWindow.xaml.cs"));
        var start = code.IndexOf("if (surfaces.Count == 1)", StringComparison.Ordinal);
        Assert.True(start >= 0, "single-surface branch missing");
        var end = code.IndexOf(
            "SingleSurfaceHost.Visibility = Visibility.Collapsed;",
            start,
            StringComparison.Ordinal);
        Assert.True(end > start, "single-surface branch did not end before multi-surface mode");
        var block = code[start..end];

        Assert.Contains("CanvasSurfacePlacement.MoveToSingleHost(", block, StringComparison.Ordinal);
        Assert.DoesNotContain("SingleSurfaceHost.Content", block, StringComparison.Ordinal);
    }
}
