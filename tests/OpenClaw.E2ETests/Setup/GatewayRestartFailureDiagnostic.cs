using System.Text.Json;
using OpenClaw.SetupEngine;

namespace OpenClaw.E2ETests.Setup;

// This fixture-only probe never prints raw systemctl properties, paths, PIDs, or process arguments.
internal static class GatewayRestartFailureDiagnostic
{
    internal const string ProbeScript = """
        unit=openclaw-gateway.service
        if ! properties=$(systemctl --user show "$unit" -p Id -p ActiveState -p SubState -p MainPID -p ExecMainPID -p ExecMainStartTimestampMonotonic -p NRestarts -p Result -p ExecMainCode -p ExecMainStatus 2>/dev/null); then
          printf 'probe=unavailable\n'
          exit 0
        fi
        id= active= sub= pid= exec_pid= started= restarts= service_result= exit_code= exit_status=
        while IFS='=' read -r key value; do
          case "$key" in
            Id) id=$value ;;
            ActiveState) active=$value ;;
            SubState) sub=$value ;;
            MainPID) pid=$value ;;
            ExecMainPID) exec_pid=$value ;;
            ExecMainStartTimestampMonotonic) started=$value ;;
            NRestarts) restarts=$value ;;
            Result) service_result=$value ;;
            ExecMainCode) exit_code=$value ;;
            ExecMainStatus) exit_status=$value ;;
          esac
        done <<< "$properties"
        printf 'probe=ok\nscope=user\n'
        if [[ "$id" == "$unit" ]]; then printf 'unit_equal=true\n'; else printf 'unit_equal=false\n'; fi
        case "$active" in active|inactive|failed|activating|deactivating|reloading) printf 'active=%s\n' "$active" ;; *) printf 'active=unknown\n' ;; esac
        case "$sub" in running|exited|dead|failed|start-pre|start-post|auto-restart|stop-sigterm|stop-post) printf 'sub=%s\n' "$sub" ;; *) printf 'sub=unknown\n' ;; esac
        if [[ "$pid" =~ ^[1-9][0-9]*$ ]]; then
          printf 'pid_present=true\n'
          if kill -0 "$pid" 2>/dev/null; then printf 'pid_live=true\n'; else printf 'pid_live=false\n'; fi
          if [[ "$exec_pid" == "$pid" ]]; then printf 'pid_equal=true\n'; else printf 'pid_equal=false\n'; fi
          if [[ -r "/proc/$pid/stat" ]]; then
            read -r stat < "/proc/$pid/stat"
            read -r -a fields <<< "${stat##*) }"
            if [[ "${fields[19]:-}" =~ ^[1-9][0-9]*$ ]]; then printf 'process_start_available=true\n'; else printf 'process_start_available=false\n'; fi
          else
            printf 'process_start_available=false\n'
          fi
        else
          printf 'pid_present=false\npid_live=false\npid_equal=unknown\nprocess_start_available=false\n'
        fi
        if [[ "$started" =~ ^[1-9][0-9]*$ ]]; then printf 'service_start_available=true\n'; else printf 'service_start_available=false\n'; fi
        if [[ "$restarts" =~ ^[0-9]{1,6}$ ]]; then printf 'restart_count=%s\n' "$restarts"; else printf 'restart_count=unknown\n'; fi
        case "$service_result" in success|exit-code|signal|core-dump|timeout|watchdog|start-limit-hit|resources|protocol|oom-kill) printf 'service_result=%s\n' "$service_result" ;; *) printf 'service_result=unknown\n' ;; esac
        if [[ "$exit_code" =~ ^[0-3]$ ]]; then printf 'exit_code=%s\n' "$exit_code"; else printf 'exit_code=unknown\n'; fi
        if [[ "$exit_status" =~ ^(0|[1-9][0-9]{0,2})$ ]] && ((exit_status <= 255)); then printf 'exit_status=%s\n' "$exit_status"; else printf 'exit_status=unknown\n'; fi
        """;

    internal static string RefusalCategory(string? message)
    {
        if (message is null)
            return "other_restart_failure";
        if (message.Contains("StateDatabaseCoordinatorContentionError", StringComparison.Ordinal))
            return "coordinator_contention";
        if (message.Contains("Cannot record restart intent for the serving Gateway", StringComparison.Ordinal))
            return "restart_intent_refused";
        if (message.Contains("Cannot verify a live serving Gateway owner", StringComparison.Ordinal))
            return "serving_owner_unverified";
        return "other_restart_failure";
    }

    internal static bool IsTargetFailure(string stepId, StepResult result) =>
        stepId == "run-wizard" &&
        result.Message?.Contains("Gateway restart after wizard failed:", StringComparison.Ordinal) == true;

