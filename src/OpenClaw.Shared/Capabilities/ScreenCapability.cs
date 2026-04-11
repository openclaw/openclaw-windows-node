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
        "screen.capture",
        "screen.list",
        "screen.record",
        "screen.record.start",
        "screen.record.stop",
    };

    public override IReadOnlyList<string> Commands => _commands;

    // Events for UI/platform-specific implementation
    public event Func<ScreenCaptureArgs, Task<ScreenCaptureResult>>? CaptureRequested;
    public event Func<Task<ScreenInfo[]>>? ListRequested;
    public event Func<ScreenRecordArgs, Task<ScreenRecordResult>>? RecordRequested;
    public event Func<ScreenRecordStartArgs, Task<string>>? StartRequested;
    public event Func<string, Task<ScreenRecordResult>>? StopRequested;
    
    public ScreenCapability(IOpenClawLogger logger) : base(logger)
    {
    }
    
    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            "screen.capture"      => await HandleCaptureAsync(request),
            "screen.list"         => await HandleListAsync(request),
            "screen.record"       => await HandleRecordAsync(request),
            "screen.record.start" => await HandleStartAsync(request),
            "screen.record.stop"  => await HandleStopAsync(request),
            _ => Error($"Unknown command: {request.Command}")
        };
    }
    
    private async Task<NodeInvokeResponse> HandleCaptureAsync(NodeInvokeRequest request)
    {
        var format = GetStringArg(request.Args, "format", "png");
        var maxWidth = GetIntArg(request.Args, "maxWidth", 1920);
        var quality = GetIntArg(request.Args, "quality", 80);
        var monitor = GetIntArg(request.Args, "monitor", 0);
        var screenIndex = GetIntArg(request.Args, "screenIndex", monitor);
        var includePointer = GetBoolArg(request.Args, "includePointer", true);
        
        Logger.Info($"screen.capture: format={format}, maxWidth={maxWidth}, monitor={screenIndex}");
        
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
            return Error($"Capture failed: {ex.Message}");
        }
    }
    
    private async Task<NodeInvokeResponse> HandleListAsync(NodeInvokeRequest request)
    {
        Logger.Info("screen.list");
        
        if (ListRequested == null)
        {
            return Error("Screen list not available");
        }
        
        try
        {
            var screens = await ListRequested();
            var formatted = new List<object>();
            foreach (var screen in screens)
            {
                formatted.Add(new
                {
                    index = screen.Index,
                    name = screen.Name,
                    primary = screen.IsPrimary,
                    bounds = new { x = screen.X, y = screen.Y, width = screen.Width, height = screen.Height },
                    workingArea = new { x = screen.WorkingX, y = screen.WorkingY, width = screen.WorkingWidth, height = screen.WorkingHeight }
                });
            }
            return Success(new { screens = formatted });
        }
        catch (Exception ex)
        {
            Logger.Error("Screen list failed", ex);
            return Error($"List failed: {ex.Message}");
        }
    }

    private async Task<NodeInvokeResponse> HandleRecordAsync(NodeInvokeRequest request)
    {
        var durationMs  = GetIntArg(request.Args, "durationMs", 5000);
        var fps         = GetIntArg(request.Args, "fps", 10);
        var screenIndex = GetIntArg(request.Args, "screenIndex", GetIntArg(request.Args, "monitor", 0));

        Logger.Info($"screen.record: durationMs={durationMs} fps={fps} screenIndex={screenIndex}");

        if (RecordRequested == null)
            return Error("Screen recording not available");

        try
        {
            var result = await RecordRequested(new ScreenRecordArgs
            {
                DurationMs  = durationMs,
                Fps         = fps,
                ScreenIndex = screenIndex,
            });

            return Success(new
            {
                format      = result.Format,
                base64      = result.Base64,
                filePath    = result.FilePath,
                durationMs  = result.DurationMs,
                fps         = result.Fps,
                screenIndex = result.ScreenIndex,
                width       = result.Width,
                height      = result.Height,
                hasAudio    = result.HasAudio,
            });
        }
        catch (Exception ex)
        {
            Logger.Error("screen.record failed", ex);
            return Error($"Record failed: {ex.GetType().Name}: {ex.Message} | {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
        }
    }

    private async Task<NodeInvokeResponse> HandleStartAsync(NodeInvokeRequest request)
    {
        var fps         = GetIntArg(request.Args, "fps", 10);
        var screenIndex = GetIntArg(request.Args, "screenIndex", GetIntArg(request.Args, "monitor", 0));

        Logger.Info($"screen.record.start: fps={fps} screenIndex={screenIndex}");

        if (StartRequested == null)
            return Error("Screen recording not available");

        try
        {
            var recordingId = await StartRequested(new ScreenRecordStartArgs
            {
                Fps         = fps,
                ScreenIndex = screenIndex,
            });
            return Success(new { recordingId });
        }
        catch (Exception ex)
        {
            Logger.Error("screen.record.start failed", ex);
            return Error($"Start failed: {ex.Message}");
        }
    }

    private async Task<NodeInvokeResponse> HandleStopAsync(NodeInvokeRequest request)
    {
        var recordingId = GetStringArg(request.Args, "recordingId", "");

        Logger.Info($"screen.record.stop: recordingId={recordingId}");

        if (string.IsNullOrEmpty(recordingId))
            return Error("recordingId is required");

        if (StopRequested == null)
            return Error("Screen recording not available");

        try
        {
            var result = await StopRequested(recordingId);
            return Success(new
            {
                format      = result.Format,
                base64      = result.Base64,
                filePath    = result.FilePath,
                durationMs  = result.DurationMs,
                fps         = result.Fps,
                screenIndex = result.ScreenIndex,
                width       = result.Width,
                height      = result.Height,
                hasAudio    = result.HasAudio,
            });
        }
        catch (Exception ex)
        {
            Logger.Error("screen.record.stop failed", ex);
            return Error($"Stop failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Parameters for a fixed-duration screen recording.
/// Memory usage: width × height × 4 bytes × (durationMs/1000 × fps) frames.
/// Recommended limits: durationMs ≤ 10 000, fps ≤ 10 for 1080p to stay under 500 MB.
/// The service enforces a hard 500 MB frame-buffer cap and stops capture early if exceeded.
/// </summary>
public class ScreenRecordArgs
{
    public int DurationMs { get; set; } = 5000;
    public int Fps { get; set; } = 10;
    public int ScreenIndex { get; set; }
}

/// <summary>
/// Parameters for an open-ended screen recording session (screen.record.start / screen.record.stop).
/// The same 500 MB frame-buffer cap applies; capture stops automatically if the limit is hit.
/// </summary>
public class ScreenRecordStartArgs
{
    public int Fps { get; set; } = 10;
    public int ScreenIndex { get; set; }
}

public class ScreenRecordResult
{
    public string Base64 { get; set; } = "";
    public string Format { get; set; } = "mp4";
    public string? FilePath { get; set; }
    public int DurationMs { get; set; }
    public int Fps { get; set; }
    public int ScreenIndex { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool HasAudio { get; set; }
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

public class ScreenInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int WorkingX { get; set; }
    public int WorkingY { get; set; }
    public int WorkingWidth { get; set; }
    public int WorkingHeight { get; set; }
    public bool IsPrimary { get; set; }
}
