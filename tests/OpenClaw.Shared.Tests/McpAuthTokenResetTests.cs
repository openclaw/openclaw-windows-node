using OpenClaw.Shared.Mcp;

namespace OpenClaw.Shared.Tests;

public class McpAuthTokenResetTests
{
    [Fact]
    public void Reset_ReplacesExistingToken()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openclaw-token-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "mcp-token.txt");
        try
        {
            var first = McpAuthToken.LoadOrCreate(path);
            var second = McpAuthToken.Reset(path);

            Assert.NotEqual(first, second);
            Assert.Equal(second, File.ReadAllText(path).Trim());
            Assert.Equal(43, second.Length);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Reset_RejectsMissingPath()
    {
        Assert.Throws<ArgumentException>(() => McpAuthToken.Reset(""));
    }
}
