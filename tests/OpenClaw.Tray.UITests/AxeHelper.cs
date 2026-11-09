using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using Axe.Windows.Automation;
using Axe.Windows.Automation.Data;
using Axe.Windows.Core.Enums;
using Xunit;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Wraps Axe.Windows scanner to validate the live UI Automation tree for
/// accessibility violations. Modeled after the WinUI-Gallery AxeHelper pattern.
///
/// The scanner attaches to the separately launched OpenClaw process and inspects
/// only its visible Hub window's UIA subtree.
/// </summary>
public static class AxeHelper
{
    private static int? _processId;
    private static readonly object _lock = new();
    private const int MaxReportedErrors = 20;
    private const int MaxDescriptionLength = 256;
    private const int MaxContextLength = 128;
    private static readonly HashSet<string> ControlTypes = new(
        Axe.Windows.Core.Types.ControlType.GetInstance().GetKeyValuePairList()
            .Select(pair => pair.Value), StringComparer.Ordinal);

    /// <summary>
    /// Rules excluded globally due to known WinUI framework bugs that are not
    /// fixable in application code. These mirror the WinUI-Gallery exclusions.
    /// </summary>
    private static readonly HashSet<RuleId> GloballyExcludedRules =
    [
        // WinUI framework generates non-informative names for some built-in controls
        RuleId.NameIsInformative,
        // Framework includes control type in auto-generated accessible names
        RuleId.NameExcludesControlType,
        // Same as above, localized variant
        RuleId.NameExcludesLocalizedControlType,
        // WinUI framework repeats sibling names in some control patterns
        RuleId.SiblingUniqueAndFocusable,
    ];

    /// <summary>
    /// Initialize the Axe.Windows scanner for the target app process.
    /// Thread-safe; subsequent calls are no-ops.
    /// </summary>
    public static void Initialize(int processId)
    {
        if (_processId != null) return;

        lock (_lock)
        {
            _processId ??= processId;
        }
    }

    /// <summary>
    /// Scan the Hub window's UIA tree and assert no accessibility violations exist.
    /// </summary>
    /// <param name="pageRuleExclusions">
    /// Optional per-page rule exclusions for known issues specific to a page.
    /// </param>
    /// <param name="context">
    /// Optional context string (e.g. page name) included in failure messages.
    /// </param>
    public static void AssertNoAccessibilityErrors(
        IntPtr hubWindowHandle,
        IEnumerable<RuleId>? pageRuleExclusions = null,
        string? context = null,
        int? targetProcessId = null)
    {
        if ((targetProcessId ?? _processId) is not { } processId)
            throw new InvalidOperationException(
                "AxeHelper.Initialize() must be called before scanning.");

        var excludedRules = new HashSet<RuleId>(GloballyExcludedRules);
        if (pageRuleExclusions != null)
            excludedRules.UnionWith(pageRuleExclusions);

        var config = Config.Builder
            .ForProcessId(processId)
            .WithOutputFileFormat(OutputFileFormat.None)
            .Build();
        var scanOptions = new ScanOptions(context, hubWindowHandle);
        AssertScan(
            () => ScannerFactory.CreateScanner(config).Scan(scanOptions),
            processId,
            () => new UiaLayoutElement(AutomationElement.FromHandle(hubWindowHandle)),
            excludedRules,
            context);
    }

    internal static void AssertScan(
        Func<ScanOutput?> scan,
        int processId,
        Func<ILayoutElement> getHub,
        ISet<RuleId> excludedRules,
        string? context)
    {
        var errors = GetErrors(scan(), excludedRules);
        if (errors.Count > 0
            && errors.All(error => HasCompleteMetadata(error) && IsStaleLayoutSnapshot(
                getHub,
                processId,
                error!.Rule.ID,
                error.Element.Properties)))
        {
            errors = GetErrors(scan(), excludedRules);
        }

        AssertNoErrors(errors, context);
    }

