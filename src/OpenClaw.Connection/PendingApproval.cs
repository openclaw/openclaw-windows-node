using System;
using System.Collections.Generic;
using System.Linq;
using OpenClaw.Shared;

namespace OpenClaw.Connection;

/// <summary>Whether a pending pairing approval is for a device (operator-class client) or a node.</summary>
public enum PairingApprovalKind
{
    Device,
    Node,
}

/// <summary>
/// Unified, UI-agnostic view of a single inbound pairing request that a local operator
/// (with <c>operator.pairing</c>/<c>operator.admin</c> scope) can approve or reject.
/// Built from either a <see cref="DevicePairingRequest"/> or a node <see cref="PairingRequest"/>
/// so the presentation layer can render and act on both kinds uniformly.
/// </summary>
public sealed class PendingApproval
{
    public PairingApprovalKind Kind { get; init; }

    /// <summary>Gateway request id used to approve/reject. May be empty on legacy gateways.</summary>
    public string RequestId { get; init; } = "";

    /// <summary>Device id (device requests) or node id (node requests).</summary>
    public string DeviceId { get; init; } = "";

    public string? DisplayName { get; init; }
    public string? Platform { get; init; }
    public string? Role { get; init; }

    /// <summary>Requested operator scopes (device requests only; empty for nodes).</summary>
    public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();

    public string? RemoteIp { get; init; }
    public string? Version { get; init; }
    public bool IsRepair { get; init; }
    public double Ts { get; init; }

    /// <summary>
    /// The id passed to the gateway approve/reject RPC. Prefers <see cref="RequestId"/> and
    /// falls back to <see cref="DeviceId"/> so legacy gateways that omit a request id are still
    /// actionable (mirrors the per-row fallback already used by the Connection page cards).
    /// </summary>
    public string DecisionId => string.IsNullOrEmpty(RequestId) ? DeviceId : RequestId;

    /// <summary>Stable identity for dedup/diffing across list refreshes.</summary>
    public string Key => $"{Kind}:{DecisionId}";

    /// <summary>True when the request carries no usable id and therefore cannot be acted on.</summary>
    public bool IsActionable => !string.IsNullOrEmpty(DecisionId);

    public static PendingApproval FromDevice(DevicePairingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new PendingApproval
        {
            Kind = PairingApprovalKind.Device,
            RequestId = request.RequestId,
            DeviceId = request.DeviceId,
            DisplayName = request.DisplayName,
            Platform = request.Platform,
            Role = string.IsNullOrWhiteSpace(request.Role) ? "operator" : request.Role,
            Scopes = request.Scopes is { Length: > 0 }
                ? request.Scopes.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray()
                : Array.Empty<string>(),
            RemoteIp = NormalizeIp(request.RemoteIp),
            IsRepair = request.IsRepair,
            Ts = request.Ts,
        };
    }

    public static PendingApproval FromNode(PairingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new PendingApproval
        {
            Kind = PairingApprovalKind.Node,
            RequestId = request.RequestId,
            DeviceId = request.NodeId,
            DisplayName = request.DisplayName,
            Platform = request.Platform,
            Role = "node",
            Scopes = Array.Empty<string>(),
            RemoteIp = NormalizeIp(request.RemoteIp),
            Version = request.Version,
            IsRepair = request.IsRepair,
            Ts = request.Ts,
        };
    }

    /// <summary>Strips the IPv4-mapped IPv6 prefix the gateway sometimes emits (mirrors the Mac client).</summary>
    private static string? NormalizeIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return ip;
        const string mappedPrefix = "::ffff:";
        return ip.StartsWith(mappedPrefix, StringComparison.OrdinalIgnoreCase)
            ? ip[mappedPrefix.Length..]
            : ip;
    }
}
