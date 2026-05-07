namespace OpenClaw.Shared.Audio;

/// <summary>
/// Pinned descriptor for the Silero VAD ONNX model that the audio
/// pipeline auto-downloads on first use.
///
/// SECURITY — same fail-closed verification discipline as
/// <see cref="WhisperModelManager"/> and <see cref="PiperVoiceManager"/>:
/// the runtime checks the downloaded file's SHA-256 against
/// <see cref="Sha256"/> before installing it. The pinned hash here was
/// captured against the upstream raw URL on 2026-05-05; re-verify from
/// an independent source before any public release (Audio_FollowUps.md
/// §2 captures the broader signed-manifest plan).
/// </summary>
public static class SileroVadModelManifest
{
    public const string FileName = "silero_vad.onnx";

    public const string DownloadUrl =
        "https://github.com/snakers4/silero-vad/raw/master/src/silero_vad/data/silero_vad.onnx";

    /// <summary>Lowercase hex SHA-256 of the canonical upstream file.</summary>
    public const string Sha256 = "1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3";

    /// <summary>Approximate compressed size in bytes (UI hint; actual size
    /// is asserted via the SHA-256 check).</summary>
    public const long ApproximateSizeBytes = 2_327_524;
}
