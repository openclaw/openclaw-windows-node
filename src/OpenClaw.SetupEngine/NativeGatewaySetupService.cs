using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Connection.NativeGateway;

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
        NativeGatewayPackage package, CancellationToken cancellationToken);

    Task<string> ApproveDevicePairingAsync(
        NativeGatewayPackage package, string requestId, CancellationToken cancellationToken);

    IDisposable OpenRecoveryTerminal(NativeGatewayPackage package);

    Task PreparePackageAsync(NativeGatewayPackage package, CancellationToken cancellationToken);

    Task ValidateConfigurationAsync(NativeGatewayPackage package, CancellationToken cancellationToken);

    Task VerifyHealthAsync(NativeGatewayPackage package, CancellationToken cancellationToken);
}

public sealed record NativeGatewaySetupDraft(string GatewayId, int Port, string PackageFamilyName)
{
    /// <summary>
    /// The contract this draft was created for. A draft saved by the retired same-user
    /// runtime deserializes to a value other than IsolatedSessionV1 and must be discarded.
    /// </summary>
    public NativeGatewayContract Contract { get; init; }
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
                    throw new NativeGatewayDraftRecoveryRequiredException(
                        "The installed Gateway package does not match the saved setup profile. " +
                        "Discard the unfinished setup draft to create a new profile for the installed Gateway package.");
                if (saved.Contract != contract)
                    throw new NativeGatewayDraftRecoveryRequiredException(
                        "The saved native setup draft belongs to a different Gateway runtime. " +
                        "Discard the unpublished draft to create a new isolated Gateway profile.");
                return saved;
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

    /// <summary>
    /// Signals that an unpublished draft no longer matches the installed package or its
    /// runtime contract, and needs explicit discard before setup can continue.
    /// </summary>
    public sealed class NativeGatewayDraftRecoveryRequiredException(string message) : InvalidOperationException(message);

    private static int SelectAvailablePort() => SelectAvailablePort(PreferredPort);

    // The conventional Gateway port keeps the native endpoint predictable; any
    // listener already holding it (WSL, CLI, or a source-built Gateway) wins.
    internal const int PreferredPort = 18789;

    internal static int SelectAvailablePort(int preferredPort)
    {
        if (IsPortAvailable(preferredPort))
            return preferredPort;

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
        Path.Combine(
            Path.GetDirectoryName(registry.GetIdentityDirectory("native-setup"))!,
            OpenClawAppIdentity.NativeGatewaySetupDraftFileName);

    public async Task<NativeGatewaySetupSession> PrepareAsync(
        NativeGatewaySetupDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(draft.Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(draft.Port, 65535);
        var package = await packageResolver.ResolveAsync(cancellationToken);
        if (!string.Equals(package.PackageFamilyName, draft.PackageFamilyName, StringComparison.Ordinal))
            throw new InvalidOperationException("The installed Gateway package changed. Return to setup and check it again.");
        var contract = await host.DetectContractAsync(package, cancellationToken);
        if (contract != NativeGatewayContract.IsolatedSessionV1 || contract != draft.Contract)
            throw new InvalidOperationException(
                "The installed Gateway runtime changed during setup. Retry after checking the package.");
        package = package with { Contract = contract };

        await host.PreparePackageAsync(package, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        IsolatedGatewayConfiguration configured = await host.PrepareIsolatedConfigurationAsync(
            package, draft.Port, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var selected = draft with { Port = configured.Port };
        if (selected.Port != draft.Port)
            AtomicFile.WriteAllText(GetDraftPath(registry), JsonSerializer.Serialize(selected));
        var record = new GatewayRecord
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
        var session = new NativeGatewaySetupSession(
            registry, selected, record, package, host, runtimeFactory());
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
}
