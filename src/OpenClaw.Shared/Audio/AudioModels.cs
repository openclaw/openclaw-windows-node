using System;
using System.Collections.Generic;

namespace OpenClaw.Shared.Audio;

/// <summary>Result of a speech-to-text transcription segment.</summary>
public sealed class TranscriptionResult
{
    public string Text { get; init; } = "";
    public TimeSpan Start { get; init; }
    public TimeSpan End { get; init; }
    public string Language { get; init; } = "en";
}

/// <summary>
/// Aggregated result of a single silence-bounded utterance — i.e. all the
/// Whisper segments produced from one VAD-bounded speech burst, combined.
/// Consumers that need "what the user said" (chat submission, stt.listen)
/// should listen for this event instead of per-segment TranscriptionResult
/// to avoid sending partial text.
/// </summary>
public sealed class UtteranceResult
{
    /// <summary>Concatenated text across all segments, single-spaced.</summary>
    public string Text { get; init; } = "";
    /// <summary>Language detected on the first segment, or null if no segments.</summary>
    public string? Language { get; init; }
    /// <summary>Start of the first segment relative to capture start.</summary>
    public TimeSpan Start { get; init; }
    /// <summary>End of the last segment relative to capture start.</summary>
    public TimeSpan End { get; init; }
    /// <summary>Immutable snapshot of the per-segment results.</summary>
    public IReadOnlyList<TranscriptionResult> Segments { get; init; } = Array.Empty<TranscriptionResult>();
}

/// <summary>Voice-activity detection event.</summary>
public sealed class VadEvent
{
    public bool IsSpeaking { get; init; }
    public float Probability { get; init; }
}

/// <summary>Configuration for the audio pipeline.</summary>
public sealed class AudioPipelineOptions
{
    /// <summary>Path to the Whisper GGML model file.</summary>
    public string ModelPath { get; init; } = "";

    /// <summary>Language code for STT (e.g. "en", "auto").</summary>
    public string Language { get; init; } = "auto";

    /// <summary>Seconds of silence before a speech segment is finalized.</summary>
    public float SilenceTimeoutSeconds { get; init; } = 1.5f;

    /// <summary>Optional audio device ID. Null = system default microphone.</summary>
    public string? DeviceId { get; init; }

    /// <summary>VAD probability threshold (0.0–1.0). Audio above this is considered speech.</summary>
    public float VadThreshold { get; init; } = 0.3f;
}

/// <summary>Pipeline state.</summary>
public enum AudioPipelineState
{
    Stopped,
    Starting,
    Listening,
    Processing,
    Error
}
