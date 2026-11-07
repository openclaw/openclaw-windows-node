using System.Text;
using System.Text.Json;
using OpenClaw.Connection.LocalAi;
using OpenClaw.SetupEngine;

namespace OpenClawTray.Services;

internal sealed record LocalAiGatewayConfigurationSnapshot(JsonElement Config, string Hash, object? Owner = null);

/// <summary>A coherent read and a conditional merge, never a read followed by an unguarded CLI write.</summary>
internal interface ILocalAiGatewayAtomicConfigurationTransport
{
    Task<LocalAiGatewayConfigurationSnapshot> CaptureAsync(CancellationToken ct);
    Task ApplyAsync(LocalAiGatewayConfigurationSnapshot expected, JsonElement patch, CancellationToken ct,
        IReadOnlyList<string>? replacePaths = null);
}

/// <summary>
/// Borrows an authenticated setup transport. Published callers must borrow from
/// GatewayConnectionManager; staged callers retain NativeGatewaySetupConnection ownership.
/// </summary>
internal sealed class LocalAiGatewayRpcConfigurationTransport : ILocalAiGatewayAtomicConfigurationTransport
{
    private const int MaximumConfigBytes = 1024 * 1024;
    private readonly IGatewayAiSetupTransport _transport;
    private readonly GatewayAiSetupRoute _owner;
    private readonly long _generation;
    public string? LastConfirmedHash { get; private set; }
    public Action? BeforeMutationDispatch { get; set; }

    public LocalAiGatewayRpcConfigurationTransport(
        NativeLocalAiGatewayTarget target, IGatewayAiSetupTransport transport)
    {
        ArgumentNullException.ThrowIfNull(target);
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        target.RequireCurrent(transport.Route);
        _owner = target.Route;
        _generation = transport.Generation;
        RequireOwner();
    }

    public async Task<LocalAiGatewayConfigurationSnapshot> CaptureAsync(CancellationToken ct)
    {
        RequireOwner();
        var response = await _transport.RequestAsync("config.get", new { }, 15_000, ct).ConfigureAwait(false);
        RequireOwner();
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("hash", out var hash) ||
            hash.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(hash.GetString()) ||
            hash.GetString()!.Length > 512 ||
            !response.TryGetProperty("config", out var config) ||
            config.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(config.GetRawText()) > MaximumConfigBytes ||
            response.TryGetProperty("valid", out var valid) && valid.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("The Gateway did not return a valid, versioned configuration snapshot.");
        return new(config.Clone(), hash.GetString()!, this);
    }

    public async Task ApplyAsync(LocalAiGatewayConfigurationSnapshot expected, JsonElement patch, CancellationToken ct,
        IReadOnlyList<string>? replacePaths = null)
    {
        LastConfirmedHash = null;
        ArgumentNullException.ThrowIfNull(expected);
        RequireOwner();
        if (!ReferenceEquals(expected.Owner, this) || patch.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(patch.GetRawText()) > MaximumConfigBytes)
            throw new InvalidOperationException("The Local AI configuration change does not belong to this Gateway snapshot.");
        var replacements = replacePaths?.ToArray() ?? [];
        if (replacements.Length > 1 ||
            replacements.Any(path => path != LocalAiGatewayProviderDefinition.ProviderModelsPath))
            throw new InvalidOperationException("Only the owned llamacpp model array may be replaced.");
        ct.ThrowIfCancellationRequested();
        object parameters = replacements.Length == 0
            ? new { raw = patch.GetRawText(), baseHash = expected.Hash }
            : new { raw = patch.GetRawText(), baseHash = expected.Hash, replacePaths = replacements };
        // Once dispatched, drain the bounded request even when setup is cancelled.
        // A cancelled local wait cannot prove that a remote write was rolled back.
        var response = await _transport.RequestMutationAsync("config.patch",
            parameters,
            15_000, ct, BeforeMutationDispatch).ConfigureAwait(false);
        if (!response.TryGetProperty("hash", out var hash) || hash.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(hash.GetString()) || hash.GetString()!.Length > 512)
            throw new InvalidDataException("The Gateway did not acknowledge the persisted configuration revision.");
        LastConfirmedHash = hash.GetString();
        RequireOwner();
    }

    private void RequireOwner()
    {
        if (!_transport.IsConnected || _transport.Route != _owner || _transport.Generation != _generation)
            throw new InvalidOperationException("The bound Local AI Gateway is offline or its session changed. Reconnect the original Gateway before retrying.");
        if (!_transport.Methods.Contains("config.get", StringComparer.Ordinal) ||
            !_transport.Methods.Contains("config.patch", StringComparer.Ordinal) ||
            !_transport.OperatorScopes.Contains("operator.admin", StringComparer.Ordinal))
            throw new InvalidOperationException("The bound Gateway does not grant guarded Local AI configuration access.");
    }
}
