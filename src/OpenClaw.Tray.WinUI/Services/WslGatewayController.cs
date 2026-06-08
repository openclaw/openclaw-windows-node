using System.Collections.ObjectModel;
using OpenClaw.Shared;

namespace OpenClawTray.Services;

public enum WslGatewayControlAction
{
    Start,
    Stop,
    Restart
}

internal sealed record WslGatewayControlResult(
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

internal static class WslGatewayControlCommandBuilder
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

internal sealed class WslGatewayController(IWslCommandRunner commandRunner, IOpenClawLogger logger)
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
        if (!distros.Any(distro => string.Equals(distro.Name, normalizedDistroName, StringComparison.OrdinalIgnoreCase)))
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
}
