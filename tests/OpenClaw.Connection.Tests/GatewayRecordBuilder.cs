using OpenClaw.Connection;

namespace OpenClaw.Connection.Tests;

/// <summary>
/// Fluent builder for <see cref="GatewayRecord"/> test data, replacing the
/// per-file <c>MakeRecord(id, url)</c> helpers. Starts from sensible defaults
/// (random id, a test wss URL) and overrides only what a test cares about.
/// See <c>docs/ARCHITECTURE.md</c> (ledger id <c>test-gateway-builder</c>).
/// </summary>
/// <remarks>
/// Lives in this Connection-layer test project rather than the shared
/// <c>OpenClaw.TestSupport</c> so that Connection-free test projects (e.g. the
/// WinNode CLI tests) don't take a transitive dependency on
/// <c>OpenClaw.Connection</c> just to use the core fixtures. Graduate it to a
/// dedicated Connection-scoped support library if a second consumer appears.
/// </remarks>
public sealed class GatewayRecordBuilder
{
    private string _id = "gw-" + Guid.NewGuid().ToString("N")[..8];
    private string _url = "wss://gateway.test";
    private string? _friendlyName;
    private string? _sharedGatewayToken;
    private string? _bootstrapToken;
    private bool _isLocal;
    private bool _requiresV2Signature;

    public GatewayRecordBuilder WithId(string id) { _id = id; return this; }
    public GatewayRecordBuilder WithUrl(string url) { _url = url; return this; }
    public GatewayRecordBuilder WithFriendlyName(string? name) { _friendlyName = name; return this; }
    public GatewayRecordBuilder WithSharedGatewayToken(string? token) { _sharedGatewayToken = token; return this; }
    public GatewayRecordBuilder WithBootstrapToken(string? token) { _bootstrapToken = token; return this; }
    public GatewayRecordBuilder Local(bool isLocal = true) { _isLocal = isLocal; return this; }
    public GatewayRecordBuilder RequiresV2Signature(bool requires = true) { _requiresV2Signature = requires; return this; }

    public GatewayRecord Build() => new()
    {
        Id = _id,
        Url = _url,
        FriendlyName = _friendlyName,
        SharedGatewayToken = _sharedGatewayToken,
        BootstrapToken = _bootstrapToken,
        IsLocal = _isLocal,
        RequiresV2Signature = _requiresV2Signature,
    };
}
