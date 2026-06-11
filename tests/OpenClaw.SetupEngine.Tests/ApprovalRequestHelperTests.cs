namespace OpenClaw.SetupEngine.Tests;

public class ApprovalRequestHelperTests
{
    [Theory]
    [InlineData("req-123")]
    [InlineData("node:abc_123.4")]
    public void IsSafeRequestId_AcceptsExpectedIds(string requestId)
    {
        Assert.True(ApprovalRequestHelper.IsSafeRequestId(requestId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-starts-with-dash")]
    [InlineData("bad;rm -rf")]
    [InlineData("bad id")]
    public void IsSafeRequestId_RejectsUnsafeIds(string requestId)
    {
        Assert.False(ApprovalRequestHelper.IsSafeRequestId(requestId));
    }

    [Fact]
    public void ApprovalCommand_UsesNodeApprovalSurface()
    {
        var command = ApprovalRequestHelper.ApprovalCommand(ApprovalRequestKind.Node);

        Assert.Contains("openclaw nodes approve", command);
        Assert.DoesNotContain("devices approve", command);
        Assert.Contains("$OPENCLAW_APPROVAL_REQUEST_ID", command);
    }

    [Fact]
    public void TryReadSinglePendingRequestId_ReturnsOnlySafePendingRequest()
    {
        var result = ApprovalRequestHelper.TryReadSinglePendingRequestId("""
        {"pending":[{"requestId":"node-req-1"}]}
        """);

        Assert.True(result.Success);
        Assert.Equal("node-req-1", result.RequestId);
    }

    [Fact]
    public void TryReadSinglePendingRequestId_RejectsAmbiguousRequests()
    {
        var result = ApprovalRequestHelper.TryReadSinglePendingRequestId("""
        {"pending":[{"requestId":"node-req-1"},{"requestId":"node-req-2"}]}
        """);

        Assert.False(result.Success);
        Assert.Contains("Multiple", result.Error);
    }

    [Fact]
    public void TryReadPendingRequestIds_RejectsUnsafeRequestId()
    {
        var result = ApprovalRequestHelper.TryReadPendingRequestIds("""
        {"pending":[{"requestId":"bad;id"}]}
        """);

        Assert.False(result.Success);
        Assert.Contains("unsafe", result.Error);
    }

    [Fact]
    public void TryReadSelectedRequestId_ReadsRequestWhenCliRequiresExplicitAuthFlags()
    {
        var result = ApprovalRequestHelper.TryReadSelectedRequestId("""
        {
          "selected": {
            "requestId": "device-req-1",
            "role": "operator"
          },
          "approveCommand": "openclaw devices approve device-req-1 --json",
          "requiresAuthFlags": {
            "token": true,
            "password": false
          }
        }
        """);

        Assert.True(result.Success);
        Assert.Equal("device-req-1", result.RequestId);
    }

    [Fact]
    public void TryReadApprovedRequestId_ReadsApproveSuccessShape()
    {
        var result = ApprovalRequestHelper.TryReadApprovedRequestId("""
        {"requestId":"device-req-2","device":{"role":"operator"}}
        """);

        Assert.True(result.Success);
        Assert.Equal("device-req-2", result.RequestId);
    }

    [Theory]
    [InlineData("plugin not found: device-pair")]
    [InlineData("plugins.entries.device-pair: plugin not found: device-pair")]
    [InlineData("error: Plugin not found")]
    public void IsPluginNotFoundError_ReturnsTrueForPluginNotFoundOutput(string output)
    {
        Assert.True(ApprovalRequestHelper.IsPluginNotFoundError(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("approval failed: unknown error")]
    [InlineData("gateway connection refused")]
    public void IsPluginNotFoundError_ReturnsFalseForOtherOutput(string output)
    {
        Assert.False(ApprovalRequestHelper.IsPluginNotFoundError(output));
    }
}
