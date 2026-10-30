using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

/// <summary>Removes one proven WSL installation, not the Companion profile or its shared Local AI files.</summary>
public sealed class WslGatewayRemoval
{
    private readonly IWslRegistrationInspector _registrations;
    private readonly Func<SetupContext, CancellationToken, Task> _stopKeepalive;
    private readonly Func<SetupContext, CancellationToken, Task>? _removeDistro;
    private readonly Func<TimeSpan, CancellationToken, Task> _deletionDelay;
    private readonly IFileSystem? _registryFileSystem;
    private sealed record RemovalReceipt(string GatewayId, string Binding, string DistroName, string InstallPath, string RegistrationId);

    public WslGatewayRemoval() : this(new WindowsWslRegistrationInspector(),
        (ctx, ct) => new StartKeepaliveStep().RollbackAsync(ctx, ct)) { }

    internal WslGatewayRemoval(IWslRegistrationInspector registrations,
        Func<SetupContext, CancellationToken, Task> stopKeepalive,
        Func<SetupContext, CancellationToken, Task>? removeDistro = null,
        Func<TimeSpan, CancellationToken, Task>? deletionDelay = null,
        IFileSystem? registryFileSystem = null)
    {
        _registrations = registrations;
        _stopKeepalive = stopKeepalive;
        _removeDistro = removeDistro;
        _deletionDelay = deletionDelay ?? Task.Delay;
        _registryFileSystem = registryFileSystem;
    }

