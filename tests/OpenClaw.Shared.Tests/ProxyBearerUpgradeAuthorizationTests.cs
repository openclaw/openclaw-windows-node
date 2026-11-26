using System.Net;
using System.Net.Sockets;
using OpenClaw.TestSupport;

namespace OpenClaw.Shared.Tests;

public sealed class ProxyBearerUpgradeAuthorizationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokedInitialAuthority_PreventsEvenTheTransportConnection(bool node)
    {
        using var identity = new TempDirectory("proxy-upgrade-");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"wss://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        var calls = 0;
        using var client = Create(node, url, identity.Path, _ =>
        {
            calls++;
            return Task.FromResult(new ReconnectAuthorizationResult(false, GatewayErrorKind.Auth, "Revoked"));
        });

        await AssertTransportDeniedAsync(client, listener);

        Assert.Equal(1, calls);
        Assert.False(listener.Pending());
    }

    [Theory]
    [InlineData(false, false, "replacement-token")]
    [InlineData(true, false, "replacement-token")]
    [InlineData(false, false, null)]
    [InlineData(true, false, null)]
    [InlineData(false, true, "original-token")]
    [InlineData(true, true, "original-token")]
    public async Task ChangedCredentialDuringAuthorization_PreventsTransportConnection(
        bool node, bool changeOrigin, string? replacement)
    {
        using var identity = new TempDirectory("proxy-upgrade-rotation-");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"wss://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        WebSocketClientBase? owner = null;
        var calls = 0;
        using var client = Create(node, url, identity.Path, _ =>
        {
            calls++;
            owner!.ProxyBearerToken = replacement;
            if (changeOrigin) owner.ProxyBearerOrigin = "wss://other.example";
            return Task.FromResult(new ReconnectAuthorizationResult(true, GatewayErrorKind.Unknown, ""));
        });
        owner = client;

        await AssertTransportDeniedAsync(client, listener);

        Assert.Equal(1, calls);
        Assert.False(listener.Pending());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneShotReconnectDenial_DoesNotReplaceInitialHandshakeAuthorization(bool node)
    {
        using var identity = new TempDirectory("proxy-upgrade-one-shot-");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"wss://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        var calls = 0;
        var reconnectCalls = 0;
        using var client = Create(node, url, identity.Path, _ =>
        {
            calls++;
            return Task.FromResult(new ReconnectAuthorizationResult(true, GatewayErrorKind.Unknown, ""));
        });
        client.ReconnectAuthorizationAsync = _ =>
        {
            reconnectCalls++;
            return Task.FromResult(new ReconnectAuthorizationResult(false, GatewayErrorKind.Auth, "One-shot"));
        };
        var connect = client.ConnectAsync();
        using var accepted = await listener.AcceptTcpClientAsync().WaitAsync(Deadline);
        Assert.Equal(1, calls);
        Assert.Equal(0, reconnectCalls);
        client.Dispose();
        await connect.WaitAsync(Deadline);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SupersededAuthorization_DoesNotFailOrConnectTheReplacement(bool node, bool allowOld)
    {
        using var identity = new TempDirectory("proxy-upgrade-generation-");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"wss://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ReconnectAuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var client = Create(node, url, identity.Path, _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                return release.Task;
            }
            return Task.FromResult(new ReconnectAuthorizationResult(false, GatewayErrorKind.Auth, "New attempt denied"));
        });
        var errors = 0;
        client.StatusChanged += (_, status) => { if (status == ConnectionStatus.Error) errors++; };
        var original = client.ConnectAsync();
        await entered.Task.WaitAsync(Deadline);
        await client.ConnectAsync().WaitAsync(Deadline);
        var replacementErrors = errors;
        release.SetResult(new ReconnectAuthorizationResult(allowOld, GatewayErrorKind.Auth, "Old result"));
        await original.WaitAsync(Deadline);
        Assert.Equal(1, replacementErrors);
        Assert.Equal(replacementErrors, errors);
        Assert.False(listener.Pending());
    }

    private static async Task AssertTransportDeniedAsync(WebSocketClientBase client, TcpListener listener)
    {
        using var cancellation = new CancellationTokenSource(Deadline);
        var accepted = listener.AcceptTcpClientAsync(cancellation.Token).AsTask();
        var connect = client.ConnectAsync();
        await Task.WhenAny(connect, accepted).WaitAsync(Deadline);
        var opened = accepted.IsCompletedSuccessfully;
        if (opened)
        {
            using var peer = await accepted;
            client.Dispose();
        }
        await connect.WaitAsync(Deadline);
        cancellation.Cancel();
        try { using var peer = await accepted; }
        catch (OperationCanceledException) { }
        Assert.False(opened, "A rejected proxy credential must not open even a TCP transport.");
    }

    private static WebSocketClientBase Create(bool node, string url, string identity,
        Func<CancellationToken, Task<ReconnectAuthorizationResult>> authorize)
    {
        var origin = new Uri(url).GetLeftPart(UriPartial.Authority);
        WebSocketClientBase client = node
            ? new WindowsNodeClient(url, "gateway-token", identity)
            {
                ProxyBearerToken = "original-token", ProxyBearerOrigin = origin,
                HandshakeAuthorizationAsync = authorize,
            }
            : new OpenClawGatewayClient(url, "gateway-token", identityPath: identity)
            {
                ProxyBearerToken = "original-token", ProxyBearerOrigin = origin,
                HandshakeAuthorizationAsync = authorize,
            };
        client.ReconnectAuthorizationAsync = _ => Task.FromResult(
            new ReconnectAuthorizationResult(false, GatewayErrorKind.Auth, "One-shot"));
        return client;
    }
}
