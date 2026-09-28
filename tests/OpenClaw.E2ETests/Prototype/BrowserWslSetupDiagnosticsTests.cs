using System.Text.Json;

namespace OpenClaw.E2ETests.Setup;

public sealed class BrowserWslSetupDiagnosticsTests
{
    private static string Entry(string message, object data) => JsonSerializer.Serialize(new { msg = message, data }) + "\n";

    [Fact]
    public void RealSetupLogger_RedactedStepIdsKeepClosedFailureAndCommandFacts()
    {
        var file = Path.GetTempFileName();
        try
        {
            using (var logger = new OpenClaw.SetupEngine.SetupLogger(file))
            {
                logger.StepStarted("install-cli", "Install CLI");
                logger.CommandCompleted("private-executable", new(42, "", "ECONNREFUSED private-value", TimeSpan.Zero, false), TimeSpan.Zero);
                logger.StepCompleted("install-cli", OpenClaw.SetupEngine.StepResult.Ok("handled"), TimeSpan.Zero);
                logger.StepStarted("run-wizard", "Gateway wizard");
                logger.CommandCompleted("private-executable", new(1, "", "StateDatabaseCoordinatorContentionError private-value", TimeSpan.Zero, false), TimeSpan.FromMilliseconds(5010.5));
                logger.StepCompleted("run-wizard", OpenClaw.SetupEngine.StepResult.Fail("GatewayRestartPreparationError private-value"), TimeSpan.Zero);
            }
            // Exercise the real producer, whose general sanitizer intentionally redacts *_id.
            using var first = JsonDocument.Parse(File.ReadLines(file).First());
            Assert.Equal("[REDACTED]", first.RootElement.GetProperty("data").GetProperty("step_id").GetString());
            var value = BrowserWslSetupDiagnostics.Read(file);
            Assert.Equal("run-wizard", value.FailedStep);
            Assert.Equal(1, value.CommandExit);
            Assert.False(value.CommandTimedOut);
            Assert.Equal(5010.5, value.CommandElapsedMs);
            Assert.Equal(new[] { "gateway_restart_preparation", "state_coordinator_contention" }, value.ObservedCodes);
            Assert.DoesNotContain("private", JsonSerializer.Serialize(value));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void RedactedStepIds_DoNotJoinAnEarlierStepsCommand()
    {
        var log = Entry("step.started: install", new { step_id = "[REDACTED]" }) +
            Entry("cmd.done: handled", new { exit_code = 42, stderr = "ECONNREFUSED private-value" }) +
            Entry("step.completed: install-cli → Success", new { step_id = "[REDACTED]", outcome = "Success" }) +
            Entry("step.started: wizard", new { step_id = "[REDACTED]" }) +
            Entry("step.completed: run-wizard → Failed", new { step_id = "[REDACTED]", outcome = "Failed" });
        var value = BrowserWslSetupDiagnostics.Project(log);
        Assert.Equal("run-wizard", value.FailedStep);
        Assert.Null(value.CommandExit);
        Assert.Empty(value.ObservedCodes);
    }

    [Fact]
    public void RedactedIds_DoNotJoinLaterStepExceptions()
    {
        var log = Entry("step.completed: run-wizard → Failed", new { step_id = "[REDACTED]", outcome = "Failed" }) +
            Entry("step.started: rollback", new { step_id = "[REDACTED]" }) +
            Entry("step.exception: private", new { step_id = "[REDACTED]", exception = "ECONNREFUSED private-value" });
        var value = BrowserWslSetupDiagnostics.Project(log);
        Assert.Equal("run-wizard", value.FailedStep);
        Assert.Empty(value.ObservedCodes);
    }

    [Fact]
    public void UnknownCompletionMessages_NeverBecomeExportedSteps()
    {
        var log = Entry("step.completed: private-value → Failed", new { step_id = "[REDACTED]", outcome = "Failed" });
        var value = BrowserWslSetupDiagnostics.Project(log);
        Assert.Equal("other", value.FailedStep);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(value));
    }

    [Fact]
    public void FailedStep_ProjectsOnlyClosedStageCodesAndNumbers()
    {
        var log = Entry("step.started: private title", new { step_id = "run-wizard" }) +
            Entry("cmd.done: private command", new { exit_code = 1, timed_out = false, elapsed_ms = 5010.5, stderr = "StateDatabaseCoordinatorContentionError private-value" }) +
            Entry("step.completed: private title", new { step_id = "run-wizard", outcome = "Failed", message = "GatewayRestartPreparationError private-value" });
        var value = BrowserWslSetupDiagnostics.Project(log);
        Assert.Equal("observed", value.ReadState);
        Assert.Equal("run-wizard", value.FailedStep);
        Assert.Equal(1, value.CommandExit);
        Assert.False(value.CommandTimedOut);
        Assert.Equal(5010.5, value.CommandElapsedMs);
        Assert.Equal(new[] { "gateway_restart_preparation", "state_coordinator_contention" }, value.ObservedCodes);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(value));
    }

