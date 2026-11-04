using OpenClaw.Connection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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

/// <summary>Minimum Gateway whose live request path honors local reasoning_effort=none.</summary>
internal static class LocalAiReasoningGatewayCompatibility
{
    private static readonly Regex VersionPattern = new(
        @"(?<![0-9A-Za-z.+-])(?<year>\d{4})\.(?<month>\d{1,2})\.(?<patch>\d+)(?<prerelease>-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?(?![0-9A-Za-z.+-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public const string UpgradeMessage = "Local AI reasoning controls require OpenClaw Gateway 2026.9.8 or newer. Upgrade the Gateway before publishing this model.";
    public const string RunningVersionUnavailableMessage = "The running WSL Gateway version could not be verified. Reconnect the Gateway before publishing Local AI.";

    public static bool TryReadRunningVersion(string? statusJson, out string? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(statusJson) || Encoding.UTF8.GetByteCount(statusJson) > 1024 * 1024)
            return false;
        try
        {
            using JsonDocument status = JsonDocument.Parse(statusJson, new JsonDocumentOptions { MaxDepth = 32 });
            if (status.RootElement.ValueKind != JsonValueKind.Object ||
                !status.RootElement.TryGetProperty("rpc", out var rpc) || rpc.ValueKind != JsonValueKind.Object ||
                !rpc.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
                !rpc.TryGetProperty("server", out var server) || server.ValueKind != JsonValueKind.Object ||
                !server.TryGetProperty("version", out var reported) ||
                reported.ValueKind != JsonValueKind.String)
                return false;
            version = reported.GetString();
            return !string.IsNullOrWhiteSpace(version);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsSupportedCliOutput(string? output) =>
        !string.IsNullOrWhiteSpace(output) && IsSupported(VersionPattern.Match(output).Value);

    public static bool IsSupported(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        Match match = VersionPattern.Match(version.Trim());
        if (!match.Success || match.Length != version.Trim().Length ||
            !int.TryParse(match.Groups["year"].Value, out int year) ||
            !int.TryParse(match.Groups["month"].Value, out int month) ||
            !int.TryParse(match.Groups["patch"].Value, out int patch) ||
            month is < 1 or > 12) return false;
        int releaseLine = (year, month, patch).CompareTo((2026, 9, 8));
        // A named prerelease of the floor was not proven. Numeric suffixes
        // are correction releases under GatewayReleaseVersion's contract.
        bool unverifiedFloorPrerelease = releaseLine == 0 &&
            match.Groups["prerelease"].Success &&
            !Regex.IsMatch(match.Groups["prerelease"].Value, @"^-\d+$");
        return releaseLine >= 0 && !unverifiedFloorPrerelease;
    }
}
