// Exercises Axe failure reporting with scan data only; no scanner, app or UI fixture is started.
using Axe.Windows.Automation;
using Axe.Windows.Automation.Data;
using Axe.Windows.Core.Enums;
using Axe.Windows.Rules;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using Xunit.Sdk;

namespace OpenClaw.Tray.UITests;

/// <summary>Protects fail-closed, content-free accessibility diagnostics. Run with the AxeHelperTests filter.</summary>
public sealed class AxeHelperTests
{
    private const int TargetProcessId = 123;
    private const int OtherProcessId = 456;

    [Fact]
    public void ViolationReportsRuleAndCanonicalControlTypeWithoutPrivateProperties()
    {
        var error = Error(new Dictionary<string, string>
        {
            ["ControlType"] = "Button(50000)",
            ["Name"] = "private conversation",
            ["AutomationId"] = "private dynamic id",
            ["Value"] = "private password",
            ["HelpText"] = "private account",
            ["private property key"] = "private property value",
        });
        var failure = Assert.Throws<FailException>(() => AssertScan(Scan(error)));

        Assert.Contains("[ClickablePointOnScreen]", failure.Message);
        Assert.Contains("Element #1 (Button(50000))", failure.Message);
        Assert.Contains("Clickable point must be on screen.", failure.Message);
        Assert.DoesNotContain("private", failure.Message);
    }

    [Theory]
    [InlineData("result")]
    [InlineData("rule")]
    [InlineData("description")]
    [InlineData("ruleId")]
    [InlineData("element")]
    [InlineData("properties")]
    public void IncompleteViolationCannotBeExcludedOrPass(string missing)
    {
        var error = Error();
        switch (missing)
        {
            case "result": error = null; break;
            case "rule": SetMetadata(error, nameof(ScanResult.Rule), null); break;
            case "description": error.Rule.Description = null; break;
            case "ruleId": error.Rule.ID = (RuleId)int.MaxValue; break;
            case "element": SetMetadata(error, nameof(ScanResult.Element), null); break;
            case "properties": SetMetadata(error.Element, nameof(ElementInfo.Properties), null); break;
        }
        var failure = Assert.Throws<FailException>(() => AssertScan(
            Scan(error!), new HashSet<RuleId> { RuleId.ClickablePointOnScreen, (RuleId)int.MaxValue }));
        Assert.Contains("1 violation(s)", failure.Message);
        Assert.Contains("incomplete scan metadata", failure.Message);
        if (missing is "element" or "properties")
            Assert.Contains("[ClickablePointOnScreen]", failure.Message);
    }

    [Theory]
    [InlineData("scan")]
    [InlineData("emptyWindows")]
    [InlineData("window")]
    [InlineData("errors")]
    [InlineData("count")]
    public void MalformedScanCannotPass(string missing)
    {
        var scan = missing switch
        {
            "scan" => null,
            "emptyWindows" => new ScanOutput([]),
            "window" => new ScanOutput([null]),
            "errors" => new ScanOutput([new WindowScanOutput()]),
            _ => new ScanOutput([Window([], 1)]),
        };
        var failure = Assert.Throws<FailException>(() => AssertScan(scan));
        Assert.Contains("metadata", failure.Message);
    }

    [Fact]
    public void ValidEmptyAndExcludedScansStillPassButOtherViolationsFail()
    {
        AssertScan(Scan());
        AssertScan(Scan(Error()), new HashSet<RuleId> { RuleId.ClickablePointOnScreen });
        Assert.Throws<FailException>(() => AssertScan(Scan(Error()),
            new HashSet<RuleId> { RuleId.NameIsInformative }));
    }

    [Fact]
    public void DiagnosticsBoundRecordsAndFieldsAndRejectNonCanonicalControlTypes()
    {
        var error = Error(new Dictionary<string, string>
        {
            ["ControlType"] = "private control type",
        });
        error.Rule.Description = "rule\r\n" + new string('d', 10_000);
        var errors = Enumerable.Repeat(error, 1000).ToArray();
        var failure = Assert.Throws<FailException>(() =>
            AxeHelper.AssertNoErrors(errors, new string('c', 10_000)));

        Assert.InRange(failure.Message.Length, 1, 9000);
        Assert.Contains("1000 violation(s)", failure.Message);
        Assert.Contains("Element #20 (Unknown)", failure.Message);
        Assert.DoesNotContain("Element #21", failure.Message);
        Assert.Contains("Additional violations omitted.", failure.Message);
        Assert.Contains("rule  ", failure.Message);
        Assert.DoesNotContain("private", failure.Message);
        Assert.DoesNotContain(new string('d', 257), failure.Message);
        Assert.DoesNotContain(new string('c', 129), failure.Message);
    }