    [Fact]
    public void RollbackCommand_CannotReplaceThePrimaryFailure()
    {
        var log = Entry("step.started: wizard", new { step_id = "run-wizard" }) +
            Entry("cmd.done: original", new { exit_code = 1, timed_out = false, elapsed_ms = 5000 }) +
            Entry("step.completed: wizard", new { step_id = "run-wizard", outcome = "Failed" }) +
            Entry("cmd.done: rollback", new { exit_code = 42, timed_out = true, elapsed_ms = 10000, stderr = "ECONNREFUSED private-value" });
        var value = BrowserWslSetupDiagnostics.Project(log);
        Assert.Equal(1, value.CommandExit);
        Assert.False(value.CommandTimedOut);
        Assert.Equal(5000, value.CommandElapsedMs);
        Assert.Empty(value.ObservedCodes);
    }

    [Fact]
    public void FailedException_AddsOnlyAllowlistedCategories()
    {
        var log = Entry("step.completed: wizard", new { step_id = "run-wizard", outcome = "Failed" }) +
            Entry("step.exception: private", new { step_id = "run-wizard", exception = "Cannot record restart intent for the serving Gateway. private-value" });
        Assert.Equal(new[] { "restart_intent_refused" }, BrowserWslSetupDiagnostics.Project(log).ObservedCodes);
    }

    [Fact]
    public void UnknownProducerValues_AreNotExported()
    {
        var log = Entry("step.started: private title", new { step_id = "private-step" }) +
            Entry("step.completed: private title", new { step_id = "private-step", outcome = "Failed", message = "private-value" });
        var value = BrowserWslSetupDiagnostics.Project(log);
        Assert.Equal("other", value.FailedStep);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(value));
    }

    [Fact]
    public void AHandledEarlierCommand_IsNotAttributedToAnotherFailedStep()
    {
        var log = Entry("step.started: install", new { step_id = "install-cli" }) +
            Entry("cmd.done: handled", new { exit_code = 1, stderr = "ECONNREFUSED private-value" }) +
            Entry("step.completed: install", new { step_id = "install-cli", outcome = "Success" }) +
            Entry("step.started: wizard", new { step_id = "run-wizard" }) +
            Entry("step.completed: wizard", new { step_id = "run-wizard", outcome = "Failed" });
        var value = BrowserWslSetupDiagnostics.Project(log);
        Assert.Null(value.CommandExit);
        Assert.Empty(value.ObservedCodes);
    }

    [Fact]
    public void ARecoveredCommand_DoesNotBecomeTheReportedFailingCommand()
    {
        var log = Entry("step.started: wizard", new { step_id = "run-wizard" }) +
            Entry("cmd.done: handled", new { exit_code = 1, stderr = "ECONNREFUSED private-value" }) +
            Entry("cmd.done: recovered", new { exit_code = 0 }) +
            Entry("step.completed: wizard", new { step_id = "run-wizard", outcome = "Failed" });
        var value = BrowserWslSetupDiagnostics.Project(log);
        Assert.Equal(0, value.CommandExit);
        Assert.Empty(value.ObservedCodes);
    }

    [Fact]
    public void MalformedAndWronglyTypedRecords_DoNotLeakOrThrow()
    {
        var value = BrowserWslSetupDiagnostics.Project("private malformed\n" +
            Entry("cmd.done: private", new { exit_code = "private", timed_out = "private", elapsed_ms = "private" }));
        Assert.Equal("partial", value.ReadState);
        Assert.Null(value.CommandExit);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(value));
    }

    [Fact]
    public void InputAndRecordLimits_AreExplicit()
    {
        var tooLarge = BrowserWslSetupDiagnostics.Project(new string('x', BrowserWslSetupDiagnostics.MaxBytes + 1));
        Assert.Equal("bounded", tooLarge.ReadState);
        Assert.True(tooLarge.Truncated);
        var many = BrowserWslSetupDiagnostics.Project(string.Concat(Enumerable.Repeat("{}\n", 1025)));
        Assert.Equal("partial", many.ReadState);
        Assert.True(many.Truncated);
        Assert.Equal(1024, many.Records);
    }

    [Fact]
    public void RecordLimit_RetainsTheLatestFailure()
    {
        var log = string.Concat(Enumerable.Repeat("{}\n", 1025)) +
            Entry("step.started: wizard", new { step_id = "run-wizard" }) +
            Entry("cmd.done: failed", new { exit_code = 1 }) +
            Entry("step.completed: wizard", new { step_id = "run-wizard", outcome = "Failed" });
        var value = BrowserWslSetupDiagnostics.Project(log);
        Assert.True(value.Truncated);
        Assert.Equal(1024, value.Records);
        Assert.Equal("run-wizard", value.FailedStep);
        Assert.Equal(1, value.CommandExit);
    }

    [Fact]
    public void TailRead_SkipsThePartialPrefixAndRetainsClosedFailureFacts()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, new string('x', BrowserWslSetupDiagnostics.MaxBytes + 8) + "\n" +
                Entry("step.completed: private", new { step_id = "run-wizard", outcome = "Failed", message = "private-value" }));
            var value = BrowserWslSetupDiagnostics.Read(file);
            Assert.True(value.Truncated);
            Assert.Equal("run-wizard", value.FailedStep);
            Assert.DoesNotContain("private", JsonSerializer.Serialize(value));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void MissingFile_IsUnknownNotASetupSuccess()
    {
        var value = BrowserWslSetupDiagnostics.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.Equal("unavailable", value.ReadState);
        Assert.Null(value.FailedStep);
        Assert.Null(value.CommandExit);
    }
}
