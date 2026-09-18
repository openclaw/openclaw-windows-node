using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

public enum NativeGatewaySetupStage { StartingGateway, VerifyingEndpoint }

public interface INativeGatewaySetupHost
{
    void ReportProgress(NativeGatewaySetupStage stage) { }

    Task<string> ListDevicePairingRequestsAsync(
        NativeGatewayPackage package,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken);

    Task<string> ApproveDevicePairingAsync(
        NativeGatewayPackage package, string requestId,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken);

    IDisposable OpenRecoveryTerminal(
        NativeGatewayPackage package,
        IReadOnlyDictionary<string, string> environment);

    Task PreparePackageAsync(
        NativeGatewayPackage package,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken);

    Task ValidateConfigurationAsync(
        NativeGatewayPackage package,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken);

    Task VerifyHealthAsync(
        NativeGatewayPackage package,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken);
}

public sealed record NativeGatewaySetupDraft(string GatewayId, int Port, string PackageFamilyName);

/// <summary>
/// Configures only a new, dedicated native gateway profile. This never runs the WSL
/// replacement pipeline, installs a package, or changes the user's default .openclaw.
/// </summary>
public sealed class NativeGatewaySetupService(
    GatewayRegistry registry,
    INativeGatewayPackageResolver packageResolver,
    INativeGatewaySetupHost host,
    Func<INativeGatewayRuntime> runtimeFactory)
{
    public async Task<NativeGatewaySetupDraft> CreateDraftAsync(CancellationToken cancellationToken)
    {
        var package = await packageResolver.ResolveAsync(cancellationToken);
        var draftPath = GetDraftPath(registry);
        if (File.Exists(draftPath))
        {
            var saved = JsonSerializer.Deserialize<NativeGatewaySetupDraft>(
                await File.ReadAllTextAsync(draftPath, cancellationToken))
                ?? throw new InvalidOperationException("The saved native setup draft is invalid.");
            _ = NativeGatewayPaths.GetStateDirectory(registry, saved.GatewayId);
            ArgumentOutOfRangeException.ThrowIfLessThan(saved.Port, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(saved.Port, 65535);
            registry.Load();
            if (registry.GetById(saved.GatewayId) is null)
            {
                if (saved.PackageFamilyName != package.PackageFamilyName)
                    throw new InvalidOperationException("The installed Gateway package does not match the saved setup profile.");
                return saved;
            }
        }
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var draft = new NativeGatewaySetupDraft(Guid.NewGuid().ToString("N"), port, package.PackageFamilyName);
        Directory.CreateDirectory(Path.GetDirectoryName(draftPath)!);
        AtomicFile.WriteAllText(draftPath, JsonSerializer.Serialize(draft));
        return draft;
    }

    internal static string GetDraftPath(GatewayRegistry registry) =>
        Path.Combine(Path.GetDirectoryName(registry.GetIdentityDirectory("native-setup"))!, "native-setup-draft.json");

    public async Task<NativeGatewaySetupSession> PrepareAsync(
        NativeGatewaySetupDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(draft.Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(draft.Port, 65535);
        var package = await packageResolver.ResolveAsync(cancellationToken);
        if (!string.Equals(package.PackageFamilyName, draft.PackageFamilyName, StringComparison.Ordinal))
            throw new InvalidOperationException("The installed Gateway package changed. Return to setup and check it again.");

        var stateDirectory = NativeGatewayPaths.GetStateDirectory(registry, draft.GatewayId);
        var configPath = NativeGatewayPaths.GetConfigPath(registry, draft.GatewayId);
        var environment = BuildEnvironment(registry, draft.GatewayId, packageResolver);
        Directory.CreateDirectory(stateDirectory);
        if (!File.Exists(configPath))
        {
            var workspace = packageResolver.ResolveDataPath(Path.Combine(stateDirectory, "workspace"));
            var baseline = new
            {
                gateway = new
                {
                    mode = "local",
                    port = draft.Port,
                    bind = "loopback",
                    auth = new { mode = "token", token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) },
                },
                agents = new { defaults = new { workspace } },
            };
            AtomicFile.WriteAllText(configPath, JsonSerializer.Serialize(baseline));
        }

        await host.PreparePackageAsync(package, environment, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var record = ReadConfiguredRecord(draft, await File.ReadAllTextAsync(configPath, cancellationToken));
        var session = new NativeGatewaySetupSession(
            registry, draft, record, package, environment, host, runtimeFactory());
        try
        {
            await session.PrepareAsync(cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    internal static IReadOnlyDictionary<string, string> BuildEnvironment(
        GatewayRegistry registry,
        string gatewayId,
        INativeGatewayPackageResolver resolver) =>
        NativeGatewayPaths.GetEnvironment(registry, gatewayId, resolver.ResolveDataPath);

    internal static GatewayRecord ReadConfiguredRecord(NativeGatewaySetupDraft draft, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("gateway", out var gateway) ||
            gateway.ValueKind != JsonValueKind.Object ||
            !HasString(gateway, "mode", "local") ||
            !HasString(gateway, "bind", "loopback") ||
            !gateway.TryGetProperty("port", out var port) ||
            !port.TryGetInt32(out var configuredPort) ||
            configuredPort != draft.Port ||
            !gateway.TryGetProperty("auth", out var auth) ||
            auth.ValueKind != JsonValueKind.Object ||
            !HasString(auth, "mode", "token") ||
            !auth.TryGetProperty("token", out var token) ||
            token.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(token.GetString()))
        {
            throw new InvalidOperationException(
                "Native setup requires the selected local port, loopback binding, and token authentication. " +
                "Run Gateway setup again using those settings.");
        }

        return new GatewayRecord
        {
            Id = draft.GatewayId,
            Url = $"ws://127.0.0.1:{draft.Port}",
            FriendlyName = "Native Gateway",
            IsLocal = true,
            RequiresV2Signature = true,
            SharedGatewayToken = token.GetString(),
            NativePackageFamilyName = draft.PackageFamilyName,
        };
    }

    private static bool HasString(JsonElement element, string name, string value) =>
        element.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        string.Equals(property.GetString(), value, StringComparison.Ordinal);
}
