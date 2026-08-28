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

    [Theory]
    [InlineData(false, "\n", false)]
    [InlineData(false, "\n", true)]
    [InlineData(false, "\r\n", false)]
    [InlineData(false, "\r\n", true)]
    [InlineData(true, "\n", false)]
    [InlineData(true, "\n", true)]
    [InlineData(true, "\r\n", false)]
    [InlineData(true, "\r\n", true)]
    public void ApplyMirroredNetworking_PreservesUtf16EncodingAndRollback(
        bool bigEndian,
        string newLine,
        bool includeFinalNewLine)
    {
        Directory.CreateDirectory(_root);
        var configPath = Path.Combine(_root, "wslconfig");
        var encoding = new UnicodeEncoding(bigEndian, byteOrderMark: true, throwOnInvalidBytes: true);
        var originalText = $"[wsl2]{newLine}memory=8GB" +
            (includeFinalNewLine ? newLine : string.Empty);
        var original = Encode(originalText, encoding);
        File.WriteAllBytes(configPath, original);
        var manager = new WslGlobalConfigManager(configPath, Path.Combine(_root, "backup"));

        Assert.True(manager.ApplyMirroredNetworking().Changed);
        var updated = File.ReadAllBytes(configPath);
        Assert.True(updated.AsSpan().StartsWith(encoding.Preamble));
        var updatedText = encoding.GetString(updated[encoding.Preamble.Length..]);
        Assert.Contains($"memory=8GB{newLine}networkingMode=mirrored", updatedText);
        Assert.Equal(includeFinalNewLine, updatedText.EndsWith(newLine, StringComparison.Ordinal));
        Assert.Equal(WslGlobalConfigRestoreResult.Restored, manager.RestoreIfUnchanged());
        Assert.Equal(original, File.ReadAllBytes(configPath));
    }

    [Fact]
    public void ApplyMirroredNetworking_InvalidEncodingDoesNotModifyFiles()
    {
        Directory.CreateDirectory(_root);
        var configPath = Path.Combine(_root, "wslconfig");
        var backupPath = Path.Combine(_root, "backup");
        byte[] original = [.. Encoding.UTF8.GetBytes("[wsl2]\nmemory=8GB\n"), 0xFF];
        File.WriteAllBytes(configPath, original);

        var manager = new WslGlobalConfigManager(configPath, backupPath);

        Assert.Throws<InvalidDataException>(() => manager.ApplyMirroredNetworking());
        Assert.Equal(original, File.ReadAllBytes(configPath));
        Assert.False(Directory.Exists(backupPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyMirroredNetworking_UnsupportedUtf32DoesNotModifyFiles(bool bigEndian)
    {
        Directory.CreateDirectory(_root);
        var configPath = Path.Combine(_root, "wslconfig");
        var backupPath = Path.Combine(_root, "backup");
        var encoding = new UTF32Encoding(bigEndian, byteOrderMark: true, throwOnInvalidCharacters: true);
        var original = Encode("# existing comment\n", encoding);
        File.WriteAllBytes(configPath, original);

        var manager = new WslGlobalConfigManager(configPath, backupPath);

        Assert.Throws<InvalidDataException>(() => manager.ApplyMirroredNetworking());
        Assert.Equal(original, File.ReadAllBytes(configPath));
        Assert.False(Directory.Exists(backupPath));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ApplyMirroredNetworking_MalformedUtf16DoesNotModifyFiles(
        bool bigEndian,
        bool unpairedSurrogate)
    {
        Directory.CreateDirectory(_root);
        var configPath = Path.Combine(_root, "wslconfig");
        var backupPath = Path.Combine(_root, "backup");
        var encoding = new UnicodeEncoding(bigEndian, byteOrderMark: true, throwOnInvalidBytes: true);
        byte[] malformedPayload = unpairedSurrogate
            ? bigEndian ? [0xD8, 0x00] : [0x00, 0xD8]
            : [0x5B];
        byte[] original = [.. encoding.Preamble, .. malformedPayload];
        File.WriteAllBytes(configPath, original);

        var manager = new WslGlobalConfigManager(configPath, backupPath);

        Assert.Throws<InvalidDataException>(() => manager.ApplyMirroredNetworking());
        Assert.Equal(original, File.ReadAllBytes(configPath));
        Assert.False(Directory.Exists(backupPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyMirroredNetworking_BomlessUtf16DoesNotModifyFiles(bool bigEndian)
    {
        Directory.CreateDirectory(_root);
        var configPath = Path.Combine(_root, "wslconfig");
        var backupPath = Path.Combine(_root, "backup");
        var encoding = new UnicodeEncoding(bigEndian, byteOrderMark: false, throwOnInvalidBytes: true);
        var original = encoding.GetBytes("[wsl2]\nmemory=8GB\n");
        File.WriteAllBytes(configPath, original);

        var manager = new WslGlobalConfigManager(configPath, backupPath);

        Assert.Throws<InvalidDataException>(() => manager.ApplyMirroredNetworking());
        Assert.Equal(original, File.ReadAllBytes(configPath));
        Assert.False(Directory.Exists(backupPath));
    }

    [Fact]
    public void ApplyMirroredNetworking_PreservesValidControlCharactersInUnknownContent()
    {
        Directory.CreateDirectory(_root);
        var configPath = Path.Combine(_root, "wslconfig");
        var backupPath = Path.Combine(_root, "backup");
        const string unknownContent = "[experimental]\n# legacy separator: \u001A\n";
        var original = Encoding.UTF8.GetBytes($"{unknownContent}[wsl2]\nmemory=8GB\n");
        File.WriteAllBytes(configPath, original);

        var manager = new WslGlobalConfigManager(configPath, backupPath);

        Assert.True(manager.ApplyMirroredNetworking().Changed);
        Assert.StartsWith(unknownContent, File.ReadAllText(configPath));
        Assert.Equal(WslGlobalConfigRestoreResult.Restored, manager.RestoreIfUnchanged());
        Assert.Equal(original, File.ReadAllBytes(configPath));
    }

    [Fact]
    public void ApplyMirroredNetworking_AppendsSectionWithoutReplacingUnknownContent()
    {
        Directory.CreateDirectory(_root);
        var configPath = Path.Combine(_root, "wslconfig");
        var backupPath = Path.Combine(_root, "backup");
        const string originalText = "[experimental]\nautoMemoryReclaim=gradual";
        var original = Encoding.UTF8.GetBytes(originalText);
        File.WriteAllBytes(configPath, original);

        var manager = new WslGlobalConfigManager(configPath, backupPath);

        Assert.True(manager.ApplyMirroredNetworking().Changed);
        Assert.Equal(
            $"{originalText}\n[wsl2]\nnetworkingMode=mirrored\n",
            File.ReadAllText(configPath));
        Assert.Equal(WslGlobalConfigRestoreResult.Restored, manager.RestoreIfUnchanged());
        Assert.Equal(original, File.ReadAllBytes(configPath));
    }

    [Fact]
    public void ApplyMirroredNetworking_CreatesAndRollsBackMissingFile()
    {
        Directory.CreateDirectory(_root);
        var configPath = Path.Combine(_root, "wslconfig");
        var manager = new WslGlobalConfigManager(configPath, Path.Combine(_root, "backup"));

        Assert.True(manager.ApplyMirroredNetworking().Changed);
        Assert.Equal("[wsl2]\nnetworkingMode=mirrored\n", File.ReadAllText(configPath));
        Assert.Equal(WslGlobalConfigRestoreResult.Restored, manager.RestoreIfUnchanged());
        Assert.False(File.Exists(configPath));
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

    private static byte[] Encode(string text, Encoding encoding) =>
        [.. encoding.Preamble, .. encoding.GetBytes(text)];

    private static string Decode(byte[] bytes)
    {
        var offset = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)
            ? Encoding.UTF8.Preamble.Length
            : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }
}
