using System;
using System.Collections.Generic;
using System.Linq;
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
        await Task.Delay(250);

        Assert.Contains(ConnectionStatus.Error, statuses);
        Assert.Single(statuses, s => s == ConnectionStatus.Connecting);
        Assert.DoesNotContain(_logger.Logs, line => line.Contains("reconnecting in", StringComparison.OrdinalIgnoreCase));

        client.Dispose();
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!predicate())
        {
            if (DateTime.UtcNow - start > timeout)
                throw new TimeoutException("Condition was not met before the timeout.");

            await Task.Delay(25);
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
