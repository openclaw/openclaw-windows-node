using OpenClaw.TestSupport.Ui;

namespace OpenClaw.GatewayFixtureHost;

/// <summary>Separates observed behavior from the gates required to accept proof.</summary>
public sealed record ContractJourneyReport(
    string CaseId,
    string? RunId,
    bool? BehaviorPassed,
    bool? IsolationUnchanged,
    bool? CleanupSucceeded,
    string? Reason,
    IReadOnlyList<ProfileMetadataChange> Changes,
    bool IsolationAuditAttempted = false,
    bool SetupAttempted = false,
    bool BehaviorAttempted = false,
    IReadOnlyList<ContractJourneyFailure>? Failures = null,
    IReadOnlyList<ContractJourneyCheckpoint>? Checkpoints = null)
{
    public string Behavior => BehaviorPassed switch
    {
        true => "passed",
        false => "failed",
        null => BehaviorAttempted ? "not-verified" : "not-run"
    };
    public string Isolation => IsolationUnchanged switch
    {
        true => "unchanged",
        false => "changed",
        null => IsolationAuditAttempted ? "audit-failed" : "not-checked"
    };
    public string Cleanup => CleanupSucceeded switch
    {
        true => "passed",
        false => "failed",
        null => SetupAttempted ? "not-verified" : "not-required"
    };
    public string Qualification => BehaviorPassed == false ? "failed"
        : BehaviorPassed == true && IsolationUnchanged == true && CleanupSucceeded == true && Reason is null
            && (Failures is null || Failures.Count == 0)
            ? "passed" : "blocked";
}

public sealed record ContractJourneyFailure(string Category, string Detail)
{
    public static ContractJourneyFailure FromException(Exception exception, bool behaviorAttempted, bool assertion) =>
        new(exception switch
        {
            ContractAutomationException => "automation",
            ContractCaptureException => "capture",
            ContractAssertionException => "assertion",
            _ when assertion => "assertion",
            _ when !behaviorAttempted => "setup",
            _ => "unknown"
        }, exception.Message);
}

public sealed record ContractJourneyCheckpoint(string Name, DateTimeOffset ObservedAtUtc);
