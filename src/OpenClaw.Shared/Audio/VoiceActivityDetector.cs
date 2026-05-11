using System;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace OpenClaw.Shared.Audio;

/// <summary>
/// Voice Activity Detection using Silero VAD ONNX model.
/// Processes 16 kHz mono audio in 512-sample chunks (~32 ms each)
/// and returns a speech probability per chunk.
/// </summary>
public sealed class VoiceActivityDetector : IDisposable
{
    private InferenceSession? _session;
    private float[] _state;      // internal RNN state: shape [2, 1, 128]
    private readonly int _stateSize;
    private readonly IOpenClawLogger _logger;

    /// <summary>Expected sample rate for input audio.</summary>
    public const int SampleRate = 16000;

    /// <summary>Number of samples per VAD chunk (512 @ 16 kHz = 32 ms).</summary>
    public const int ChunkSamples = 512;

    public bool IsLoaded => _session != null;

    public VoiceActivityDetector(IOpenClawLogger logger)
    {
        _logger = logger;
        _stateSize = 2 * 1 * 128;
        _state = new float[_stateSize];
    }

    /// <summary>Load the Silero VAD ONNX model from disk.</summary>
    public void LoadModel(string modelPath)
    {
        if (!System.IO.File.Exists(modelPath))
            throw new System.IO.FileNotFoundException($"VAD model not found: {modelPath}");

        var opts = new SessionOptions
        {
            InterOpNumThreads = 1,
            IntraOpNumThreads = 1,
            EnableCpuMemArena = true
        };
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

        _session?.Dispose();
        _session = new InferenceSession(modelPath, opts);
        ResetState();
        _logger.Info($"Silero VAD model loaded: {modelPath}");
    }

    /// <summary>Reset the internal RNN state (call between utterances).</summary>
    public void ResetState()
    {
        Array.Clear(_state, 0, _state.Length);
    }

    /// <summary>
    /// Process a single chunk of audio and return the speech probability (0.0–1.0).
    /// Input must be exactly <see cref="ChunkSamples"/> float samples at 16 kHz.
    /// </summary>
    public float ProcessChunk(float[] audioChunk)
    {
        if (_session == null)
            throw new InvalidOperationException("VAD model not loaded. Call LoadModel first.");

        if (audioChunk.Length != ChunkSamples)
            throw new ArgumentException($"Audio chunk must be exactly {ChunkSamples} samples, got {audioChunk.Length}");

        // Build input tensors matching Silero VAD v5 expected shapes.
        // See: github.com/snakers4/silero-vad/blob/master/examples/csharp/SileroVadOnnxModel.cs
        var inputTensor = new DenseTensor<float>(audioChunk, new[] { 1, ChunkSamples });
        var srTensor = new DenseTensor<long>(new long[] { SampleRate }, new[] { 1 });
        var stateTensor = new DenseTensor<float>(_state, new[] { 2, 1, 128 });

        using var results = _session.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor),
            NamedOnnxValue.CreateFromTensor("sr", srTensor),
            NamedOnnxValue.CreateFromTensor("state", stateTensor)
        });

        float probability = 0f;
        foreach (var result in results)
        {
            if (result.Name == "output")
            {
                var tensor = result.AsTensor<float>();
                probability = tensor.Length > 0 ? tensor.GetValue(0) : 0f;
            }
            else if (result.Name == "stateN")
            {
                var newState = result.AsTensor<float>();
                for (int i = 0; i < _stateSize && i < newState.Length; i++)
                    _state[i] = newState.GetValue(i);
            }
        }

        return probability;
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}
