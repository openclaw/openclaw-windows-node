using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
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
        var scanner = ScannerFactory.CreateScanner(config);
        var scanOptions = new ScanOptions(context, hubWindowHandle);
        void Log(string message)
        {
            Console.WriteLine(message);
            diagnosticSink?.Invoke(message);
        }
        List<ScanResult> Scan()
        {
            Log($"Axe client PID={Environment.ProcessId}; app PID={processId}; root HWND=0x{hubWindowHandle.ToInt64():X}; context={context}");
            var outputs = scanner.Scan(scanOptions).WindowScanOutputs;
            Assert.NotEmpty(outputs);
            var failures = new List<ScanResult>();
            foreach (var error in outputs.SelectMany(output => output.Errors))
            {
                var (diagnostic, containsContent) = DescribeSnapshotNode(hubWindowHandle, error.Element.Properties);
                var emptyProxy = IsEmptyProxyViolation(error.Rule.ID, error.Element.Properties, containsContent);
                var viewportClip = IsScrollViewportClipViolation(
                    processId, hubWindowHandle, error.Rule.ID, error.Element.Properties, Log);
                Log($"Axe violation rule={error.Rule.ID}; {diagnostic}; emptyProxyExcluded={emptyProxy}; " +
                    string.Join("; ", error.Element.Properties.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}='{p.Value}'")));
                if (!excludedRules.Contains(error.Rule.ID) && !emptyProxy && !viewportClip
                    && !IsOutsideOwnedWindow(processId, hubWindowHandle, error.Element.Properties))
                    failures.Add(error);
            }
            return failures;
        }
        var errors = Scan();
        if (errors.Count > 0
            && errors.All(error => IsStaleLayoutSnapshot(
                hubWindowHandle,
                error.Rule.ID,
                error.Element.Properties)))
        {
            scanner = ScannerFactory.CreateScanner(config);
            errors = Scan();
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
                error.Element.Properties,
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
        if (!properties.TryGetValue("RuntimeId", out var value) || !value.StartsWith('[') || !value.EndsWith(']'))
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
        runtimeId is null ? null : hub.FindAll(TreeScope.Subtree, Condition.TrueCondition)
            .Cast<AutomationElement>().FirstOrDefault(element => element.GetRuntimeId().SequenceEqual(runtimeId));

    private static bool IsScrollViewportClipViolation(
        int processId, IntPtr root, RuleId rule, IReadOnlyDictionary<string, string> properties, Action<string> log)
    {
        if (rule != RuleId.BoundingRectangleSizeReasonable) return false;
        var diagnostic = $"RuntimeId='{(properties.TryGetValue("RuntimeId", out var id) ? id : "(missing)")}'";
        bool Keep(string reason)
        {
            log($"Axe scroll-viewport clip excluded=False; {diagnostic}; reason={reason}");
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
                diagnostic += $"; nodeRect={bounds}; isOffscreen={isOffscreen}";
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
                    diagnostic += $"; ancestorClass='{parent.Current.ClassName}'; ancestorId='{parent.Current.AutomationId}'; " +
                        $"ancestorRect={viewport}; verticallyScrollable={vertical}; horizontallyScrollable={horizontal}; " +
                        $"verticalPercent={state.VerticalScrollPercent}; horizontalPercent={state.HorizontalScrollPercent}; " +
                        $"verticalViewSize={state.VerticalViewSize}; horizontalViewSize={state.HorizontalViewSize}";
                }
                if (!underHub) return Keep("raw ancestor chain no longer reaches the owned hub");
                var result = ClassifyScrollViewportClip(rule, bounds, isOffscreen, viewport, vertical, horizontal);
                log($"Axe scroll-viewport clip excluded={result.Excluded}; {diagnostic}; " +
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

    private static (string Diagnostic, bool? ContainsContent) DescribeSnapshotNode(
        IntPtr root, IReadOnlyDictionary<string, string> properties)
    {
        properties.TryGetValue("NativeWindowHandle", out var native);
        _ = long.TryParse(native, CultureInfo.InvariantCulture, out var handle);
        var window = new IntPtr(handle);
        GetWindowThreadProcessId(window, out var owner);
        var windowClass = new StringBuilder(256);
        _ = GetClassName(window, windowClass, windowClass.Capacity);
        properties.TryGetValue("ProviderDescription", out var provider);
        var hr = Regex.Match(provider ?? "", @"hr=0x[0-9A-Fa-f]+").Value;
        string Value(string key) => properties.TryGetValue(key, out var value) ? value : "(missing)";
        var prefix = $"ControlType='{Value("ControlType")}'; Name='{Value("Name")}'; AutomationId='{Value("AutomationId")}'; " +
            $"ClassName='{Value("ClassName")}'; ProviderDescription='{provider}'; " +
            $"nativeHWND=0x{handle:X}; nativeOwnerPID={owner}; nativeClass='{windowClass}'; nativeUnderHub={window == root || IsChild(root, window)}; " +
            $"rootOwner=0x{GetAncestor(window, 3).ToInt64():X}; hr='{hr}'";
        try
        {
            // Axe serializes runtime IDs as hexadecimal. Match the exact peer in
            // the owned tree, never the first unnamed or same-HWND descendant.
            var runtimeId = SnapshotRuntimeId(properties);
            var hub = AutomationElement.FromHandle(root);
            var node = FindSnapshotNode(hub, runtimeId);
            if (node is null)
                return ($"{prefix}; bounds=unresolved; underHub=unresolved; underEditor=unresolved; childContent=unknown", null);
            var hasChild = TreeWalker.RawViewWalker.GetFirstChild(node) is not null;
            var named = !string.IsNullOrWhiteSpace(node.Current.Name) || !string.IsNullOrWhiteSpace(node.Current.AutomationId);
            var type = node.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty, true);
            var typed = type is ControlType controlType && controlType.Id != 0;
            var underEditor = false;
            for (var parent = node; parent is not null; parent = TreeWalker.RawViewWalker.GetParent(parent))
            {
                if (parent.Current.AutomationId == "AutomationEditorDialog") underEditor = true;
                if (Automation.Compare(parent, hub)) break;
            }
            bool? containsContent = hasChild || named || typed ? true : type == AutomationElement.NotSupported ? null : false;
            return ($"{prefix}; bounds={node.Current.BoundingRectangle}; underHub=True; underEditor={underEditor}; " +
                $"childContent={hasChild}; liveNamed={named}; liveTyped={typed}", containsContent);
        }
        catch (ElementNotAvailableException)
        {
            return ($"{prefix}; bounds=unavailable; underHub=unresolved; underEditor=unresolved; childContent=unknown", null);
        }
        catch (COMException error)
        {
            return ($"{prefix}; liveHR=0x{error.HResult:X8}; bounds=unavailable; underHub=unresolved; underEditor=unresolved; childContent=unknown", null);
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
        var outside = IsForeignUnhostedProcess(processId, (int)actualProcessId, hosted);
        if (outside)
            Console.WriteLine($"Axe scoped out unrelated HWND 0x{handle:X}, PID {actualProcessId}; owned root 0x{root.ToInt64():X}, PID {processId}.");
        return outside;
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
        IReadOnlyDictionary<string, string> properties,
        string? automationId,
        string? name)
    {
        try
        {
            var hub = AutomationElement.FromHandle(hubWindowHandle);
            AutomationElement? element;
            if (properties.ContainsKey("RuntimeId"))
            {
                element = FindSnapshotNode(hub, SnapshotRuntimeId(properties));
            }
            else
            {
                Condition? condition = !string.IsNullOrWhiteSpace(automationId)
                    ? new PropertyCondition(AutomationElement.AutomationIdProperty, automationId) : null;
                if (condition is null && properties.TryGetValue("NativeWindowHandle", out var native)
                    && int.TryParse(native, CultureInfo.InvariantCulture, out var nativeHandle) && nativeHandle != 0)
                    condition = new PropertyCondition(AutomationElement.NativeWindowHandleProperty, nativeHandle);
                if (condition is null && !string.IsNullOrWhiteSpace(name))
                    condition = new PropertyCondition(AutomationElement.NameProperty, name);
                if (condition is null)
                    return "snapshot has no named/native identity; no unrelated unnamed descendant substituted";
                element = hub.FindFirst(TreeScope.Descendants, condition);
            }
            if (element is null)
                return "element no longer present";

            var clickablePoint = element.TryGetClickablePoint(out var point)
                ? $"{point.X:0.##},{point.Y:0.##}"
                : "none";
            var focused = hub.FindFirst(TreeScope.Subtree, new PropertyCondition(AutomationElement.HasKeyboardFocusProperty, true));
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

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);
}
