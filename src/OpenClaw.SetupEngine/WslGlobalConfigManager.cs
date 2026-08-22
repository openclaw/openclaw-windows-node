using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenClaw.SetupEngine;

internal interface IWslGlobalConfigManager
{
    WslGlobalConfigStatus Inspect();
    WslGlobalConfigApplyResult ApplyMirroredNetworking();
    WslGlobalConfigRestoreResult RestoreIfUnchanged();
}

/// <summary>
/// Applies the global WSL mirrored-networking prerequisite without replacing
/// unrelated user configuration. The exact original bytes are retained so a
/// rollback can restore them when the user has not edited the file meanwhile.
/// </summary>
internal sealed class WslGlobalConfigManager : IWslGlobalConfigManager
{
    private const string Wsl2Section = "wsl2";
    private const string NetworkingModeKey = "networkingMode";
    private const string MirroredValue = "mirrored";

    private readonly string _configPath;
    private readonly string _backupPath;
    private readonly string _metadataPath;

    public WslGlobalConfigManager(string configPath, string backupDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);

        _configPath = Path.GetFullPath(configPath);
        var backupRoot = Path.GetFullPath(backupDirectory);
        _backupPath = Path.Combine(backupRoot, "wslconfig.original");
        _metadataPath = Path.Combine(backupRoot, "wslconfig.rollback.json");
    }

    public WslGlobalConfigStatus Inspect()
    {
        if (!File.Exists(_configPath))
            return new(false, false);

        var document = WslConfigDocument.Parse(ReadUtf8Document(_configPath).Text);
        return new(true, document.IsMirrored);
    }

    public WslGlobalConfigApplyResult ApplyMirroredNetworking()
    {
        var originalExists = File.Exists(_configPath);
        var original = originalExists
            ? File.ReadAllBytes(_configPath)
            : [];
        var decoded = DecodeUtf8(original);
        var document = WslConfigDocument.Parse(decoded.Text);

        if (document.IsMirrored)
            return new(false, null);

        var updatedText = document.WithMirroredNetworking(decoded.NewLine);
        var updated = EncodeUtf8(updatedText, decoded.HasBom);
        var metadata = new WslGlobalConfigRollbackMetadata(
            OriginalExisted: originalExists,
            OriginalSha256: ComputeSha256(original),
            AppliedSha256: ComputeSha256(updated));

        Directory.CreateDirectory(Path.GetDirectoryName(_backupPath)!);
        AtomicWriteBytes(_backupPath, original);
        AtomicFile.WriteAllText(
            _metadataPath,
            JsonSerializer.Serialize(metadata, SetupConfig.JsonWriteOptions));
        AtomicWriteBytes(_configPath, updated);

        return new(true, metadata);
    }

    public WslGlobalConfigRestoreResult RestoreIfUnchanged()
    {
        if (!File.Exists(_metadataPath))
            return WslGlobalConfigRestoreResult.NoBackup;

        WslGlobalConfigRollbackMetadata? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<WslGlobalConfigRollbackMetadata>(
                File.ReadAllText(_metadataPath),
                SetupConfig.JsonOptions);
        }
        catch (JsonException)
        {
            return WslGlobalConfigRestoreResult.InvalidBackup;
        }

        if (metadata is null || !IsSha256(metadata.AppliedSha256) || !IsSha256(metadata.OriginalSha256))
            return WslGlobalConfigRestoreResult.InvalidBackup;

        var current = File.Exists(_configPath) ? File.ReadAllBytes(_configPath) : [];
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(ComputeSha256(current)),
                Convert.FromHexString(metadata.AppliedSha256)))
        {
            return WslGlobalConfigRestoreResult.UserModified;
        }

        if (metadata.OriginalExisted)
        {
            if (!File.Exists(_backupPath))
                return WslGlobalConfigRestoreResult.InvalidBackup;

            var original = File.ReadAllBytes(_backupPath);
            if (!string.Equals(ComputeSha256(original), metadata.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                return WslGlobalConfigRestoreResult.InvalidBackup;

            AtomicWriteBytes(_configPath, original);
        }
        else if (File.Exists(_configPath))
        {
            File.Delete(_configPath);
        }

        TryDelete(_metadataPath);
        TryDelete(_backupPath);
        return WslGlobalConfigRestoreResult.Restored;
    }

    private static Utf8Document ReadUtf8Document(string path) => DecodeUtf8(File.ReadAllBytes(path));

    private static Utf8Document DecodeUtf8(byte[] bytes)
    {
        var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var text = Encoding.UTF8.GetString(bytes, hasBom ? Encoding.UTF8.Preamble.Length : 0, bytes.Length - (hasBom ? Encoding.UTF8.Preamble.Length : 0));
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return new(text, hasBom, newLine);
    }

    private static byte[] EncodeUtf8(string text, bool includeBom)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        if (!includeBom)
            return payload;

        var preamble = Encoding.UTF8.Preamble;
        var result = new byte[preamble.Length + payload.Length];
        preamble.CopyTo(result.AsSpan());
        payload.CopyTo(result, preamble.Length);
        return result;
    }

    private static void AtomicWriteBytes(string path, byte[] contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory ?? Directory.GetCurrentDirectory(),
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tempPath, contents);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static string ComputeSha256(byte[] contents) =>
        Convert.ToHexStringLower(SHA256.HashData(contents));

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort. A subsequent rollback can retry using the same metadata.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort. A subsequent rollback can retry using the same metadata.
        }
    }

    private sealed record Utf8Document(string Text, bool HasBom, string NewLine);

    private sealed class WslConfigDocument
    {
        private readonly string _text;
        private readonly List<Line> _lines;
        private readonly int? _wsl2HeaderIndex;
        private readonly int? _networkingModeIndex;
        private readonly int _wsl2EndIndex;

        private WslConfigDocument(
            string text,
            List<Line> lines,
            int? wsl2HeaderIndex,
            int? networkingModeIndex,
            int wsl2EndIndex,
            bool isMirrored)
        {
            _text = text;
            _lines = lines;
            _wsl2HeaderIndex = wsl2HeaderIndex;
            _networkingModeIndex = networkingModeIndex;
            _wsl2EndIndex = wsl2EndIndex;
            IsMirrored = isMirrored;
        }

        public bool IsMirrored { get; }

        public static WslConfigDocument Parse(string text)
        {
            var lines = SplitLines(text);
            int? wsl2Header = null;
            int? networkingMode = null;
            var wsl2End = lines.Count;
            var inWsl2 = false;
            var mirrored = false;

            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Content.Trim();
                if (trimmed.StartsWith('['))
                {
                    if (!trimmed.EndsWith(']') || trimmed.Length < 3)
                        throw new InvalidDataException($"Malformed WSL configuration section on line {i + 1}.");

                    var section = trimmed[1..^1].Trim();
                    if (inWsl2)
                        wsl2End = i;
                    inWsl2 = section.Equals(Wsl2Section, StringComparison.OrdinalIgnoreCase);
                    if (inWsl2)
                    {
                        if (wsl2Header is not null)
                            throw new InvalidDataException("The WSL configuration contains more than one [wsl2] section.");
                        wsl2Header = i;
                    }
                    continue;
                }

                if (!inWsl2 || trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                    continue;

                var separator = trimmed.IndexOf('=');
                if (separator < 1)
                    continue;

                var key = trimmed[..separator].Trim();
                if (!key.Equals(NetworkingModeKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (networkingMode is not null)
                    throw new InvalidDataException("The [wsl2] section contains more than one networkingMode setting.");

                networkingMode = i;
                var value = trimmed[(separator + 1)..].Trim();
                mirrored = value.Equals(MirroredValue, StringComparison.OrdinalIgnoreCase);
            }

            return new(text, lines, wsl2Header, networkingMode, wsl2End, mirrored);
        }

        public string WithMirroredNetworking(string newLine)
        {
            if (IsMirrored)
                return _text;

            if (_wsl2HeaderIndex is null)
            {
                var separator = _text.Length == 0 || _text.EndsWith('\n') ? string.Empty : newLine;
                return $"{_text}{separator}[wsl2]{newLine}{NetworkingModeKey}={MirroredValue}{newLine}";
            }

            if (_networkingModeIndex is { } settingIndex)
            {
                var line = _lines[settingIndex];
                _lines[settingIndex] = line with { Content = $"{NetworkingModeKey}={MirroredValue}" };
            }
            else
            {
                var settingTerminator = newLine;
                if (_wsl2EndIndex == _lines.Count &&
                    _lines.Count > 0 &&
                    _lines[^1].Terminator.Length == 0)
                {
                    // Preserve a missing final newline while still placing the new
                    // setting on its own line. Without this boundary, an existing
                    // final line such as "memory=8GB" becomes
                    // "memory=8GBnetworkingMode=mirrored".
                    _lines[^1] = _lines[^1] with { Terminator = newLine };
                    settingTerminator = string.Empty;
                }

                _lines.Insert(
                    _wsl2EndIndex,
                    new Line($"{NetworkingModeKey}={MirroredValue}", settingTerminator));
            }

            return string.Concat(_lines.Select(line => line.Content + line.Terminator));
        }

        private static List<Line> SplitLines(string text)
        {
            var lines = new List<Line>();
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n')
                    continue;

                var contentEnd = i > start && text[i - 1] == '\r' ? i - 1 : i;
                var terminator = contentEnd == i ? "\n" : "\r\n";
                lines.Add(new Line(text[start..contentEnd], terminator));
                start = i + 1;
            }

            if (start < text.Length)
                lines.Add(new Line(text[start..], string.Empty));
            return lines;
        }

        private sealed record Line(string Content, string Terminator);
    }
}

internal sealed record WslGlobalConfigStatus(bool Exists, bool IsMirrored);

internal sealed record WslGlobalConfigApplyResult(
    bool Changed,
    WslGlobalConfigRollbackMetadata? RollbackMetadata);

internal sealed record WslGlobalConfigRollbackMetadata(
    bool OriginalExisted,
    string OriginalSha256,
    string AppliedSha256);

internal enum WslGlobalConfigRestoreResult
{
    NoBackup,
    Restored,
    UserModified,
    InvalidBackup
}
