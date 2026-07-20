using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Capabilities;

/// <summary>
/// Camera capability using Windows.Media.Capture
/// </summary>
public class CameraCapability : NodeCapabilityBase
{
    public override string Category => "camera";
    
    private static readonly string[] _commands = new[]
    {
        "camera.list",
        "camera.snap",
        "camera.clip"
    };
    
    public override IReadOnlyList<string> Commands => _commands;
    
    // Events for platform-specific implementation
    public event Func<CancellationToken, Task<CameraInfo[]>>? ListRequested;
    public event Func<CameraSnapArgs, CancellationToken, Task<CameraSnapResult>>? SnapRequested;
    public event Func<CameraClipArgs, CancellationToken, Task<CameraClipResult>>? ClipRequested;
    
    public CameraCapability(IOpenClawLogger logger) : base(logger)
    {
    }

    private static int Clamp(int value, int min, int max)
        => value < min ? min : (value > max ? max : value);
    
    public override Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
        => ExecuteAsync(request, CancellationToken.None);

    public override async Task<NodeInvokeResponse> ExecuteAsync(
        NodeInvokeRequest request,
        CancellationToken cancellationToken)
    {
        return request.Command switch
        {
            "camera.list" => await HandleListAsync(request, cancellationToken),
            "camera.snap" => await HandleSnapAsync(request, cancellationToken),
            "camera.clip" => await HandleClipAsync(request, cancellationToken),
            _ => Error($"Unknown command: {request.Command}")
        };
    }
    
    private async Task<NodeInvokeResponse> HandleListAsync(
        NodeInvokeRequest request,
        CancellationToken cancellationToken)
    {
        Logger.Info("camera.list");
        
        if (ListRequested == null)
        {
            return Error("Camera list not available");
        }
        
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cameras = await ListRequested(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Success(new { cameras });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Error("cancelled");
        }
        catch (Exception ex)
        {
            Logger.Error("Camera list failed", ex);
            return Error("List failed");
        }
    }
    
    // Boundary clamps - reject extreme/negative caller values up-front.
    private const int MinCameraDimension = 16;
    private const int MaxCameraWidth = 4096;
    private const int MinQuality = 1;
    private const int MaxQuality = 100;
    private const int MaxClipDurationMs = 60_000;

    private async Task<NodeInvokeResponse> HandleSnapAsync(
        NodeInvokeRequest request,
        CancellationToken cancellationToken)
    {
        var deviceId = GetStringArg(request.Args, "deviceId");
        var format = GetStringArg(request.Args, "format", "jpeg");
        var maxWidth = Clamp(GetIntArg(request.Args, "maxWidth", 1280), MinCameraDimension, MaxCameraWidth);
        var quality = Clamp(GetIntArg(request.Args, "quality", 80), MinQuality, MaxQuality);
        
        Logger.Info($"camera.snap: deviceId={deviceId ?? "(default)"}, format={format}");
        
        if (SnapRequested == null)
        {
            return Error("Camera snap not available");
        }
        
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await SnapRequested(new CameraSnapArgs
            {
                DeviceId = deviceId,
                Format = format ?? "jpeg",
                MaxWidth = maxWidth,
                Quality = quality
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            
            return Success(new
            {
                format = result.Format,
                width = result.Width,
                height = result.Height,
                base64 = result.Base64
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Error("cancelled");
        }
        catch (Exception ex)
        {
            Logger.Error("Camera snap failed", ex);
            return Error("Snap failed");
        }
    }
    
    private async Task<NodeInvokeResponse> HandleClipAsync(
        NodeInvokeRequest request,
        CancellationToken cancellationToken)
    {
        var deviceId = GetStringArg(request.Args, "deviceId");
        // Floor at 100ms - anything shorter is meaningless and a 0/negative
        // value previously slipped through the `Math.Min` cap.
        var durationMs = Clamp(GetIntArg(request.Args, "durationMs", 3000), 100, MaxClipDurationMs);
        var includeAudio = GetBoolArg(request.Args, "includeAudio", true);
        var format = GetStringArg(request.Args, "format", "mp4") ?? "mp4";
        
        Logger.Info($"camera.clip: deviceId={deviceId ?? "(default)"}, durationMs={durationMs}, includeAudio={includeAudio}, format={format}");
        
        if (ClipRequested == null)
        {
            return Error("Camera clip not available");
        }
        
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ClipRequested(new CameraClipArgs
            {
                DeviceId = deviceId,
                DurationMs = durationMs,
                IncludeAudio = includeAudio,
                Format = format
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            
            return Success(new
            {
                format = result.Format,
                base64 = result.Base64,
                durationMs = result.DurationMs,
                hasAudio = result.HasAudio
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Error("cancelled");
        }
        catch (Exception ex)
        {
            Logger.Error("Camera clip failed", ex);
            return Error("Clip failed");
        }
    }
}

public class CameraInfo
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
}

public class CameraSnapArgs
{
    public string? DeviceId { get; set; }
    public string Format { get; set; } = "jpeg";
    public int MaxWidth { get; set; } = 1280;
    public int Quality { get; set; } = 80;
}

public class CameraSnapResult
{
    public string Format { get; set; } = "jpeg";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Base64 { get; set; } = "";
}

public class CameraClipArgs
{
    public string? DeviceId { get; set; }
    public int DurationMs { get; set; } = 3000;
    public bool IncludeAudio { get; set; } = true;
    public string Format { get; set; } = "mp4";
}

public class CameraClipResult
{
    public string Format { get; set; } = "mp4";
    public string Base64 { get; set; } = "";
    public int DurationMs { get; set; }
    public bool HasAudio { get; set; }
}
