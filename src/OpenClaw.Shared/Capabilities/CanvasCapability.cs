using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

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
        "canvas.a2ui.pushJSONL",
        "canvas.a2ui.reset",
        "canvas.a2ui.dump",
        "canvas.caps",
    };
    
    public override IReadOnlyList<string> Commands => _commands;
    
    // Events for UI to handle
    public event EventHandler<CanvasPresentArgs>? PresentRequested;
    public event EventHandler? HideRequested;
    /// <summary>
    /// Subscriber decides how to handle a navigate request and returns the
    /// opener that actually serviced it: <c>"canvas"</c> if an in-process
    /// WebView2 frame navigated, <c>"browser"</c> if the URL was handed to the
    /// OS default browser. Throwing surfaces as an error to the gateway.
    /// Single-subscriber: same multi-handler hazard as the other Func events.
    /// </summary>
    private Func<string, Task<string>>? _navigateRequested;
    public event Func<string, Task<string>> NavigateRequested
    {
        add => SetSingleHandler(ref _navigateRequested, value, nameof(NavigateRequested));
        remove => ClearSingleHandler(ref _navigateRequested, value);
    }
    // Func-based "events" are inherently single-handler — multi-subscribe to a
    // Delegate.Combine'd Func silently invokes only the last subscriber's
    // return value, hiding the others. Expose them as single-subscriber events
    // that throw on a second subscribe so this is loud.
    private Func<string, Task<string>>? _evalRequested;
    public event Func<string, Task<string>> EvalRequested
    {
        add => SetSingleHandler(ref _evalRequested, value, nameof(EvalRequested));
        remove => ClearSingleHandler(ref _evalRequested, value);
    }
    private Func<CanvasSnapshotArgs, Task<string>>? _snapshotRequested;
    public event Func<CanvasSnapshotArgs, Task<string>> SnapshotRequested
    {
        add => SetSingleHandler(ref _snapshotRequested, value, nameof(SnapshotRequested));
        remove => ClearSingleHandler(ref _snapshotRequested, value);
    }
    public event EventHandler<CanvasA2UIArgs>? A2UIPushRequested;
    public event EventHandler? A2UIResetRequested;
    /// <summary>Returns a JSON state dump of the native A2UI surface graph.</summary>
    private Func<Task<string>>? _a2uiDumpRequested;
    public event Func<Task<string>> A2UIDumpRequested
    {
        add => SetSingleHandler(ref _a2uiDumpRequested, value, nameof(A2UIDumpRequested));
        remove => ClearSingleHandler(ref _a2uiDumpRequested, value);
    }
    /// <summary>Returns a JSON capability summary describing which canvas operations are supported.</summary>
    private Func<Task<string>>? _capsRequested;
    public event Func<Task<string>> CapsRequested
    {
        add => SetSingleHandler(ref _capsRequested, value, nameof(CapsRequested));
        remove => ClearSingleHandler(ref _capsRequested, value);
    }

    private static void SetSingleHandler<T>(ref T? slot, T value, string name) where T : Delegate
    {
        if (slot != null && !ReferenceEquals(slot, value))
            throw new InvalidOperationException($"{name} accepts only one subscriber. Detach the previous handler first.");
        slot = value;
    }
    private static void ClearSingleHandler<T>(ref T? slot, T value) where T : Delegate
    {
        if (ReferenceEquals(slot, value)) slot = null;
    }
    
    public CanvasCapability(IOpenClawLogger logger) : base(logger)
    {
    }

    private static int Clamp(int value, int min, int max)
        => value < min ? min : (value > max ? max : value);

    private static int ClampPosition(int value)
    {
        if (value == -1) return -1; // documented "center" sentinel
        return value < MinPosition ? MinPosition : (value > MaxPosition ? MaxPosition : value);
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
            "canvas.a2ui.pushJSONL" => HandleA2UIPush(request),
            "canvas.a2ui.reset" => HandleA2UIReset(request),
            "canvas.a2ui.dump" => await HandleA2UIDumpAsync(),
            "canvas.caps" => await HandleCapsAsync(),
            _ => Error($"Unknown command: {request.Command}")
        };
    }
    
    // Window-bounds clamps. -1 is the documented "center" sentinel for x/y so
    // we preserve negatives below MinPosition by routing them to -1.
    private const int MinDimension = 100;
    private const int MaxDimension = 7680;
    private const int MinPosition = -16384;
    private const int MaxPosition = 16384;
    private const int MinSnapshotWidth = 32;
    private const int MaxSnapshotWidth = 7680;
    private const int MinQuality = 1;
    private const int MaxQuality = 100;

    // A2UI push caps. Inline transport in McpHttpServer caps at 4 MiB; jsonlPath
    // bypasses that, so re-enforce here. The line-count cap protects the UI thread
    // from a single push that explodes into thousands of dispatcher posts.
    internal const long MaxA2UIJsonlBytes = 4L * 1024 * 1024;
    internal const int MaxA2UIJsonlLines = 4096;

    private Task<NodeInvokeResponse> HandlePresentAsync(NodeInvokeRequest request)
    {
        var url = GetStringArg(request.Args, "url");
        var html = GetStringArg(request.Args, "html");
        var width = Clamp(GetIntArg(request.Args, "width", 800), MinDimension, MaxDimension);
        var height = Clamp(GetIntArg(request.Args, "height", 600), MinDimension, MaxDimension);
        var x = ClampPosition(GetIntArg(request.Args, "x", -1)); // -1 = center
        var y = ClampPosition(GetIntArg(request.Args, "y", -1));
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
        var rawUrl = GetStringArg(request.Args, "url");
        if (string.IsNullOrEmpty(rawUrl))
        {
            return Error("Missing url parameter");
        }

        // Validate up front so the OS-level Process.Start in the subscriber
        // can't be tricked into shell-executing javascript:/file:/app-protocol
        // URIs. The subscriber re-validates as defense-in-depth.
        if (!HttpUrlValidator.TryParse(rawUrl, out var canonical, out var validationError))
        {
            // Avoid leaking the raw URL — agents sometimes hand us tokenized
            // OAuth/reset URLs that fail validation, and our log files have
            // an effectively-unbounded retention policy. Sanitize to scheme +
            // host + first path segment.
            Logger.Warn($"canvas.navigate rejected: {validationError} (sanitized: {UrlLogSanitizer.Sanitize(rawUrl)})");
            return Error($"Invalid url: {validationError}");
        }

        Logger.Info($"canvas.navigate: {UrlLogSanitizer.Sanitize(canonical)}");

        var handler = _navigateRequested;
        if (handler == null)
        {
            // No subscriber means there's no surface to navigate and no opener
            // to fall back to. Tell the agent honestly so it can pick another
            // tool instead of believing it succeeded.
            return Error("CANVAS_NOT_AVAILABLE: no navigate handler registered");
        }

        try
        {
            var opener = await handler(canonical!);
            // opener is the subscriber's word for how it serviced the request:
            // "canvas" (existing WebView2 frame), "browser" (default browser),
            // or anything else the subscriber wants to surface back to the agent.
            return Success(new { navigated = true, opener, url = canonical });
        }
        catch (Exception ex)
        {
            Logger.Error($"canvas.navigate handler failed: {ex.Message}", ex);
            return Error($"Navigate failed: {ex.Message}");
        }
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
        
        var evalHandler = _evalRequested;
        if (evalHandler == null)
        {
            return Error("Canvas not available");
        }

        try
        {
            var result = await evalHandler(script);
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
        var maxWidth = Clamp(GetIntArg(request.Args, "maxWidth", 1200), MinSnapshotWidth, MaxSnapshotWidth);
        var quality = Clamp(GetIntArg(request.Args, "quality", 80), MinQuality, MaxQuality);
        
        Logger.Info($"canvas.snapshot: format={format}, maxWidth={maxWidth}");
        
        var snapshotHandler = _snapshotRequested;
        if (snapshotHandler == null)
        {
            return Error("Canvas not available");
        }

        try
        {
            var base64 = await snapshotHandler(new CanvasSnapshotArgs
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
            try
            {
                jsonl = ReadValidatedJsonlPath(jsonlPath, request.Command);
            }
            catch (Exception ex)
            {
                Logger.Error($"{request.Command}: failed to read jsonlPath", ex);
                return Error($"Failed to read jsonlPath: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(jsonl))
        {
            return Error("Missing jsonl or jsonlPath parameter");
        }

        // Inline-jsonl size cap. Encoding.UTF8.GetByteCount streams over chars
        // without allocating, so this is cheap.
        long byteCount = System.Text.Encoding.UTF8.GetByteCount(jsonl);
        if (byteCount > MaxA2UIJsonlBytes)
        {
            Logger.Warn($"{request.Command}: jsonl payload too large ({byteCount} > {MaxA2UIJsonlBytes})");
            return Error($"jsonl exceeds maximum size of {MaxA2UIJsonlBytes} bytes");
        }

        // Line-count cap. A push that fans out to thousands of UI-thread
        // dispatches has DoS potential even if individually small.
        int lineCount = CountLines(jsonl);
        if (lineCount > MaxA2UIJsonlLines)
        {
            Logger.Warn($"{request.Command}: jsonl line count too high ({lineCount} > {MaxA2UIJsonlLines})");
            return Error($"jsonl exceeds maximum of {MaxA2UIJsonlLines} lines");
        }

        Logger.Info($"{request.Command}: {byteCount} bytes, {lineCount} lines");

        A2UIPushRequested?.Invoke(this, new CanvasA2UIArgs
        {
            Jsonl = jsonl,
            JsonlPath = jsonlPath,
            Props = props.ValueKind != default ? props.GetRawText() : "{}"
        });

        return Success(new { pushed = true });
    }

    private static int CountLines(string s)
    {
        // Count non-empty newline-delimited lines without allocating an array.
        int count = 0;
        bool inLine = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\n' || c == '\r')
            {
                if (inLine) { count++; inLine = false; }
            }
            else if (!char.IsWhiteSpace(c))
            {
                inLine = true;
            }
        }
        if (inLine) count++;
        return count;
    }

    private string ReadValidatedJsonlPath(string jsonlPath, string command)
    {
        string fullPath;
        string tempRoot;
        try
        {
            fullPath = Path.GetFullPath(jsonlPath);
            tempRoot = EnsureTrailingSeparator(Path.GetFullPath(Path.GetTempPath()));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Invalid jsonlPath: {ex.Message}", ex);
        }

        if (!IsPathWithinRoot(fullPath, tempRoot))
        {
            Logger.Warn($"{command}: jsonlPath outside temp directory: {fullPath}");
            throw new InvalidOperationException("jsonlPath must be within the system temp directory");
        }

        var fi = new FileInfo(fullPath);
        if (fi.Exists && fi.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            FileSystemInfo? resolved;
            try
            {
                resolved = fi.ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (Exception ex)
            {
                Logger.Warn($"{command}: jsonlPath reparse point could not be resolved: {ex.Message}");
                throw new InvalidOperationException("jsonlPath contains an unresolvable reparse point", ex);
            }

            if (resolved == null)
            {
                Logger.Warn($"{command}: jsonlPath reparse point could not be resolved");
                throw new InvalidOperationException("jsonlPath contains an unresolvable reparse point");
            }

            if (!IsPathWithinRoot(resolved.FullName, tempRoot))
            {
                Logger.Warn($"{command}: jsonlPath reparse point resolves outside temp directory: {resolved.FullName}");
                throw new InvalidOperationException("jsonlPath reparse point must resolve within the system temp directory");
            }
        }

        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var finalPath = GetFinalPathFromHandle(stream.SafeFileHandle);
        if (!IsPathWithinRoot(finalPath, tempRoot))
        {
            Logger.Warn($"{command}: jsonlPath file handle resolves outside temp directory: {finalPath}");
            throw new InvalidOperationException("jsonlPath must resolve within the system temp directory");
        }

        if (stream.Length > MaxA2UIJsonlBytes)
        {
            Logger.Warn($"{command}: jsonlPath file too large ({stream.Length} > {MaxA2UIJsonlBytes})");
            throw new InvalidOperationException($"jsonlPath exceeds maximum size of {MaxA2UIJsonlBytes} bytes");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(NormalizeFinalPath(path));
        var normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(NormalizeFinalPath(root)));
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
            return path;
        return path + Path.DirectorySeparatorChar;
    }

    private static string GetFinalPathFromHandle(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
            return string.Empty;

        var capacity = 512;
        while (capacity <= 32768)
        {
            var sb = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity, 0);
            if (length == 0)
                throw new IOException($"GetFinalPathNameByHandle failed with Win32 error {Marshal.GetLastWin32Error()}");
            if (length < sb.Capacity)
                return NormalizeFinalPath(sb.ToString());
            capacity = (int)length + 1;
        }
        throw new IOException("GetFinalPathNameByHandle returned an unexpectedly long path");
    }

    private static string NormalizeFinalPath(string path)
    {
        const string extendedPrefix = @"\\?\";
        const string extendedUncPrefix = @"\\?\UNC\";
        if (path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + path.Substring(extendedUncPrefix.Length);
        if (path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase))
            return path.Substring(extendedPrefix.Length);
        return path;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);
    
    private NodeInvokeResponse HandleA2UIReset(NodeInvokeRequest request)
    {
        Logger.Info("canvas.a2ui.reset");
        A2UIResetRequested?.Invoke(this, EventArgs.Empty);
        return Success(new { reset = true });
    }

    private async Task<NodeInvokeResponse> HandleA2UIDumpAsync()
    {
        Logger.Info("canvas.a2ui.dump");
        var dumpHandler = _a2uiDumpRequested;
        if (dumpHandler == null)
            return Error("CANVAS_NOT_OPEN: no A2UI canvas is currently active");
        try
        {
            var json = await dumpHandler();
            // Pass through as a JSON-typed payload so MCP clients see structured data,
            // not a quoted string.
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return Success(System.Text.Json.JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText()));
        }
        catch (Exception ex)
        {
            return Error($"CANVAS_DUMP_FAILED: {ex.Message}");
        }
    }

    private async Task<NodeInvokeResponse> HandleCapsAsync()
    {
        var capsHandler = _capsRequested;
        if (capsHandler == null)
        {
            return Success(new
            {
                renderer = "none",
                eval = false,
                snapshot = false,
                navigate = false,
                a2ui = new { version = "0.8", introspect = false },
            });
        }
        try
        {
            var json = await capsHandler();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return Success(System.Text.Json.JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText()));
        }
        catch (Exception ex)
        {
            return Error($"CANVAS_CAPS_FAILED: {ex.Message}");
        }
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
