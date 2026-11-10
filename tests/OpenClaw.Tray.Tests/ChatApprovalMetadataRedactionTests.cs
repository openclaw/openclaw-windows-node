// Approval banner title and host are untrusted reviewer text, just like command and message.
// Run with --filter FullyQualifiedName~ChatApprovalMetadataRedactionTests.
using System.Text.Json;
using OpenClaw.Chat;
using OpenClaw.Shared;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.Tests;

public sealed class ChatApprovalMetadataRedactionTests
{
    [Theory]
    [InlineData("title")]
    [InlineData("host")]
    public void SecretInMetadataIsRedactedAndCannotPermit(string field)
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            phase = "requested", approvalId = "fixture", command = "echo hi",
            title = field == "title" ? "Authori\u200Bzation: Bearer s3cret42" : "Review",
            host = field == "host" ? "Authorization: Bearer s3cret42" : "gateway",
        });
        var mapping = ChatEventMapper.Map(new AgentEventInfo { Stream = "approval", SessionKey = "main", Data = data });
        var request = Assert.IsType<ChatPermissionRequestEvent>(mapping.Event);
        Assert.DoesNotContain("s3cret42", request.PermissionKind);
        Assert.DoesNotContain("s3cret42", request.ToolName);
        Assert.DoesNotContain("s3cret42", request.Detail);
        Assert.Equal(["deny"], request.Actions);
    }
}
