using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace OpenClaw.Shared.Capabilities;

/// <summary>
/// Canvas capability - WebView2-based canvas for displaying content
/// </summary>
public class CanvasCapability : NodeCapabilityBase
{
    public override string Category => "canvas";
    
    private static readonly string[] _commands = new[]
    {
        "canvas.present",
        "canvas.hide",
        "canvas.navigate",
        "canvas.eval",
        "canvas.snapshot",
        "canvas.a2ui.push",
        "canvas.a2ui.reset"
    };
    
    public override IReadOnlyList<string> Commands => _commands;
    
    // Events for UI to handle
    public event EventHandler<CanvasPresentArgs>? PresentRequested;
    public event EventHandler? HideRequested;
    public event EventHandler<string>? NavigateRequested;
    public event Func<string, Task<string>>? EvalRequested;
    public event Func<CanvasSnapshotArgs, Task<string>>? SnapshotRequested;
    public event EventHandler<CanvasA2UIArgs>? A2UIPushRequested;
    public event EventHandler? A2UIResetRequested;
    
    public CanvasCapability(IOpenClawLogger logger) : base(logger)
    {
    }
    
    public override async Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
    {
        return request.Command switch
        {
            "canvas.present" => await HandlePresentAsync(request),
            "canvas.hide" => HandleHide(request),
            "canvas.navigate" => await HandleNavigateAsync(request),
            "canvas.eval" => await HandleEvalAsync(request),
            "canvas.snapshot" => await HandleSnapshotAsync(request),
            "canvas.a2ui.push" => HandleA2UIPush(request),
            "canvas.a2ui.reset" => HandleA2UIReset(request),
            _ => Error($"Unknown command: {request.Command}")
        };
    }
    
    private Task<NodeInvokeResponse> HandlePresentAsync(NodeInvokeRequest request)
    {
        var url = GetStringArg(request.Args, "url");
        var html = GetStringArg(request.Args, "html");
        var width = GetIntArg(request.Args, "width", 800);
        var height = GetIntArg(request.Args, "height", 600);
        var x = GetIntArg(request.Args, "x", -1); // -1 = center
        var y = GetIntArg(request.Args, "y", -1);
        var title = GetStringArg(request.Args, "title", "Canvas");
        var alwaysOnTop = GetBoolArg(request.Args, "alwaysOnTop", false);
        
        Logger.Info($"canvas.present: url={url ?? "(html)"}, size={width}x{height}");
        
        PresentRequested?.Invoke(this, new CanvasPresentArgs
        {
            Url = url,
            Html = html,
            Width = width,
            Height = height,
            X = x,
            Y = y,
            Title = title ?? "Canvas",
            AlwaysOnTop = alwaysOnTop
        });
        
        return Task.FromResult(Success(new { presented = true }));
    }
    
    private NodeInvokeResponse HandleHide(NodeInvokeRequest request)
    {
        Logger.Info("canvas.hide");
        HideRequested?.Invoke(this, EventArgs.Empty);
        return Success(new { hidden = true });
    }
    
    private async Task<NodeInvokeResponse> HandleNavigateAsync(NodeInvokeRequest request)
    {
        var url = GetStringArg(request.Args, "url");
        if (string.IsNullOrEmpty(url))
        {
            return Error("Missing url parameter");
        }
        
        Logger.Info($"canvas.navigate: {url}");
        NavigateRequested?.Invoke(this, url);
        
        return Success(new { navigated = true });
    }
    
    private async Task<NodeInvokeResponse> HandleEvalAsync(NodeInvokeRequest request)
    {
        var script = GetStringArg(request.Args, "script")
            ?? GetStringArg(request.Args, "javaScript")
            ?? GetStringArg(request.Args, "javascript");
        if (string.IsNullOrEmpty(script))
        {
            return Error("Missing script parameter");
        }
        
        Logger.Info($"canvas.eval: {script[..Math.Min(50, script.Length)]}...");
        
        if (EvalRequested == null)
        {
            return Error("Canvas not available");
        }
        
        try
        {
            var result = await EvalRequested(script);
            return Success(new { result });
        }
        catch (Exception ex)
        {
            return Error($"Eval failed: {ex.Message}");
        }
    }
    
