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

    [Fact]
    public void ChatVoiceDialogs_RouteDisabledCapabilityToPermissions_AndMissingModelToVoiceSettings()
    {
        var chatPage = Read("src", "OpenClaw.Tray.WinUI", "Pages", "ChatPage.xaml.cs");
        var chatWindow = Read("src", "OpenClaw.Tray.WinUI", "Windows", "ChatWindow.xaml.cs");
        var resources = Read("src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw");

        Assert.Contains("ChatVoiceDialog_OpenPermissionsSettings", chatPage);
        Assert.Contains("NavigateToPermissionsSettings", chatPage);
        Assert.Contains("_hub.NavigateTo(\"permissions\")", chatPage);
        Assert.Contains("ChatVoiceDialog_OpenVoiceSettings", chatPage);
        Assert.Contains("NavigateToVoiceSettings", chatPage);
        Assert.Contains("_hub.NavigateTo(\"voice\")", chatPage);

        Assert.Contains("ChatVoiceDialog_OpenPermissionsSettings", chatWindow);
        Assert.Contains("ShowHub(\"permissions\")", chatWindow);
        Assert.Contains("ChatVoiceDialog_OpenVoiceSettings", chatWindow);
        Assert.Contains("ShowHub(\"voice\")", chatWindow);

        Assert.Contains("Speech-to-text is disabled. Enable the capability in Permissions", resources);
        Assert.Contains("ChatVoiceDialog_OpenPermissionsSettings", resources);
        Assert.Contains("Open Permissions", resources);
        Assert.Contains("Speech-to-Text is on, but voice input will not work until at least one speech model is downloaded.", resources);
    }
}
