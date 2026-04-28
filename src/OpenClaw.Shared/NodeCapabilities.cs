using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenClaw.Shared;

/// <summary>
/// Represents a command that a node can handle
/// </summary>
public class NodeCommand
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = ""; // canvas, camera, screen, system, etc.
}

/// <summary>
/// Request from gateway to invoke a node command
/// </summary>
public class NodeInvokeRequest
{
    public string Id { get; set; } = "";
    public string Command { get; set; } = "";
    public JsonElement Args { get; set; }
}

public class NodeInvokeCompletedEventArgs : EventArgs
{
    public string RequestId { get; set; } = "";
    public string Command { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public TimeSpan Duration { get; set; }
    public string? NodeId { get; set; }
}

/// <summary>
/// Response to a node.invoke request
/// </summary>
public class NodeInvokeResponse
{
    public string Id { get; set; } = "";
    public bool Ok { get; set; }
    public object? Payload { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Interface for implementing node capabilities
/// </summary>
public interface INodeCapability
{
    /// <summary>The capability category (canvas, camera, screen, system)</summary>
    string Category { get; }
    
    /// <summary>Commands this capability can handle</summary>
    IReadOnlyList<string> Commands { get; }
    
    /// <summary>Check if this capability can handle the given command</summary>
    bool CanHandle(string command);
    
    /// <summary>Execute a command and return the result</summary>
    Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request);
}

/// <summary>
/// Base class for node capabilities with common functionality
/// </summary>
public abstract class NodeCapabilityBase : INodeCapability
{
    public abstract string Category { get; }
    public abstract IReadOnlyList<string> Commands { get; }
    
    protected IOpenClawLogger Logger { get; }
    
    protected NodeCapabilityBase(IOpenClawLogger logger)
    {
        Logger = logger;
    }
    
    public virtual bool CanHandle(string command)
    {
        return Commands.Contains(command);
    }
    
    public abstract Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request);
    
    protected NodeInvokeResponse Success(object? payload = null)
    {
        return new NodeInvokeResponse { Ok = true, Payload = payload };
    }
    
    protected NodeInvokeResponse Error(string message)
    {
        return new NodeInvokeResponse { Ok = false, Error = message };
    }
    
    protected T? GetArg<T>(JsonElement args, string name, T? defaultValue = default)
    {
        if (args.ValueKind == JsonValueKind.Undefined || args.ValueKind == JsonValueKind.Null)
            return defaultValue;
        if (args.TryGetProperty(name, out var prop))
        {
            try
            {
                return JsonSerializer.Deserialize<T>(prop.GetRawText());
            }
            catch
            {
                return defaultValue;
            }
        }
        return defaultValue;
    }
    
    protected string? GetStringArg(JsonElement args, string name, string? defaultValue = null)
    {
        if (args.ValueKind == JsonValueKind.Undefined || args.ValueKind == JsonValueKind.Null)
            return defaultValue;
        if (args.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return defaultValue;
    }
    
    protected int GetIntArg(JsonElement args, string name, int defaultValue = 0)
    {
        if (args.ValueKind == JsonValueKind.Undefined || args.ValueKind == JsonValueKind.Null)
            return defaultValue;
        if (args.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            try { return prop.GetInt32(); }
            catch (FormatException) { return defaultValue; }
        }
        return defaultValue;
    }
    
    protected bool GetBoolArg(JsonElement args, string name, bool defaultValue = false)
    {
        if (args.ValueKind == JsonValueKind.Undefined || args.ValueKind == JsonValueKind.Null)
            return defaultValue;
        if (args.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) return true;
            if (prop.ValueKind == JsonValueKind.False) return false;
        }
        return defaultValue;
    }

    /// <summary>
    /// Get a string array from a JSON array property. Non-string and whitespace-only elements
    /// are ignored. Strings are trimmed to preserve the historical system.which behavior.
    /// </summary>
    protected string[] GetStringArrayArg(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Undefined || args.ValueKind == JsonValueKind.Null)
            return Array.Empty<string>();
        if (!args.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var buffer = new string[prop.GetArrayLength()];
        var count = 0;
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var value = item.GetString()?.Trim();
            if (!string.IsNullOrEmpty(value))
                buffer[count++] = value;
        }

        return count > 0 ? buffer[..count] : [];
    }
}

/// <summary>
/// Node registration information
/// </summary>
public class NodeRegistration
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Platform { get; set; } = "windows";
    public string DisplayName { get; set; } = "";
    public List<string> Capabilities { get; set; } = new();
    public List<string> Commands { get; set; } = new();
    public Dictionary<string, bool> Permissions { get; set; } = new();
}
