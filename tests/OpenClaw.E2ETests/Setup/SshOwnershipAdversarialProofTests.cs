using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        var profileDir = Path.Combine(proofDir, "profile");
        var sshDir = Path.Combine(profileDir, ".ssh");
        var sshConfigPath = Path.Combine(profileDir, "ssh_config");
        Directory.CreateDirectory(sshDir);
        var gatewayPath = Path.Combine(_fixture.DataDir, "gateways.json");
        var settingsPath = Path.Combine(_fixture.DataDir, "settings.json");
        var originalGatewayBytes = File.ReadAllBytes(gatewayPath);
        var originalSettingsBytes = File.ReadAllBytes(settingsPath);
        using var sshPortLease = MirroredWslPortLease.Acquire();
        var sshPort = sshPortLease.Port;
        var tunnelPort = AllocateFreeForwardPortPair();
        var screenshotDegraded = Path.Combine(proofDir, "04-connection-degraded.png");
        var captureUiProof = string.Equals(
            Environment.GetEnvironmentVariable("OPENCLAW_CAPTURE_UI_PROOF"),
            "1",
            StringComparison.Ordinal);
        TcpListener? adversary = null;
        (string? Operator, string? Node) beforeTokens = (null, null);

        try
        {
            beforeTokens = ReadRoleTokens();
            Assert.False(string.IsNullOrWhiteSpace(beforeTokens.Operator));
            Assert.False(string.IsNullOrWhiteSpace(beforeTokens.Node));

            await _fixture.StopTrayAsync();
            await ConfigureProofSshAsync(profileDir, sshDir, sshPort);
            var proofSshd = await StartProofSshdAsync(profileDir, sshDir, sshPort);
            WriteObject("00-proof-sshd.json", new
            {
                unitName = proofSshd.UnitName,
                processId = proofSshd.ProcessId,
                executablePath = proofSshd.ExecutablePath,
                commandLine = proofSshd.CommandLine,
            });
            var identityFile = Path.Combine(sshDir, "id_ed25519").Replace('\\', '/');
            await File.WriteAllTextAsync(
                sshConfigPath,
                $"""
                Host *
                    BatchMode yes
                    IdentitiesOnly yes
                    IdentityFile "{identityFile}"
                    UserKnownHostsFile NUL
                    StrictHostKeyChecking no
                    ProxyCommand wsl.exe -d {_fixture.DistroName} -- nc 127.0.0.1 {sshPort}
                """);
            PatchActiveGateway(tunnelPort, proofSshd.HostAddress, sshPort, browserControlPort: null);
            _fixture.SetTrayEnvironmentVariable("HOME", profileDir);
            _fixture.SetTrayEnvironmentVariable("USERPROFILE", profileDir);
            _fixture.SetTrayEnvironmentVariable("OPENCLAW_E2E_SSH_CONFIG_FILE", sshConfigPath);
            if (captureUiProof)
            {
                _fixture.SetTrayEnvironmentVariable("OPENCLAW_VISUAL_TEST", "1");
                _fixture.SetTrayEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR", proofDir);
            }
            await _fixture.StartTrayAsync();

            var ownedSnapshot = await WaitForListenerSnapshotAsync(
                listeners => listeners.Any(listener =>
                    listener.Port == tunnelPort &&
                    string.Equals(listener.ProcessName, "ssh", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(30));
            var ownedListeners = ownedSnapshot.Listeners
                .Where(listener => listener.Port == tunnelPort)
                .ToArray();
            var sshListeners = ownedListeners
                .Where(listener =>
                    string.Equals(listener.ProcessName, "ssh", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.NotEmpty(sshListeners);
            var sshProcessId = Assert.Single(sshListeners.Select(listener => listener.ProcessId).Distinct());
            var sshListener = Assert.Single(
                sshListeners,
                listener => listener.Address.Equals(IPAddress.Loopback));
            using var ready = await WaitForReadyStatusAsync(TimeSpan.FromSeconds(30));
            AssertReady(ready.RootElement);
            WriteJson("01-valid-ready.json", ready.RootElement);
            WriteObject("02-owned-listener.json", new
            {
                tunnelPort,
                owned = true,
                listenerCount = ownedListeners.Length,
                processName = sshListener.ProcessName,
                processId = sshProcessId,
                addresses = sshListeners.Select(listener => listener.Address.ToString()).ToArray(),
            });

            adversary = new TcpListener(IPAddress.Parse("127.0.0.2"), tunnelPort);
            adversary.Start();
            var competingSnapshot = await WaitForListenerSnapshotAsync(
                listeners =>
                    listeners.Any(listener =>
                        listener.Port == tunnelPort &&
                        listener.ProcessId == Environment.ProcessId) &&
                    listeners.Any(listener =>
                        listener.Port == tunnelPort &&
                        listener.ProcessId == sshListener.ProcessId),
                TimeSpan.FromSeconds(30));
            var competingListeners = competingSnapshot.Listeners
                .Where(listener => listener.Port == tunnelPort)
                .ToArray();
            Assert.Contains(competingListeners, listener => listener.ProcessId == Environment.ProcessId);
            Assert.Contains(competingListeners, listener => listener.ProcessId == sshListener.ProcessId);
            WriteObject("03-competing-listener.json", new
            {
                tunnelPort,
                listenerCount = competingListeners.Length,
                unrelatedListenerPresent = true,
                ownedProcessId = sshListener.ProcessId,
                unrelatedProcessId = Environment.ProcessId,
            });

            using (var reconnect = await _fixture.Client!.CallToolExpectSuccessAsync(
                       "app.connection.reconnectNode"))
            {
                Assert.True(reconnect.RootElement.GetProperty("reconnected").GetBoolean());
            }
            using var degraded = await WaitForStatusAsync(
                status =>
                    status.GetProperty("overallState").GetString() == "Degraded" &&
                    status.GetProperty("nodeState").GetString() == "Error" &&
                    status.GetProperty("nodeError").GetString()?.Contains(
                        "credentials were not sent",
                        StringComparison.OrdinalIgnoreCase) == true,
                TimeSpan.FromSeconds(45));
            adversary.Stop();
            adversary = null;
            Assert.Contains(
                "credentials were not sent",
                degraded.RootElement.GetProperty("nodeError").GetString(),
                StringComparison.OrdinalIgnoreCase);
            WriteJson("04-degraded-status.json", degraded.RootElement);
            using (var connectionStatus = await _fixture.Client!.CallToolExpectSuccessAsync(
                       "app.connection.status"))
            {
                WriteJson("04-connection-diagnostics.json", connectionStatus.RootElement);
            }
            if (captureUiProof)
                await NavigateAndCaptureAsync("connection", screenshotDegraded);

            using (var reconnect = await _fixture.Client!.CallToolExpectSuccessAsync(
                       "app.connection.reconnectNode"))
            {
                Assert.True(reconnect.RootElement.GetProperty("reconnected").GetBoolean());
            }
            await _fixture.WaitForConnectionReady(TimeSpan.FromSeconds(90));
            await _fixture.WaitForNodeListReady(TimeSpan.FromSeconds(60));
            using var recovered = await WaitForReadyStatusAsync(TimeSpan.FromSeconds(30));
            AssertReady(recovered.RootElement);
            WriteJson("05-recovered-ready.json", recovered.RootElement);

            var afterTokens = ReadRoleTokens();
            Assert.Equal(beforeTokens.Operator, afterTokens.Operator);
            Assert.Equal(beforeTokens.Node, afterTokens.Node);
            using (var approvals = await WaitForConnectedPendingApprovalsAsync(
                       TimeSpan.FromSeconds(30)))
            {
                Assert.True(approvals.RootElement.GetProperty("connected").GetBoolean());
                Assert.Equal(0, approvals.RootElement.GetProperty("totalPending").GetInt32());
                Assert.Empty(approvals.RootElement.GetProperty("devicePending").EnumerateArray());
                Assert.Empty(approvals.RootElement.GetProperty("nodePending").EnumerateArray());
                WriteJson("05-pending-approvals.json", approvals.RootElement);
            }

            await _fixture.StopTrayAsync();
            var identityDir = _fixture.ReadActiveGatewayCredentialState().IdentityDir;
            var clear = DeviceIdentityStore.BeginTransactionalTokenClear(identityDir);
            Assert.True(clear.Success, clear.Error);
            Assert.NotNull(clear.Transaction);
            var newerOperatorToken = $"proof-operator-{Guid.NewGuid():N}";
            var newerNodeToken = $"proof-node-{Guid.NewGuid():N}";
            var lateWriter = new DeviceIdentity(identityDir);
            lateWriter.Initialize();
            lateWriter.StoreDeviceTokenForRole("operator", newerOperatorToken);
            lateWriter.StoreDeviceTokenForRole("node", newerNodeToken);
            var restore = DeviceIdentityStore.RestoreTransactionalTokenClear(clear.Transaction!);
            Assert.Equal(DeviceTokenRestoreOutcome.Superseded, restore.Outcome);
            var lateWriterTokens = ReadRoleTokens();
            Assert.Equal(newerOperatorToken, lateWriterTokens.Operator);
            Assert.Equal(newerNodeToken, lateWriterTokens.Node);
            Assert.NotEqual(beforeTokens.Operator, lateWriterTokens.Operator);
            Assert.NotEqual(beforeTokens.Node, lateWriterTokens.Node);
            WriteObject("07-late-writer-rollback.json", new
            {
                restoreOutcome = restore.Outcome.ToString(),
                newerOperatorCredentialPreserved = true,
                newerNodeCredentialPreserved = true,
                originalOperatorCredentialWasNotRestored = true,
                originalNodeCredentialWasNotRestored = true,
            });

            WriteObject("proof-summary.json", new
            {
                head = ResolveHeadSha(),
                distro = _fixture.DistroName,
                gatewayPort = _fixture.GatewayPort,
                sshPort,
                tunnelPort,
                ambiguousListenerOwnershipRejectedBeforeCredentialSend = true,
                recoveredReady = true,
                sameOperatorCredential = true,
                sameNodeCredential = true,
                lateWriterWonRollback = true,
                degradedScreenshot = captureUiProof && File.Exists(screenshotDegraded),
            });
            WriteRedactedTrayLog();
        }
        finally
        {
            adversary?.Stop();
            await _fixture.StopTrayAsync();
            File.WriteAllBytes(gatewayPath, originalGatewayBytes);
            File.WriteAllBytes(settingsPath, originalSettingsBytes);
            if (!string.IsNullOrWhiteSpace(beforeTokens.Operator) &&
                !string.IsNullOrWhiteSpace(beforeTokens.Node))
            {
                var identityDir = _fixture.ReadActiveGatewayCredentialState().IdentityDir;
                var originalIdentity = new DeviceIdentity(identityDir);
                originalIdentity.Initialize();
                originalIdentity.StoreDeviceTokenForRole("operator", beforeTokens.Operator);
                originalIdentity.StoreDeviceTokenForRole("node", beforeTokens.Node);
            }
            _fixture.RemoveTrayEnvironmentVariable("HOME");
            _fixture.RemoveTrayEnvironmentVariable("USERPROFILE");
            _fixture.RemoveTrayEnvironmentVariable("OPENCLAW_E2E_SSH_CONFIG_FILE");
            _fixture.RemoveTrayEnvironmentVariable("OPENCLAW_VISUAL_TEST");
            _fixture.RemoveTrayEnvironmentVariable("OPENCLAW_VISUAL_TEST_DIR");
            var sshdUnit = $"openclaw-pr1076-sshd-{sshPort}.service";
            await _fixture.RunInWslAsync(
                $"systemctl stop '{sshdUnit}' 2>/dev/null || true; " +
                $"systemctl reset-failed '{sshdUnit}' 2>/dev/null || true",
                TimeSpan.FromSeconds(15),
                inputViaStdin: true,
                user: "root");
            try { Directory.Delete(profileDir, recursive: true); } catch { }
            await _fixture.StartTrayAsync();
        }

        return;

        (string? Operator, string? Node) ReadRoleTokens()
        {
            var identityDir = _fixture.ReadActiveGatewayCredentialState().IdentityDir;
            using var document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(identityDir, "device-key-ed25519.json")));
            return (
                ReadString(document.RootElement, "DeviceToken"),
                ReadString(document.RootElement, "NodeDeviceToken"));
        }

        void PatchActiveGateway(
            int localTunnelPort,
            string sshHost,
            int localSshPort,
            int? browserControlPort)
        {
            var root = JsonNode.Parse(File.ReadAllText(gatewayPath))!.AsObject();
            var activeId = root["activeId"]!.GetValue<string>();
            var records = root["gateways"]!.AsArray();
            var active = records
                .Select(node => node!.AsObject())
                .Single(record => record["id"]!.GetValue<string>() == activeId);
            active["sshTunnel"] = JsonSerializer.SerializeToNode(
                new
                {
                    user = "openclaw",
                    host = sshHost,
                    remotePort = _fixture.GatewayPort,
                    localPort = localTunnelPort,
                    includeBrowserProxyForward = true,
                    sshPort = localSshPort,
                });
            if (browserControlPort.HasValue)
                active["browserControlPort"] = browserControlPort.Value;
            else
                active.Remove("browserControlPort");
            File.WriteAllText(
                gatewayPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        void WriteJson(string fileName, JsonElement element) =>
            File.WriteAllText(
                Path.Combine(proofDir, fileName),
                JsonSerializer.Serialize(
                    JsonSerializer.Deserialize<object>(element.GetRawText()),
                    new JsonSerializerOptions { WriteIndented = true }));

        void WriteObject(string fileName, object value) =>
            File.WriteAllText(
                Path.Combine(proofDir, fileName),
                JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

        void WriteRedactedTrayLog()
        {
            var logPath = Path.Combine(_fixture.DataDir, "openclaw-tray.log");
            if (!File.Exists(logPath))
                return;
            var selected = File.ReadLines(logPath)
                .Where(line =>
                    line.Contains("listener", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("tunnel", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Degraded", StringComparison.OrdinalIgnoreCase))
                .TakeLast(200);
            File.WriteAllLines(
                Path.Combine(proofDir, "selected-tray-log.redacted.txt"),
                selected.Select(TokenSanitizer.SanitizeLogMessage));
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

    private async Task ConfigureProofSshAsync(
        string profileDir,
        string sshDir,
        int sshPort)
    {
        var keyPath = Path.Combine(sshDir, "id_ed25519");
        var keygen = await RunProcessAsync(
            "ssh-keygen.exe",
            ["-q", "-t", "ed25519", "-N", "", "-f", keyPath]);
        Assert.Equal(0, keygen.ExitCode);

        var install = await _fixture.RunInWslAsync(
            "set -e; export DEBIAN_FRONTEND=noninteractive; " +
            "if ! command -v sshd >/dev/null || ! command -v nc >/dev/null; then " +
            "apt-get update -qq; apt-get install -y -qq --no-install-recommends openssh-server netcat-openbsd; fi; " +
            "ssh-keygen -A; install -d -m 0755 /run/sshd",
            TimeSpan.FromMinutes(3),
            user: "root");
        Assert.Equal(0, install.ExitCode);

        var publicKey = await File.ReadAllTextAsync(keyPath + ".pub");
        var publicKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(publicKey));
        var authorize = await _fixture.RunInWslAsync(
            $"set -e; install -d -m 700 -o openclaw -g openclaw /home/openclaw/.ssh; echo '{publicKeyBase64}' | base64 -d > /home/openclaw/.ssh/authorized_keys; chown openclaw:openclaw /home/openclaw/.ssh/authorized_keys; chmod 600 /home/openclaw/.ssh/authorized_keys",
            TimeSpan.FromSeconds(30),
            user: "root");
        Assert.Equal(0, authorize.ExitCode);

        _fixture.SetTrayEnvironmentVariable("HOME", profileDir);
        _fixture.SetTrayEnvironmentVariable("USERPROFILE", profileDir);
    }

    private async Task<ProofSshdProcess> StartProofSshdAsync(
        string profileDir,
        string sshDir,
        int sshPort)
    {
        var unitName = $"openclaw-pr1076-sshd-{sshPort}.service";
        var start = await _fixture.RunInWslAsync(
            $"set -e; systemctl stop '{unitName}' 2>/dev/null || true; " +
            $"systemctl reset-failed '{unitName}' 2>/dev/null || true; " +
            $"systemd-run --quiet --unit='{unitName}' --collect --property=Type=exec " +
            $"/usr/sbin/sshd -D -e -p {sshPort} -o KexAlgorithms=curve25519-sha256",
            TimeSpan.FromSeconds(15),
            inputViaStdin: true,
            user: "root");
        Assert.Equal(0, start.ExitCode);

        var inspect = await _fixture.RunInWslAsync(
            "for i in $(seq 1 50); do " +
            $"if systemctl is-active --quiet '{unitName}'; then " +
            $"pid=$(systemctl show '{unitName}' -p MainPID --value); " +
            "if [ \"$pid\" != '0' ] && [ -r \"/proc/$pid/cmdline\" ]; then " +
            "exe=$(readlink -f \"/proc/$pid/exe\" 2>/dev/null || true); " +
            "cmd=$(tr '\\0' ' ' < \"/proc/$pid/cmdline\" 2>/dev/null || true); " +
            $"if [ \"$exe\" = '/usr/sbin/sshd' ] && [[ \"$cmd\" == *'-D'* ]] && [[ \"$cmd\" == *'-e'* ]] && [[ \"$cmd\" == *'-p {sshPort}'* ]]; then " +
            "printf '%s\\n%s\\n%s\\n' \"$pid\" \"$exe\" \"$cmd\"; exit 0; fi; fi; " +
            "fi; sleep 0.1; done; " +
            $"systemctl status '{unitName}' --no-pager >&2 || true; " +
            $"journalctl -u '{unitName}' -n 50 --no-pager >&2 || true; exit 1",
            TimeSpan.FromSeconds(15),
            inputViaStdin: true,
            user: "root");
        Assert.Equal(0, inspect.ExitCode);
        var inspectionLines = inspect.Stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.True(inspectionLines.Length >= 3, $"Missing sshd PID/command proof: {inspect.Stdout}");
        Assert.True(int.TryParse(inspectionLines[0], out var pid), $"Invalid sshd PID: {inspectionLines[0]}");
        Assert.Equal("/usr/sbin/sshd", inspectionLines[1]);
        Assert.Contains("-D", inspectionLines[2], StringComparison.Ordinal);
        Assert.Contains("-e", inspectionLines[2], StringComparison.Ordinal);
        Assert.Contains($"-p {sshPort}", inspectionLines[2], StringComparison.Ordinal);

        const string hostAddress = "127.0.0.1";

        ProcessResult? preflight = null;
        var preflightTimeout = TimeSpan.FromSeconds(30);
        var preflightStopwatch = Stopwatch.StartNew();
        while (true)
        {
            var remaining = preflightTimeout - preflightStopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;
            var attemptTimeout = TimeSpan.FromMilliseconds(
                Math.Min(TimeSpan.FromSeconds(5).TotalMilliseconds, remaining.TotalMilliseconds));
            preflight = await RunProcessAsync(
                "ssh.exe",
                [
                    "-o", "BatchMode=yes",
                    "-o", "IdentitiesOnly=yes",
                    "-o", "StrictHostKeyChecking=no",
                    "-o", "UserKnownHostsFile=NUL",
                    "-o", $"ProxyCommand=wsl.exe -d {_fixture.DistroName} -- nc 127.0.0.1 {sshPort}",
                    "-i", Path.Combine(sshDir, "id_ed25519"),
                    "-p", sshPort.ToString(),
                    $"openclaw@{hostAddress}",
                    "true"
                ],
                new Dictionary<string, string>
                {
                    ["HOME"] = profileDir,
                    ["USERPROFILE"] = profileDir,
                },
                attemptTimeout);
            if (preflight.ExitCode == 0)
                break;
            remaining = preflightTimeout - preflightStopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;
            await Task.Delay(TimeSpan.FromMilliseconds(
                Math.Min(TimeSpan.FromMilliseconds(250).TotalMilliseconds, remaining.TotalMilliseconds)));
        }
        Assert.NotNull(preflight);
        Assert.True(
            preflight.ExitCode == 0,
            $"SSH preflight failed ({preflight.ExitCode}): " +
            TokenSanitizer.SanitizeLogMessage(preflight.Stderr));
        return new ProofSshdProcess(
            unitName,
            pid,
            inspectionLines[1],
            inspectionLines[2],
            hostAddress);
    }

    private async Task<JsonDocument> ReadStatusAsync() =>
        await _fixture.Client!.CallToolExpectSuccessAsync("app.status");

    private async Task<JsonDocument> WaitForConnectedPendingApprovalsAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        string last = "<none>";
        while (DateTime.UtcNow < deadline)
        {
            using var document = await _fixture.Client!.CallToolExpectSuccessAsync(
                "app.connection.pendingApprovals");
            last = document.RootElement.GetRawText();
            if (document.RootElement.GetProperty("connected").GetBoolean())
                return JsonDocument.Parse(last);
            await Task.Delay(500);
        }

        throw new TimeoutException($"Pending approvals never reached connected state. Last: {last}");
    }

    private async Task<JsonDocument> WaitForStatusAsync(
        Func<JsonElement, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        string last = "<none>";
        while (DateTime.UtcNow < deadline)
        {
            using var document = await ReadStatusAsync();
            last = document.RootElement.GetRawText();
            if (predicate(document.RootElement))
                return JsonDocument.Parse(last);
            await Task.Delay(500);
        }
        throw new TimeoutException($"Status predicate was not satisfied. Last: {last}");
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

    private static void AssertReady(JsonElement status)
    {
        Assert.Equal("Ready", status.GetProperty("overallState").GetString());
        Assert.Equal("Connected", status.GetProperty("operatorState").GetString());
        Assert.Equal("Connected", status.GetProperty("nodeState").GetString());
        Assert.True(status.GetProperty("nodePaired").GetBoolean());
    }

    private Task<JsonDocument> WaitForReadyStatusAsync(TimeSpan timeout) =>
        WaitForStatusAsync(
            status =>
                status.GetProperty("overallState").GetString() == "Ready" &&
                status.GetProperty("operatorState").GetString() == "Connected" &&
                status.GetProperty("nodeState").GetString() == "Connected" &&
                status.GetProperty("nodePaired").GetBoolean(),
            timeout);

    private static async Task<WindowsTcpListenerSnapshotResult> WaitForListenerSnapshotAsync(
        Func<IReadOnlyList<WindowsTcpListenerInfo>, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        WindowsTcpListenerSnapshotResult? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = WindowsTcpListenerSnapshot.Capture();
            if (last.Ipv4Complete && last.Ipv6Complete && predicate(last.Listeners))
                return last;
            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"Listener predicate was not satisfied. IPv4 complete: {last?.Ipv4Complete}; " +
            $"IPv6 complete: {last?.Ipv6Complete}; listener count: {last?.Listeners.Count ?? 0}.");
    }

    private async Task NavigateAndCaptureAsync(string page, string outputPath)
    {
        var captureStartedAt = DateTime.UtcNow;
        using var navigate = await _fixture.Client!.CallToolExpectSuccessAsync(
            "app.navigate",
            new { page });
        Assert.True(navigate.RootElement.GetProperty("navigated").GetBoolean());
        var captureDirectory = Path.Combine(
            Path.GetDirectoryName(outputPath)!,
            "Connection");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var capture = Directory.Exists(captureDirectory)
                ? Directory.EnumerateFiles(captureDirectory, "capture-*.png")
                    .Select(path => new FileInfo(path))
                    .Where(file => file.LastWriteTimeUtc >= captureStartedAt.AddSeconds(-1))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
            if (capture is not null)
            {
                try
                {
                    var isComposedFrame = false;
                    using (var stream = new FileStream(
                               capture.FullName,
                               FileMode.Open,
                               FileAccess.Read,
                               FileShare.ReadWrite))
                    using (var bitmap = new Bitmap(stream))
                    {
                        var sampledColors = new HashSet<int>();
                        var xStep = Math.Max(1, bitmap.Width / 40);
                        var yStep = Math.Max(1, bitmap.Height / 40);
                        for (var y = 0; y < bitmap.Height; y += yStep)
                        {
                            for (var x = 0; x < bitmap.Width; x += xStep)
                                sampledColors.Add(bitmap.GetPixel(x, y).ToArgb());
                        }
                        isComposedFrame = sampledColors.Count >= 8;
                    }

                    if (isComposedFrame)
                    {
                        File.Copy(capture.FullName, outputPath, overwrite: true);
                        return;
                    }
                }
                catch (ArgumentException)
                {
                    // Capture is still being encoded; retry.
                }
                catch (IOException)
                {
                    // Capture is still being encoded; retry.
                }
            }

            await Task.Delay(250);
        }

        throw new InvalidOperationException(
            "Connection page did not produce a composed XAML frame for proof capture.");
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

    private static string? ReadString(JsonElement root, string propertyName)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }
        return null;
    }

    private static string ResolveHeadSha()
    {
        var result = RunProcessAsync("git.exe", ["rev-parse", "HEAD"])
            .GetAwaiter()
            .GetResult();
        return result.ExitCode == 0 ? result.Stdout.Trim() : "unknown";
    }

    private static int AllocateFreeForwardPortPair()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var candidate = Random.Shared.Next(20_000, 40_000);
            IReadOnlyList<TcpListener>? gatewayForward = null;
            IReadOnlyList<TcpListener>? browserForward = null;
            try
            {
                gatewayForward = StartExclusiveLoopbackListeners(candidate);
                browserForward = StartExclusiveLoopbackListeners(candidate + 2);
                return candidate;
            }
            catch (SocketException)
            {
                // Try another pair.
            }
            finally
            {
                StopListeners(browserForward);
                StopListeners(gatewayForward);
            }
        }

        throw new InvalidOperationException("Unable to allocate an SSH forward port pair.");
    }

    private static IReadOnlyList<TcpListener> StartExclusiveLoopbackListeners(int port)
    {
        var listeners = new List<TcpListener>(MirroredWslPortLease.BindProbeAddresses.Count);
        try
        {
            foreach (var address in MirroredWslPortLease.BindProbeAddresses)
            {
                var listener = new TcpListener(address, port);
                listeners.Add(listener);
                listener.Server.ExclusiveAddressUse = true;
                listener.Start();
            }

            return listeners;
        }
        catch
        {
            StopListeners(listeners);
            throw;
        }
    }

    private static void StopListeners(IReadOnlyList<TcpListener>? listeners)
    {
        if (listeners is null)
            return;

        foreach (var listener in listeners)
            listener.Stop();
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private sealed record ProofSshdProcess(
        string UnitName,
        int ProcessId,
        string ExecutablePath,
        string CommandLine,
        string HostAddress);

    private sealed class InitialHandshakeRaceTunnelManager(string webSocketUrl)
        : ISshTunnelManager
    {
        public bool OwnedListenerReady { get; set; } = true;
        public int OwnedListenerCheckCount { get; private set; }
        public bool IsActive { get; private set; }
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
            return Task.FromResult(webSocketUrl);
        }

        public Task StopAsync()
        {
            IsActive = false;
            ActiveConfig = null;
            return Task.CompletedTask;
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
