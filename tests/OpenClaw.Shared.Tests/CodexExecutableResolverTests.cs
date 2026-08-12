using System.Collections;
using System.Diagnostics;
using System.Reflection;
using OpenClaw.Shared.Codex;

namespace OpenClaw.Shared.Tests;

public sealed class CodexExecutableResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"openclaw-codex-resolver-{Guid.NewGuid():N}");

    public CodexExecutableResolverTests()
    {
        Directory.CreateDirectory(_root);
    }

    public static TheoryData<bool> PackagedAliasLocalAppDataShapes() => new()
    {
        false,
        true,
    };

    [Theory]
    [MemberData(nameof(PackagedAliasLocalAppDataShapes))]
    public void Resolve_PrefersThePackagedAppExecutionAlias(bool trailingSeparator)
    {
        var localAppData = Directory.CreateDirectory(Path.Combine(_root, "local-app-data")).FullName;
        var aliasDirectory = Directory.CreateDirectory(
            Path.Combine(localAppData, "Microsoft", "WindowsApps")).FullName;
        var alias = CreateFile(aliasDirectory, "codex.exe");
        var pathDirectory = Directory.CreateDirectory(Path.Combine(_root, "path-bin")).FullName;
        _ = CreateFile(pathDirectory, "codex.exe");
        var platform = new TestPlatform(
            trailingSeparator ? localAppData + Path.DirectorySeparatorChar : localAppData,
            pathDirectory,
            attributes: path => string.Equals(path, alias, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Archive | FileAttributes.ReparsePoint
                : File.GetAttributes(path));

        var plan = new CodexExecutableResolver(platform).Resolve();

        Assert.NotNull(plan);
        Assert.Equal(Path.GetFullPath(alias), plan.ExecutablePath);
        AssertLaunchBoundary(plan);
    }

    [Fact]
    public void Resolve_ReturnsCanonicalExistingCodexExeFromCurrentProcessPath()
    {
        var firstDirectory = Directory.CreateDirectory(Path.Combine(_root, "missing-bin")).FullName;
        var secondDirectory = Directory.CreateDirectory(
            Path.Combine(_root, "bin", "nested", "..")).FullName;
        var executable = CreateFile(secondDirectory, "codex.exe");
        var platform = new TestPlatform(
            Path.Combine(_root, "no-local-app-data"),
            string.Join(Path.PathSeparator, firstDirectory, secondDirectory));

        var plan = new CodexExecutableResolver(platform).Resolve();

        Assert.NotNull(plan);
        Assert.Equal(Path.GetFullPath(executable), plan.ExecutablePath);
        AssertLaunchBoundary(plan);
    }

    public static TheoryData<string> UntrustedPathCandidates() => new()
    {
        "missing",
        "directory-named-codex.exe",
        "codex",
        "codex.cmd",
        "other.exe",
    };

    [Theory]
    [MemberData(nameof(UntrustedPathCandidates))]
    public void Resolve_RejectsMissingDirectoriesAndNonExecutableCandidates(string candidateKind)
    {
        var pathDirectory = Path.Combine(_root, candidateKind);
        string pathValue;
        if (candidateKind == "missing")
        {
            pathValue = pathDirectory;
        }
        else
        {
            Directory.CreateDirectory(pathDirectory);
            if (candidateKind == "directory-named-codex.exe")
                Directory.CreateDirectory(Path.Combine(pathDirectory, "codex.exe"));
            else
                _ = CreateFile(pathDirectory, candidateKind);
            pathValue = pathDirectory;
        }

        var plan = new CodexExecutableResolver(new TestPlatform(
            Path.Combine(_root, "no-local-app-data"),
            pathValue)).Resolve();

        Assert.Null(plan);
    }

    [Theory]
    [InlineData("relative-bin")]
    [InlineData("..\\outside-bin")]
    public void Resolve_RejectsRelativeAndTraversalPathEntries(string pathEntry)
    {
        var platform = new TestPlatform(
            Path.Combine(_root, "no-local-app-data"),
            pathEntry);

        Assert.Null(new CodexExecutableResolver(platform).Resolve());
    }

    [Fact]
    public void Resolve_RejectsFullyQualifiedPathEntryContainingTraversal()
    {
        var pathDirectory = Directory.CreateDirectory(
            Path.Combine(_root, "path-parent", "bin")).FullName;
        _ = CreateFile(pathDirectory, "codex.exe");
        var traversalPath = Path.Combine(
            _root,
            "path-parent",
            "unused",
            "..",
            "bin");
        var platform = new TestPlatform(
            Path.Combine(_root, "no-local-app-data"),
            traversalPath);

        Assert.True(Path.IsPathFullyQualified(traversalPath));
        Assert.Null(new CodexExecutableResolver(platform).Resolve());
    }

    [Fact]
    public void Resolve_RejectsReparsePointCandidateOutsideThePackagedAliasDirectory()
    {
        var pathDirectory = Directory.CreateDirectory(Path.Combine(_root, "path-bin")).FullName;
        var executable = CreateFile(pathDirectory, "codex.exe");
        var platform = new TestPlatform(
            Path.Combine(_root, "no-local-app-data"),
            pathDirectory,
            attributes: path => string.Equals(path, executable, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Archive | FileAttributes.ReparsePoint
                : File.GetAttributes(path));

        Assert.Null(new CodexExecutableResolver(platform).Resolve());
    }

    [Fact]
    public void Resolve_HasNoCallerSuppliedExecutableCandidate()
    {
        var resolveMethods = typeof(CodexExecutableResolver)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name == nameof(CodexExecutableResolver.Resolve))
            .ToArray();

        Assert.Single(resolveMethods);
        Assert.Empty(resolveMethods[0].GetParameters());
        Assert.False(resolveMethods[0].IsPublic);
        Assert.DoesNotContain(
            typeof(CodexExecutableResolver).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            constructor => constructor.IsPublic);
    }

    [Fact]
    public void CreateProcessStartInfo_UsesArgumentListAndRedirectedStdio()
    {
        var pathDirectory = Directory.CreateDirectory(Path.Combine(_root, "path-bin")).FullName;
        var executable = CreateFile(pathDirectory, "codex.exe");
        var plan = new CodexExecutableResolver(new TestPlatform(
            Path.Combine(_root, "no-local-app-data"),
            pathDirectory)).Resolve();

        Assert.NotNull(plan);
        var startInfo = plan.CreateProcessStartInfo();

        Assert.Equal(executable, startInfo.FileName);
        Assert.Equal(new[] { "app-server", "--listen", "stdio://" }, startInfo.ArgumentList);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(string.Empty, startInfo.Arguments);
    }

    [Fact]
    public void LaunchPlan_RejectsAnExecutableThatIsNoLongerTrustedAtStartTime()
    {
        var pathDirectory = Directory.CreateDirectory(Path.Combine(_root, "path-bin")).FullName;
        var executable = CreateFile(pathDirectory, "codex.exe");
        var platform = new TestPlatform(Path.Combine(_root, "no-local-app-data"), pathDirectory);
        var plan = new CodexExecutableResolver(platform).Resolve();

        Assert.NotNull(plan);
        File.Delete(executable);

        Assert.False(plan.IsTrustedForLaunch());
    }

    private static void AssertLaunchBoundary(CodexLaunchPlan plan)
    {
        Assert.Equal(new[] { "app-server", "--listen", "stdio://" }, plan.Arguments);
        Assert.Empty(plan.EnvironmentOverrides);
        Assert.False(plan.UseShellExecute);
        Assert.True(plan.RedirectStandardInput);
        Assert.True(plan.RedirectStandardOutput);
        Assert.True(plan.RedirectStandardError);

        Assert.Throws<NotSupportedException>(() =>
            ((IList)plan.Arguments)[0] = "exec");
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary)plan.EnvironmentOverrides).Add("CODEX_HOME", "untrusted"));
    }

    private static string CreateFile(string directory, string fileName)
    {
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        File.WriteAllBytes(path, []);
        return path;
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private sealed class TestPlatform : ICodexExecutablePlatform
    {
        private readonly Func<string, FileAttributes> _attributes;

        public TestPlatform(
            string? localApplicationData,
            string? pathEnvironment,
            Func<string, FileAttributes>? attributes = null)
        {
            LocalApplicationData = localApplicationData;
            PathEnvironment = pathEnvironment;
            _attributes = attributes ?? File.GetAttributes;
        }

        public string? LocalApplicationData { get; }

        public string? PathEnvironment { get; }

        public string GetFullPath(string path) => Path.GetFullPath(path);

        public bool IsPathFullyQualified(string path) => Path.IsPathFullyQualified(path);

        public bool FileExists(string path) => File.Exists(path);

        public FileAttributes GetAttributes(string path) => _attributes(path);
    }
}
