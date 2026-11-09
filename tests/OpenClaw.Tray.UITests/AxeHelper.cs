using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
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
    private const int MaxSnapshotBoundsLength = 128;
    private const int MaxLayoutDiagnosticBytes = 32 * 1024;
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
        string? context,
        Action<string>? writeDiagnostics = null)
    {
        LayoutObservation[] first = [], second = [];
        int? firstErrorCount = null, secondErrorCount = null;
        var scanCount = 1;
        var retryAttempted = false;
        try
        {
            var errors = GetErrors(scan(), excludedRules);
            firstErrorCount = errors.Count;
            first = ObserveCachedLayout(1, errors);
            var ordinal = 0;
            if (errors.Count > 0 && errors.All(error =>
                {
                    var observation = ordinal < first.Length ? first[ordinal] : null;
                    ordinal++;
                    if (!HasCompleteMetadata(error))
                    {
                        if (observation is not null) observation.Reason = LayoutRetryReason.IncompleteMetadata;
                        return false;
                    }
                    return IsStaleLayoutSnapshot(getHub, processId, error!.Rule.ID,
                        error.Element.Properties, observation);
                }))
            {
                retryAttempted = true;
                scanCount = 2;
                errors = GetErrors(scan(), excludedRules);
                secondErrorCount = errors.Count;
                second = ObserveCachedLayout(2, errors);
            }

            AssertNoErrors(errors, context);
        }
        finally
        {
            WriteLayoutDiagnostics(first, second, scanCount, retryAttempted,
                firstErrorCount, secondErrorCount, writeDiagnostics);
        }
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
        IReadOnlyDictionary<string, string> properties,
        LayoutObservation? observation)
    {
        bool Finish(LayoutRetryReason reason, bool result = false)
        {
            if (observation is not null) observation.Reason = reason;
            return result;
        }

        // These are the original predicates, in the original short-circuit order.
        // Diagnostic parsing never participates in retry eligibility.
        if (ruleId is not (RuleId.ClickablePointOnScreen or RuleId.BoundingRectangleSizeReasonable))
            return Finish(LayoutRetryReason.RuleNotEligible);
        if (!properties.TryGetValue("LogicalSize", out var logicalSize) || logicalSize is null)
            return Finish(LayoutRetryReason.SnapshotSizeMissing);
        if (!logicalSize.Contains("h=0", StringComparison.Ordinal))
            return Finish(LayoutRetryReason.SnapshotNotZeroHeight);
        if (!properties.TryGetValue("AutomationId", out var automationId) || string.IsNullOrWhiteSpace(automationId))
            return Finish(LayoutRetryReason.AutomationIdMissing);
        try
        {
            if (observation is not null) observation.Reason = LayoutRetryReason.ProbeInterrupted;
            var hub = getHub();
            if (hub.ProcessId != processId)
                return Finish(LayoutRetryReason.ForeignHub);
            var element = hub.FindByAutomationId(automationId);
            if (element is null)
                return Finish(LayoutRetryReason.ElementMissing);
            if (element.ProcessId != processId)
                return Finish(LayoutRetryReason.ForeignElement);

            var bounds = element.Bounds;
            var offscreen = element.IsOffscreen;
            if (observation is not null)
            {
                // Only the values from the existing reads are cached. No diagnostic UIA read.
                observation.LiveBounds = LayoutSize.From(bounds.Width, bounds.Height);
                observation.LiveIsOffscreen = offscreen;
            }
            if (offscreen) return Finish(LayoutRetryReason.LiveOffscreen);
            var eligible = bounds.Width > 0 && bounds.Height > 0;
            return Finish(eligible ? LayoutRetryReason.Eligible : LayoutRetryReason.LiveEmpty, eligible);
        }
        catch (ElementNotAvailableException)
        {
            return Finish(LayoutRetryReason.ElementUnavailable);
        }
    }

    private enum LayoutRetryReason
    {
        NotEvaluated, IncompleteMetadata, RuleNotEligible, SnapshotSizeMissing, SnapshotNotZeroHeight,
        AutomationIdMissing, ProbeInterrupted, ForeignHub, ElementMissing, ForeignElement,
        LiveOffscreen, LiveEmpty, Eligible, ElementUnavailable
    }

    private readonly record struct LayoutSize(bool Available, double? Width, double? Height, double? Area)
    {
        internal static LayoutSize From(double width, double height) =>
            double.IsFinite(width) && width >= 0 && double.IsFinite(height) && height >= 0
                && double.IsFinite(width * height)
                ? new(true, width, height, width * height) : default;
    }

    // Primitive data only. Never retain ScanResult, property dictionaries, IDs, or native elements.
    private sealed class LayoutObservation(int scan, int ordinal)
    {
        public int Scan { get; } = scan;
        public int ErrorOrdinal { get; } = ordinal;
        public string Rule { get; set; } = "Unknown";
        public string ControlType { get; set; } = "Unknown";
        public bool MetadataUnavailable { get; set; }
        public LayoutSize SnapshotBounds { get; set; }
        public bool? AutomationIdPresent { get; set; }
        internal LayoutRetryReason Reason { get; set; }
        public string RetryReason => Reason.ToString();
        public LayoutSize LiveBounds { get; set; }
        public bool? LiveIsOffscreen { get; set; }
    }

    private static LayoutObservation[] ObserveCachedLayout(int scan, IReadOnlyList<ScanResult?> errors)
    {
        var observations = new LayoutObservation[Math.Min(errors.Count, MaxReportedErrors)];
        for (var index = 0; index < observations.Length; index++)
        {
            var observation = observations[index] = new LayoutObservation(scan, index + 1);
            try
            {
                var error = errors[index];
                if (error?.Rule is { } rule && Enum.IsDefined(rule.ID)) observation.Rule = rule.ID.ToString();
                if (error?.Element?.Properties is not { } properties)
                {
                    observation.MetadataUnavailable = true;
                    continue;
                }
                if (properties.TryGetValue("ControlType", out var type) && type is not null && ControlTypes.Contains(type))
                    observation.ControlType = type;
                observation.AutomationIdPresent = properties.TryGetValue("AutomationId", out var id)
                    && !string.IsNullOrWhiteSpace(id);
                if (properties.TryGetValue("BoundingRectangle", out var bounds))
                    observation.SnapshotBounds = ReadCachedBounds(bounds);
            }
            catch (Exception) { observation.MetadataUnavailable = true; }
        }
        return observations;
    }

    private static LayoutSize ReadCachedBounds(string? text)
    {
        // Axe.Windows 2.4.2 (1c8709bd): A11yProperty -> Rectangle -> DisplayStrings.
        // Canonical nonnegative-size form is [l={Int32},t={Int32},r={Int32},b={Int32}].
        // Alternate/localized/malformed forms stay unavailable, never echo or change policy.
        if (text is null || text.Length > MaxSnapshotBoundsLength
            || !text.StartsWith("[l=", StringComparison.Ordinal) || !text.EndsWith(']')) return default;
        var parts = text[1..^1].Split(',');
        if (parts.Length != 4 || !parts[1].StartsWith("t=", StringComparison.Ordinal)
            || !parts[2].StartsWith("r=", StringComparison.Ordinal) || !parts[3].StartsWith("b=", StringComparison.Ordinal)) return default;
        const NumberStyles style = NumberStyles.AllowLeadingSign;
        if (!int.TryParse(parts[0].AsSpan(2), style, CultureInfo.InvariantCulture, out var left)
            || !int.TryParse(parts[1].AsSpan(2), style, CultureInfo.InvariantCulture, out var top)
            || !int.TryParse(parts[2].AsSpan(2), style, CultureInfo.InvariantCulture, out var right)
            || !int.TryParse(parts[3].AsSpan(2), style, CultureInfo.InvariantCulture, out var bottom)) return default;
        return LayoutSize.From((double)right - left, (double)bottom - top);
    }

    private static void WriteLayoutDiagnostics(LayoutObservation[] first, LayoutObservation[] second,
        int scanCount, bool retryAttempted, int? firstErrorCount, int? secondErrorCount, Action<string>? write)
    {
        if (first.Length == 0 && second.Length == 0) return;
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                schema = 1, source = "axe-layout", scanCount, retryAttempted, firstErrorCount, secondErrorCount,
                observationsTruncated = (firstErrorCount ?? 0) > first.Length || (secondErrorCount ?? 0) > second.Length,
                observations = first.Concat(second)
            });
            if (Encoding.UTF8.GetByteCount(body) > MaxLayoutDiagnosticBytes)
                body = "{\"schema\":1,\"source\":\"axe-layout\",\"unavailable\":true}";
            if (write is null) Console.WriteLine(body);
            else write(body);
        }
        catch (Exception) { /* Diagnostics must not replace an assertion, probe failure, or passing result. */ }
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
