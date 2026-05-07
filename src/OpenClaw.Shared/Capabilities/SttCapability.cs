using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Capabilities;

/// <summary>
/// Speech-to-text node capability. Three commands:
///
/// * <see cref="TranscribeCommand"/> — bounded fixed-duration capture + transcription.
///   Caller must specify <c>maxDurationMs</c> (capped at <see cref="MaxTranscribeDurationMs"/>).
///   Useful for quick "give me 5 seconds of audio" prompts.
///
/// * <see cref="ListenCommand"/> — VAD-driven capture that returns when speech ends
///   or after <c>timeoutMs</c> (default <see cref="DefaultListenTimeoutMs"/>, range
///   <see cref="MinListenTimeoutMs"/>..<see cref="MaxListenTimeoutMs"/>).
///   Useful for conversational "listen until I stop talking" prompts.
///
/// * <see cref="StatusCommand"/> — reports engine readiness (no PII).
///
/// The actual engine lives in the tray (Whisper.net + NAudio + Silero VAD).
/// Whisper is local-first and privacy-respecting; the legacy WinRT
/// <c>SpeechRecognizer</c> + desktop SAPI fallback was removed because both
/// stacks are old, can leak audio to the Microsoft cloud (online-speech),
/// and don't work in unpackaged builds.
///
/// **Privacy invariants for the response surface:**
/// - Validation errors never echo the caller-supplied language string.
/// - Handler exceptions never propagate their <c>Message</c> into the response;
///   full detail stays in the local logger only. This is critical because
///   failed-invoke errors land in recent activity / support bundles.
/// - <see cref="StatusCommand"/> response carries no PII (no transcript fragments,
///   no language history, no device IDs, no model paths).
/// </summary>
public sealed class SttCapability : NodeCapabilityBase
{
    public const string TranscribeCommand = "stt.transcribe";
    public const string ListenCommand = "stt.listen";
    public const string StatusCommand = "stt.status";

    public const int MaxTranscribeDurationMs = 30_000;
    public const int MinListenTimeoutMs = 1_000;
    public const int MaxListenTimeoutMs = 120_000;
    public const int DefaultListenTimeoutMs = 30_000;

    public const string DefaultLanguage = "en-US";
    public const string AutoLanguage = "auto";

    /// <summary>
    /// Engine identifier returned in <c>engineEffective</c> on every successful
    /// stt.* response. Currently always <c>"whisper"</c>; the field exists so
    /// adding a future engine doesn't break the wire shape.
    /// </summary>
    public const string EngineWhisper = "whisper";

    private static readonly string[] _commands = [TranscribeCommand, ListenCommand, StatusCommand];

    // Conservative BCP-47 check: 2-3 letter language, optional script
    // (4 letter), optional region (2 letter or 3 digit), each separated
    // by a hyphen. Rejects whitespace and punctuation that would otherwise
    // trip Windows.Globalization.Language ctor. The literal "auto"
    // sentinel is accepted in addition (Whisper supports auto-detect).
    private static readonly Regex BcpTagRegex = new(
        "^[A-Za-z]{2,3}(?:-[A-Za-z]{4})?(?:-(?:[A-Za-z]{2}|[0-9]{3}))?$",
        RegexOptions.Compiled);

    public override string Category => "stt";
    public override IReadOnlyList<string> Commands => _commands;

    /// <summary>
    /// Tray-side handler for <see cref="TranscribeCommand"/>: bounded fixed-duration
    /// capture + transcription.
    /// </summary>
    public event Func<SttTranscribeArgs, CancellationToken, Task<SttTranscribeResult>>? TranscribeRequested;

    /// <summary>
    /// Tray-side handler for <see cref="ListenCommand"/>: VAD-driven capture that
    /// returns on end-of-speech or after <c>timeoutMs</c>.
    /// </summary>
    public event Func<SttListenArgs, CancellationToken, Task<SttListenResult>>? ListenRequested;

    /// <summary>
    /// Tray-side handler for <see cref="StatusCommand"/>: returns per-engine readiness.
    /// </summary>
    public event Func<CancellationToken, Task<SttStatusResult>>? StatusRequested;

    public SttCapability(IOpenClawLogger logger) : base(logger) { }

