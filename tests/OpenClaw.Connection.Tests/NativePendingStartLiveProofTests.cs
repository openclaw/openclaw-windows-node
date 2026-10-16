using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.Shared;
using Xunit.Abstractions;

namespace OpenClaw.Connection.Tests;

public sealed class NativePendingStartLiveFactAttribute : FactAttribute
{
    public NativePendingStartLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENCLAW_RUN_NATIVE_PENDING_PROOF") != "1")
            Skip = "Requires an explicitly approved, prepared gw-startup synthetic session.";
    }
}

public sealed class NativePendingStartLiveProofTests(ITestOutputHelper output)
{
    private const string Family = "OpenClawFoundation.OpenClawGateway-gw-startup_rfcbke2p71se2";

    [NativePendingStartLiveFact]
    public async Task AcceptedPendingStartWaitsForRealAttestedListenerWithoutRestartOrCredentials()
    {
        Assert.True(OperatingSystem.IsWindows());
        var version = RequiredEnvironment("OPENCLAW_NATIVE_PROOF_VERSION");
        Assert.True(Version.TryParse(version, out _));
        var port = int.Parse(RequiredEnvironment("OPENCLAW_NATIVE_PROOF_PORT"), CultureInfo.InvariantCulture);
        Assert.InRange(port, 1, 65535);
        var proofPath = RequiredEnvironment("OPENCLAW_NATIVE_PROOF_OUTPUT");
        Assert.True(Path.IsPathFullyQualified(proofPath));
        Assert.False(File.Exists(proofPath), "Do not overwrite a previous proof.");
        var aliasDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", Family);
        var package = new NativeGatewayPackage(Family, version,
            Path.Combine(aliasDirectory, "openclaw-gw-startup.exe"),
            Path.Combine(aliasDirectory, "clawctl-gw-startup.exe"))
        {
            Contract = NativeGatewayContract.IsolatedSessionV1
        };
        NativeGatewayPaths.ValidatePackage(package, Family);
        Assert.True(File.Exists(package.ClawCtlAliasPath));
        Assert.True(File.Exists(package.OpenClawAliasPath));

        var record = new GatewayRecord
        {
            Id = "synthetic-pending-proof",
            Url = $"ws://127.0.0.1:{port}",
            NativePackageFamilyName = Family,
            NativeRuntimeContract = NativeGatewayPackageClient.IsolatedContract,
            IsLocal = true
        };
        var clock = Stopwatch.StartNew();
        var calls = new List<string>();
        int starts = 0;
        long? acknowledgementMilliseconds = null;
        bool acceptedPending = false;
        bool success = false;
        bool stopped = false;
        IsolatedGatewayStatus? running = null;
        GatewayEndpointProvenance? provenance = null;

        var client = new NativeGatewayPackageClient(async (selected, arguments, cancellationToken) =>
        {
            Assert.Equal(package, selected);
            Assert.Equal(3, arguments.Count);
            Assert.Equal("gateway-service", arguments[0]);
            Assert.Contains(arguments[1], new[] { "start", "status", "stop" });
            Assert.Equal("--json", arguments[2]);
            calls.Add(string.Join(" ", arguments));
            if (arguments[1] == "start")
                Assert.Equal(1, ++starts);

            var start = new ProcessStartInfo(selected.ClawCtlAliasPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("The approved test package could not be invoked.");
            var result = await NativeGatewayPackageClient.CompleteInvocationAsync(
                process, null, cancellationToken);
            if (arguments[1] == "start")
            {
                acknowledgementMilliseconds = clock.ElapsedMilliseconds;
                using var document = JsonDocument.Parse(result.StandardOutput);
                var root = document.RootElement;
                Assert.Equal(0, result.ExitCode);
                Assert.True(root.GetProperty("ok").GetBoolean());
                var gateway = root.GetProperty("gateway");
                Assert.Equal("starting", gateway.GetProperty("state").GetString());
                Assert.Single(gateway.EnumerateObject());
                acceptedPending = true;
                output.WriteLine($"Accepted pending response at {acknowledgementMilliseconds} ms.");
            }
            return result;
        });
        await using var runtime = new IsolatedGatewayRuntime(
            new ProofPackageResolver(package), client, WindowsTcpListenerSnapshot.Capture,
            WindowsProcessSequenceSnapshot.Capture, File.Exists, timeProvider: TimeProvider.System);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        try
        {
            await runtime.EnsureRunningAsync(record, deadline.Token);
            Assert.True(acceptedPending);
            Assert.Equal(1, starts);
            Assert.InRange(acknowledgementMilliseconds!.Value, 80_000, 120_000);
            Assert.InRange(clock.ElapsedMilliseconds, 110_000, 180_000);
            running = await client.StatusAsync(package, deadline.Token);
            Assert.Equal("running", running.State);
            Assert.Equal(port, running.Port);
            Assert.False(string.IsNullOrWhiteSpace(running.AgentUserSid));

            var first = WindowsProcessSequenceSnapshot.Capture();
            var tcp = WindowsTcpListenerSnapshot.Capture();
            var second = WindowsProcessSequenceSnapshot.Capture();
            Assert.True(tcp.Ipv4Complete && tcp.Ipv6Complete);
            var listeners = tcp.Listeners.Where(listener => listener.Port == port).ToArray();
            Assert.NotEmpty(listeners);
            foreach (var listener in listeners)
            {
                Assert.True(System.Net.IPAddress.IsLoopback(listener.Address));
                Assert.True(first.TryGetValue(listener.ProcessId, out var sequence));
                Assert.Equal(sequence, second[listener.ProcessId]);
                Assert.Contains(running.Listeners, owned =>
                    owned.Port == port && owned.ProcessId == listener.ProcessId &&
                    owned.SequenceNumber == sequence && owned.ProcessStartTimeUtc != default);
            }
            provenance = await runtime.InspectAsync(record, deadline.Token);
            Assert.Equal(GatewayEndpointProvenanceKind.ExpectedManagedGateway, provenance.Kind);
            Assert.Equal(1, starts);
            success = true;
        }
        finally
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await runtime.StopAsync(cleanup.Token);
                stopped = true;
            }
            finally
            {
                var proof = new
                {
                    success,
                    stopped,
                    packageFamily = Family,
                    packageVersion = version,
                    port,
                    startCalls = starts,
                    acceptedPending,
                    acknowledgementMilliseconds,
                    elapsedMilliseconds = clock.ElapsedMilliseconds,
                    credentialBearingCommands = 0,
                    calls,
                    provenance = provenance?.Kind.ToString(),
                    verifiedListeners = running?.Listeners
                };
                Directory.CreateDirectory(Path.GetDirectoryName(proofPath)!);
                File.WriteAllText(proofPath,
                    JsonSerializer.Serialize(proof, new JsonSerializerOptions { WriteIndented = true }));
                output.WriteLine($"Live proof: {proofPath}");
            }
        }
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Set {name} for the approved live proof.");

    private sealed class ProofPackageResolver(NativeGatewayPackage package) : INativeGatewayPackageResolver
    {
        public Task<NativeGatewayPackage> ResolveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(package);
        }
    }
}
