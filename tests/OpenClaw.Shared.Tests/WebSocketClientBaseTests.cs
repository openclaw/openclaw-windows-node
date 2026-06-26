using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OpenClaw.Shared.Tests;

/// <summary>
/// Concrete test double for WebSocketClientBase. 
/// Exposes hooks and tracking for unit testing base class behavior.
/// </summary>
public class TestWebSocketClient : WebSocketClientBase
{
    public List<string> ProcessedMessages { get; } = new();
    public int OnConnectedCallCount { get; private set; }
    public int OnDisconnectedCallCount { get; private set; }
    public int OnErrorCallCount { get; private set; }
    public Exception? LastError { get; private set; }
    public int OnDisposingCallCount { get; private set; }
    public bool AutoReconnectEnabled { get; set; } = true;

    protected override int ReceiveBufferSize => 8192;
    protected override string ClientRole => "test";

    public TestWebSocketClient(string gatewayUrl, string token, IOpenClawLogger? logger = null)
        : base(gatewayUrl, token, logger) { }

    protected override Task ProcessMessageAsync(string json)
    {
        ProcessedMessages.Add(json);
        return Task.CompletedTask;
    }

    protected override Task OnConnectedAsync()
    {
        OnConnectedCallCount++;
        return Task.CompletedTask;
    }

    protected override void OnDisconnected()
    {
        OnDisconnectedCallCount++;
    }

    protected override void OnError(Exception ex)
    {
        OnErrorCallCount++;
        LastError = ex;
    }

    protected override void OnDisposing()
    {
        OnDisposingCallCount++;
    }

    protected override bool ShouldAutoReconnect() => AutoReconnectEnabled;

    // Expose protected members for testing
    public void TestRaiseStatusChanged(ConnectionStatus status)
        => RaiseStatusChanged(status);

    public bool TestIsDisposed => IsDisposed;
    public string TestGatewayUrlForDisplay => GatewayUrlForDisplay;
    public string TestToken => _token;
    public IOpenClawLogger TestLogger => _logger;
}

[Collection("WebSocketClientBase")]
public class WebSocketClientBaseTests
{
    private readonly TestLogger _logger = new();

    [Theory]
    [InlineData("http://localhost:18789", "ws://localhost:18789")]
    [InlineData("https://gateway.example.com", "wss://gateway.example.com")]
    [InlineData("ws://localhost:18789", "ws://localhost:18789")]
    [InlineData("wss://gateway.example.com", "wss://gateway.example.com")]
    public void Constructor_NormalizesUrl(string input, string expected)
    {
        var client = new TestWebSocketClient(input, "test-token", _logger);
        Assert.Equal(expected, client.TestGatewayUrlForDisplay);
        Assert.DoesNotContain("@", client.TestGatewayUrlForDisplay);
        client.Dispose();
    }

