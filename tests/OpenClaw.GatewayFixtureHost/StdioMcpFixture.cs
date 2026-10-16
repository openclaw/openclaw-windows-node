using System.Text.Json;

namespace OpenClaw.GatewayFixtureHost;

/// <summary>Protocol-shaped subprocess for timeout/cleanup tests; never launches the app.</summary>
internal static class StdioMcpFixture
{
    internal static async Task<int> RunAsync(string mode, string identityPath)
    {
        await File.WriteAllTextAsync(identityPath, JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId, startUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()
        }));
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var request = JsonDocument.Parse(line);
            var root = request.RootElement;
            if (!root.TryGetProperty("id", out var id)) continue;
            var method = root.GetProperty("method").GetString();
            if (mode == "exit") return 42;
            if (mode == "hang-initialize" || (mode == "hang-tool" && method == "tools/call"))
            {
                await Task.Delay(TimeSpan.FromSeconds(60));
                continue;
            }
            object result = method switch
            {
                "initialize" => new
                {
                    protocolVersion = root.GetProperty("params").GetProperty("protocolVersion").GetString(),
                    capabilities = new { tools = new { } }, serverInfo = new { name = "owned-fixture", version = "fixture" }
                },
                "tools/list" => new { tools = Array.Empty<object>() },
                _ => new { content = new[] { new { type = "text", text = "{\"ok\":true}" } },
                    structuredContent = new { ok = true } }
            };
            Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.Clone(), result }));
            await Console.Out.FlushAsync();
        }
        return 0;
    }
}
