using OpenClaw.Connection;

namespace OpenClaw.SetupEngine;

/// <summary>How the Gateway verified for the Ready screen is hosted.</summary>
public enum SetupReadyGatewayKind { Unknown, Native, Wsl, LocalExisting, Remote }

/// <summary>
/// Describes the Gateway that setup verified, from its saved registry record, so the Ready
/// screen's summary row and step timeline match how the user got there. The Ready window runs
/// after a restart, so the original setup route is no longer in memory; the record is.
/// </summary>
/// <example>
/// <code>
/// var gateway = SetupReadyGateway.From(registry.GetById(proof.GatewayId));
/// timeline.Update(OnboardingFlowPolicy.GetStages(gateway.Route, config), OnboardingStage.Ready);
/// </code>
/// </example>
public sealed record SetupReadyGateway(SetupReadyGatewayKind Kind, string? Host = null)
{
    public static SetupReadyGateway Unknown { get; } = new(SetupReadyGatewayKind.Unknown);

    /// <summary>The setup route whose steps the timeline shows. Unknown keeps the native steps.</summary>
    public SetupGatewayRoute Route => Kind switch
    {
        SetupReadyGatewayKind.Wsl => SetupGatewayRoute.ManagedWsl,
        SetupReadyGatewayKind.LocalExisting => SetupGatewayRoute.Existing,
        SetupReadyGatewayKind.Remote => SetupGatewayRoute.Remote,
        _ => SetupGatewayRoute.Native,
    };

    public static SetupReadyGateway From(GatewayRecord? record)
    {
        if (record is null)
            return Unknown;
        if (!string.IsNullOrWhiteSpace(record.NativePackageFamilyName) ||
            !string.IsNullOrWhiteSpace(record.NativeRuntimeContract))
            return new(SetupReadyGatewayKind.Native);
        if (GatewayRecordEditing.ResolveManagedDistroName(record) is not null)
            return new(SetupReadyGatewayKind.Wsl);
        if (record.SshTunnel is { } tunnel)
            return new(SetupReadyGatewayKind.Remote, DisplayHost(record, tunnel.Host));
        if (GatewayRecordEditing.IsLoopbackEndpoint(record.Url))
            return new(SetupReadyGatewayKind.LocalExisting);
        return new(SetupReadyGatewayKind.Remote,
            DisplayHost(record, Uri.TryCreate(record.Url, UriKind.Absolute, out var uri) ? uri.Host : null));
    }

    private static string? DisplayHost(GatewayRecord record, string? host) =>
        !string.IsNullOrWhiteSpace(record.FriendlyName) ? record.FriendlyName.Trim()
            : string.IsNullOrWhiteSpace(host) ? null : host;
}
