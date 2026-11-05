using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Audio;

/// <summary>The model and transcription I/O used by audio capture pipelines.</summary>
public interface ISpeechToTextService : IDisposable
{
    bool IsModelLoaded { get; }
    void LoadModel(string modelPath);
    Task<List<TranscriptionResult>> TranscribeAsync(
        float[] samples, string language = "auto", CancellationToken cancellationToken = default);
}
