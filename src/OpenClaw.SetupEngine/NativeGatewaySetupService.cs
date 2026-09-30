using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Connection;
using OpenClaw.Connection.NativeGateway;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

public enum NativeGatewaySetupStage { StartingGateway, VerifyingEndpoint }

public interface INativeGatewaySetupHost
{
    void ReportProgress(NativeGatewaySetupStage stage) { }

    Task<NativeGatewayContract> DetectContractAsync(
        NativeGatewayPackage package, CancellationToken cancellationToken);

    Task<IsolatedGatewayConfiguration> PrepareIsolatedConfigurationAsync(
        NativeGatewayPackage package, int port, CancellationToken cancellationToken);

    Task ApplyIsolatedCapabilitiesAsync(
        NativeGatewayPackage package, IReadOnlyList<string> commandIds,
        CancellationToken cancellationToken);

    Task<IsolatedGatewayConfiguration> CheckIsolatedPairingConfigurationAsync(
        NativeGatewayPackage package, CancellationToken cancellationToken);

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

public sealed record NativeGatewaySetupDraft(string GatewayId, int Port, string PackageFamilyName)
{
    // Durable intent makes a port-only config/descriptor update recoverable across a crash.
    public int? PreviousPort { get; init; }
    public NativeGatewayContract Contract { get; init; } = NativeGatewayContract.Legacy;
}

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
        var contract = await host.DetectContractAsync(package, cancellationToken);
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
                if (saved.Contract != contract)
                    throw new NativeGatewayDraftRecoveryRequiredException(
                        "The saved native setup draft belongs to a different Gateway runtime. " +
                        "Discard the unpublished draft to create a new isolated Gateway profile.");
                return contract == NativeGatewayContract.IsolatedSessionV1
                    ? saved
                    : ResumeDraft(saved, draftPath);
            }
        }
        var port = SelectAvailablePort();
        var draft = new NativeGatewaySetupDraft(Guid.NewGuid().ToString("N"), port, package.PackageFamilyName)
        {
            Contract = contract
        };
        Directory.CreateDirectory(Path.GetDirectoryName(draftPath)!);
        AtomicFile.WriteAllText(draftPath, JsonSerializer.Serialize(draft));
        return draft;
    }

    /// <summary>
    /// Discards only the setup descriptor after explicit runtime replacement consent.
    /// A removed registry entry does not prove its profile was never published.
    /// </summary>
    public async Task DiscardIncompatibleDraftAsync(CancellationToken cancellationToken)
    {
        var draftPath = GetDraftPath(registry);
        if (!File.Exists(draftPath))
            return;

        var saved = JsonSerializer.Deserialize<NativeGatewaySetupDraft>(
            await File.ReadAllTextAsync(draftPath, cancellationToken).ConfigureAwait(false))
            ?? throw new InvalidOperationException("The saved native setup draft is invalid.");
        _ = NativeGatewayPaths.GetStateDirectory(registry, saved.GatewayId);
        registry.Load();
        if (registry.GetById(saved.GatewayId) is not null)
            throw new InvalidOperationException("The saved native Gateway profile has already been published and cannot be discarded from setup.");

        cancellationToken.ThrowIfCancellationRequested();
        // As with Store migration recovery, discard intent, never the data it describes.
        File.Delete(draftPath);
    }

    private NativeGatewaySetupDraft ResumeDraft(NativeGatewaySetupDraft draft, string draftPath)
    {
        if (draft.PreviousPort is not null)
            draft = FinishPortChange(draft, draftPath);
        if (IsPortAvailable(draft.Port))
            return draft;

        var configPath = NativeGatewayPaths.GetConfigPath(registry, draft.GatewayId);
        if (File.Exists(configPath))
            _ = ReadConfiguredRecord(draft, File.ReadAllText(configPath));
        var pending = draft with { Port = SelectAvailablePort(), PreviousPort = draft.Port };
        AtomicFile.WriteAllText(draftPath, JsonSerializer.Serialize(pending));
        System.Diagnostics.Trace.TraceInformation("Native setup is replacing an unavailable draft port.");
        return FinishPortChange(pending, draftPath);
    }

    /// <summary>Signals that an unpublished same-user draft requires explicit replacement after a package upgrade.</summary>
    public sealed class NativeGatewayDraftRecoveryRequiredException(string message) : InvalidOperationException(message);

    private NativeGatewaySetupDraft FinishPortChange(NativeGatewaySetupDraft draft, string draftPath)
    {
        var previousPort = draft.PreviousPort
            ?? throw new InvalidOperationException("The native setup port change has no previous port.");
        ArgumentOutOfRangeException.ThrowIfLessThan(previousPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(previousPort, 65535);
        var configPath = NativeGatewayPaths.GetConfigPath(registry, draft.GatewayId);
        if (File.Exists(configPath))
        {
            var json = File.ReadAllText(configPath);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("gateway", out var gateway) ||
                gateway.ValueKind != JsonValueKind.Object ||
                !gateway.TryGetProperty("port", out var port) || !port.TryGetInt32(out var currentPort) ||
                (currentPort != previousPort && currentPort != draft.Port))
                throw new InvalidOperationException("Native Gateway configuration changed during port recovery. Restore the draft configuration before retrying.");
            _ = ReadConfiguredRecord(draft with { Port = currentPort }, json);
            var config = JsonNode.Parse(json)!.AsObject();
            config["gateway"]!["port"] = draft.Port;
            AtomicFile.WriteAllText(configPath, config.ToJsonString());
        }
        var completed = draft with { PreviousPort = null };
        AtomicFile.WriteAllText(draftPath, JsonSerializer.Serialize(completed));
        return completed;
    }

    private static int SelectAvailablePort()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (!Socket.OSSupportsIPv6 || CanBind(IPAddress.IPv6Loopback, port))
                return port;
        }
        throw new IOException("Could not select an available native Gateway port. Close conflicting listeners and retry.");
    }

    private static bool IsPortAvailable(int port) =>
        CanBind(IPAddress.Loopback, port) && (!Socket.OSSupportsIPv6 || CanBind(IPAddress.IPv6Loopback, port));

    private static bool CanBind(IPAddress address, int port)
    {
        using var listener = new TcpListener(address, port) { ExclusiveAddressUse = true };
        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            return false;
        }
    }

    internal static string GetDraftPath(GatewayRegistry registry) =>
        Path.Combine(Path.GetDirectoryName(registry.GetIdentityDirectory("native-setup"))!, "native-setup-draft.json");

    public async Task<NativeGatewaySetupSession> PrepareAsync(
        NativeGatewaySetupDraft draft,
        CancellationToken cancellationToken)
    {
        if (draft.PreviousPort is not null)
            throw new InvalidOperationException("Resume the native setup draft to finish port recovery before preparing the Gateway.");
        ArgumentOutOfRangeException.ThrowIfLessThan(draft.Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(draft.Port, 65535);
        var package = await packageResolver.ResolveAsync(cancellationToken);
        if (!string.Equals(package.PackageFamilyName, draft.PackageFamilyName, StringComparison.Ordinal))
            throw new InvalidOperationException("The installed Gateway package changed. Return to setup and check it again.");
        var contract = await host.DetectContractAsync(package, cancellationToken);
        if (contract != draft.Contract)
            throw new InvalidOperationException(
                "The installed Gateway runtime changed during setup. Retry after checking the package.");
        package = package with { Contract = contract };

        if (contract == NativeGatewayContract.IsolatedSessionV1)
        {
            await host.PreparePackageAsync(package, new Dictionary<string, string>(), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            IsolatedGatewayConfiguration configured = await host.PrepareIsolatedConfigurationAsync(
                package, draft.Port, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var selected = draft with { Port = configured.Port };
            if (selected.Port != draft.Port)
                AtomicFile.WriteAllText(GetDraftPath(registry), JsonSerializer.Serialize(selected));
            var isolatedRecord = new GatewayRecord
            {
                Id = selected.GatewayId,
                Url = $"ws://127.0.0.1:{selected.Port}",
                FriendlyName = "Native Gateway",
                IsLocal = true,
                RequiresV2Signature = true,
                SharedGatewayToken = configured.Token,
                NativePackageFamilyName = package.PackageFamilyName,
                NativeRuntimeContract = NativeGatewayPackageClient.IsolatedContract
            };
            var isolatedSession = new NativeGatewaySetupSession(
                registry, selected, isolatedRecord, package,
                new Dictionary<string, string>(), host, runtimeFactory());
            try
            {
                await isolatedSession.PrepareAsync(cancellationToken);
                return isolatedSession;
            }
            catch
            {
                await isolatedSession.DisposeAsync();
                throw;
            }
        }

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
