using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Capabilities;

public class VoiceCapability : NodeCapabilityBase
{
    private const string LegacySkipCommand = "voice.skip";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public override string Category => "voice";

    public override IReadOnlyList<string> Commands => VoiceCommands.All;

    public event Func<Task<VoiceAudioDeviceInfo[]>>? ListDevicesRequested;
    public event Func<Task<VoiceSettings>>? SettingsRequested;
    public event Func<VoiceSettingsUpdateArgs, Task<VoiceSettings>>? SettingsUpdateRequested;
    public event Func<Task<VoiceStatusInfo>>? StatusRequested;
    public event Func<VoiceStartArgs, Task<VoiceStatusInfo>>? StartRequested;
    public event Func<VoiceStopArgs, Task<VoiceStatusInfo>>? StopRequested;
    public event Func<VoicePauseArgs, Task<VoiceStatusInfo>>? PauseRequested;
    public event Func<VoiceResumeArgs, Task<VoiceStatusInfo>>? ResumeRequested;
    public event Func<VoiceSkipArgs, Task<VoiceStatusInfo>>? SkipRequested;

    public VoiceCapability(IOpenClawLogger logger) : base(logger)
    {
    }

    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            VoiceCommands.ListDevices => await HandleListDevicesAsync(),
            VoiceCommands.GetSettings => await HandleGetSettingsAsync(),
            VoiceCommands.SetSettings => await HandleSetSettingsAsync(request),
            VoiceCommands.GetStatus => await HandleGetStatusAsync(),
            VoiceCommands.Start => await HandleStartAsync(request),
            VoiceCommands.Stop => await HandleStopAsync(request),
            VoiceCommands.Pause => await HandlePauseAsync(request),
            VoiceCommands.Resume => await HandleResumeAsync(request),
            VoiceCommands.Skip or LegacySkipCommand => await HandleSkipAsync(request),
            _ => Error($"Unknown command: {request.Command}")
        };
    }

    private async Task<NodeInvokeResponse> HandleListDevicesAsync()
    {
        Logger.Info(VoiceCommands.ListDevices);

        if (ListDevicesRequested == null)
            return Error("Voice device enumeration not available");

        try
        {
            return Success(await ListDevicesRequested());
        }
        catch (Exception ex)
        {
            Logger.Error("Voice device enumeration failed", ex);
            return Error($"Device enumeration failed: {ex.Message}");
        }
    }

    private async Task<NodeInvokeResponse> HandleGetSettingsAsync()
    {
        Logger.Info(VoiceCommands.GetSettings);

        if (SettingsRequested == null)
            return Error("Voice settings not available");

        try
        {
            return Success(await SettingsRequested());
        }
        catch (Exception ex)
        {
            Logger.Error("Voice settings get failed", ex);
            return Error($"Get settings failed: {ex.Message}");
        }
    }

    private async Task<NodeInvokeResponse> HandleSetSettingsAsync(NodeInvokeRequest request)
    {
        Logger.Info(VoiceCommands.SetSettings);

        if (SettingsUpdateRequested == null)
            return Error("Voice settings update not available");

        try
        {
            var rawArgs = request.Args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? "{}"
                : request.Args.GetRawText();
            VoiceSettingsUpdateArgs? update = null;
            if (request.Args.ValueKind == JsonValueKind.Object &&
                request.Args.TryGetProperty("update", out var updateEl))
            {
                update = JsonSerializer.Deserialize<VoiceSettingsUpdateArgs>(updateEl.GetRawText(), s_jsonOptions);
            }

            update ??= JsonSerializer.Deserialize<VoiceSettingsUpdateArgs>(rawArgs, s_jsonOptions);

            if (update == null)
                return Error("Missing update payload");

            return Success(await SettingsUpdateRequested(update));
        }
        catch (Exception ex)
        {
            Logger.Error("Voice settings update failed", ex);
            return Error($"Set settings failed: {ex.Message}");
        }
    }

    private async Task<NodeInvokeResponse> HandleGetStatusAsync()
    {
        Logger.Info(VoiceCommands.GetStatus);

        if (StatusRequested == null)
            return Error("Voice status not available");

        try
        {
            return Success(await StatusRequested());
        }
        catch (Exception ex)
        {
            Logger.Error("Voice status get failed", ex);
            return Error($"Get status failed: {ex.Message}");
        }
    }

    private async Task<NodeInvokeResponse> HandleStartAsync(NodeInvokeRequest request)
    {
        Logger.Info(VoiceCommands.Start);

        if (StartRequested == null)
            return Error("Voice start not available");

        try
        {
            var rawArgs = request.Args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? "{}"
                : request.Args.GetRawText();
            var args = JsonSerializer.Deserialize<VoiceStartArgs>(rawArgs, s_jsonOptions) ?? new VoiceStartArgs();
            return Success(await StartRequested(args));
        }
        catch (Exception ex)
        {
            Logger.Error("Voice start failed", ex);
            return Error($"Start failed: {ex.Message}");
        }
    }

    private async Task<NodeInvokeResponse> HandleStopAsync(NodeInvokeRequest request)
    {
        Logger.Info(VoiceCommands.Stop);

        if (StopRequested == null)
            return Error("Voice stop not available");

        try
        {
            var rawArgs = request.Args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? "{}"
                : request.Args.GetRawText();
            var args = JsonSerializer.Deserialize<VoiceStopArgs>(rawArgs, s_jsonOptions) ?? new VoiceStopArgs();
            return Success(await StopRequested(args));
        }
        catch (Exception ex)
        {
            Logger.Error("Voice stop failed", ex);
            return Error($"Stop failed: {ex.Message}");
        }
    }

    private async Task<NodeInvokeResponse> HandlePauseAsync(NodeInvokeRequest request)
    {
        Logger.Info(VoiceCommands.Pause);

        if (PauseRequested == null)
            return Error("Voice pause not available");

        try
        {
            var rawArgs = request.Args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? "{}"
                : request.Args.GetRawText();
            var args = JsonSerializer.Deserialize<VoicePauseArgs>(rawArgs, s_jsonOptions) ?? new VoicePauseArgs();
            return Success(await PauseRequested(args));
        }
        catch (Exception ex)
        {
            Logger.Error("Voice pause failed", ex);
            return Error($"Pause failed: {ex.Message}");
        }
    }

    private async Task<NodeInvokeResponse> HandleResumeAsync(NodeInvokeRequest request)
    {
        Logger.Info(VoiceCommands.Resume);

        if (ResumeRequested == null)
            return Error("Voice resume not available");

        try
        {
            var rawArgs = request.Args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? "{}"
                : request.Args.GetRawText();
            var args = JsonSerializer.Deserialize<VoiceResumeArgs>(rawArgs, s_jsonOptions) ?? new VoiceResumeArgs();
            return Success(await ResumeRequested(args));
        }
        catch (Exception ex)
        {
            Logger.Error("Voice resume failed", ex);
            return Error($"Resume failed: {ex.Message}");
        }
    }

    private async Task<NodeInvokeResponse> HandleSkipAsync(NodeInvokeRequest request)
    {
        Logger.Info(VoiceCommands.Skip);

        if (SkipRequested == null)
            return Error("Voice skip not available");

        try
        {
            var rawArgs = request.Args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? "{}"
                : request.Args.GetRawText();
            var args = JsonSerializer.Deserialize<VoiceSkipArgs>(rawArgs, s_jsonOptions) ?? new VoiceSkipArgs();
            return Success(await SkipRequested(args));
        }
        catch (Exception ex)
        {
            Logger.Error("Voice skip failed", ex);
            return Error($"Skip failed: {ex.Message}");
        }
    }
}
