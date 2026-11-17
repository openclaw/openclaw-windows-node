using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
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

        AssertNoAccessibilityErrors(processId, hubWindowHandle, pageRuleExclusions, context);
    }

    public static void AssertNoAccessibilityErrors(
        int processId,
        IntPtr hubWindowHandle,
        IEnumerable<RuleId>? pageRuleExclusions = null,
        string? context = null,
        Action<string>? diagnosticSink = null)
    {
        Assert.NotEqual(IntPtr.Zero, hubWindowHandle);
        GetWindowThreadProcessId(hubWindowHandle, out var rootProcessId);
        Assert.Equal(processId, (int)rootProcessId);
        Assert.Equal(hubWindowHandle, GetAncestor(hubWindowHandle, 2));
        Assert.Equal(processId, AutomationElement.FromHandle(hubWindowHandle).Current.ProcessId);

        var excludedRules = new HashSet<RuleId>(GloballyExcludedRules);
        if (pageRuleExclusions != null)
            excludedRules.UnionWith(pageRuleExclusions);

        var config = Config.Builder
            .ForProcessId(processId)
            .WithOutputFileFormat(OutputFileFormat.None)
            .Build();
        var scanOptions = new ScanOptions(context, hubWindowHandle);
        var diagnosticCount = 0;
        var scanNumber = 0;
        var scopeOrdinal = 0;

        void Emit(string message)
        {
            Console.WriteLine(message);
            diagnosticSink?.Invoke(message);
        }

        void Log(string message)
        {
            if (diagnosticCount >= MaxReportedErrors)
            {
                if (diagnosticCount == MaxReportedErrors)
                {
                    diagnosticCount++;
                    Emit("Additional accessibility diagnostics omitted.");
                }
                return;
            }

            diagnosticCount++;
            Emit(BoundedText(message, MaxDescriptionLength));
        }

        bool IsScopeExcluded(ScanResult error)
        {
            var ordinal = ++scopeOrdinal;
            var rule = error.Rule.ID;
            var properties = error.Element.Properties;
            var outside = IsOutsideOwnedWindow(processId, hubWindowHandle, properties);

            // Without permitted live evidence of absent content, retain the proxy.
            // The pure predicate still requires a positively proven false value.
            var emptyProxy = IsEmptyProxyViolation(rule, properties, containsContent: null);
            var viewportClip = !outside && IsScrollViewportClipViolation(
                processId, hubWindowHandle, rule, properties,
                message => Log($"Scan {scanNumber}, scope item #{ordinal}: {message}"));

            Log($"Scan {scanNumber}, scope item #{ordinal}: rule={rule}; " +
                $"outsideOwnedWindow={outside}; emptyProxyExcluded={emptyProxy}; " +
                $"childContent=unknown; viewportClipExcluded={viewportClip}");
            return outside || emptyProxy || viewportClip;
        }

        AssertScan(
            () =>
            {
                scanNumber++;
                scopeOrdinal = 0;
                Log($"Axe scan={scanNumber}; client PID={Environment.ProcessId}; " +
                    $"app PID={processId}; root HWND=0x{hubWindowHandle.ToInt64():X}");
                return ScannerFactory.CreateScanner(config).Scan(scanOptions);
            },
            processId,
            () => new UiaLayoutElement(AutomationElement.FromHandle(hubWindowHandle)),
            excludedRules,
            context,
            IsScopeExcluded);
    }

    internal static void AssertScan(
        Func<ScanOutput?> scan,
        int processId,
        Func<ILayoutElement> getHub,
        ISet<RuleId> excludedRules,
        string? context)
        => AssertScan(scan, processId, getHub, excludedRules, context, null);

    internal static void AssertScan(
        Func<ScanOutput?> scan,
        int processId,
        Func<ILayoutElement> getHub,
        ISet<RuleId> excludedRules,
        string? context,
        Func<ScanResult, bool>? scopeExclusion)
    {
        List<ScanResult?> Scan()
        {
            // Validate the original output before any additional exclusions.
            var errors = GetErrors(scan(), excludedRules);
            if (scopeExclusion is null)
                return errors;

            return errors.Where(error =>
                !HasCompleteMetadata(error) || !scopeExclusion(error!)).ToList();
        }

        var errors = Scan();
        if (errors.Count > 0
            && errors.All(error => HasCompleteMetadata(error) && IsStaleLayoutSnapshot(
                getHub,
                processId,
                error!.Rule.ID,
                error.Element.Properties)))
        {
            errors = Scan();
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

    internal static bool IsForeignUnhostedProcess(int expectedProcessId, int actualProcessId, bool hostedUnderRoot) =>
        actualProcessId > 0 && actualProcessId != expectedProcessId && !hostedUnderRoot;

    internal static bool IsEmptyProxyViolation(RuleId rule, IReadOnlyDictionary<string, string> properties, bool? containsContent)
    {
        string Value(string key) => properties.TryGetValue(key, out var value) ? value : "";
        return rule == RuleId.BoundingRectangleNotNull && containsContent == false
            && string.IsNullOrWhiteSpace(Value("Name")) && string.IsNullOrWhiteSpace(Value("AutomationId"))
            && (string.IsNullOrWhiteSpace(Value("ControlType")) || Value("ControlType") == "0")
            && Value("LogicalSize") == "[w=0,h=0]"
            && !string.Equals(Value("IsEnabled"), "True", StringComparison.OrdinalIgnoreCase)
            && (Value("ProviderDescription").Contains("Main:Nested", StringComparison.Ordinal)
                || Value("ProviderDescription").Contains("Hwnd(parent link)", StringComparison.Ordinal));
    }

    internal static int[]? SnapshotRuntimeId(IReadOnlyDictionary<string, string> properties)
    {
        if (!properties.TryGetValue("RuntimeId", out var value)
            || value is null || !value.StartsWith('[') || !value.EndsWith(']'))
            return null;
        var parts = value[1..^1].Split(',');
        var result = new int[parts.Length];
        for (var index = 0; index < parts.Length; index++)
            if (!int.TryParse(parts[index].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result[index]))
                return null;
        return result;
    }

    internal static (bool Excluded, string Reason) ClassifyScrollViewportClip(
        RuleId rule, System.Windows.Rect bounds, bool isOffscreen, System.Windows.Rect? viewport,
        bool verticallyScrollable, bool horizontallyScrollable)
    {
        const double edgeTolerancePixels = 1;
        if (rule != RuleId.BoundingRectangleSizeReasonable)
            return (false, "different rule");
        if (isOffscreen)
            return (false, "already offscreen");
        if (bounds.IsEmpty || !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top)
            || !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Bottom)
            || (bounds.Width == 0) == (bounds.Height == 0))
            return (false, "not a finite degenerate line");
        if (viewport is not { } clip || clip.IsEmpty || clip.Width <= 0 || clip.Height <= 0
            || !double.IsFinite(clip.Left) || !double.IsFinite(clip.Top)
            || !double.IsFinite(clip.Right) || !double.IsFinite(clip.Bottom))
            return (false, "no finite scroll viewport");

        var verticalClip = bounds.Height == 0;
        if (verticalClip ? !verticallyScrollable : !horizontallyScrollable)
            return (false, "clipped axis is not scrollable");
        var extentStart = verticalClip ? bounds.Left : bounds.Top;
        var extentEnd = verticalClip ? bounds.Right : bounds.Bottom;
        var viewportStart = verticalClip ? clip.Left : clip.Top;
        var viewportEnd = verticalClip ? clip.Right : clip.Bottom;
        if (extentStart < viewportStart - edgeTolerancePixels || extentEnd > viewportEnd + edgeTolerancePixels)
            return (false, "other-axis extent exceeds viewport");
        var edge = verticalClip ? bounds.Top : bounds.Left;
        if (Math.Abs(edge - (verticalClip ? clip.Top : clip.Left)) <= edgeTolerancePixels)
            return (true, verticalClip ? "top" : "left");
        if (Math.Abs(edge - (verticalClip ? clip.Bottom : clip.Right)) <= edgeTolerancePixels)
            return (true, verticalClip ? "bottom" : "right");
        return (false, "line is not on viewport edge");
    }

    private static AutomationElement? FindSnapshotNode(AutomationElement hub, int[]? runtimeId) =>
        runtimeId is null ? null : hub.FindAll(TreeScope.Subtree, System.Windows.Automation.Condition.TrueCondition)
            .Cast<AutomationElement>().FirstOrDefault(element => element.GetRuntimeId().SequenceEqual(runtimeId));

    private static bool IsScrollViewportClipViolation(
        int processId, IntPtr root, RuleId rule, IReadOnlyDictionary<string, string> properties, Action<string> log)
    {
        if (rule != RuleId.BoundingRectangleSizeReasonable) return false;

        bool Keep(string reason)
        {
            log($"Axe scroll-viewport clip excluded=False; reason={reason}");
            return false;
        }

        try
        {
            return OwnedWindowCapture.WithPhysicalCoordinates(() =>
            {
                var hub = AutomationElement.FromHandle(root);
                var node = FindSnapshotNode(hub, SnapshotRuntimeId(properties));
                if (node is null) return Keep("snapshot peer unresolved");
                if (node.Current.ProcessId != processId) return Keep("snapshot peer is not in the app process");
                var bounds = node.Current.BoundingRectangle;
                var isOffscreen = node.Current.IsOffscreen;
                AutomationElement? scrollAncestor = null;
                System.Windows.Rect? viewport = null;
                var vertical = false;
                var horizontal = false;
                var underHub = false;
                for (var parent = TreeWalker.RawViewWalker.GetParent(node); parent is not null;
                    parent = TreeWalker.RawViewWalker.GetParent(parent))
                {
                    if (parent.Current.ProcessId != processId) return Keep("raw ancestor is not in the app process");
                    if (Automation.Compare(parent, hub)) { underHub = true; break; }
                    if (scrollAncestor is not null || !parent.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern))
                        continue;
                    scrollAncestor = parent;
                    viewport = parent.Current.BoundingRectangle;
                    var state = ((ScrollPattern)pattern).Current;
                    vertical = state.VerticallyScrollable;
                    horizontal = state.HorizontallyScrollable;
                    log($"Scroll viewport={viewport}; verticallyScrollable={vertical}; horizontallyScrollable={horizontal}; " +
                        $"verticalPercent={state.VerticalScrollPercent}; horizontalPercent={state.HorizontalScrollPercent}; " +
                        $"verticalViewSize={state.VerticalViewSize}; horizontalViewSize={state.HorizontalViewSize}");
                }
                if (!underHub) return Keep("raw ancestor chain no longer reaches the owned hub");
                var result = ClassifyScrollViewportClip(rule, bounds, isOffscreen, viewport, vertical, horizontal);
                log($"Axe scroll-viewport clip excluded={result.Excluded}; nodeRect={bounds}; isOffscreen={isOffscreen}; " +
                    $"matchedEdge={(result.Excluded ? result.Reason : "none")}; reason={result.Reason}");
                return result.Excluded;
            });
        }
        catch (ElementNotAvailableException)
        {
            return Keep("peer became unavailable");
        }
        catch (COMException error)
        {
            return Keep($"peer query failed with HRESULT 0x{error.HResult:X8}");
        }
    }

    private static bool IsOutsideOwnedWindow(int processId, IntPtr root, IReadOnlyDictionary<string, string> properties)
    {
        // ProviderDescription can name the client-side proxy process. Require native
        // window ownership before excluding a result; retain hosted and unknown peers.
        if (properties.TryGetValue("ProcessId", out var reported)
            && int.TryParse(reported, CultureInfo.InvariantCulture, out var reportedProcessId)
            && reportedProcessId == processId)
            return false;
        if (!properties.TryGetValue("NativeWindowHandle", out var value)
            || !long.TryParse(value, CultureInfo.InvariantCulture, out var handle) || handle == 0)
            return false;

        var window = new IntPtr(handle);
        GetWindowThreadProcessId(window, out var actualProcessId);
        var hosted = window == root || IsChild(root, window) || GetAncestor(window, 3) == root;
        return IsForeignUnhostedProcess(processId, (int)actualProcessId, hosted);
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

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
