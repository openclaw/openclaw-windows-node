using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Audio;

/// <summary>
/// Catalog, downloads and on-disk lifecycle for Kokoro (kokoro.cpp) voice packs.
///
/// A voice pack is one ONNX model plus the voices file whose style vectors it
/// was trained with; every voice in a pack needs both files. The G2P
/// dictionaries are not part of a pack: they ship inside the Larroy.Kokoro
/// NuGet package (<c>&lt;app&gt;/kokoro-dict</c>) because they are tied to
/// the native G2P code version.
///
/// Storage layout under the tray's data directory:
///   models/kokoro/&lt;pack-id&gt;/
///       &lt;model&gt;.onnx
///       voices-*.bin
///   models/kokoro/&lt;pack-id&gt;.installing   (present only while downloading)
///
/// **Integrity:** both files are SHA-256-verified against the pinned hashes
/// in <see cref="AvailablePacks"/> before they are moved into place; a
/// mismatch is a hard failure and the partial files are deleted. See
/// <c>docs/AUDIO_MODEL_ASSETS.md</c>.
/// </summary>
public sealed class KokoroModelManager
{
    /// <summary>Voice selected on fresh installs.</summary>
    public const string DefaultVoiceId = "af_maple";

    private const string InstallMarkerSuffix = ".installing";
    private const string LarroyReleaseBase = "https://github.com/larroy/kokoro.cpp/releases/download/voices_model_files/";

    // Static so two managers over the same data directory coalesce against the
    // same in-flight download. Ordinal: pack ids are fixed catalog keys.
    private static readonly ConcurrentDictionary<string, Lazy<Task>> InFlightDownloads = new(StringComparer.Ordinal);

    private static readonly string[] ChineseVoiceIds =
    [
        "zf_001", "zf_002", "zf_003", "zf_004", "zf_005", "zf_006", "zf_007", "zf_008", "zf_017", "zf_018",
        "zf_019", "zf_021", "zf_022", "zf_023", "zf_024", "zf_026", "zf_027", "zf_028", "zf_032", "zf_036",
        "zf_038", "zf_039", "zf_040", "zf_042", "zf_043", "zf_044", "zf_046", "zf_047", "zf_048", "zf_049",
        "zf_051", "zf_059", "zf_060", "zf_067", "zf_070", "zf_071", "zf_072", "zf_073", "zf_074", "zf_075",
        "zf_076", "zf_077", "zf_078", "zf_079", "zf_083", "zf_084", "zf_085", "zf_086", "zf_087", "zf_088",
        "zf_090", "zf_092", "zf_093", "zf_094", "zf_099",
        "zm_009", "zm_010", "zm_011", "zm_012", "zm_013", "zm_014", "zm_015", "zm_016", "zm_020", "zm_025",
        "zm_029", "zm_030", "zm_031", "zm_033", "zm_034", "zm_035", "zm_037", "zm_041", "zm_045", "zm_050",
        "zm_052", "zm_053", "zm_054", "zm_055", "zm_056", "zm_057", "zm_058", "zm_061", "zm_062", "zm_063",
        "zm_064", "zm_065", "zm_066", "zm_068", "zm_069", "zm_080", "zm_081", "zm_082", "zm_089", "zm_091",
        "zm_095", "zm_096", "zm_097", "zm_098", "zm_100",
    ];

    /// <summary>
    /// Curated catalog of Kokoro voice packs offered in the UI. Voice ids are
    /// kokoro.cpp voice names and are case-sensitive.
    ///
    /// SECURITY: pinned SHA-256 hashes (lowercase hex) and sizes verified
    /// against the release assets on 2026-10-08. Downloads with a different
    /// hash are rejected. Before every public release: re-verify each hash
    /// and document provenance in <c>docs/AUDIO_MODEL_ASSETS.md</c>.
    /// </summary>
    public static readonly KokoroModelPackInfo[] AvailablePacks =
    [
        new("v1.1-en-zh", "English and Chinese (Kokoro v1.1)",
            new KokoroAssetInfo("kokoro-v1.1-zh.onnx", LarroyReleaseBase + "kokoro-v1.1-zh.onnx",
                "eefec708cbc7aba8e8129b5c2f7cb92e1fe7d281af1e1dd451592d9ff0714a0d", 343_605_188),
            new KokoroAssetInfo("voices-v1.1-zh.bin", LarroyReleaseBase + "voices-v1.1-zh.bin",
                "e678019845e6cfe3b7c34531779396b28f509451b91e6535d5dc09bbf11a4be5", 53_792_177),
            [
                new("af_maple", "English (US): Maple", "en-US"),
                new("af_sol", "English (US): Sol", "en-US"),
                new("bf_vale", "English (GB): Vale", "en-GB"),
                .. ChineseVoiceIds.Select(id => new KokoroVoiceInfo(id, $"中文 (CN): {id}", "zh-CN")),
            ]),
        new("v1.0-es", "Español (Kokoro v1.0)",
            new KokoroAssetInfo("kokoro-v1.0.onnx",
                "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/kokoro-v1.0.onnx",
                "7d5df8ecf7d4b1878015a32686053fd0eebe2bc377234608764cc0ef3636a6c5", 325_532_387),
            new KokoroAssetInfo("voices-v1.0-es.bin", LarroyReleaseBase + "voices-v1.0-es.bin",
                "cdaf0ecca101f3738763f6e3a13b63d46137909b8ebcf9de2be4ef2a73014c52", 1_566_778),
            [
                new("ef_dora", "Español (ES): Dora", "es-ES"),
                new("em_alex", "Español (ES): Alex", "es-ES"),
                new("em_santa", "Español (ES): Santa", "es-ES"),
            ]),
    ];

