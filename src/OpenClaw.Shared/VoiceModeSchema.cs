using System;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared;

public static class VoiceCommands
{
    public const string ListDevices = "voice.devices.list";
    public const string GetSettings = "voice.settings.get";
    public const string SetSettings = "voice.settings.set";
    public const string GetStatus = "voice.status.get";
    public const string Start = "voice.start";
    public const string Stop = "voice.stop";
    public const string Pause = "voice.pause";
    public const string Resume = "voice.resume";
    public const string Skip = "voice.response.skip";

    private static readonly ReadOnlyCollection<string> s_all = Array.AsReadOnly(
    [
        ListDevices,
        GetSettings,
        SetSettings,
        GetStatus,
        Start,
        Stop,
        Pause,
        Resume,
        Skip
    ]);

    public static IReadOnlyList<string> All => s_all;
}

[JsonConverter(typeof(VoiceActivationModeJsonConverter))]
public enum VoiceActivationMode
{
    Off,
    VoiceWake,
    TalkMode
}

[JsonConverter(typeof(VoiceRuntimeStateJsonConverter))]
public enum VoiceRuntimeState
{
    Stopped,
    Paused,
    Idle,
    Arming,
    ListeningForVoiceWake,
    ListeningContinuously,
    RecordingUtterance,
    SubmittingAudio,
    AwaitingResponse,
    PlayingResponse,
    Error
}

public sealed class VoiceSettings
{
    public VoiceActivationMode Mode { get; set; } = VoiceActivationMode.Off;
    public bool Enabled { get; set; }
    public bool ShowRepeaterAtStartup { get; set; } = true;
    public bool ShowConversationToasts { get; set; }
    public string SpeechToTextProviderId { get; set; } = VoiceProviderIds.Windows;
    public string TextToSpeechProviderId { get; set; } = VoiceProviderIds.Windows;
    public string? InputDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    public int SampleRateHz { get; set; } = 16000;
    public int CaptureChunkMs { get; set; } = 80;
    public bool BargeInEnabled { get; set; } = true;
    public VoiceWakeSettings VoiceWake { get; set; } = new();
    public TalkModeSettings TalkMode { get; set; } = new();
}

public sealed class VoiceWakeSettings
{
    public string Engine { get; set; } = "NanoWakeWord";
    public string ModelId { get; set; } = "hey_openclaw";
    public float TriggerThreshold { get; set; } = 0.65f;
    public int TriggerCooldownMs { get; set; } = 2000;
    public int PreRollMs { get; set; } = 1200;
    public int EndSilenceMs { get; set; } = 900;
}

public sealed class TalkModeSettings
{
    public int MinSpeechMs { get; set; } = 250;
    public int EndSilenceMs { get; set; } = 900;
    public int MaxUtteranceMs { get; set; } = 15000;
}

public sealed class VoiceAudioDeviceInfo
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
    public bool IsInput { get; set; }
    public bool IsOutput { get; set; }
}

public sealed class VoiceStatusInfo
{
    public bool Available { get; set; }
    public bool Running { get; set; }
    public VoiceActivationMode Mode { get; set; } = VoiceActivationMode.Off;
    public VoiceRuntimeState State { get; set; } = VoiceRuntimeState.Stopped;
    public string? SessionKey { get; set; }
    public string? InputDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    public string? VoiceWakeModelId { get; set; }
    public bool VoiceWakeLoaded { get; set; }
    public DateTime? LastVoiceWakeUtc { get; set; }
    public DateTime? LastUtteranceUtc { get; set; }
    public int PendingReplyCount { get; set; }
    public bool CanSkipReply { get; set; }
    public string? CurrentReplyPreview { get; set; }
    public string? LastError { get; set; }
}

public sealed class VoiceStartArgs
{
    public VoiceActivationMode? Mode { get; set; }
    public string? SessionKey { get; set; }
}

public sealed class VoiceStopArgs
{
    public string? Reason { get; set; }
}

public sealed class VoicePauseArgs
{
    public string? Reason { get; set; }
}

public sealed class VoiceResumeArgs
{
    public string? Reason { get; set; }
}

public sealed class VoiceSkipArgs
{
    public string? Reason { get; set; }
}

