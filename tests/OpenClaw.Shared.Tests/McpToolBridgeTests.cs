using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Mcp;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class McpToolBridgeTests
{
    private sealed class FakeCapability : INodeCapability
    {
        public string Category { get; }
        public IReadOnlyList<string> Commands { get; }
        public Func<NodeInvokeRequest, Task<NodeInvokeResponse>>? OnExecute;

        public FakeCapability(string category, params string[] commands)
        {
            Category = category;
            Commands = commands;
        }

        public bool CanHandle(string command) => System.Linq.Enumerable.Contains(Commands, command);

        public Task<NodeInvokeResponse> ExecuteAsync(NodeInvokeRequest request)
            => OnExecute?.Invoke(request)
               ?? Task.FromResult(new NodeInvokeResponse { Ok = true, Payload = new { echoed = request.Command } });
    }

    private static McpToolBridge CreateBridge(IReadOnlyList<INodeCapability> caps)
        => new(() => caps);

    [Fact]
    public async Task Initialize_ReturnsProtocolAndServerInfo()
    {
        var bridge = CreateBridge(new List<INodeCapability>());
        var resp = await bridge.HandleRequestAsync(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize""}");

        Assert.NotNull(resp);
        using var doc = JsonDocument.Parse(resp!);
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
        Assert.True(result.TryGetProperty("capabilities", out _));
        Assert.True(result.TryGetProperty("serverInfo", out _));
    }

    [Fact]
    public async Task ToolsList_FlattensCommandsAcrossCapabilities()
    {
        var caps = new List<INodeCapability>
        {
            new FakeCapability("alpha", "alpha.one", "alpha.two"),
            new FakeCapability("beta", "beta.x"),
        };
        var bridge = CreateBridge(caps);
        var resp = await bridge.HandleRequestAsync(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/list""}");

        using var doc = JsonDocument.Parse(resp!);
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools");
        Assert.Equal(3, tools.GetArrayLength());
        var names = new List<string>();
        foreach (var t in tools.EnumerateArray())
            names.Add(t.GetProperty("name").GetString()!);
        Assert.Contains("alpha.one", names);
        Assert.Contains("alpha.two", names);
        Assert.Contains("beta.x", names);
    }

    [Fact]
    public async Task ToolsList_KnownCommands_GetCuratedDescriptions()
    {
        var caps = new List<INodeCapability>
        {
            new FakeCapability("system", "system.notify"),
            new FakeCapability("canvas", "canvas.a2ui.push"),
            new FakeCapability("screen", "screen.snapshot"),
            new FakeCapability("camera", "camera.snap"),
            new FakeCapability("tts", "tts.speak"),
            new FakeCapability("custom", "custom.unknown"),
        };
        var bridge = CreateBridge(caps);
        var resp = await bridge.HandleRequestAsync(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/list""}");

        using var doc = JsonDocument.Parse(resp!);
        var byName = new Dictionary<string, string>();
        foreach (var t in doc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray())
        {
            byName[t.GetProperty("name").GetString()!] = t.GetProperty("description").GetString()!;
        }

        // Curated descriptions should be specific, not the generic "{category} capability: {cmd}" stub.
        Assert.Contains("toast notification", byName["system.notify"]);
        Assert.Contains("A2UI v0.8", byName["canvas.a2ui.push"]);
        Assert.Contains("screenshot", byName["screen.snapshot"]);
        Assert.Contains("camera", byName["camera.snap"], System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Speak text", byName["tts.speak"]);

        // Unknown commands keep the generic fallback so newly-added capabilities still render.
        Assert.Equal("custom capability: custom.unknown", byName["custom.unknown"]);
    }

    [Fact]
    public async Task ToolsList_PicksUpNewCapabilityRegisteredAfterStart()
    {
        var caps = new List<INodeCapability>
        {
            new FakeCapability("alpha", "alpha.one"),
        };
        var bridge = CreateBridge(caps);

        // Simulate post-start registration — same pattern as RegisterCapability().
        caps.Add(new FakeCapability("gamma", "gamma.new"));

        var resp = await bridge.HandleRequestAsync(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/list""}");
        using var doc = JsonDocument.Parse(resp!);
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools");
        Assert.Equal(2, tools.GetArrayLength());
    }

    [Fact]
    public async Task ToolsCall_DispatchesToCapability_AndReturnsTextContent()
    {
        var fake = new FakeCapability("alpha", "alpha.echo")
        {
            OnExecute = req => Task.FromResult(new NodeInvokeResponse
            {
                Ok = true,
                Payload = new { hello = "world", n = 42 },
            }),
        };
        var bridge = CreateBridge(new List<INodeCapability> { fake });

        var resp = await bridge.HandleRequestAsync(
            @"{""jsonrpc"":""2.0"",""id"":7,""method"":""tools/call"",""params"":{""name"":""alpha.echo"",""arguments"":{""x"":1}}}");

        using var doc = JsonDocument.Parse(resp!);
        var result = doc.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        var content = result.GetProperty("content");
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        var text = content[0].GetProperty("text").GetString()!;
        // Payload is JSON-serialized as a string in the text content.
        using var payload = JsonDocument.Parse(text);
        Assert.Equal("world", payload.RootElement.GetProperty("hello").GetString());
        Assert.Equal(42, payload.RootElement.GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_ReturnsToolErrorNotJsonRpcError()
    {
        var bridge = CreateBridge(new List<INodeCapability>());
        var resp = await bridge.HandleRequestAsync(
            @"{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/call"",""params"":{""name"":""nope""}}");

        using var doc = JsonDocument.Parse(resp!);
        // MCP convention: tool failures come back as result.isError=true,
        // not JSON-RPC error. JSON-RPC errors are reserved for protocol issues.
        Assert.True(doc.RootElement.TryGetProperty("result", out var result));
        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task ToolsCall_CapabilityFailure_PropagatesAsToolError()
    {
        var fake = new FakeCapability("alpha", "alpha.fail")
        {
            OnExecute = _ => Task.FromResult(new NodeInvokeResponse { Ok = false, Error = "kaboom" }),
        };
        var bridge = CreateBridge(new List<INodeCapability> { fake });

        var resp = await bridge.HandleRequestAsync(
            @"{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/call"",""params"":{""name"":""alpha.fail""}}");

        using var doc = JsonDocument.Parse(resp!);
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("kaboom", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task UnknownMethod_ReturnsJsonRpcMethodNotFound()
    {
        var bridge = CreateBridge(new List<INodeCapability>());
        var resp = await bridge.HandleRequestAsync(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""nonsense""}");

        using var doc = JsonDocument.Parse(resp!);
        Assert.Equal(-32601, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Notification_ReturnsNullResponseBody()
    {
        var bridge = CreateBridge(new List<INodeCapability>());
        // No "id" → JSON-RPC notification → no response.
        var resp = await bridge.HandleRequestAsync(@"{""jsonrpc"":""2.0"",""method"":""notifications/initialized""}");
        Assert.Null(resp);
    }

    [Fact]
    public async Task GarbageInput_ReturnsParseError()
    {
        var bridge = CreateBridge(new List<INodeCapability>());
        var resp = await bridge.HandleRequestAsync("not json");
        using var doc = JsonDocument.Parse(resp!);
        Assert.Equal(-32700, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task NonObjectRoot_ReturnsInvalidRequest()
    {
        var bridge = CreateBridge(new List<INodeCapability>());
        var resp = await bridge.HandleRequestAsync("[1,2,3]");
        using var doc = JsonDocument.Parse(resp!);
        Assert.Equal(-32600, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ToolsCall_MissingParams_ReturnsToolError()
    {
        var bridge = CreateBridge(new List<INodeCapability>());
        var resp = await bridge.HandleRequestAsync(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/call""}");
        using var doc = JsonDocument.Parse(resp!);
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task ToolsCall_NameNotString_ReturnsToolError()
    {
        var bridge = CreateBridge(new List<INodeCapability>());
        var resp = await bridge.HandleRequestAsync(
            @"{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/call"",""params"":{""name"":42}}");
        using var doc = JsonDocument.Parse(resp!);
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task ToolsCall_ArgumentsNotObject_ReturnsToolError()
    {
        var fake = new FakeCapability("alpha", "alpha.echo");
        var bridge = CreateBridge(new List<INodeCapability> { fake });
        var resp = await bridge.HandleRequestAsync(
            @"{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/call"",""params"":{""name"":""alpha.echo"",""arguments"":[1,2,3]}}");
        using var doc = JsonDocument.Parse(resp!);
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task NumericId_RoundtripsRawValue()
    {
        // Non-integer numeric id used to throw on GetInt64; verify it's preserved.
        var bridge = CreateBridge(new List<INodeCapability>());
        var resp = await bridge.HandleRequestAsync(
            @"{""jsonrpc"":""2.0"",""id"":1.5,""method"":""ping""}");
        using var doc = JsonDocument.Parse(resp!);
        var id = doc.RootElement.GetProperty("id");
        Assert.Equal(JsonValueKind.Number, id.ValueKind);
        Assert.Equal(1.5, id.GetDouble());
    }

    [Fact]
    public async Task LargeNumericId_RoundtripsRawValue()
    {
        var bridge = CreateBridge(new List<INodeCapability>());
        var resp = await bridge.HandleRequestAsync(
            @"{""jsonrpc"":""2.0"",""id"":99999999999999999999,""method"":""ping""}");
        using var doc = JsonDocument.Parse(resp!);
        var id = doc.RootElement.GetProperty("id");
        Assert.Equal(JsonValueKind.Number, id.ValueKind);
        Assert.Equal("99999999999999999999", id.GetRawText());
    }

    [Fact]
    public async Task StringId_Roundtrips()
    {
        var bridge = CreateBridge(new List<INodeCapability>());
        var resp = await bridge.HandleRequestAsync(
            @"{""jsonrpc"":""2.0"",""id"":""abc-123"",""method"":""ping""}");
        using var doc = JsonDocument.Parse(resp!);
        Assert.Equal("abc-123", doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task ResourcesList_ReturnsEmptyForCompat()
    {
        // Cursor probes resources/list at startup; we want compat, not MethodNotFound.
        var bridge = CreateBridge(new List<INodeCapability>());
        var resp = await bridge.HandleRequestAsync(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""resources/list""}");
        using var doc = JsonDocument.Parse(resp!);
        var resources = doc.RootElement.GetProperty("result").GetProperty("resources");
        Assert.Equal(0, resources.GetArrayLength());
    }

    [Fact]
    public async Task PromptsList_ReturnsEmptyForCompat()
    {
        var bridge = CreateBridge(new List<INodeCapability>());
        var resp = await bridge.HandleRequestAsync(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""prompts/list""}");
        using var doc = JsonDocument.Parse(resp!);
        var prompts = doc.RootElement.GetProperty("result").GetProperty("prompts");
        Assert.Equal(0, prompts.GetArrayLength());
    }

    [Fact]
    public async Task ToolsCall_LongRunning_CancellationReturnsTimeoutToolError()
    {
        // CR-003: a tool that wedges past the request deadline must surface as
        // a tool error instead of pinning the handler. The bridge gives up
        // waiting once the CT fires; the underlying Task continues but is no
        // longer the caller's problem.
        var tcs = new TaskCompletionSource<NodeInvokeResponse>();
        var fake = new FakeCapability("alpha", "alpha.slow")
        {
            OnExecute = _ => tcs.Task,
        };
        var bridge = CreateBridge(new List<INodeCapability> { fake });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var resp = await bridge.HandleRequestAsync(
            @"{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/call"",""params"":{""name"":""alpha.slow""}}",
            cts.Token);

        // Unblock the dangling task so xunit doesn't complain about leaked work.
        tcs.TrySetResult(new NodeInvokeResponse { Ok = true });

        using var doc = JsonDocument.Parse(resp!);
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("timed out", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task UnhandledException_ReturnsGenericInternalError_NotLeakingMessage()
    {
        var fake = new FakeCapability("alpha", "alpha.boom")
        {
            OnExecute = _ => throw new InvalidOperationException("secret-internal-detail"),
        };
        var bridge = CreateBridge(new List<INodeCapability> { fake });

        var resp = await bridge.HandleRequestAsync(
            @"{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/call"",""params"":{""name"":""alpha.boom""}}");

        using var doc = JsonDocument.Parse(resp!);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal(-32603, error.GetProperty("code").GetInt32());
        Assert.DoesNotContain("secret-internal-detail", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Initialize_ReturnsCustomServerNameAndVersion()
    {
        var bridge = new McpToolBridge(
            () => new List<INodeCapability>(),
            serverName: "my-mcp-server",
            serverVersion: "1.2.3");

        var resp = await bridge.HandleRequestAsync(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize""}");

        using var doc = JsonDocument.Parse(resp!);
        var serverInfo = doc.RootElement.GetProperty("result").GetProperty("serverInfo");
        Assert.Equal("my-mcp-server", serverInfo.GetProperty("name").GetString());
        Assert.Equal("1.2.3", serverInfo.GetProperty("version").GetString());
    }

    [Fact]
    public async Task ToolsCall_NullArguments_IsAccepted()
    {
        var fake = new FakeCapability("alpha", "alpha.echo");
        var bridge = CreateBridge(new List<INodeCapability> { fake });

        var resp = await bridge.HandleRequestAsync(
            @"{""jsonrpc"":""2.0"",""id"":1,""method"":""tools/call"",""params"":{""name"":""alpha.echo"",""arguments"":null}}");

        using var doc = JsonDocument.Parse(resp!);
        Assert.True(doc.RootElement.TryGetProperty("result", out var result));
        Assert.False(result.GetProperty("isError").GetBoolean());
    }
}