    private readonly string _packsDirectory;
    private readonly IOpenClawLogger _logger;

    public KokoroModelManager(string dataDirectory, IOpenClawLogger logger)
    {
        _packsDirectory = Path.Combine(dataDirectory, "models", "kokoro");
        _logger = logger;
        Directory.CreateDirectory(_packsDirectory);
    }

    /// <summary>Pack that contains <paramref name="voiceId"/>, or null for blank or unknown ids.</summary>
    public static KokoroModelPackInfo? FindPackForVoice(string? voiceId)
    {
        if (string.IsNullOrEmpty(voiceId)) return null;
        foreach (var pack in AvailablePacks)
        {
            foreach (var voice in pack.Voices)
            {
                if (string.Equals(voice.VoiceId, voiceId, StringComparison.Ordinal))
                    return pack;
            }
        }
        return null;
    }

    /// <summary>Catalog voice for <paramref name="voiceId"/>, or null for blank or unknown ids.</summary>
    public static KokoroVoiceInfo? FindVoice(string? voiceId) =>
        FindPackForVoice(voiceId)?.Voices.First(v => string.Equals(v.VoiceId, voiceId, StringComparison.Ordinal));

    /// <summary>Catalog entry for <paramref name="packId"/>; throws for unknown ids.</summary>
    public static KokoroModelPackInfo GetPack(string packId)
    {
        foreach (var pack in AvailablePacks)
        {
            if (string.Equals(pack.PackId, packId, StringComparison.Ordinal))
                return pack;
        }
        throw new ArgumentException($"Unknown Kokoro voice pack: '{packId}'.");
    }

    public string GetPackDirectory(string packId) => Path.Combine(_packsDirectory, GetPack(packId).PackId);

    public string GetModelPath(string packId) => Path.Combine(GetPackDirectory(packId), GetPack(packId).Model.FileName);

    public string GetVoicesPath(string packId) => Path.Combine(GetPackDirectory(packId), GetPack(packId).VoicesFile.FileName);

    /// <summary>True when both pack files are present and no download is half-finished.</summary>
    public bool IsPackDownloaded(string packId)
    {
        try
        {
            return !File.Exists(GetInstallMarkerPath(packId)) && HasCompleteLayout(packId);
        }
        catch (Exception ex)
        {
            _logger.Debug($"KokoroModelManager.IsPackDownloaded('{packId}'): {ex.Message}");
            return false;
        }
    }

    /// <summary>True when <paramref name="voiceId"/> is in the catalog and its pack is downloaded.</summary>
    public bool IsVoiceReady(string? voiceId) =>
        FindPackForVoice(voiceId) is { } pack && IsPackDownloaded(pack.PackId);

    /// <summary>
    /// Download and verify a voice pack. Reports progress as bytes downloaded
    /// across both files / <see cref="KokoroModelPackInfo.TotalSizeBytes"/>.
    /// Per-pack single-flight: concurrent calls await the same download, and
    /// cancelling one caller only cancels its wait.
    /// </summary>
    public Task DownloadPackAsync(
        string packId,
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var pack = GetPack(packId);
        if (IsPackDownloaded(pack.PackId))
        {
            _logger.Info($"Kokoro voice pack '{pack.PackId}' already downloaded");
            return Task.CompletedTask;
        }

        return SingleFlightDownload.RunAsync(
            InFlightDownloads,
            pack.PackId,
            token => DownloadPackCoreAsync(pack, progress, token),
            cancellationToken);
    }

