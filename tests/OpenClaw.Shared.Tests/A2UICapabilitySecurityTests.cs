using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Boundary-layer enforcement for the unified review's C5 (size caps) and M5
/// (symlink-resolved temp validation). These run against the capability
/// directly with no UI dependency.
/// </summary>
public class A2UICapabilitySecurityTests
{
    private static System.Text.Json.JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task A2UIPush_InlineJsonl_ExceedsByteCap_ReturnsError()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        // Build a payload just over the 4 MiB cap. Use ASCII so byte count == char count.
        var big = new string('a', (int)CanvasCapability.MaxA2UIJsonlBytes + 16);
        var req = new NodeInvokeRequest
        {
            Id = "sec1",
            Command = "canvas.a2ui.push",
            Args = Parse($"{{\"jsonl\":{JsonSerializer.Serialize(big)}}}"),
        };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("maximum size", res.Error);
    }

    [Fact]
    public async Task A2UIPush_InlineJsonl_ExceedsLineCap_ReturnsError()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        // One short line per row, well past the line cap, well under the byte cap.
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < CanvasCapability.MaxA2UIJsonlLines + 16; i++)
            sb.Append("{}\n");
        var req = new NodeInvokeRequest
        {
            Id = "sec2",
            Command = "canvas.a2ui.push",
            Args = Parse($"{{\"jsonl\":{JsonSerializer.Serialize(sb.ToString())}}}"),
        };
        var res = await cap.ExecuteAsync(req);
        Assert.False(res.Ok);
        Assert.Contains("maximum of", res.Error);
    }

    [Fact]
    public async Task A2UIPush_FileJsonl_OverCap_ReturnsError()
    {
        var cap = new CanvasCapability(NullLogger.Instance,
            async (path, _, stop) => await File.ReadAllBytesAsync(path, stop));
        var tmpFile = Path.Combine(Path.GetTempPath(), $"a2ui-oversize-{System.Guid.NewGuid():N}.jsonl");
        try
        {
            // 4 MiB + 1 — just over the cap. Use FileStream + SetLength to avoid
            // allocating a huge string in memory just for the test.
            using (var fs = File.Create(tmpFile))
            {
                fs.SetLength(CanvasCapability.MaxA2UIJsonlBytes + 1);
            }
            var req = new NodeInvokeRequest
            {
                Id = "sec3",
                Command = "canvas.a2ui.push",
                Args = Parse($"{{\"jsonlPath\":{JsonSerializer.Serialize(tmpFile)}}}"),
            };
            var res = await cap.ExecuteAsync(req);
            Assert.False(res.Ok);
            Assert.Equal("Failed to read jsonlPath", res.Error);
        }
        finally
        {
            // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
            try { File.Delete(tmpFile); } catch { }
        }
    }

    [Fact]
    public async Task A2UIPush_JsonlPathWithoutSandboxReader_IsUnavailable()
    {
        var cap = new CanvasCapability(NullLogger.Instance);
        var result = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Command = "canvas.a2ui.push",
            Args = Parse(JsonSerializer.Serialize(new { jsonlPath = Path.Combine(Path.GetTempPath(), "unavailable.jsonl") }))
        });
        Assert.False(result.Ok);
        Assert.Equal("Failed to read jsonlPath", result.Error);
    }

    [Fact]
    public async Task A2UIPush_SandboxDenialDoesNotReachTheRenderer()
    {
        var cap = new CanvasCapability(NullLogger.Instance, (_, _, _) =>
            Task.FromException<byte[]?>(new OpenClaw.Shared.FileTransfer.FileCommandException(
                "PERMISSION_DENIED", "The shared policy denies this folder.")));
        var rendered = false;
        cap.A2UIPushRequested += (_, _) => rendered = true;
        var result = await cap.ExecuteAsync(new NodeInvokeRequest
        {
            Command = "canvas.a2ui.push",
            Args = Parse(JsonSerializer.Serialize(new { jsonlPath = Path.Combine(Path.GetTempPath(), "denied.jsonl") }))
        });
        Assert.False(result.Ok);
        Assert.False(rendered);
    }
}
