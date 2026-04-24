using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Capabilities;

/// <summary>
/// Location capability using Windows.Devices.Geolocation
/// </summary>
public class LocationCapability : NodeCapabilityBase
{
    public override string Category => "location";
    
    private static readonly string[] _commands = new[] { "location.get" };
    
    public override IReadOnlyList<string> Commands => _commands;
    
    public event Func<LocationGetArgs, Task<LocationResult>>? GetRequested;
    
    public LocationCapability(IOpenClawLogger logger) : base(logger)
    {
    }
    
    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            "location.get" => await HandleGetAsync(request),
            _ => Error($"Unknown command: {request.Command}")
        };
    }
    
    private async Task<NodeInvokeResponse> HandleGetAsync(NodeInvokeRequest request)
    {
        var accuracy = GetStringArg(request.Args, "accuracy", "default");
        var maxAgeMs = GetIntArg(request.Args, "maxAge", 30000);
        var timeoutMs = GetIntArg(request.Args, "locationTimeout", 10000);
        
        Logger.Info($"location.get: accuracy={accuracy}, maxAge={maxAgeMs}, timeout={timeoutMs}");
        
        if (GetRequested == null)
            return Error("Location not available");
        
        try
        {
            var result = await GetRequested(new LocationGetArgs
            {
                Accuracy = accuracy ?? "default",
                MaxAgeMs = maxAgeMs,
                TimeoutMs = timeoutMs
            });
            return Success(new
            {
                latitude = result.Latitude,
                longitude = result.Longitude,
                accuracy = result.AccuracyMeters,
                timestamp = result.TimestampMs
            });
        }
        catch (UnauthorizedAccessException)
        {
            return Error("LOCATION_PERMISSION_REQUIRED");
        }
        catch (Exception ex)
        {
            Logger.Error("location.get failed", ex);
            return Error($"Location failed: {ex.Message}");
        }
    }
}

public class LocationGetArgs
{
    public string Accuracy { get; set; } = "default";
    public int MaxAgeMs { get; set; } = 30000;
    public int TimeoutMs { get; set; } = 10000;
}

public class LocationResult
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double AccuracyMeters { get; set; }
    public long TimestampMs { get; set; }
}
