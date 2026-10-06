using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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
        string? context = null)
    {
        if (_processId is not { } processId)
            throw new InvalidOperationException(
                "AxeHelper.Initialize() must be called before scanning.");

        var excludedRules = new HashSet<RuleId>(GloballyExcludedRules);
        if (pageRuleExclusions != null)
            excludedRules.UnionWith(pageRuleExclusions);

        var config = Config.Builder
            .ForProcessId(processId)
            .WithOutputFileFormat(OutputFileFormat.None)
            .Build();
        var scanner = ScannerFactory.CreateScanner(config);
        var scanOptions = new ScanOptions(context, hubWindowHandle);
        var errors = scanner.Scan(scanOptions).WindowScanOutputs
            .SelectMany(output => output.Errors)
            .Where(error => !excludedRules.Contains(error.Rule.ID))
            .ToList();
        if (errors.Count > 0
            && errors.All(error => IsStaleLayoutSnapshot(
                hubWindowHandle,
                error.Rule.ID,
                error.Element.Properties)))
        {
            scanner = ScannerFactory.CreateScanner(config);
            errors = scanner.Scan(scanOptions).WindowScanOutputs
                .SelectMany(output => output.Errors)
                .Where(error => !excludedRules.Contains(error.Rule.ID))
                .ToList();
        }

        if (errors.Count == 0) return;

        var errorMessages = errors.Select(error =>
        {
            var controlType = error.Element.Properties.TryGetValue("ControlType", out var ct)
                ? ct : "Unknown";
            var name = error.Element.Properties.TryGetValue("Name", out var n)
                ? n : "(no name)";
            var automationId = error.Element.Properties.TryGetValue("AutomationId", out var aid)
                ? aid : "(no id)";
            var axeProperties = string.Join(
                ", ",
                error.Element.Properties
                   .OrderBy(property => property.Key, StringComparer.Ordinal)
                   .Select(property => $"{property.Key}='{property.Value}'"));
            var liveState = DescribeLiveState(
                hubWindowHandle,
                automationId == "(no id)" ? null : automationId,
                name == "(no name)" ? null : name);
            return $"  [{error.Rule.ID}] Element '{controlType}' " +
                   $"(Name='{name}', AutomationId='{automationId}') " +
                   $"violated rule: {error.Rule.Description}\r\n" +
                   $"    Axe properties: {axeProperties}\r\n" +
                   $"    Live UIA state: {liveState}";
        });

        var header = string.IsNullOrEmpty(context)
            ? $"Accessibility scan found {errors.Count} violation(s):"
            : $"Accessibility scan of '{context}' found {errors.Count} violation(s):";

        Assert.Fail($"{header}\r\n{string.Join("\r\n", errorMessages)}");
    }

    private static bool IsStaleLayoutSnapshot(
        IntPtr hubWindowHandle,
        RuleId ruleId,
        IReadOnlyDictionary<string, string> properties)
    {
        if (ruleId is not (
                RuleId.ClickablePointOnScreen
                or RuleId.BoundingRectangleSizeReasonable)
            || !properties.TryGetValue("LogicalSize", out var logicalSize)
            || !logicalSize.Contains("h=0", StringComparison.Ordinal))
        {
            return false;
        }

        properties.TryGetValue("AutomationId", out var automationId);
        properties.TryGetValue("Name", out var name);
        try
        {
            var hub = AutomationElement.FromHandle(hubWindowHandle);
            var element = hub.FindFirst(
                TreeScope.Descendants,
                !string.IsNullOrWhiteSpace(automationId)
                    ? new PropertyCondition(
                        AutomationElement.AutomationIdProperty,
                        automationId)
                    : new PropertyCondition(
                        AutomationElement.NameProperty,
                        name ?? string.Empty));
            if (element is null)
                return false;

            var bounds = element.Current.BoundingRectangle;
            return !element.Current.IsOffscreen
                && bounds.Width > 0
                && bounds.Height > 0;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static string DescribeLiveState(
        IntPtr hubWindowHandle,
        string? automationId,
        string? name)
    {
        try
        {
            var hub = AutomationElement.FromHandle(hubWindowHandle);
            var condition = !string.IsNullOrWhiteSpace(automationId)
                ? new PropertyCondition(AutomationElement.AutomationIdProperty, automationId)
                : new PropertyCondition(AutomationElement.NameProperty, name ?? string.Empty);
            var element = hub.FindFirst(TreeScope.Descendants, condition);
            if (element is null)
                return "element no longer present";

            var clickablePoint = element.TryGetClickablePoint(out var point)
                ? $"{point.X:0.##},{point.Y:0.##}"
                : "none";
            var focused = AutomationElement.FocusedElement;
            var foregroundWindow = GetForegroundWindow();
            return $"bounds={element.Current.BoundingRectangle}; " +
                   $"clickablePoint={clickablePoint}; " +
                   $"isOffscreen={element.Current.IsOffscreen}; " +
                   $"isEnabled={element.Current.IsEnabled}; " +
                   $"isKeyboardFocusable={element.Current.IsKeyboardFocusable}; " +
                   $"hasKeyboardFocus={element.Current.HasKeyboardFocus}; " +
                   $"focusedName='{focused?.Current.Name ?? string.Empty}'; " +
                   $"hubBounds={hub.Current.BoundingRectangle}; " +
                   $"hubIsOffscreen={hub.Current.IsOffscreen}; " +
                   $"hubIsForeground={foregroundWindow == hubWindowHandle}; " +
                   $"hubHandle=0x{hubWindowHandle.ToInt64():X}; " +
                   $"foregroundHandle=0x{foregroundWindow.ToInt64():X}";
        }
        catch (ElementNotAvailableException)
        {
            return "element became unavailable";
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
