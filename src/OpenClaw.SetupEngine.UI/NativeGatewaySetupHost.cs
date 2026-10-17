using OpenClaw.Connection.NativeGateway;
using System.Diagnostics;

namespace OpenClaw.SetupEngine.UI;

/// <summary>
/// Runs native setup commands only through the installed package's qualified aliases.
/// The isolated package owns its Gateway configuration and state, so no host-side
/// profile environment is passed to these commands.
/// </summary>
internal sealed class NativeGatewaySetupHost(
    Action<string>? progress = null,
    Action<NativeGatewaySetupStage>? stageProgress = null) : INativeGatewaySetupHost
{
    // Includes packaged Node/CLI startup, not just the Gateway RPC timeout.
    private static readonly TimeSpan PairingCommandTimeout = TimeSpan.FromMinutes(2);
    private readonly NativeGatewayPackageClient _packageClient = new();

    public Task<NativeGatewayContract> DetectContractAsync(
        NativeGatewayPackage package, CancellationToken cancellationToken) =>
        _packageClient.DetectAsync(package, cancellationToken);

    public Task<IsolatedGatewayConfiguration> PrepareIsolatedConfigurationAsync(
        NativeGatewayPackage package, int port, CancellationToken cancellationToken) =>
        _packageClient.PrepareAsync(package, port, cancellationToken);

    public Task ApplyIsolatedCapabilitiesAsync(
        NativeGatewayPackage package, IReadOnlyList<string> commandIds, CancellationToken cancellationToken) =>
        RunAsync(package.OpenClawAliasPath,
            ["config", "set", ConfigureGatewayStep.NodeCommandsAllowKey,
                System.Text.Json.JsonSerializer.Serialize(commandIds), "--strict-json"],
            TimeSpan.FromMinutes(2),
            "Applying Windows node capabilities inside the isolated Gateway...", cancellationToken);

    public void ReportProgress(NativeGatewaySetupStage stage)
    {
        stageProgress?.Invoke(stage);
        progress?.Invoke(stage switch
        {
            NativeGatewaySetupStage.StartingGateway => "Starting the packaged Gateway and verifying its listener...",
            NativeGatewaySetupStage.VerifyingEndpoint => "Verifying the Gateway endpoint before connecting...",
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        });
    }

    public IDisposable OpenRecoveryTerminal(NativeGatewayPackage package)
    {
        var terminal = new ProcessStartInfo(package.ClawCtlAliasPath)
        {
            UseShellExecute = true
        };
        terminal.ArgumentList.Add("pwsh");
        return new RecoveryTerminal(Process.Start(terminal)
            ?? throw new InvalidOperationException("The isolated Gateway terminal could not be opened."));
    }

    private sealed class RecoveryTerminal(Process process) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            finally { process.Dispose(); }
        }
    }

    public Task PreparePackageAsync(NativeGatewayPackage package, CancellationToken cancellationToken)
    {
        progress?.Invoke("Preparing the isolated Gateway session...");
        return _packageClient.SetupAsync(package, cancellationToken);
    }

    public Task ValidateConfigurationAsync(NativeGatewayPackage package, CancellationToken cancellationToken) =>
        RunAsync(package.OpenClawAliasPath, ["config", "validate", "--json"],
            TimeSpan.FromMinutes(2), "Validating native Gateway configuration...", cancellationToken);

    public Task VerifyHealthAsync(NativeGatewayPackage package, CancellationToken cancellationToken) =>
        RunAsync(package.OpenClawAliasPath, ["gateway", "health", "--json"],
            TimeSpan.FromMinutes(2), "Checking native Gateway health...", cancellationToken);

    public Task<IsolatedGatewayConfiguration> CheckIsolatedPairingConfigurationAsync(
        NativeGatewayPackage package, CancellationToken cancellationToken) =>
        _packageClient.CheckAsync(package, cancellationToken);

    public Task<string> ListDevicePairingRequestsAsync(
        NativeGatewayPackage package, CancellationToken cancellationToken) =>
        RunAsync(package.OpenClawAliasPath, ["devices", "list", "--json"], PairingCommandTimeout,
            "Verifying this Companion's pairing request...", cancellationToken);

    public Task<string> ApproveDevicePairingAsync(
        NativeGatewayPackage package, string requestId, CancellationToken cancellationToken) =>
        RunAsync(package.OpenClawAliasPath, ["devices", "approve", requestId, "--json"], PairingCommandTimeout,
            "Pairing this Companion with its setup Gateway...", cancellationToken);

    private async Task<string> RunAsync(
        string executable, string[] arguments,
        TimeSpan timeout, string message, CancellationToken cancellationToken)
    {
        progress?.Invoke(message);
        using var logger = new SetupLogger(filePath: null);
        var result = await new CommandRunner(logger).RunAsync(
            executable, arguments, timeout, ct: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.TimedOut || result.ExitCode != 0)
        {
            var detail = SetupLogger.Sanitize(result.Stderr + Environment.NewLine + result.Stdout).Trim();
            throw new InvalidOperationException(
                $"{message} {(result.TimedOut ? "Timed out." : $"Exit code {result.ExitCode}.")} {detail}");
        }
        return result.Stdout;
    }
}
