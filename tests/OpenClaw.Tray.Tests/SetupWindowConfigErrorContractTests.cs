namespace OpenClaw.Tray.Tests;

public sealed class SetupWindowConfigErrorContractTests
{
    [Fact]
    public void MinimumSize_ConstrainsTheNativePresenterBeforeConfigurationAndTracksDpiUntilClose()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "SetupWindow.xaml.cs"));
        var ready = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "SetupReadyWindow.cs"));
        var sizing = File.ReadAllText(Path.Combine(root, "src", "OpenClaw.SetupEngine.UI", "SetupWindowSizingController.cs"));
        AssertInOrder(source, "InitializeComponent();", "_windowSizing = new(this, RootFrame);", "SetupWindowCommandLine.TryParse(");
        Assert.Contains("_windowSizing = new(this, _frame);", ready);
        Assert.DoesNotContain("AppWindow.Resize", source);
        Assert.DoesNotContain("AppWindow.Resize", ready);
        Assert.Contains("_window.AppWindow.Presenter is not OverlappedPresenter presenter", sizing);
        Assert.Contains("GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window))", sizing);
        Assert.Contains("SetupWindowSizing.MinimumPixels(dpi)", sizing);
        Assert.Contains("presenter.PreferredMinimumWidth = minimum.Width", sizing);
        Assert.Contains("presenter.PreferredMinimumHeight = minimum.Height", sizing);
        Assert.Contains("SetupWindowSizing.InitialPixels(", sizing);
        Assert.Contains("_content.Loaded += ContentLoaded", sizing);
        Assert.Contains("root.Changed += RootChanged", sizing);
        Assert.Contains("previous.Changed -= RootChanged", sizing);
        Assert.Contains("args.DidPositionChange || args.DidPresenterChange", sizing);
        Assert.Contains("_window.Closed += WindowClosed", sizing);
        Assert.Contains("WindowClosed(object sender, WindowEventArgs args) => Dispose()", sizing);
        var close = sizing[sizing.IndexOf("public void Dispose()", StringComparison.Ordinal)..];
        Assert.Contains("_content.Loaded -= ContentLoaded", close);
        Assert.Contains("_window.AppWindow.Changed -= WindowChanged", close);
        Assert.Contains("root.Changed -= RootChanged", close);
        Assert.Contains("_root = null", close);
        var apply = sizing[sizing.IndexOf("internal void ApplyMinimumSize()", StringComparison.Ordinal)..
            sizing.IndexOf("private void WindowClosed", StringComparison.Ordinal)];
        Assert.DoesNotContain("AppWindow.Resize", apply);
        Assert.DoesNotContain("IsResizable = false", sizing);
        Assert.DoesNotContain("IsMaximizable = false", sizing);
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
