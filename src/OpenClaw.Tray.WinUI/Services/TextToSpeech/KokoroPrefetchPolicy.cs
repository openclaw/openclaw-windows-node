using System;
using OpenClaw.Shared.Audio;
using OpenClaw.Shared.Capabilities;

namespace OpenClawTray.Services;

/// <summary>When <see cref="KokoroVoicePackPrefetcher"/> downloads a Kokoro voice pack.</summary>
internal static class KokoroPrefetchPolicy
{
    /// <summary>
    /// Pack to download now, or null. Requires TTS in use, auto-download not declined,
    /// Kokoro as the provider, a catalog voice whose pack is neither downloaded nor
    /// downloading, and an unmetered connection (queried last, only when needed).
    /// </summary>
    public static string? SelectPackToPrefetch(
        string? provider,
        string? voiceId,
        bool ttsEnabled,
        bool autoDownload,
        Func<string, bool> isPackDownloaded,
        Func<bool> isMeteredConnection)
    {
        if (!ttsEnabled || !autoDownload) return null;
        if (!string.Equals(TtsCapability.ResolveProvider(null, provider), TtsCapability.KokoroProvider, StringComparison.Ordinal))
            return null;
        if (KokoroModelManager.FindPackForVoice(voiceId) is not { } pack) return null;
        if (isPackDownloaded(pack.PackId) || KokoroModelManager.IsDownloadInProgress(pack.PackId)) return null;
        return isMeteredConnection() ? null : pack.PackId;
    }
}
