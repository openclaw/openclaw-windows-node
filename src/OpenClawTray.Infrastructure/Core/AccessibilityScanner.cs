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

        // Chart-specific checks
        CheckChartRules(el, ctx, findings);

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
        FlexElement f => f.Children,
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
    //  Chart-specific checks (A11Y_CHART_001 – A11Y_CHART_012)
    // ════════════════════════════════════════════════════════════════

    /// <summary>Runs all chart-specific accessibility rules on chart CanvasElements.</summary>
    private static void CheckChartRules(Element el, ScanContext ctx, List<A11yDiagnostic> findings)
    {
        CanvasElement? canvas = null;

        if (el is CanvasElement c && c.ChartData is not null)
        {
            canvas = c;
        }
        else if (el.Attached?.TryGetValue(typeof(Charting.Accessibility.ChartScannerHint), out var hint) == true
            && hint is Charting.Accessibility.ChartScannerHint scannerHint)
        {
            // FuncElement wrappers (keyboard navigator) carry the inner canvas as a scanner hint
            canvas = scannerHint.InnerCanvas;
        }

        if (canvas is null || canvas.ChartData is null)
            return;

        var chartData = canvas.ChartData;

        CheckChartTitle(canvas, chartData, ctx, findings);
        CheckChartDescription(canvas, chartData, ctx, findings);
        CheckChartColorOnly(canvas, ctx, findings);

        // Skip palette checks when .RawColors() opted out — those would produce
        // incorrect warnings for charts that intentionally use custom colors.
        if (!canvas.IsRawColors)
        {
            CheckChartPaletteContrast(canvas, ctx, findings);
            CheckChartPaletteColorblind(canvas, ctx, findings);
            CheckChartPaletteBackground(canvas, ctx, findings);
        }
        CheckChartRawColors(canvas, ctx, findings);
        CheckChartInteractiveKeyboard(canvas, ctx, findings);
        CheckChartTightHitTest(canvas, ctx, findings);
        CheckChartFocusIndicatorContrast(canvas, ctx, findings);
        CheckChartAnnounceEveryFrame(canvas, ctx, findings);
    }

    /// <summary>A11Y_CHART_001: Chart has no Title/AutomationName and no derivable name.</summary>
    private static void CheckChartTitle(CanvasElement canvas, Charting.Accessibility.IChartAccessibilityData data,
        ScanContext ctx, List<A11yDiagnostic> findings)
    {
        // Has explicit AutomationName? (Ignore "Plot area" — that's an auto-set structural label)
        if (HasAutomationName(canvas) && canvas.Modifiers?.AutomationName != "Plot area") return;
        // Has Title?
        if (!string.IsNullOrWhiteSpace(data.Name)) return;

        findings.Add(new A11yDiagnostic
        {
            Id = "A11Y_CHART_001",
            Severity = "warning",
            Message = "Chart has no accessible name — set .Title(\"...\") or .AutomationName(\"...\")",
            WcagCriterion = "1.1.1",
            ElementType = "CanvasElement (Chart)",
            AutomationId = GetAutomationId(canvas),
            ComponentType = ctx.CurrentComponent,
            Fix = new A11yFixSuggestion
            {
                Modifier = "Title",
                SuggestedValue = null,
                CodeSnippet = ".Title(\"descriptive chart title\")",
            },
            Context = ctx.BuildContext(canvas, GetSiblingTexts(ctx)),
        });
    }

    /// <summary>A11Y_CHART_002: Chart has no Description and auto-summary is empty.</summary>
    private static void CheckChartDescription(CanvasElement canvas, Charting.Accessibility.IChartAccessibilityData data,
        ScanContext ctx, List<A11yDiagnostic> findings)
    {
        // Has explicit description?
        if (!string.IsNullOrWhiteSpace(data.Description)) return;

        // Does the auto-summarizer produce a non-empty summary?
        var summary = Charting.Accessibility.ChartSummarizer.Summarize(data);
        if (data.Series.Count > 0)
            return;

        findings.Add(new A11yDiagnostic
        {
            Id = "A11Y_CHART_002",
            Severity = "warning",
            Message = "Chart has no description and auto-summary is empty — set .Description(\"...\") or provide data with labels",
            WcagCriterion = "1.1.1",
            ElementType = "CanvasElement (Chart)",
            AutomationId = GetAutomationId(canvas),
            ComponentType = ctx.CurrentComponent,
            Fix = new A11yFixSuggestion
            {
                Modifier = "Description",
                SuggestedValue = null,
                CodeSnippet = ".Description(\"what this chart shows\")",
            },
            Context = ctx.BuildContext(canvas, GetSiblingTexts(ctx)),
        });
    }

    /// <summary>A11Y_CHART_004: .ColorOnly() used — color is the sole series encoding.</summary>
    private static void CheckChartColorOnly(CanvasElement canvas, ScanContext ctx, List<A11yDiagnostic> findings)
    {
        if (!canvas.IsColorOnly) return;

        findings.Add(new A11yDiagnostic
        {
            Id = "A11Y_CHART_004",
            Severity = "warning",
            Message = "Chart uses .ColorOnly() — color is the sole series differentiator, which is inaccessible to colorblind users",
            WcagCriterion = "1.4.1",
            ElementType = "CanvasElement (Chart)",
            AutomationId = GetAutomationId(canvas),
            ComponentType = ctx.CurrentComponent,
            Fix = new A11yFixSuggestion
            {
                Modifier = "ColorOnly",
                SuggestedValue = "Remove .ColorOnly() or add .SeriesShapes(...)",
                CodeSnippet = "Remove .ColorOnly() to enable default shape+dash encoding",
            },
            Context = ctx.BuildContext(canvas, GetSiblingTexts(ctx)),
        });
    }

    /// <summary>A11Y_CHART_009: Custom palette fails pairwise WCAG 3:1 contrast.</summary>
    private static void CheckChartPaletteContrast(CanvasElement canvas, ScanContext ctx, List<A11yDiagnostic> findings)
    {
        if (canvas.CustomPalette is not { } palette) return;
        if (palette.Count < 2) return;

        for (int i = 0; i < palette.Count; i++)
        {
            for (int j = i + 1; j < palette.Count; j++)
            {
                double contrast = Charting.Accessibility.ChartPalette.ContrastRatio(palette[i], palette[j]);
                if (contrast < 3.0)
                {
                    // Generate hardened alternative
                    var hardenResult = Charting.Accessibility.ChartPalette.Harden(
                        Enumerable.Range(0, palette.Count).Select(k => palette[k]).ToArray());

                    findings.Add(new A11yDiagnostic
                    {
                        Id = "A11Y_CHART_009",
                        Severity = "warning",
                        Message = $"Custom palette: colors {i} and {j} have contrast ratio {contrast:F1}:1, below the 3:1 minimum",
                        WcagCriterion = "1.4.11",
                        ElementType = "CanvasElement (Chart)",
                        AutomationId = GetAutomationId(canvas),
                        ComponentType = ctx.CurrentComponent,
                        Fix = new A11yFixSuggestion
                        {
                            Modifier = "SeriesColors",
                            SuggestedValue = string.Join(", ", hardenResult.Palette.Colors.Select(c => c.ToHex())),
                            CodeSnippet = ".Palette(ChartPalette.OkabeIto) or use .SeriesColors() with the suggested values",
                        },
                        Context = ctx.BuildContext(canvas, GetSiblingTexts(ctx)),
                    });
                    return; // Report first failure only
                }
            }
        }
    }

    /// <summary>A11Y_CHART_010: Custom palette fails colorblind ΔE &lt; 10.</summary>
    private static void CheckChartPaletteColorblind(CanvasElement canvas, ScanContext ctx, List<A11yDiagnostic> findings)
    {
        if (canvas.CustomPalette is not { } palette) return;
        if (palette.Count < 2) return;

        for (int i = 0; i < palette.Count; i++)
        {
            for (int j = i + 1; j < palette.Count; j++)
            {
                double minDE = Charting.Accessibility.ChartPalette.MinColorblindDeltaE(palette[i], palette[j]);
                if (minDE < 10.0)
                {
                    var hardenResult = Charting.Accessibility.ChartPalette.Harden(
                        Enumerable.Range(0, palette.Count).Select(k => palette[k]).ToArray());

                    findings.Add(new A11yDiagnostic
                    {
                        Id = "A11Y_CHART_010",
                        Severity = "warning",
                        Message = $"Custom palette: colors {i} and {j} have ΔE {minDE:F1} under colorblind simulation, below the 10.0 minimum",
                        WcagCriterion = "1.4.1",
                        ElementType = "CanvasElement (Chart)",
                        AutomationId = GetAutomationId(canvas),
                        ComponentType = ctx.CurrentComponent,
                        Fix = new A11yFixSuggestion
                        {
                            Modifier = "SeriesColors",
                            SuggestedValue = string.Join(", ", hardenResult.Palette.Colors.Select(c => c.ToHex())),
                            CodeSnippet = ".Palette(ChartPalette.OkabeIto) or use hardened alternative",
                        },
                        Context = ctx.BuildContext(canvas, GetSiblingTexts(ctx)),
                    });
                    return;
                }
            }
        }
    }

    /// <summary>A11Y_CHART_011: Custom palette fails background contrast.</summary>
    private static void CheckChartPaletteBackground(CanvasElement canvas, ScanContext ctx, List<A11yDiagnostic> findings)
    {
        if (canvas.CustomPalette is not { } palette) return;

        var lightBg = new Charting.D3.D3Color(255, 255, 255);
        var darkBg = new Charting.D3.D3Color(32, 32, 32);

        for (int i = 0; i < palette.Count; i++)
        {
            double lightContrast = Charting.Accessibility.ChartPalette.ContrastRatio(palette[i], lightBg);
            double darkContrast = Charting.Accessibility.ChartPalette.ContrastRatio(palette[i], darkBg);

            if (lightContrast < 3.0 && darkContrast < 3.0)
            {
                var hardenResult = Charting.Accessibility.ChartPalette.Harden(
                    Enumerable.Range(0, palette.Count).Select(k => palette[k]).ToArray());

                findings.Add(new A11yDiagnostic
                {
                    Id = "A11Y_CHART_011",
                    Severity = "warning",
                    Message = $"Custom palette: color {i} fails background contrast on both light ({lightContrast:F1}:1) and dark ({darkContrast:F1}:1) backgrounds",
                    WcagCriterion = "1.4.11",
                    ElementType = "CanvasElement (Chart)",
                    AutomationId = GetAutomationId(canvas),
                    ComponentType = ctx.CurrentComponent,
                    Fix = new A11yFixSuggestion
                    {
                        Modifier = "SeriesColors",
                        SuggestedValue = string.Join(", ", hardenResult.Palette.Colors.Select(c => c.ToHex())),
                        CodeSnippet = "Adjust color lightness to ensure ≥3:1 contrast against chart backgrounds",
                    },
                    Context = ctx.BuildContext(canvas, GetSiblingTexts(ctx)),
                });
                return;
            }
        }
    }

    /// <summary>A11Y_CHART_012: .RawColors() escape hatch used — informational.</summary>
    private static void CheckChartRawColors(CanvasElement canvas, ScanContext ctx, List<A11yDiagnostic> findings)
    {
        if (!canvas.IsRawColors) return;

        findings.Add(new A11yDiagnostic
        {
            Id = "A11Y_CHART_012",
            Severity = "info",
            Message = "Chart uses .RawColors() — palette accessibility checks are bypassed",
            WcagCriterion = "1.4.1",
            ElementType = "CanvasElement (Chart)",
            AutomationId = GetAutomationId(canvas),
            ComponentType = ctx.CurrentComponent,
            Fix = new A11yFixSuggestion
            {
                Modifier = "RawColors",
                SuggestedValue = null,
                CodeSnippet = "Consider using .Palette(ChartPalette.OkabeIto) or .SeriesColors() for accessible colors",
            },
            Context = ctx.BuildContext(canvas, GetSiblingTexts(ctx)),
        });
    }

    /// <summary>A11Y_CHART_003: Chart is .Interactive() but keyboard navigation is disabled.</summary>
    private static void CheckChartInteractiveKeyboard(CanvasElement canvas, ScanContext ctx, List<A11yDiagnostic> findings)
    {
        if (!canvas.IsInteractive) return;
        if (!canvas.IsKeyboardDisabled) return;

        findings.Add(new A11yDiagnostic
        {
            Id = "A11Y_CHART_003",
            Severity = "warning",
            Message = "Interactive chart has keyboard navigation disabled — users who rely on keyboard cannot navigate data points",
            WcagCriterion = "2.1.1",
            ElementType = "CanvasElement (Chart)",
            AutomationId = GetAutomationId(canvas),
            ComponentType = ctx.CurrentComponent,
            Fix = new A11yFixSuggestion
            {
                Modifier = "DisableKeyboard",
                SuggestedValue = null,
                CodeSnippet = "Remove .DisableKeyboard() to enable keyboard navigation",
            },
            Context = ctx.BuildContext(canvas, GetSiblingTexts(ctx)),
        });
    }

    /// <summary>A11Y_CHART_005: .TightHitTest() on markers that may be smaller than 24px.</summary>
    private static void CheckChartTightHitTest(CanvasElement canvas, ScanContext ctx, List<A11yDiagnostic> findings)
    {
        if (!canvas.IsTightHitTest) return;

        findings.Add(new A11yDiagnostic
        {
            Id = "A11Y_CHART_005",
            Severity = "warning",
            Message = "Chart uses .TightHitTest() — point markers may have hit targets smaller than 24×24 CSS pixels",
            WcagCriterion = "2.5.8",
            ElementType = "CanvasElement (Chart)",
            AutomationId = GetAutomationId(canvas),
            ComponentType = ctx.CurrentComponent,
            Fix = new A11yFixSuggestion
            {
                Modifier = "TightHitTest",
                SuggestedValue = null,
                CodeSnippet = "Remove .TightHitTest() to enable automatic 24×24 hit target expansion",
            },
            Context = ctx.BuildContext(canvas, GetSiblingTexts(ctx)),
        });
    }

    /// <summary>A11Y_CHART_006: Custom focus indicator color has insufficient contrast (&lt; 3:1) against chart background.</summary>
    private static void CheckChartFocusIndicatorContrast(CanvasElement canvas, ScanContext ctx, List<A11yDiagnostic> findings)
    {
        if (canvas.CustomFocusColor is not { } focusColor) return;

        // Check contrast against both light and dark chart backgrounds
        var fc = new Charting.D3.D3Color(focusColor.R, focusColor.G, focusColor.B);
        var lightBg = new Charting.D3.D3Color(255, 255, 255);
        var darkBg = new Charting.D3.D3Color(30, 30, 30);

        double lightContrast = Charting.Accessibility.ChartPalette.ContrastRatio(fc, lightBg);
        double darkContrast = Charting.Accessibility.ChartPalette.ContrastRatio(fc, darkBg);

        // Fail if the custom color doesn't meet 3:1 against either background
        if (lightContrast >= 3.0 && darkContrast >= 3.0)
            return;

        findings.Add(new A11yDiagnostic
        {
            Id = "A11Y_CHART_006",
            Severity = "warning",
            Message = $"Custom focus indicator color has contrast ratio {Math.Min(lightContrast, darkContrast):F1}:1 — minimum 3:1 required (WCAG 2.4.13). Use the default double-ring focus indicator.",
            WcagCriterion = "2.4.13",
            ElementType = "CanvasElement (Chart)",
            AutomationId = GetAutomationId(canvas),
            ComponentType = ctx.CurrentComponent,
            Fix = new A11yFixSuggestion
            {
                Modifier = "FocusColor",
                SuggestedValue = null,
                CodeSnippet = "Remove .FocusColor(...) to use the default double-ring focus indicator",
            },
            Context = ctx.BuildContext(canvas, GetSiblingTexts(ctx)),
        });
    }

    /// <summary>A11Y_CHART_007: .AnnounceEveryFrame() floods the live region with rapid-fire announcements.</summary>
    private static void CheckChartAnnounceEveryFrame(CanvasElement canvas, ScanContext ctx, List<A11yDiagnostic> findings)
    {
        if (!canvas.IsAnnounceEveryFrame) return;

        findings.Add(new A11yDiagnostic
        {
            Id = "A11Y_CHART_007",
            Severity = "warning",
            Message = ".AnnounceEveryFrame() causes the chart to announce every animation frame — this floods the live region and overwhelms screen-reader users",
            WcagCriterion = "4.1.3",
            ElementType = "CanvasElement (Chart)",
            AutomationId = GetAutomationId(canvas),
            ComponentType = ctx.CurrentComponent,
            Fix = new A11yFixSuggestion
            {
                Modifier = "AnnounceEveryFrame",
                SuggestedValue = null,
                CodeSnippet = "Remove .AnnounceEveryFrame() — the chart's live region already debounces announcements to settled states",
            },
            Context = ctx.BuildContext(canvas, GetSiblingTexts(ctx)),
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
