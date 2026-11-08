using OpenClaw.SetupEngine;
using OpenClaw.SetupEngine.UI;

namespace OpenClaw.Tray.UITests;

public sealed class NativeGatewayStagedPackageRegistrarTests
{
    // Windows reports an unstaged family as the generic 0x80073CF9 with extended error 0x80070490.
    // This exercises the real deployment API, so a projection that hides the extended code would turn
    // a still-running Store deployment (#1756) into an immediate setup failure.
    [Fact]
    public async Task UnstagedFamily_IsNotStagedRatherThanAFailure()
    {
        var registrar = new NativeGatewayStagedPackageRegistrar("OpenClawFoundation.OpenClawGatewayProbe_rfcbke2p71se2");

        var result = await registrar.RegisterAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(1));

        Assert.Equal(NativeGatewayStagedRegistrationResult.NotStaged, result);
    }
}
