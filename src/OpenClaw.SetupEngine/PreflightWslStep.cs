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

public sealed class PreflightWslStep : SetupStep
{
    public override string Id => "preflight-wsl";
    public override string DisplayName => "Verify WSL available";
    public override bool CanRetry => false;

    public override async Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct)
    {
        var versionResult = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--version"], TimeSpan.FromSeconds(5), ct: ct);
        if (versionResult.ExitCode != 0 && LooksUnavailable(versionResult))
        {
            var installResult = await InstallWslPlatformAsync(ctx, ct);
            if (!installResult.IsSuccess)
                return installResult;

            versionResult = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--version"], TimeSpan.FromSeconds(5), ct: ct);
        }

        if (versionResult.ExitCode != 0)
        {
            if (LooksTooOldForVersionCommand(versionResult))
                return StepResult.Terminal($"WSL is installed but too old for clean app-owned gateway setup. {WslInstallSupport.UpdateInstructions}");

            return StepResult.Terminal($"WSL is not available. {FirstUsefulLine(versionResult)}");
        }

        var versionOutput = NormalizeWslOutput($"{versionResult.Stdout}\n{versionResult.Stderr}");
        if (!WslInstallSupport.TryParseWslVersion(versionOutput, out var wslVersion))
            return StepResult.Terminal($"WSL version output did not include a parseable WSL version. {WslInstallSupport.UpdateInstructions}");

        if (!WslInstallSupport.SupportsDirectNamedInstall(wslVersion))
            return StepResult.Terminal($"WSL {wslVersion} cannot create a clean app-owned OpenClaw gateway distro. {WslInstallSupport.UpdateInstructions}");

        ctx.Logger.Info($"WSL version output: {NormalizeWslOutput(versionResult.Stdout).Trim()}");
        ctx.Logger.Info($"WSL direct named install is supported (version {wslVersion})");

        // wsl --version can succeed even when the WSL2 platform itself is
        // unusable (Virtual Machine Platform component disabled, hardware
        // virtualization off in firmware, Hyper-V missing, ...). Surface
        // that diagnostic now so the user gets an actionable message
        // before pipeline reaches the actual `wsl --install` step.
        var statusIssue = await DetectEnvironmentIssueAsync(ctx, ct);
        if (statusIssue != null)
            return StepResult.Terminal(statusIssue);

        return StepResult.Ok("WSL available");
    }

    internal static async Task<string?> DetectEnvironmentIssueAsync(SetupContext ctx, CancellationToken ct)
    {
        var status = await ctx.Commands.RunAsync(
            WslConstants.WslExePath,
            ["--status"],
            TimeSpan.FromSeconds(10),
            ct: ct);

        var combined = $"{status.Stdout}\n{status.Stderr}";
        if (WslInstallSupport.TryGetEnvironmentIssue(combined, out var message))
        {
            ctx.Logger.Warn($"WSL environment issue detected: {NormalizeWslOutput(combined).Trim()}");
            return message;
        }

        return null;
    }

    private static async Task<StepResult> InstallWslPlatformAsync(SetupContext ctx, CancellationToken ct)
    {
        ctx.Logger.Warn("WSL platform appears to be missing; launching elevated WSL platform install");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = WslConstants.WslExePath,
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WorkingDirectory = WslConstants.SafeWindowsWorkingDirectory
            };
            psi.ArgumentList.Add("--install");
            psi.ArgumentList.Add("--no-distribution");

            using var process = Process.Start(psi);
            if (process == null)
                return StepResult.Fail("Could not start elevated WSL platform installer.");

            await process.WaitForExitAsync(ct);

            if (process.ExitCode == 3010)
                return StepResult.Terminal("WSL platform install requires a restart. Reboot Windows, then run setup again.");

            if (process.ExitCode != 0)
                return StepResult.Fail($"WSL platform install failed with exit code {process.ExitCode}.");

            var probe = await ctx.Commands.RunAsync(WslConstants.WslExePath, ["--version"], TimeSpan.FromSeconds(5), ct: ct);
            if (probe.ExitCode != 0 || LooksUnavailable(probe))
                return StepResult.Terminal("WSL platform install completed, but Windows still reports WSL unavailable. Reboot Windows, then run setup again.");

            return StepResult.Ok("WSL platform installed");
        }
        catch (System.ComponentModel.Win32Exception ex) when ((uint)ex.NativeErrorCode == 1223)
        {
            return StepResult.Fail("WSL platform install was cancelled at the elevation prompt.");
        }
        catch (Exception ex)
        {
            return StepResult.Fail($"WSL platform install failed: {ex.Message}", ex);
        }
    }

    private static bool LooksUnavailable(CommandResult result)
    {
        var text = NormalizeWslOutput($"{result.Stdout}\n{result.Stderr}");
        return text.Contains("aka.ms/wslinstall", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Windows Subsystem for Linux has no installed distributions", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not recognized", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not installed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksTooOldForVersionCommand(CommandResult result)
    {
        var text = NormalizeWslOutput($"{result.Stdout}\n{result.Stderr}");
        return text.Contains("Invalid command line option", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unknown option", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWslOutput(string value)
        => WslInstallSupport.Normalize(value);

    private static string FirstUsefulLine(CommandResult result)
    {
        var text = NormalizeWslOutput($"{result.Stderr}\n{result.Stdout}");
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
            ?? "Run wsl --install from an elevated terminal and retry setup.";
    }
}
