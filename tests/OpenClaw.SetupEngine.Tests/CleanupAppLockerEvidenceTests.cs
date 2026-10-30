using System.Xml;

namespace OpenClaw.SetupEngine.Tests;

public sealed class CleanupAppLockerEvidenceTests
{
    private const string Sid = "S-1-5-21-1000";
    private const string Rule = "{12345678-1111-2222-3333-444444444444}";

    private static string Event(int id, long record, string channel = "Microsoft-Windows-AppLocker/EXE and DLL",
        string provider = "Microsoft-Windows-AppLocker", string actor = Sid, string rule = Rule) =>
        $"""
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System><Provider Name="{provider}"/><EventID>{id}</EventID><EventRecordID>{record}</EventRecordID><Channel>{channel}</Channel></System>
          <UserData><RuleAndFileData xmlns="http://schemas.microsoft.com/schemas/event/Microsoft.Windows/1.0.0">
            <FilePath>%osdrive%\proof\host.exe</FilePath><TargetUser>{actor}</TargetUser><RuleId>{rule}</RuleId>
          </RuleAndFileData></UserData>
        </Event>
        """;

    [Theory]
    [InlineData(8000, true)]
    [InlineData(8001, true)]
    [InlineData(8002, true)]
    [InlineData(8003, true)]
    [InlineData(8004, true)]
    [InlineData(8005, false)]
    public void SubscriptionQuery_SelectsActualProviderAndExePolicyEvents(int id, bool expected)
    {
        var evidence = new CleanupAppLockerEvidence(20);
        var document = new XmlDocument();
        // EventLog's restricted XPath treats event namespaces implicitly.
        document.LoadXml($"<Event><System><Provider Name='Microsoft-Windows-AppLocker'/><EventID>{id}</EventID><EventRecordID>21</EventRecordID></System></Event>");
        Assert.Equal(expected, document.SelectSingleNode(evidence.Query) is not null);
    }

    [Theory]
    [InlineData("Microsoft-Windows-AppLocker/MSI and Script", "Microsoft-Windows-AppLocker")]
    [InlineData("Microsoft-Windows-AppLocker/EXE and DLL", "Microsoft-Windows-CodeIntegrity")]
    public void WrongPublisherOrChannel_CannotQualifyPolicy(string channel, string provider)
    {
        var evidence = new CleanupAppLockerEvidence(20);
        Assert.False(evidence.Observe(Event(8001, 21, channel, provider)));
        Assert.False(evidence.PolicyApplied);
    }

    [Fact]
    public void ExistingOrPrePolicyExecution_CannotQualifyNewEnforcement()
    {
        var evidence = new CleanupAppLockerEvidence(20);
        Assert.False(evidence.Observe(Event(8001, 20)));
        Assert.Throws<InvalidOperationException>(() =>
            evidence.FindExecution(@"C:\proof\host.exe", 8004, Sid, "C:", Rule));
        evidence.Observe(Event(8004, 21));
        evidence.Observe(Event(8001, 22));
        Assert.Equal(string.Empty, evidence.FindExecution(@"C:\proof\host.exe", 8004, Sid, "C:", Rule));
        evidence.Observe(Event(8004, 23));
        Assert.NotEmpty(evidence.FindExecution(@"C:\proof\host.exe", 8004, Sid, "C:", Rule));
    }

    [Fact]
    public void AuditWrongActorAndWrongRule_AreNotEnforcingDenialEvidence()
    {
        var evidence = new CleanupAppLockerEvidence(0);
        evidence.Observe(Event(8001, 1));
        evidence.Observe(Event(8003, 2));
        evidence.Observe(Event(8004, 3, actor: "S-1-5-18"));
        evidence.Observe(Event(8004, 4, rule: "{99999999-1111-2222-3333-444444444444}"));
        Assert.Equal(string.Empty, evidence.FindExecution(@"C:\proof\host.exe", 8004, Sid, "C:", Rule));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            evidence.FindExecution(@"C:\proof\host.exe", 8003, Sid, "C:", Rule));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PolicyConversionFailure_StopsAcceptance(bool afterApplied)
    {
        var evidence = new CleanupAppLockerEvidence(0);
        if (afterApplied)
            evidence.Observe(Event(8001, 1));
        evidence.Observe(Event(8000, 2));
        Assert.Equal(afterApplied ? 1 : -1, evidence.AppliedRecordId);
        Assert.False(evidence.PolicyApplied);
        Assert.Throws<InvalidOperationException>(evidence.RequireHealthyPolicy);
        Assert.Throws<InvalidOperationException>(() =>
            evidence.FindExecution(@"C:\proof\host.exe", 8004, Sid, "C:", Rule));
    }

    [Fact]
    public void RuleIdentity_UsesTheActualGuidRatherThanBraceFormatting()
    {
        var evidence = new CleanupAppLockerEvidence(0);
        evidence.Observe(Event(8001, 1));
        evidence.Observe(Event(8002, 2, rule: "12345678-1111-2222-3333-444444444444"));
        Assert.NotEmpty(evidence.FindExecution(@"C:\proof\host.exe", 8002, Sid, "C:", Rule));
    }

    [Fact]
    public void MissingNotification_ReconcilesReadablePolicyAndExecutionRecords()
    {
        var evidence = new CleanupAppLockerEvidence(20);
        var result = evidence.ReconcileAndRead(
            () => new[] { Event(8001, 21), Event(8004, 22) },
            () => evidence.PolicyApplied
                ? evidence.FindExecution(@"C:\proof\host.exe", 8004, Sid, "C:", Rule)
                : string.Empty);
        Assert.NotEmpty(result);
        Assert.Equal(21, evidence.AppliedRecordId);
        Assert.False(evidence.Observe(Event(8004, 22)));
    }

    [Fact]
    public void ReconciliationQueryFailure_PropagatesInsteadOfBecomingMissingEvents()
    {
        var evidence = new CleanupAppLockerEvidence(20);
        var failure = new InvalidOperationException("query access failure");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            evidence.ReconcileAndRead(() => throw failure, () => string.Empty)));
    }

    [Fact]
    public void ReconciliationWithoutRecords_DoesNotInventAcceptance()
    {
        var evidence = new CleanupAppLockerEvidence(20);
        Assert.Equal(string.Empty, evidence.ReconcileAndRead(
            () => Array.Empty<string>(), () => evidence.PolicyApplied ? "applied" : string.Empty));
        Assert.Equal(-1, evidence.AppliedRecordId);
        Assert.Throws<ArgumentOutOfRangeException>(() => evidence.QueryAfter(19));
    }
}
