using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using OpenClaw.Connection;
using OpenClaw.SetupEngine;
using OpenClawTray.Services;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.Tests;

public sealed class SetupDashboardHandoffTests
{
    private static GatewayRecord Gateway => new()
    {
        Id = "gateway-a", Url = "wss://gateway.example/control/",
    };

    private static GatewayAiSetupCompletion Receipt(SetupCompletionIntent intent = SetupCompletionIntent.CustodianOnboarding) =>
        new(intent, Gateway.Id, GatewayDashboardBinding.Capture(Gateway), "provider/model", "agent-a", 7,
            IdentityBinding: new string('B', 64), SessionKey: "agent:agent-a:main");

    private static SetupNativeCompletion Native(GatewayAiSetupCompletion receipt) =>
        new(receipt, new(SetupNativeDestination.Chat, "agent:agent-a:main"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeStartup_DoesNotWaitForUpdatePromptOrExitForInstaller(bool acceptInstaller)
    {
        using var directory = new TempDirectory();
        var clock = new StartupClock();
        var store = new SetupDashboardHandoffStore(directory.Path, clock);
        var handle = store.Issue(Native(Receipt()));
        var router = new ActivationRouter("openclaw", "unused-source-test");
        var input = new LaunchActivationInput(null, [], handle, false);
        var heldPrompt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var updateCalls = 0;
        var startup = router.CheckOrdinaryStartupUpdateAsync(input, () =>
        {
            updateCalls++;
            clock.Now += TimeSpan.FromMinutes(6);
            return acceptInstaller ? Task.FromResult(false) : heldPrompt.Task;
        });
        Assert.True(await startup.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, updateCalls);
        using var lease = store.Acquire(handle).Lease;
        Assert.NotNull(lease);
        Assert.False(lease!.IsExpired);
        Assert.Equal(handle, Assert.IsType<ActivationRoute.CompleteAiSetup>(
            Assert.IsType<ActivationPlan.Dispatch>(router.PlanLaunch(input)).Route).Handle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("chat")]
    [InlineData("ai-v3:invalid")]
    public async Task OrdinaryStartup_StillRunsUpdateAndHonorsInstallerExit(string? target)
    {
        var router = new ActivationRouter("openclaw", "unused-source-test");
        var updates = 0;
        Assert.False(await router.CheckOrdinaryStartupUpdateAsync(new(null, [], target, false),
            () => { updates++; return Task.FromResult(false); }));
        Assert.Equal(1, updates);
    }

    [Theory]
    [InlineData(SetupCompletionIntent.Dashboard)]
    [InlineData(SetupCompletionIntent.CustodianOnboarding)]
    public void RestartAndForwardedActivation_PreserveTypedReceipt(SetupCompletionIntent intent)
    {
        var receipt = Receipt(intent);
        using var directory = new TempDirectory();
        var store = new SetupDashboardHandoffStore(directory.Path);
        var encoded = store.Issue(Native(receipt));
        Assert.Equal(encoded, SetupDashboardHandoff.ParseHandle(encoded));
        var router = new ActivationRouter("openclaw", "unused-source-test");
        foreach (var input in new[]
        {
            new LaunchActivationInput(null, [], encoded, false),
            new LaunchActivationInput(
                "openclaw://setup-dashboard?handle=" + Uri.EscapeDataString(encoded), [], null, false),
        })
        {
            var plan = Assert.IsType<ActivationPlan.Dispatch>(router.PlanLaunch(input));
            Assert.Equal(encoded, Assert.IsType<ActivationRoute.CompleteAiSetup>(plan.Route).Handle);
        }
        using var lease = store.Acquire(encoded).Lease;
        Assert.Equal(receipt, lease!.Completion);
        lease.Consume();
    }

    [Theory]
    [InlineData("chat", "chat")]
    [InlineData("settings", "settings")]
    [InlineData("connection", "connection")]
    public void OldRestartArguments_KeepTheirRoutes(string value, string page)
    {
        var router = new ActivationRouter("openclaw", "unused-source-test");
        var plan = Assert.IsType<ActivationPlan.Dispatch>(
            router.PlanLaunch(new(null, [], value, false)));
        Assert.Equal(page, Assert.IsType<ActivationRoute.OpenHub>(plan.Route).Page);
    }

    [Theory]
    [InlineData("ai-v1:invalid")]
    [InlineData("ai-v2:0000000000000000000000000000000000000000000000000000000000000000")]
    public void RetiredOrInvalidHandoff_IsVisibleFailureRoute_NotGlobalDashboardFallback(string handle)
    {
        var router = new ActivationRouter("openclaw", "unused-source-test");
        var plan = Assert.IsType<ActivationPlan.Dispatch>(
            router.PlanLaunch(new(null, [], handle, false)));
        Assert.Null(Assert.IsType<ActivationRoute.CompleteAiSetup>(plan.Route).Handle);
        using var directory = new TempDirectory();
        var store = new SetupDashboardHandoffStore(directory.Path);
        Assert.Equal(SetupHandoffAcquisitionStatus.Invalid, store.Acquire(handle).Status);
        Assert.Throws<SetupNativeOwnershipException>(() => store.Issue(Native(Receipt() with { ModelTarget = "utility" })));
        Assert.Throws<InvalidOperationException>(() => store.Issue(Native(Receipt() with { VerifiedGeneration = 0 })));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("channels", "/channels")]
    public async Task OrdinaryDashboard_UsesRequestedPathAndNormalCredentials(string? path, string suffix)
    {
        string? launched = null;
        var launcher = new GatewayDashboardLauncher(() => true,
            () => new(Gateway.Url, "synthetic shared", false, CredentialResolver.SourceSharedGatewayToken),
            url => { launched = url; return Task.FromResult(true); },
            () => throw new InvalidOperationException("Unexpected failure"));
        Assert.True(await launcher.OpenAsync(path));
        Assert.Equal("https://gateway.example/control" + suffix + "#token=synthetic%20shared", launched);
        Assert.DoesNotContain("custodian", launched);
    }

    [Theory]
    [InlineData(CredentialResolver.SourceDeviceToken, false)]
    [InlineData(CredentialResolver.SourceBootstrapToken, true)]
    public async Task DeviceAndBootstrapTokens_NeverEnterBrowserUrl(string source, bool bootstrap)
    {
        string? launched = null;
        var launcher = new GatewayDashboardLauncher(() => true,
            () => new(Gateway.Url, "do-not-export", bootstrap, source),
            url => { launched = url; return Task.FromResult(true); },
            () => throw new InvalidOperationException("Unexpected failure"));
        Assert.True(await launcher.OpenAsync());
        Assert.Equal("https://gateway.example/control", launched);
    }

    [Theory]
    [InlineData("tunnel")]
    [InlineData("credential")]
    [InlineData("browser")]
    [InlineData("exception")]
    public async Task Failure_IsReportedOnceWithoutAutomaticRetry(string failure)
    {
        var launches = 0;
        var failures = 0;
        var launcher = new GatewayDashboardLauncher(() => failure != "tunnel",
            () => failure == "credential" ? null : new(Gateway.Url, "shared", false, CredentialResolver.SourceSharedGatewayToken),
            _ =>
            {
                launches++;
                if (failure == "exception") throw new InvalidOperationException("synthetic-secret-url");
                return Task.FromResult(false);
            }, () => failures++);
        Assert.False(await launcher.OpenAsync());
        Assert.Equal(failure is "browser" or "exception" ? 1 : 0, launches);
        Assert.Equal(1, failures);
    }

    [Fact]
    public void EndpointBinding_ExcludesRotatingTokensButIncludesSshRealm()
    {
        var original = Gateway with { SshTunnel = new("user", "ssh.example", 18789, 19001) };
        var binding = GatewayDashboardBinding.Capture(original);
        Assert.Equal(binding, GatewayDashboardBinding.Capture(original with { SharedGatewayToken = "rotated" }));
        Assert.NotEqual(binding, GatewayDashboardBinding.Capture(
            original with { SshTunnel = original.SshTunnel! with { Host = "other.example" } }));
    }

    [Fact]
    public async Task ExplicitDashboardRetry_KeepsRequestedPathAndClearsFailureOnlyAfterOpen()
    {
        var urls = new List<string>();
        var failures = 0;
        var opened = 0;
        var launcher = new GatewayDashboardLauncher(() => true,
            () => new(Gateway.Url, "synthetic", false, CredentialResolver.SourceSharedGatewayToken),
            url => { urls.Add(url); return Task.FromResult(urls.Count == 2); },
            () => failures++, () => opened++);
        Assert.False(await launcher.OpenAsync("channels"));
        Assert.Equal(0, opened);
        Assert.Single(urls);
        Assert.True(await launcher.OpenAsync("channels"));
        Assert.Equal(urls[0], urls[1]);
        Assert.Equal(1, failures);
        Assert.Equal(1, opened);
    }

    [Fact]
    public void IssuedBinding_RejectsSwitchToAnotherGateway()
    {
        var tunnel = new SshTunnelConfig("root", "gateway.example", 18789, 45678, false, 22);
        var issued = new GatewayRecord
        {
            Id = "gateway-a",
            Url = "wss://gateway.example/mount/",
            SharedGatewayToken = "token-a",
            SshTunnel = tunnel,
        };
        var other = new GatewayRecord { Id = "gateway-b", Url = "ws://127.0.0.1:18789/" };

        Assert.True(DashboardIssuedBinding.Matches(issued, tunnel, issued));
        Assert.False(DashboardIssuedBinding.Matches(issued, tunnel, other));
        Assert.False(DashboardIssuedBinding.Matches(issued, tunnel, null));
    }

    [Fact]
    public async Task DashboardHandoff_HidesCredentialUntilOwnershipHoldsOnce()
    {
        using var origins = new TempDirectory("ocwn-1484-origins-");
        DashboardCredentialHandoff.OriginStorePath = origins.Combine("origins.json");
        DashboardCredentialHandoff.ResetOriginsForTests();
        const string destination = "https://localhost:45678/mount/?view=compact#section&token=secret";
        var open = DashboardCredentialHandoff.Start(() => Task.FromResult(true), destination);
        Assert.DoesNotContain("token=", open, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($":{DashboardCredentialHandoff.Port}/", open, StringComparison.Ordinal);
        using var http = new HttpClient();
        var first = await http.GetStringAsync(open);
        Assert.Contains("token=secret", first, StringComparison.Ordinal);
        Assert.Contains("/s/", first, StringComparison.Ordinal);
        Assert.DoesNotContain("45678", first, StringComparison.Ordinal);
        Assert.Equal(
            "application/javascript",
            DashboardCredentialHandoff.ReadContentType(
                "HTTP/1.1 200 OK\r\nContent-Type: application/javascript\r\n\r\n"));
        Assert.Equal(
            "<script src=\"/s/abc/assets/app.js\"></script>",
            DashboardCredentialHandoff.RewriteRootAbsolute(
                "<script src=\"/assets/app.js\"></script>",
                "/s/abc"));
        Assert.Equal("/s/abc/assets/app.js", DashboardCredentialHandoff.RewriteLocation("/assets/app.js", "/s/abc"));
        var other = DashboardCredentialHandoff.Start(() => Task.FromResult(true), destination, originKey: "other-gateway");
        Assert.DoesNotContain($":{DashboardCredentialHandoff.Port}/", other, StringComparison.Ordinal);
        Assert.Equal(
            42,
            WindowsTcpListenerSnapshot.MatchAcceptedProcess(
                [
                    new WindowsTcpListenerSnapshot.AcceptedTcpEndpoint(IPAddress.Loopback, 45678, IPAddress.Loopback, 51000, 42),
                    new WindowsTcpListenerSnapshot.AcceptedTcpEndpoint(IPAddress.Loopback, 51000, IPAddress.Loopback, 45678, 7),
                    new WindowsTcpListenerSnapshot.AcceptedTcpEndpoint(IPAddress.IPv6Loopback, 45678, IPAddress.IPv6Loopback, 51000, 9),
                ],
                new IPEndPoint(IPAddress.Loopback, 45678),
                new IPEndPoint(IPAddress.Loopback, 51000)));
        Assert.Equal(
            9,
            WindowsTcpListenerSnapshot.MatchAcceptedProcess(
                [
                    new WindowsTcpListenerSnapshot.AcceptedTcpEndpoint(IPAddress.Loopback, 45678, IPAddress.Loopback, 51000, 42),
                    new WindowsTcpListenerSnapshot.AcceptedTcpEndpoint(IPAddress.IPv6Loopback, 45678, IPAddress.IPv6Loopback, 51000, 9),
                ],
                new IPEndPoint(IPAddress.IPv6Loopback, 45678),
                new IPEndPoint(IPAddress.IPv6Loopback, 51000)));
        Assert.Equal(
            9,
            WindowsTcpListenerSnapshot.MatchListener(
                [
                    new WindowsTcpListenerInfo(IPAddress.Loopback, 45678, 42, null, null),
                    new WindowsTcpListenerInfo(IPAddress.IPv6Loopback, 45678, 9, null, null),
                ],
                new IPEndPoint(IPAddress.IPv6Loopback, 45678))?.ProcessId);
        Assert.Equal(
            "hello",
            Encoding.ASCII.GetString(DashboardCredentialHandoff.DecodeChunked(
                Encoding.ASCII.GetBytes("5\r\nhello\r\n0\r\n\r\n"))!));
        var denied = await http.GetAsync(open);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.DoesNotContain("secret", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var closed = DashboardCredentialHandoff.Start(() => Task.FromResult(false), destination, originKey: "closed-gateway");
        var blocked = await http.GetAsync(closed);
        Assert.Equal(HttpStatusCode.NotFound, blocked.StatusCode);
        Assert.DoesNotContain("secret", await blocked.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DashboardHandoff_RejectsAnotherGatewaysNonceOnThisOrigin()
    {
        using var origins = new TempDirectory("ocwn-1484-origins-");
        DashboardCredentialHandoff.OriginStorePath = origins.Combine("origins.json");
        DashboardCredentialHandoff.ResetOriginsForTests();
        const string destination = "https://localhost:45678/mount/?view=compact#token=secret";
        var first = DashboardCredentialHandoff.Start(() => Task.FromResult(true), destination, originKey: "gateway-a");
        var second = DashboardCredentialHandoff.Start(() => Task.FromResult(true), destination, originKey: "gateway-b");
        var firstUri = new Uri(first);
        var secondUri = new Uri(second);
        Assert.NotEqual(firstUri.Port, secondUri.Port);
        var nonceB = secondUri.AbsolutePath["/d/".Length..];
        using var http = new HttpClient();
        var cross = await http.GetAsync($"http://127.0.0.1:{firstUri.Port}/s/{nonceB}/");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        Assert.DoesNotContain("secret", await cross.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var referred = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{firstUri.Port}/assets/app.js");
        referred.Headers.Referrer = new Uri($"http://127.0.0.1:{secondUri.Port}/s/{nonceB}/");
        var referredResponse = await http.SendAsync(referred);
        Assert.Equal(HttpStatusCode.NotFound, referredResponse.StatusCode);
    }

    [Fact]
    public void DashboardHandoff_ReusesSavedOriginAfterRestart()
    {
        using var origins = new TempDirectory("ocwn-1484-origins-");
        DashboardCredentialHandoff.OriginStorePath = origins.Combine("origins.json");
        DashboardCredentialHandoff.ResetOriginsForTests();
        const string destination = "https://localhost:45678/mount/";
        var first = DashboardCredentialHandoff.Start(() => Task.FromResult(true), destination, originKey: "gateway-a");
        var port = new Uri(first).Port;
        DashboardCredentialHandoff.ResetOriginsForTests();
        var second = DashboardCredentialHandoff.Start(() => Task.FromResult(true), destination, originKey: "gateway-a");
        Assert.Equal(port, new Uri(second).Port);
        Assert.Equal(port, new Uri(DashboardCredentialHandoff.Start(() => Task.FromResult(true), destination, originKey: "gateway-a")).Port);
    }

    [Fact]
    public async Task AcceptedProcessId_MatchesTheIpv6LoopbackConnection()
    {
        using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();
        var server = (IPEndPoint)listener.LocalEndpoint;
        using var client = new TcpClient(AddressFamily.InterNetworkV6);
        await client.ConnectAsync(server);
        var local = (IPEndPoint)client.Client.LocalEndPoint!;
        using var accepted = await listener.AcceptTcpClientAsync();
        Assert.Equal(Environment.ProcessId, WindowsTcpListenerSnapshot.AcceptedProcessId(server, local));
        var match = WindowsTcpListenerSnapshot.MatchListener(WindowsTcpListenerSnapshot.Capture().Listeners, server);
        Assert.Equal(Environment.ProcessId, match?.ProcessId);
    }

    [Fact]
    public async Task DashboardHandoff_DecodesChunkedUpstreamBody()
    {
        using var origins = new TempDirectory("ocwn-1484-origins-");
        DashboardCredentialHandoff.OriginStorePath = origins.Combine("origins.json");
        DashboardCredentialHandoff.ResetOriginsForTests();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var upstreamPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buffer = new byte[2048];
            var read = 0;
            while (read < 4 || !Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read));
                if (count == 0)
                    break;
                read += count;
            }
            var response =
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n" +
                "b\r\n{\"ok\":true}\r\n0\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
        });

        var open = DashboardCredentialHandoff.Start(
            () => Task.FromResult(true),
            $"http://127.0.0.1:{upstreamPort}/item",
            originKey: "chunk-gateway");
        using var http = new HttpClient();
        var page = await http.GetStringAsync(open);
        var marker = "location.replace(";
        var start = page.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var jsonStart = start + marker.Length;
        var jsonEnd = page.IndexOf(")</script>", jsonStart, StringComparison.Ordinal);
        var sessionUrl = System.Text.Json.JsonSerializer.Deserialize<string>(page[jsonStart..jsonEnd]);
        Assert.NotNull(sessionUrl);
        var body = await http.GetStringAsync(sessionUrl);
        Assert.Equal("{\"ok\":true}", body);
        Assert.DoesNotContain("\r\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenPrepared_RefusesBrowserWhenOwnershipRecheckFails()
    {
        var launches = 0;
        var launcher = new GatewayDashboardLauncher(
            () => true,
            () => null,
            _ =>
            {
                launches++;
                return Task.FromResult(true);
            },
            () => throw new InvalidOperationException("Unexpected failure"));

        Assert.False(await launcher.OpenPreparedAsync(
            "https://localhost/control#token=secret",
            () => Task.FromResult(false)));
        Assert.Equal(0, launches);
    }

    private sealed class StartupClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
