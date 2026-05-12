using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class PairingApprovalCommandTests
{
    [Fact]
    public void Build_UsesRequestId()
    {
        var command = PairingApprovalCommand.Build("bd8ad7b1-4a57-48cc-93a4-36c8626c87e9");

        Assert.Equal("openclaw devices approve bd8ad7b1-4a57-48cc-93a4-36c8626c87e9", command);
    }

    [Fact]
    public void Build_WithoutRequestId_FallsBackToList()
    {
        Assert.Equal("openclaw devices list", PairingApprovalCommand.Build(null));
        Assert.Equal("openclaw devices list", PairingApprovalCommand.Build("   "));
    }
}
