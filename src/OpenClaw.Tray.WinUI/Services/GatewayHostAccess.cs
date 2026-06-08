using System;
using OpenClaw.Connection;

namespace OpenClawTray.Services;

internal enum GatewayTerminalTarget
{
    None,
    Wsl,
    Ssh
}

/// <summary>
/// Localization indirection for <see cref="GatewayHostAccessPlan"/> / <see cref="GatewayHostAccessClassifier"/>.
/// Defaults are identity (return the resource key unchanged) so the file is unit-testable
/// without a WinUI runtime. <c>App.xaml.cs</c> wires these up to <c>LocalizationHelper</c>
/// at startup so the running app sees real localized strings.
/// </summary>
internal static class GatewayHostAccessLocalization
{
    public static Func<string, string> GetString { get; set; } = key => key;
    public static Func<string, object?[], string> Format { get; set; } = (key, _) => key;
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
        var defaultReason = disabledReason ?? GatewayHostAccessLocalization.GetString("GatewayHostAccess_NoTerminalAccess");
        return new GatewayHostAccessPlan(
            gatewayId,
            GatewayTerminalTarget.None,
            null,
            null,
            null,
            false,
            GatewayHostAccessLocalization.GetString("GatewayHostAccess_OpenTerminalLabel"),
            defaultReason,
            defaultReason);
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
                GatewayHostAccessLocalization.GetString("GatewayHostAccess_OpenTerminalLabel"),
                GatewayHostAccessLocalization.Format("GatewayHostAccess_OpenTerminalInWslTooltip_Format", new object?[] { distroName }),
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
                GatewayHostAccessLocalization.GetString("GatewayHostAccess_OpenSshTerminalLabel"),
                GatewayHostAccessLocalization.Format("GatewayHostAccess_OpenSshTerminalTooltip_Format", new object?[] { sshUser, sshHost }),
                null);
        }

        return GatewayHostAccessPlan.None(
            record.Id,
            GatewayHostAccessLocalization.GetString("GatewayHostAccess_NoWslOrSshDisabled"));
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
