using OpenClaw.Shared;
using OpenClaw.Connection;

namespace OpenClaw.Connection.Tests;

public class CredentialResolverTests
{
    private readonly MockDeviceIdentityReader _mockReader = new();
    private readonly CredentialResolver _resolver;

    public CredentialResolverTests()
    {
        _resolver = new CredentialResolver(_mockReader);
    }

    // ─── Operator resolution ───

    [Fact]
    public void ResolveOperator_PrefersDeviceToken_OverSharedAndBootstrap()
    {
        var record = new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://test",
            SharedGatewayToken = "shared",
            BootstrapToken = "boot"
        };
        _mockReader.OperatorToken = "paired-tok";

        var result = _resolver.ResolveOperator(record, "/id");

        Assert.NotNull(result);
        Assert.Equal("paired-tok", result.Token);
        Assert.False(result.IsBootstrapToken);
        Assert.Equal(CredentialResolver.SourceDeviceToken, result.Source);
    }

    [Fact]
    public void ResolveOperator_FallsToSharedToken_WhenNoDeviceToken()
    {
        var record = new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://test",
            SharedGatewayToken = "shared",
            BootstrapToken = "boot"
        };
        _mockReader.OperatorToken = null;

        var result = _resolver.ResolveOperator(record, "/id");

        Assert.NotNull(result);
        Assert.Equal("shared", result.Token);
        Assert.False(result.IsBootstrapToken);
        Assert.Equal(CredentialResolver.SourceSharedGatewayToken, result.Source);
    }

    [Fact]
    public void ResolveOperator_FallsToBootstrap_WhenNoDeviceOrShared()
    {
        var record = new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://test",
            BootstrapToken = "boot"
        };
        _mockReader.OperatorToken = null;

        var result = _resolver.ResolveOperator(record, "/id");

        Assert.NotNull(result);
        Assert.Equal("boot", result.Token);
        Assert.True(result.IsBootstrapToken);
        Assert.Equal(CredentialResolver.SourceBootstrapToken, result.Source);
    }

    [Fact]
    public void ResolveOperator_ReturnsNull_WhenNoCredentials()
    {
        var record = new GatewayRecord { Id = "gw-1", Url = "wss://test" };
        _mockReader.OperatorToken = null;

        var result = _resolver.ResolveOperator(record, "/id");

        Assert.Null(result);
    }

    [Fact]
    public void ResolveOperator_NeverDowngradesPairedDevice()
    {
        // Even if bootstrap is present, paired device token wins
        var record = new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://test",
            BootstrapToken = "boot"
        };
        _mockReader.OperatorToken = "paired-tok";

        var result = _resolver.ResolveOperator(record, "/id");

        Assert.NotNull(result);
        Assert.Equal("paired-tok", result.Token);
        Assert.False(result.IsBootstrapToken);
    }

    // ─── Node resolution ───

    [Fact]
    public void ResolveNode_PrefersNodeDeviceToken_OverSharedAndBootstrap()
    {
        var record = new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://test",
            SharedGatewayToken = "shared",
            BootstrapToken = "boot"
        };
        _mockReader.NodeToken = "node-paired-tok";

        var result = _resolver.ResolveNode(record, "/id");

        Assert.NotNull(result);
        Assert.Equal("node-paired-tok", result.Token);
        Assert.False(result.IsBootstrapToken);
        Assert.Equal(CredentialResolver.SourceNodeDeviceToken, result.Source);
    }

    [Fact]
    public void ResolveNode_FallsToSharedToken_WhenNoNodeToken()
    {
        var record = new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://test",
            SharedGatewayToken = "shared"
        };
        _mockReader.NodeToken = null;

        var result = _resolver.ResolveNode(record, "/id");

        Assert.NotNull(result);
        Assert.Equal("shared", result.Token);
        Assert.False(result.IsBootstrapToken);
    }

    [Fact]
    public void ResolveNode_FallsToBootstrap_WhenNoNodeOrShared()
    {
        var record = new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://test",
            BootstrapToken = "boot"
        };
        _mockReader.NodeToken = null;

        var result = _resolver.ResolveNode(record, "/id");

        Assert.NotNull(result);
        Assert.Equal("boot", result.Token);
        Assert.True(result.IsBootstrapToken);
    }

    [Fact]
    public void ResolveNode_ReturnsNull_WhenNoCredentials()
    {
        var record = new GatewayRecord { Id = "gw-1", Url = "wss://test" };
        _mockReader.NodeToken = null;

        var result = _resolver.ResolveNode(record, "/id");

        Assert.Null(result);
    }

    [Fact]
    public void ResolveNode_NeverDowngradesPairedNode()
    {
        var record = new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://test",
            SharedGatewayToken = "shared",
            BootstrapToken = "boot"
        };
        _mockReader.NodeToken = "node-paired-tok";

        var result = _resolver.ResolveNode(record, "/id");

        Assert.Equal("node-paired-tok", result!.Token);
        Assert.False(result.IsBootstrapToken);
    }

    // ─── Edge cases ───

    [Fact]
    public void ResolveOperator_ThrowsOnNullRecord()
    {
        Assert.Throws<ArgumentNullException>(() => _resolver.ResolveOperator(null!, "/id"));
    }

    [Fact]
    public void ResolveNode_ThrowsOnNullRecord()
    {
        Assert.Throws<ArgumentNullException>(() => _resolver.ResolveNode(null!, "/id"));
    }

    [Fact]
    public void ResolveOperator_SkipsWhitespaceTokens()
    {
        var record = new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://test",
            SharedGatewayToken = "   ",
            BootstrapToken = "  "
        };
        _mockReader.OperatorToken = "  ";

        var result = _resolver.ResolveOperator(record, "/id");

        Assert.Null(result);
    }

    // ─── Mock ───

    private sealed class MockDeviceIdentityReader : IDeviceIdentityReader
    {
        public string? OperatorToken { get; set; }
        public string? NodeToken { get; set; }

        public string? TryReadStoredDeviceToken(string dataPath) => OperatorToken;
        public string? TryReadStoredNodeDeviceToken(string dataPath) => NodeToken;
    }
}
