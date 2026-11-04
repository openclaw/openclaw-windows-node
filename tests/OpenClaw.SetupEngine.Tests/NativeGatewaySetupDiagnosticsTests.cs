using System.IO;
using System.Net;
using OpenClaw.Connection;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.SetupEngine;

namespace OpenClaw.SetupEngine.Tests;

public sealed class NativeGatewaySetupDiagnosticsTests
{
    [Fact]
    public void ListenerExceptionNamesThePortTheVerdictAndTheDetail()
    {
        var error = new NativeGatewayListenerException(new GatewayEndpointProvenance(
            GatewayEndpointProvenanceKind.UnknownListener, 19001, Detail: "x"));

        Assert.Equal(
        [
            "Port 19001: a listener that Companion could not verify.",
            "x",
            "Error type: NativeGatewayListenerException.",
        ], NativeGatewaySetupDiagnostics.Describe(error));
    }

    [Fact]
    public void StartupTimeoutReportsTheLastTypedStatus()
    {
        var error = new NativeGatewayStartupTimeoutException(
            NativeGatewayStartupTimeoutPhase.Readiness,
            TimeSpan.FromSeconds(180),
            new IsolatedGatewayStatus("starting", null, null, [], "running", "not-ready", "config-invalid"));

        Assert.Contains(
            "Last reported Gateway status: gateway starting, session running, readiness not-ready (config-invalid).",
            NativeGatewaySetupDiagnostics.Describe(error));
    }

    [Fact]
    public void StartupTimeoutNeverSurfacesTheInnerExceptionText()
    {
        var error = new NativeGatewayStartupTimeoutException(
            NativeGatewayStartupTimeoutPhase.Readiness, TimeSpan.FromSeconds(180), null, new TimeoutException("sensitive"));

        var lines = NativeGatewaySetupDiagnostics.Describe(error);

        Assert.DoesNotContain(lines, line => line.Contains("sensitive"));
    }

    [Fact]
    public void AggregateExceptionReportsEveryInnerMessageAndType()
    {
        var error = new AggregateException(
            new IOException("first"),
            new IOException("second"));

        var lines = NativeGatewaySetupDiagnostics.Describe(error);

        Assert.Contains("first", lines);
        Assert.Contains("second", lines);
        Assert.Equal(2, lines.Count(line => line == "Error type: IOException."));
    }
}
