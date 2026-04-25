using System.Collections.Generic;
using System.Text.Json;
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
}
