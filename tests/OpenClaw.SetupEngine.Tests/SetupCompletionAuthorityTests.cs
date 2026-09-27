using OpenClaw.Connection;
using OpenClaw.Shared;
using OpenClaw.TestSupport;

namespace OpenClaw.SetupEngine.Tests;

public sealed class SetupCompletionAuthorityTests
{
    [Theory]
    [InlineData("rotated")]
    [InlineData("missing")]
    [InlineData("corrupt")]
    public void PersistedIdentityCannotRelabelAnAlreadyVerifiedClient(string change)
    {
        using var directory = new TempDirectory();
        var identity = new DeviceIdentity(directory.Path);
        identity.Initialize();
        var captured = SetupCompletionAuthority.CaptureIdentity(directory.Path, identity.DeviceId);
        SetupCompletionAuthority.RequirePersistedIdentity(directory.Path, captured);
        var file = Path.Combine(directory.Path, "device-key-ed25519.json");
        if (change == "rotated")
        {
            using var replacement = new TempDirectory();
            new DeviceIdentity(replacement.Path).Initialize();
            File.Copy(Path.Combine(replacement.Path, "device-key-ed25519.json"), file, true);
        }
        else if (change == "missing") File.Delete(file);
        else File.WriteAllText(file, "{invalid");
        var before = File.Exists(file) ? File.ReadAllText(file) : null;
        Assert.Throws<SetupNativeOwnershipException>(() =>
            SetupCompletionAuthority.RequirePersistedIdentity(directory.Path, captured));
        Assert.Equal(captured, SetupCompletionAuthority.CaptureIdentity(directory.Path, identity.DeviceId));
        Assert.Equal(before, File.Exists(file) ? File.ReadAllText(file) : null);
    }

    [Fact]
    public void VerifiedReconnectNeverGeneratesAMissingIdentity()
    {
        using var directory = new TempDirectory();
        Assert.Throws<DeviceIdentityLoadException>(() => new OpenClawGatewayClient(
            "wss://not-contacted.invalid", "synthetic", identityPath: directory.Path, requireExistingIdentity: true));
        Assert.False(File.Exists(Path.Combine(directory.Path, "device-key-ed25519.json")));
    }

    [Fact]
    public void NoAuthenticatedSigningIdentityCannotProduceValidAuthority()
    {
        var record = new GatewayRecord { Id = "gateway", Url = "wss://not-contacted.invalid" };
        var binding = new SetupGatewaySessionBinding(record);
        var route = binding.GetRoute(record, "identity", "agent:primary:main", null);
        Assert.Null(route.IdentityBinding);
        Assert.False(SetupCompletionAuthority.IsValid(route.IdentityBinding, route.SessionKey, route.AgentId));
    }

    private static GatewayAiSetupCompletion Proof => new(SetupCompletionIntent.CustodianOnboarding,
        "gateway", new string('A', 64), "provider/model", "primary", 1,
        IdentityBinding: SetupCompletionAuthority.CaptureIdentity("identity", "device-a"),
        SessionKey: "agent:primary:main");

    [Theory]
    [InlineData("device")]
    [InlineData("identity")]
    [InlineData("session")]
    [InlineData("missing")]
    public void CrossSessionVerification_RejectsChangedOrMissingStableAuthority(string change)
    {
        var current = Proof with
        {
            VerifiedGeneration = 7,
            IdentityBinding = change switch
            {
                "device" => SetupCompletionAuthority.CaptureIdentity("identity", "device-b"),
                "identity" => SetupCompletionAuthority.CaptureIdentity("other", "device-a"),
                "missing" => null,
                _ => Proof.IdentityBinding,
            },
            SessionKey = change == "session" ? "agent:primary:alternate" : Proof.SessionKey,
        };
        Assert.Throws<SetupNativeOwnershipException>(() => SetupNativeVerification.RequireSame(
            Proof, new(current, current.SessionKey!)));
    }

    [Fact]
    public void FreshGenerationWithSameIdentityAndFullSession_IsAcceptedWithoutLeakingIdentityPath()
    {
        SetupNativeVerification.RequireSame(Proof, new(Proof with { VerifiedGeneration = 19 }, Proof.SessionKey!));
        var json = System.Text.Json.JsonSerializer.Serialize(Proof);
        Assert.DoesNotContain(Path.GetFullPath("identity"), json);
        Assert.DoesNotContain("device-a", json);
        Assert.NotEqual(SetupCompletionAuthority.CaptureIdentity("a|b", "c"),
            SetupCompletionAuthority.CaptureIdentity("a", "b|c"));
    }

    [Fact]
    public async Task PersistedPortDrift_IsRejectedBeforeFreshSessionCanConnect()
    {
        using var temp = new TempDirectory();
        var registry = new GatewayRegistry(temp.Path);
        var record = registry.AddOrUpdate(new()
        {
            Id = "gateway", Url = "wss://not-contacted.invalid",
            SshTunnel = new("user", "not-contacted.invalid", 18789, 19001),
        });
        registry.SetActive(record.Id);
        registry.Save();
        var path = registry.GetIdentityDirectory(record.Id);
        var device = new DeviceIdentity(path);
        device.Initialize();
        var proof = Proof with { EndpointBinding = GatewayDashboardBinding.Capture(record),
            IdentityBinding = SetupCompletionAuthority.CaptureIdentity(path, device.DeviceId) };
        SetupGatewaySession.RequireCompletionGateway(temp.Path, proof);
        registry.Update(record.Id, value => value with { SshTunnel = value.SshTunnel! with { LocalPort = 19002 } });
        registry.Save();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            SetupNativeCompletionVerifier.VerifyAsync(temp.Path, proof, timeout.Token));
    }
}
