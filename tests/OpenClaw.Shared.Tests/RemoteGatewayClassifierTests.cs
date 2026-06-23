using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public class RemoteGatewayClassifierTests
{
    [Theory]
    [InlineData("ws://localhost:18789")]
    [InlineData("wss://127.0.0.1:18789")]
    [InlineData("http://[::1]:18789")]
    public void Classify_LoopbackHosts_AreLocalLoopback(string url)
    {
        var profile = RemoteGatewayClassifier.Classify(url);

        Assert.Equal(GatewayConnectionTopology.Local, profile.Topology);
        Assert.Equal(GatewayTransportSecurity.LocalLoopback, profile.Security);
        Assert.True(profile.IsLocal);
        Assert.False(profile.IsRemote);
        Assert.False(profile.RecommendsTransportHardening);
    }

    [Theory]
    [InlineData("wss://gateway.example.com:18789")]
    [InlineData("https://gateway.example.com")]
    public void Classify_RemoteTls_IsDirectSecure(string url)
    {
        var profile = RemoteGatewayClassifier.Classify(url);

        Assert.Equal(GatewayConnectionTopology.DirectSecure, profile.Topology);
        Assert.Equal(GatewayTransportSecurity.Encrypted, profile.Security);
        Assert.True(profile.IsRemote);
        Assert.True(profile.IsTls);
        Assert.False(profile.RecommendsTransportHardening);
    }

    [Theory]
    [InlineData("ws://gateway.example.com:18789")]
    [InlineData("http://10.0.0.5:18789")]
    [InlineData("ws://machine-name:18789")]
    public void Classify_RemoteCleartext_IsDirectInsecure_AndWarns(string url)
    {
        var profile = RemoteGatewayClassifier.Classify(url);

        Assert.Equal(GatewayConnectionTopology.DirectInsecure, profile.Topology);
        Assert.Equal(GatewayTransportSecurity.Cleartext, profile.Security);
        Assert.True(profile.IsRemote);
        Assert.False(profile.IsTls);
        Assert.True(profile.RecommendsTransportHardening);
    }

    [Fact]
    public void Classify_WithSshTunnel_IsEncryptedTunnel_EvenForLoopbackUrl()
    {
        // The WebSocket talks to localhost but the bytes are SSH-encrypted to a
        // remote host — treat as remote-but-encrypted, never warn.
        var profile = RemoteGatewayClassifier.Classify("ws://localhost:18789", hasSshTunnel: true);

        Assert.Equal(GatewayConnectionTopology.SshTunnel, profile.Topology);
        Assert.Equal(GatewayTransportSecurity.Encrypted, profile.Security);
        Assert.True(profile.IsRemote);
        Assert.False(profile.IsLocal);
        Assert.False(profile.RecommendsTransportHardening);
    }

    [Fact]
    public void Classify_SshTunnel_TakesPrecedenceOverCleartextRemoteUrl()
    {
        var profile = RemoteGatewayClassifier.Classify("ws://gateway.example.com:18789", hasSshTunnel: true);

        Assert.Equal(GatewayConnectionTopology.SshTunnel, profile.Topology);
        Assert.False(profile.RecommendsTransportHardening);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not a url")]
    [InlineData("ws://")]
    public void Classify_UnparseableInput_IsUnknown_AndDoesNotWarn(string? url)
    {
        var profile = RemoteGatewayClassifier.Classify(url);

        Assert.Equal(GatewayConnectionTopology.Unknown, profile.Topology);
        Assert.False(profile.RecommendsTransportHardening);
        Assert.False(profile.IsRemote);
    }
}
