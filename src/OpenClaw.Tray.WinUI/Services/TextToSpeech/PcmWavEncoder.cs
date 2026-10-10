using System.IO;

namespace OpenClawTray.Services;

/// <summary>WAV encoding for local neural TTS engines (Piper, Kokoro) that return float PCM.</summary>
internal static class PcmWavEncoder
{
    /// <summary>
    /// Convert 32-bit float PCM samples (range -1..1) to a standard 16-bit
    /// PCM mono WAV blob the WinUI MediaPlayer can play.
    /// </summary>
    internal static byte[] EncodeMono16(float[] samples, int sampleRate)
    {
        const int bitsPerSample = 16;
        const int channels = 1;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = channels * bitsPerSample / 8;
        var dataSize = samples.Length * sizeof(short);

        using var ms = new MemoryStream(44 + dataSize);
        using var w = new BinaryWriter(ms);
        // RIFF header
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataSize);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        // fmt chunk
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);  // PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write((short)bitsPerSample);
        // data chunk
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(dataSize);
        // 16-bit PCM: clamp + scale.
        for (int i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
            w.Write((short)(s * short.MaxValue));
        }
        w.Flush();
        return ms.ToArray();
    }
}
