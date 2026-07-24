using System.Collections.ObjectModel;
using OpenClaw.Shared;

namespace OpenClaw.Connection;

public enum WslGatewayControlAction
{
    Start,
    Stop,
    Restart
}

public sealed record WslGatewayControlResult(
    string DistroName,
    WslGatewayControlAction Action,
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool Success => ExitCode == 0;

    public string OutputSummary
    {
        get
        {
            var summary = string.IsNullOrWhiteSpace(StandardOutput) ? StandardError : StandardOutput;
            return string.IsNullOrWhiteSpace(summary) ? string.Empty : summary.Trim();
        }
    }
}

public static class WslGatewayControlCommandBuilder
{
    internal const string OpenClawWslPathPrefix = "export PATH=\"/home/openclaw/.openclaw/bin:/opt/openclaw/bin:/usr/local/bin:$PATH\"";

    public static IReadOnlyList<string> Build(WslGatewayControlAction action)
    {
        return new ReadOnlyCollection<string>([
            "bash",
            "-lc",
            $"{OpenClawWslPathPrefix} && openclaw gateway {ToVerb(action)}"
        ]);
    }

    public static string ToVerb(WslGatewayControlAction action)
    {
        return action switch
        {
            WslGatewayControlAction.Start => "start",
            WslGatewayControlAction.Stop => "stop",
            WslGatewayControlAction.Restart => "restart",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported WSL gateway action.")
        };
    }
}

public sealed class WslGatewayController(IWslCommandRunner commandRunner, IOpenClawLogger logger)
{
    public async Task<WslGatewayControlResult> RunAsync(
        string distroName,
        WslGatewayControlAction action,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(distroName))
        {
            throw new ArgumentException("WSL distro name is required.", nameof(distroName));
        }

        var normalizedDistroName = distroName.Trim();
        var distros = await commandRunner.ListDistrosAsync(cancellationToken).ConfigureAwait(false);

        // Only short-circuit as "not registered" when the probe returned a non-empty
        // enumeration that definitively lacks the distro. An empty list is ambiguous:
        // `wsl --list` may have failed or timed out (ListDistrosAsync collapses any
        // failure to an empty list), so fail open and let the actual control command
        // surface the real error instead of dead-ending recovery with a misleading message.
        if (distros.Count > 0 &&
            !distros.Any(distro => string.Equals(distro.Name, normalizedDistroName, StringComparison.OrdinalIgnoreCase)))
        {
            return new WslGatewayControlResult(
                normalizedDistroName,
                action,
                -1,
                string.Empty,
                $"WSL distro '{normalizedDistroName}' is not registered.");
        }

        logger.Info($"Running OpenClaw gateway {WslGatewayControlCommandBuilder.ToVerb(action)} in WSL distro '{normalizedDistroName}'.");
        var result = await commandRunner.RunInDistroAsync(
            normalizedDistroName,
            WslGatewayControlCommandBuilder.Build(action),
            cancellationToken).ConfigureAwait(false);

        return new WslGatewayControlResult(
            normalizedDistroName,
            action,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError);
    }

    /// <summary>
    /// Forcefully restarts a distro that could not be controlled in-place: it issues a host-side
    /// <c>wsl.exe --terminate</c> (which succeeds even when the distro/VM is wedged and cannot run an
    /// in-distro command), then cold-restarts the gateway. The follow-up in-distro command re-boots a
    /// fresh VM (systemd), so this recovers a hung WSL instance that a plain <c>gateway restart</c>
    /// cannot reach. Used only as an escalation after an in-place restart fails.
    /// </summary>
    public async Task<WslGatewayControlResult> ForceRestartAsync(
        string distroName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(distroName))
        {
            throw new ArgumentException("WSL distro name is required.", nameof(distroName));
        }

        var normalizedDistroName = distroName.Trim();
        logger.Info($"Force-terminating WSL distro '{normalizedDistroName}' before restart (in-place restart failed).");

        // Terminate is best-effort: a "not running" distro still returns success, and any failure here
        // should not stop the cold restart below from surfacing the real error.
        // slopwatch-ignore: SW003 Terminate is best-effort; its failure cannot improve the caller state and the restart below is authoritative.
        try { await commandRunner.TerminateDistroAsync(normalizedDistroName, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.Warn($"Force-terminate of '{normalizedDistroName}' failed (continuing to restart): {ex.Message}"); }

        return await RunAsync(normalizedDistroName, WslGatewayControlAction.Restart, cancellationToken).ConfigureAwait(false);
    }
}
