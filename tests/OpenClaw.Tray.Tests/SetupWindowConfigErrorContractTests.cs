namespace OpenClaw.Tray.Tests;

public sealed class SetupWindowConfigErrorContractTests
{
    [Fact]
    public void MinimumSize_ConstrainsTheNativePresenterBeforeConfigurationAndTracksDpiUntilClose()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs"));
        AssertInOrder(source, "Closed += async", "ApplyMinimumWindowSize();", "SetupWindowCommandLine.TryParse(");
        Assert.Contains("AppWindow.Presenter is not OverlappedPresenter presenter", source);
        Assert.Contains("GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))", source);
        Assert.Contains("SetupWindowSizing.MinimumPixels(dpi)", source);
        Assert.Contains("presenter.PreferredMinimumWidth = minimum.Width", source);
        Assert.Contains("presenter.PreferredMinimumHeight = minimum.Height", source);
        Assert.Contains("SetupWindowSizing.InitialPixels(dpi)", source);
        Assert.Contains("RootFrame.Loaded += AttachMinimumSizeRoot", source);
        Assert.Contains("root.Changed += MinimumSizeRootChanged", source);
        Assert.Contains("previous.Changed -= MinimumSizeRootChanged", source);
        Assert.Contains("args.DidPositionChange || args.DidPresenterChange", source);
        var close = source[source.IndexOf("Closed += async", StringComparison.Ordinal)..
            source.IndexOf("// Extend into title bar", StringComparison.Ordinal)];
        Assert.Contains("RootFrame.Loaded -= AttachMinimumSizeRoot", close);
        Assert.Contains("AppWindow.Changed -= MinimumSizeWindowChanged", close);
        Assert.Contains("sizingRoot.Changed -= MinimumSizeRootChanged", close);
        Assert.Contains("_minimumSizeRoot = null", close);
        var apply = source[source.IndexOf("private void ApplyMinimumWindowSize()", StringComparison.Ordinal)..
            source.IndexOf("public void NavigateToWelcome", StringComparison.Ordinal)];
        Assert.DoesNotContain("AppWindow.Resize", apply);
        Assert.DoesNotContain("IsResizable = false", source);
        Assert.DoesNotContain("IsMaximizable = false", source);
    }

    [Fact]
    public void ConfigurationLoadFailuresAreSurfacedBeforeSetupStarts()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs"));

        Assert.Contains("SetupWindowCommandLine.TryParse(", source);
        Assert.Contains("SetupConfig.TryLoadFromFile(configPath", source);
        Assert.Contains("could not be loaded", source);
        Assert.Contains("new CompletePageArgs(", source);
        Assert.Contains("Success: false", source);
        Assert.Contains("ShowStartupPreference: false", source);
        Assert.DoesNotContain("throw new FileNotFoundException(", source);
        Assert.DoesNotContain("File.Exists(configPath)", source);
        Assert.DoesNotContain("GetArg(args", source);
        Assert.DoesNotContain("HasFlag(args", source);
        AssertInOrder(
            source,
            "Closed += async",
            "SetupWindowCommandLine.TryParse(",
            "if (configPath == null)",
            "SetupConfig.TryLoadFromFile(configPath",
            "SetupRunLock.TryAcquire(_dataDir");
    }

    private static void AssertInOrder(string source, params string[] fragments)
    {
        var previousIndex = -1;
        foreach (var fragment in fragments)
        {
            var index = source.IndexOf(fragment, StringComparison.Ordinal);
            Assert.True(index > previousIndex, $"Expected '{fragment}' after the previous fragment.");
            previousIndex = index;
        }
    }
}
