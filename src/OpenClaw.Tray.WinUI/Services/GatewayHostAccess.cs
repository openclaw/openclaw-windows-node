using OpenClaw.Connection;

namespace OpenClawTray.Services;

internal enum GatewayTerminalTarget
{
    None,
    Wsl,
    Ssh
}

internal sealed record GatewayHostAccessPlan(
    string? GatewayId,
    GatewayTerminalTarget TerminalTarget,
    string? DistroName,
    string? SshUser,
    string? SshHost,
    bool CanControlWslGateway,
    string TerminalLabel,
    string TerminalTooltip,
    string? DisabledReason)
{
    public bool CanOpenTerminal => TerminalTarget != GatewayTerminalTarget.None;

    public bool IsWslManaged => !string.IsNullOrWhiteSpace(DistroName);

    public static GatewayHostAccessPlan None(string? gatewayId = null, string? disabledReason = null)
    {
        return new GatewayHostAccessPlan(
            gatewayId,
            GatewayTerminalTarget.None,
            null,
            null,
            null,
            false,
            "Open terminal",
            disabledReason ?? "This gateway does not have WSL or SSH terminal access.",
            disabledReason ?? "This gateway does not have WSL or SSH terminal access.");
    }
}

internal static class GatewayHostAccessClassifier
{
    public static GatewayHostAccessPlan Classify(GatewayRecord? record)
    {
        if (record is null)
        {
            return GatewayHostAccessPlan.None();
        }

        var distroName = Normalize(record.SetupManagedDistroName);
        var sshUser = Normalize(record.SshTunnel?.User);
        var sshHost = Normalize(record.SshTunnel?.Host);

        if (distroName is not null)
        {
            return new GatewayHostAccessPlan(
                record.Id,
                GatewayTerminalTarget.Wsl,
                distroName,
                sshUser,
                sshHost,
                true,
                "Open terminal",
                $"Open a terminal in the {distroName} WSL gateway.",
                null);
        }

        if (sshUser is not null && sshHost is not null)
        {
            return new GatewayHostAccessPlan(
                record.Id,
                GatewayTerminalTarget.Ssh,
                null,
                sshUser,
                sshHost,
                false,
                "Open SSH terminal",
                $"Open an SSH terminal to {sshUser}@{sshHost}.",
                null);
        }

        return GatewayHostAccessPlan.None(
            record.Id,
            "This gateway was not created with WSL and does not have an SSH tunnel.");
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