    [Fact]
    public void Constructor_StoresToken()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "my-token", _logger);
        Assert.Equal("my-token", client.TestToken);
        client.Dispose();
    }

    [Fact]
    public void Constructor_UsesNullLoggerWhenNotProvided()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "token");
        Assert.NotNull(client.TestLogger);
        client.Dispose();
    }

    [Fact]
    public void Constructor_ThrowsOnNullUrl()
    {
        Assert.Throws<ArgumentException>(() => 
            new TestWebSocketClient(null!, "token", _logger));
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyUrl()
    {
        Assert.Throws<ArgumentException>(() => 
            new TestWebSocketClient("", "token", _logger));
    }

    [Fact]
    public void Constructor_ThrowsOnNullToken()
    {
        Assert.Throws<ArgumentException>(() => 
            new TestWebSocketClient("ws://localhost", null!, _logger));
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyToken()
    {
        Assert.Throws<ArgumentException>(() => 
            new TestWebSocketClient("ws://localhost", "", _logger));
    }

    [Fact]
    public void Constructor_WithCredentialUrl_StripsFromDisplay()
    {
        var client = new TestWebSocketClient("ws://user:pass@localhost:18789", "token", _logger);
        Assert.Equal("ws://localhost:18789", client.TestGatewayUrlForDisplay);
        Assert.DoesNotContain("pass", client.TestGatewayUrlForDisplay);
        client.Dispose();
    }

    [Fact]
    public void Dispose_SetsIsDisposed()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "token", _logger);
        Assert.False(client.TestIsDisposed);
        client.Dispose();
        Assert.True(client.TestIsDisposed);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "token", _logger);
        client.Dispose();
        client.Dispose(); // second call should not throw
        Assert.True(client.TestIsDisposed);
        Assert.Equal(1, client.OnDisposingCallCount); // hook called only once
    }

    [Fact]
    public void Dispose_CallsOnDisposingHook()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "token", _logger);
        client.Dispose();
        Assert.Equal(1, client.OnDisposingCallCount);
    }

    [Fact]
    public void RaiseStatusChanged_FiresEvent()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "token", _logger);
        ConnectionStatus? received = null;
        client.StatusChanged += (_, status) => received = status;

        client.TestRaiseStatusChanged(ConnectionStatus.Connecting);

        Assert.Equal(ConnectionStatus.Connecting, received);
        client.Dispose();
    }

    [Fact]
    public void RaiseStatusChanged_WithNoSubscribers_DoesNotThrow()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "token", _logger);
        client.TestRaiseStatusChanged(ConnectionStatus.Connected); // no subscribers — should not throw
        client.Dispose();
    }

    [Fact]
    public void RaiseStatusChanged_MultipleSubscribers_AllNotified()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "token", _logger);
        var statuses = new List<ConnectionStatus>();
        client.StatusChanged += (_, s) => statuses.Add(s);
        client.StatusChanged += (_, s) => statuses.Add(s);

        client.TestRaiseStatusChanged(ConnectionStatus.Error);

        Assert.Equal(2, statuses.Count);
        Assert.All(statuses, s => Assert.Equal(ConnectionStatus.Error, s));
        client.Dispose();
    }

    [Fact]
    public void IsConnected_FalseBeforeConnect()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "token", _logger);
        // Reflection to check IsConnected on the base
        var prop = typeof(WebSocketClientBase).GetProperty("IsConnected",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var isConnected = (bool)prop!.GetValue(client)!;
        Assert.False(isConnected);
        client.Dispose();
    }

    [Fact]
    public void IsConnected_FalseAfterDispose()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "token", _logger);
        client.Dispose();
        var prop = typeof(WebSocketClientBase).GetProperty("IsConnected",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var isConnected = (bool)prop!.GetValue(client)!;
        Assert.False(isConnected);
    }

    [Fact]
    public async Task ConnectAsync_RaisesStatusChangedConnecting()
    {
        var client = new TestWebSocketClient("ws://localhost:18789", "token", _logger);
        var statuses = new List<ConnectionStatus>();
        client.StatusChanged += (_, s) => statuses.Add(s);

        // ConnectAsync should always emit Connecting.
        // Depending on timing/shutdown races, it may then emit Error or be canceled.
        await client.ConnectAsync();

        Assert.Contains(ConnectionStatus.Connecting, statuses);
        client.Dispose();
    }

    [Fact]
    public async Task ConnectAsync_WhenConnectionFails_StartsReconnectLoop()
    {
        var client = new TestWebSocketClient("ws://127.0.0.1:1", "token", _logger);
        var statuses = new List<ConnectionStatus>();
        client.StatusChanged += (_, s) => statuses.Add(s);

        await client.ConnectAsync();
        await WaitForConditionAsync(
            () => statuses.Count(s => s == ConnectionStatus.Connecting) >= 2,
            TimeSpan.FromSeconds(2));

        Assert.Contains(ConnectionStatus.Error, statuses);
        Assert.True(statuses.Count(s => s == ConnectionStatus.Connecting) >= 2);
        Assert.Contains(_logger.Logs, line => line.Contains("reconnecting in 1", StringComparison.OrdinalIgnoreCase) && line.Contains("ms (attempt 1)", StringComparison.OrdinalIgnoreCase));

        client.Dispose();
    }

    [Fact]
    public async Task ConnectAsync_WhenAutoReconnectDisabled_DoesNotStartReconnectLoop()
    {
        var client = new TestWebSocketClient("ws://127.0.0.1:1", "token", _logger)
        {
            AutoReconnectEnabled = false
        };
        var statuses = new List<ConnectionStatus>();
        client.StatusChanged += (_, s) => statuses.Add(s);

        await client.ConnectAsync();
        // slopwatch-ignore: SW004 Test delay is an intentional bounded async wait; replacing it would change the scenario under test.
        await Task.Delay(250);

        Assert.Contains(ConnectionStatus.Error, statuses);
        Assert.Single(statuses, s => s == ConnectionStatus.Connecting);
        Assert.DoesNotContain(_logger.Logs, line => line.Contains("reconnecting in", StringComparison.OrdinalIgnoreCase));

        client.Dispose();
    }

    [Fact]
    public async Task ConnectAsync_StaleConnectionDoesNotStartListenerOnNewerSocket()
    {
        using var server = new LoopbackWebSocketServer();
        await server.StartAsync();
        var client = new BlockingFirstConnectClient(server.WebSocketUrl, "token", _logger);
        var statuses = new ConcurrentQueue<ConnectionStatus>();
        var unexpectedErrorStatus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += (_, s) => statuses.Enqueue(s);
        client.StatusChanged += (_, s) =>
        {
            if (s == ConnectionStatus.Error)
                unexpectedErrorStatus.TrySetResult();
        };

        var firstConnect = client.ConnectAsync();
        await client.FirstConnectEntered.WaitAsync(TimeSpan.FromSeconds(2));

        var secondConnect = client.ConnectAsync();
        await client.SecondConnectReturned.WaitAsync(TimeSpan.FromSeconds(2));

        client.ReleaseFirstConnect();
        await Task.WhenAll(firstConnect, secondConnect).WaitAsync(TimeSpan.FromSeconds(2));

        // If the stale first ConnectAsync starts a listener after the second
        // connection is current, two listeners race on the same ClientWebSocket
        // and one reports a listen error.
        var unexpected = await Task.WhenAny(
            unexpectedErrorStatus.Task,
            Task.Delay(TimeSpan.FromMilliseconds(250)));

        Assert.Equal(2, client.OnConnectedCallCount);
        Assert.NotSame(unexpectedErrorStatus.Task, unexpected);
        Assert.Equal(0, client.OnErrorCallCount);
        Assert.DoesNotContain(ConnectionStatus.Error, statuses);

        client.Dispose();
    }

    [Fact]
    public async Task ReconnectBackoff_DoesNotDisposeNewerConnection_WhenSupersededDuringDelay()
    {
        using var server = new LoopbackWebSocketServer();
        await server.StartAsync();
        var client = new ReconnectBackoffRaceClient(server.WebSocketUrl, "token", _logger);
        var statuses = new ConcurrentQueue<ConnectionStatus>();
        client.StatusChanged += (_, s) => statuses.Enqueue(s);

        await client.ConnectAsync();
        await client.FirstConnected.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForConditionAsync(() => server.AcceptedCount >= 1, TimeSpan.FromSeconds(2));

        await server.CloseSocketAsync(0);
        await WaitForConditionAsync(
            () => statuses.Count(s => s == ConnectionStatus.Connecting) >= 2,
            TimeSpan.FromSeconds(2));

        await client.ConnectAsync();
        await client.SecondConnected.WaitAsync(TimeSpan.FromSeconds(2));

        var staleReconnectWon = await Task.WhenAny(
            client.ThirdConnected,
            Task.Delay(TimeSpan.FromMilliseconds(1800)));

        Assert.NotSame(client.ThirdConnected, staleReconnectWon);
        Assert.Equal(2, client.OnConnectedCallCount);
        Assert.Equal(2, server.AcceptedCount);

        client.Dispose();
    }

    [Fact]
    public async Task ReconnectBackoff_ContinuesAfterFailedRetry_WhenNoNewerConnectionOwnsSocket()
    {
        using var server = new LoopbackWebSocketServer();
        await server.StartAsync();
        var client = new ReconnectBackoffRaceClient(server.WebSocketUrl, "token", _logger);
        var statuses = new ConcurrentQueue<ConnectionStatus>();
        client.StatusChanged += (_, s) => statuses.Enqueue(s);

        await client.ConnectAsync();
        await client.FirstConnected.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForConditionAsync(() => server.AcceptedCount >= 1, TimeSpan.FromSeconds(2));

        await server.CloseSocketAsync(0);
        server.StopAccepting();

        await WaitForConditionAsync(
            () => statuses.Count(s => s == ConnectionStatus.Connecting) >= 4,
            TimeSpan.FromSeconds(4));

        Assert.Equal(1, client.OnConnectedCallCount);
        Assert.True(_logger.Logs.Count(
            line => line.Contains("reconnecting in", StringComparison.OrdinalIgnoreCase)) >= 2);

        client.Dispose();
    }

    [Fact]
    public async Task ReconnectBackoff_ReconnectsCurrentClosingSocket_WhenSupersededLoopIsActive()
    {
        using var server = new LoopbackWebSocketServer();
        await server.StartAsync();
        var client = new ReconnectBackoffRaceClient(server.WebSocketUrl, "token", _logger);
        var statuses = new ConcurrentQueue<ConnectionStatus>();
        client.StatusChanged += (_, s) => statuses.Enqueue(s);

        await client.ConnectAsync();
        await client.FirstConnected.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForConditionAsync(() => server.AcceptedCount >= 1, TimeSpan.FromSeconds(2));

        await server.CloseSocketAsync(0);
        await WaitForConditionAsync(
            () => statuses.Count(s => s == ConnectionStatus.Connecting) >= 2,
            TimeSpan.FromSeconds(2));

        await client.ConnectAsync();
        await client.SecondConnected.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForConditionAsync(() => server.AcceptedCount >= 2, TimeSpan.FromSeconds(2));

        await server.CloseSocketAsync(1);

        var reconnect = await Task.WhenAny(
            client.ThirdConnected,
            Task.Delay(TimeSpan.FromSeconds(3)));

        Assert.Same(client.ThirdConnected, reconnect);
        Assert.True(server.AcceptedCount >= 3);

        client.Dispose();
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!predicate())
        {
            if (DateTime.UtcNow - start > timeout)
                throw new TimeoutException("Condition was not met before the timeout.");

            // slopwatch-ignore: SW004 Test delay is an intentional bounded async wait; replacing it would change the scenario under test.
            await Task.Delay(25);
        }
    }
}

