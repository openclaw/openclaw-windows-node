using System.ComponentModel;
using OpenClaw.Connection;
using OpenClaw.Connection.NativeGateway;

namespace OpenClaw.SetupEngine;

/// <summary>Safe-to-display failure facts for native setup. Inputs are typed diagnostics; every line is sanitized.</summary>
public static class NativeGatewaySetupDiagnostics
{
    public static IReadOnlyList<string> Describe(Exception error)
    {
        var lines = new List<string>();
        if (error is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.Flatten().InnerExceptions)
            {
                lines.Add(SetupLogger.Sanitize(inner.Message));
                DescribeFacts(inner, lines);
            }
            return lines;
        }
        DescribeFacts(error, lines);
        return lines;
    }

    private static void DescribeFacts(Exception error, List<string> lines)
    {
        switch (error)
        {
            case NativeGatewayListenerException listener:
                GatewayEndpointProvenance p = listener.Provenance;
                string kind = p.Kind switch
                {
                    GatewayEndpointProvenanceKind.NoListener => "nothing is listening",
                    GatewayEndpointProvenanceKind.ExpectedManagedGateway => "the verified Gateway is listening",
                    GatewayEndpointProvenanceKind.ConflictingOpenClawGateway => "another OpenClaw Gateway is listening",
                    GatewayEndpointProvenanceKind.UnknownListener => "a listener that Companion could not verify",
                    GatewayEndpointProvenanceKind.NotApplicable => "the ownership check does not apply",
                    _ => p.Kind.ToString(),
                };
                lines.Add($"Port {p.Port}: {kind}.");
                if (!string.IsNullOrWhiteSpace(p.Detail))
                    lines.Add(SetupLogger.Sanitize(p.Detail));
                break;
            case NativeGatewayStartupTimeoutException timeout:
                // The exception message already names the phase and budget.
                if (timeout.LastStatus is not null)
                    lines.Add($"Last reported Gateway status: {timeout.LastStatus.Describe()}.");
                break;
            case Win32Exception win32:
                lines.Add($"Windows error code: {win32.NativeErrorCode}.");
                break;
        }
        lines.Add($"Error type: {error.GetType().Name}.");
    }
}
