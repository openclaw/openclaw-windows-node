using OpenClaw.Shared;
using System;
using System.Threading.Tasks;

namespace OpenClawTray.Services.Voice;

public interface IUiDispatcher
{
    bool TryEnqueue(Action callback);
}

public sealed class DispatcherQueueAdapter : IUiDispatcher
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;

    public DispatcherQueueAdapter(Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    public bool TryEnqueue(Action callback)
    {
        return _dispatcherQueue.TryEnqueue(() => callback());
    }
}

public interface IVoiceRuntime
{
    event EventHandler<VoiceConversationTurnEventArgs>? ConversationTurnAvailable;
    event EventHandler<VoiceTranscriptDraftEventArgs>? TranscriptDraftUpdated;
    Func<string, string?, Task<VoiceTranscriptSubmitOutcome>>? TranscriptSubmitter { get; set; }
    void NotifyManualTranscriptSubmitted(string text, string? sessionKey = null);
}

public interface IVoiceConfigurationApi
{
    Task<VoiceSettings> GetSettingsAsync();
    Task<VoiceSettings> UpdateSettingsAsync(VoiceSettingsUpdateArgs update);
    Task<VoiceAudioDeviceInfo[]> ListDevicesAsync();
    VoiceProviderCatalog GetProviderCatalog();
    VoiceProviderConfigurationStore GetProviderConfiguration();
    void SetProviderConfiguration(VoiceProviderConfigurationStore configurationStore);
}

public interface IVoiceRuntimeControlApi
{
    VoiceStatusInfo CurrentStatus { get; }
    Task<VoiceStatusInfo> GetStatusAsync();
    Task<VoiceStatusInfo> StartAsync(VoiceStartArgs args);
    Task<VoiceStatusInfo> StopAsync(VoiceStopArgs args);
    Task<VoiceStatusInfo> PauseAsync(VoicePauseArgs? args = null);
    Task<VoiceStatusInfo> ResumeAsync(VoiceResumeArgs? args = null);
    Task<VoiceStatusInfo> SkipCurrentReplyAsync(VoiceSkipArgs? args = null);
    Task<VoiceStatusInfo> ToggleQuickPauseAsync();
}

public interface IVoiceChatWindow
{
    bool IsClosed { get; }
    event EventHandler<VoiceTranscriptSubmittedEventArgs>? VoiceTranscriptSubmitted;
    Task UpdateVoiceTranscriptDraftAsync(string text, bool clear);
    Task<bool> TrySubmitVoiceTranscriptAsync(string text);
    Task<bool> PrepareVoiceTranscriptForManualSendAsync(string text);
}

public sealed class VoiceTranscriptSubmittedEventArgs : EventArgs
{
    public string Text { get; set; } = "";
    public string? SessionKey { get; set; }
}
