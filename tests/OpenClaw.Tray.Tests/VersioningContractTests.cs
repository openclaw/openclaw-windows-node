using System.Text.RegularExpressions;

namespace OpenClaw.Tray.Tests;

public sealed class VersioningContractTests
{
    private static readonly Regex ProjectVersionElement = new(
        @"<Version>\s*\d+\.\d+\.\d+[^<]*</Version>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
        var repoRoot = TestRepositoryPaths.GetRepositoryRoot();
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
    public void GitVersion_MainBranchPreservesAlphaReleaseTags()
    {
        var repoRoot = TestRepositoryPaths.GetRepositoryRoot();
        var configPath = Path.Combine(repoRoot, "GitVersion.yml");
        var config = File.ReadAllText(configPath);
        var mainBranchMatch = Regex.Match(
            config,
            @"(?ms)^  main:\s*$\r?\n(?<body>.*?)(?=^  \S|\z)",
            RegexOptions.CultureInvariant);

        Assert.True(mainBranchMatch.Success, "GitVersion.yml must configure the main branch.");
        Assert.Matches(
            @"(?m)^\s+label:\s*'?alpha'?\s*$",
            mainBranchMatch.Groups["body"].Value);
    }

    [Fact]
    public void BuildScript_PreflightsGitVersionRepositoryHistory()
    {
        var repoRoot = TestRepositoryPaths.GetRepositoryRoot();
        var buildScript = File.ReadAllText(Path.Combine(repoRoot, "build.ps1"));

        Assert.Contains("Git metadata not found. GitVersion requires a git clone with full history.", buildScript);
        Assert.Contains("rev-parse --is-shallow-repository", buildScript);
        Assert.Contains("GitVersion requires full git history", buildScript);
        Assert.Contains("git fetch --unshallow --tags origin", buildScript);
    }

    [Fact]
    public void ActiveCodeAndTests_DoNotContainStaleReleaseVersion()
    {
        var repoRoot = TestRepositoryPaths.GetRepositoryRoot();
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
