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
    public void ChatVoiceDialogs_RouteDisabledSttCapabilityToPermissions_AndMissingModelToVoiceSettings()
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

    [Fact]
    public void ChatVoiceDialogs_RouteDisabledTtsCapabilityToPermissions_AndPreserveFallbackForMissingSetup()
    {
        var chatPage = Read("src", "OpenClaw.Tray.WinUI", "Pages", "ChatPage.xaml.cs");
        var chatWindow = Read("src", "OpenClaw.Tray.WinUI", "Windows", "ChatWindow.xaml.cs");
        var permissionsPage = Read("src", "OpenClaw.Tray.WinUI", "Pages", "PermissionsPage.xaml.cs");
        var resources = Read("src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw");

        Assert.Contains("ReadChatTextAloudAsync", chatPage);
        Assert.Contains("OnSpeakerMuteChangedAsync", chatPage);
        Assert.Contains("EnsureTtsReadyForChatAsync", chatPage);
        Assert.Contains("ShowTtsUnavailableDialogAsync", chatPage);
        Assert.Contains("IsAutomaticChatTtsEnabled", chatPage);
        Assert.Contains("IsChatTtsPlaybackReady", chatPage);
        Assert.Contains("_speakerMuteGate.WaitAsync(0)", chatPage);
        Assert.Contains("_voiceSettingsDialogOpen", chatPage);
        Assert.Contains("_functionalHost?.SetSpeakerMuted(true);\r\n            await ShowTtsUnavailableDialogAsync();", chatPage);
        Assert.Contains("ChatVoiceDialog_OutputOffTitle", chatPage);
        Assert.Contains("ChatVoiceDialog_OutputOffMessage", chatPage);
        Assert.Contains("NavigateToPermissionsSettings", chatPage);
        Assert.DoesNotContain("ChatVoiceDialog_TtsSetupRequired", chatPage);

        Assert.Contains("ReadChatTextAloudAsync", chatWindow);
        Assert.Contains("OnSpeakerMuteChangedAsync", chatWindow);
        Assert.Contains("EnsureTtsReadyForChatAsync", chatWindow);
        Assert.Contains("ShowTtsUnavailableDialogAsync", chatWindow);
        Assert.Contains("IsAutomaticChatTtsEnabled", chatWindow);
        Assert.Contains("IsChatTtsPlaybackReady", chatWindow);
        Assert.Contains("_speakerMuteGate.WaitAsync(0)", chatWindow);
        Assert.Contains("_voiceSettingsDialogOpen", chatWindow);
        Assert.Contains("_functionalHost?.SetSpeakerMuted(true);\r\n            await ShowTtsUnavailableDialogAsync();", chatWindow);
        Assert.Contains("ChatVoiceDialog_OutputOffTitle", chatWindow);
        Assert.Contains("ChatVoiceDialog_OutputOffMessage", chatWindow);
        Assert.Contains("ShowHub(\"permissions\")", chatWindow);
        Assert.DoesNotContain("ChatVoiceDialog_TtsSetupRequired", chatWindow);

        Assert.Contains("SpeechSetupReadiness.IsConfiguredTtsProviderSetupRequired", permissionsPage);
        Assert.Contains("SpeechSetupReadiness.IsAutomaticChatTtsEnabled", Read("src", "OpenClaw.Tray.WinUI", "App.xaml.cs"));
        Assert.Contains("IsAutomaticChatTtsEnabled", Read("src", "OpenClaw.Tray.WinUI", "Services", "SpeechSetupReadiness.cs"));
        Assert.Contains("return settings?.NodeTtsEnabled == true;", Read("src", "OpenClaw.Tray.WinUI", "Services", "SpeechSetupReadiness.cs"));
        Assert.Contains("Text-to-speech is disabled. Enable the capability in Permissions", resources);
        Assert.DoesNotContain("ChatVoiceDialog_TtsSetupRequired", resources);
    }
}