public sealed class VoiceSettingsUpdateArgs
{
    public VoiceSettings Settings { get; set; } = new();
    public bool Persist { get; set; } = true;
}

public static class VoiceProviderIds
{
    public const string Windows = "windows";
    public const string HttpWs = "http-ws";
    public const string FoundryLocal = "foundry-local";
    public const string OpenAiWhisper = "openai-whisper";
    public const string ElevenLabsSpeechToText = "elevenlabs-stt";
    public const string AzureAiSpeech = "azure-ai-speech";
    public const string SherpaOnnx = "sherpa-onnx";
    public const string MiniMax = "minimax";
    public const string ElevenLabs = "elevenlabs";
}

public static class VoiceProviderRuntimeIds
{
    public const string Windows = "windows";
    public const string Streaming = "streaming";
    public const string Embedded = "embedded";
    public const string Cloud = "cloud";
}

public static class VoiceProviderSettingKeys
{
    public const string ApiKey = "apiKey";
    public const string Endpoint = "endpoint";
    public const string Model = "model";
    public const string ModelPath = "modelPath";
    public const string VoiceId = "voiceId";
    public const string VoiceSettingsJson = "voiceSettingsJson";
}

public static class VoiceTextToSpeechResponseModes
{
    public const string Binary = "binary";
    public const string HexJsonString = "hexJsonString";
    public const string Base64JsonString = "base64JsonString";
}

public sealed class VoiceProviderCredentials
{
    public string? MiniMaxApiKey { get; set; }
    public string MiniMaxModel { get; set; } = "speech-2.8-turbo";
    public string MiniMaxVoiceId { get; set; } = "English_MatureBoss";
    public string? ElevenLabsApiKey { get; set; }
    public string? ElevenLabsModel { get; set; }
    public string? ElevenLabsVoiceId { get; set; }
}

public sealed class VoiceProviderConfigurationStore
{
    public List<VoiceProviderConfiguration> Providers { get; set; } = [];
}

public sealed class VoiceProviderConfiguration
{
    public string ProviderId { get; set; } = "";
    public Dictionary<string, string> Values { get; set; } = [];
}

public sealed class VoiceProviderSettingDefinition
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Secret { get; set; }
    public bool Required { get; set; } = true;
    public bool JsonValue { get; set; }
    public string? DefaultValue { get; set; }
    public string? Placeholder { get; set; }
    public string? Description { get; set; }
    public List<string> Options { get; set; } = [];
}

public sealed class VoiceTextToSpeechHttpContract
{
    public string EndpointTemplate { get; set; } = "";
    public string HttpMethod { get; set; } = "POST";
    public string AuthenticationHeaderName { get; set; } = "Authorization";
    public string? AuthenticationScheme { get; set; } = "Bearer";
    public string ApiKeySettingKey { get; set; } = VoiceProviderSettingKeys.ApiKey;
    public string RequestContentType { get; set; } = "application/json";
    public string RequestBodyTemplate { get; set; } = "";
    public string ResponseAudioMode { get; set; } = VoiceTextToSpeechResponseModes.Binary;
    public string? ResponseAudioJsonPath { get; set; }
    public string? ResponseStatusCodeJsonPath { get; set; }
    public string? ResponseStatusMessageJsonPath { get; set; }
    public string? SuccessStatusValue { get; set; }
    public string OutputContentType { get; set; } = "audio/mpeg";
}

public sealed class VoiceTextToSpeechWebSocketContract
{
    public string EndpointTemplate { get; set; } = "";
    public string AuthenticationHeaderName { get; set; } = "Authorization";
    public string? AuthenticationScheme { get; set; } = "Bearer";
    public string ApiKeySettingKey { get; set; } = VoiceProviderSettingKeys.ApiKey;
    public string ConnectSuccessEventName { get; set; } = "connected_success";
    public string StartMessageTemplate { get; set; } = "";
    public string StartSuccessEventName { get; set; } = "task_started";
    public string ContinueMessageTemplate { get; set; } = "";
    public string FinishMessageTemplate { get; set; } = "{ \"event\": \"task_finish\" }";
    public string ResponseAudioMode { get; set; } = VoiceTextToSpeechResponseModes.Binary;
    public string? ResponseAudioJsonPath { get; set; } = "data.audio";
    public string? ResponseStatusCodeJsonPath { get; set; } = "base_resp.status_code";
    public string? ResponseStatusMessageJsonPath { get; set; } = "base_resp.status_msg";
    public string? FinalFlagJsonPath { get; set; } = "is_final";
    public string TaskFailedEventName { get; set; } = "task_failed";
    public string? SuccessStatusValue { get; set; } = "0";
    public string OutputContentType { get; set; } = "audio/mpeg";
}

