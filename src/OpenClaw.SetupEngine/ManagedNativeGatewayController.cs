using System.Text.Json;

namespace OpenClaw.SetupEngine;

public enum NativeGatewayControlAction
{
    Status,
    Start,
    Stop,
    Restart,
}

public sealed record NativeGatewayControlResult(
    string TaskName,
    NativeGatewayControlAction Action,
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

    public bool IsRunning => Success && NativeGatewayStatusParser.IsRunning(StandardOutput);
}

public sealed class ManagedNativeGatewayController(
    string dataDir,
    string localDataDir,
    SetupLogger? logger = null,
    ICommandRunner? commandRunner = null)
{
    public async Task<NativeGatewayControlResult> RunAsync(
        string taskName,
        NativeGatewayControlAction action,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskName))
            throw new ArgumentException("Native gateway task name is required.", nameof(taskName));

        var normalizedTaskName = taskName.Trim();
        if (!TryGetProfileFromTaskName(normalizedTaskName, out var profile))
        {
            return new NativeGatewayControlResult(
                normalizedTaskName,
                action,
                -1,
                "",
                $"Native gateway task '{normalizedTaskName}' is not an OpenClaw Companion-managed task name.");
        }

        var config = new SetupConfig
        {
            InstallMode = GatewayInstallMode.NativeWindows,
            DistroName = profile,
        };

        if (!GatewayInstallModeDetector.IsNativeProfileOwned(localDataDir, config))
        {
            return new NativeGatewayControlResult(
                normalizedTaskName,
                action,
                -1,
                "",
                $"Native gateway task '{normalizedTaskName}' is not owned by this OpenClaw Companion install.");
        }

        var ownsLogger = logger is null;
        var effectiveLogger = logger ?? new SetupLogger(filePath: null, LogLevel.Warn);
        try
        {
            using var journal = new TransactionJournal(filePath: null);
            var ctx = new SetupContext(
                config,
                effectiveLogger,
                journal,
                commandRunner ?? new CommandRunner(effectiveLogger),
                ct,
                dataDir,
                localDataDir);

            var result = await GatewayCliRunner.RunNativeAsync(
                ctx,
                BuildGatewayArguments(action),
                TimeSpan.FromSeconds(action == NativeGatewayControlAction.Status ? 30 : 60),
                ct: ct).ConfigureAwait(false);

            return new NativeGatewayControlResult(
                normalizedTaskName,
                action,
                result.ExitCode,
                result.Stdout,
                result.Stderr);
        }
        finally
        {
            if (ownsLogger)
                effectiveLogger.Dispose();
        }
    }

    public static string ToVerb(NativeGatewayControlAction action) => action switch
    {
        NativeGatewayControlAction.Status => "status",
        NativeGatewayControlAction.Start => "start",
        NativeGatewayControlAction.Stop => "stop",
        NativeGatewayControlAction.Restart => "restart",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported native gateway action."),
    };

    public static IReadOnlyList<string> BuildGatewayArguments(NativeGatewayControlAction action) =>
        action == NativeGatewayControlAction.Status
            ? ["gateway", ToVerb(action), "--json"]
            : ["gateway", ToVerb(action)];

    public static bool TryGetProfileFromTaskName(string taskName, out string profile)
    {
        profile = "";
        const string prefix = "OpenClaw Gateway (";
        if (!taskName.StartsWith(prefix, StringComparison.Ordinal) || !taskName.EndsWith(')'))
            return false;

        profile = taskName[prefix.Length..^1];
        return !string.IsNullOrWhiteSpace(profile);
    }
}

internal static class NativeGatewayStatusParser
{
    public static bool IsRunning(string statusJson)
    {
        try
        {
            using var document = JsonDocument.Parse(statusJson);
            return document.RootElement.TryGetProperty("service", out var service)
                && service.ValueKind == JsonValueKind.Object
                && service.TryGetProperty("runtime", out var runtime)
                && runtime.ValueKind == JsonValueKind.Object
                && runtime.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(status.GetString(), "running", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
