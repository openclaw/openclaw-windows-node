using OpenClaw.Connection;
namespace OpenClaw.E2ETests.Setup;
public sealed class BrowserPrototypeTransportTests
{
    [Fact]
    public void StdinNormalizesWindowsRawStringLineEndings()
    {
        Assert.Equal("set -euo pipefail\nprintf ok\n", BrowserWslOwnerPrototypeTests.NormalizeInput("set -euo pipefail\r\nprintf ok\r\n"));
        Assert.DoesNotContain("\r", BrowserWslOwnerPrototypeTests.NormalizeInput(BrowserBootstrapWslCommand.Script.Replace("\n", "\r\n")));
    }
}
