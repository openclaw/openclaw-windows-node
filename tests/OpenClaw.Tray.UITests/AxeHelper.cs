using System;
using System.Collections.Generic;
using System.Linq;
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
    private static IScanner? _scanner;
    private static readonly object _lock = new();

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
        if (_scanner != null) return;

        lock (_lock)
        {
            if (_scanner != null) return;

            var config = Config.Builder
                .ForProcessId(processId)
                .WithOutputFileFormat(OutputFileFormat.None)
                .Build();
            _scanner = ScannerFactory.CreateScanner(config);
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
        string? context = null)
    {
        if (_scanner == null)
            throw new InvalidOperationException(
                "AxeHelper.Initialize() must be called before scanning.");

        var excludedRules = new HashSet<RuleId>(GloballyExcludedRules);
        if (pageRuleExclusions != null)
            excludedRules.UnionWith(pageRuleExclusions);

        var scanOutput = _scanner.Scan(new ScanOptions(context, hubWindowHandle));

        var errors = scanOutput.WindowScanOutputs
            .SelectMany(output => output.Errors)
            .Where(error => !excludedRules.Contains(error.Rule.ID))
            .ToList();

        if (errors.Count == 0) return;

        var errorMessages = errors.Select(error =>
        {
            var controlType = error.Element.Properties.TryGetValue("ControlType", out var ct)
                ? ct : "Unknown";
            var name = error.Element.Properties.TryGetValue("Name", out var n)
                ? n : "(no name)";
            var automationId = error.Element.Properties.TryGetValue("AutomationId", out var aid)
                ? aid : "(no id)";
            return $"  [{error.Rule.ID}] Element '{controlType}' " +
                   $"(Name='{name}', AutomationId='{automationId}') " +
                   $"violated rule: {error.Rule.Description}";
        });

        var header = string.IsNullOrEmpty(context)
            ? $"Accessibility scan found {errors.Count} violation(s):"
            : $"Accessibility scan of '{context}' found {errors.Count} violation(s):";

        Assert.Fail($"{header}\r\n{string.Join("\r\n", errorMessages)}");
    }
}
