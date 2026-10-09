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
