using OpenClaw.Connection;
using OpenClaw.SetupEngine;

namespace OpenClawTray.Services;

internal sealed class NativeGatewayKeepAliveService(
    Func<GatewayRegistry?> getRegistry,
    NativeGatewayLifecycleCoordinator? lifecycle = null)
{
    private readonly Func<GatewayRegistry?> _getRegistry = getRegistry;
    private readonly NativeGatewayLifecycleCoordinator? _lifecycle = lifecycle;
    private static string StopIntentPath => GatewayInstallModeDetector.GetNativeStopIntentPath(
        AppIdentity.ResolveSetupLocalDataDirectory());

    public static void RecordUserStopped(string taskName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StopIntentPath)!);
        var tempPath = StopIntentPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                tempPath,
                System.Text.Json.JsonSerializer.Serialize(
                    new { TaskName = taskName, StoppedAtUtc = DateTime.UtcNow },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, StopIntentPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    public static void ClearUserStopped()
    {
        GatewayInstallModeDetector.DeleteNativeStopIntent(AppIdentity.ResolveSetupLocalDataDirectory());
    }

    public async Task TryEnsureAsync()
    {
        try
        {
            var activeRecord = _getRegistry()?.GetActive();
            if (!IsManagedNativeRecord(activeRecord))
            {
                return;
            }
            var taskName = activeRecord!.SetupManagedNativeTaskName!;

            if (IsUserStopped(taskName))
            {
                Logger.Info("[NativeGatewayKeepAlive] Managed native gateway was explicitly stopped by the user; skipping auto-start.");
                return;
            }

            var controller = new ManagedNativeGatewayController(
                AppIdentity.ResolveRoamingDataDirectory(),
                AppIdentity.ResolveSetupLocalDataDirectory());
            var effectiveLifecycle = _lifecycle ?? new NativeGatewayLifecycleCoordinator(controller);
            var status = await effectiveLifecycle.RunAsync(
                taskName,
                NativeGatewayControlAction.Status).ConfigureAwait(false);

            if (status.IsRunning)
            {
                Logger.Info("[NativeGatewayKeepAlive] Managed native gateway is running.");
                return;
            }

            Logger.Warn($"[NativeGatewayKeepAlive] Managed native gateway is not running; attempting start. Status: {status.OutputSummary}");
            var start = await effectiveLifecycle.RunAsync(
                taskName,
                NativeGatewayControlAction.Start).ConfigureAwait(false);

            if (start.Success)
                Logger.Info("[NativeGatewayKeepAlive] Managed native gateway start requested.");
            else
                Logger.Warn($"[NativeGatewayKeepAlive] Managed native gateway start failed: {start.OutputSummary}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[NativeGatewayKeepAlive] Startup check failed (non-fatal): {ex.Message}");
        }
    }

    internal static bool IsUserStopped(string taskName)
    {
        if (!File.Exists(StopIntentPath))
            return false;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(StopIntentPath));
            var matches = document.RootElement.TryGetProperty("TaskName", out var value)
                && string.Equals(value.GetString(), taskName, StringComparison.Ordinal);
            if (!matches)
                ClearUserStopped();
            return matches;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Logger.Warn($"[NativeGatewayKeepAlive] Failed to read native stop intent marker: {ex.Message}");
            return false;
        }
    }

    internal static bool IsManagedNativeRecord(GatewayRecord? record) =>
        record is not null
        && record.SshTunnel is null
        && !string.IsNullOrWhiteSpace(record.SetupManagedNativeTaskName)
        && (record.IsLocal || OpenClaw.Shared.LocalGatewayUrlClassifier.IsLocalGatewayUrl(record.Url));
}