    [Fact]
    public void DiagnosticsDoNotReadGlobalFocusOrNames()
    {
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "build.ps1")))
                directory = directory.Parent;
            root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        }
        var source = File.ReadAllText(Path.Combine(root, "tests", "OpenClaw.Tray.UITests", "AxeHelper.cs"));
        Assert.DoesNotContain("FocusedElement", source);
        Assert.DoesNotContain("GetForegroundWindow", source);
        Assert.DoesNotContain(".Current.Name", source);
        Assert.DoesNotContain("AutomationElement.NameProperty", source);
    }

    [Theory]
    [InlineData(RuleId.ClickablePointOnScreen)]
    [InlineData(RuleId.BoundingRectangleSizeReasonable)]
    public void StaleOwnedLayoutRetriesOnceAndAcceptsCleanSecondScan(RuleId rule)
    {
        var error = LayoutError();
        error.Rule.ID = rule;
        var scans = new Queue<ScanOutput?>([Scan(error), Scan()]);

        AxeHelper.AssertScan(() => scans.Dequeue(), TargetProcessId, OwnedHub, new HashSet<RuleId>(), null);

        Assert.Empty(scans);
    }

    [Fact]
    public void PersistentViolationFailsAfterOneRetry()
    {
        var error = LayoutError();
        var scans = new Queue<ScanOutput?>([Scan(error), Scan(error)]);
        var failure = Assert.Throws<FailException>(() =>
            AxeHelper.AssertScan(() => scans.Dequeue(), TargetProcessId, OwnedHub, new HashSet<RuleId>(), null));

        Assert.Contains("[ClickablePointOnScreen]", failure.Message);
        Assert.Empty(scans);
    }

    [Fact]
    public void RetryUsesTheSameRuleExclusions()
    {
        var excluded = Error();
        excluded.Rule.ID = RuleId.NameIsInformative;
        var scans = new Queue<ScanOutput?>([Scan(LayoutError()), Scan(excluded)]);

        AxeHelper.AssertScan(() => scans.Dequeue(), TargetProcessId, OwnedHub,
            new HashSet<RuleId> { RuleId.NameIsInformative }, null);

        Assert.Empty(scans);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("count")]
    [InlineData("incompleteExcludedRule")]
    public void MalformedSecondScanCannotPass(string malformed)
    {
        var incomplete = new ScanResult();
        SetMetadata(incomplete, nameof(ScanResult.Rule), new RuleInfo
        {
            ID = RuleId.NameIsInformative,
            Description = "Names must be informative.",
        });
        var second = malformed switch
        {
            "null" => null,
            "count" => new ScanOutput([Window([], 1)]),
            _ => Scan(incomplete),
        };
        var scans = new Queue<ScanOutput?>([Scan(LayoutError()), second]);
        var failure = Assert.Throws<FailException>(() =>
            AxeHelper.AssertScan(() => scans.Dequeue(), TargetProcessId, OwnedHub,
                new HashSet<RuleId> { RuleId.NameIsInformative }, null));

        Assert.Contains("metadata", failure.Message);
        Assert.Empty(scans);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MixedIncompleteAndStaleResultsCannotRetry(bool incompleteFirst)
    {
        var incomplete = new ScanResult();
        var stale = LayoutError();
        var scans = new Queue<ScanOutput?>([incompleteFirst ? Scan(incomplete, stale) : Scan(stale, incomplete)]);
        var failure = Assert.Throws<FailException>(() =>
            AxeHelper.AssertScan(() => scans.Dequeue(), TargetProcessId, OwnedHub, new HashSet<RuleId>(), null));

        Assert.Contains("2 violation(s)", failure.Message);
        Assert.Contains("incomplete scan metadata", failure.Message);
        Assert.Empty(scans);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingAutomationIdFailsWithoutReadingTheHub(string? automationId)
    {
        var properties = new Dictionary<string, string> { ["LogicalSize"] = "w=100,h=0" };
        if (automationId is not null)
            properties["AutomationId"] = automationId;

        var failure = Assert.Throws<FailException>(() => AssertScan(Scan(Error(properties))));

        Assert.Contains("1 violation(s)", failure.Message);
    }

    [Theory]
    [InlineData(RuleId.NameIsInformative, "w=100,h=0")]
    [InlineData(RuleId.ClickablePointOnScreen, "w=100,h=20")]
    [InlineData(RuleId.ClickablePointOnScreen, null)]
    public void NonStaleMetadataFailsWithoutReadingTheHub(RuleId rule, string? logicalSize)
    {
        var error = Error(new Dictionary<string, string>
        {
            ["AutomationId"] = "Composer",
            ["LogicalSize"] = logicalSize!,
        });
        error.Rule.ID = rule;

        Assert.Throws<FailException>(() => AssertScan(Scan(error)));
    }

    [Fact]
    public void ForeignHubFailsBeforeSearchingItsDescendants()
    {
        AssertLayoutFailure(() => new LayoutElement(OtherProcessId));
    }

    [Fact]
    public void ForeignElementFailsBeforeReadingItsGeometry()
    {
        AssertLayoutFailure(() => new LayoutElement(TargetProcessId)
        {
            Find = _ => new LayoutElement(OtherProcessId)
            {
                ReadBounds = () => throw new InvalidOperationException("Foreign geometry must not be read."),
            },
        });
    }

    [Fact]
    public void MissingElementFailsWithoutRetry()
    {
        AssertLayoutFailure(() => new LayoutElement(TargetProcessId) { Find = _ => null });
    }

    [Theory]
    [InlineData(true, 100, 20)]
    [InlineData(false, 0, 20)]
    [InlineData(false, 100, 0)]
    public void OffscreenOrEmptyLiveLayoutFailsWithoutRetry(bool offscreen, double width, double height)
    {
        AssertLayoutFailure(() => new LayoutElement(TargetProcessId)
        {
            Find = _ => new LayoutElement(TargetProcessId)
            {
                IsOffscreen = offscreen,
                ReadBounds = () => new Rect(0, 0, width, height),
            },
        });
    }

    [Theory]
    [InlineData("hub")]
    [InlineData("lookup")]
    [InlineData("geometry")]
    public void DisappearingElementPreservesTheOriginalViolation(string stage)
    {
        AssertLayoutFailure(() => stage == "hub"
            ? throw new ElementNotAvailableException()
            : new LayoutElement(TargetProcessId)
            {
                Find = _ => stage == "lookup"
                    ? throw new ElementNotAvailableException()
                    : new LayoutElement(TargetProcessId)
                    {
                        ReadBounds = () => throw new ElementNotAvailableException(),
                    },
            });
    }

    [Fact]
    public void UnexpectedLayoutFailurePropagatesInsteadOfPassingOrRetrying()
    {
        var expected = new InvalidOperationException("Synthetic UIA failure.");
        var scans = new Queue<ScanOutput?>([Scan(LayoutError())]);
        var failure = Assert.Throws<InvalidOperationException>(() =>
            AxeHelper.AssertScan(() => scans.Dequeue(), TargetProcessId, () => throw expected,
                new HashSet<RuleId>(), null));

        Assert.Same(expected, failure);
        Assert.Empty(scans);
    }

    [Theory]
    [InlineData("[l=10,t=20,r=12,b=23]", 2, 3, 6)]
    [InlineData("[l=-10,t=-20,r=-8,b=-17]", 2, 3, 6)]
    [InlineData("[l=10,t=20,r=10,b=20]", 0, 0, 0)]
    public void CachedBoundsUseThePinnedProducerFormatWithoutEchoingProperties(
        string bounds, double width, double height, double area)
    {
        using var diagnostic = ObserveDiagnosticFailure(DiagnosticError(bounds));
        var row = diagnostic.RootElement.GetProperty("observations")[0];
        Assert.Equal(1, row.GetProperty("Scan").GetInt32());
        Assert.Equal(1, row.GetProperty("ErrorOrdinal").GetInt32());
        Assert.Equal("BoundingRectangleSizeReasonable", row.GetProperty("Rule").GetString());
        Assert.Equal("Text(50020)", row.GetProperty("ControlType").GetString());
        Assert.True(row.GetProperty("AutomationIdPresent").GetBoolean());
        Assert.Equal("SnapshotNotZeroHeight", row.GetProperty("RetryReason").GetString());
        var size = row.GetProperty("SnapshotBounds");
        Assert.True(size.GetProperty("Available").GetBoolean());
        Assert.Equal(width, size.GetProperty("Width").GetDouble());
        Assert.Equal(height, size.GetProperty("Height").GetDouble());
        Assert.Equal(area, size.GetProperty("Area").GetDouble());
        Assert.False(row.GetProperty("LiveBounds").GetProperty("Available").GetBoolean());
        Assert.DoesNotContain("private", diagnostic.RootElement.GetRawText());
        Assert.DoesNotContain(bounds, diagnostic.RootElement.GetRawText());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[l=0,t=0,w=2,h=3]")]
    [InlineData("private [l=0,t=0,r=2,b=3]")]
    [InlineData("[l=0,t=0,r=2,b=3] private")]
    [InlineData("[l=0,t=0,r=2,b=3,private=1]")]
    [InlineData("[l=0,t=0,r=-1,b=3]")]
    [InlineData("[l=0,t=0,r=2,b=-1]")]
    [InlineData("[l=0,t=0,r=NaN,b=3]")]
    [InlineData("[l=0,t=0,r=Infinity,b=3]")]
    [InlineData("[l=0,t=0,r=2.5,b=3]")]
    [InlineData("[l=0,t=0,r=2147483648,b=3]")]
    [InlineData("[l=0,t=0,r=2,b= 3]")]
    [InlineData("[l=0,t=0,r=2,b=3")]
    public void UnknownOrMalformedCachedBoundsRemainUnavailable(string? bounds)
    {
        using var diagnostic = ObserveDiagnosticFailure(DiagnosticError(bounds));
        var size = diagnostic.RootElement.GetProperty("observations")[0].GetProperty("SnapshotBounds");
        Assert.False(size.GetProperty("Available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, size.GetProperty("Width").ValueKind);
        Assert.Equal(JsonValueKind.Null, size.GetProperty("Height").ValueKind);
        Assert.Equal(JsonValueKind.Null, size.GetProperty("Area").ValueKind);
        Assert.DoesNotContain("private", diagnostic.RootElement.GetRawText());
    }

    [Fact]
    public void LayoutDiagnosticSchemaContainsOnlyApprovedOperationalFields()
    {
        using var diagnostic = ObserveDiagnosticFailure(DiagnosticError("[l=10,t=20,r=12,b=23]"));
        static string[] Names(JsonElement value) => value.EnumerateObject().Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "firstErrorCount", "observations", "observationsTruncated", "retryAttempted",
            "scanCount", "schema", "secondErrorCount", "source" }, Names(diagnostic.RootElement));
        var row = diagnostic.RootElement.GetProperty("observations")[0];
        Assert.Equal(new[] { "AutomationIdPresent", "ControlType", "ErrorOrdinal", "LiveBounds", "LiveIsOffscreen",
            "MetadataUnavailable", "RetryReason", "Rule", "Scan", "SnapshotBounds" }, Names(row));
        Assert.Equal(new[] { "Area", "Available", "Height", "Width" }, Names(row.GetProperty("SnapshotBounds")));
        Assert.Equal("axe-layout", diagnostic.RootElement.GetProperty("source").GetString());
        Assert.Equal(1, diagnostic.RootElement.GetProperty("schema").GetInt32());
    }

    [Fact]
    public void DiagnosticPropertyAndRecordBoundsNeverLeakContent()
    {
        using var oversized = ObserveDiagnosticFailure(DiagnosticError("[l=0,t=0,r=2,b=3]" + new string('x', 10_000)));
        Assert.False(oversized.RootElement.GetProperty("observations")[0]
            .GetProperty("SnapshotBounds").GetProperty("Available").GetBoolean());
        var error = Error(new Dictionary<string, string>
        {
            ["ControlType"] = "private custom type", ["AutomationId"] = "private id",
            ["Name"] = "private text", ["BoundingRectangle"] = "private bounds"
        });
        error.Rule.ID = (RuleId)int.MaxValue;
        using var unknown = ObserveDiagnosticFailure(error);
        var row = unknown.RootElement.GetProperty("observations")[0];
        Assert.Equal("Unknown", row.GetProperty("Rule").GetString());
        Assert.Equal("Unknown", row.GetProperty("ControlType").GetString());
        Assert.Equal("IncompleteMetadata", row.GetProperty("RetryReason").GetString());
        Assert.DoesNotContain("private", unknown.RootElement.GetRawText());
    }

    [Theory]
    [InlineData("incomplete", "IncompleteMetadata")]
    [InlineData("rule", "RuleNotEligible")]
    [InlineData("logical-size", "SnapshotSizeMissing")]
    [InlineData("nonzero", "SnapshotNotZeroHeight")]
    [InlineData("id", "AutomationIdMissing")]
    public void CachedRetryRefusalsDoNotAddLiveQueries(string missing, string reason)
    {
        var properties = new Dictionary<string, string> { ["LogicalSize"] = "[w=100,h=0]", ["AutomationId"] = "Composer" };
        var error = Error(properties);
        if (missing == "incomplete") SetMetadata(error, nameof(ScanResult.Rule), null);
        if (missing == "rule") error.Rule.ID = RuleId.NameIsInformative;
        if (missing == "logical-size") properties.Remove("LogicalSize");
        if (missing == "nonzero") properties["LogicalSize"] = "[w=100,h=20]";
        if (missing == "id") properties["AutomationId"] = " ";
        using var diagnostic = ObserveDiagnosticFailure(error);
        Assert.Equal(reason, diagnostic.RootElement.GetProperty("observations")[0].GetProperty("RetryReason").GetString());
        Assert.False(diagnostic.RootElement.GetProperty("retryAttempted").GetBoolean());
    }

    [Theory]
    [InlineData("hub", "ForeignHub")]
    [InlineData("missing", "ElementMissing")]
    [InlineData("foreign", "ForeignElement")]
    [InlineData("offscreen", "LiveOffscreen")]
    [InlineData("empty", "LiveEmpty")]
    [InlineData("unavailable", "ElementUnavailable")]
    public void ExistingOwnedProbeRefusalsAreReportedWithoutAnotherProbe(string stage, string reason)
    {
        var messages = new List<string>();
        var scans = new Queue<ScanOutput?>([Scan(LayoutError())]);
        AxeHelper.ILayoutElement GetHub() => stage switch
        {
            "hub" => new LayoutElement(OtherProcessId),
            "unavailable" => throw new ElementNotAvailableException(),
            _ => new LayoutElement(TargetProcessId)
            {
                Find = _ => stage == "missing" ? null : new LayoutElement(stage == "foreign" ? OtherProcessId : TargetProcessId)
                {
                    IsOffscreen = stage == "offscreen",
                    ReadBounds = () => stage == "foreign" ? throw new InvalidOperationException("Foreign geometry must not be read")
                        : new Rect(0, 0, stage == "empty" ? 0 : 100, 20)
                }
            }
        };
        Assert.Throws<FailException>(() => AxeHelper.AssertScan(() => scans.Dequeue(), TargetProcessId,
            GetHub, new HashSet<RuleId>(), null, messages.Add));
        Assert.Empty(scans);
        using var diagnostic = JsonDocument.Parse(Assert.Single(messages));
        Assert.Equal(reason, diagnostic.RootElement.GetProperty("observations")[0].GetProperty("RetryReason").GetString());
        Assert.False(diagnostic.RootElement.GetProperty("retryAttempted").GetBoolean());
    }

    [Fact]
    public void CachedDiagnosticReadFailureCannotReplaceTheOriginalAssertion()
    {
        var error = Error(new ThrowingBoundsProperties());
        error.Rule.ID = RuleId.NameIsInformative; // Original policy never probes this rule.
        using var diagnostic = ObserveDiagnosticFailure(error);
        var row = diagnostic.RootElement.GetProperty("observations")[0];
        Assert.True(row.GetProperty("MetadataUnavailable").GetBoolean());
        Assert.Equal("RuleNotEligible", row.GetProperty("RetryReason").GetString());
        Assert.False(row.GetProperty("SnapshotBounds").GetProperty("Available").GetBoolean());
        Assert.DoesNotContain("private", diagnostic.RootElement.GetRawText());
    }

    [Fact]
    public void ObservationsPreserveAllShortCircuitAndDoNotProbeLaterErrors()
    {
        var messages = new List<string>();
        var first = Error(new Dictionary<string, string> { ["LogicalSize"] = "[w=1,h=1]" });
        Assert.Throws<FailException>(() => AxeHelper.AssertScan(() => Scan(first, LayoutError()), TargetProcessId,
            () => throw new InvalidOperationException("Unexpected probe"), new HashSet<RuleId>(), null, messages.Add));
        using var diagnostic = JsonDocument.Parse(Assert.Single(messages));
        var rows = diagnostic.RootElement.GetProperty("observations");
        Assert.Equal("SnapshotNotZeroHeight", rows[0].GetProperty("RetryReason").GetString());
        Assert.Equal("NotEvaluated", rows[1].GetProperty("RetryReason").GetString());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("LiveIsOffscreen").ValueKind);
    }

    [Fact]
    public void LiveValuesAreReadOnceInOriginalOrderAndSecondScanIsNotReevaluated()
    {
        var reads = new List<string>();
        var child = new CountingLayoutElement("element", reads);
        var hub = new CountingLayoutElement("hub", reads) { Child = child };
        var messages = new List<string>();
        var scans = new Queue<ScanOutput?>([Scan(LayoutError()), Scan(Error())]);
        Assert.Throws<FailException>(() => AxeHelper.AssertScan(() => scans.Dequeue(), TargetProcessId,
            () => { reads.Add("getHub"); return hub; }, new HashSet<RuleId>(), null, messages.Add));
        Assert.Equal(new[] { "getHub", "hub.pid", "hub.find", "element.pid", "element.bounds", "element.offscreen" }, reads);
        Assert.Empty(scans);
        using var diagnostic = JsonDocument.Parse(Assert.Single(messages));
        Assert.Equal(2, diagnostic.RootElement.GetProperty("scanCount").GetInt32());
        Assert.True(diagnostic.RootElement.GetProperty("retryAttempted").GetBoolean());
        var rows = diagnostic.RootElement.GetProperty("observations");
        Assert.Equal("Eligible", rows[0].GetProperty("RetryReason").GetString());
        Assert.Equal(100, rows[0].GetProperty("LiveBounds").GetProperty("Width").GetDouble());
        Assert.Equal(20, rows[0].GetProperty("LiveBounds").GetProperty("Height").GetDouble());
        Assert.Equal(2000, rows[0].GetProperty("LiveBounds").GetProperty("Area").GetDouble());
        Assert.False(rows[0].GetProperty("LiveIsOffscreen").GetBoolean());
        Assert.Equal(2, rows[1].GetProperty("Scan").GetInt32());
        Assert.Equal("NotEvaluated", rows[1].GetProperty("RetryReason").GetString());
    }

    [Theory]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, true)]
    [InlineData(1.0, true)]
    public void DiagnosticNumericValidationCannotChangeOriginalLiveRetryCriterion(double width, bool retry)
    {
        var messages = new List<string>();
        var calls = 0;
        Action run = () => AxeHelper.AssertScan(() => ++calls == 1 ? Scan(LayoutError()) : Scan(), TargetProcessId,
            () => new LayoutElement(TargetProcessId)
            {
                Find = _ => new LayoutElement(TargetProcessId) { ReadBounds = () => new Rect(0, 0, width, 1) }
            }, new HashSet<RuleId>(), null, messages.Add);
        if (retry) run(); else Assert.Throws<FailException>(run);
        Assert.Equal(retry ? 2 : 1, calls);
        using var diagnostic = JsonDocument.Parse(Assert.Single(messages));
        var live = diagnostic.RootElement.GetProperty("observations")[0].GetProperty("LiveBounds");
        Assert.Equal(double.IsFinite(width), live.GetProperty("Available").GetBoolean());
        if (double.IsFinite(width)) Assert.Equal(1, live.GetProperty("Area").GetDouble());
        else Assert.Equal(JsonValueKind.Null, live.GetProperty("Area").ValueKind);
    }

    [Fact]
    public void MalformedDiagnosticBoundsCannotTightenTheExistingSnapshotSubstringCriterion()
    {
        var messages = new List<string>();
        var error = Error(new Dictionary<string, string>
        {
            ["LogicalSize"] = "private legacy h=0", ["AutomationId"] = "Composer",
            ["BoundingRectangle"] = "private invalid size"
        });
        var scans = new Queue<ScanOutput?>([Scan(error), Scan()]);
        AxeHelper.AssertScan(() => scans.Dequeue(), TargetProcessId, OwnedHub, new HashSet<RuleId>(), null, messages.Add);
        Assert.Empty(scans);
        using var diagnostic = JsonDocument.Parse(Assert.Single(messages));
        Assert.True(diagnostic.RootElement.GetProperty("retryAttempted").GetBoolean());
        Assert.False(diagnostic.RootElement.GetProperty("observations")[0].GetProperty("SnapshotBounds").GetProperty("Available").GetBoolean());
        Assert.DoesNotContain("private", diagnostic.RootElement.GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThrowingDiagnosticWriterPreservesTheSamePrimaryException(bool secondScan)
    {
        var primary = new InvalidOperationException("private primary");
        var calls = 0;
        var writes = 0;
        var observed = Assert.Throws<InvalidOperationException>(() => AxeHelper.AssertScan(
            () => ++calls == 1 ? Scan(LayoutError()) : throw primary, TargetProcessId,
            () => secondScan ? OwnedHub() : throw primary, new HashSet<RuleId>(), null,
            _ => { writes++; throw new IOException("private diagnostic sink"); }));
        Assert.Same(primary, observed);
        Assert.Equal(secondScan ? 2 : 1, calls);
        Assert.Equal(1, writes);
    }

    [Fact]
    public void DiagnosticWriterFailureCannotFailAnOtherwisePassingRetry()
    {
        var writes = 0;
        var scans = new Queue<ScanOutput?>([Scan(LayoutError()), Scan()]);
        AxeHelper.AssertScan(() => scans.Dequeue(), TargetProcessId, OwnedHub, new HashSet<RuleId>(), null,
            _ => { writes++; throw new IOException("private sink"); });
        Assert.Empty(scans);
        Assert.Equal(1, writes);
        AxeHelper.AssertScan(() => Scan(), TargetProcessId, OwnedHub, new HashSet<RuleId>(), null,
            _ => throw new InvalidOperationException("A clean first scan should not emit diagnostics"));
    }

    [Fact]
    public void BothScanObservationsAreCappedWithoutSkippingPolicyEvaluations()
    {
        var messages = new List<string>();
        var probes = 0;
        var scans = new Queue<ScanOutput?>([Scan(Enumerable.Repeat(LayoutError(), 25).ToArray()), Scan(Enumerable.Repeat(Error(), 30).ToArray())]);
        Assert.Throws<FailException>(() => AxeHelper.AssertScan(() => scans.Dequeue(), TargetProcessId,
            () => { probes++; return OwnedHub(); }, new HashSet<RuleId>(), null, messages.Add));
        Assert.Empty(scans);
        Assert.Equal(25, probes);
        var text = Assert.Single(messages);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(text), 1, 32 * 1024);
        using var diagnostic = JsonDocument.Parse(text);
        Assert.True(diagnostic.RootElement.GetProperty("observationsTruncated").GetBoolean());
        Assert.Equal(25, diagnostic.RootElement.GetProperty("firstErrorCount").GetInt32());
        Assert.Equal(30, diagnostic.RootElement.GetProperty("secondErrorCount").GetInt32());
        var rows = diagnostic.RootElement.GetProperty("observations").EnumerateArray().ToArray();
        Assert.Equal(40, rows.Length);
        Assert.Equal(20, rows.Count(row => row.GetProperty("Scan").GetInt32() == 1));
        Assert.Equal(20, rows.Count(row => row.GetProperty("Scan").GetInt32() == 2));
        Assert.All(rows, row => Assert.InRange(row.GetProperty("ErrorOrdinal").GetInt32(), 1, 20));
    }

    private static ScanResult DiagnosticError(string? bounds)
    {
        var error = Error(new Dictionary<string, string>
        {
            ["ControlType"] = "Text(50020)", ["LogicalSize"] = "[w=100,h=20]",
            ["BoundingRectangle"] = bounds!, ["AutomationId"] = "private dynamic id",
            ["Name"] = "private user content", ["private key"] = "private value"
        });
        error.Rule.ID = RuleId.BoundingRectangleSizeReasonable;
        error.Rule.Description = "The BoundingRectangle property must represent an area of at least 25 pixels.";
        return error;
    }

    private static JsonDocument ObserveDiagnosticFailure(ScanResult error)
    {
        var messages = new List<string>();
        var failure = Assert.Throws<FailException>(() => AxeHelper.AssertScan(() => Scan(error), TargetProcessId,
            () => throw new InvalidOperationException("No native lookup is allowed in this diagnostic case"),
            new HashSet<RuleId>(), null, messages.Add));
        Assert.DoesNotContain("private", failure.Message);
        return JsonDocument.Parse(Assert.Single(messages));
    }

    private sealed class ThrowingBoundsProperties : IReadOnlyDictionary<string, string>
    {
        public int Count => 0;
        public IEnumerable<string> Keys => throw new InvalidOperationException("No property enumeration");
        public IEnumerable<string> Values => throw new InvalidOperationException("No property enumeration");
        public string this[string key] => throw new InvalidOperationException("No property indexer");
        public bool ContainsKey(string key) => throw new InvalidOperationException("No property enumeration");
        public bool TryGetValue(string key, out string value)
        {
            if (key == "BoundingRectangle") throw new InvalidOperationException("private cached data failure");
            value = null!;
            return false;
        }
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => throw new InvalidOperationException("No property enumeration");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CountingLayoutElement(string role, List<string> reads) : AxeHelper.ILayoutElement
    {
        public AxeHelper.ILayoutElement? Child { get; init; }
        public int ProcessId { get { reads.Add(role + ".pid"); return TargetProcessId; } }
        public Rect Bounds { get { reads.Add(role + ".bounds"); return new Rect(0, 0, 100, 20); } }
        public bool IsOffscreen { get { reads.Add(role + ".offscreen"); return false; } }
        public AxeHelper.ILayoutElement? FindByAutomationId(string automationId)
        {
            reads.Add(role + ".find");
            return Child;
        }
    }

    private static void AssertLayoutFailure(Func<AxeHelper.ILayoutElement> getHub)
    {
        var scans = new Queue<ScanOutput?>([Scan(LayoutError())]);
        var failure = Assert.Throws<FailException>(() =>
            AxeHelper.AssertScan(() => scans.Dequeue(), TargetProcessId, getHub, new HashSet<RuleId>(), null));
        Assert.Contains("[ClickablePointOnScreen]", failure.Message);
        Assert.Empty(scans);
    }

    private static ScanResult LayoutError() => Error(new Dictionary<string, string>
    {
        ["LogicalSize"] = "w=100,h=0",
        ["AutomationId"] = "Composer",
    });

    private static AxeHelper.ILayoutElement OwnedHub() => new LayoutElement(TargetProcessId)
    {
        Find = id =>
        {
            Assert.Equal("Composer", id);
            return new LayoutElement(TargetProcessId);
        },
    };

    private sealed class LayoutElement(int processId) : AxeHelper.ILayoutElement
    {
        public int ProcessId => processId;
        public bool IsOffscreen { get; init; }
        public Rect Bounds => ReadBounds();
        public Func<Rect> ReadBounds { get; init; } = () => new Rect(0, 0, 100, 20);
        public Func<string, AxeHelper.ILayoutElement?> Find { get; init; } =
            _ => throw new InvalidOperationException("Unexpected descendant lookup.");
        public AxeHelper.ILayoutElement? FindByAutomationId(string automationId) => Find(automationId);
    }

    private static ScanResult Error(IReadOnlyDictionary<string, string>? properties = null)
    {
        var element = new ElementInfo();
        SetMetadata(element, nameof(ElementInfo.Properties), properties ?? new Dictionary<string, string>());
        var error = new ScanResult();
        SetMetadata(error, nameof(ScanResult.Rule), new RuleInfo
        {
            ID = RuleId.ClickablePointOnScreen,
            Description = "Clickable point must be on screen.",
        });
        SetMetadata(error, nameof(ScanResult.Element), element);
        return error;
    }

    private static ScanOutput Scan(params ScanResult[] errors) =>
        new([Window(errors, errors.Length)]);

    private static WindowScanOutput Window(ScanResult[] errors, int count)
    {
        var window = new WindowScanOutput();
        SetMetadata(window, nameof(WindowScanOutput.Errors), errors);
        SetMetadata(window, nameof(WindowScanOutput.ErrorCount), count);
        return window;
    }

    // Axe's DTO setters are internal. Populate real scan inputs without booting its desktop scanner.
    private static void SetMetadata<T>(T target, string property, object? value) where T : class =>
        typeof(T).GetProperty(property)!.SetValue(target, value);

    private static void AssertScan(ScanOutput? scan, ISet<RuleId>? exclusions = null) =>
        AxeHelper.AssertScan(() => scan, TargetProcessId,
            () => throw new InvalidOperationException("Unexpected live layout read."),
            exclusions ?? new HashSet<RuleId>(), null);
}
