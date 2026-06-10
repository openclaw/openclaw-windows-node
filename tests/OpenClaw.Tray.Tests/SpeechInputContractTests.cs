namespace OpenClaw.Tray.Tests;

public sealed class SpeechInputContractTests
{
    private static string RepoRoot()
    {
        var env = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "openclaw-windows-node.slnx")) &&
                Directory.Exists(Path.Combine(d.FullName, "src")))
                return d.FullName;
            d = d.Parent;
        }

        throw new InvalidOperationException(
            "Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

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