public sealed class VoiceProviderOption
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Runtime { get; set; } = VoiceProviderRuntimeIds.Windows;
    public bool Enabled { get; set; } = true;
    public bool VisibleInSettings { get; set; } = true;
    public bool Selectable { get; set; } = true;
    public string? Description { get; set; }
    public List<VoiceProviderSettingDefinition> Settings { get; set; } = [];
    public VoiceTextToSpeechHttpContract? TextToSpeechHttp { get; set; }
    public VoiceTextToSpeechWebSocketContract? TextToSpeechWebSocket { get; set; }

    [JsonIgnore]
    public string DisplayName => Selectable ? Name : $"{Name} (coming soon)";

    [JsonIgnore]
    public double DisplayOpacity => Selectable ? 1.0 : 0.55;
}

public sealed class VoiceProviderCatalog
{
    public List<VoiceProviderOption> SpeechToTextProviders { get; set; } = [];
    public List<VoiceProviderOption> TextToSpeechProviders { get; set; } = [];
}

public sealed class VoiceActivationModeJsonConverter : JsonConverter<VoiceActivationMode>
{
    public override VoiceActivationMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value switch
        {
            "VoiceWake" or "WakeWord" => VoiceActivationMode.VoiceWake,
            "TalkMode" or "AlwaysOn" => VoiceActivationMode.TalkMode,
            _ => VoiceActivationMode.Off
        };
    }

    public override void Write(Utf8JsonWriter writer, VoiceActivationMode value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            VoiceActivationMode.VoiceWake => "VoiceWake",
            VoiceActivationMode.TalkMode => "TalkMode",
            _ => "Off"
        });
    }
}

public sealed class VoiceRuntimeStateJsonConverter : JsonConverter<VoiceRuntimeState>
{
    public override VoiceRuntimeState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value switch
        {
            "ListeningForVoiceWake" or "ListeningForWakeWord" => VoiceRuntimeState.ListeningForVoiceWake,
            "Stopped" => VoiceRuntimeState.Stopped,
            "Paused" => VoiceRuntimeState.Paused,
            "Idle" => VoiceRuntimeState.Idle,
            "Arming" => VoiceRuntimeState.Arming,
            "ListeningContinuously" => VoiceRuntimeState.ListeningContinuously,
            "RecordingUtterance" => VoiceRuntimeState.RecordingUtterance,
            "SubmittingAudio" => VoiceRuntimeState.SubmittingAudio,
            "AwaitingResponse" => VoiceRuntimeState.AwaitingResponse,
            "PlayingResponse" => VoiceRuntimeState.PlayingResponse,
            "Error" => VoiceRuntimeState.Error,
            _ => VoiceRuntimeState.Stopped
        };
    }

    public override void Write(Utf8JsonWriter writer, VoiceRuntimeState value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            VoiceRuntimeState.ListeningForVoiceWake => "ListeningForVoiceWake",
            VoiceRuntimeState.Stopped => "Stopped",
            VoiceRuntimeState.Paused => "Paused",
            VoiceRuntimeState.Idle => "Idle",
            VoiceRuntimeState.Arming => "Arming",
            VoiceRuntimeState.ListeningContinuously => "ListeningContinuously",
            VoiceRuntimeState.RecordingUtterance => "RecordingUtterance",
            VoiceRuntimeState.SubmittingAudio => "SubmittingAudio",
            VoiceRuntimeState.AwaitingResponse => "AwaitingResponse",
            VoiceRuntimeState.PlayingResponse => "PlayingResponse",
            VoiceRuntimeState.Error => "Error",
            _ => "Stopped"
        });
    }
}
