using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.E2ETests.Setup;

[Collection("E2E Setup")]
public sealed class SshOwnershipAdversarialProofTests
{
    private readonly E2ESetupFixture _fixture;

    public SshOwnershipAdversarialProofTests(E2ESetupFixture fixture)
    {
        _fixture = fixture;
        if (_fixture.SetupError is not null)
            throw new InvalidOperationException($"E2E setup failed: {_fixture.SetupError}");
    }

    [E2EFact]
    public async Task UnownedListenerIsRejectedThenOwnedTunnelRecoversWithoutRepairing()
    {
        var proofDir = Path.Combine(_fixture.ArtifactDir, "pr1076-proof");
        Directory.CreateDirectory(proofDir);
        var registryDir = Path.Combine(
            Path.GetTempPath(),
            $"openclaw-owned-listener-recovery-proof-{Guid.NewGuid():N}");
        Directory.CreateDirectory(registryDir);

        await using var server = new InitialHandshakeChallengeServer();
        try
        {
            var registry = new GatewayRegistry(registryDir);
            var tunnelConfig = new SshTunnelConfig(
                "proof-user",
                "proof-host",
                RemotePort: server.Port,
                LocalPort: server.Port);
            var record = new GatewayRecord
            {
                Id = "owned-listener-recovery-proof",
                Url = "wss://proof.invalid",
                SharedGatewayToken = "synthetic-proof-credential",
                SshTunnel = tunnelConfig,
            };
            registry.AddOrUpdate(record);
            registry.SetActive(record.Id);
            var tunnel = new InitialHandshakeRaceTunnelManager(server.WebSocketUrl)
            {
                OwnedListenerReady = false,
            };
            using var manager = new GatewayConnectionManager(
                new CredentialResolver(DeviceIdentityFileReader.Instance),
                new GatewayClientFactory(),
                registry,
                NullLogger.Instance,
                tunnelManager: tunnel);

            await manager.ConnectAsync(record.Id);
            var rejected = await WaitForOperatorErrorAsync(manager, TimeSpan.FromSeconds(5));
            var checksAfterRejection = tunnel.OwnedListenerCheckCount;

            Assert.False(server.Accepted.IsCompleted);
            Assert.Contains(
                "credentials were not sent",
                rejected.OperatorError,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(tunnel.IsActive);

            tunnel.OwnedListenerReady = true;
            await manager.ConnectAsync(record.Id);
            await server.Accepted.WaitAsync(TimeSpan.FromSeconds(10));
            var connectFrames = await server.CompleteHandshakeAsync(TimeSpan.FromSeconds(5));
            var recovered = await WaitForOperatorConnectedAsync(manager, TimeSpan.FromSeconds(5));

            Assert.Equal(1, connectFrames);
            Assert.True(tunnel.OwnedListenerCheckCount > checksAfterRejection);
            Assert.True(tunnel.IsActive);
            Assert.Equal(RoleConnectionState.Connected, recovered.OperatorState);
            var current = Assert.IsType<GatewayRecord>(registry.GetById(record.Id));
            Assert.Equal(record.Id, current.Id);
            Assert.Equal(record.Url, current.Url);
            Assert.Equal(record.SharedGatewayToken, current.SharedGatewayToken);
            Assert.Equal(record.SshTunnel, current.SshTunnel);

            File.WriteAllText(
                Path.Combine(proofDir, "owned-listener-recovery.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        head = ResolveHeadSha(),
                        unownedListenerRejectedBeforeWebSocketConnect = true,
                        rejection = rejected.OperatorError,
                        ownershipChecksAfterRejection = checksAfterRejection,
                        ownershipChecksAfterRecovery = tunnel.OwnedListenerCheckCount,
                        credentialBearingConnectFramesReceived = connectFrames,
                        recoveredOperatorState = recovered.OperatorState.ToString(),
                        gatewayCredentialUnchanged = true,
                        tunnelConfigurationUnchanged = true,
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            try { Directory.Delete(registryDir, recursive: true); } catch (IOException) { }
        }
    }

    [E2EFact]
    public async Task InitialHandshakeListenerReplacementWithholdsCredentialFrame()
    {
        var proofDir = Path.Combine(_fixture.ArtifactDir, "pr1076-proof");
        Directory.CreateDirectory(proofDir);
        var registryDir = Path.Combine(
            Path.GetTempPath(),
            $"openclaw-initial-handshake-proof-{Guid.NewGuid():N}");
        Directory.CreateDirectory(registryDir);

        await using var server = new InitialHandshakeChallengeServer();
        try
        {
            var registry = new GatewayRegistry(registryDir);
            var tunnelConfig = new SshTunnelConfig(
                "proof-user",
                "proof-host",
                RemotePort: server.Port,
                LocalPort: E2ESetupFixture.AllocateFreePort());
            var record = new GatewayRecord
            {
                Id = "initial-handshake-proof",
                Url = "wss://proof.invalid",
                SharedGatewayToken = "synthetic-proof-credential",
                SshTunnel = tunnelConfig,
            };
            registry.AddOrUpdate(record);
            registry.SetActive(record.Id);
            var tunnel = new InitialHandshakeRaceTunnelManager(server.WebSocketUrl);
            using var manager = new GatewayConnectionManager(
                new CredentialResolver(DeviceIdentityFileReader.Instance),
                new GatewayClientFactory(),
                registry,
                NullLogger.Instance,
                tunnelManager: tunnel);

            await manager.ConnectAsync(record.Id);
            await server.Accepted.WaitAsync(TimeSpan.FromSeconds(10));
            var checksBeforeChallenge = tunnel.OwnedListenerCheckCount;

            tunnel.OwnedListenerReady = false;
            var connectFrames = await server.SendChallengeAndCountConnectFramesAsync(
                TimeSpan.FromSeconds(2));
            var snapshot = await WaitForOperatorErrorAsync(manager, TimeSpan.FromSeconds(5));

            Assert.Equal(checksBeforeChallenge + 1, tunnel.OwnedListenerCheckCount);
            Assert.Equal(0, connectFrames);
            Assert.Contains(
                "credentials were not sent",
                snapshot.OperatorError,
                StringComparison.OrdinalIgnoreCase);

            File.WriteAllText(
                Path.Combine(proofDir, "initial-handshake-replacement.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        head = ResolveHeadSha(),
                        webSocketAcceptedBeforeListenerReplacement = true,
                        listenerOwnedAtTunnelStart = true,
                        listenerOwnedAtCredentialHandoff = false,
                        ownershipChecksBeforeChallenge = checksBeforeChallenge,
                        ownershipChecksAfterChallenge = tunnel.OwnedListenerCheckCount,
                        credentialBearingConnectFramesReceived = connectFrames,
                        operatorState = snapshot.OperatorState.ToString(),
                        error = snapshot.OperatorError,
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            try { Directory.Delete(registryDir, recursive: true); } catch (IOException) { }
        }
    }

    [E2EFact]
    public async Task InitialNodeHandshakeListenerReplacementWithholdsCredentialFrame()
    {
        var proofDir = Path.Combine(_fixture.ArtifactDir, "pr1076-proof");
        Directory.CreateDirectory(proofDir);
        var registryDir = Path.Combine(
            Path.GetTempPath(),
            $"openclaw-initial-node-handshake-proof-{Guid.NewGuid():N}");
        Directory.CreateDirectory(registryDir);

        await using var server = new InitialHandshakeChallengeServer();
        try
        {
            var registry = new GatewayRegistry(registryDir);
            var tunnelConfig = new SshTunnelConfig(
                "proof-user",
                "proof-host",
                RemotePort: server.Port,
                LocalPort: server.Port);
            var record = new GatewayRecord
            {
                Id = "initial-node-handshake-proof",
                Url = "wss://proof.invalid",
                SharedGatewayToken = "synthetic-node-proof-credential",
                SshTunnel = tunnelConfig,
            };
            registry.AddOrUpdate(record);
            registry.SetActive(record.Id);
            var tunnel = new InitialHandshakeRaceTunnelManager(server.WebSocketUrl);
            var nodeConnector = new NodeConnector(NullLogger.Instance);
            using var manager = new GatewayConnectionManager(
                new CredentialResolver(DeviceIdentityFileReader.Instance),
                new GatewayClientFactory(),
                registry,
                NullLogger.Instance,
                nodeConnector: nodeConnector,
                isNodeEnabled: () => true,
                tunnelManager: tunnel);

            await manager.ConnectNodeOnlyAsync(record.Id);
            await server.Accepted.WaitAsync(TimeSpan.FromSeconds(10));
            var checksBeforeChallenge = tunnel.OwnedListenerCheckCount;

            tunnel.OwnedListenerReady = false;
            var connectFrames = await server.SendChallengeAndCountConnectFramesAsync(
                TimeSpan.FromSeconds(2));
            var snapshot = await WaitForNodeErrorAsync(manager, TimeSpan.FromSeconds(5));

            Assert.True(
                tunnel.OwnedListenerCheckCount > checksBeforeChallenge,
                "Node credential handoff did not re-check listener ownership after the challenge.");
            Assert.Equal(0, connectFrames);
            Assert.Equal(RoleConnectionState.Error, snapshot.NodeState);

            File.WriteAllText(
                Path.Combine(proofDir, "initial-node-handshake-replacement.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        head = ResolveHeadSha(),
                        webSocketAcceptedBeforeListenerReplacement = true,
                        listenerOwnedAtTunnelStart = true,
                        listenerOwnedAtCredentialHandoff = false,
                        ownershipChecksBeforeChallenge = checksBeforeChallenge,
                        ownershipChecksAfterChallenge = tunnel.OwnedListenerCheckCount,
                        credentialBearingConnectFramesReceived = connectFrames,
                        nodeState = snapshot.NodeState.ToString(),
                        error = snapshot.NodeError,
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            try { Directory.Delete(registryDir, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<GatewayConnectionSnapshot> WaitForOperatorErrorAsync(
        GatewayConnectionManager manager,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = manager.CurrentSnapshot;
            if (snapshot.OperatorState == RoleConnectionState.Error)
                return snapshot;
            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Operator did not enter Error state. Last: {manager.CurrentSnapshot.OperatorState}");
    }

    private static async Task<GatewayConnectionSnapshot> WaitForOperatorConnectedAsync(
        GatewayConnectionManager manager,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = manager.CurrentSnapshot;
            if (snapshot.OperatorState == RoleConnectionState.Connected)
                return snapshot;
            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Operator did not enter Connected state. Last: {manager.CurrentSnapshot.OperatorState}");
    }

    private static async Task<GatewayConnectionSnapshot> WaitForNodeErrorAsync(
        GatewayConnectionManager manager,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = manager.CurrentSnapshot;
            if (snapshot.NodeState == RoleConnectionState.Error)
                return snapshot;
            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Node did not enter Error state. Last: {manager.CurrentSnapshot.NodeState}");
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                start.Environment[key] = value;
        }
        using var process = Process.Start(start) ??
            throw new InvalidOperationException($"Failed to start {fileName}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var timeoutCts = new CancellationTokenSource(effectiveTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return new ProcessResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between HasExited and Kill.
            }

            await process.WaitForExitAsync();
            var timeoutMessage =
                $"Process timed out after {effectiveTimeout.TotalSeconds:F0} seconds.";
            var stderrText = await stderr;
            return new ProcessResult(
                -1,
                await stdout,
                string.IsNullOrWhiteSpace(stderrText)
                    ? timeoutMessage
                    : $"{stderrText.TrimEnd()}{Environment.NewLine}{timeoutMessage}");
        }
    }

    private static string ResolveHeadSha()
    {
        var result = RunProcessAsync("git.exe", ["rev-parse", "HEAD"])
            .GetAwaiter()
            .GetResult();
        return result.ExitCode == 0 ? result.Stdout.Trim() : "unknown";
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private sealed class InitialHandshakeRaceTunnelManager(string webSocketUrl)
        : ISshTunnelManager
    {
        public bool OwnedListenerReady { get; set; } = true;
        public int OwnedListenerCheckCount { get; private set; }
        public bool IsActive { get; private set; }
        public long OwnershipGeneration { get; private set; }
        public SshTunnelConfig? ActiveConfig { get; private set; }
        public string? LocalTunnelUrl => IsActive ? webSocketUrl : null;

        public bool IsRestartPending(SshTunnelExit tunnelExit) => false;

        public Task<bool> IsOwnedListenerReadyAsync(
            SshTunnelConfig config,
            int destinationPort,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            OwnedListenerCheckCount++;
            return Task.FromResult(
                OwnedListenerReady &&
                IsActive &&
                ActiveConfig == config &&
                destinationPort == config.LocalPort);
        }

        public Task<string> StartAsync(SshTunnelConfig config, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            IsActive = true;
            ActiveConfig = config;
            OwnershipGeneration++;
            return Task.FromResult(webSocketUrl);
        }

        public async Task<SshTunnelStartResult> StartOwnedAsync(
            SshTunnelConfig config,
            CancellationToken ct)
        {
            var url = await StartAsync(config, ct);
            return new SshTunnelStartResult(url, config, OwnershipGeneration);
        }

        public Task StopAsync()
        {
            IsActive = false;
            ActiveConfig = null;
            return Task.CompletedTask;
        }

        public Task<bool> StopIfOwnedAsync(
            SshTunnelConfig config,
            long ownershipGeneration,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsActive ||
                ActiveConfig != config ||
                OwnershipGeneration != ownershipGeneration)
            {
                return Task.FromResult(false);
            }

            IsActive = false;
            ActiveConfig = null;
            return Task.FromResult(true);
        }

        public void Dispose()
        {
        }
    }

    private sealed class InitialHandshakeChallengeServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource<WebSocket> _socket =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _acceptTask;

        public InitialHandshakeChallengeServer()
        {
            Exception? lastError = null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var candidate = E2ESetupFixture.AllocateFreePort();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
                try
                {
                    listener.Start();
                    _listener = listener;
                    Port = candidate;
                    WebSocketUrl = $"ws://127.0.0.1:{candidate}/";
                    _acceptTask = Task.Run(AcceptAsync);
                    return;
                }
                catch (HttpListenerException ex)
                {
                    lastError = ex;
                    listener.Close();
                }
            }

            throw new InvalidOperationException(
                "Could not bind initial-handshake proof server.",
                lastError);
        }

        public int Port { get; }
        public string WebSocketUrl { get; }
        public Task Accepted => _socket.Task;

        public async Task<int> SendChallengeAndCountConnectFramesAsync(TimeSpan observation)
        {
            var socket = await _socket.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var challenge = JsonSerializer.Serialize(new
            {
                type = "event",
                @event = "connect.challenge",
                payload = new
                {
                    nonce = "initial-handshake-proof",
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            });
            await socket.SendAsync(
                Encoding.UTF8.GetBytes(challenge),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);

            var connectFrames = 0;
            var deadline = DateTime.UtcNow.Add(observation);
            while (DateTime.UtcNow < deadline)
            {
                using var receiveCts = new CancellationTokenSource(
                    deadline - DateTime.UtcNow);
                string? frame;
                try
                {
                    frame = await ReceiveTextAsync(socket, receiveCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException)
                {
                    // A denied credential handoff aborts this proof socket without a close frame.
                    break;
                }

                if (frame is null)
                    break;
                using var document = JsonDocument.Parse(frame);
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var type) &&
                    type.GetString() == "req" &&
                    root.TryGetProperty("method", out var method) &&
                    method.GetString() == "connect")
                {
                    connectFrames++;
                }
            }

            return connectFrames;
        }

        public async Task<int> CompleteHandshakeAsync(TimeSpan observation)
        {
            var socket = await _socket.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var challenge = JsonSerializer.Serialize(new
            {
                type = "event",
                @event = "connect.challenge",
                payload = new
                {
                    nonce = "owned-listener-recovery-proof",
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
            });
            await socket.SendAsync(
                Encoding.UTF8.GetBytes(challenge),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);

            var connectFrames = 0;
            var deadline = DateTime.UtcNow.Add(observation);
            while (DateTime.UtcNow < deadline)
            {
                using var receiveCts = new CancellationTokenSource(
                    deadline - DateTime.UtcNow);
                var frame = await ReceiveTextAsync(socket, receiveCts.Token);
                if (frame is null)
                    break;

                using var document = JsonDocument.Parse(frame);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type) ||
                    type.GetString() != "req" ||
                    !root.TryGetProperty("method", out var method) ||
                    method.GetString() != "connect" ||
                    !root.TryGetProperty("id", out var id))
                {
                    continue;
                }

                connectFrames++;
                var response = JsonSerializer.Serialize(new
                {
                    type = "res",
                    id = id.GetString(),
                    ok = true,
                    payload = new
                    {
                        type = "hello-ok",
                        protocol = 4,
                        server = new { version = "proof" },
                    },
                });
                await socket.SendAsync(
                    Encoding.UTF8.GetBytes(response),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    CancellationToken.None);
                break;
            }

            return connectFrames;
        }

        private async Task AcceptAsync()
        {
            try
            {
                var context = await _listener.GetContextAsync();
                var webSocket = await context.AcceptWebSocketAsync(subProtocol: null);
                _socket.TrySetResult(webSocket.WebSocket);
            }
            catch (Exception ex) when (
                ex is HttpListenerException or ObjectDisposedException &&
                _cts.IsCancellationRequested)
            {
                _socket.TrySetCanceled(_cts.Token);
            }
        }

        private static async Task<string?> ReceiveTextAsync(
            WebSocket socket,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            using var stream = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                    return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
            if (_socket.Task.IsCompletedSuccessfully)
            {
                var socket = _socket.Task.Result;
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    try
                    {
                        await socket.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "proof complete",
                            CancellationToken.None);
                    }
                    catch (WebSocketException)
                    {
                    }
                }
                socket.Dispose();
            }
            try { await _acceptTask; } catch (OperationCanceledException) { }
            _cts.Dispose();
        }
    }

}
