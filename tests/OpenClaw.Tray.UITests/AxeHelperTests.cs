// Exercises Axe failure reporting with scan data only; no scanner, app or UI fixture is started.
using Axe.Windows.Automation;
using Axe.Windows.Automation.Data;
using Axe.Windows.Core.Enums;
using Axe.Windows.Rules;
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
    [InlineData(true)]
    [InlineData(false)]
    public void ScopeExclusionCannotInspectOrHideIncompleteResults(bool incompleteFirst)
    {
        var incomplete = new ScanResult();
        var stale = LayoutError();
        var scans = new Queue<ScanOutput?>(
            [incompleteFirst ? Scan(incomplete, stale) : Scan(stale, incomplete)]);

        var failure = Assert.Throws<FailException>(() =>
            AxeHelper.AssertScan(
                () => scans.Dequeue(),
                TargetProcessId,
                () => throw new InvalidOperationException("Incomplete results must not retry."),
                new HashSet<RuleId>(),
                null,
                scopeExclusion: error =>
                {
                    // The incomplete result must never reach an exclusion callback.
                    Assert.Same(stale, error);
                    return true;
                }));

        Assert.Contains("1 violation(s)", failure.Message);
        Assert.Contains("incomplete scan metadata", failure.Message);
        Assert.Empty(scans);
    }

    [Fact]
    public void ScopeExclusionsApplyBeforeRetryAndAgainToTheSecondScan()
    {
        var scoped = Error();
        scoped.Rule.ID = RuleId.BoundingRectangleNotNull;
        var scans = new Queue<ScanOutput?>(
            [Scan(scoped, LayoutError()), Scan(scoped)]);

        AxeHelper.AssertScan(
            () => scans.Dequeue(),
            TargetProcessId,
            OwnedHub,
            new HashSet<RuleId>(),
            null,
            scopeExclusion: error => error.Rule.ID == RuleId.BoundingRectangleNotNull);

        // Without filtering the first scan, the non-layout result prevents retry.
        // Without filtering the second scan, the persistent scoped result fails.
        Assert.Empty(scans);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("count")]
    [InlineData("incompleteExcludedRule")]
    public void ScopeExclusionsCannotHideMalformedSecondScan(string malformed)
    {
        var scoped = Error();
        scoped.Rule.ID = RuleId.BoundingRectangleNotNull;
        var incomplete = new ScanResult();
        SetMetadata(incomplete, nameof(ScanResult.Rule), new RuleInfo
        {
            ID = RuleId.NameIsInformative,
            Description = "Names must be informative.",
        });
        var second = malformed switch
        {
            "null" => null,
            // This complete result would be scope-excluded, but the original
            // count mismatch must fail before the callback sees it.
            "count" => new ScanOutput([Window([scoped], 0)]),
            _ => Scan(incomplete),
        };
        var scans = new Queue<ScanOutput?>([Scan(LayoutError()), second]);

        var failure = Assert.Throws<FailException>(() =>
            AxeHelper.AssertScan(
                () => scans.Dequeue(),
                TargetProcessId,
                OwnedHub,
                new HashSet<RuleId> { RuleId.NameIsInformative },
                null,
                scopeExclusion: error =>
                {
                    // Neither malformed window metadata nor an incomplete result
                    // may be converted into a clean scan by scope filtering.
                    Assert.NotNull(error.Element);
                    Assert.NotNull(error.Element.Properties);
                    return error.Rule.ID != RuleId.ClickablePointOnScreen;
                }));

        Assert.Contains("metadata", failure.Message);
        Assert.Empty(scans);
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
