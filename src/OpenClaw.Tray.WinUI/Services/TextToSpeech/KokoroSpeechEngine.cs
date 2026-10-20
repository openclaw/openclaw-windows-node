using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Kokoro.Net;
using OpenClaw.Shared;
using OpenClaw.Shared.Audio;

namespace OpenClawTray.Services;

/// <summary>
/// Process-wide owner of the single loaded Kokoro context. Loading a voice pack
/// takes seconds and hundreds of MB, and three call sites (NodeService, the chat
/// fallback, Voice Settings previews) construct their own
/// <see cref="TextToSpeechService"/>, so the context lives here instead of per
/// service. The engine lives for the process; <see cref="UnloadAsync"/> frees
/// the context before its pack files are deleted.
/// </summary>
internal sealed class KokoroSpeechEngine
{
    public static KokoroSpeechEngine Shared { get; } = new(new AppLogger());

    private readonly IOpenClawLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1); // serializes load + synthesis; one kokoro_ctx is not thread-safe
    private KokoroContext? _context;
    private string? _loadedPackId;

    internal KokoroSpeechEngine(IOpenClawLogger logger) => _logger = logger;

    /// <summary>
    /// Synthesize <paramref name="text"/> with <paramref name="voiceId"/> from
    /// <paramref name="packId"/>, loading (or switching to) that pack first.
    /// Native synthesis cannot be interrupted, so cancellation is honored only
    /// between load, synthesis and encoding.
    /// </summary>
    public async Task<byte[]> SynthesizeWavAsync(
        KokoroModelManager models,
        string packId,
        string voiceId,
        string text,
        float speed,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var context = EnsureLoaded(models, packId);
                cancellationToken.ThrowIfCancellationRequested();
                var audio = context.Synthesize(text, voiceId, speed);
                cancellationToken.ThrowIfCancellationRequested();
                return PcmWavEncoder.EncodeMono16(audio.Samples, audio.SampleRate);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Dispose the context if it holds <paramref name="packId"/>, so its files can be deleted.</summary>
    public async Task UnloadAsync(string packId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!string.Equals(_loadedPackId, packId, StringComparison.Ordinal))
                return;
            _context?.Dispose();
            _context = null;
            _loadedPackId = null;
            _logger.Info($"Kokoro voice pack '{packId}' unloaded");
        }
        finally
        {
            _gate.Release();
        }
    }

    // Caller holds _gate.
    private KokoroContext EnsureLoaded(KokoroModelManager models, string packId)
    {
        if (_context is not null && string.Equals(_loadedPackId, packId, StringComparison.Ordinal))
            return _context;

        _context?.Dispose();
        _context = null;
        _loadedPackId = null;

        var stopwatch = Stopwatch.StartNew();
        // Cpu is explicit so kokoro.cpp skips its CUDA probe (and the stderr noise it prints).
        var context = new KokoroContext(
            models.GetModelPath(packId),
            models.GetVoicesPath(packId),
            KokoroContext.BundledDictDirectory,
            new KokoroOptions(KokoroDevice.Cpu, 0));
        _context = context;
        _loadedPackId = packId;
        _logger.Info($"Kokoro voice pack '{packId}' loaded in {stopwatch.ElapsedMilliseconds} ms (kokoro {KokoroContext.Version()})");
        return context;
    }
}