    private async Task DownloadPackCoreAsync(
        KokoroModelPackInfo pack,
        IProgress<(long downloaded, long total)>? progress,
        CancellationToken cancellationToken)
    {
        // SECURITY: refuse to install anything without a pinned hash.
        if (string.IsNullOrWhiteSpace(pack.Model.Sha256) || string.IsNullOrWhiteSpace(pack.VoicesFile.Sha256))
        {
            throw new InvalidOperationException(
                $"Kokoro voice pack '{pack.PackId}' has no pinned SHA-256; refusing to download.");
        }

        var packDir = GetPackDirectory(pack.PackId);
        var installMarkerPath = GetInstallMarkerPath(pack.PackId);
        KokoroAssetInfo[] assets = [pack.Model, pack.VoicesFile];
        var tempPaths = assets.Select(a => Path.Combine(packDir, a.FileName + ".tmp")).ToArray();
        var hadCompleteLayout = HasCompleteLayout(pack.PackId);
        _logger.Info($"Downloading Kokoro voice pack '{pack.PackId}'");

        try
        {
            Directory.CreateDirectory(packDir);
            File.WriteAllText(installMarkerPath, string.Empty);
            using var httpClient = AllowedAssetDownload.CreateClient(TimeSpan.FromMinutes(30));
            long downloaded = 0;
            for (var i = 0; i < assets.Length; i++)
            {
                var asset = assets[i];
                var tempPath = tempPaths[i];
                using (var response = await AllowedAssetDownload.GetAsync(httpClient, asset.DownloadUrl, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
                    var buffer = new byte[81920];
                    int bytesRead;
                    while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                        downloaded += bytesRead;
                        progress?.Report((downloaded, pack.TotalSizeBytes));
                    }
                }

                await VerifyHashAsync(tempPath, asset.Sha256!, pack.PackId, cancellationToken).ConfigureAwait(false);
                File.Move(tempPath, Path.Combine(packDir, asset.FileName), overwrite: true);
            }

            File.Delete(installMarkerPath);
            _logger.Info($"Kokoro voice pack '{pack.PackId}' verified and ready at {packDir}");
        }
        catch
        {
            // Best-effort cleanup so a retry starts clean.
            DeleteTempFiles(tempPaths, "post-failure");
            try
            {
                if (!hadCompleteLayout && Directory.Exists(packDir))
                    Directory.Delete(packDir, recursive: true);
                if (!Directory.Exists(packDir) && File.Exists(installMarkerPath))
                    File.Delete(installMarkerPath);
            }
            catch (Exception cleanupEx) { _logger.Debug($"KokoroModelManager: post-failure pack cleanup failed: {cleanupEx.Message}"); }
            throw;
        }
        finally
        {
            DeleteTempFiles(tempPaths, "finally");
        }
    }

    private void DeleteTempFiles(string[] tempPaths, string stage)
    {
        foreach (var tempPath in tempPaths)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (Exception cleanupEx) { _logger.Debug($"KokoroModelManager: {stage} temp cleanup failed: {cleanupEx.Message}"); }
        }
    }

    private string GetInstallMarkerPath(string packId) => $"{GetPackDirectory(packId)}{InstallMarkerSuffix}";

    private bool HasCompleteLayout(string packId) =>
        File.Exists(GetModelPath(packId)) && File.Exists(GetVoicesPath(packId));

    /// <summary>
    /// Compare the SHA-256 of <paramref name="filePath"/> to <paramref name="expectedHex"/>.
    /// Does not echo the actual hash, to avoid handing attackers a confirmation oracle.
    /// </summary>
    private static async Task VerifyHashAsync(string filePath, string expectedHex, string packId, CancellationToken cancellationToken)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var actual = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        var actualHex = Convert.ToHexString(actual).ToLowerInvariant();
        if (!string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new System.Security.SecurityException(
                $"Kokoro voice pack '{packId}' failed integrity check. The downloaded file does not match the pinned SHA-256.");
        }
    }

    /// <summary>Delete a downloaded voice pack (and any stale install marker).</summary>
    public bool DeletePack(string packId)
    {
        var dir = GetPackDirectory(packId);
        var markerPath = GetInstallMarkerPath(packId);
        var deleted = false;
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
            deleted = true;
        }
        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
            deleted = true;
        }
        if (!deleted) return false;
        _logger.Info($"Deleted Kokoro voice pack '{packId}'");
        return true;
    }

    /// <summary>Total disk usage of a voice pack, or 0 if it is not on disk.</summary>
    public long GetPackSize(string packId)
    {
        var dir = GetPackDirectory(packId);
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(f).Length; }
            catch (Exception ex) { _logger.Debug($"KokoroModelManager.GetPackSize: skip file '{f}': {ex.Message}"); }
        }
        return total;
    }
}

/// <summary>One downloadable file of a Kokoro voice pack.</summary>
/// <param name="Sha256">Pinned lowercase hex SHA-256. Downloads are refused when null.</param>
/// <param name="SizeBytes">Exact file size, used for progress and the UI size hint.</param>
public sealed record KokoroAssetInfo(string FileName, string DownloadUrl, string? Sha256, long SizeBytes);

/// <summary>A Kokoro voice. <paramref name="VoiceId"/> is the case-sensitive kokoro.cpp voice name.</summary>
public sealed record KokoroVoiceInfo(string VoiceId, string DisplayName, string LanguageTag);

/// <summary>A Kokoro model plus the voices file it was trained with.</summary>
public sealed record KokoroModelPackInfo(
    string PackId,
    string DisplayName,
    KokoroAssetInfo Model,
    KokoroAssetInfo VoicesFile,
    IReadOnlyList<KokoroVoiceInfo> Voices)
{
    public long TotalSizeBytes => Model.SizeBytes + VoicesFile.SizeBytes;
}
