using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OpenClaw.Connection;
using OpenClaw.Shared;

namespace OpenClaw.SetupEngine;

public sealed class CleanupStaleDistroStep : SetupStep
{
    public override string Id => "cleanup-distro";
    public override string DisplayName => "Clean up stale WSL distro";
    public override bool CanRetry => false;

    public override bool CanSkip(SetupContext ctx) => !ctx.Config.CleanBeforeRun;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        if (!DistroInstallPathPolicy.TryGetManagedInstallPath(ctx.LocalDataDir, distro, out var wslDir, out var pathError))
            return StepResult.Terminal(pathError);

        var list = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--list", "--quiet"], TimeSpan.FromSeconds(15), ct: ct);
        if (list.ExitCode != 0)
            return StepResult.Ok("WSL not available or no distros - nothing to clean");

        var distros = WslInstallSupport.ParseQuietDistroList(list.Stdout);

        ctx.Logger.Debug($"Found WSL distros: [{string.Join(", ", distros)}]");

        if (!distros.Any(d => d.Equals(distro, StringComparison.OrdinalIgnoreCase)))
        {
            // Distro not registered, but disk directory may still exist from prior crash
            if (Directory.Exists(wslDir))
            {
                ctx.Logger.Info($"Removing orphaned WSL directory: {wslDir}");
                var delete = await DeleteDistroDirectoryWithRetries(ctx, distro, wslDir, ct);
                if (!delete.IsSuccess)
                    return delete;
            }
            ctx.Logger.Decision("No stale distro found", "skip cleanup");
            return StepResult.Ok("No stale distro to clean");
        }

        ctx.Logger.Decision($"Found existing distro '{distro}'", "terminating and unregistering");

        // Stop only the app-owned distro. Global WSL shutdown would disrupt unrelated distros.
        await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--terminate", distro], TimeSpan.FromSeconds(30), ct: ct);
        await Task.Delay(2000, ct); // Let port release

        var unregister = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--unregister", distro], TimeSpan.FromSeconds(60), ct: ct);
        if (unregister.ExitCode != 0)
        {
            ctx.Logger.Warn($"First unregister attempt failed (exit {unregister.ExitCode}); retrying targeted termination");
            await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--terminate", distro], TimeSpan.FromSeconds(30), ct: ct);
            await Task.Delay(3000, ct);
            unregister = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--unregister", distro], TimeSpan.FromSeconds(60), ct: ct);
        }

        if (unregister.ExitCode == 0)
        {
            // Also remove the on-disk WSL vhdx directory (--import fails if it exists)
            var delete = await DeleteDistroDirectoryWithRetries(ctx, distro, wslDir, ct);
            if (!delete.IsSuccess)
                return delete;

            // Wait for port to be released
            ctx.Logger.Info("Waiting for port release after distro termination...");
            await PreflightPortStep.WaitForPortFreeAsync(ctx.Config.GatewayPort, ctx.Config.Gateway.Bind, ctx.Logger, ct);
            return StepResult.Ok($"Unregistered stale distro '{distro}'");
        }

        return StepResult.Fail($"Failed to unregister distro: {unregister.Stderr}");
    }

    internal static async Task<StepResult> DeleteDistroDirectoryWithRetries(
        SetupContext ctx,
        string distroName,
        string wslDir,
        CancellationToken ct)
    {
        var deletePath = wslDir;
        Exception? lastError = null;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (!DistroInstallPathPolicy.TryValidateDeleteTarget(
                    ctx.LocalDataDir,
                    distroName,
                    wslDir,
                    out deletePath,
                    out var pathError))
            {
                return StepResult.Terminal(pathError);
            }

            try
            {
                if (File.Exists(deletePath))
                {
                    if (File.GetAttributes(deletePath).HasFlag(FileAttributes.ReparsePoint))
                        return StepResult.Fail($"App-owned WSL path '{deletePath}' is a reparse point; remove it manually and retry setup.");

                    ctx.Logger.Info($"Removing app-owned WSL file at install path: {deletePath}");
                    File.Delete(deletePath);
                }
                else if (Directory.Exists(deletePath))
                {
                    if (new DirectoryInfo(deletePath).Attributes.HasFlag(FileAttributes.ReparsePoint))
                        return StepResult.Fail($"App-owned WSL directory '{deletePath}' is a reparse point; remove it manually and retry setup.");

                    ctx.Logger.Info($"Removing app-owned WSL directory: {deletePath}");
                    Directory.Delete(deletePath, recursive: true);
                }

                var parent = Path.GetDirectoryName(deletePath);
                if (!string.IsNullOrWhiteSpace(parent) &&
                    Directory.Exists(parent) &&
                    !new DirectoryInfo(parent).Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                    !Directory.EnumerateFileSystemEntries(parent).Any())
                {
                    Directory.Delete(parent);
                    ctx.Logger.Info("Deleted empty wsl\\ parent directory");
                }

                return StepResult.Ok("WSL directory removed");
            }
            catch (DirectoryNotFoundException)
            {
                return StepResult.Ok("WSL directory already absent");
            }
            catch (IOException ex)
            {
                lastError = ex;
                if (attempt >= 3)
                    break;

                ctx.Logger.Warn($"VHD directory still locked, retrying in {(attempt + 1) * 2}s...");
                await Task.Delay(TimeSpan.FromSeconds((attempt + 1) * 2), ct);
            }
            catch (UnauthorizedAccessException ex)
            {
                lastError = ex;
                if (attempt >= 3)
                    break;

                ctx.Logger.Warn($"VHD directory access denied, retrying in {(attempt + 1) * 2}s...");
                await Task.Delay(TimeSpan.FromSeconds((attempt + 1) * 2), ct);
            }
        }

        return StepResult.Fail(
            $"Failed to remove app-owned WSL directory '{deletePath}'. Close any process using the OpenClaw WSL distro and retry setup."
            + (lastError is null ? "" : $" Last error: {lastError.Message}"));
    }
}
