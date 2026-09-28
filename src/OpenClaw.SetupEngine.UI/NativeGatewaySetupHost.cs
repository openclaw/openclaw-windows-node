using OpenClaw.Connection.NativeGateway;
using System.Diagnostics;

namespace OpenClaw.SetupEngine.UI;

internal sealed class NativeGatewaySetupHost(
    Action<string>? progress = null,
    Action<NativeGatewaySetupStage>? stageProgress = null) : INativeGatewaySetupHost
{
    // Includes packaged Node/CLI startup, not just the Gateway RPC timeout.
    private static readonly TimeSpan PairingCommandTimeout = TimeSpan.FromMinutes(2);

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

    public IDisposable OpenRecoveryTerminal(
        NativeGatewayPackage package, IReadOnlyDictionary<string, string> environment)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            WorkingDirectory = environment["OPENCLAW_STATE_DIR"],
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NoExit");
        foreach (var (key, value) in environment)
            start.Environment[key] = value;
        start.Environment["PATH"] = Path.GetDirectoryName(package.OpenClawAliasPath) +
            Path.PathSeparator + start.Environment["PATH"];
        return new RecoveryTerminal(Process.Start(start)
            ?? throw new InvalidOperationException("The native profile terminal could not be opened."));
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

    public Task PreparePackageAsync(
        NativeGatewayPackage package,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken) =>
        RunAsync(package.ClawCtlAliasPath, ["setup"], environment,
            TimeSpan.FromMinutes(3), "Preparing the packaged Gateway runtime...", cancellationToken);

    public Task ValidateConfigurationAsync(
        NativeGatewayPackage package,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken) =>
        RunAsync(package.OpenClawAliasPath, ["config", "validate", "--json"], environment,
            TimeSpan.FromMinutes(2), "Validating native Gateway configuration...", cancellationToken);

    public Task VerifyHealthAsync(
        NativeGatewayPackage package,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken) =>
        RunAsync(package.OpenClawAliasPath, ["gateway", "health", "--json"], environment,
            TimeSpan.FromMinutes(2), "Checking native Gateway health...", cancellationToken);

    public Task<string> ListDevicePairingRequestsAsync(
        NativeGatewayPackage package,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken) =>
        RunAsync(package.OpenClawAliasPath, ["devices", "list", "--json"],
            environment, PairingCommandTimeout, "Verifying this Companion's pairing request...", cancellationToken);

    public Task<string> ApproveDevicePairingAsync(
        NativeGatewayPackage package, string requestId,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken) =>
        RunAsync(package.OpenClawAliasPath, ["devices", "approve", requestId, "--json"],
            environment, PairingCommandTimeout, "Pairing this Companion with its setup Gateway...", cancellationToken);

    private async Task<string> RunAsync(
        string executable, string[] arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout, string message, CancellationToken cancellationToken)
    {
        progress?.Invoke(message);
        using var logger = new SetupLogger(filePath: null);
        var result = await new CommandRunner(logger).RunAsync(
            executable, arguments, timeout, environment,
            workingDirectory: environment["OPENCLAW_STATE_DIR"],
            ct: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.TimedOut || result.ExitCode != 0)
        {
            var detail = SetupLogger.Sanitize(result.Stderr + Environment.NewLine + result.Stdout).Trim();
            throw new InvalidOperationException(
                $"{message} {(result.TimedOut ? "Timed out." : $"Exit code {result.ExitCode}.")} {detail}");
        }
        if (arguments is ["setup"] && !string.IsNullOrWhiteSpace(result.Stdout))
            progress?.Invoke(SetupLogger.Sanitize(result.Stdout).Trim());
        return result.Stdout;
    }
}