    internal static List<ScanResult?> GetErrors(ScanOutput? scan, ISet<RuleId> excludedRules)
    {
        var outputs = scan?.WindowScanOutputs;
        if (outputs is not { Count: > 0 })
            Assert.Fail("Accessibility scan returned missing window metadata.");

        var errors = new List<ScanResult?>();
        foreach (var output in outputs)
        {
            if (output?.Errors is null)
                Assert.Fail("Accessibility scan returned missing error metadata.");

            var windowErrors = output.Errors.ToList();
            if (output.ErrorCount != windowErrors.Count)
                Assert.Fail("Accessibility scan returned inconsistent error metadata.");

            // Incomplete results must never disappear through an exclusion or a stale-layout retry.
            errors.AddRange(windowErrors.Where(error =>
                !HasCompleteMetadata(error) || !excludedRules.Contains(error.Rule.ID)));
        }

        return errors;
    }

    private static bool HasCompleteMetadata(ScanResult? error) =>
        error?.Rule is { } rule
        && Enum.IsDefined(rule.ID)
        && !string.IsNullOrWhiteSpace(rule.Description)
        && error.Element?.Properties is not null;

    internal static void AssertNoErrors(IReadOnlyList<ScanResult?> errors, string? context)
    {
        if (errors.Count == 0) return;

        var message = new StringBuilder("Accessibility scan");
        if (!string.IsNullOrEmpty(context))
            message.Append(" of '").Append(BoundedText(context, MaxContextLength)).Append('\'');
        message.Append(" found ").Append(errors.Count).Append(" violation(s):");
        for (var index = 0; index < Math.Min(errors.Count, MaxReportedErrors); index++)
        {
            var error = errors[index];
            var rule = error?.Rule;
            var ruleId = rule is not null && Enum.IsDefined(rule.ID) ? rule.ID.ToString() : "Unknown rule";
            var controlType = "Unknown";
            if (error?.Element?.Properties?.TryGetValue("ControlType", out var type) == true
                && type is not null && ControlTypes.Contains(type))
                controlType = type;

            // UIA names, IDs and arbitrary properties can contain user content. Only emit
            // canonical control types and an ordinal, without querying the live desktop.
            message.Append("\r\n  [").Append(ruleId).Append("] Element #").Append(index + 1)
                .Append(" (").Append(controlType).Append("): ")
                .Append(BoundedText(rule?.Description, MaxDescriptionLength));
            if (!HasCompleteMetadata(error))
                message.Append(" [incomplete scan metadata]");
        }
        if (errors.Count > MaxReportedErrors)
            message.Append("\r\n  Additional violations omitted.");
        Assert.Fail(message.ToString());
    }

    private static string BoundedText(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(missing)";
        var text = new string(value.Take(limit)
            .Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        return value.Length > limit ? text + "..." : text;
    }

    private static bool IsStaleLayoutSnapshot(
        Func<ILayoutElement> getHub,
        int processId,
        RuleId ruleId,
        IReadOnlyDictionary<string, string> properties)
    {
        if (ruleId is not (
                RuleId.ClickablePointOnScreen
                or RuleId.BoundingRectangleSizeReasonable)
            || !properties.TryGetValue("LogicalSize", out var logicalSize)
            || logicalSize is null
            || !logicalSize.Contains("h=0", StringComparison.Ordinal))
        {
            return false;
        }

        if (!properties.TryGetValue("AutomationId", out var automationId)
            || string.IsNullOrWhiteSpace(automationId))
            return false;
        try
        {
            var hub = getHub();
            if (hub.ProcessId != processId)
                return false;
            var element = hub.FindByAutomationId(automationId);
            if (element is null || element.ProcessId != processId)
                return false;

            var bounds = element.Bounds;
            return !element.IsOffscreen
                && bounds.Width > 0
                && bounds.Height > 0;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    // Keep UIA access separate from retry policy so tests never query the shared desktop.
    internal interface ILayoutElement
    {
        int ProcessId { get; }
        Rect Bounds { get; }
        bool IsOffscreen { get; }
        ILayoutElement? FindByAutomationId(string automationId);
    }

    private sealed class UiaLayoutElement(AutomationElement element) : ILayoutElement
    {
        public int ProcessId => element.Current.ProcessId;
        public Rect Bounds => element.Current.BoundingRectangle;
        public bool IsOffscreen => element.Current.IsOffscreen;

        public ILayoutElement? FindByAutomationId(string automationId)
        {
            var child = element.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
            return child is null ? null : new UiaLayoutElement(child);
        }
    }
}