    public async Task<PipelineResult> RunAsync(SetupContext ctx, string gatewayId, string binding)
    {
        var ct = ctx.CancellationToken;
        try
        {
            if (!ctx.Config.ConfirmDestructive && !ctx.Config.DryRun)
                throw new InvalidOperationException("Gateway removal requires explicit destructive confirmation.");
            var registry = new GatewayRegistry(ctx.DataDir, _registryFileSystem);
            var snapshot = registry.AdoptPersistedSnapshot(registry.GetSnapshot());
            var expected = snapshot.Records.SingleOrDefault(record => record.Id == snapshot.ActiveId);
            if (expected?.Id != gatewayId || LocalGatewaySettings.Classify(expected) != LocalGatewayKind.Wsl ||
                LocalGatewaySettings.CaptureRemovalAuthority(expected) != binding)
                throw new InvalidOperationException("The selected WSL Gateway changed. Review removal again.");
            ctx.DistroName = ctx.Config.DistroName = GatewayRecordEditing.ResolveManagedDistroName(expected)!;
            var inspection = RequireOwnedRegistration(ctx, expected);
            if (ctx.Config.DryRun)
                return new(PipelineOutcome.Success, Message: "The selected WSL installation is eligible for removal. No files or processes were changed.");

            ctx.IsUninstalling = true;
            if (inspection.Status == WslRegistrationInspectionStatus.Found)
            {
                await AtomicFile.WriteAllTextAsync(ReceiptPath(ctx, expected),
                    JsonSerializer.Serialize(new RemovalReceipt(expected.Id, GatewayDashboardBinding.Capture(expected), ctx.DistroName!,
                        ManagedPath(ctx), inspection.RegistrationId!)), ct).ConfigureAwait(false);
                if (RequireOwnedRegistration(ctx, expected) != inspection)
                    throw new InvalidOperationException("The WSL registration changed during removal. No keepalive was stopped.");
                RequireCurrentTarget(registry, expected);
                ct.ThrowIfCancellationRequested();
                await _stopKeepalive(ctx, ct).ConfigureAwait(false);
                var fresh = RequireOwnedRegistration(ctx, expected);
                if (fresh != inspection)
                    throw new InvalidOperationException("The WSL registration changed during removal. No distro was unregistered.");
                RequireCurrentTarget(registry, expected);
                ct.ThrowIfCancellationRequested();
                if (_removeDistro is not null)
                    await _removeDistro(ctx, ct).ConfigureAwait(false);
                else
                    await RemoveRegisteredDistroAsync(ctx, registry, expected, inspection, ct).ConfigureAwait(false);
            }

            var after = _registrations.Inspect(ctx.DistroName!);
            if (after.Status != WslRegistrationInspectionStatus.NotFound)
                throw new InvalidOperationException("WSL did not confirm removal. The saved Gateway was retained for retry.");
            var installPath = ManagedPath(ctx);
            if (Directory.Exists(installPath) || File.Exists(installPath))
            {
                var deletion = await CleanupStaleDistroStep.DeleteDistroDirectoryWithRetries(
                    ctx, ctx.DistroName!, installPath, ct,
                    () => RequireResidualDeletionAuthority(ctx, registry, expected),
                    _deletionDelay).ConfigureAwait(false);
                if (!deletion.IsSuccess) throw new IOException(deletion.Message);
            }
            ct.ThrowIfCancellationRequested();
            RequireResidualDeletionAuthority(ctx, registry, expected);
            await WindowsNodeBootstrapContextStep.ForgetRemovedDistroAsync(ctx, ct).ConfigureAwait(false);
            RequireResidualDeletionAuthority(ctx, registry, expected);
            RemoveMatchingSetupState(ctx);
            // Finish and verify ownership metadata cleanup while the saved target still exists, so a
            // locked marker or receipt leaves a retryable target instead of stale metadata.
            ManagedDistroOwnership.DeleteMarker(ctx.LocalDataDir, ctx.DistroName!, installPath);
            if (ManagedDistroOwnership.HasPathBoundMarkerEvidence(ctx.LocalDataDir, ctx.DistroName!))
                throw new IOException("The WSL ownership marker could not be deleted. The saved Gateway was retained for retry.");
            File.Delete(ReceiptPath(ctx, expected));
            GatewayIdentityRemoval.Remove(registry, expected);
            return new(PipelineOutcome.Success, Message: "The selected WSL Gateway was removed. Other gateways and Companion data were preserved.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new(PipelineOutcome.Cancelled, Message: "Gateway removal was cancelled. Retry the saved Gateway to finish cleanup.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or InvalidDataException or ArgumentException or JsonException or TimeoutException)
        {
            ctx.Logger.Error($"Selected WSL Gateway removal failed: {exception.Message}");
            return new(PipelineOutcome.Failed, Message: exception.Message);
        }
    }

    private WslRegistrationInspection RequireOwnedRegistration(SetupContext ctx, GatewayRecord expected)
    {
        var path = ManagedPath(ctx);
        var hasMarker = ManagedDistroOwnership.HasPathBoundMarkerEvidence(ctx.LocalDataDir, ctx.DistroName!);
        var receipt = ReadReceipt(ctx, expected, path);
        var inspection = _registrations.Inspect(ctx.DistroName!);
        if (inspection.Status == WslRegistrationInspectionStatus.NotFound &&
            (!Directory.Exists(path) && !File.Exists(path) || hasMarker || receipt is not null))
            return inspection;
        if (inspection.Status != WslRegistrationInspectionStatus.Found ||
            string.IsNullOrWhiteSpace(inspection.BasePath) ||
            !DistroInstallPathPolicy.PathsReferToSameLocation(inspection.BasePath, path) ||
            !hasMarker && string.IsNullOrWhiteSpace(expected.SetupManagedDistroName) ||
            receipt is not null && inspection.RegistrationId != receipt.RegistrationId)
            throw new InvalidOperationException(
                "The WSL registration is not proven to belong to this OpenClaw installation. No distro was removed.");
        return inspection;
    }

    private static void RequireCurrentTarget(GatewayRegistry registry, GatewayRecord expected)
    {
        var snapshot = registry.AdoptPersistedSnapshot(registry.GetSnapshot());
        if (snapshot.ActiveId != expected.Id ||
            !LocalGatewaySettings.IsSameTarget(expected, snapshot.Records.SingleOrDefault(record => record.Id == expected.Id)))
            throw new InvalidOperationException("The selected WSL Gateway changed. No further removal was performed.");
    }

    private void RequireResidualDeletionAuthority(SetupContext ctx, GatewayRegistry registry, GatewayRecord expected)
    {
        if (RequireOwnedRegistration(ctx, expected).Status != WslRegistrationInspectionStatus.NotFound)
            throw new InvalidOperationException("The WSL distro is registered again. Its files and saved Gateway were retained.");
        RequireCurrentTarget(registry, expected);
    }

    private async Task RemoveRegisteredDistroAsync(SetupContext ctx, GatewayRegistry registry,
        GatewayRecord target, WslRegistrationInspection expected, CancellationToken ct)
    {
        foreach (var operation in new[] { "--terminate", "--unregister" })
        {
            ct.ThrowIfCancellationRequested();
            var current = _registrations.Inspect(ctx.DistroName!);
            if (current.Status == WslRegistrationInspectionStatus.NotFound) return;
            if (current != expected)
                throw new InvalidOperationException("The WSL registration changed. No command was sent to the replacement.");
            RequireCurrentTarget(registry, target);
            var result = await ctx.Commands.RunAsync(WslConstants.WslExePath,
                [operation, ctx.DistroName!], TimeSpan.FromSeconds(60), ct: ct).ConfigureAwait(false);
            if (result.TimedOut)
                throw new TimeoutException("WSL Gateway removal timed out. The saved target was retained; check WSL status before retrying.");
            if (result.ExitCode != 0 &&
                _registrations.Inspect(ctx.DistroName!).Status != WslRegistrationInspectionStatus.NotFound)
                throw new InvalidOperationException($"WSL Gateway removal failed during {operation} (exit {result.ExitCode}). Retry the saved target after checking WSL status.");
        }
    }

    private static string ReceiptPath(SetupContext ctx, GatewayRecord record) =>
        ReceiptPath(ctx.LocalDataDir, record);

    private static string ReceiptPath(string localDataDir, GatewayRecord record) =>
        Path.Combine(localDataDir, $"wsl-removal-{GatewayDashboardBinding.Capture(record)}.json");

    /// <summary>
    /// True once a removal wrote its receipt. The receipt is written before any destructive step and
    /// deleted only on success, so its presence means teardown may have started.
    /// </summary>
    public static bool HasRemovalReceipt(string localDataDir, GatewayRecord record) =>
        File.Exists(ReceiptPath(localDataDir, record));

    private static RemovalReceipt? ReadReceipt(SetupContext ctx, GatewayRecord record, string path)
    {
        var receiptPath = ReceiptPath(ctx, record);
        if (!File.Exists(receiptPath)) return null;
        RemovalReceipt? receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<RemovalReceipt>(File.ReadAllText(receiptPath));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The WSL removal receipt at '{receiptPath}' is invalid. Inspect or restore the receipt before retrying; no distro was removed.", exception);
        }
        if (receipt is null || receipt.GatewayId != record.Id || receipt.Binding != GatewayDashboardBinding.Capture(record) ||
            receipt.DistroName != ctx.DistroName || string.IsNullOrWhiteSpace(receipt.RegistrationId) ||
            !DistroInstallPathPolicy.PathsReferToSameLocation(receipt.InstallPath, path))
            throw new InvalidDataException(
                $"The WSL removal receipt at '{receiptPath}' does not match this installation. Inspect or restore the receipt before retrying; no distro was removed.");
        return receipt;
    }

    private static string ManagedPath(SetupContext ctx)
    {
        if (!DistroInstallPathPolicy.TryGetManagedInstallPath(ctx.LocalDataDir, ctx.DistroName, out var path, out var error))
            throw new InvalidOperationException(error);
        return path;
    }

    private static void RemoveMatchingSetupState(SetupContext ctx)
    {
        var path = Path.Combine(ctx.LocalDataDir, "setup-state.json");
        using var lease = PersistenceFileLease.Acquire(path);
        if (!File.Exists(path)) return;
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        if (json.RootElement.TryGetProperty("DistroName", out var name) &&
            string.Equals(name.GetString(), ctx.DistroName, StringComparison.OrdinalIgnoreCase))
            File.Delete(path);
    }
}
