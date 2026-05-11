using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Capabilities;

/// <summary>
/// Screen capture capability using Windows.Graphics.Capture
/// </summary>
public class ScreenCapability : NodeCapabilityBase
{
    public override string Category => "screen";
    
    private static readonly string[] _commands = new[]
    {
        "screen.snapshot",
        "screen.record"
    };
    
    public override IReadOnlyList<string> Commands => _commands;
    
    // Events for UI/platform-specific implementation
    public event Func<ScreenCaptureArgs, Task<ScreenCaptureResult>>? CaptureRequested;
    public event Func<ScreenRecordArgs, Task<ScreenRecordResult>>? RecordRequested;
    
    public ScreenCapability(IOpenClawLogger logger) : base(logger)
    {
    }
    
    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            "screen.snapshot" => await HandleCaptureAsync(request),
            "screen.record" => await HandleRecordAsync(request),
            _ => Error($"Unknown command: {request.Command}")
        };
    }
    
    // Clamp bounds — reject extreme caller values before any work starts.
    private const int MinDimension = 16;
    private const int MaxScreenWidth = 7680;       // 8K horizontal
    private const int MinQuality = 1;
    private const int MaxQuality = 100;
    private const int MaxScreenIndex = 32;          // far above any plausible monitor count

    private async Task<NodeInvokeResponse> HandleCaptureAsync(NodeInvokeRequest request)
    {
        var format = GetStringArg(request.Args, "format", "png");
        var maxWidth = Clamp(GetIntArg(request.Args, "maxWidth", 1920), MinDimension, MaxScreenWidth);
        var quality = Clamp(GetIntArg(request.Args, "quality", 80), MinQuality, MaxQuality);
        var monitor = GetIntArg(request.Args, "monitor", 0);
        var screenIndex = Clamp(GetIntArg(request.Args, "screenIndex", monitor), 0, MaxScreenIndex);
        var includePointer = GetBoolArg(request.Args, "includePointer", true);

        Logger.Info($"screen.snapshot: format={format}, maxWidth={maxWidth}, monitor={screenIndex}");
        
        if (CaptureRequested == null)
        {
            return Error("Screen capture not available");
        }
        
        try
        {
            var result = await CaptureRequested(new ScreenCaptureArgs
            {
                Format = format ?? "png",
                MaxWidth = maxWidth,
                Quality = quality,
                MonitorIndex = screenIndex,
                IncludePointer = includePointer
            });
            
            var image = $"data:image/{result.Format.ToLowerInvariant()};base64,{result.Base64}";
            return Success(new 
            { 
                format = result.Format,
                width = result.Width,
                height = result.Height,
                base64 = result.Base64,
                image
            });
        }
        catch (Exception ex)
        {
            Logger.Error("Screen capture failed", ex);
            return Error("Capture failed");
        }
    }

    private async Task<NodeInvokeResponse> HandleRecordAsync(NodeInvokeRequest request)
    {
        var format = GetStringArg(request.Args, "format", "mp4");
        if (!string.IsNullOrWhiteSpace(format) &&
            !string.Equals(format, "mp4", StringComparison.OrdinalIgnoreCase))
        {
            return Error("Unsupported screen recording format. Only mp4 is supported.");
        }

        var durationMs = Clamp(GetIntArg(request.Args, "durationMs", 10000), 100, MaxRecordDurationMs);
        var fpsRaw = GetDoubleArg(request.Args, "fps", 10);
        var fps = fpsRaw < 1 ? 1 : (fpsRaw > 60 ? 60 : fpsRaw);
        var screenIndex = Clamp(GetIntArg(request.Args, "screenIndex", 0), 0, MaxScreenIndex);
        var includeAudio = GetBoolArg(request.Args, "includeAudio", false);

        Logger.Info($"screen.record: durationMs={durationMs}, fps={fps}, screenIndex={screenIndex}, includeAudio={includeAudio}");

        if (RecordRequested == null)
        {
            return Error("Screen recording not available");
        }

        try
        {
            var result = await RecordRequested(new ScreenRecordArgs
            {
                DurationMs = durationMs,
                Fps = fps,
                ScreenIndex = screenIndex,
                Format = "mp4",
                IncludeAudio = includeAudio
            });

            return Success(new
            {
                format = result.Format,
                base64 = result.Base64,
                durationMs = result.DurationMs,
                fps = result.Fps,
                screenIndex = result.ScreenIndex,
                hasAudio = result.HasAudio
            });
        }
        catch (Exception ex)
        {
            Logger.Error("Screen recording failed", ex);
            return Error("Recording failed");
        }
    }

    private const int MaxRecordDurationMs = 5 * 60 * 1000; // 5 minutes

    private static int Clamp(int value, int min, int max)
        => value < min ? min : (value > max ? max : value);

    private static double GetDoubleArg(System.Text.Json.JsonElement args, string name, double defaultValue)
    {
        if (args.ValueKind == System.Text.Json.JsonValueKind.Undefined ||
            args.ValueKind == System.Text.Json.JsonValueKind.Null)
            return defaultValue;

        if (args.TryGetProperty(name, out var prop) && prop.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            try { return prop.GetDouble(); }
            catch (FormatException) { return defaultValue; }
        }

        return defaultValue;
    }
}

public class ScreenCaptureArgs
{
    public string Format { get; set; } = "png";
    public int MaxWidth { get; set; } = 1920;
    public int Quality { get; set; } = 80;
    public int MonitorIndex { get; set; } = 0;
    public bool IncludePointer { get; set; } = true;
}

public class ScreenCaptureResult
{
    public string Format { get; set; } = "png";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Base64 { get; set; } = "";
}

public class ScreenRecordArgs
{
    public string Format { get; set; } = "mp4";
    public int DurationMs { get; set; } = 10000;
    public double Fps { get; set; } = 10;
    public int ScreenIndex { get; set; }
    public bool IncludeAudio { get; set; }
}

public class ScreenRecordResult
{
    public string Format { get; set; } = "mp4";
    public string Base64 { get; set; } = "";
    public int DurationMs { get; set; }
    public double Fps { get; set; }
    public int ScreenIndex { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool HasAudio { get; set; }
}

