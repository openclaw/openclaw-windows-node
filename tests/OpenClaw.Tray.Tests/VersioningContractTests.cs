using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

public sealed class VersioningContractTests
{
    private static readonly Regex ProjectVersionElement = new(
        @"<Version>\s*\d+\.\d+\.\d+[^<]*</Version>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string GetRepositoryRoot()
    {
        var envRepoRoot = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(envRepoRoot) && Directory.Exists(envRepoRoot))
            return envRepoRoot;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if ((Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                 File.Exists(Path.Combine(directory.FullName, ".git"))) &&
                File.Exists(Path.Combine(directory.FullName, "README.md")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }

    private static bool IsGeneratedOrIgnoredPath(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin", StringComparer.OrdinalIgnoreCase) ||
               segments.Contains("obj", StringComparer.OrdinalIgnoreCase) ||
               segments.Contains("node_modules", StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductProjects_DoNotHardcodeReleaseVersion()
    {
        var repoRoot = GetRepositoryRoot();
        var projectFiles = Directory.EnumerateFiles(
            Path.Combine(repoRoot, "src"),
            "*.csproj",
            SearchOption.AllDirectories);

        var offenders = projectFiles
            .Where(path => ProjectVersionElement.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(repoRoot, path))
            .OrderBy(path => path)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Product project files must not hardcode release versions. Offenders:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void ActiveCodeAndTests_DoNotContainStaleReleaseVersion()
    {
        var repoRoot = GetRepositoryRoot();
        var staleBareVersion = string.Concat("0.", "4.7");
        var staleDisplayVersion = "v" + staleBareVersion;
        var searchableExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs",
            ".csproj",
            ".props",
            ".targets",
            ".ps1",
            ".iss",
            ".yml"
        };

        var offenders = new[] { "src", "tests", "scripts", ".github" }
            .Select(root => Path.Combine(repoRoot, root))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(path => !IsGeneratedOrIgnoredPath(Path.GetRelativePath(repoRoot, path)))
            .Where(path => searchableExtensions.Contains(Path.GetExtension(path)))
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return text.Contains(staleBareVersion, StringComparison.Ordinal) ||
                       text.Contains(staleDisplayVersion, StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(repoRoot, path))
            .OrderBy(path => path)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Active code/tests must not contain the stale release literal. Offenders:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }
}
