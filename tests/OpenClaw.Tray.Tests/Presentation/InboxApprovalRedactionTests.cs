// Approval presentation must redact original text before deriving full text, compact previews or labels.
using OpenClaw.Shared;
using OpenClawTray.Presentation;

namespace OpenClaw.Tray.Tests.Presentation;

public sealed class InboxApprovalRedactionTests
{
    [Theory]
    [InlineData("Authorization: Bearer s3cret42")]
    [InlineData("Autho\u200Brization: Bearer s3cret42")]
    [InlineData("Autho\uE000rization: Bearer s3cret42")]
    [InlineData("curl https://example.test/run?token=s3cret42")]
    public void FullPreviewAndProjectedLabelNeverExposeTheSecret(string command)
    {
        var approval = new GatewayPendingApproval("a", GatewayApprovalKind.Exec, command, 1, 60001);
        var item = InboxGatewayItems.FromApproval(approval, DateTimeOffset.UtcNow.AddMinutes(1));
        foreach (var text in new[] { InboxGatewayItems.DisplayCommand(command), InboxGatewayItems.CompactCommand(command), item.Label })
        {
            Assert.DoesNotContain("s3cret42", text);
            Assert.Contains("***", text);
        }
        Assert.Same(approval, item.Approval);
        Assert.Equal(command, item.Approval!.Command); // Display redaction must not rewrite the observed request.
    }

    [Fact]
    public void CompactPreviewRedactsBeforeItsLengthCap()
    {
        var text = "Authorization: Bearer " + "s3cret42" + new string('z', 80);
        var preview = InboxGatewayItems.CompactCommand(text);
        Assert.DoesNotContain("s3cret42", preview);
        Assert.True(preview.Length <= 64);
    }
}
