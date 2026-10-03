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
    private sealed record RemovalReceipt(string GatewayId, string Binding, string DistroName, string InstallPath, string RegistrationId);

    public WslGatewayRemoval() : this(new WindowsWslRegistrationInspector(),
        (ctx, ct) => new StartKeepaliveStep().RollbackAsync(ctx, ct)) { }

    internal WslGatewayRemoval(IWslRegistrationInspector registrations,
        Func<SetupContext, CancellationToken, Task> stopKeepalive,
        Func<SetupContext, CancellationToken, Task>? removeDistro = null)
    {
        _registrations = registrations;
        _stopKeepalive = stopKeepalive;
        _removeDistro = removeDistro;
    }

    public async Task<PipelineResult> RunAsync(SetupContext ctx, string gatewayId, string binding)
    {
        var ct = ctx.CancellationToken;
        try
        {
            if (!ctx.Config.ConfirmDestructive && !ctx.Config.DryRun)
                throw new InvalidOperationException("Gateway removal requires explicit destructive confirmation.");
            var registry = new GatewayRegistry(ctx.DataDir);
            registry.Load();
            var expected = registry.GetActive();
            if (expected?.Id != gatewayId || LocalGatewaySettings.Classify(expected) != LocalGatewayKind.Wsl ||
                GatewayDashboardBinding.Capture(expected) != binding)
                throw new InvalidOperationException("The selected WSL Gateway changed. Review removal again.");
            ctx.DistroName = ctx.Config.DistroName = GatewayRecordEditing.ResolveManagedDistroName(expected)!;
            var inspection = RequireOwnedRegistration(ctx, expected);
            if (ctx.Config.DryRun)
                return new(PipelineOutcome.Success, Message: "The selected WSL installation is eligible for removal. No files or processes were changed.");

            ctx.IsUninstalling = true;
            if (inspection.Status == WslRegistrationInspectionStatus.Found)
            {
                await AtomicFile.WriteAllTextAsync(ReceiptPath(ctx, expected),
                    JsonSerializer.Serialize(new RemovalReceipt(expected.Id, binding, ctx.DistroName!,
                        ManagedPath(ctx), inspection.RegistrationId!)), ct).ConfigureAwait(false);
                await _stopKeepalive(ctx, ct).ConfigureAwait(false);
                var fresh = RequireOwnedRegistration(ctx, expected);
                if (fresh != inspection)
                    throw new InvalidOperationException("The WSL registration changed during removal. No distro was unregistered.");
                if (_removeDistro is not null)
                    await _removeDistro(ctx, ct).ConfigureAwait(false);
                else
                    await RemoveRegisteredDistroAsync(ctx, inspection, ct).ConfigureAwait(false);
            }

            var after = _registrations.Inspect(ctx.DistroName!);
            if (after.Status != WslRegistrationInspectionStatus.NotFound)
                throw new InvalidOperationException("WSL did not confirm removal. The saved Gateway was retained for retry.");
            var installPath = ManagedPath(ctx);
            if (Directory.Exists(installPath) || File.Exists(installPath))
            {
                var deletion = await CleanupStaleDistroStep.DeleteDistroDirectoryWithRetries(
                    ctx, ctx.DistroName!, installPath, ct).ConfigureAwait(false);
                if (!deletion.IsSuccess) throw new IOException(deletion.Message);
            }
            ct.ThrowIfCancellationRequested();
            await WindowsNodeBootstrapContextStep.ForgetRemovedDistroAsync(ctx, ct).ConfigureAwait(false);
            RemoveMatchingSetupState(ctx);
            // Retain the target and its identity until every installation cleanup step succeeds.
            GatewayIdentityRemoval.Remove(registry, expected);
            ManagedDistroOwnership.DeleteMarker(ctx.LocalDataDir, ctx.DistroName!, installPath);
            File.Delete(ReceiptPath(ctx, expected));
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

    private async Task RemoveRegisteredDistroAsync(SetupContext ctx, WslRegistrationInspection expected, CancellationToken ct)
    {
        foreach (var operation in new[] { "--terminate", "--unregister" })
        {
            ct.ThrowIfCancellationRequested();
            var current = _registrations.Inspect(ctx.DistroName!);
            if (current.Status == WslRegistrationInspectionStatus.NotFound) return;
            if (current != expected)
                throw new InvalidOperationException("The WSL registration changed. No command was sent to the replacement.");
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
        Path.Combine(ctx.LocalDataDir, $"wsl-removal-{GatewayDashboardBinding.Capture(record)}.json");

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
