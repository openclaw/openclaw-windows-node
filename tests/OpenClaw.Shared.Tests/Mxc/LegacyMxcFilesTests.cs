using OpenClaw.Shared.Mxc;
using Xunit;

namespace OpenClaw.Shared.Tests.Mxc;

public sealed class LegacyMxcFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? Directory.GetCurrentDirectory(),
        ".mxc-test-runs", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CopyOverZipUpgrade_RemovesOnlyHistoricalPayloadAndIsIdempotent()
    {
        foreach (var relative in LegacyMxcFiles.RelativePaths) Write(relative);
        Write("wslcsdk.dll");
        Write(@"tools\mxc\x64\user-file.txt");
        Write("settings.json");
        Write("gateways.json");
        Assert.Empty(LegacyMxcFiles.Cleanup(_root));
        Assert.All(LegacyMxcFiles.RelativePaths, relative => Assert.False(File.Exists(Path.Combine(_root, relative))));
        Assert.True(File.Exists(Path.Combine(_root, "wslcsdk.dll")));
        Assert.True(File.Exists(Path.Combine(_root, @"tools\mxc\x64\user-file.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "settings.json")));
        Assert.True(File.Exists(Path.Combine(_root, "gateways.json")));
        Assert.False(Directory.Exists(Path.Combine(_root, @"tools\mxc\arm64")));
        Assert.Empty(LegacyMxcFiles.Cleanup(_root));
    }

    [Fact]
    public void PackagedVersions_AreOsOwnedAndNeverCleaned()
    {
        foreach (var relative in LegacyMxcFiles.RelativePaths) Write(relative);
        Assert.Empty(LegacyMxcFiles.Cleanup(_root, isPackaged: true));
        Assert.All(LegacyMxcFiles.RelativePaths, relative => Assert.True(File.Exists(Path.Combine(_root, relative))));
    }

    [Fact]
    public void InstallerChecksEveryExactDeletionIncludingEmptyDirectories()
    {
        var repo = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? Directory.GetCurrentDirectory();
        var installer = File.ReadAllText(Path.Combine(repo, "installer.iss"));
        var section = System.Text.RegularExpressions.Regex.Match(installer, @"(?ms)^\[InstallDelete\]\s*(.*?)^\[").Groups[1].Value;
        var entries = section.Split('\n').Where(line => line.StartsWith("Type:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(7, entries.Length);
        Assert.Equal(4, entries.Count(line => line.StartsWith("Type: files;", StringComparison.Ordinal)));
        foreach (var entry in entries)
        {
            var path = System.Text.RegularExpressions.Regex.Match(entry, "Name: \"([^\"]+)\"").Groups[1].Value;
            Assert.StartsWith(@"{app}\tools\mxc\", path + "\\");
            Assert.Contains($"Check: LegacyMxcPathIsOrdinary(ExpandConstant('{path}'))", entry);
        }
        var wrapper = installer[installer.IndexOf("function LegacyMxcPathIsOrdinary(", StringComparison.Ordinal)..
            installer.IndexOf("function InitializeUninstall:", StringComparison.Ordinal)];
        Assert.Contains("Result := MigrationPathIsOrdinary(Path);", wrapper);
        Assert.Contains("if not Result then", wrapper);
    }

    private void Write(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "synthetic");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
