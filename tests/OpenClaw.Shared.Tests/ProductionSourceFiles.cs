using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenClaw.Shared.Tests;

internal static class ProductionSourceFiles
{
    private static readonly Lazy<IReadOnlyList<SourceFileSnapshot>> SourceFiles = new(LoadSourceFiles);

    public static IReadOnlyList<SourceFileSnapshot> All => SourceFiles.Value;

    private static IReadOnlyList<SourceFileSnapshot> LoadSourceFiles()
    {
        var repoRoot = FindRepoRoot();
        var srcDir = Path.Combine(repoRoot, "src");

        return Directory
            .GetFiles(srcDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutputPath(path))
            .Select(path => new SourceFileSnapshot(path, File.ReadAllText(path)))
            .ToArray();
    }

    private static bool IsBuildOutputPath(string path) =>
        path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
        path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    internal static string FindRepoRoot()
    {
        var envRoot = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot) &&
            File.Exists(Path.Combine(envRoot, "openclaw-windows-node.slnx")))
        {
            return envRoot;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "openclaw-windows-node.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate openclaw-windows-node.slnx from test base directory.");
    }
}

internal sealed record SourceFileSnapshot(string Path, string Text);
