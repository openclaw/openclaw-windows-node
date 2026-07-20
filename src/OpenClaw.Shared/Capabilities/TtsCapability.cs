using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Capabilities;

public sealed class TtsCapability : NodeCapabilityBase
{
    public const string SpeakCommand = "tts.speak";
    public const string StatusCommand = "tts.status";
    public const string WindowsProvider = "windows";
    public const string ElevenLabsProvider = "elevenlabs";
    /// <summary>
    /// Local neural TTS via Sherpa-ONNX wrapping Piper voices. No network
    /// egress; voice models download once to %LOCALAPPDATA%.
    /// </summary>
    public const string PiperProvider = "piper";
    public const int MaxTextLength = 5000;

    // ============================================================
    // Provider readiness reasons (PII-free; surfaced by tts.status and used
    // by the fallback resolver below). Kept as string constants rather than
    // an enum so the wire shape mirrors stt.status's readiness string.
    // ============================================================

    /// <summary>Provider can serve a speak call right now.</summary>
    public const string ReadinessReady = "ready";
    /// <summary>ElevenLabs selected but no API key is configured.</summary>
    public const string ReadinessNeedsApiKey = "needs-api-key";
    /// <summary>ElevenLabs selected with a key but no voice id is configured.</summary>
    public const string ReadinessNeedsVoice = "needs-voice";
    /// <summary>Piper selected but the chosen voice isn't downloaded yet.</summary>
    public const string ReadinessVoiceNotDownloaded = "voice-not-downloaded";
    /// <summary>Provider is unknown or otherwise unusable.</summary>
    public const string ReadinessUnavailable = "unavailable";

    /// <summary>All known providers, in catalog order (Piper is the default).</summary>
    public static readonly string[] AllProviders = [PiperProvider, WindowsProvider, ElevenLabsProvider];

    private static readonly string[] _commands = [SpeakCommand, StatusCommand];

    public override string Category => "tts";
    public override IReadOnlyList<string> Commands => _commands;

    public event Func<TtsSpeakArgs, CancellationToken, Task<TtsSpeakResult>>? SpeakRequested;

    /// <summary>
    /// Tray-side handler for <see cref="StatusCommand"/>: reports per-provider
    /// readiness plus the configured/effective provider. Carries no PII.
    /// </summary>
    public event Func<CancellationToken, Task<TtsStatusResult>>? StatusRequested;

    public TtsCapability(IOpenClawLogger logger) : base(logger)
    {
    }