[CollectionDefinition("WebSocketClientBase", DisableParallelization = true)]
public sealed class WebSocketClientBaseTestCollection
{
}

internal sealed class BlockingFirstConnectClient : WebSocketClientBase
{
    private readonly TaskCompletionSource _firstConnectEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseFirstConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _secondConnectReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _connectCallbacks;

    public int OnConnectedCallCount => Volatile.Read(ref _connectCallbacks);
    public int OnErrorCallCount { get; private set; }
    public Task FirstConnectEntered => _firstConnectEntered.Task;
    public Task SecondConnectReturned => _secondConnectReturned.Task;

    public BlockingFirstConnectClient(string gatewayUrl, string token, IOpenClawLogger? logger = null)
        : base(gatewayUrl, token, logger)
    {
    }

    protected override int ReceiveBufferSize => 8192;
    protected override string ClientRole => "race-test";
    protected override bool ShouldAutoReconnect() => false;

    protected override Task ProcessMessageAsync(string json) => Task.CompletedTask;

    protected override async Task OnConnectedAsync()
    {
        var count = Interlocked.Increment(ref _connectCallbacks);
        if (count == 1)
        {
            _firstConnectEntered.TrySetResult();
            await _releaseFirstConnect.Task;
            return;
        }

        _secondConnectReturned.TrySetResult();
    }

