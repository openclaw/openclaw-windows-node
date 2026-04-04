namespace OpenClawTray.Services.Voice;

public static class VoiceCaptureMath
{
    private const float DefaultSignalThreshold = 0.015f;

    public static uint ResolveDesiredSamplesPerQuantum(int sampleRateHz, int chunkMs)
    {
        if (sampleRateHz <= 0)
        {
            sampleRateHz = 16000;
        }

        if (chunkMs <= 0)
        {
            chunkMs = 80;
        }

        var desired = (sampleRateHz * chunkMs) / 1000;
        return (uint)Math.Max(desired, 128);
    }

    public static bool HasAudibleSignal(float peakLevel, float threshold = DefaultSignalThreshold)
    {
        return peakLevel >= threshold;
    }

    public static float ComputePeakLevel(byte[] data)
    {
        if (data.Length < sizeof(float))
        {
            return 0f;
        }

        float peak = 0f;
        var alignedLength = data.Length - (data.Length % sizeof(float));
        for (var offset = 0; offset < alignedLength; offset += sizeof(float))
        {
            var sample = Math.Abs(BitConverter.ToSingle(data, offset));
            if (sample > peak)
            {
                peak = sample;
            }
        }

        return float.IsFinite(peak) ? peak : 0f;
    }
}