    public static string ResolveProvider(string? requestedProvider, string? configuredProvider)
    {
        var provider = string.IsNullOrWhiteSpace(requestedProvider)
            ? configuredProvider
            : requestedProvider;

        return string.IsNullOrWhiteSpace(provider)
            ? PiperProvider
            : provider.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Resolve the provider that should actually serve a speak request given
    /// the set of providers that are currently usable. The requested (then
    /// configured, then default) provider wins when it is usable. Fallback to
    /// Windows is limited to configured/default provider degradation; explicit
    /// provider requests stay strict so callers can rely on provider selection.
    /// When no provider is usable (should not happen because Windows is always
    /// available) the preferred provider is returned unchanged so the caller's
    /// downstream error stays meaningful.
    /// </summary>
    /// <param name="requestedProvider">Per-call provider override (may be null).</param>
    /// <param name="configuredProvider">User's configured default provider.</param>
    /// <param name="readyProviders">Lower-cased provider ids that can serve a call now.</param>
    public static TtsProviderResolution ResolveEffectiveProvider(
        string? requestedProvider,
        string? configuredProvider,
        IReadOnlySet<string> readyProviders,
        bool allowFallback)
    {
        ArgumentNullException.ThrowIfNull(readyProviders);

        var preferred = ResolveProvider(requestedProvider, configuredProvider);
        if (readyProviders.Contains(preferred))
            return new TtsProviderResolution(preferred, preferred, FellBack: false);

        // Windows is the always-offline safety net for configured/default
        // provider degradation. Explicit per-call provider requests stay
        // strict and are not silently rerouted.
        if (allowFallback
            && !string.Equals(preferred, WindowsProvider, StringComparison.Ordinal)
            && readyProviders.Contains(WindowsProvider))
        {
            return new TtsProviderResolution(preferred, WindowsProvider, FellBack: true);
        }

        // Nothing usable - surface the preferred provider so the dispatch
        // layer's "not configured" error is about the provider the user asked
        // for, not an arbitrary fallback.
        return new TtsProviderResolution(preferred, preferred, FellBack: false);
    }

    public override Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
        => ExecuteAsync(request, CancellationToken.None);

    public override async Task<NodeInvokeResponse> ExecuteAsync(
        NodeInvokeRequest request,
        CancellationToken cancellationToken)
    {
        if (string.Equals(request.Command, StatusCommand, StringComparison.Ordinal))
            return await HandleStatusAsync(cancellationToken).ConfigureAwait(false);

        if (!string.Equals(request.Command, SpeakCommand, StringComparison.Ordinal))
            return Error($"Unknown command: {request.Command}");

        var text = GetStringArg(request.Args, "text")?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return Error("Missing required text");
        if (text.Length > MaxTextLength)
            return Error($"TTS text exceeds {MaxTextLength} characters.");

        if (SpeakRequested == null)
            return Error("TTS speak not available");

        var args = new TtsSpeakArgs
        {
            Text = text,
            Provider = NormalizeOptional(GetStringArg(request.Args, "provider")),
            VoiceId = NormalizeOptional(GetStringArg(request.Args, "voiceId")),
            Model = NormalizeOptional(GetStringArg(request.Args, "model")),
            Interrupt = GetBoolArg(request.Args, "interrupt")
        };

        Logger.Info($"tts.speak: provider={args.Provider ?? "(default)"}, chars={args.Text.Length}, interrupt={args.Interrupt}");

        try
        {
            var result = await SpeakRequested(args, cancellationToken).ConfigureAwait(false);
            return Success(new
            {
                spoken = result.Spoken,
                provider = result.Provider,
                requestedProvider = result.RequestedProvider ?? result.Provider,
                fellBack = result.FellBack,
                contentType = result.ContentType,
                durationMs = result.DurationMs
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Error("Speak canceled");
        }
        catch (Exception ex)
        {
            // Privacy: never echo raw exception text into the response. The
            // exception flows through the failed-invoke path and may be
            // persisted to recent activity / support bundles. ElevenLabs
            // error messages can contain key prefixes; OS speech errors
            // can contain device names. Full detail stays in the local
            // log only. (Same pattern as SttCapability.)
            Logger.Error("TTS speak failed", ex);
            return Error("Speak failed");
        }
    }

    private async Task<NodeInvokeResponse> HandleStatusAsync(CancellationToken cancellationToken)
    {
        if (StatusRequested == null)
            return Error("TTS status not available");

        try
        {
            var result = await StatusRequested(cancellationToken).ConfigureAwait(false);
            return Success(new
            {
                configuredProvider = result.ConfiguredProvider,
                effectiveProvider = result.EffectiveProvider,
                willFallBack = result.WillFallBack,
                providers = result.Providers.Select(p => new
                {
                    provider = p.Provider,
                    readiness = p.Readiness,
                    isReady = p.IsReady
                }).ToArray()
            });
        }
        catch (Exception ex)
        {
            // Status must not leak provider internals (voice ids, key
            // fragments, device names); carry only a fixed message.
            Logger.Error("TTS status failed", ex);
            return Error("Status failed");
        }
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class TtsSpeakArgs
{
    public string Text { get; set; } = "";
    public string? Provider { get; set; }
    public string? VoiceId { get; set; }
    public string? Model { get; set; }
    public bool Interrupt { get; set; }
}

public sealed class TtsSpeakResult
{
    public bool Spoken { get; set; } = true;
    public string Provider { get; set; } = TtsCapability.WindowsProvider;

    /// <summary>
    /// The provider that was originally requested/configured before any
    /// fallback. Equals <see cref="Provider"/> when no fallback occurred.
    /// Null is treated as "same as Provider" by the response surface.
    /// </summary>
    public string? RequestedProvider { get; set; }

    /// <summary>
    /// True when the requested/configured provider was not usable and the
    /// service fell back to another provider (Windows) to serve the call.
    /// </summary>
    public bool FellBack { get; set; }

    public string? ContentType { get; set; }
    public int? DurationMs { get; set; }
}

/// <summary>
/// Result of <see cref="TtsCapability.ResolveEffectiveProvider"/>: the
/// provider originally preferred and the one that should actually serve the
/// call after fallback.
/// </summary>
/// <param name="RequestedProvider">The resolved requested/configured provider before fallback.</param>
/// <param name="EffectiveProvider">The provider that should serve the call.</param>
/// <param name="FellBack">True when <paramref name="EffectiveProvider"/> differs from <paramref name="RequestedProvider"/> because the latter wasn't usable.</param>
public readonly record struct TtsProviderResolution(
    string RequestedProvider,
    string EffectiveProvider,
    bool FellBack);

/// <summary>Per-provider readiness snapshot (PII-free) for <c>tts.status</c>.</summary>
public sealed class TtsProviderStatus
{
    public string Provider { get; set; } = "";
    /// <summary>
    /// One of <see cref="TtsCapability.ReadinessReady"/>,
    /// <see cref="TtsCapability.ReadinessNeedsApiKey"/>,
    /// <see cref="TtsCapability.ReadinessNeedsVoice"/>,
    /// <see cref="TtsCapability.ReadinessVoiceNotDownloaded"/>, or
    /// <see cref="TtsCapability.ReadinessUnavailable"/>.
    /// </summary>
    public string Readiness { get; set; } = TtsCapability.ReadinessUnavailable;
    public bool IsReady { get; set; }
}

/// <summary>
/// Status surface for TTS, mirroring <c>stt.status</c>. Reports the
/// configured default provider, the provider that would actually run now
/// (after fallback), and per-provider readiness. The configured/effective
/// view reflects the configured defaults only - a specific <c>tts.speak</c>
/// call that supplies its own voiceId can still avoid a fallback that this
/// snapshot reports. Carries no PII (no voice ids, no key fragments, no
/// device names).
/// </summary>
public sealed class TtsStatusResult
{
    public string ConfiguredProvider { get; set; } = TtsCapability.PiperProvider;
    public string EffectiveProvider { get; set; } = TtsCapability.PiperProvider;
    public bool WillFallBack { get; set; }
    public IReadOnlyList<TtsProviderStatus> Providers { get; set; } = Array.Empty<TtsProviderStatus>();
}
