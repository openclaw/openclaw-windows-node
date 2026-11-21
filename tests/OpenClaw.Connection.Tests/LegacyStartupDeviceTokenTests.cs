using OpenClaw.Shared;

namespace OpenClaw.Connection.Tests;

public class LegacyStartupDeviceTokenTests : IDisposable
{
    private readonly string _root;
    private readonly string _legacyDir;
    private readonly CredentialResolver _resolver = new(DeviceIdentityFileReader.Instance);

    public LegacyStartupDeviceTokenTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "openclaw-legacy-token-" + Guid.NewGuid().ToString("N"));
        _legacyDir = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(_legacyDir);
    }

    public void Dispose()
    {
        // slopwatch-ignore: SW003 Test cleanup or fixture teardown is best-effort and must not hide the test outcome.
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Prefer_CopiesLegacyIdentity_AndUsesDeviceTokenInsteadOfShared()
    {
        WriteLegacyIdentity("paired-tok", "node-tok");
        var perGateway = DirectoryFor("gateway");
        var record = RecordWithWeakerTokens();

        var operatorChoice = LegacyStartupDeviceToken.Prefer(
            _resolver.ResolveOperatorDetailed(record, perGateway),
            record.Url,
            "WSS://Gateway.Example/socket",
            perGateway,
            _legacyDir,
            dir => _resolver.ResolveOperatorDetailed(record, dir));

        Assert.True(operatorChoice.Copied);
        Assert.Null(operatorChoice.CopyError);
        Assert.Equal(perGateway, operatorChoice.IdentityDirectory);
        Assert.Equal("paired-tok", operatorChoice.Resolution.Credential!.Token);
        Assert.Equal(CredentialResolver.SourceDeviceToken, operatorChoice.Resolution.Credential.Source);
        Assert.True(File.Exists(Path.Combine(perGateway, LegacyStartupDeviceToken.IdentityFileName)));
        Assert.Equal(
            record.Url,
            File.ReadAllText(Path.Combine(perGateway, LegacyStartupDeviceToken.BoundUrlFileName)).Trim());
        Assert.True(File.Exists(Path.Combine(_legacyDir, LegacyStartupDeviceToken.IdentityFileName)));

        var nodeDir = DirectoryFor("node-gateway");
        var nodeChoice = LegacyStartupDeviceToken.Prefer(
            _resolver.ResolveNodeDetailed(record, nodeDir),
            record.Url,
            record.Url,
            nodeDir,
            _legacyDir,
            dir => _resolver.ResolveNodeDetailed(record, dir));

        Assert.True(nodeChoice.Copied);
        Assert.Equal("node-tok", nodeChoice.Resolution.Credential!.Token);
        Assert.Equal(CredentialResolver.SourceNodeDeviceToken, nodeChoice.Resolution.Credential.Source);
    }

    [Fact]
    public void Prefer_CopyFailure_StillUsesLegacyDeviceTokenOverShared()
    {
        WriteLegacyIdentity("paired-tok", "node-tok");
        var blockedParent = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(blockedParent, "x");
        var perGateway = Path.Combine(blockedParent, "identity");
        var record = RecordWithWeakerTokens();

        var choice = LegacyStartupDeviceToken.Prefer(
            _resolver.ResolveOperatorDetailed(record, perGateway),
            record.Url,
            record.Url,
            perGateway,
            _legacyDir,
            dir => _resolver.ResolveOperatorDetailed(record, dir));

        Assert.False(choice.Copied);
        Assert.False(string.IsNullOrWhiteSpace(choice.CopyError));
        Assert.Equal(_legacyDir, choice.IdentityDirectory);
        Assert.Equal("paired-tok", choice.Resolution.Credential!.Token);
        Assert.Equal(CredentialResolver.SourceDeviceToken, choice.Resolution.Credential.Source);
        Assert.False(File.Exists(Path.Combine(perGateway, LegacyStartupDeviceToken.IdentityFileName)));
    }

    [Fact]
    public void Prefer_DoesNotCopyOrDowngrade_WhenDeviceTokenAlreadyResolved()
    {
        WriteLegacyIdentity("legacy-tok", null);
        var perGateway = DirectoryFor("gateway");
        var record = RecordWithWeakerTokens();
        var primary = new GatewayCredentialResolution(
            new GatewayCredential("already-paired", false, CredentialResolver.SourceDeviceToken),
            GatewayCredentialResolutionStatus.Resolved);

        var choice = LegacyStartupDeviceToken.Prefer(
            primary,
            record.Url,
            record.Url,
            perGateway,
            _legacyDir,
            dir => _resolver.ResolveOperatorDetailed(record, dir));

        Assert.False(choice.Copied);
        Assert.Null(choice.CopyError);
        Assert.Equal("already-paired", choice.Resolution.Credential!.Token);
        Assert.False(File.Exists(Path.Combine(perGateway, LegacyStartupDeviceToken.IdentityFileName)));
    }

    [Fact]
    public void Prefer_DoesNotCopy_WhenPerGatewayIdentityAlreadyExists()
    {
        WriteLegacyIdentity("legacy-tok", null);
        var perGateway = DirectoryFor("gateway");
        var existing = Path.Combine(perGateway, LegacyStartupDeviceToken.IdentityFileName);
        File.WriteAllText(existing, "{}");
        var record = RecordWithWeakerTokens();

        var choice = LegacyStartupDeviceToken.Prefer(
            _resolver.ResolveOperatorDetailed(record, perGateway),
            record.Url,
            record.Url,
            perGateway,
            _legacyDir,
            dir => _resolver.ResolveOperatorDetailed(record, dir));

        Assert.False(choice.Copied);
        Assert.Equal("{}", File.ReadAllText(existing));
        Assert.Equal(CredentialResolver.SourceSharedGatewayToken, choice.Resolution.Credential!.Source);
        Assert.Equal("shared", choice.Resolution.Credential.Token);
    }

    [Fact]
    public void Prefer_CorruptLegacyIdentity_DoesNotCopyAndKeepsSharedToken()
    {
        File.WriteAllText(
            Path.Combine(_legacyDir, LegacyStartupDeviceToken.IdentityFileName),
            "{not-json");
        var perGateway = DirectoryFor("gateway");
        var record = RecordWithWeakerTokens();

        var choice = LegacyStartupDeviceToken.Prefer(
            _resolver.ResolveOperatorDetailed(record, perGateway),
            record.Url,
            record.Url,
            perGateway,
            _legacyDir,
            dir => _resolver.ResolveOperatorDetailed(record, dir));

        Assert.False(choice.Copied);
        Assert.Null(choice.CopyError);
        Assert.Equal(perGateway, choice.IdentityDirectory);
        Assert.Equal("shared", choice.Resolution.Credential!.Token);
        Assert.False(File.Exists(Path.Combine(perGateway, LegacyStartupDeviceToken.IdentityFileName)));
    }

    [Fact]
    public void Prefer_DoesNotCopy_WhenGatewayUrlDiffers()
    {
        WriteLegacyIdentity("paired-tok", null);
        var perGateway = DirectoryFor("gateway");
        var record = RecordWithWeakerTokens();

        var choice = LegacyStartupDeviceToken.Prefer(
            _resolver.ResolveOperatorDetailed(record, perGateway),
            record.Url,
            "wss://other.example",
            perGateway,
            _legacyDir,
            dir => _resolver.ResolveOperatorDetailed(record, dir));

        Assert.False(choice.Copied);
        Assert.Equal("shared", choice.Resolution.Credential!.Token);
        Assert.False(File.Exists(Path.Combine(perGateway, LegacyStartupDeviceToken.IdentityFileName)));
    }

    [Fact]
    public void SelectIdentityDirectory_GivesEachReassignedUrlItsOwnDeviceToken()
    {
        var root = DirectoryFor("stamped-root");
        LegacyStartupDeviceToken.StampBoundUrl(root, "wss://a.example");

        var first = LegacyStartupDeviceToken.SelectIdentityDirectory(root, "wss://b.example");
        var second = LegacyStartupDeviceToken.SelectIdentityDirectory(root, "wss://c.example");
        var firstAgain = LegacyStartupDeviceToken.SelectIdentityDirectory(root, "wss://b.example");

        Assert.Equal(Path.GetFullPath(first), Path.GetFullPath(firstAgain));
        Assert.NotEqual(Path.GetFullPath(first), Path.GetFullPath(second));
        Assert.NotEqual(Path.GetFullPath(root), Path.GetFullPath(first));
        Assert.True(LegacyStartupDeviceToken.AllowsStoredDeviceToken(first, "wss://b.example"));
        Assert.False(LegacyStartupDeviceToken.AllowsStoredDeviceToken(first, "wss://c.example"));

        var identity = new DeviceIdentity(first);
        identity.Initialize();
        identity.StoreDeviceTokenForRole("operator", "bee-token", ["operator.read"]);
        var shared = new GatewayRecord
        {
            Id = "gw-1",
            Url = "wss://b.example",
            SharedGatewayToken = "shared"
        };

        var fromFirst = _resolver.ResolveOperator(shared, first);
        var fromSecond = _resolver.ResolveOperator(shared with { Url = "wss://c.example" }, second);

        Assert.Equal("bee-token", fromFirst!.Token);
        Assert.Equal(CredentialResolver.SourceDeviceToken, fromFirst.Source);
        Assert.Equal("shared", fromSecond!.Token);
        Assert.Equal(CredentialResolver.SourceSharedGatewayToken, fromSecond.Source);
    }

    [Fact]
    public void SelectIdentityDirectory_KeepsAnUnstampedRoot()
    {
        var root = DirectoryFor("plain-root");
        var selected = LegacyStartupDeviceToken.SelectIdentityDirectory(root, "wss://a.example");
        Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(selected));
    }

    [Fact]
    public void Prefer_CopiesLegacyNodeToken_WhenOperatorCredentialIsShared()
    {
        WriteLegacyIdentity(null, "node-tok");
        var perGateway = DirectoryFor("gateway");
        var record = RecordWithWeakerTokens();

        var choice = LegacyStartupDeviceToken.Prefer(
            _resolver.ResolveOperatorDetailed(record, perGateway),
            record.Url,
            record.Url,
            perGateway,
            _legacyDir,
            dir => _resolver.ResolveOperatorDetailed(record, dir));

        Assert.True(choice.Copied);
        Assert.Equal("shared", choice.Resolution.Credential!.Token);
        Assert.Equal(CredentialResolver.SourceSharedGatewayToken, choice.Resolution.Credential.Source);
        var node = _resolver.ResolveNode(record, perGateway);
        Assert.Equal("node-tok", node!.Token);
        Assert.Equal(CredentialResolver.SourceNodeDeviceToken, node.Source);
    }

    [Fact]
    public void EndpointIdentity_PreservesPathCase()
    {
        var upper = "wss://host.example/GatewayA";
        var lowerPath = "wss://host.example/gatewaya";
        var sameHost = "WSS://Host.Example/GatewayA";
        Assert.NotEqual(
            LegacyStartupDeviceToken.EndpointIdentityKey(upper),
            LegacyStartupDeviceToken.EndpointIdentityKey(lowerPath));
        Assert.Equal(
            LegacyStartupDeviceToken.EndpointIdentityKey(upper),
            LegacyStartupDeviceToken.EndpointIdentityKey(sameHost));

        var root = DirectoryFor("case-root");
        File.WriteAllText(
            Path.Combine(root, LegacyStartupDeviceToken.IdentityFileName),
            "{}");
        LegacyStartupDeviceToken.StampBoundUrl(root, upper);
        var other = LegacyStartupDeviceToken.SelectIdentityDirectory(root, lowerPath);
        Assert.NotEqual(Path.GetFullPath(root), Path.GetFullPath(other));
        Assert.False(LegacyStartupDeviceToken.AllowsStoredDeviceToken(root, lowerPath));
        Assert.True(LegacyStartupDeviceToken.AllowsStoredDeviceToken(root, sameHost));
        Assert.False(File.Exists(Path.Combine(other, LegacyStartupDeviceToken.IdentityFileName)));
    }

    private void WriteLegacyIdentity(string? operatorToken, string? nodeToken)
    {
        var identity = new DeviceIdentity(_legacyDir);
        identity.Initialize();
        if (operatorToken != null)
            identity.StoreDeviceTokenForRole("operator", operatorToken, ["operator.read"]);
        if (nodeToken != null)
            identity.StoreDeviceTokenForRole("node", nodeToken, ["node.connect"]);
    }

    private string DirectoryFor(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static GatewayRecord RecordWithWeakerTokens() => new()
    {
        Id = "gw-1",
        Url = "wss://gateway.example/socket",
        SharedGatewayToken = "shared",
        BootstrapToken = "boot"
    };
}
