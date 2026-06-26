namespace OpenClaw.Tray.Tests;

public sealed class SpeechInputContractTests
{
    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { TestRepositoryPaths.GetRepositoryRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void VoiceService_DoesNotLoadNativeVad_OnRecordStartup()
    {
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Services", "VoiceService.cs");

        Assert.DoesNotContain("new VoiceActivityDetector", cs);
        Assert.DoesNotContain(".LoadModel(vad", cs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DownloadVadModelAsync", cs);
    }

    [Fact]
    public void AudioPipeline_UsesManagedEnergyVad_NotNativeVad()
    {
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Services", "AudioPipeline.cs");

        Assert.DoesNotContain("VoiceActivityDetector", cs);
        Assert.Contains("energy >= startThreshold", cs);
        Assert.Contains("energy >= stayThreshold", cs);
    }
}
