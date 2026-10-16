using System.Text.Json;
using OpenClaw.GatewayFixtureHost;
using OpenClaw.TestSupport.Ui;

namespace OpenClaw.Tray.IntegrationTests;

public sealed class ContractJourneyReportTests
{
    [Theory]
    [InlineData(true, true, true, null, "passed", "passed")]
    [InlineData(true, false, true, "profile changed", "passed", "blocked")]
    [InlineData(true, null, true, "audit failed", "passed", "blocked")]
    [InlineData(true, true, false, "cleanup failed", "passed", "blocked")]
    [InlineData(true, true, true, "evidence failed", "passed", "blocked")]
    [InlineData(false, true, true, "assertion failed", "failed", "failed")]
    [InlineData(null, null, null, "previous isolation failure", "not-run", "blocked")]
    [InlineData(null, true, true, "startup failed", "not-run", "blocked")]
    public void QualificationCannotHideBehaviorOrGateFailures(bool? behavior, bool? isolation, bool? cleanup,
        string? reason, string expectedBehavior, string qualification)
    {
        var report = new ContractJourneyReport("case", "synthetic", behavior, isolation, cleanup, reason, []);
        Assert.Equal(expectedBehavior, report.Behavior);
        Assert.Equal(qualification, report.Qualification);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report));
        Assert.Equal(expectedBehavior, json.RootElement.GetProperty("Behavior").GetString());
        Assert.Equal(qualification, json.RootElement.GetProperty("Qualification").GetString());
    }

    [Fact]
    public void NotRunDoesNotPretendTheGuardsWereExecuted()
    {
        var report = new ContractJourneyReport("case", null, null, null, null, "previous case blocked", []);
        Assert.Equal("not-checked", report.Isolation);
        Assert.Equal("not-required", report.Cleanup);
        Assert.Null(report.RunId);
    }

    [Fact]
    public void FailedAuditIsDistinctFromObservedProfileChange()
    {
        var failed = new ContractJourneyReport("case", "synthetic", true, null, true, "audit failed", [],
            IsolationAuditAttempted: true);
        var changed = failed with { IsolationUnchanged = false };
        Assert.Equal("audit-failed", failed.Isolation);
        Assert.Equal("changed", changed.Isolation);
    }

    [Fact]
    public void FailedBeforeSnapshotDoesNotPretendIsolationWasNeverChecked()
    {
        var report = new ContractJourneyReport("case", null, null, null, null, "before snapshot failed", [],
            IsolationAuditAttempted: true);
        Assert.Equal("not-run", report.Behavior);
        Assert.Equal("audit-failed", report.Isolation);
        Assert.Equal("not-required", report.Cleanup);
        Assert.Equal("blocked", report.Qualification);
    }

    [Fact]
    public void FailedStartupDoesNotClaimItsPartialResourcesWereCleaned()
    {
        var report = new ContractJourneyReport("case", null, null, true, null, "startup failed", [],
            IsolationAuditAttempted: true, SetupAttempted: true);
        Assert.Equal("not-verified", report.Cleanup);
        Assert.Equal("blocked", report.Qualification);
    }

    [Theory]
    [InlineData("automation")]
    [InlineData("capture")]
    [InlineData("unknown")]
    public void InterruptedObservationDoesNotClaimTheProductFailed(string category)
    {
        var error = new InvalidOperationException("synthetic cause");
        Exception failure = category switch
        {
            "automation" => new ContractAutomationException("driver failed", error),
            "capture" => new ContractCaptureException("capture failed", error),
            _ => error
        };
        var detail = ContractJourneyFailure.FromException(failure, true, false);
        Assert.Equal(category, detail.Category);
        var report = new ContractJourneyReport("case", "run", null, true, true, failure.Message, [],
            BehaviorAttempted: true, Failures: [detail]);
        Assert.Equal("not-verified", report.Behavior);
        Assert.Equal("blocked", report.Qualification);
    }

    [Fact]
    public void ExplicitProductAssertionIsAProductFailure()
    {
        var detail = ContractJourneyFailure.FromException(new ContractAssertionException("preference changed"), true, false);
        Assert.Equal("assertion", detail.Category);
        var report = new ContractJourneyReport("case", "run", false, true, true, detail.Detail, [],
            BehaviorAttempted: true, Failures: [detail]);
        Assert.Equal("failed", report.Behavior);
        Assert.Equal("failed", report.Qualification);
    }

    [Fact]
    public void FailureCategoriesCannotBeHiddenBySuccessfulBooleans()
    {
        var report = new ContractJourneyReport("case", "run", true, true, true, null, [],
            Failures: [new("capture", "missing evidence")]);
        Assert.Equal("passed", report.Behavior);
        Assert.Equal("blocked", report.Qualification);
    }
}
