using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;
using DesktopDictationGrammar = System.Speech.Recognition.DictationGrammar;
using DesktopRecognizeMode = System.Speech.Recognition.RecognizeMode;
using DesktopSpeechRecognitionEngine = System.Speech.Recognition.SpeechRecognitionEngine;

namespace OpenClawTray.Services.SpeechToText;

/// <summary>
/// Bounded-window speech-to-text service backed by Windows
/// <see cref="SpeechRecognizer"/> running in continuous-recognition mode.
/// One call records for up to <c>maxDurationMs</c>, accumulating
/// <see cref="SpeechContinuousRecognitionSession.ResultGenerated"/> phrases,
/// then stops the session and returns the joined transcript.
///
/// Single-flight: a second concurrent caller fails fast with
/// "STT already in progress" rather than tearing down the active session
/// (the capability deliberately exposes no <c>interrupt</c> arg).
///
/// **Privacy invariant:** transcript text is never passed to <see cref="_logger"/>.
/// Logger sees outcome + duration only.
/// </summary>
public sealed class SpeechToTextService : IDisposable
{
    private readonly IOpenClawLogger _logger;
    private readonly SettingsManager _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SpeechToTextService(IOpenClawLogger logger, SettingsManager settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public async Task<SttTranscribeResult> TranscribeAsync(SttTranscribeArgs args, CancellationToken cancellationToken = default)
    {
        // Resolve language: per-call wins, then configured setting, then default.
        var languageTag = !string.IsNullOrWhiteSpace(args.Language)
            ? args.Language!
            : (!string.IsNullOrWhiteSpace(_settings.SttLanguage) ? _settings.SttLanguage : SttCapability.DefaultLanguage);

        // Preflight: bail before opening the mic if the OS can't recognize this language.
        ValidateLanguageSupported(languageTag);

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("STT already in progress");

        SpeechRecognizer? recognizer = null;
        try
        {
            try
            {
                recognizer = await CreateRecognizerAsync(languageTag);

                var stopwatch = Stopwatch.StartNew();
                var text = await CaptureWindowAsync(recognizer, args.MaxDurationMs, cancellationToken);
                stopwatch.Stop();

                // Log outcome only — never the transcript text.
                _logger.Info($"stt.transcribe completed: language={languageTag}, durationMs={stopwatch.ElapsedMilliseconds:0}, transcribed={!string.IsNullOrEmpty(text)}");

                return new SttTranscribeResult
                {
                    Transcribed = !string.IsNullOrEmpty(text),
                    Text = text,
                    DurationMs = (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue),
                    Language = languageTag
                };
            }
            catch (InvalidOperationException)
            {
                // Already wrapped with a friendly message (mic denied, mic
                // unavailable, language pack missing, no speech, "STT already
                // in progress", or our own "Speech recognizer unavailable: X").
                throw;
            }
            catch (Exception ex) when (IsSapiError(ex.HResult))
            {
                // The Windows SAPI / WinRT SpeechRecognition stack often
                // surfaces a generic "Internal Speech Error" / "The text
                // associated with this error code could not be found." for
                // ANY failure inside CompileConstraintsAsync, StartAsync, or
                // session callbacks. Keep the HRESULT visible because Windows
                // often maps these failures to "Internal Speech Error".
                _logger.Warn($"[stt] speech stack failure: HRESULT=0x{ex.HResult:X8} type={ex.GetType().Name} message={ex.Message}");
                recognizer?.Dispose();
                recognizer = null;

                try
                {
                    _logger.Info($"[stt] falling back to desktop SAPI recognizer: language={languageTag}");
                    var stopwatch = Stopwatch.StartNew();
                    var text = await CaptureWindowWithDesktopSapiAsync(languageTag, args.MaxDurationMs, cancellationToken).ConfigureAwait(false);
                    stopwatch.Stop();

                    _logger.Info($"stt.transcribe completed: engine=desktop-sapi, language={languageTag}, durationMs={stopwatch.ElapsedMilliseconds:0}, transcribed={!string.IsNullOrEmpty(text)}");
                    return new SttTranscribeResult
                    {
                        Transcribed = !string.IsNullOrEmpty(text),
                        Text = text,
                        DurationMs = (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue),
                        Language = languageTag
                    };
                }
                catch (Exception fallbackEx)
                {
                    _logger.Warn($"[stt] desktop SAPI fallback failed: type={fallbackEx.GetType().Name} message={fallbackEx.Message}");
                    throw new InvalidOperationException(
                        $"Speech recognition failed (0x{ex.HResult:X8}) and the desktop SAPI fallback also failed: {fallbackEx.Message}. Confirm Online speech recognition, microphone access, and the Speech optional feature are enabled for this language. Underlying WinRT error: {ex.Message}",
                        fallbackEx);
                }
            }
            catch (Exception ex)
            {
                // Unexpected — log the HRESULT so we can extend IsSapiError if
                // a real SAPI error slips through the filter, then re-throw
                // unwrapped so the capability surfaces the original message.
                _logger.Warn($"[stt] unmapped failure: HRESULT=0x{ex.HResult:X8} type={ex.GetType().Name} message={ex.Message}");
                throw;
            }
        }
        finally
        {
            recognizer?.Dispose();
            _gate.Release();
        }
    }

    private static void ValidateLanguageSupported(string tag)
    {
        // Default dictation grammar uses the same topic-language inventory.
        // Match by case-insensitive tag before opening the mic.
        var supported = SpeechRecognizer.SupportedTopicLanguages;
        if (supported == null || supported.Count == 0)
            throw new InvalidOperationException("Speech recognition is unavailable on this system.");

        var match = supported.Any(lang =>
            string.Equals(lang.LanguageTag, tag, StringComparison.OrdinalIgnoreCase));
        if (!match)
            throw new InvalidOperationException($"Language pack '{tag}' is not installed for speech recognition.");
    }

    private async Task<SpeechRecognizer> CreateRecognizerAsync(string languageTag)
    {
        var systemLanguageTag = SpeechRecognizer.SystemSpeechLanguage?.LanguageTag;
        var useSystemLanguage =
            string.Equals(systemLanguageTag, languageTag, StringComparison.OrdinalIgnoreCase);

        _logger.Info($"[stt] recognizer language: requested={languageTag}, system={systemLanguageTag ?? "(none)"}, mode={(useSystemLanguage ? "system" : "explicit")}");

        SpeechRecognizer recognizer;
        try
        {
            // If the caller requested the active Windows speech language, use
            // the system-language constructor. This follows the same path as
            // built-in dictation more closely than forcing an equivalent tag.
            recognizer = useSystemLanguage
                ? new SpeechRecognizer()
                : new SpeechRecognizer(new Language(languageTag));
        }
        catch (ArgumentException ex)
        {
            // Defense in depth — preflight should have caught this.
            throw new InvalidOperationException($"Language pack '{languageTag}' is not installed for speech recognition.", ex);
        }

        try
        {
            // Do not add an explicit SpeechRecognitionTopicConstraint here.
            // CompileConstraintsAsync with an empty Constraints collection uses
            // Windows' default dictation grammar, which avoids 0x800455A0
            // failures seen on some systems with the explicit Dictation topic.
            _logger.Info($"[stt] compiling recognizer constraints: mode={(useSystemLanguage ? "system" : "explicit")}");
            var compilation = await recognizer.CompileConstraintsAsync();
            _logger.Info($"[stt] recognizer constraints compiled: status={compilation.Status}, mode={(useSystemLanguage ? "system" : "explicit")}");
            if (compilation.Status != SpeechRecognitionResultStatus.Success)
            {
                throw new InvalidOperationException($"Speech recognizer unavailable: {compilation.Status}");
            }

            return recognizer;
        }
        catch
        {
            recognizer.Dispose();
            throw;
        }
    }

    private async Task<string> CaptureWindowAsync(
        SpeechRecognizer recognizer,
        int maxDurationMs,
        CancellationToken cancellationToken)
    {
        // Buffer phrase results as they arrive; concatenate at the end.
        var phrases = new List<string>();
        var phraseLock = new object();

        void OnResult(SpeechContinuousRecognitionSession session, SpeechContinuousRecognitionResultGeneratedEventArgs e)
        {
            // Drop low-confidence noise — Rejected confidence is what
            // SpeechRecognizer returns for babble / background sound.
            if (e.Result.Confidence == SpeechRecognitionConfidence.Rejected)
                return;
            var phrase = e.Result.Text;
            if (string.IsNullOrWhiteSpace(phrase))
                return;
            lock (phraseLock)
            {
                phrases.Add(phrase);
            }
        }

        recognizer.ContinuousRecognitionSession.ResultGenerated += OnResult;

        try
        {
            using var durationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            durationCts.CancelAfter(maxDurationMs);

            try
            {
                await recognizer.ContinuousRecognitionSession.StartAsync().AsTask(cancellationToken);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException("Microphone permission denied. Enable microphone access for desktop apps in Windows Settings → Privacy & security → Microphone (packaged MSIX installs additionally need per-app permission for OpenClaw Tray).", ex);
            }
            catch (Exception ex) when ((uint)ex.HResult == 0x80045509)
            {
                // SPERR_AUDIO_NOT_FOUND — no audio input device available.
                throw new InvalidOperationException("Microphone unavailable.", ex);
            }
            // Other SAPI/speech errors are caught at the outer scope
            // (TranscribeAsync) so failures from CompileConstraintsAsync or
            // session callbacks get the same friendly mapping.

            try
            {
                await Task.Delay(Timeout.Infinite, durationCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Either the caller cancelled or the duration cap fired.
                // Both paths fall through to StopAsync below — we MUST
                // release the mic before deciding whether to surface the
                // cancel as an error.
            }

            // Always try to stop cleanly. StopAsync awaits any in-flight
            // ResultGenerated dispatch so we don't drop the last phrase.
            try
            {
                await recognizer.ContinuousRecognitionSession.StopAsync().AsTask(CancellationToken.None);
            }
            catch
            {
                // StopAsync can throw if the session was already cancelled by the
                // OS; we still want to release the mic and proceed.
            }

            // Caller cancellation wins over a partial transcript — the API
            // contract returns "Transcribe canceled" rather than partial text.
            // Duration-cap cancellation falls through and we return what we got.
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            recognizer.ContinuousRecognitionSession.ResultGenerated -= OnResult;
        }

        lock (phraseLock)
        {
            if (phrases.Count == 0)
            {
                // Plan contract: no-speech / timeout is an error, not a
                // success with empty text. Caller distinguishes this from
                // a transient failure by the message.
                throw new InvalidOperationException("No speech detected within the bounded capture window.");
            }
            var sb = new StringBuilder(phrases.Sum(p => p.Length + 1));
            for (int i = 0; i < phrases.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(phrases[i]);
            }
            return sb.ToString();
        }
    }

    private static async Task<string> CaptureWindowWithDesktopSapiAsync(
        string languageTag,
        int maxDurationMs,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() => CaptureWindowWithDesktopSapi(languageTag, maxDurationMs, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static string CaptureWindowWithDesktopSapi(
        string languageTag,
        int maxDurationMs,
        CancellationToken cancellationToken)
    {
        var culture = CultureInfo.GetCultureInfo(languageTag);
        var recognizerInfo = DesktopSpeechRecognitionEngine.InstalledRecognizers()
            .FirstOrDefault(info => string.Equals(info.Culture.Name, culture.Name, StringComparison.OrdinalIgnoreCase));

        if (recognizerInfo == null)
            throw new InvalidOperationException($"Desktop speech recognizer language pack '{languageTag}' is not installed.");

        using var engine = new DesktopSpeechRecognitionEngine(recognizerInfo);
        var phrases = new List<string>();
        var phraseLock = new object();
        Exception? recognitionError = null;

        using var recognitionEnded = new ManualResetEventSlim(false);
        using var cancellationRegistration = cancellationToken.Register(() => recognitionEnded.Set());

        engine.SpeechRecognized += (_, e) =>
        {
            if (e.Result.Confidence <= 0.0f || string.IsNullOrWhiteSpace(e.Result.Text))
                return;

            lock (phraseLock)
            {
                phrases.Add(e.Result.Text);
            }
        };
        engine.RecognizeCompleted += (_, e) =>
        {
            if (e.Error != null)
                recognitionError = e.Error;
            recognitionEnded.Set();
        };

        try
        {
            engine.LoadGrammar(new DesktopDictationGrammar());
            engine.SetInputToDefaultAudioDevice();
            engine.RecognizeAsync(DesktopRecognizeMode.Multiple);

            recognitionEnded.Wait(maxDurationMs);
        }
        finally
        {
            try
            {
                engine.RecognizeAsyncStop();
            }
            catch (InvalidOperationException)
            {
                engine.RecognizeAsyncCancel();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (recognitionError != null)
            throw new InvalidOperationException($"Desktop speech recognition failed: {recognitionError.Message}", recognitionError);

        lock (phraseLock)
        {
            if (phrases.Count == 0)
                throw new InvalidOperationException("No speech detected within the bounded capture window.");

            var sb = new StringBuilder(phrases.Sum(p => p.Length + 1));
            for (int i = 0; i < phrases.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(phrases[i]);
            }
            return sb.ToString();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <summary>
    /// True for HRESULTs in the SAPI/Windows speech facility ranges. Windows
    /// surfaces these as "Internal Speech Error" / "The text associated with
    /// this error code could not be found" because the strings are not in the
    /// system message table — the most common real cause is online speech
    /// recognition being disabled in Privacy settings.
    /// </summary>
    private static bool IsSapiError(int hresult)
    {
        var u = (uint)hresult;
        // Windows speech HRESULTs use facility 0x004 (FACILITY_ITF for SAPI
        // and the WinRT speech subsystem). The full range 0x8004XXXX is
        // shared with other COM/ITF errors, but the "no friendly text"
        // / "Internal Speech Error" surface is specific to this range and
        // a broader catch is safer than missing real failures. Note: we
        // already handle UnauthorizedAccessException + 0x80045509
        // ("Microphone unavailable") above, so this catch sees only the
        // residual speech-stack errors.
        return (u & 0xFFFF0000u) == 0x80040000u;
    }
}
