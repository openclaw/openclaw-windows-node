namespace OpenClaw.Tray.Tests.Usage;

/// <summary>
/// Pins the Usage page ownership split recorded in docs/ARCHITECTURE.md: the XAML page only hosts
/// the Reactor tree, usage reads go through UsagePageController rather than AppState pushes, Gateway
/// JSON is parsed in OpenClaw.Shared, and the views bind theme resources.
/// </summary>
public sealed class UsagePageOwnershipTests
{
    private static readonly string[] ForbiddenInViews =
    [
        "JsonDocument", "JsonElement", "GetProperty(", "RequestUsage", "AppState",
        "Color.FromArgb", "Colors.", "new SolidColorBrush",
    ];

    [Fact]
    public void UsagePageHostAndViewsDoNotParseGatewayJsonOrHardCodeColors()
    {
        var pages = Path.Combine(RepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "Pages");
        var files = Directory.GetFiles(Path.Combine(pages, "Usage"), "*.cs")
            .Append(Path.Combine(pages, "UsagePage.xaml.cs"))
            .ToArray();
        Assert.Contains(files, f => f.EndsWith("ReactorUsagePage.cs", StringComparison.Ordinal));

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (var token in ForbiddenInViews)
                Assert.False(source.Contains(token, StringComparison.Ordinal), $"{Path.GetFileName(file)} contains {token}");
        }

        var xaml = File.ReadAllText(Path.Combine(pages, "UsagePage.xaml"));
        Assert.Contains("x:Name=\"UsageHost\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Uid=", xaml, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var env = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException("Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }
}