    /// <summary>
    /// Trim and validate a single language tag. Returns the trimmed tag on
    /// success, the literal <see cref="AutoLanguage"/> sentinel on a case-insensitive
    /// "auto" input, or <c>null</c> if the input is neither.
    /// Public so UI surfaces can validate against the same rule the wire applies.
    /// </summary>
    public static string? NormalizeLanguageTag(string tag)
    {
        var trimmed = tag.Trim();
        if (string.Equals(trimmed, AutoLanguage, StringComparison.OrdinalIgnoreCase))
            return AutoLanguage;
        return BcpTagRegex.IsMatch(trimmed) ? trimmed : null;
    }

    /// <summary>
    /// Resolve the language to use for a recognition call: per-call argument
    /// wins, then configured setting, then <see cref="DefaultLanguage"/>.
    /// Returns <c>null</c> if the resolved string fails validation.
    /// </summary>
    public static string? ResolveLanguage(string? requested, string? configured)
    {
        var candidate = !string.IsNullOrWhiteSpace(requested)
            ? requested
            : (!string.IsNullOrWhiteSpace(configured) ? configured : DefaultLanguage);

        return NormalizeLanguageTag(candidate!);
    }

    public override Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
        => ExecuteAsync(request, CancellationToken.None);

    public override async Task<NodeInvokeResponse> ExecuteAsync(
        NodeInvokeRequest request,
        CancellationToken cancellationToken)
    {
        return request.Command switch
        {
            TranscribeCommand => await HandleTranscribeAsync(request, cancellationToken).ConfigureAwait(false),
            ListenCommand     => await HandleListenAsync(request, cancellationToken).ConfigureAwait(false),
            StatusCommand     => await HandleStatusAsync(cancellationToken).ConfigureAwait(false),
            _ => Error($"Unknown command: {request.Command}")
        };
    }

