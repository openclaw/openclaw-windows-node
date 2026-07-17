using System;

namespace OpenClaw.Connection;

public enum GatewayTerminalTarget
{
    None,
    Wsl,
    Ssh,
    Native
}

/// <summary>
/// Localization indirection for <see cref="GatewayHostAccessPlan"/> / <see cref="GatewayHostAccessClassifier"/>.
/// Defaults are identity (return the resource key unchanged) so the file is unit-testable
/// without a WinUI runtime. <c>App.xaml.cs</c> wires these up to <c>LocalizationHelper</c>
/// at startup so the running app sees real localized strings.
/// </summary>
public static class GatewayHostAccessLocalization
{
    public static Func<string, string> GetString { get; set; } = key => key;
    public static Func<string, object?[], string> Format { get; set; } = (key, _) => key;
}

public sealed record GatewayHostAccessPlan(
    string? GatewayId,
    GatewayTerminalTarget TerminalTarget,
    string? DistroName,
    string? SshUser,
    string? SshHost,
    bool CanControlWslGateway,
    string? NativeTaskName,
    string TerminalLabel,
    string TerminalTooltip,
    string? DisabledReason)
{
    public bool CanOpenTerminal => TerminalTarget != GatewayTerminalTarget.None;

    public bool IsWslManaged => !string.IsNullOrWhiteSpace(DistroName);

    public bool CanControlNativeGateway => !string.IsNullOrWhiteSpace(NativeTaskName);

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
            null,
            GatewayHostAccessLocalization.GetString("GatewayHostAccess_OpenTerminalLabel"),
            defaultReason,
            defaultReason);
    }
}

public static class GatewayHostAccessClassifier
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
                null,
                GatewayHostAccessLocalization.GetString("GatewayHostAccess_OpenTerminalLabel"),
                GatewayHostAccessLocalization.Format("GatewayHostAccess_OpenTerminalInWslTooltip_Format", new object?[] { distroName }),
                null);
        }

        var nativeTaskName = Normalize(record.SetupManagedNativeTaskName);
        if (record.IsLocal && nativeTaskName is not null)
        {
            return new GatewayHostAccessPlan(
                record.Id,
                GatewayTerminalTarget.Native,
                null,
                sshUser,
                sshHost,
                false,
                nativeTaskName,
                GatewayHostAccessLocalization.GetString("GatewayHostAccess_OpenTerminalLabel"),
                GatewayHostAccessLocalization.Format("GatewayHostAccess_OpenTerminalNativeTooltip_Format", new object?[] { nativeTaskName }),
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
                null,
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