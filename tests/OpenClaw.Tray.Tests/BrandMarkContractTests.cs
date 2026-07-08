namespace OpenClaw.Tray.Tests;

public sealed class BrandMarkContractTests
{
    private const string LobsterEmoji = "\ud83e\udd9e";
    private const string RobotEmoji = "\ud83e\udd16";
    private const string RedBotAssetName = "Square44x44Logo.targetsize-256_altform-unplated.png";
    private static string RepoRoot => TestRepositoryPaths.GetRepositoryRoot();

    private static string ReadTrayFile(params string[] pathParts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot, "src", "OpenClaw.Tray.WinUI" }.Concat(pathParts).ToArray()));

    [Fact]
    public void BrandMark_UsesSharedRedBotImageAsset()
    {
        var xaml = ReadTrayFile("Controls", "BrandMark.xaml");
        var code = ReadTrayFile("Controls", "BrandMark.xaml.cs");
        var assets = ReadTrayFile("Helpers", "BrandAssets.cs");
        var redBotAssetPath = Path.Combine(
            RepoRoot,
            "src",
            "OpenClaw.Tray.WinUI",
            "Assets",
            RedBotAssetName);

        Assert.Contains("<Image", xaml);
        Assert.Contains("AutomationProperties.AccessibilityView=\"Raw\"", xaml);
        Assert.Contains("IsHitTestVisible=\"False\"", xaml);
        Assert.Contains("IsTabStop=\"False\"", xaml);
        Assert.Contains("BrandAssets.CreateRedBotMarkSource()", code);
        Assert.Contains(RedBotAssetName, assets);
        Assert.True(File.Exists(redBotAssetPath), $"BrandMark asset is missing: {redBotAssetPath}");
        Assert.DoesNotContain("FontIcon", xaml);
        Assert.DoesNotContain($"Text=\"{LobsterEmoji}\"", xaml);
    }

    [Fact]
    public void KnownBrandSurfaces_UseBrandMark()
    {
        Assert.Contains("controls:BrandMark", ReadTrayFile("Windows", "HubWindow.xaml"));
        Assert.Contains("controls:BrandMark", ReadTrayFile("Pages", "AgentEventsPage.xaml"));

        var expectedCodeFiles = new[]
        {
            Path.Combine("Dialogs", "WelcomeDialog.cs"),
            Path.Combine("Dialogs", "PairingApprovalDialog.cs"),
            Path.Combine("Dialogs", "RecordingConsentDialog.cs"),
        };

        foreach (var file in expectedCodeFiles)
        {
            Assert.Contains("new BrandMark", ReadTrayFile(file.Split(Path.DirectorySeparatorChar)));
        }
    }

    [Fact]
    public void TrayWinUiSources_DoNotRenderEmojiBrandMarks()
    {
        var sourceRoot = Path.Combine(RepoRoot, "src", "OpenClaw.Tray.WinUI");
        var offenders = Directory
            .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsBuildArtifact(sourceRoot, path))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (path, line, lineNumber: index + 1)))
            .Where(item => item.line.Contains(LobsterEmoji, StringComparison.Ordinal)
                || item.line.Contains(RobotEmoji, StringComparison.Ordinal))
            .Select(item => $"{Path.GetRelativePath(RepoRoot, item.path)}:{item.lineNumber}")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Use the shared BrandMark control instead of emoji brand marks:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));

        static bool IsBuildArtifact(string sourceRoot, string path)
        {
            var relative = Path.GetRelativePath(sourceRoot, path);
            return relative.StartsWith($"bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith($"obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
        }
    }
}