    private async Task<NodeInvokeResponse> HandleTranscribeAsync(
        NodeInvokeRequest request,
        CancellationToken cancellationToken)
    {
        // maxDurationMs is required and bounded server-side. We deliberately
        // reject 0/negative rather than substituting a default — callers
        // explicitly choose how much mic time they're spending.
        var maxDurationMs = GetIntArg(request.Args, "maxDurationMs", 0);
        if (maxDurationMs <= 0)
            return Error("Missing required maxDurationMs");
        if (maxDurationMs > MaxTranscribeDurationMs)
            return Error($"maxDurationMs exceeds {MaxTranscribeDurationMs} ms");

        var requestedLanguage = GetStringArg(request.Args, "language");
        string? resolvedLanguage = null;
        if (!string.IsNullOrWhiteSpace(requestedLanguage))
        {
            resolvedLanguage = NormalizeLanguageTag(requestedLanguage);
            if (resolvedLanguage == null)
                return Error("Invalid language tag");
        }

        if (TranscribeRequested == null)
            return Error("STT transcribe not available");

        var args = new SttTranscribeArgs
        {
            MaxDurationMs = maxDurationMs,
            Language = resolvedLanguage  // null lets the tray fall back to its configured setting
        };

        Logger.Info($"stt.transcribe: maxDurationMs={args.MaxDurationMs}, language={args.Language ?? "(default)"}");

        try
        {
            var result = await TranscribeRequested(args, cancellationToken).ConfigureAwait(false);
            return Success(new
            {
                transcribed = result.Transcribed,
                text = result.Text,
                durationMs = result.DurationMs,
                language = result.Language,
                engineEffective = result.EngineEffective
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Error("Transcribe canceled");
        }
        catch (Exception ex)
        {
            // Privacy: never echo raw exception text into the response. The
            // exception flows through the failed-invoke path and may be
            // persisted to recent activity / support bundles. Full detail
            // stays in the local log only.
            Logger.Error("STT transcribe failed", ex);
            return Error("Transcribe failed");
        }
    }

    private async Task<NodeInvokeResponse> HandleListenAsync(
        NodeInvokeRequest request,
        CancellationToken cancellationToken)
    {
        // timeoutMs is optional with a sane default; bounded both ways so
        // a hostile caller can't pin the mic open for an hour.
        var timeoutMs = GetIntArg(request.Args, "timeoutMs", DefaultListenTimeoutMs);
        if (timeoutMs < MinListenTimeoutMs) timeoutMs = MinListenTimeoutMs;
        if (timeoutMs > MaxListenTimeoutMs) timeoutMs = MaxListenTimeoutMs;

        var requestedLanguage = GetStringArg(request.Args, "language");
        string resolvedLanguage = AutoLanguage;
        if (!string.IsNullOrWhiteSpace(requestedLanguage))
        {
            var normalized = NormalizeLanguageTag(requestedLanguage);
            if (normalized == null)
                return Error("Invalid language tag");
            resolvedLanguage = normalized;
        }

        if (ListenRequested == null)
            return Error("STT listen not available");

        var args = new SttListenArgs
        {
            TimeoutMs = timeoutMs,
            Language = resolvedLanguage
        };

        Logger.Info($"stt.listen: timeoutMs={timeoutMs}, language={resolvedLanguage}");

        try
        {
            var result = await ListenRequested(args, cancellationToken).ConfigureAwait(false);
            return Success(new
            {
                text = result.Text,
                language = result.Language,
                durationMs = result.DurationMs,
                segments = result.Segments,
                engineEffective = result.EngineEffective
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Error("Listen canceled");
        }
        catch (Exception ex)
        {
            // Same privacy invariant as Transcribe.
            Logger.Error("STT listen failed", ex);
            return Error("Listen failed");
        }
    }

    private async Task<NodeInvokeResponse> HandleStatusAsync(CancellationToken cancellationToken)
    {
        if (StatusRequested == null)
            return Error("STT status not available");

        try
        {
            var result = await StatusRequested(cancellationToken).ConfigureAwait(false);
            return Success(new
            {
                engine = result.Engine,
                readiness = result.Readiness,
                modelDownloadProgress = result.ModelDownloadProgress,
                isListenWithVadSupported = result.IsListenWithVadSupported,
                isBoundedTranscribeSupported = result.IsBoundedTranscribeSupported
            });
        }
        catch (Exception ex)
        {
            // Status must not leak engine internals; carry only a fixed message.
            Logger.Error("STT status failed", ex);
            return Error("Status failed");
        }
    }
}

public sealed class SttTranscribeArgs
{
    public int MaxDurationMs { get; set; }
    /// <summary>
    /// BCP-47 tag (e.g., "en-US"), the literal "auto" sentinel, or null
    /// to let the tray fall back to its configured <c>SttLanguage</c> setting.
    /// </summary>
    public string? Language { get; set; }
}

public sealed class SttTranscribeResult
{
    public bool Transcribed { get; set; }
    public string Text { get; set; } = "";
    public int DurationMs { get; set; }
    public string Language { get; set; } = SttCapability.DefaultLanguage;

    /// <summary>
    /// Engine that served this call. Always <see cref="SttCapability.EngineWhisper"/>
    /// today; the field exists so a future engine doesn't break the wire.
    /// </summary>
    public string EngineEffective { get; set; } = SttCapability.EngineWhisper;
}

public sealed class SttListenArgs
{
    public int TimeoutMs { get; set; }
    /// <summary>
    /// BCP-47 tag (e.g., "en-US"), or the literal "auto" sentinel
    /// (default; lets Whisper auto-detect).
    /// </summary>
    public string Language { get; set; } = SttCapability.AutoLanguage;
}

public sealed class SttListenResult
{
    public string Text { get; set; } = "";
    public string Language { get; set; } = SttCapability.AutoLanguage;
    public int DurationMs { get; set; }
    public IReadOnlyList<SttSegment> Segments { get; set; } = Array.Empty<SttSegment>();

    public string EngineEffective { get; set; } = SttCapability.EngineWhisper;
}

public sealed class SttSegment
{
    public string Text { get; set; } = "";
    public int StartMs { get; set; }
    public int EndMs { get; set; }
}

public sealed class SttStatusResult
{
    public string Engine { get; set; } = SttCapability.EngineWhisper;

    /// <summary>One of "ready", "initializing", "model-downloading", "model-not-downloaded", "unavailable".</summary>
    public string Readiness { get; set; } = "unavailable";

    /// <summary>0..1 download progress when <see cref="Readiness"/> == "model-downloading"; null otherwise.</summary>
    public double? ModelDownloadProgress { get; set; }

    public bool IsListenWithVadSupported { get; set; }
    public bool IsBoundedTranscribeSupported { get; set; }
}
