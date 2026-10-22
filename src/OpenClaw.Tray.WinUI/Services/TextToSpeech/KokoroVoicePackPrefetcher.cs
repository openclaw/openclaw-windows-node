using System;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Audio;

namespace OpenClawTray.Services;

/// <summary>
/// Downloads the selected Kokoro voice pack in the background so the default
/// provider can speak without a manual Voice Settings download. Evaluated at
/// startup and after every settings save (setup enabling TTS, provider or voice
/// changes, the Download/Delete buttons). A failed or skipped download is not
/// retried until the next launch or save. The transfer itself is shared with
/// Voice Settings through <see cref="KokoroModelManager"/>'s single-flight.
/// Model storage is created only when a download is eligible, so this never blocks startup.
/// </summary>
internal sealed class KokoroVoicePackPrefetcher
{
    private readonly SettingsManager _settings;
    private readonly IOpenClawLogger _logger;
    private readonly string _dataDirectory;
    private readonly Func<bool> _isMeteredConnection;
    private KokoroModelManager? _models;

    public KokoroVoicePackPrefetcher(
        SettingsManager settings,
        IOpenClawLogger logger,
        string dataDirectory,
        Func<bool> isMeteredConnection)
    {
        _settings = settings;
        _logger = logger;
        _dataDirectory = dataDirectory;
        _isMeteredConnection = isMeteredConnection;
    }

    public void Start()
    {
        _settings.Saved += (_, _) => Evaluate();
        Evaluate();
    }

    /// <summary>
    /// Created on first use, after the settings-only gates in <see cref="KokoroPrefetchPolicy"/>
    /// pass, so a disabled feature never touches model storage and a storage failure
    /// (no permission, a file where the directory should be) stays inside Evaluate's
    /// handler instead of reaching app startup. A failed creation is retried on the next
    /// evaluation; a racing duplicate creation is harmless (CreateDirectory is idempotent).
    /// </summary>
    private KokoroModelManager GetModels() =>
        _models ??= new KokoroModelManager(_dataDirectory, _logger);

    private void Evaluate()
    {
        string? packId;
        KokoroModelManager models;
        try
        {
            packId = KokoroPrefetchPolicy.SelectPackToPrefetch(
                _settings.TtsProvider,
                _settings.TtsKokoroVoiceId,
                _settings.NodeTtsEnabled || _settings.VoiceTtsEnabled,
                _settings.TtsKokoroAutoDownload,
                id => GetModels().IsPackDownloaded(id),
                _isMeteredConnection);
            if (packId is null) return;
            models = GetModels();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Kokoro voice pack prefetch check failed: {ex.Message}");
            return;
        }

        _logger.Info($"Prefetching Kokoro voice pack '{packId}' in the background");
        // Not Task.Run: the single-flight entry is registered synchronously, so a Voice
        // Settings save that triggered this sees IsDownloadInProgress and joins it.
        _ = DownloadAsync(models, packId);
    }

    private async Task DownloadAsync(KokoroModelManager models, string packId)
    {
        try
        {
            await models.DownloadPackAsync(packId, progress: null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Kokoro voice pack prefetch failed ({VoicePackDownloadFailure.Classify(ex)}): {ex.Message}");
        }
    }
}
