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
        "screen.snapshot"
        // Future: "screen.record"
    };
    
    public override IReadOnlyList<string> Commands => _commands;
    
    // Events for UI/platform-specific implementation
    public event Func<ScreenCaptureArgs, Task<ScreenCaptureResult>>? CaptureRequested;
    
    public ScreenCapability(IOpenClawLogger logger) : base(logger)
    {
    }
    
    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            "screen.snapshot" => await HandleCaptureAsync(request),
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
            return Error($"Capture failed: {ex.Message}");
        }
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

