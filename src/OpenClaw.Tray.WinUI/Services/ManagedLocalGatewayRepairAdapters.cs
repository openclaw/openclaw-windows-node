using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OpenClaw.Connection;

namespace OpenClawTray.Services;

/// <summary>
/// <see cref="IManagedLocalGatewayRestarter"/> backed by <see cref="WslGatewayController"/>.
/// Restart output is not surfaced verbatim to avoid leaking command echo into logs; only
/// success/failure and a bounded, sanitized summary are propagated.
/// </summary>
public sealed class WslManagedLocalGatewayRestarter(WslGatewayController controller) : IManagedLocalGatewayRestarter
{
    private readonly WslGatewayController _controller = controller ?? throw new ArgumentNullException(nameof(controller));

    public async Task<ManagedLocalGatewayRestartResult> RestartAsync(
        string distroName,
        CancellationToken cancellationToken,
        Func<bool>? canContinue = null)
    {
        canContinue ??= static () => true;
        if (!canContinue())
            return new ManagedLocalGatewayRestartResult(false, "Gateway changed before restart.");

        var result = await _controller
            .RunAsync(distroName, WslGatewayControlAction.Restart, cancellationToken)
            .ConfigureAwait(false);

        if (result.Success)
            return new ManagedLocalGatewayRestartResult(true);

        if (!canContinue())
            return new ManagedLocalGatewayRestartResult(false, "Gateway changed before forced restart.");

        // The in-place restart failed — the distro/VM may be wedged and unable to run an in-distro
        // command (an in-distro command also times out to a failure rather than hanging forever).
        // Escalate to a host-side terminate + cold restart, which recovers a hung WSL instance
        // (ForceRestartAsync logs the terminate).
        result = await _controller
            .ForceRestartAsync(distroName, cancellationToken)
            .ConfigureAwait(false);

        if (result.Success)
            return new ManagedLocalGatewayRestartResult(true);

        var detail = result.ExitCode == 0
            ? "Gateway restart reported failure."
            : $"wsl.exe exited with code {result.ExitCode}.";
        return new ManagedLocalGatewayRestartResult(false, detail);
    }
}

/// <summary>
/// Lightweight TCP reachability probe for a gateway URL — used by the repair coordinator to decide
/// whether the gateway process is up (reconnect only) or down (restart the distro). Never sends
/// credentials; it only opens and immediately closes a TCP connection to host:port.
/// </summary>
public static class GatewayReachabilityProbe
{
    public static async Task<bool> IsReachableAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host;
        var port = uri.Port > 0
            ? uri.Port
            : uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ? 443 : 80;

        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false; // refused / unreachable / timed out
        }
    }
}
