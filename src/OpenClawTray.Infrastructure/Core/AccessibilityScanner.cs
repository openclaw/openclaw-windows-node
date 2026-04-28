using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;

namespace OpenClawTray.Infrastructure.Core;

// ════════════════════════════════════════════════════════════════════════
//  Accessibility diagnostic records (AI-agent-friendly structured output)
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// A single accessibility diagnostic finding from the scanner.
/// Each finding maps to a specific WCAG 2.1 criterion and includes
/// rich contextual information so an AI agent can generate intelligent
/// fixes from the JSON export alone.
/// </summary>
public record A11yDiagnostic
{
    /// <summary>Diagnostic ID (e.g., "A11Y_001").</summary>
    public required string Id { get; init; }

    /// <summary>"warning" or "info".</summary>
    public required string Severity { get; init; }

    /// <summary>Human-readable description of the issue.</summary>
    public required string Message { get; init; }

    /// <summary>WCAG 2.1 success criterion (e.g., "1.1.1").</summary>
    public required string WcagCriterion { get; init; }

    /// <summary>The C# record type name of the element (e.g., "ButtonElement").</summary>
    public required string ElementType { get; init; }

    /// <summary>AutomationId if set on the element, null otherwise.</summary>
    public string? AutomationId { get; init; }

    /// <summary>The component type name wrapping this element, if known.</summary>
    public string? ComponentType { get; init; }

    /// <summary>What modifier to add and a suggested value.</summary>
    public required A11yFixSuggestion Fix { get; init; }

    /// <summary>Contextual clues from the surrounding element tree.</summary>
    public required A11yContext Context { get; init; }
}

/// <summary>
/// Tells an AI agent exactly which modifier to add and what value to use.
/// </summary>
public record A11yFixSuggestion
{
    /// <summary>The Reactor modifier method name (e.g., "AutomationName").</summary>
    public required string Modifier { get; init; }

    /// <summary>A suggested value, or null if the agent must infer from context.</summary>
    public string? SuggestedValue { get; init; }

    /// <summary>Example code snippet (e.g., ".AutomationName(\"...\")"). </summary>
    public string? CodeSnippet { get; init; }
}

/// <summary>
/// Rich contextual information harvested from the element tree during the scan.
/// An AI agent uses this to generate semantically correct accessible names
/// without needing to re-read the source code.
/// </summary>
public record A11yContext
{
    /// <summary>AutomationName of the nearest ancestor that has one.</summary>
    public string? ParentAutomationName { get; init; }

    /// <summary>Text and level of the nearest heading above this element.</summary>
    public string? NearestHeading { get; init; }

    /// <summary>Nearest landmark type name (e.g., "Navigation", "Main").</summary>
    public string? NearestLandmark { get; init; }

    /// <summary>Text labels of sibling elements at the same level.</summary>
    public string[]? SiblingTexts { get; init; }

    /// <summary>String description of the child content (e.g., "SymbolIcon(Symbol.Delete)").</summary>
    public string? ChildContent { get; init; }

    /// <summary>Text content of the first TextBlockElement child, if any.</summary>
    public string? ChildText { get; init; }

    /// <summary>Type names of immediate children.</summary>
    public string[]? ChildTypes { get; init; }

    /// <summary>Header property value (for TextFieldElement etc.).</summary>
    public string? Header { get; init; }

    /// <summary>Placeholder text (for TextFieldElement etc.).</summary>
    public string? PlaceholderText { get; init; }
}

// ════════════════════════════════════════════════════════════════════════
//  Scanner implementation
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// Post-reconciliation accessibility scanner that walks the Reactor virtual
/// element tree and produces structured diagnostics. Intended for DEBUG
/// builds only — call via <see cref="ReactorHostControl.EnableAccessibilityDiagnostics"/>.
///
/// The scanner produces AI-agent-friendly JSON output: each diagnostic
/// includes rich contextual clues (sibling texts, parent names, child
/// content) so an AI agent can generate semantically correct fixes in
/// a single pass without re-reading source code.
/// </summary>
public static partial class AccessibilityScanner
{
    /// <summary>
    /// Scans the virtual element tree for accessibility issues.
    /// Returns a list of diagnostics, each with rich context for AI-agent consumption.
    /// </summary>
    public static List<A11yDiagnostic> Scan(Element root)
    {
        var findings = new List<A11yDiagnostic>();
        var ctx = new ScanContext();
        Walk(root, ctx, findings);

        // Post-walk checks (tree-wide)
        CheckNoMainLandmark(ctx, findings);
        CheckTabIndexGaps(ctx, findings);
        CheckUnresolvedLabeledBy(ctx, findings);

        return findings;
    }

