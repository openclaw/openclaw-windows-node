using OpenClaw.Connection;
using System.Text;

namespace OpenClawTray.Services;

internal sealed record LocalAiGatewayCommandResult(
    int ExitCode, string StandardOutput, string StandardError,
    bool TimedOut = false, bool OutcomeIndeterminate = false)
{
    public bool Success => ExitCode == 0 && !TimedOut && !OutcomeIndeterminate;
}

internal sealed record LocalAiGatewayCommandOutcome(
    LocalAiGatewayCommandResult? Result, string? Detail)
{
    public bool Routed => Result is not null;
}

/// <summary>
/// Configuration execution only. The provider coordinator retains publication,
/// fallback and endpoint-lifetime policy; each transport revalidates its pinned owner.
/// </summary>
internal interface ILocalAiGatewayConfigurationTransport
{
    Task<LocalAiGatewayCommandOutcome> RunAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken);
    Task<LocalAiGatewayCommandOutcome> ApplyBatchAsync(
        string batch, CancellationToken cancellationToken);
}

internal sealed class WslLocalAiGatewayConfigurationTransport(
    IWslCommandRunner commands,
    ILocalAiGatewayDistroResolver distroResolver) : ILocalAiGatewayConfigurationTransport
{
    private const string FixedPath = "/home/openclaw/.openclaw/bin:/opt/openclaw/bin:/usr/local/bin:/usr/bin:/bin";

    public Task<LocalAiGatewayCommandOutcome> RunAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var command = new List<string>(arguments.Count + 3)
        {
            "/usr/bin/env",
            $"PATH={FixedPath}",
            "openclaw",
        };
        command.AddRange(arguments);
        return RunInManagedDistroAsync(command, cancellationToken);
    }

    public Task<LocalAiGatewayCommandOutcome> ApplyBatchAsync(
        string batch, CancellationToken cancellationToken)
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(batch));
        // stdin prevents wsl.exe from expanding the Linux temporary-file variable.
        string script =
            "set -e\n" +
            "batch_file=\"$(mktemp)\"\n" +
            "trap 'rm -f \"$batch_file\"' EXIT\n" +
            $"printf '%s' '{encoded}' | base64 -d > \"$batch_file\"\n" +
            "openclaw config set --batch-file \"$batch_file\" --dry-run\n" +
            "openclaw config set --batch-file \"$batch_file\"\n";
        return RunInManagedDistroAsync(
            ["/usr/bin/env", $"PATH={FixedPath}", "/bin/sh", "-s"],
            cancellationToken, script);
    }

    private async Task<LocalAiGatewayCommandOutcome> RunInManagedDistroAsync(
        IReadOnlyList<string> command, CancellationToken cancellationToken,
        string? standardInput = null)
    {
        LocalAiGatewayDistroResolution resolution = distroResolver.Resolve();
        if (!resolution.Success)
            return new(null, resolution.Detail);

        WslCommandResult result = await commands.RunInDistroAsync(
            resolution.DistroName!, command, cancellationToken, standardInput: standardInput)
            .ConfigureAwait(false);
        return new(new(result.ExitCode, result.StandardOutput, result.StandardError,
            result.TimedOut, result.OutcomeIndeterminate), null);
    }
}
