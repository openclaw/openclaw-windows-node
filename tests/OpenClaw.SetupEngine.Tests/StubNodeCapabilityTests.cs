using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;

namespace OpenClaw.SetupEngine.Tests;

public class StubNodeCapabilityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PairingSurface_MatchesRuntimeProtocolFeatures_WithoutExecuting(bool includeRun)
    {
        var runtime = new SystemCapability(NullLogger.Instance, includeRunCommands: includeRun);
        var stub = new StubNodeCapability("system", runtime.Commands.ToArray());

        Assert.Equal(runtime.ProtocolCapabilities, stub.ProtocolCapabilities);
        Assert.Equal(includeRun ? new[] { "system.run.execution-context.v1" } : Array.Empty<string>(),
            stub.ProtocolCapabilities);
        Assert.False(stub.CanHandle("system.run.execution-context.v1"));
        var response = await stub.ExecuteAsync(new NodeInvokeRequest { Command = "system.run" });
        Assert.False(response.Ok);
        Assert.Contains("Setup stub", response.Error);
    }

    [Fact]
    public void OtherCategory_DoesNotClaimSystemProtocolFeatures()
    {
        var stub = new StubNodeCapability("camera", ["camera.snap"]);
        Assert.Empty(stub.ProtocolCapabilities);
    }
}