    /// <summary>
    /// Exports diagnostics as structured JSON to the specified file path.
    /// </summary>
    public static void ExportJson(List<A11yDiagnostic> diagnostics, string filePath)
    {
        var report = new A11yReport
        {
            Timestamp = DateTime.UtcNow.ToString("o"),
            DiagnosticCount = diagnostics.Count,
            BySeverity = diagnostics.GroupBy(d => d.Severity)
                .ToDictionary(g => g.Key, g => g.Count()),
            ByCheck = diagnostics.GroupBy(d => d.Id)
                .ToDictionary(g => g.Key, g => g.Count()),
            Diagnostics = diagnostics,
        };

        var dir = global::System.IO.Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !global::System.IO.Directory.Exists(dir))
            global::System.IO.Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(report, A11yJsonContext.Default.A11yReport);
        global::System.IO.File.WriteAllText(filePath, json);
    }

    // ════════════════════════════════════════════════════════════════
    //  Tree walk
    // ════════════════════════════════════════════════════════════════

    private static void Walk(Element? el, ScanContext ctx, List<A11yDiagnostic> findings)
    {
        if (el is null or EmptyElement) return;

        ctx.Push(el);

        // Per-element checks
        CheckIconButton(el, ctx, findings);
        CheckImage(el, ctx, findings);
        CheckFormField(el, ctx, findings);
        CheckHeadingStyle(el, ctx, findings);
        CheckConcreteBrushOnInteractive(el, ctx, findings);

        // Collect data for post-walk checks
        CollectTabIndex(el, ctx);
        CollectLabeledBy(el, ctx);
        CollectAutomationId(el, ctx);
        CollectLandmark(el, ctx);

        // Recurse into children
        foreach (var child in GetChildren(el))
            Walk(child, ctx, findings);

        ctx.Pop();
    }

    // ════════════════════════════════════════════════════════════════
    //  Child extraction — knows about every container element type
    // ════════════════════════════════════════════════════════════════

    private static IEnumerable<Element?> GetChildren(Element el) => el switch
    {
        // Multi-child containers
        StackElement s => s.Children,
        GridElement g => g.Children,
        WrapGridElement w => w.Children,
        CanvasElement c => c.Children,
        RelativePanelElement r => r.Children,
        GroupElement grp => grp.Children,

        // Single-child containers
        ScrollViewElement sc => Single(sc.Child),
        BorderElement b => Single(b.Child),
        ViewboxElement vb => Single(vb.Child),
        ErrorBoundaryElement eb => Single(eb.Child),
        CommandHostElement ch => Single(ch.Child),
        PopupElement p => Single(p.Child),
        RefreshContainerElement rc => Single(rc.Content),
        SwipeControlElement sw => Single(sw.Content),
        ParallaxViewElement pv => Single(pv.Child),

        // Elements with optional child content
        ExpanderElement ex => Single(ex.Content),
        SplitViewElement sv => Pair(sv.Pane, sv.Content),
        SemanticZoomElement sz => Pair(sz.ZoomedInView, sz.ZoomedOutView),
        NavigationViewElement nv => Single(nv.Content),
        ButtonElement btn => SingleOrEmpty(btn.ContentElement),

        // List/collection elements with item arrays
        ListViewElement lv => lv.Items,
        GridViewElement gv => gv.Items,
        FlipViewElement fv => fv.Items,

        // Navigation host (opaque — route content is resolved at runtime)
        NavigationHostElement => [],

        // Everything else has no children the scanner can walk
        _ => [],
    };

    private static Element?[] Single(Element? child) => [child];
    private static Element?[] Pair(Element? a, Element? b) => [a, b];
    private static Element?[] SingleOrEmpty(Element? child) => child is not null ? [child] : [];

    // ════════════════════════════════════════════════════════════════
    //  Per-element checks (A11Y_001 – A11Y_005)
    // ════════════════════════════════════════════════════════════════

    /// <summary>A11Y_001: Icon-only Button without accessible name.</summary>
    private static void CheckIconButton(Element el, ScanContext ctx, List<A11yDiagnostic> findings)
    {
        if (el is not ButtonElement btn) return;

        // Only flag buttons that have a non-text content element (icon) and
        // either no label or a label that looks like an emoji/symbol
        bool isIconOnly = btn.ContentElement is not null
            || string.IsNullOrWhiteSpace(btn.Label)
            || IsLikelyEmoji(btn.Label);
        if (!isIconOnly) return;

        // Check if AutomationName is set
        if (HasAutomationName(el)) return;

        findings.Add(new A11yDiagnostic
        {
            Id = "A11Y_001",
            Severity = "warning",
            Message = "Icon-only Button has no accessible name — screen readers cannot describe this control",
            WcagCriterion = "1.1.1",
            ElementType = el.GetType().Name,
            AutomationId = GetAutomationId(el),
            ComponentType = ctx.CurrentComponent,
            Fix = new A11yFixSuggestion
            {
                Modifier = "AutomationName",
                SuggestedValue = null,
                CodeSnippet = ".AutomationName(\"...\")",
            },
            Context = ctx.BuildContext(el, GetSiblingTexts(ctx)),
        });
    }

    /// <summary>A11Y_002: Image without alt text or AccessibilityHidden.</summary>
    private static void CheckImage(Element el, ScanContext ctx, List<A11yDiagnostic> findings)
    {
        if (el is not ImageElement img) return;
        if (HasAutomationName(el)) return;
        if (IsAccessibilityHidden(el)) return;

        findings.Add(new A11yDiagnostic
        {
            Id = "A11Y_002",
            Severity = "warning",
            Message = "Image has no accessible name and is not hidden from assistive technology",
            WcagCriterion = "1.1.1",
            ElementType = "ImageElement",
            AutomationId = GetAutomationId(el),
            ComponentType = ctx.CurrentComponent,
            Fix = new A11yFixSuggestion
            {
                Modifier = "AutomationName",
                SuggestedValue = null,
                CodeSnippet = ".AutomationName(\"description\") or .AccessibilityHidden()",
            },
            Context = ctx.BuildContext(el, GetSiblingTexts(ctx)),
        });
    }

    /// <summary>A11Y_003: Form field without label.</summary>
    private static void CheckFormField(Element el, ScanContext ctx, List<A11yDiagnostic> findings)
    {
        // Match input elements that need labels
        string? header = null;
        string? placeholder = null;
        switch (el)
        {
            case TextFieldElement tf:
                header = tf.Header;
                placeholder = tf.Placeholder;
                break;
            case NumberBoxElement nb:
                header = nb.Header;
                break;
            case PasswordBoxElement pb:
                placeholder = pb.PlaceholderText;
                break;
            case AutoSuggestBoxElement asb:
                placeholder = asb.PlaceholderText;
                break;
            default:
                return;
        }

        // Has a header label?
        if (!string.IsNullOrEmpty(header)) return;

        // Has AutomationName?
        if (HasAutomationName(el)) return;

        // Has LabeledBy?
        if (HasLabeledBy(el)) return;

        findings.Add(new A11yDiagnostic
        {
            Id = "A11Y_003",
            Severity = "warning",
            Message = "Form field has no label — set header:, .AutomationName(), or .LabeledBy() for screen readers",
            WcagCriterion = "3.3.2",
            ElementType = el.GetType().Name,
            AutomationId = GetAutomationId(el),
            ComponentType = ctx.CurrentComponent,
            Fix = new A11yFixSuggestion
            {
                Modifier = "AutomationName",
                SuggestedValue = null,
                CodeSnippet = ".AutomationName(\"field label\")",
            },
            Context = ctx.BuildContext(el, GetSiblingTexts(ctx)) with
            {
                Header = header,
                PlaceholderText = placeholder,
            },
        });
    }

    /// <summary>A11Y_004: Heading-styled text without HeadingLevel.</summary>
    private static void CheckHeadingStyle(Element el, ScanContext ctx, List<A11yDiagnostic> findings)
    {
        if (el is not TextBlockElement text) return;

        // Heuristic: large + bold text that isn't annotated as a heading
        bool isLargeFont = text.FontSize.HasValue && text.FontSize.Value >= 20;
        bool isBoldWeight = text.Weight.HasValue &&
            (text.Weight.Value.Weight >= 600); // SemiBold = 600, Bold = 700

        // Also check modifiers for FontSize/FontWeight
        if (!isLargeFont && el.Modifiers?.FontSize is >= 20) isLargeFont = true;
        if (!isBoldWeight && el.Modifiers?.FontWeight is { } fw && fw.Weight >= 600) isBoldWeight = true;

        if (!isLargeFont || !isBoldWeight) return;

        // Already has a HeadingLevel?
        if (el.Modifiers?.HeadingLevel.HasValue == true) return;

        findings.Add(new A11yDiagnostic
        {
            Id = "A11Y_004",
            Severity = "info",
            Message = $"Text \"{Truncate(text.Content, 40)}\" is styled as a heading but has no HeadingLevel set",
            WcagCriterion = "1.3.1",
            ElementType = "TextBlockElement",
            AutomationId = GetAutomationId(el),
            ComponentType = ctx.CurrentComponent,
            Fix = new A11yFixSuggestion
            {
                Modifier = "HeadingLevel",
                SuggestedValue = "Level1",
                CodeSnippet = ".HeadingLevel(AutomationHeadingLevel.Level1)",
            },
            Context = ctx.BuildContext(el, GetSiblingTexts(ctx)) with
            {
                ChildText = text.Content,
            },
        });
    }

    /// <summary>A11Y_005: Concrete brush on interactive control.</summary>
    private static void CheckConcreteBrushOnInteractive(Element el, ScanContext ctx, List<A11yDiagnostic> findings)
    {
        // Only flag interactive controls
        bool isInteractive = el is ButtonElement or TextFieldElement or NumberBoxElement
            or PasswordBoxElement or CheckBoxElement or RadioButtonElement
            or ComboBoxElement or SliderElement or ToggleSwitchElement
            or ToggleButtonElement;
        if (!isInteractive) return;

        // Check for concrete Background brush (not ThemeRef)
        var bg = el.Modifiers?.Background;
        if (bg is null) return;

        // If element has ThemeBindings for Background, it's using ThemeRef — OK
        if (el.ThemeBindings is not null && el.ThemeBindings.ContainsKey("Background")) return;

        findings.Add(new A11yDiagnostic
        {
            Id = "A11Y_005",
            Severity = "warning",
            Message = "Interactive control uses a concrete brush that won't adapt in High Contrast mode",
            WcagCriterion = "1.4.11",
            ElementType = el.GetType().Name,
            AutomationId = GetAutomationId(el),
            ComponentType = ctx.CurrentComponent,
            Fix = new A11yFixSuggestion
            {
                Modifier = "Background",
                SuggestedValue = "Theme.Accent",
                CodeSnippet = ".Background(Theme.Accent) or another ThemeRef token",
            },
            Context = ctx.BuildContext(el, GetSiblingTexts(ctx)),
        });
    }

    // ════════════════════════════════════════════════════════════════
    //  Post-walk checks (A11Y_006 – A11Y_008)
    // ════════════════════════════════════════════════════════════════

    /// <summary>A11Y_006: No Main landmark in the root tree.</summary>
    private static void CheckNoMainLandmark(ScanContext ctx, List<A11yDiagnostic> findings)
    {
        if (ctx.HasMainLandmark) return;

        findings.Add(new A11yDiagnostic
        {
            Id = "A11Y_006",
            Severity = "info",
            Message = "No element has .Landmark(AutomationLandmarkType.Main) — screen readers cannot identify the main content region",
            WcagCriterion = "1.3.1",
            ElementType = "(tree-wide)",
            Fix = new A11yFixSuggestion
            {
                Modifier = "Landmark",
                SuggestedValue = "Main",
                CodeSnippet = ".Landmark(AutomationLandmarkType.Main)",
            },
            Context = new A11yContext(),
        });
    }

    /// <summary>A11Y_007: Non-sequential TabIndex values (gaps > 1).</summary>
    private static void CheckTabIndexGaps(ScanContext ctx, List<A11yDiagnostic> findings)
    {
        if (ctx.TabIndices.Count < 2) return;

        var sorted = ctx.TabIndices.OrderBy(t => t).ToList();
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] - sorted[i - 1] > 1)
            {
                findings.Add(new A11yDiagnostic
                {
                    Id = "A11Y_007",
                    Severity = "info",
                    Message = $"TabIndex gap: {sorted[i - 1]} → {sorted[i]}. Non-sequential values may confuse keyboard navigation order",
                    WcagCriterion = "2.1.1",
                    ElementType = "(tree-wide)",
                    Fix = new A11yFixSuggestion
                    {
                        Modifier = "TabIndex",
                        SuggestedValue = null,
                        CodeSnippet = "Renumber TabIndex values sequentially",
                    },
                    Context = new A11yContext(),
                });
                break; // Report only the first gap
            }
        }
    }

    /// <summary>A11Y_008: .LabeledBy() references an AutomationId not found in the tree.</summary>
    private static void CheckUnresolvedLabeledBy(ScanContext ctx, List<A11yDiagnostic> findings)
    {
        foreach (var (labeledById, elementType, automationId) in ctx.LabeledByRefs)
        {
            if (!ctx.AllAutomationIds.Contains(labeledById))
            {
                findings.Add(new A11yDiagnostic
                {
                    Id = "A11Y_008",
                    Severity = "warning",
                    Message = $".LabeledBy(\"{labeledById}\") references an AutomationId not found in the element tree",
                    WcagCriterion = "3.3.2",
                    ElementType = elementType,
                    AutomationId = automationId,
                    Fix = new A11yFixSuggestion
                    {
                        Modifier = "LabeledBy",
                        SuggestedValue = null,
                        CodeSnippet = $"Ensure an element with .AutomationId(\"{labeledById}\") exists in the tree",
                    },
                    Context = new A11yContext(),
                });
            }
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Data collection helpers
    // ════════════════════════════════════════════════════════════════

    private static void CollectTabIndex(Element el, ScanContext ctx)
    {
        if (el.Modifiers?.TabIndex is { } idx)
            ctx.TabIndices.Add(idx);
    }

    private static void CollectLabeledBy(Element el, ScanContext ctx)
    {
        var labeledBy = el.Modifiers?.Accessibility?.LabeledBy;
        if (labeledBy is not null)
            ctx.LabeledByRefs.Add((labeledBy, el.GetType().Name, GetAutomationId(el)));
    }

    private static void CollectAutomationId(Element el, ScanContext ctx)
    {
        var id = el.Modifiers?.AutomationId;
        if (id is not null)
            ctx.AllAutomationIds.Add(id);
    }

    private static void CollectLandmark(Element el, ScanContext ctx)
    {
        var landmark = el.Modifiers?.Accessibility?.LandmarkType;
        if (landmark == AutomationLandmarkType.Main)
            ctx.HasMainLandmark = true;
    }

    // ════════════════════════════════════════════════════════════════
    //  Modifier inspection helpers
    // ════════════════════════════════════════════════════════════════

    private static bool HasAutomationName(Element el) =>
        !string.IsNullOrEmpty(el.Modifiers?.AutomationName);

    private static bool HasLabeledBy(Element el) =>
        !string.IsNullOrEmpty(el.Modifiers?.Accessibility?.LabeledBy);

    private static bool IsAccessibilityHidden(Element el) =>
        el.Modifiers?.Accessibility?.AccessibilityView == AccessibilityView.Raw;

    private static string? GetAutomationId(Element el) =>
        el.Modifiers?.AutomationId;

    private static bool IsLikelyEmoji(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        // Single character or very short string that's non-ASCII → likely emoji/icon
        if (text.Length <= 2 && text.Any(c => c > 0x2000)) return true;
        // Unicode symbol ranges commonly used for icons
        return text.Length <= 4 && text.All(c =>
            c > 0x2000 || char.IsHighSurrogate(c) || char.IsLowSurrogate(c));
    }

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..(maxLen - 3)] + "...";

    private static string[] GetSiblingTexts(ScanContext ctx) =>
        ctx.CurrentSiblingTexts.ToArray();

    // ════════════════════════════════════════════════════════════════
    //  Scan context (maintained during tree walk)
    // ════════════════════════════════════════════════════════════════

    private sealed class ScanContext
    {
        // Stack for nearest heading/landmark/parent name
        private readonly Stack<ContextFrame> _stack = new();

        // Post-walk collections
        public readonly HashSet<int> TabIndices = new();
        public readonly List<(string LabeledBy, string ElementType, string? AutomationId)> LabeledByRefs = new();
        public readonly HashSet<string> AllAutomationIds = new();
        public bool HasMainLandmark;

        // Current sibling context (texts of siblings in the same container)
        public readonly List<string> CurrentSiblingTexts = new();

        public string? CurrentComponent => _stack.Count > 0 ? _stack.Peek().ComponentType : null;

        public void Push(Element el)
        {
            var frame = new ContextFrame();

            // Inherit from parent
            if (_stack.Count > 0)
            {
                var parent = _stack.Peek();
                frame.NearestHeading = parent.NearestHeading;
                frame.NearestLandmark = parent.NearestLandmark;
                frame.ParentAutomationName = parent.ParentAutomationName;
                frame.ComponentType = parent.ComponentType;
            }

            // Update from current element
            if (el is TextBlockElement text && el.Modifiers?.HeadingLevel.HasValue == true)
                frame.NearestHeading = text.Content;

            var landmark = el.Modifiers?.Accessibility?.LandmarkType;
            if (landmark.HasValue)
                frame.NearestLandmark = landmark.Value.ToString();

            if (!string.IsNullOrEmpty(el.Modifiers?.AutomationName))
                frame.ParentAutomationName = el.Modifiers!.AutomationName;

            if (el is ComponentElement comp)
                frame.ComponentType = comp.ComponentType?.Name;

            // Collect sibling texts from container children
            CurrentSiblingTexts.Clear();
            foreach (var child in GetChildren(el))
            {
                if (child is TextBlockElement t)
                    CurrentSiblingTexts.Add(t.Content);
                else if (child is ButtonElement b && !string.IsNullOrEmpty(b.Label))
                    CurrentSiblingTexts.Add(b.Label);
            }

            _stack.Push(frame);
        }

        public void Pop()
        {
            if (_stack.Count > 0)
                _stack.Pop();
        }

        public A11yContext BuildContext(Element el, string[] siblingTexts)
        {
            var frame = _stack.Count > 0 ? _stack.Peek() : new ContextFrame();

            // Extract child info
            var children = GetChildren(el).Where(c => c is not null and not EmptyElement).ToList();
            string? childContent = null;
            string? childText = null;
            string[]? childTypes = null;

            if (children.Count > 0)
            {
                childTypes = children.Select(c => c!.GetType().Name).ToArray();
                var firstText = children.OfType<TextBlockElement>().FirstOrDefault();
                if (firstText is not null)
                    childText = firstText.Content;
                if (children.Count == 1)
                    childContent = children[0]!.ToString();
            }

            // For ButtonElement, use ContentElement if present
            if (el is ButtonElement btn && btn.ContentElement is not null)
            {
                childContent = btn.ContentElement.GetType().Name;
                childTypes = [btn.ContentElement.GetType().Name];
                if (btn.ContentElement is TextBlockElement ct)
                    childText = ct.Content;
            }

            return new A11yContext
            {
                ParentAutomationName = frame.ParentAutomationName,
                NearestHeading = frame.NearestHeading,
                NearestLandmark = frame.NearestLandmark,
                SiblingTexts = siblingTexts.Length > 0 ? siblingTexts : null,
                ChildContent = childContent,
                ChildText = childText,
                ChildTypes = childTypes,
            };
        }
    }

    private sealed class ContextFrame
    {
        public string? NearestHeading;
        public string? NearestLandmark;
        public string? ParentAutomationName;
        public string? ComponentType;
    }

    // ════════════════════════════════════════════════════════════════
    //  JSON report envelope
    // ════════════════════════════════════════════════════════════════

    private sealed class A11yReport
    {
        public required string Timestamp { get; init; }
        public required int DiagnosticCount { get; init; }
        public required Dictionary<string, int> BySeverity { get; init; }
        public required Dictionary<string, int> ByCheck { get; init; }
        public required List<A11yDiagnostic> Diagnostics { get; init; }
    }

    // Source-generated serialization options matching the inline options
    // previously constructed in ExportToJson. Keeps the camelCase + ignore-null
    // policy while enabling Native AOT.
    [JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(A11yReport))]
    private partial class A11yJsonContext : JsonSerializerContext
    {
    }
}
