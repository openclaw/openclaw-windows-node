namespace OpenClaw.Connection.NativeGateway;

/// <summary>
/// Removes the explicitly selected installation. Call under the connection transition
/// gate and the profile's setup/Local AI ownership locks so neither can restart it.
/// </summary>
public sealed class NativeGatewayRemoval(
    GatewayRegistry registry, INativeGatewayPackageResolver resolver, NativeGatewayPackageClient packageClient)
{
    public async Task RemoveAsync(
        GatewayRecord expected, Func<Task> disconnect, CancellationToken cancellationToken)
    {
        var kind = LocalGatewaySettings.Classify(expected);
        if (kind is not (LocalGatewayKind.Native or LocalGatewayKind.LegacyNative))
            throw new InvalidOperationException("Select a supported native Gateway in Connection settings before removal.");

        var baseline = registry.CapturePersistedSnapshot();
        RequireTarget(expected, baseline, kind);
        NativeGatewayPackage? package = null;
        if (kind == LocalGatewayKind.Native)
        {
            package = await resolver.ResolveAsync(expected.NativePackageFamilyName!, cancellationToken).ConfigureAwait(false);
            NativeGatewayPaths.ValidatePackage(package, expected.NativePackageFamilyName!);
            if (await packageClient.DetectAsync(package, cancellationToken).ConfigureAwait(false) != NativeGatewayContract.IsolatedSessionV1)
                throw new InvalidOperationException("The Gateway package contract changed. Reopen Connection settings before removal.");
        }

        RequireTarget(expected, registry.CapturePersistedSnapshot(), kind);
        cancellationToken.ThrowIfCancellationRequested();
        await disconnect().ConfigureAwait(false);
        RequireTarget(expected, registry.CapturePersistedSnapshot(), kind);
        cancellationToken.ThrowIfCancellationRequested();
        if (package is not null)
            await packageClient.TeardownAsync(package, cancellationToken).ConfigureAwait(false);

        try
        {
            RequireTarget(expected, registry.CapturePersistedSnapshot(), kind);
            GatewayIdentityRemoval.Remove(registry, expected);
        }
        catch (Exception exception) when (package is not null &&
            exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "The native Gateway installation was removed, but connection/profile cleanup needs attention. " +
                "If the saved connection remains, select it and retry removal. " + exception.Message, exception);
        }
    }

    private static void RequireTarget(GatewayRecord expected, GatewayRegistrySnapshot snapshot, LocalGatewayKind kind)
    {
        if (snapshot.ActiveId != expected.Id ||
            !LocalGatewaySettings.IsSameTarget(expected, snapshot.Records.SingleOrDefault(record => record.Id == expected.Id)))
            throw new InvalidOperationException("The selected Gateway changed. Review removal again.");
        if (kind == LocalGatewayKind.Native && snapshot.Records.Any(record =>
            record.Id != expected.Id && record.NativePackageFamilyName == expected.NativePackageFamilyName &&
            record.NativeRuntimeContract == NativeGatewayPackageClient.IsolatedContract))
            throw new InvalidOperationException("Other saved connections use this native Gateway package. Remove those duplicate connections in Connection settings first.");
    }

}
