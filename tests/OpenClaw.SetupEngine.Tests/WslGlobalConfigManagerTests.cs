using System.Text;

namespace OpenClaw.SetupEngine.Tests;

public sealed class WslGlobalConfigManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"wsl-global-config-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("\n", false, false)]
    [InlineData("\n", false, true)]
    [InlineData("\n", true, false)]
    [InlineData("\n", true, true)]
    [InlineData("\r\n", false, false)]
    [InlineData("\r\n", false, true)]
    [InlineData("\r\n", true, false)]
    [InlineData("\r\n", true, true)]
    public void ApplyMirroredNetworking_PreservesLineBoundariesAndEncoding(
        string newLine,
        bool includeBom,
        bool includeFinalNewLine)
    {
        Directory.CreateDirectory(_root);
        var configPath = Path.Combine(_root, "wslconfig");
        var backupPath = Path.Combine(_root, "backup");
        var originalText = $"[experimental]{newLine}autoMemoryReclaim=gradual{newLine}" +
            $"[wsl2]{newLine}memory=8GB" +
            (includeFinalNewLine ? newLine : string.Empty);
        var originalBytes = Encode(originalText, includeBom);
        File.WriteAllBytes(configPath, originalBytes);

        var manager = new WslGlobalConfigManager(configPath, backupPath);
        var result = manager.ApplyMirroredNetworking();

        Assert.True(result.Changed);
        var updatedBytes = File.ReadAllBytes(configPath);
        Assert.Equal(includeBom, updatedBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        var updatedText = Decode(updatedBytes);
        Assert.Contains($"memory=8GB{newLine}networkingMode=mirrored", updatedText);
        Assert.DoesNotContain("memory=8GBnetworkingMode", updatedText);
        Assert.Contains($"[experimental]{newLine}autoMemoryReclaim=gradual", updatedText);
        Assert.Equal(includeFinalNewLine, updatedText.EndsWith(newLine, StringComparison.Ordinal));

        Assert.Equal(WslGlobalConfigRestoreResult.Restored, manager.RestoreIfUnchanged());
        Assert.Equal(originalBytes, File.ReadAllBytes(configPath));
    }

    [Fact]
    public void ApplyMirroredNetworking_ReplacesSettingAndIsIdempotent()
    {
        Directory.CreateDirectory(_root);
        var configPath = Path.Combine(_root, "wslconfig");
        File.WriteAllText(
            configPath,
            "[wsl2]\r\nnetworkingMode=nat\r\nprocessors=4\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var manager = new WslGlobalConfigManager(configPath, Path.Combine(_root, "backup"));

        Assert.True(manager.ApplyMirroredNetworking().Changed);
        Assert.Equal(
            "[wsl2]\r\nnetworkingMode=mirrored\r\nprocessors=4\r\n",
            File.ReadAllText(configPath));
        Assert.False(manager.ApplyMirroredNetworking().Changed);
    }

    [Fact]
    public void RestoreIfUnchanged_PreservesAConcurrentUserEdit()
    {
        Directory.CreateDirectory(_root);
        var configPath = Path.Combine(_root, "wslconfig");
        File.WriteAllText(configPath, "[wsl2]\nmemory=8GB", new UTF8Encoding(false));
        var manager = new WslGlobalConfigManager(configPath, Path.Combine(_root, "backup"));
        manager.ApplyMirroredNetworking();
        File.AppendAllText(configPath, "\nprocessors=4");

        Assert.Equal(WslGlobalConfigRestoreResult.UserModified, manager.RestoreIfUnchanged());
        Assert.Contains("processors=4", File.ReadAllText(configPath));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Test cleanup is best effort and must not hide the assertion result.
        }
    }

    private static byte[] Encode(string text, bool includeBom)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        return includeBom ? [.. Encoding.UTF8.Preamble, .. payload] : payload;
    }

    private static string Decode(byte[] bytes)
    {
        var offset = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)
            ? Encoding.UTF8.Preamble.Length
            : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }
}
