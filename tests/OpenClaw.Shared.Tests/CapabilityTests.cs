using System.IO;
using System.Text.Json;
using Xunit;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Unit tests for each capability: SystemCapability, CanvasCapability,
/// ScreenCapability, CameraCapability.
/// Tests execute logic, arg parsing, event raising, and error paths.
/// No hardware or UI dependencies.
/// </summary>
public class SystemCapabilityTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void CanHandle_SystemNotify()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        Assert.True(cap.CanHandle("system.notify"));
        Assert.True(cap.CanHandle("system.run"));
        Assert.False(cap.CanHandle("system.unknown"));
        Assert.Equal("system", cap.Category);
    }

    [Fact]
    public async Task Notify_RaisesEvent_WithArgs()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        SystemNotifyArgs? received = null;
        cap.NotifyRequested += (s, a) => received = a;

        var req = new NodeInvokeRequest
        {
            Id = "n1",
            Command = "system.notify",
            Args = Parse("""{"title":"Hello","body":"World","sound":false}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(received);
        Assert.Equal("Hello", received!.Title);
        Assert.Equal("World", received.Body);
        Assert.False(received.PlaySound);
    }

    [Fact]
    public async Task Notify_DefaultsTitle_WhenMissing()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        SystemNotifyArgs? received = null;
        cap.NotifyRequested += (s, a) => received = a;

        var req = new NodeInvokeRequest
        {
            Id = "n2",
            Command = "system.notify",
            Args = Parse("""{"body":"Just body"}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Equal("OpenClaw", received!.Title);
    }

    [Fact]
    public async Task UnknownCommand_ReturnsError()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "n3",
            Command = "system.unknown",
            Args = Parse("""{}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("Unknown command", res.Error);
    }

    [Fact]
    public async Task Run_AcceptsCommandAsArray()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var runner = new FakeCommandRunner();
        cap.SetCommandRunner(runner);

        var req = new NodeInvokeRequest
        {
            Id = "r1",
            Command = "system.run",
            Args = Parse("""{"command":["echo","hello","world"]}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Equal("echo", runner.LastRequest!.Command);
        Assert.Equal(new[] { "hello", "world" }, runner.LastRequest.Args);
    }

    [Fact]
    public async Task Run_AcceptsCommandAsString()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var runner = new FakeCommandRunner();
        cap.SetCommandRunner(runner);

        var req = new NodeInvokeRequest
        {
            Id = "r2",
            Command = "system.run",
            Args = Parse("""{"command":"hostname"}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Equal("hostname", runner.LastRequest!.Command);
        Assert.Null(runner.LastRequest.Args);
    }

    [Fact]
    public async Task Run_AcceptsSingleElementArray()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var runner = new FakeCommandRunner();
        cap.SetCommandRunner(runner);

        var req = new NodeInvokeRequest
        {
            Id = "r3",
            Command = "system.run",
            Args = Parse("""{"command":["hostname"]}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Equal("hostname", runner.LastRequest!.Command);
        Assert.Null(runner.LastRequest.Args);
    }

    [Fact]
    public async Task Run_ReturnsError_WhenNoCommand()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        cap.SetCommandRunner(new FakeCommandRunner());

        var req = new NodeInvokeRequest
        {
            Id = "r4",
            Command = "system.run",
            Args = Parse("""{}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("Missing command", res.Error);
    }

    [Fact]
    public async Task Run_ReturnsError_WhenNoRunner()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "r5",
            Command = "system.run",
            Args = Parse("""{"command":["echo","test"]}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("not available", res.Error);
    }

    [Fact]
    public async Task Run_ReadsTimeoutMs()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var runner = new FakeCommandRunner();
        cap.SetCommandRunner(runner);

        var req = new NodeInvokeRequest
        {
            Id = "r6",
            Command = "system.run",
            Args = Parse("""{"command":["test"],"timeoutMs":60000}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Equal(60000, runner.LastRequest!.TimeoutMs);
    }

    [Fact]
    public void CanHandle_SystemWhich()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        Assert.True(cap.CanHandle("system.which"));
    }

    [Fact]
    public async Task Which_FindsKnownBins()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        // cmd.exe should always exist on Windows
        var req = new NodeInvokeRequest
        {
            Id = "w1",
            Command = "system.which",
            Args = Parse("""{"bins":["cmd"]}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);

        var payload = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(res.Payload));
        Assert.True(payload.TryGetProperty("bins", out var binsEl));
        // cmd should resolve on Windows
        if (OperatingSystem.IsWindows())
        {
            Assert.True(binsEl.TryGetProperty("cmd", out var cmdPath));
            Assert.Contains("cmd", cmdPath.GetString()!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Which_OmitsMissingBins()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "w2",
            Command = "system.which",
            Args = Parse("""{"bins":["totally_nonexistent_binary_xyz123"]}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);

        var payload = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(res.Payload));
        Assert.True(payload.TryGetProperty("bins", out var binsEl));
        Assert.False(binsEl.TryGetProperty("totally_nonexistent_binary_xyz123", out _));
    }

    [Fact]
    public async Task Which_RejectsPathsWithSeparators()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "w3",
            Command = "system.which",
            Args = Parse("""{"bins":["..\\..\\etc\\passwd","../../../bin/sh"]}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);

        var payload = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(res.Payload));
        Assert.True(payload.TryGetProperty("bins", out var binsEl));
        // Both should be rejected (contain path separators)
        Assert.Equal(0, binsEl.EnumerateObject().Count());
    }

    [Fact]
    public async Task Which_ReturnsErrorWhenNoBins()
    {
        var cap = new SystemCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "w4",
            Command = "system.which",
            Args = Parse("""{"bins":[]}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
    }

    [Fact]
    public void ResolveExecutable_FindsCmdOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = SystemCapability.ResolveExecutable("cmd");
        Assert.NotNull(path);
        Assert.EndsWith(".exe", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveExecutable_RejectsPathTraversal()
    {
        Assert.Null(SystemCapability.ResolveExecutable("..\\cmd"));
        Assert.Null(SystemCapability.ResolveExecutable("../bin/sh"));
        Assert.Null(SystemCapability.ResolveExecutable("C:\\Windows\\cmd"));
    }

    private class FakeCommandRunner : ICommandRunner
    {
        public string Name => "fake";
        public CommandRequest? LastRequest { get; private set; }

        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new CommandResult
            {
                Stdout = "ok",
                Stderr = "",
                ExitCode = 0,
                TimedOut = false,
                DurationMs = 1
            });
        }
    }
}

public class CanvasCapabilityTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void CanHandle_AllCanvasCommands()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        Assert.True(cap.CanHandle("canvas.present"));
        Assert.True(cap.CanHandle("canvas.hide"));
        Assert.True(cap.CanHandle("canvas.navigate"));
        Assert.True(cap.CanHandle("canvas.eval"));
        Assert.True(cap.CanHandle("canvas.snapshot"));
        Assert.True(cap.CanHandle("canvas.a2ui.push"));
        Assert.True(cap.CanHandle("canvas.a2ui.reset"));
        Assert.False(cap.CanHandle("canvas.unknown"));
        Assert.Equal("canvas", cap.Category);
    }

    [Fact]
    public async Task Present_RaisesEvent_WithArgs()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        CanvasPresentArgs? received = null;
        cap.PresentRequested += (s, a) => received = a;

        var req = new NodeInvokeRequest
        {
            Id = "c1",
            Command = "canvas.present",
            Args = Parse("""{"url":"https://example.com","width":1024,"height":768,"title":"Test","alwaysOnTop":true}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(received);
        Assert.Equal("https://example.com", received!.Url);
        Assert.Equal(1024, received.Width);
        Assert.Equal(768, received.Height);
        Assert.Equal("Test", received.Title);
        Assert.True(received.AlwaysOnTop);
    }

    [Fact]
    public async Task Present_UsesDefaults_WhenArgsMissing()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        CanvasPresentArgs? received = null;
        cap.PresentRequested += (s, a) => received = a;

        var req = new NodeInvokeRequest
        {
            Id = "c2",
            Command = "canvas.present",
            Args = Parse("""{}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Equal(800, received!.Width);
        Assert.Equal(600, received.Height);
        Assert.Equal("Canvas", received.Title);
        Assert.False(received.AlwaysOnTop);
    }

    [Fact]
    public async Task Hide_RaisesEvent()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        bool hideRaised = false;
        cap.HideRequested += (s, e) => hideRaised = true;

        var req = new NodeInvokeRequest { Id = "c3", Command = "canvas.hide", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.True(hideRaised);
    }

    [Fact]
    public async Task Navigate_ReturnsError_WhenUrlMissing()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "c4", Command = "canvas.navigate", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("url", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Eval_AcceptsJavaScriptParam()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        string? evaledScript = null;
        cap.EvalRequested += (script) =>
        {
            evaledScript = script;
            return Task.FromResult("42");
        };

        var req = new NodeInvokeRequest
        {
            Id = "c5",
            Command = "canvas.eval",
            Args = Parse("""{"javaScript":"document.title"}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Equal("document.title", evaledScript);
    }

    [Fact]
    public async Task Eval_ReturnsError_WhenNoScript()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "c6", Command = "canvas.eval", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("script", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Eval_ReturnsError_WhenNoHandler()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "c7",
            Command = "canvas.eval",
            Args = Parse("""{"script":"test"}""")
        };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("not available", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Snapshot_ReturnsError_WhenNoHandler()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "c8", Command = "canvas.snapshot", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("not available", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A2UIPush_ReturnsError_WhenNoJsonl()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "c9", Command = "canvas.a2ui.push", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("jsonl", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A2UIPush_RaisesEvent_WithJsonl()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        CanvasA2UIArgs? received = null;
        cap.A2UIPushRequested += (s, a) => received = a;

        var req = new NodeInvokeRequest
        {
            Id = "c10",
            Command = "canvas.a2ui.push",
            Args = Parse("""{"jsonl":"{\"type\":\"text\"}"}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(received);
        Assert.Contains("text", received!.Jsonl);
    }

    [Fact]
    public async Task A2UIReset_RaisesEvent()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        bool resetRaised = false;
        cap.A2UIResetRequested += (s, e) => resetRaised = true;

        var req = new NodeInvokeRequest { Id = "c11", Command = "canvas.a2ui.reset", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.True(resetRaised);
    }

    [Fact]
    public async Task Navigate_RaisesEvent_WhenUrlPresent()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        string? navigatedUrl = null;
        cap.NavigateRequested += (s, url) => navigatedUrl = url;

        var req = new NodeInvokeRequest
        {
            Id = "c12",
            Command = "canvas.navigate",
            Args = Parse("""{"url":"https://example.com/page"}""")
        };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Equal("https://example.com/page", navigatedUrl);
    }

    [Fact]
    public async Task Eval_ReturnsError_WhenHandlerThrows()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        cap.EvalRequested += (script) => throw new InvalidOperationException("WebView2 not ready");

        var req = new NodeInvokeRequest
        {
            Id = "c13",
            Command = "canvas.eval",
            Args = Parse("""{"script":"document.title"}""")
        };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("WebView2 not ready", res.Error);
    }

    [Fact]
    public async Task Snapshot_CallsHandler_WithArgs()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        CanvasSnapshotArgs? receivedArgs = null;
        cap.SnapshotRequested += (args) =>
        {
            receivedArgs = args;
            return Task.FromResult("base64data");
        };

        var req = new NodeInvokeRequest
        {
            Id = "c14",
            Command = "canvas.snapshot",
            Args = Parse("""{"format":"jpeg","maxWidth":800,"quality":70}""")
        };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(receivedArgs);
        Assert.Equal("jpeg", receivedArgs!.Format);
        Assert.Equal(800, receivedArgs.MaxWidth);
        Assert.Equal(70, receivedArgs.Quality);
    }

    [Fact]
    public async Task Snapshot_ReturnsError_WhenHandlerThrows()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        cap.SnapshotRequested += (args) => throw new InvalidOperationException("Canvas not visible");

        var req = new NodeInvokeRequest { Id = "c15", Command = "canvas.snapshot", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("Canvas not visible", res.Error);
    }

    [Fact]
    public async Task A2UIPush_WithJsonlPath_ReadsFile()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        CanvasA2UIArgs? received = null;
        cap.A2UIPushRequested += (s, a) => received = a;

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, """{"type":"text","value":"hello"}""");
            var req = new NodeInvokeRequest
            {
                Id = "c16",
                Command = "canvas.a2ui.push",
                Args = Parse($$$"""{"jsonlPath":"{{{tmpFile.Replace("\\", "\\\\")}}}"}""")
            };
            var res = await cap.ExecuteAsync(req);
            Assert.True(res.Ok);
            Assert.NotNull(received);
            Assert.Contains("hello", received!.Jsonl);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public async Task A2UIPush_WithMissingJsonlPath_ReturnsError()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        // Use a path within the temp directory so path validation passes
        var missingFile = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.jsonl");
        var req = new NodeInvokeRequest
        {
            Id = "c17",
            Command = "canvas.a2ui.push",
            Args = Parse($"{{\"jsonlPath\":\"{missingFile.Replace("\\", "\\\\")}\"}}") 
        };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("Failed to read jsonlPath", res.Error);
    }
    
    [Fact]
    public async Task A2UIPush_WithJsonlPathOutsideTempDir_ReturnsError()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest
        {
            Id = "c18",
            Command = "canvas.a2ui.push",
            Args = Parse("""{"jsonlPath":"C:\\Windows\\System32\\config\\SAM"}""")
        };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("temp directory", res.Error);
    }
    
    [Fact]
    public async Task A2UIPush_WithJsonlPathTraversal_ReturnsError()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        // Path traversal attempt to escape temp directory
        var traversalPath = Path.Combine(Path.GetTempPath(), "..", "..", "Windows", "System32", "config", "SAM");
        var req = new NodeInvokeRequest
        {
            Id = "c19",
            Command = "canvas.a2ui.push",
            Args = Parse($"{{\"jsonlPath\":\"{traversalPath.Replace("\\", "\\\\")}\"}}") 
        };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("temp directory", res.Error);
    }
}

public class ScreenCapabilityTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void CanHandle_ScreenCommands()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        Assert.True(cap.CanHandle("screen.capture"));
        Assert.True(cap.CanHandle("screen.list"));
        Assert.False(cap.CanHandle("screen.record"));
        Assert.Equal("screen", cap.Category);
    }

    [Fact]
    public async Task Capture_ReturnsError_WhenNoHandler()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "s1", Command = "screen.capture", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("not available", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Capture_CallsHandler_WithArgs()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        ScreenCaptureArgs? receivedArgs = null;
        cap.CaptureRequested += (args) =>
        {
            receivedArgs = args;
            return Task.FromResult(new ScreenCaptureResult { Format = "png", Width = 1920, Height = 1080, Base64 = "abc" });
        };

        var req = new NodeInvokeRequest
        {
            Id = "s2",
            Command = "screen.capture",
            Args = Parse("""{"format":"jpeg","maxWidth":800,"quality":50,"screenIndex":1}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(receivedArgs);
        Assert.Equal("jpeg", receivedArgs!.Format);
        Assert.Equal(800, receivedArgs.MaxWidth);
        Assert.Equal(50, receivedArgs.Quality);
        Assert.Equal(1, receivedArgs.MonitorIndex);
    }

    [Fact]
    public async Task List_ReturnsError_WhenNoHandler()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "s3", Command = "screen.list", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.NotNull(res.Error);
        Assert.Contains("not available", res.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_ReturnsScreens_WhenHandler()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        cap.ListRequested += () => Task.FromResult(new[] 
        { 
            new ScreenInfo { Index = 0, Name = "Main", IsPrimary = true, Width = 2560, Height = 1440 } 
        });

        var req = new NodeInvokeRequest { Id = "s4", Command = "screen.list", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(res.Payload);
        
        // Verify payload contains expected screen data
        var json = System.Text.Json.JsonSerializer.Serialize(res.Payload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("screens", out var screensEl));
        Assert.Equal(System.Text.Json.JsonValueKind.Array, screensEl.ValueKind);
        Assert.Equal(1, screensEl.GetArrayLength());
        var screen = screensEl[0];
        Assert.Equal("Main", screen.GetProperty("name").GetString());
        Assert.True(screen.GetProperty("primary").GetBoolean());
        var bounds = screen.GetProperty("bounds");
        Assert.Equal(2560, bounds.GetProperty("width").GetInt32());
        Assert.Equal(1440, bounds.GetProperty("height").GetInt32());
    }

    [Fact]
    public async Task Capture_ReturnsError_WhenHandlerThrows()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        cap.CaptureRequested += (args) => throw new InvalidOperationException("Display access denied");

        var req = new NodeInvokeRequest { Id = "s5", Command = "screen.capture", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("Display access denied", res.Error);
    }

    [Fact]
    public async Task List_ReturnsError_WhenHandlerThrows()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        cap.ListRequested += () => throw new InvalidOperationException("Screen enumeration failed");

        var req = new NodeInvokeRequest { Id = "s6", Command = "screen.list", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("Screen enumeration failed", res.Error);
    }

    [Fact]
    public async Task Capture_ResponseIncludesDataUri()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        cap.CaptureRequested += (args) => Task.FromResult(new ScreenCaptureResult
        {
            Format = "png",
            Width = 1920,
            Height = 1080,
            Base64 = "abc123"
        });

        var req = new NodeInvokeRequest { Id = "s7", Command = "screen.capture", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);

        var json = JsonSerializer.Serialize(res.Payload);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("image", out var imageEl));
        Assert.StartsWith("data:image/png;base64,", imageEl.GetString());
        Assert.Contains("abc123", imageEl.GetString());
    }

    [Fact]
    public async Task Capture_UsesMonitorAlias_ForScreenIndex()
    {
        var cap = new ScreenCapability(NullLogger.Instance);
        ScreenCaptureArgs? receivedArgs = null;
        cap.CaptureRequested += (args) =>
        {
            receivedArgs = args;
            return Task.FromResult(new ScreenCaptureResult { Format = "png", Width = 1920, Height = 1080, Base64 = "" });
        };

        // "monitor" is an alias for "screenIndex"
        var req = new NodeInvokeRequest
        {
            Id = "s8",
            Command = "screen.capture",
            Args = Parse("""{"monitor":2}""")
        };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(receivedArgs);
        Assert.Equal(2, receivedArgs!.MonitorIndex);
    }
}

public class CameraCapabilityTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void CanHandle_CameraCommands()
    {
        var cap = new CameraCapability(NullLogger.Instance);
        Assert.True(cap.CanHandle("camera.list"));
        Assert.True(cap.CanHandle("camera.snap"));
        Assert.False(cap.CanHandle("camera.clip"));
        Assert.Equal("camera", cap.Category);
    }

    [Fact]
    public async Task List_ReturnsError_WhenNoHandler()
    {
        var cap = new CameraCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "cam1", Command = "camera.list", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.NotNull(res.Error);
        Assert.Contains("not available", res.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task List_ReturnsCameras_WhenHandler()
    {
        var cap = new CameraCapability(NullLogger.Instance);
        cap.ListRequested += () => Task.FromResult(new[]
        {
            new CameraInfo { DeviceId = "cam-1", Name = "Front", IsDefault = true },
            new CameraInfo { DeviceId = "cam-2", Name = "Back", IsDefault = false }
        });

        var req = new NodeInvokeRequest { Id = "cam2", Command = "camera.list", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(res.Payload);
        
        // Verify payload contains expected camera entries
        var json = System.Text.Json.JsonSerializer.Serialize(res.Payload);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("cameras", out var camerasEl));
        Assert.Equal(System.Text.Json.JsonValueKind.Array, camerasEl.ValueKind);
        Assert.Equal(2, camerasEl.GetArrayLength());
        Assert.Equal("cam-1", camerasEl[0].GetProperty("DeviceId").GetString());
        Assert.Equal("Front", camerasEl[0].GetProperty("Name").GetString());
        Assert.True(camerasEl[0].GetProperty("IsDefault").GetBoolean());
        Assert.Equal("cam-2", camerasEl[1].GetProperty("DeviceId").GetString());
        Assert.False(camerasEl[1].GetProperty("IsDefault").GetBoolean());
    }

    [Fact]
    public async Task Snap_ReturnsError_WhenNoHandler()
    {
        var cap = new CameraCapability(NullLogger.Instance);
        var req = new NodeInvokeRequest { Id = "cam3", Command = "camera.snap", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.NotNull(res.Error);
        Assert.Contains("not available", res.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Snap_CallsHandler_WithArgs()
    {
        var cap = new CameraCapability(NullLogger.Instance);
        CameraSnapArgs? receivedArgs = null;
        cap.SnapRequested += (args) =>
        {
            receivedArgs = args;
            return Task.FromResult(new CameraSnapResult { Format = "jpeg", Width = 640, Height = 480, Base64 = "img" });
        };

        var req = new NodeInvokeRequest
        {
            Id = "cam4",
            Command = "camera.snap",
            Args = Parse("""{"deviceId":"cam-1","format":"png","maxWidth":320,"quality":50}""")
        };

        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.NotNull(receivedArgs);
        Assert.Equal("cam-1", receivedArgs!.DeviceId);
        Assert.Equal("png", receivedArgs.Format);
        Assert.Equal(320, receivedArgs.MaxWidth);
        Assert.Equal(50, receivedArgs.Quality);
    }

    [Fact]
    public async Task Snap_UsesDefaults_WhenArgsMissing()
    {
        var cap = new CameraCapability(NullLogger.Instance);
        CameraSnapArgs? receivedArgs = null;
        cap.SnapRequested += (args) =>
        {
            receivedArgs = args;
            return Task.FromResult(new CameraSnapResult { Format = "jpeg", Width = 640, Height = 480, Base64 = "img" });
        };

        var req = new NodeInvokeRequest { Id = "cam5", Command = "camera.snap", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.True(res.Ok);
        Assert.Null(receivedArgs!.DeviceId);
        Assert.Equal("jpeg", receivedArgs.Format);
        Assert.Equal(1280, receivedArgs.MaxWidth);
        Assert.Equal(80, receivedArgs.Quality);
    }

    [Fact]
    public async Task Snap_ReturnsError_WhenHandlerThrows()
    {
        var cap = new CameraCapability(NullLogger.Instance);
        cap.SnapRequested += (args) => throw new InvalidOperationException("Camera access blocked");

        var req = new NodeInvokeRequest { Id = "cam6", Command = "camera.snap", Args = Parse("""{}""") };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("Camera access blocked", res.Error);
    }
}

public class VoiceCapabilityTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void CanHandle_VoiceCommands()
    {
        var cap = new VoiceCapability(NullLogger.Instance);
        Assert.True(cap.CanHandle(VoiceCommands.ListDevices));
        Assert.True(cap.CanHandle(VoiceCommands.GetSettings));
        Assert.True(cap.CanHandle(VoiceCommands.SetSettings));
        Assert.True(cap.CanHandle(VoiceCommands.GetStatus));
        Assert.True(cap.CanHandle(VoiceCommands.Start));
        Assert.True(cap.CanHandle(VoiceCommands.Stop));
        Assert.False(cap.CanHandle("voice.unknown"));
        Assert.Equal("voice", cap.Category);
    }

    [Fact]
    public async Task ListDevices_ReturnsArrayFromHandler()
    {
        var cap = new VoiceCapability(NullLogger.Instance);
        cap.ListDevicesRequested += () => Task.FromResult<VoiceAudioDeviceInfo[]>(
        [
            new VoiceAudioDeviceInfo
            {
                DeviceId = "default-input",
                Name = "System default microphone",
                IsDefault = true,
                IsInput = true
            }
        ]);

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "voice1",
            Command = VoiceCommands.ListDevices,
            Args = Parse("""{}""")
        });

        Assert.True(res.Ok);
        var json = JsonSerializer.Serialize(res.Payload);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal("default-input", doc.RootElement[0].GetProperty("DeviceId").GetString());
    }

    [Fact]
    public async Task GetSettings_ReturnsSettingsFromHandler()
    {
        var cap = new VoiceCapability(NullLogger.Instance);
        cap.SettingsRequested += () => Task.FromResult(new VoiceSettings
        {
            Enabled = true,
            Mode = VoiceActivationMode.VoiceWake
        });

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "voice2",
            Command = VoiceCommands.GetSettings,
            Args = Parse("""{}""")
        });

        Assert.True(res.Ok);
        var json = JsonSerializer.Serialize(res.Payload);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("Enabled").GetBoolean());
        Assert.Equal("VoiceWake", doc.RootElement.GetProperty("Mode").GetString());
    }

    [Fact]
    public async Task SetSettings_UsesUpdateEnvelope_WhenPresent()
    {
        var cap = new VoiceCapability(NullLogger.Instance);
        VoiceSettingsUpdateArgs? received = null;
        cap.SettingsUpdateRequested += update =>
        {
            received = update;
            return Task.FromResult(update.Settings);
        };

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "voice3",
            Command = VoiceCommands.SetSettings,
            Args = Parse("""{"update":{"persist":false,"settings":{"enabled":true,"mode":"TalkMode"}}}""")
        });

        Assert.True(res.Ok);
        Assert.NotNull(received);
        Assert.False(received!.Persist);
        Assert.Equal(VoiceActivationMode.TalkMode, received.Settings.Mode);
    }

    [Fact]
    public async Task GetStatus_ReturnsStatusFromHandler()
    {
        var cap = new VoiceCapability(NullLogger.Instance);
        cap.StatusRequested += () => Task.FromResult(new VoiceStatusInfo
        {
            Available = true,
            Running = true,
            Mode = VoiceActivationMode.TalkMode,
            State = VoiceRuntimeState.ListeningContinuously
        });

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "voice4",
            Command = VoiceCommands.GetStatus,
            Args = Parse("""{}""")
        });

        Assert.True(res.Ok);
        var json = JsonSerializer.Serialize(res.Payload);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("Running").GetBoolean());
        Assert.Equal("ListeningContinuously", doc.RootElement.GetProperty("State").GetString());
    }

    [Fact]
    public async Task Start_PassesArgsToHandler()
    {
        var cap = new VoiceCapability(NullLogger.Instance);
        VoiceStartArgs? received = null;
        cap.StartRequested += args =>
        {
            received = args;
            return Task.FromResult(new VoiceStatusInfo
            {
                Available = true,
                Running = true,
                Mode = args.Mode ?? VoiceActivationMode.Off,
                State = VoiceRuntimeState.ListeningForVoiceWake,
                SessionKey = args.SessionKey
            });
        };

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "voice5",
            Command = VoiceCommands.Start,
            Args = Parse("""{"mode":"VoiceWake","sessionKey":"session-123"}""")
        });

        Assert.True(res.Ok);
        Assert.NotNull(received);
        Assert.Equal(VoiceActivationMode.VoiceWake, received!.Mode);
        Assert.Equal("session-123", received.SessionKey);
    }

    [Fact]
    public async Task Stop_PassesReasonToHandler()
    {
        var cap = new VoiceCapability(NullLogger.Instance);
        VoiceStopArgs? received = null;
        cap.StopRequested += args =>
        {
            received = args;
            return Task.FromResult(new VoiceStatusInfo
            {
                Available = true,
                Running = false,
                Mode = VoiceActivationMode.Off,
                State = VoiceRuntimeState.Stopped,
                LastError = args.Reason
            });
        };

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "voice6",
            Command = VoiceCommands.Stop,
            Args = Parse("""{"reason":"user requested"}""")
        });

        Assert.True(res.Ok);
        Assert.NotNull(received);
        Assert.Equal("user requested", received!.Reason);
    }

    [Fact]
    public async Task Start_ReturnsError_WhenHandlerMissing()
    {
        var cap = new VoiceCapability(NullLogger.Instance);
        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "voice7",
            Command = VoiceCommands.Start,
            Args = Parse("""{}""")
        });

        Assert.False(res.Ok);
        Assert.Contains("not available", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LegacyVoiceSkipCommand_RemainsAccepted()
    {
        var cap = new VoiceCapability(NullLogger.Instance);
        VoiceSkipArgs? received = null;
        cap.SkipRequested += args =>
        {
            received = args;
            return Task.FromResult(new VoiceStatusInfo
            {
                Available = true,
                Running = true,
                Mode = VoiceActivationMode.TalkMode,
                State = VoiceRuntimeState.PlayingResponse
            });
        };

        var res = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Id = "voice8",
            Command = "voice.skip",
            Args = Parse("""{"reason":"legacy caller"}""")
        });

        Assert.True(res.Ok);
        Assert.NotNull(received);
        Assert.Equal("legacy caller", received!.Reason);
    }
}
