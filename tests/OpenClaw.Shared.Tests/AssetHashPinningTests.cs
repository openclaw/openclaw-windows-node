using System.Text.RegularExpressions;
using OpenClaw.Shared.Audio;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Pre-GA security guard. Every shipped Whisper model and Piper voice MUST
/// have a pinned SHA-256 hash so the runtime can refuse tampered downloads.
/// New entries that forget the hash will fail this test loudly instead of
/// quietly being installable from a compromised source.
///
/// See WhisperModelManager.AvailableModels / PiperVoiceManager.AvailableVoices
/// and Audio_FollowUps.md §2.
/// </summary>
public class AssetHashPinningTests
{
    private static readonly Regex Sha256Hex = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    [Fact]
    public void EveryWhisperModel_HasPinnedSha256()
    {
        Assert.NotEmpty(WhisperModelManager.AvailableModels);
        foreach (var m in WhisperModelManager.AvailableModels)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Sha256),
                $"Whisper model '{m.Name}' is missing a pinned SHA-256 hash. Add one to AvailableModels.");
            Assert.Matches(Sha256Hex, m.Sha256!);
        }
    }

    [Fact]
    public void EveryPiperVoice_HasPinnedSha256()
    {
        Assert.NotEmpty(PiperVoiceManager.AvailableVoices);
        foreach (var v in PiperVoiceManager.AvailableVoices)
        {
            Assert.False(string.IsNullOrWhiteSpace(v.Sha256),
                $"Piper voice '{v.VoiceId}' is missing a pinned SHA-256 hash. Add one to AvailableVoices.");
            Assert.Matches(Sha256Hex, v.Sha256!);
        }
    }

    [Fact]
    public void EveryWhisperModel_UsesHttpsDownloadUrl()
    {
        foreach (var m in WhisperModelManager.AvailableModels)
        {
            Assert.StartsWith("https://", m.DownloadUrl);
        }
    }

    [Fact]
    public void EveryPiperVoice_UsesHttpsDownloadUrl()
    {
        foreach (var v in PiperVoiceManager.AvailableVoices)
        {
            Assert.StartsWith("https://", v.DownloadUrl);
        }
    }

    [Fact]
    public void SileroVadModel_HasPinnedSha256()
    {
        Assert.False(string.IsNullOrWhiteSpace(SileroVadModelManifest.Sha256),
            "Silero VAD model is missing a pinned SHA-256 hash. Add one to SileroVadModelManifest.");
        Assert.Matches(Sha256Hex, SileroVadModelManifest.Sha256);
        Assert.StartsWith("https://", SileroVadModelManifest.DownloadUrl);
    }
}
