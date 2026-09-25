using System.Text.Json;
using OpenClaw.SetupEngine;

namespace OpenClaw.E2ETests.Setup;

public sealed class GatewayRestartFailureDiagnosticTests
{
    [Fact]
    public void SelectsOnlyGuardedPostWizardRestartFailure()
    {
        Assert.True(GatewayRestartFailureDiagnostic.IsTargetFailure(
            "run-wizard", StepResult.Fail("Gateway restart after wizard failed: GATEWAY_RESTART_PREPARATION_REFUSED")));
        Assert.False(GatewayRestartFailureDiagnostic.IsTargetFailure(
            "run-wizard", StepResult.Fail("Gateway wizard failed: secret")));
        Assert.False(GatewayRestartFailureDiagnostic.IsTargetFailure(
            "start-gateway", StepResult.Fail("Gateway restart after wizard failed: unexpected")));
    }

    [Theory]
    [InlineData("StateDatabaseCoordinatorContentionError: another OpenClaw process owns state-lifecycle", "coordinator_contention")]
    [InlineData("Cannot record restart intent for the serving Gateway", "restart_intent_refused")]
    [InlineData("Cannot verify a live serving Gateway owner", "serving_owner_unverified")]
    [InlineData("unrelated sensitive details", "other_restart_failure")]
    public void ClassifiesOnlyKnownRefusals(string message, string expected)
    {
        Assert.Equal(expected, GatewayRestartFailureDiagnostic.RefusalCategory(message));
    }

    [Fact]
    public void ParsesOnlyAllowlistedCoarseFacts()
    {
        var raw = """
            probe=ok
            scope=user
            unit_equal=true
            active=active
            sub=running
            pid_present=true
            pid_live=true
            pid_equal=false
            process_start_available=true
            service_start_available=true
            restart_count=2
            service_result=exit-code
            exit_code=1
            exit_status=78
            """;
        var snapshot = GatewayRestartFailureDiagnostic.Parse(Result(raw));
        Assert.Equal("ok", snapshot.Probe);
        Assert.Equal("user", snapshot.Scope);
        Assert.True(snapshot.UnitEqual);
        Assert.True(snapshot.PidLive);
        Assert.False(snapshot.PidEqual);
        Assert.Equal(2, snapshot.RestartCount);
        Assert.Equal("exit-code", snapshot.ServiceResult);
        Assert.Equal(1, snapshot.ExecMainCode);
        Assert.Equal(78, snapshot.ExecMainStatus);
        Assert.DoesNotContain("home", JsonSerializer.Serialize(snapshot));
    }

    [Theory]
    [InlineData("service_result=private-path")]
    [InlineData("exit_code=4")]
    [InlineData("exit_status=256")]
    public void RejectsOutOfRangeServiceExitFacts(string replacement)
    {
        const string raw = """
            probe=ok
            scope=user
            unit_equal=true
            active=activating
            sub=auto-restart
            pid_present=false
            pid_live=false
            pid_equal=unknown
            process_start_available=false
            service_start_available=true
            restart_count=0
            service_result=unknown
            exit_code=unknown
            exit_status=unknown
            """;
        var fieldName = replacement.Split('=')[0];
        var output = raw.Replace($"{fieldName}=unknown", replacement, StringComparison.Ordinal);

        var snapshot = GatewayRestartFailureDiagnostic.Parse(Result(output));

        Assert.Equal("invalid_output", snapshot.Probe);
        Assert.DoesNotContain(replacement, JsonSerializer.Serialize(snapshot));
    }

    [Fact]
    public void RejectsUnexpectedOrUnvalidatedOutputWithoutPersistingIt()
    {
        var snapshot = GatewayRestartFailureDiagnostic.Parse(
            Result("probe=ok\nscope=user\nsensitive_path=/home/user/.openclaw/secret\n"));
        Assert.Equal("invalid_output", snapshot.Probe);
        Assert.DoesNotContain("sensitive_path", JsonSerializer.Serialize(snapshot));
        Assert.Equal("timeout", GatewayRestartFailureDiagnostic.Parse(Result("", timedOut: true)).Probe);
        Assert.Equal("unavailable", GatewayRestartFailureDiagnostic.Parse(Result("", exitCode: 1)).Probe);
    }

    [Fact]
    public void ProbeRunsAsUserThroughStdinAndNeverPrintsRawStatus()
    {
        var script = GatewayRestartFailureDiagnostic.ProbeScript;
        Assert.Contains("systemctl --user show", script);
        Assert.Contains("-p ExecMainStatus", script);
        Assert.Contains("read -r key value", script);
        Assert.DoesNotContain("printf '%s\\n' \"$properties\"", script);
        Assert.DoesNotContain("openclaw gateway restart", script);
        Assert.DoesNotContain("ps ", script);
    }

    private static CommandResult Result(string stdout, int exitCode = 0, bool timedOut = false) =>
        new(exitCode, stdout, "", TimeSpan.Zero, timedOut);
}
