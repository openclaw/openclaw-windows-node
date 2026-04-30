using System.Text;
using System.Text.Json;
using OpenClaw.WinNode.Cli;

namespace OpenClaw.WinNode.Cli.Tests;

public class BuildToolsCallBodyTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static string ToString((byte[] Buffer, int Length) result)
        => Encoding.UTF8.GetString(result.Buffer, 0, result.Length);

    [Fact]
    public void Produces_jsonrpc_envelope_with_tools_call_method()
    {
        var (buf, len) = CliRunner.BuildToolsCallBody("system.which", Args("{\"bins\":[\"git\"]}"));
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buf, 0, len));
        var root = doc.RootElement;

        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, root.GetProperty("id").GetInt32());
        Assert.Equal("tools/call", root.GetProperty("method").GetString());

        var p = root.GetProperty("params");
        Assert.Equal("system.which", p.GetProperty("name").GetString());
        var args = p.GetProperty("arguments");
        Assert.Equal(JsonValueKind.Array, args.GetProperty("bins").ValueKind);
        Assert.Equal("git", args.GetProperty("bins")[0].GetString());
    }

    [Fact]
    public void Empty_object_args_round_trip()
    {
        var body = ToString(CliRunner.BuildToolsCallBody("screen.snapshot", Args("{}")));
        using var doc = JsonDocument.Parse(body);
        var args = doc.RootElement.GetProperty("params").GetProperty("arguments");
        Assert.Equal(JsonValueKind.Object, args.ValueKind);
        Assert.Empty(args.EnumerateObject());
    }

    [Fact]
    public void Nested_args_preserve_structure()
    {
        var json = "{\"a\":{\"b\":[1,2,{\"c\":\"d\"}]},\"e\":true,\"f\":null}";
        var body = ToString(CliRunner.BuildToolsCallBody("x.y", Args(json)));
        using var doc = JsonDocument.Parse(body);
        var args = doc.RootElement.GetProperty("params").GetProperty("arguments");
        Assert.Equal("d", args.GetProperty("a").GetProperty("b")[2].GetProperty("c").GetString());
        Assert.True(args.GetProperty("e").GetBoolean());
        Assert.Equal(JsonValueKind.Null, args.GetProperty("f").ValueKind);
    }

    [Fact]
    public void Length_matches_actual_payload_size()
    {
        // F-14: the buffer is the underlying MemoryStream array (oversized);
        // only Length bytes are valid. Ensure consumers honor that.
        var (buf, len) = CliRunner.BuildToolsCallBody("x", Args("{}"));
        Assert.True(len > 0);
        Assert.True(buf.Length >= len);
        var s = Encoding.UTF8.GetString(buf, 0, len);
        Assert.EndsWith("}", s);
    }
}
