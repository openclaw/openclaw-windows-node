// Exercises Axe failure reporting with scan data only; no scanner, app or UI fixture is started.
using Axe.Windows.Automation;
using Axe.Windows.Automation.Data;
using Axe.Windows.Core.Enums;
using Axe.Windows.Rules;
using Xunit.Sdk;

namespace OpenClaw.Tray.UITests;

/// <summary>Protects fail-closed, content-free accessibility diagnostics. Run with the AxeHelperTests filter.</summary>
public sealed class AxeHelperTests
{
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
        AxeHelper.AssertNoErrors(AxeHelper.GetErrors(scan, exclusions ?? new HashSet<RuleId>()), null);
}