    protected override void OnError(Exception ex) => OnErrorCallCount++;

    public void ReleaseFirstConnect() => _releaseFirstConnect.TrySetResult();
}

internal sealed class ReconnectBackoffRaceClient : WebSocketClientBase
{
    private readonly TaskCompletionSource _firstConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _secondConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _thirdConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _connectCallbacks;

    public int OnConnectedCallCount => Volatile.Read(ref _connectCallbacks);
    public Task FirstConnected => _firstConnected.Task;
    public Task SecondConnected => _secondConnected.Task;
    public Task ThirdConnected => _thirdConnected.Task;

    public ReconnectBackoffRaceClient(string gatewayUrl, string token, IOpenClawLogger? logger = null)
        : base(gatewayUrl, token, logger)
    {
    }

    protected override int ReceiveBufferSize => 8192;
    protected override string ClientRole => "reconnect-race-test";
    protected override Task ProcessMessageAsync(string json) => Task.CompletedTask;

    protected override Task OnConnectedAsync()
    {
        var count = Interlocked.Increment(ref _connectCallbacks);
        switch (count)
        {
            case 1:
                _firstConnected.TrySetResult();
                break;
            case 2:
                _secondConnected.TrySetResult();
                break;
            case 3:
                _thirdConnected.TrySetResult();
                break;
        }

        return Task.CompletedTask;
    }
}

internal sealed class LoopbackWebSocketServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<WebSocket> _acceptedSockets = new();
    private Task? _acceptLoop;

    public string WebSocketUrl { get; }
    public int AcceptedCount
    {
        get
        {
            lock (_acceptedSockets)
            {
                return _acceptedSockets.Count;
            }
        }
    }

    public LoopbackWebSocketServer()
    {
        var port = GetFreeTcpPort();
        var prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(prefix);
        WebSocketUrl = $"ws://127.0.0.1:{port}/";
    }

    public Task StartAsync()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException) when (_cts.Token.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
            lock (_acceptedSockets)
            {
                _acceptedSockets.Add(wsContext.WebSocket);
            }
        }
    }

    public async Task CloseSocketAsync(int index)
    {
        WebSocket socket;
        lock (_acceptedSockets)
        {
            socket = _acceptedSockets[index];
        }

        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "test close",
                CancellationToken.None);
        }
    }

    public void StopAccepting()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        lock (_acceptedSockets)
        {
            foreach (var socket in _acceptedSockets)
            {
                try { socket.Dispose(); } catch { }
            }
        }
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts.Dispose();
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

public class TestLogger : IOpenClawLogger
{
    public List<string> Logs { get; } = new();
    public void Info(string message) => Logs.Add($"INFO: {message}");
    public void Debug(string message) => Logs.Add($"DEBUG: {message}");
    public void Warn(string message) => Logs.Add($"WARN: {message}");
    public void Error(string message, Exception? ex = null) => Logs.Add($"ERROR: {message}");
}
