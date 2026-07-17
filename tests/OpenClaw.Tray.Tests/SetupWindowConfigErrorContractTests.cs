namespace OpenClaw.Tray.Tests;

public sealed class SetupWindowConfigErrorContractTests
{
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
