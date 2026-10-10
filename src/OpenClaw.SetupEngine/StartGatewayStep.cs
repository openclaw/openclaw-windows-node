using System.Text.RegularExpressions;

namespace OpenClaw.SetupEngine;

public sealed class StartGatewayStep : SetupStep
{
    // Gateway 2026.9.6 permits 330s for service stop and 60s for replacement
    // health. Leave 30s for CLI preparation instead of killing it at 30s.
    internal static readonly TimeSpan RestartCommandTimeout = TimeSpan.FromSeconds(420);
    internal static readonly TimeSpan StartCommandTimeout = TimeSpan.FromSeconds(90);
    public override string Id => "start-gateway";
    public override string DisplayName => "Start gateway";
    public override RetryPolicy Retry => new(MaxAttempts: 3, InitialDelay: TimeSpan.FromSeconds(3));

    public override Task<StepResult> ExecuteAsync(SetupContext ctx, CancellationToken ct) =>
        StartOrRestartAndWaitForHealthAsync(ctx, restart: false, ct);

    internal static Task<StepResult> RestartAndWaitForHealthAsync(
        SetupContext ctx,
        CancellationToken ct) =>
        StartOrRestartAndWaitForHealthAsync(ctx, restart: true, ct);

    internal static Task<StepResult> RestartAndWaitForHealthAsync(
        SetupContext ctx,
        TimeSpan commandTimeout,
        CancellationToken ct) =>
        StartOrRestartAndWaitForHealthAsync(ctx, restart: true, ct, commandTimeout);