    private async Task<NodeInvokeResponse> HandleSnapshotAsync(NodeInvokeRequest request)
    {
        var format = GetStringArg(request.Args, "format", "png");
        var maxWidth = GetIntArg(request.Args, "maxWidth", 1200);
        var quality = GetIntArg(request.Args, "quality", 80);
        
        Logger.Info($"canvas.snapshot: format={format}, maxWidth={maxWidth}");
        
        if (SnapshotRequested == null)
        {
            return Error("Canvas not available");
        }
        
        try
        {
            var base64 = await SnapshotRequested(new CanvasSnapshotArgs
            {
                Format = format ?? "png",
                MaxWidth = maxWidth,
                Quality = quality
            });
            
            return Success(new { format, base64 });
        }
        catch (Exception ex)
        {
            return Error($"Snapshot failed: {ex.Message}");
        }
    }
    
    private NodeInvokeResponse HandleA2UIPush(NodeInvokeRequest request)
    {
        var jsonl = GetStringArg(request.Args, "jsonl");
        var jsonlPath = GetStringArg(request.Args, "jsonlPath");
        var props = request.Args.TryGetProperty("props", out var propsEl) ? propsEl : default;
        
        if (string.IsNullOrWhiteSpace(jsonl) && !string.IsNullOrWhiteSpace(jsonlPath))
        {
            // Validate jsonlPath to prevent arbitrary file reads.
            // Resolve to absolute path and reject traversal or suspicious paths.
            try
            {
                var fullPath = Path.GetFullPath(jsonlPath);
                var tempRoot = Path.GetFullPath(Path.GetTempPath());
                if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warn($"canvas.a2ui.push: jsonlPath outside temp directory: {fullPath}");
                    return Error("jsonlPath must be within the system temp directory");
                }
            }
            catch (Exception ex)
            {
                return Error($"Invalid jsonlPath: {ex.Message}");
            }

            try
            {
                jsonl = File.ReadAllText(jsonlPath);
            }
            catch (Exception ex)
            {
                Logger.Error($"canvas.a2ui.push: failed to read jsonlPath ({jsonlPath})", ex);
                return Error($"Failed to read jsonlPath: {ex.Message}");
            }
        }
        
        if (string.IsNullOrWhiteSpace(jsonl))
        {
            return Error("Missing jsonl or jsonlPath parameter");
        }
        
        Logger.Info($"canvas.a2ui.push: {jsonl.Length} chars");
        
        A2UIPushRequested?.Invoke(this, new CanvasA2UIArgs
        {
            Jsonl = jsonl,
            JsonlPath = jsonlPath,
            Props = props.ValueKind != default ? props.GetRawText() : "{}"
        });
        
        return Success(new { pushed = true });
    }
    
    private NodeInvokeResponse HandleA2UIReset(NodeInvokeRequest request)
    {
        Logger.Info("canvas.a2ui.reset");
        A2UIResetRequested?.Invoke(this, EventArgs.Empty);
        return Success(new { reset = true });
    }
}

public class CanvasPresentArgs : EventArgs
{
    public string? Url { get; set; }
    public string? Html { get; set; }
    public int Width { get; set; } = 800;
    public int Height { get; set; } = 600;
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public string Title { get; set; } = "Canvas";
    public bool AlwaysOnTop { get; set; }
}

public class CanvasSnapshotArgs
{
    public string Format { get; set; } = "png";
    public int MaxWidth { get; set; } = 1200;
    public int Quality { get; set; } = 80;
}

public class CanvasA2UIArgs : EventArgs
{
    public string? Jsonl { get; set; }
    public string? JsonlPath { get; set; }
    public string Props { get; set; } = "{}";
}
