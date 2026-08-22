using System.Text.Json;

namespace OpenClaw.Connection.Tests;

public sealed class GatewayTailscaleAuthLiveVerifierTests
{
    [Fact]
    public void ConnectionManager_WiresWslLiveVerifierIntoDashboardRevalidation()
    {
        var source = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src",
            "OpenClaw.Connection",
            "GatewayConnectionManager.cs"));
        var revalidationStart = source.IndexOf(
            "public async Task<bool> RevalidateTailscaleDashboardAuthAsync(",
            StringComparison.Ordinal);
        var diagnosticsStart = source.IndexOf(
            "public ConnectionDiagnostics Diagnostics",
            revalidationStart,
            StringComparison.Ordinal);

        Assert.True(revalidationStart >= 0);
        Assert.True(diagnosticsStart > revalidationStart);
        Assert.Contains(
            "new GatewayTailscaleAuthLiveVerifier(\n            new WslExeCommandRunner(_logger))",
            source.Replace("\r\n", "\n", StringComparison.Ordinal));
        var revalidation = source[revalidationStart..diagnosticsStart]
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(
            "new GatewayTailscaleAuthUpgradeService(\n" +
            "                _registry,\n" +
            "                _tailscaleAuthLiveVerifier);",
            revalidation);
    }

    [Theory]
    [MemberData(nameof(StatusResults))]
    public async Task VerifyAsync_ClassifiesStatusResult(
        WslCommandResult commandResult,
        string expected)
    {
        var runner = new FakeWslCommandRunner((_, command, _) => Task.FromResult(
            command.Contains("serve", StringComparer.Ordinal)
                ? ServeStatus()
                : commandResult));
        var verifier = new GatewayTailscaleAuthLiveVerifier(runner, TimeSpan.FromSeconds(1));

        var result = await verifier.VerifyAsync(ManagedRecord(), 18789, CancellationToken.None);

        Assert.Equal(expected, result.ToString());
        Assert.Equal("OpenClawGateway", runner.DistroName);
        Assert.Equal(["/usr/bin/tailscale", "status", "--json"], runner.Commands[0]);
        Assert.Equal(
            ["-d", "OpenClawGateway", "--user", "root", "--", "/usr/bin/tailscale", "status", "--json"],
            runner.HostArguments[0]);
        Assert.Equal(expected == "Ready" ? 2 : 1, runner.ProbeCalls);
        if (expected == "Ready")
        {
            Assert.Equal(["/usr/bin/tailscale", "serve", "status", "--json"], runner.Commands[1]);
            Assert.Equal(
                ["-d", "OpenClawGateway", "--user", "root", "--", "/usr/bin/tailscale", "serve", "status", "--json"],
                runner.HostArguments[1]);
        }
    }

    [Fact]
    public async Task VerifyAsync_RecordWithoutManagedDistroIsUnavailableWithoutCommand()
    {
        var runner = new FakeWslCommandRunner((_, _, _) =>
            Task.FromResult(RunningStatus()));
        var verifier = new GatewayTailscaleAuthLiveVerifier(runner, TimeSpan.FromSeconds(1));

        var result = await verifier.VerifyAsync(
            ManagedRecord() with { SetupManagedDistroName = null, FriendlyName = null },
            18789,
            CancellationToken.None);

        Assert.Equal(GatewayTailscaleAuthLiveState.Unavailable, result);
        Assert.Equal(0, runner.ProbeCalls);
    }

    [Fact]
    public async Task VerifyAsync_RunnerFailureIsUnavailable()
    {
        var runner = new FakeWslCommandRunner((_, _, _) =>
            Task.FromException<WslCommandResult>(new InvalidOperationException("wsl unavailable")));
        var verifier = new GatewayTailscaleAuthLiveVerifier(runner, TimeSpan.FromSeconds(1));

        var result = await verifier.VerifyAsync(ManagedRecord(), 18789, CancellationToken.None);

        Assert.Equal(GatewayTailscaleAuthLiveState.Unavailable, result);
    }

    [Fact]
    public async Task VerifyAsync_TimeoutIsUnavailable()
    {
        var runner = new FakeWslCommandRunner(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        var verifier = new GatewayTailscaleAuthLiveVerifier(runner, TimeSpan.FromMilliseconds(20));

        var result = await verifier.VerifyAsync(ManagedRecord(), 18789, CancellationToken.None);

        Assert.Equal(GatewayTailscaleAuthLiveState.Unavailable, result);
    }

    [Fact]
    public async Task VerifyAsync_CallerCancellationPropagates()
    {
        var runner = new FakeWslCommandRunner(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        var verifier = new GatewayTailscaleAuthLiveVerifier(runner, TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            verifier.VerifyAsync(ManagedRecord(), 18789, cancellation.Token));
    }

    [Theory]
    [InlineData("other.tail.example.", 18789, false, "NotReady")]
    [InlineData("host.tail.example.", 19999, false, "NotReady")]
    [InlineData("host.tail.example.", 18789, true, "NotReady")]
    public async Task VerifyAsync_FailsClosedWhenEndpointOrServeRouteDoesNotMatch(
        string dnsName,
        int proxyPort,
        bool funnelEnabled,
        string expected)
    {
        var runner = new FakeWslCommandRunner((_, command, _) => Task.FromResult(
            command.Contains("serve", StringComparer.Ordinal)
                ? ServeStatus(proxyPort: proxyPort, funnelEnabled: funnelEnabled)
                : Status("Running", dnsName)));
        var verifier = new GatewayTailscaleAuthLiveVerifier(runner, TimeSpan.FromSeconds(1));

        var result = await verifier.VerifyAsync(ManagedRecord(), 18789, CancellationToken.None);

        Assert.Equal(expected, result.ToString());
    }

    [Fact]
    public async Task VerifyAsync_UnrelatedServeHostIsNotReady()
    {
        var runner = new FakeWslCommandRunner((_, command, _) => Task.FromResult(
            command.Contains("serve", StringComparer.Ordinal)
                ? ServeStatus(host: "other.tail.example")
                : RunningStatus()));
        var verifier = new GatewayTailscaleAuthLiveVerifier(runner, TimeSpan.FromSeconds(1));

        var result = await verifier.VerifyAsync(ManagedRecord(), 18789, CancellationToken.None);

        Assert.Equal(GatewayTailscaleAuthLiveState.NotReady, result);
    }

    [Fact]
    public async Task VerifyAsync_AcceptsCurrentForegroundServeStatusShape()
    {
        var runner = new FakeWslCommandRunner((_, command, _) => Task.FromResult(
            command.Contains("serve", StringComparer.Ordinal)
                ? ForegroundServeStatus()
                : RunningStatus()));
        var verifier = new GatewayTailscaleAuthLiveVerifier(runner, TimeSpan.FromSeconds(1));

        var result = await verifier.VerifyAsync(ManagedRecord(), 18789, CancellationToken.None);

        Assert.Equal(GatewayTailscaleAuthLiveState.Ready, result);
    }

    [Theory]
    [MemberData(nameof(UnsafeForegroundServeStatuses))]
    public async Task VerifyAsync_FailsClosedForUnsafeForegroundServeStatus(
        string foreground,
        string expected)
    {
        var runner = new FakeWslCommandRunner((_, command, _) => Task.FromResult(
            command.Contains("serve", StringComparer.Ordinal)
                ? new WslCommandResult(0, $"{{\"Foreground\":{foreground}}}", "")
                : RunningStatus()));
        var verifier = new GatewayTailscaleAuthLiveVerifier(runner, TimeSpan.FromSeconds(1));

        var result = await verifier.VerifyAsync(ManagedRecord(), 18789, CancellationToken.None);

        Assert.Equal(expected, result.ToString());
    }

    [Theory]
    [InlineData(8443, "", "NotReady")]
    [InlineData(443, "/unrelated", "NotReady")]
    [InlineData(443, "/app/..", "NotReady")]
    [InlineData(443, "\\backend", "NotReady")]
    [InlineData(443, "?target=other", "NotReady")]
    [InlineData(443, "#fragment", "NotReady")]
    public async Task VerifyAsync_RequiresExactPublicEndpointAndCoreRootProxy(
        int endpointPort,
        string proxySuffix,
        string expected)
    {
        var runner = new FakeWslCommandRunner((_, command, _) => Task.FromResult(
            command.Contains("serve", StringComparer.Ordinal)
                ? ServeStatus(endpointPort: endpointPort, proxySuffix: proxySuffix)
                : RunningStatus()));
        var verifier = new GatewayTailscaleAuthLiveVerifier(runner, TimeSpan.FromSeconds(1));

        var result = await verifier.VerifyAsync(ManagedRecord(), 18789, CancellationToken.None);

        Assert.Equal(expected, result.ToString());
    }

    [Theory]
    [InlineData("user:password@")]
    [InlineData("@")]
    public async Task VerifyAsync_RejectsProxyUserInfo(string proxyUserInfo)
    {
        var runner = new FakeWslCommandRunner((_, command, _) => Task.FromResult(
            command.Contains("serve", StringComparer.Ordinal)
                ? ServeStatus(proxyUserInfo: proxyUserInfo)
                : RunningStatus()));
        var verifier = new GatewayTailscaleAuthLiveVerifier(runner, TimeSpan.FromSeconds(1));

        var result = await verifier.VerifyAsync(ManagedRecord(), 18789, CancellationToken.None);

        Assert.Equal(GatewayTailscaleAuthLiveState.NotReady, result);
    }

    [Theory]
    [InlineData("http://host.tail.example:443")]
    [InlineData("https://user@host.tail.example:443")]
    [InlineData("https://@host.tail.example:443")]
    [InlineData("https://host.tail.example:443/path")]
    [InlineData("https://host.tail.example:443/app/..")]
    [InlineData("https://host.tail.example:443\\app")]
    [InlineData("https://host.tail.example:443?query=1")]
    [InlineData("https://host.tail.example:443#fragment")]
    public async Task VerifyAsync_RejectsNonCanonicalServeEndpoint(string endpoint)
    {
        var endpointJson = JsonSerializer.Serialize(endpoint);
        var serveJson = $$"""
            {
              "Web": {
                {{endpointJson}}: {
                  "Handlers": { "/": { "Proxy": "http://127.0.0.1:18789" } }
                }
              }
            }
            """;
        var runner = new FakeWslCommandRunner((_, command, _) => Task.FromResult(
            command.Contains("serve", StringComparer.Ordinal)
                ? new WslCommandResult(0, serveJson, "")
                : RunningStatus()));
        var verifier = new GatewayTailscaleAuthLiveVerifier(runner, TimeSpan.FromSeconds(1));

        var result = await verifier.VerifyAsync(ManagedRecord(), 18789, CancellationToken.None);

        Assert.Equal(GatewayTailscaleAuthLiveState.NotReady, result);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"Web\":[]}")]
    [InlineData("{\"Web\":{\"host.tail.example:443\":[]}}")]
    public async Task VerifyAsync_StructurallyUnexpectedServeStatusIsUnavailable(string serveJson)
    {
        var runner = new FakeWslCommandRunner((_, command, _) => Task.FromResult(
            command.Contains("serve", StringComparer.Ordinal)
                ? new WslCommandResult(0, serveJson, "")
                : RunningStatus()));
        var verifier = new GatewayTailscaleAuthLiveVerifier(runner, TimeSpan.FromSeconds(1));

        var result = await verifier.VerifyAsync(ManagedRecord(), 18789, CancellationToken.None);

        Assert.Equal(GatewayTailscaleAuthLiveState.Unavailable, result);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{\"Proxy\":42}")]
    public async Task VerifyAsync_MalformedSiblingHandlerIsUnavailable(string malformedHandler)
    {
        var serveJson = $$"""
            {
              "Web": {
                "host.tail.example:443": {
                  "Handlers": {
                    "/": { "Proxy": "http://127.0.0.1:18789" },
                    "/bad": {{malformedHandler}}
                  }
                }
              }
            }
            """;
        var runner = new FakeWslCommandRunner((_, command, _) => Task.FromResult(
            command.Contains("serve", StringComparer.Ordinal)
                ? new WslCommandResult(0, serveJson, "")
                : RunningStatus()));
        var verifier = new GatewayTailscaleAuthLiveVerifier(runner, TimeSpan.FromSeconds(1));

        var result = await verifier.VerifyAsync(ManagedRecord(), 18789, CancellationToken.None);

        Assert.Equal(GatewayTailscaleAuthLiveState.Unavailable, result);
    }

    [Theory]
    [InlineData("\"AllowFunnel\": false, \"Funnel\": true", "NotReady")]
    [InlineData("\"AllowFunnel\": 42", "Unavailable")]
    [InlineData("\"Funnel\": [false, {\"legacy\": true}]", "NotReady")]
    public async Task VerifyAsync_EvaluatesEveryFunnelAliasAndRejectsUnknownShapes(
        string funnelProperties,
        string expected)
    {
        var serveJson = $$"""
            {
              "Web": {
                "host.tail.example:443": {
                  "Handlers": { "/": { "Proxy": "http://127.0.0.1:18789" } }
                }
              },
              {{funnelProperties}}
            }
            """;
        var runner = new FakeWslCommandRunner((_, command, _) => Task.FromResult(
            command.Contains("serve", StringComparer.Ordinal)
                ? new WslCommandResult(0, serveJson, "")
                : RunningStatus()));
        var verifier = new GatewayTailscaleAuthLiveVerifier(runner, TimeSpan.FromSeconds(1));

        var result = await verifier.VerifyAsync(ManagedRecord(), 18789, CancellationToken.None);

        Assert.Equal(expected, result.ToString());
    }

    public static TheoryData<WslCommandResult, string> StatusResults => new()
    {
        { RunningStatus(), "Ready" },
        { Status("NeedsLogin", "host.tail.example."), "NotReady" },
        { Status("Stopped", "host.tail.example."), "NotReady" },
        { Status("Running", ""), "NotReady" },
        { new WslCommandResult(0, "{}", ""), "NotReady" },
        { new WslCommandResult(0, "not-json", ""), "Unavailable" },
        { new WslCommandResult(1, "", "tailscaled unavailable"), "Unavailable" },
    };

    public static TheoryData<string, string> UnsafeForegroundServeStatuses => new()
    {
        { "[]", "Unavailable" },
        { "{\"config-id\":[]}", "Unavailable" },
        {
            """
            {
              "config-id": {
                "Web": {
                  "host.tail.example:443": {
                    "Handlers": { "/": { "Proxy": "http://127.0.0.1:18789" } }
                  }
                },
                "AllowFunnel": true
              }
            }
            """,
            "NotReady"
        },
        {
            """
            {
              "gateway": {
                "Web": {
                  "host.tail.example:443": {
                    "Handlers": { "/": { "Proxy": "http://127.0.0.1:18789" } }
                  }
                }
              },
              "funnel": { "AllowFunnel": true }
            }
            """,
            "NotReady"
        },
        {
            """
            {
              "gateway": {
                "Web": {
                  "host.tail.example:443": {
                    "Handlers": { "/": { "Proxy": "http://127.0.0.1:18789" } }
                  }
                }
              },
              "malformed": []
            }
            """,
            "Unavailable"
        },
    };

    private static WslCommandResult RunningStatus() => Status("Running", "host.tail.example.");

    private static WslCommandResult ServeStatus(
        string host = "host.tail.example",
        int endpointPort = 443,
        int proxyPort = 18789,
        string proxySuffix = "",
        string proxyUserInfo = "",
        bool funnelEnabled = false)
    {
        var proxyJson = JsonSerializer.Serialize(
            $"http://{proxyUserInfo}127.0.0.1:{proxyPort}{proxySuffix}");
        return new(
            0,
            $$"""
            {
              "Web": {
                "{{host}}:{{endpointPort}}": {
                  "Handlers": { "/": { "Proxy": {{proxyJson}} } }
                }
              },
              "AllowFunnel": { "{{host}}:{{endpointPort}}": {{funnelEnabled.ToString().ToLowerInvariant()}} }
            }
            """,
            "");
    }

    private static WslCommandResult ForegroundServeStatus() =>
        new(
            0,
            """
            {
              "Foreground": {
                "75980230dda8b0e0": {
                  "TCP": { "443": { "HTTPS": true } },
                  "Web": {
                    "host.tail.example:443": {
                      "Handlers": { "/": { "Proxy": "http://127.0.0.1:18789" } }
                    }
                  }
                }
              }
            }
            """,
            "");

    private static string GetRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if ((Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                 File.Exists(Path.Combine(current.FullName, ".git"))) &&
                File.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "OpenClaw.Connection",
                    "GatewayConnectionManager.cs")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }

    private static WslCommandResult Status(string backendState, string dnsName) =>
        new(
            0,
            $"{{\"BackendState\":\"{backendState}\",\"Self\":{{\"DNSName\":\"{dnsName}\"}}}}",
            "");

    private static GatewayRecord ManagedRecord() => new()
    {
        Id = "gateway-1",
        Url = "wss://host.tail.example",
        IsLocal = true,
        SetupManagedDistroName = "OpenClawGateway",
        TrustTailscaleAuth = true,
    };

    private sealed class FakeWslCommandRunner(
        Func<string, IReadOnlyList<string>, CancellationToken, Task<WslCommandResult>> runInDistro)
        : IWslCommandRunner
    {
        public int ProbeCalls { get; private set; }
        public string? DistroName { get; private set; }
        public List<IReadOnlyList<string>> Commands { get; } = [];
        public List<IReadOnlyList<string>> HostArguments { get; } = [];

        public Task<WslCommandResult> RunInDistroAsync(
            string name,
            IReadOnlyList<string> command,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null)
            => throw new NotSupportedException();

        public Task<WslCommandResult> RunAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            HostArguments.Add(arguments);
            Assert.True(arguments.Count >= 6);
            Assert.Equal("-d", arguments[0]);
            Assert.Equal("--user", arguments[2]);
            Assert.Equal("root", arguments[3]);
            Assert.Equal("--", arguments[4]);
            ProbeCalls++;
            DistroName = arguments[1];
            var command = arguments.Skip(5).ToArray();
            Commands.Add(command);
            return runInDistro(DistroName, command, cancellationToken);
        }

        public Task<IReadOnlyList<WslDistroInfo>> ListDistrosAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WslCommandResult> TerminateDistroAsync(
            string name,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WslCommandResult> UnregisterDistroAsync(
            string name,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