    private static async Task<StepResult> StartOrRestartAndWaitForHealthAsync(
        SetupContext ctx,
        bool restart,
        CancellationToken ct,
        TimeSpan? commandTimeout = null)
    {
        var distro = ctx.DistroName!;
        var pathCmd = ctx.WslPathPrefix;
        var action = restart ? "restart" : "start";

        if (!restart)
        {
            var portCheck = await ctx.Commands.RunInWslAsync(
                distro, $"ss -H -ltnp 'sport = :{ctx.Config.GatewayPort}'",
                TimeSpan.FromSeconds(10), ct: ct);

            if (portCheck.ExitCode != 0)
            {
                return StepResult.Fail(
                    $"Could not inspect gateway port {ctx.Config.GatewayPort} " +
                    $"(exit {portCheck.ExitCode}){FormatCommandDetail(portCheck)}.");
            }

            if (!string.IsNullOrWhiteSpace(portCheck.Stdout))
            {
                // Installation may already start the service. Process names (including node) are not ownership proof.
                var service = await ctx.Commands.RunInWslAsync(
                    distro, "systemctl --user show openclaw-gateway.service -p MainPID --value",
                    TimeSpan.FromSeconds(10), ct: ct);
                if (service.ExitCode != 0)
                {
                    return StepResult.Fail(
                        "Could not inspect openclaw-gateway.service MainPID " +
                        $"(exit {service.ExitCode}){FormatCommandDetail(service)}.");
                }

                if (!int.TryParse(service.Stdout.Trim(), out var pid) || pid <= 0)
                {
                    return StepResult.Fail(
                        "openclaw-gateway.service is not running with a valid MainPID" +
                        $"{FormatCommandDetail(service)}.");
                }

                var listeners = portCheck.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var listenerOwners = listeners
                    .Select(line => Regex.Matches(line, @"pid=(\d+),")
                        .Select(owner => owner.Groups[1].Value)
                        .ToArray())
                    .ToArray();
                if (listenerOwners.Any(owners => owners.Length == 0))
                {
                    return StepResult.Fail(
                        $"Could not determine which process owns gateway port {ctx.Config.GatewayPort}. " +
                        "Verify the WSL listener and openclaw-gateway.service status, then retry setup.");
                }

                var expectedPid = pid.ToString();
                var foreignPids = listenerOwners
                    .SelectMany(owners => owners)
                    .Where(ownerPid => !string.Equals(ownerPid, expectedPid, StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (foreignPids.Length > 0)
                {
                    var names = string.Join(", ", Regex.Matches(portCheck.Stdout, "\\(\\\"([^\\\"]+)\\\",")
                        .Select(owner => owner.Groups[1].Value).Distinct());
                    var ownerDetail = names.Length > 0 ? $" Owning process: {names}." : "";
                    var failure =
                        $"Port {ctx.Config.GatewayPort} is already in use by another process. " +
                        $"Listener PIDs outside openclaw-gateway.service: {string.Join(", ", foreignPids)}." +
                        ownerDetail;
                    ctx.Logger.Warn(failure);
                    return StepResult.Fail($"{failure} Either stop the conflicting process or change GatewayPort in the setup config.");
                }

                ctx.Logger.Info($"Port {ctx.Config.GatewayPort} is owned by openclaw-gateway.service (PID {pid}). Post-install port check succeeded.");
            }
        }

        var timeout = commandTimeout ?? (restart ? RestartCommandTimeout : StartCommandTimeout);
        ct.ThrowIfCancellationRequested();
        var start = await ctx.Commands.RunInWslAsync(
            distro, $"{pathCmd} && openclaw gateway {action}", timeout, ct: ct, inputViaStdin: true);

        if (start.TimedOut)
            return CommandFailure(action, start, timeout);

        if (start.ExitCode != 0)
        {
            // Check if systemd start-limit-hit
            if (start.Stderr.Contains("start-limit", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Logger.Warn("Start-limit hit, resetting and retrying");
                await ctx.Commands.RunInWslAsync(
                    distro,
                    "systemctl --user reset-failed openclaw-gateway.service",
                    TimeSpan.FromSeconds(10),
                    ct: ct);
                await Task.Delay(2000, ct);
                start = await ctx.Commands.RunInWslAsync(
                    distro,
                    $"{pathCmd} && openclaw gateway {action}",
                    timeout,
                    ct: ct,
                    inputViaStdin: true);
                if (start.TimedOut || start.ExitCode != 0)
                    return CommandFailure(action, start, timeout, afterReset: true);
            }
            else
            {
                return CommandFailure(action, start, timeout);
            }
        }

        return await WaitForHealthAsync(ctx, ct);
    }

    private static StepResult CommandFailure(
        string action, CommandResult result, TimeSpan timeout, bool afterReset = false)
    {
        // Recovery facts must not depend on the diagnostic display limit.
        var output = $"{result.Stdout}\n{result.Stderr}";
        var servingOwnerUnavailable = output.Contains(
            SetupWizardRunner.RestartServingOwnerDiagnostic, StringComparison.Ordinal);
        var restartIntentContention =
            GatewayWizardRestartRecoveryPolicy.IsRestartIntentCoordinatorContention(output);
        var message = $"Gateway {action} failed{(afterReset ? " after reset" : "")} " +
            $"(phase=CLI, exit {result.ExitCode}, timedOut={result.TimedOut}, " +
            $"elapsed={result.Elapsed.TotalSeconds:F1}s, limit={timeout.TotalSeconds:F1}s). " +
            $"stdout: {BoundedOutput(result.Stdout)}; stderr: {BoundedOutput(result.Stderr)}";
        // A killed CLI may already have signaled the service. Neither old output
        // nor a reachable port makes another restart safe, including pipeline retries.
        return result.TimedOut
            ? StepResult.Terminal($"{message}. Gateway state is unknown; no automatic retry.")
            : StepResult.Fail(message) with
            {
                GatewayRestartServingOwnerUnavailable = action == "restart" && servingOwnerUnavailable,
                GatewayRestartIntentContention = action == "restart" && restartIntentContention,
            };
    }

    private static string BoundedOutput(string output)
    {
        var sanitized = SetupLogger.Sanitize(output).ReplaceLineEndings(" ").Trim();
        return sanitized.Length == 0 ? "(empty)" :
            sanitized.Length <= 2048 ? sanitized : sanitized[..2048] + "...[truncated]";
    }

    private static string FormatCommandDetail(CommandResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.Stderr)
            ? result.Stdout.Trim()
            : result.Stderr.Trim();
        if (string.IsNullOrWhiteSpace(detail))
            return "";

        detail = detail.ReplaceLineEndings(" ");
        return $": {(detail.Length <= 240 ? detail : detail[..240] + "...")}";
    }

    internal static async Task<StepResult> WaitForHealthAsync(
        SetupContext ctx,
        CancellationToken ct)
    {
        var distro = ctx.DistroName!;
        ctx.Logger.Info("Waiting for gateway health endpoint...");
        var healthDeadline = DateTimeOffset.UtcNow.Add(TimeSpan.FromSeconds(ctx.Config.Gateway.HealthTimeoutSeconds));

        while (DateTimeOffset.UtcNow < healthDeadline)
        {
            ct.ThrowIfCancellationRequested();

            var status = await ctx.Commands.RunInWslAsync(
                distro, "curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:" + ctx.Config.GatewayPort + "/ --max-time 3",
                TimeSpan.FromSeconds(10), ct: ct);

            if (status.ExitCode == 0 && status.Stdout.Trim() is "200" or "401" or "403")
            {
                ctx.Logger.Info($"Gateway is accepting connections (HTTP {status.Stdout.Trim()})");
                return StepResult.Ok("Gateway running");
            }

            ctx.Logger.Debug($"Gateway not yet accepting connections (curl exit={status.ExitCode}, response={status.Stdout.Trim()})");

            await Task.Delay(2000, ct);
        }

        // Capture service status and journal for diagnostics
        var statusResult = await ctx.Commands.RunInWslAsync(
            distro,
            "systemctl --user status openclaw-gateway.service 2>&1 || true",
            TimeSpan.FromSeconds(10),
            ct: ct);

        var journal = await ctx.Commands.RunInWslAsync(
            distro,
            "journalctl --user-unit openclaw-gateway.service --no-pager -n 30 2>&1 || true",
            TimeSpan.FromSeconds(10),
            ct: ct);

        var redactedStatus = RedactTokens(statusResult.Stdout);
        var redactedJournal = RedactTokens(journal.Stdout);

        ctx.Logger.Error($"Gateway health timeout.\nService status:\n{redactedStatus}\nJournal:\n{redactedJournal}");

        return StepResult.Fail($"Gateway did not become healthy within {ctx.Config.Gateway.HealthTimeoutSeconds}s");
    }

    internal static string RedactTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return System.Text.RegularExpressions.Regex.Replace(
            text,
            @"[0-9a-fA-F]{32,}",
            m => m.Value[..8] + "…[REDACTED]");
    }

    public override async Task RollbackAsync(SetupContext ctx, CancellationToken ct)
    {
        var distro = ctx.DistroName!;

        // Check if distro is running before trying systemctl stop
        var list = await ctx.Commands.RunAsyncAllowingInheritedPipeHandleEscape(
            WslConstants.WslExePath,
            ["--list", "--quiet"],
            TimeSpan.FromSeconds(15),
            ct: ct);
        if (!WslInstallSupport.ContainsDistro(list.Stdout, distro))
        {
            ctx.Logger.Info("[Uninstall] Distro not registered — skipping gateway stop");
            return;
        }

        // Check distro state — only stop if Running
        var verbose = await ctx.Commands.RunAsyncAllowingInheritedPipeHandleEscape(
            WslConstants.WslExePath,
            ["--list", "--verbose"],
            TimeSpan.FromSeconds(15),
            ct: ct);
        var isRunning = WslInstallSupport.Normalize(verbose.Stdout)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Contains(distro, StringComparison.OrdinalIgnoreCase)
                      && line.Contains("Running", StringComparison.OrdinalIgnoreCase));

        if (!isRunning)
        {
            ctx.Logger.Info("[Uninstall] Distro not running — skipping systemctl stop");
            return;
        }

        // Stop gateway service with 5-second timeout (mirrors old uninstall step 3)
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await ctx.Commands.RunInWslAsync(
                distro, "bash -c 'systemctl --user stop openclaw-gateway 2>&1 || true'",
                TimeSpan.FromSeconds(10), ct: cts.Token);
            ctx.Logger.Info("[Uninstall] Stopped gateway service");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ctx.Logger.Warn("[Uninstall] systemctl stop timed out (5s); distro may be wedged — wsl --unregister will force-terminate");
        }
    }
}
