using OpenClaw.Connection;
using OpenClaw.SetupEngine;

namespace OpenClawTray.Services;

internal sealed class NativeGatewayKeepAliveService(Func<GatewayRegistry?> getRegistry)
{
    private readonly Func<GatewayRegistry?> _getRegistry = getRegistry;
    private static string StopIntentPath => Path.Combine(
        AppIdentity.ResolveSetupLocalDataDirectory(),
        "native-gateway-user-stopped.json");

    public static void RecordUserStopped(string taskName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StopIntentPath)!);
        File.WriteAllText(
            StopIntentPath,
            System.Text.Json.JsonSerializer.Serialize(
                new { TaskName = taskName, StoppedAtUtc = DateTime.UtcNow },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    public static void ClearUserStopped()
    {
        if (File.Exists(StopIntentPath))
            File.Delete(StopIntentPath);
    }

    public async Task TryEnsureAsync()
    {
        try
        {
            var activeRecord = _getRegistry()?.GetActive();
            if (activeRecord is not { IsLocal: true } ||
                string.IsNullOrWhiteSpace(activeRecord.SetupManagedNativeTaskName))
            {
                return;
            }

            if (IsUserStopped(activeRecord.SetupManagedNativeTaskName))
            {
                Logger.Info("[NativeGatewayKeepAlive] Managed native gateway was explicitly stopped by the user; skipping auto-start.");
                return;
            }

            var controller = new ManagedNativeGatewayController(
                AppIdentity.ResolveRoamingDataDirectory(),
                AppIdentity.ResolveSetupLocalDataDirectory());
            var status = await controller.RunAsync(
                activeRecord.SetupManagedNativeTaskName,
                NativeGatewayControlAction.Status).ConfigureAwait(false);

            if (status.IsRunning)
            {
                Logger.Info("[NativeGatewayKeepAlive] Managed native gateway is running.");
                return;
            }

            Logger.Warn($"[NativeGatewayKeepAlive] Managed native gateway is not running; attempting start. Status: {status.OutputSummary}");
            var start = await controller.RunAsync(
                activeRecord.SetupManagedNativeTaskName,
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
            return document.RootElement.TryGetProperty("TaskName", out var value)
                && string.Equals(value.GetString(), taskName, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Logger.Warn($"[NativeGatewayKeepAlive] Failed to read native stop intent marker: {ex.Message}");
            return false;
        }
    }
}
