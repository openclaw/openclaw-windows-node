using System.Diagnostics;
using OpenClaw.Shared.IO;
using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

public sealed class WindowsExistingPathResolverTests
{
    [Fact]
    public async Task Resolve_UsesHandleTargetForFileAndDirectoryAliases()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TempDirectory("physical-path-");
        string target = temp.Combine("physical");
        string alias = temp.Combine("logical");
        Directory.CreateDirectory(target);
        string file = Path.Combine(target, "setup.jsonl");
        await File.WriteAllTextAsync(file, "{}\n");
        var start = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c mklink /J \"{alias}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            Assert.Equal(WindowsExistingPathResolver.Resolve(target), WindowsExistingPathResolver.Resolve(alias));
            Assert.Equal(WindowsExistingPathResolver.Resolve(file),
                WindowsExistingPathResolver.Resolve(Path.Combine(alias, "setup.jsonl")));
        }
        finally
        {
            Directory.Delete(alias);
        }
    }
}