    internal static async Task CaptureAsync(
        SetupContext ctx, string stepId, StepResult result, string artifactPath)
    {
        if (!IsTargetFailure(stepId, result))
            return;

        var probe = await ctx.Commands.RunInWslAsync(
            ctx.DistroName!,
            ProbeScript.Replace("\r\n", "\n", StringComparison.Ordinal),
            TimeSpan.FromSeconds(8),
            ct: CancellationToken.None,
            inputViaStdin: true);

        var snapshot = Parse(probe);
        var artifact = new
        {
            phase = "before_setup_rollback",
            refusalCategory = RefusalCategory(result.Message),
            ownerPredicate = "not_exposed_by_gateway_cli",
            snapshot.Probe,
            snapshot.Scope,
            snapshot.UnitEqual,
            snapshot.Active,
            snapshot.Sub,
            snapshot.PidPresent,
            snapshot.PidLive,
            snapshot.PidEqual,
            snapshot.ProcessStartAvailable,
            snapshot.ServiceStartAvailable,
            snapshot.RestartCount,
            snapshot.ServiceResult,
            snapshot.ExecMainCode,
            snapshot.ExecMainStatus
        };
        await File.WriteAllTextAsync(artifactPath, JsonSerializer.Serialize(artifact));
        ctx.Logger.Info($"Pre-rollback gateway restart diagnostic: {JsonSerializer.Serialize(artifact)}");
    }

    internal sealed record Snapshot(
        string Probe,
        string? Scope = null,
        bool? UnitEqual = null,
        string? Active = null,
        string? Sub = null,
        bool? PidPresent = null,
        bool? PidLive = null,
        bool? PidEqual = null,
        bool? ProcessStartAvailable = null,
        bool? ServiceStartAvailable = null,
        int? RestartCount = null,
        string? ServiceResult = null,
        int? ExecMainCode = null,
        int? ExecMainStatus = null);

    internal static Snapshot Parse(CommandResult result)
    {
        if (result.TimedOut)
            return new("timeout");
        if (result.ExitCode != 0)
            return new("unavailable");

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('=', 2);
            if (parts.Length != 2 || !fields.TryAdd(parts[0], parts[1]))
                return new("invalid_output");
        }

        if (fields.Count == 1 && fields.GetValueOrDefault("probe") == "unavailable")
            return new("unavailable");
        if (fields.Count != 14 || fields.GetValueOrDefault("probe") != "ok" ||
            fields.GetValueOrDefault("scope") != "user" ||
            !TryBoolean(fields, "unit_equal", out var unitEqual) ||
            !TryBoolean(fields, "pid_present", out var pidPresent) ||
            !TryBoolean(fields, "pid_live", out var pidLive) ||
            !TryBoolean(fields, "process_start_available", out var processStart) ||
            !TryBoolean(fields, "service_start_available", out var serviceStart) ||
            !fields.TryGetValue("pid_equal", out var pidEqualText) ||
            pidEqualText is not ("true" or "false" or "unknown") ||
            !fields.TryGetValue("active", out var active) ||
            active is not ("active" or "inactive" or "failed" or "activating" or "deactivating" or "reloading" or "unknown") ||
            !fields.TryGetValue("sub", out var sub) ||
            sub is not ("running" or "exited" or "dead" or "failed" or "start-pre" or "start-post" or "auto-restart" or "stop-sigterm" or "stop-post" or "unknown") ||
            !fields.TryGetValue("restart_count", out var restartCountText) ||
            !(restartCountText == "unknown" ||
              int.TryParse(restartCountText, out var count) && count is >= 0 and <= 999999) ||
            !fields.TryGetValue("service_result", out var serviceResult) ||
            serviceResult is not ("success" or "exit-code" or "signal" or "core-dump" or
                "timeout" or "watchdog" or "start-limit-hit" or "resources" or
                "protocol" or "oom-kill" or "unknown") ||
            !fields.TryGetValue("exit_code", out var exitCodeText) ||
            !(exitCodeText == "unknown" ||
              int.TryParse(exitCodeText, out var exitCode) && exitCode is >= 0 and <= 3) ||
            !fields.TryGetValue("exit_status", out var exitStatusText) ||
            !(exitStatusText == "unknown" ||
              int.TryParse(exitStatusText, out var exitStatus) && exitStatus is >= 0 and <= 255))
            return new("invalid_output");

        return new Snapshot(
            "ok", "user", unitEqual, active, sub, pidPresent, pidLive,
            pidEqualText == "unknown" ? null : pidEqualText == "true",
            processStart, serviceStart,
            restartCountText == "unknown" ? null : int.Parse(restartCountText),
            serviceResult,
            exitCodeText == "unknown" ? null : int.Parse(exitCodeText),
            exitStatusText == "unknown" ? null : int.Parse(exitStatusText));
    }

    private static bool TryBoolean(Dictionary<string, string> fields, string name, out bool value)
    {
        value = false;
        if (!fields.TryGetValue(name, out var text) || text is not ("true" or "false"))
            return false;
        value = text == "true";
        return true;
    }
}
