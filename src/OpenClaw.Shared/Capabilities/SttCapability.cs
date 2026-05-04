using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Capabilities;

/// <summary>
/// Standalone speech-to-text capability for the Windows node.
///
/// Mirrors <see cref="TtsCapability"/> in shape: shared bounds &amp; arg
/// parsing, with a single event hook that the tray's
/// <c>SpeechToTextService</c> implements using
/// <c>Windows.Media.SpeechRecognition.SpeechContinuousRecognitionSession</c>.
///
/// Audio in → text out. No Talk Mode integration, no chat send,
/// no wake word. Default-off privacy-sensitive command.
/// </summary>
public sealed class SttCapability : NodeCapabilityBase
{
    public const string TranscribeCommand = "stt.transcribe";
    public const string WindowsProvider = "windows";
    public const int MaxDurationMs = 30_000;
    public const string DefaultLanguage = "en-US";

    private static readonly string[] _commands = [TranscribeCommand];

    // Conservative BCP-47 check: 2-3 letter language, optional script
    // (4 letter), optional region (2 letter or 3 digit), each separated
    // by a hyphen. Rejects whitespace and punctuation that would otherwise
    // trip Windows.Globalization.Language ctor.
    private static readonly Regex BcpTagRegex = new(
        "^[A-Za-z]{2,3}(?:-[A-Za-z]{4})?(?:-(?:[A-Za-z]{2}|[0-9]{3}))?$",
        RegexOptions.Compiled);

    public override string Category => "stt";
    public override IReadOnlyList<string> Commands => _commands;

    /// <summary>
    /// Tray-side handler that performs the actual recognition.
    /// </summary>
    public event Func<SttTranscribeArgs, CancellationToken, Task<SttTranscribeResult>>? TranscribeRequested;

    public SttCapability(IOpenClawLogger logger) : base(logger)
    {
    }

    /// <summary>
    /// Resolve the language string callers should run recognition with:
    /// per-call argument wins, then configured setting, then default.
    /// Returns null if the supplied string fails the BCP-47 sanity check
    /// (caller should map this to a clear error).
    /// </summary>
    public static string? ResolveLanguage(string? requested, string? configured)
    {
        var candidate = !string.IsNullOrWhiteSpace(requested)
            ? requested
            : (!string.IsNullOrWhiteSpace(configured) ? configured : DefaultLanguage);

        return NormalizeLanguageTag(candidate!);
    }

    /// <summary>
    /// Trim and BCP-47-validate a single tag. Returns the trimmed tag on
    /// success or null if the input is not a recognizable language tag.
    /// </summary>
    private static string? NormalizeLanguageTag(string tag)
    {
        var trimmed = tag.Trim();
        return BcpTagRegex.IsMatch(trimmed) ? trimmed : null;
    }

    public override Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
        => ExecuteAsync(request, CancellationToken.None);

    public override async Task<NodeInvokeResponse> ExecuteAsync(
        NodeInvokeRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Command, TranscribeCommand, StringComparison.Ordinal))
            return Error($"Unknown command: {request.Command}");

        // maxDurationMs is required and bounded server-side. We deliberately
        // reject 0/negative rather than substituting a default — callers
        // explicitly choose how much mic time they're spending.
        var maxDurationMs = GetIntArg(request.Args, "maxDurationMs", 0);
        if (maxDurationMs <= 0)
            return Error("Missing required maxDurationMs");
        if (maxDurationMs > MaxDurationMs)
            return Error($"maxDurationMs exceeds {MaxDurationMs} ms");

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
                language = result.Language
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
}

public sealed class SttTranscribeArgs
{
    public int MaxDurationMs { get; set; }
    /// <summary>
    /// BCP-47 language tag (e.g., "en-US"). Null lets the tray service
    /// fall back to its configured <c>SttLanguage</c> setting.
    /// </summary>
    public string? Language { get; set; }
}

public sealed class SttTranscribeResult
{
    public bool Transcribed { get; set; }
    public string Text { get; set; } = "";
    public int DurationMs { get; set; }
    public string Language { get; set; } = SttCapability.DefaultLanguage;
}
