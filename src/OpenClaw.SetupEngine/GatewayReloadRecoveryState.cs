using System.Text.Json;

namespace OpenClaw.SetupEngine;

internal sealed record GatewayReloadRecoveryState(
    int Version,
    string DistroName,
    string? ReloadMode,
    DateTimeOffset CreatedAtUtc);

internal static class GatewayReloadRecoveryStore
{
    internal const int CurrentVersion = 1;
    internal const string FileName = "setup-gateway-reload-recovery.json";

    internal static string GetPath(SetupContext ctx) => Path.Combine(ctx.LocalDataDir, FileName);

    internal static GatewayReloadRecoveryState? Load(SetupContext ctx)
    {
        var path = GetPath(ctx);
        GatewayReloadRecoveryState? state;
        try
        {
            state = JsonSerializer.Deserialize<GatewayReloadRecoveryState>(
                File.ReadAllText(path),
                SetupConfig.JsonOptions);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException($"Gateway reload recovery marker is unreadable: {ex.Message}", ex);
        }

        if (state is null)
            throw new InvalidDataException("Gateway reload recovery marker must contain a JSON object.");
        if (state.Version != CurrentVersion)
            throw new InvalidDataException($"Gateway reload recovery marker version {state.Version} is unsupported.");
        if (string.IsNullOrWhiteSpace(state.DistroName))
            throw new InvalidDataException("Gateway reload recovery marker is missing the distro name.");
        if (state.ReloadMode is not null && !IsSupportedReloadMode(state.ReloadMode))
            throw new InvalidDataException($"Gateway reload recovery marker contains unsupported reload mode '{state.ReloadMode}'.");

        return state;
    }

    internal static void Save(SetupContext ctx, string? reloadMode)
    {
        var distroName = ctx.DistroName;
        if (string.IsNullOrWhiteSpace(distroName))
            throw new InvalidOperationException("Cannot suspend gateway reload without a target distro name.");
        if (reloadMode is not null && !IsSupportedReloadMode(reloadMode))
            throw new InvalidOperationException($"Cannot suspend gateway reload with unsupported restore mode '{reloadMode}'.");

        var state = new GatewayReloadRecoveryState(
            CurrentVersion,
            distroName,
            reloadMode,
            DateTimeOffset.UtcNow);
        AtomicFile.WriteAllText(
            GetPath(ctx),
            JsonSerializer.Serialize(state, SetupConfig.JsonWriteOptions));
    }

    internal static void Clear(SetupContext ctx)
    {
        try
        {
            File.Delete(GetPath(ctx));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Clearing an optional recovery marker is idempotent on a fresh install.
        }
    }

    internal static bool IsSupportedReloadMode(string? reloadMode) => reloadMode is
        "off" or "hot" or "restart" or "hybrid";
}
