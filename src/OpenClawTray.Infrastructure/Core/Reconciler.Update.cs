using OpenClawTray.Infrastructure.Animation;
using OpenClawTray.Infrastructure.Hosting;
using OpenClawTray.Infrastructure.Controls.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Imaging;
using WinUI = Microsoft.UI.Xaml.Controls;
using WinPrim = Microsoft.UI.Xaml.Controls.Primitives;
using WinShapes = Microsoft.UI.Xaml.Shapes;
using Windows.UI.WebUI;

namespace OpenClawTray.Infrastructure.Core;

// AI-HINT: Reconciler.Update.cs — patches existing WinUI controls to match new Elements.
// Update() diffs old vs new Element and mutates the existing control in-place.
// Critical optimization: Element.ShallowEquals short-circuits when nothing changed.
// Returns null if existing control was patched; returns a new UIElement if the
// control type changed (caller must swap). Each UpdateXxx method mirrors its
// MountXxx counterpart but only touches properties that differ.

public sealed partial class Reconciler
{
    /// <summary>
    /// Diffs oldEl vs newEl and patches the existing control. Returns null if patched in-place,
    /// or a replacement UIElement if the control type changed at runtime.
    /// </summary>
    private UIElement? Update(Element oldEl, Element newEl, UIElement control, Action requestRerender)
    {
        DebugElementsDiffed++;
        // Unwrap all layers of ModifiedElement, accumulating modifiers.
        // Inner modifiers override outer ones (via Merge: other wins where non-null).
        ElementModifiers? oldModifiers = oldEl.Modifiers;
        ElementModifiers? modifiers = newEl.Modifiers;
        while (oldEl is ModifiedElement oldMod && newEl is ModifiedElement newMod)
        {
            oldModifiers = oldModifiers is not null
                ? oldModifiers.Merge(oldMod.WrappedModifiers)
                : oldMod.WrappedModifiers;
            modifiers = modifiers is not null
                ? modifiers.Merge(newMod.WrappedModifiers)
                : newMod.WrappedModifiers;
            oldEl = oldMod.Inner;
            newEl = newMod.Inner;
        }
        // Merge any modifiers from the final inner element
        if (oldEl.Modifiers is not null)
            oldModifiers = oldModifiers is not null ? oldModifiers.Merge(oldEl.Modifiers) : oldEl.Modifiers;
        if (newEl.Modifiers is not null)
            modifiers = modifiers is not null ? modifiers.Merge(newEl.Modifiers) : newEl.Modifiers;

        // Short-circuit: if old and new elements are structurally identical,
        // skip all WinUI property access. This is the critical optimization for
        // large grids where only a fraction of elements change each frame.
        // Exception: elements with ThemeBindings must always re-apply because
        // the resolved brush value depends on the control's effective theme,
        // which can change independently of the element tree (e.g., parent
        // RequestedTheme toggle).
        if (Element.ShallowEquals(oldEl, newEl) && ReferenceEquals(oldModifiers, modifiers))
        {
            DebugElementsSkipped++;
            if (newEl.ThemeBindings is not null && control is FrameworkElement thFeSE)
                ApplyThemeBindings(thFeSE, newEl.ThemeBindings);
            // Re-resolve ThemeRef-based resource overrides on theme change
            if (newEl.ResourceOverrides is { ThemeRefs.Count: > 0 } && control is FrameworkElement resFeSE)
                ApplyResourceOverrides(resFeSE, newEl.ResourceOverrides, newEl.ResourceOverrides);
            return null; // null = keep existing control as-is
        }
        DebugUIElementsModified++;

        // Push context values onto scope before processing children
        var ctxValues = newEl.ContextValues;
        int ctxCount = 0;
        if (ctxValues is { Count: > 0 })
        {
            _contextScope.Push(ctxValues);
            ctxCount = ctxValues.Count;
        }

        UIElement? result;
        try
        {

        // Registered types checked first
        if (_typeRegistry.TryGetValue(newEl.GetType(), out var reg))
        {
            result = reg.Update(oldEl, newEl, control, requestRerender, this);
        }
        else
        {
        result = (oldEl, newEl, control) switch
        {
            (TextBlockElement o, TextBlockElement n, TextBlock tb)
                => EnableBitmaskDiff ? UpdateTextBitmask(o, n, tb) : UpdateText(n, tb),
            (RichTextBlockElement o, RichTextBlockElement n, WinUI.RichTextBlock rtb)
                => UpdateRichTextBlock(o, n, rtb),
            (ButtonElement o, ButtonElement n, WinUI.Button b)
                => UpdateButton(o, n, b, requestRerender),
            (HyperlinkButtonElement, HyperlinkButtonElement n, WinUI.HyperlinkButton hb)
                => UpdateHyperlinkButton(n, hb),
            (RepeatButtonElement, RepeatButtonElement n, WinPrim.RepeatButton rb)
                => UpdateRepeatButton(n, rb),
            (ToggleButtonElement, ToggleButtonElement n, WinPrim.ToggleButton tb)
                => UpdateToggleButton(n, tb),
            (DropDownButtonElement, DropDownButtonElement n, WinUI.DropDownButton ddb)
                => UpdateDropDownButton(n, ddb),
            (SplitButtonElement, SplitButtonElement n, WinUI.SplitButton sb)
                => UpdateSplitButton(n, sb),
            (ToggleSplitButtonElement, ToggleSplitButtonElement n, WinUI.ToggleSplitButton tsb)
                => UpdateToggleSplitButton(n, tsb),
            (RichEditBoxElement, RichEditBoxElement n, WinUI.RichEditBox reb)
                => UpdateRichEditBox(n, reb),
            (TextFieldElement o, TextFieldElement n, TextBox tb)
                => UpdateTextField(o, n, tb),
            (PasswordBoxElement, PasswordBoxElement n, WinUI.PasswordBox pb)
                => UpdatePasswordBox(n, pb),
            (NumberBoxElement, NumberBoxElement n, WinUI.NumberBox nb)
                => UpdateNumberBox(n, nb),
            (AutoSuggestBoxElement, AutoSuggestBoxElement n, WinUI.AutoSuggestBox asb)
                => UpdateAutoSuggestBox(n, asb),
            (CheckBoxElement, CheckBoxElement n, WinUI.CheckBox cb)
                => UpdateCheckBox(n, cb),
            (RadioButtonElement, RadioButtonElement n, WinUI.RadioButton rb)
                => UpdateRadioButton(n, rb),
            (RadioButtonsElement o, RadioButtonsElement n, WinUI.RadioButtons rbg)
                => UpdateRadioButtons(o, n, rbg),
            (ComboBoxElement o, ComboBoxElement n, WinUI.ComboBox cb)
                => UpdateComboBox(o, n, cb, requestRerender),
            (SliderElement, SliderElement n, WinUI.Slider s)
                => UpdateSlider(n, s),
            (ToggleSwitchElement, ToggleSwitchElement n, WinUI.ToggleSwitch ts)
                => UpdateToggleSwitch(n, ts),
            (RatingControlElement, RatingControlElement n, WinUI.RatingControl r)
                => UpdateRatingControl(n, r),
            (ColorPickerElement, ColorPickerElement n, WinUI.ColorPicker cp)
                => UpdateColorPicker(n, cp),
            (CalendarDatePickerElement, CalendarDatePickerElement n, WinUI.CalendarDatePicker cdp)
                => UpdateCalendarDatePicker(n, cdp),
            (DatePickerElement, DatePickerElement n, WinUI.DatePicker dp)
                => UpdateDatePicker(n, dp),
            (TimePickerElement, TimePickerElement n, WinUI.TimePicker tp)
                => UpdateTimePicker(n, tp),
            (ProgressElement, ProgressElement n, WinUI.ProgressBar pb)
                => UpdateProgress(n, pb),
            (ProgressRingElement, ProgressRingElement n, WinUI.ProgressRing pr)
                => UpdateProgressRing(n, pr),
            (ImageElement o, ImageElement n, WinUI.Image img)
                => UpdateImage(o, n, img),
            (PersonPictureElement, PersonPictureElement n, WinUI.PersonPicture pp)
                => UpdatePersonPicture(n, pp),
            (WebView2Element o, WebView2Element n, WinUI.WebView2 wv)
                => UpdateWebView2(o, n, wv),
            (WrapGridElement o, WrapGridElement n, WinUI.VariableSizedWrapGrid wg)
                => UpdateWrapGrid(o, n, wg, requestRerender),
            (StackElement o, StackElement n, WinUI.StackPanel sp)
                => UpdateStack(o, n, sp, requestRerender),
            (ScrollViewElement o, ScrollViewElement n, WinUI.ScrollViewer sv)
                => UpdateScrollView(o, n, sv, newEl, requestRerender),
            (BorderElement o, BorderElement n, WinUI.Border b)
                => UpdateBorder(o, n, b, newEl, requestRerender),
            (ViewboxElement o, ViewboxElement n, WinUI.Viewbox vb)
                => UpdateViewbox(o, n, vb, requestRerender),
            (ExpanderElement o, ExpanderElement n, WinUI.Expander exp)
                => UpdateExpander(o, n, exp, requestRerender),
            (SplitViewElement o, SplitViewElement n, WinUI.SplitView sv)
                => UpdateSplitView(o, n, sv, requestRerender),
            (NavigationHostElement o, NavigationHostElement n, WinUI.Grid navGrid)
                => UpdateNavigationHost(o, n, navGrid, requestRerender),
            (NavigationViewElement o, NavigationViewElement n, WinUI.NavigationView nv)
                => UpdateNavigationView(o, n, nv, requestRerender),
            (TitleBarElement o, TitleBarElement n, WinUI.TitleBar tb)
                => UpdateTitleBar(o, n, tb, requestRerender),
            (TabViewElement o, TabViewElement n, WinUI.TabView tabView)
                => UpdateTabView(o, n, tabView, requestRerender),
            (BreadcrumbBarElement, BreadcrumbBarElement n, WinUI.BreadcrumbBar bcb)
                => UpdateBreadcrumbBar(n, bcb),
            (PivotElement o, PivotElement n, WinUI.Pivot pivot)
                => UpdatePivot(o, n, pivot, requestRerender),
            (ListViewElement o, ListViewElement n, WinUI.ListView lv)
                => UpdateListView(o, n, lv, requestRerender),
            (GridViewElement o, GridViewElement n, WinUI.GridView gv)
                => UpdateGridView(o, n, gv, requestRerender),
            (TreeViewElement o, TreeViewElement n, WinUI.TreeView tv)
                => UpdateTreeView(o, n, tv, requestRerender),
            (FlipViewElement o, FlipViewElement n, WinUI.FlipView fv)
                => UpdateFlipView(o, n, fv, requestRerender),
            (InfoBarElement, InfoBarElement n, WinUI.InfoBar ib)
                => UpdateInfoBar(n, ib),
            (InfoBadgeElement, InfoBadgeElement n, WinUI.InfoBadge badge)
                => UpdateInfoBadge(n, badge),
            (ContentDialogElement o, ContentDialogElement n, FrameworkElement cdFe)
                => UpdateContentDialog(o, n, cdFe, requestRerender),
            (TeachingTipElement, TeachingTipElement n, WinUI.TeachingTip tip)
                => UpdateTeachingTip(n, tip),
            (MenuBarElement o, MenuBarElement n, WinUI.MenuBar mb)
                => UpdateMenuBar(o, n, mb),
            (CommandHostElement o, CommandHostElement n, WinUI.Grid chGrid)
                => UpdateCommandHost(o, n, chGrid, requestRerender),
            (CommandBarElement o, CommandBarElement n, WinUI.CommandBar cb)
                => UpdateCommandBar(o, n, cb, requestRerender),
            (Core.GridElement o, Core.GridElement n, WinUI.Grid g)
                => UpdateGrid(o, n, g, requestRerender),
            (CanvasElement o, CanvasElement n, WinUI.Canvas cvs)
                => UpdateCanvas(o, n, cvs, requestRerender),
            (TemplatedListElementBase o, TemplatedListElementBase n, WinUI.ListView lv)
                => UpdateTemplatedListView(o, n, lv, requestRerender),
            (TemplatedListElementBase o, TemplatedListElementBase n, WinUI.GridView gv)
                => UpdateTemplatedGridView(o, n, gv, requestRerender),
            (TemplatedListElementBase o, TemplatedListElementBase n, WinUI.FlipView fv)
                => UpdateTemplatedFlipView(o, n, fv, requestRerender),
            (LazyStackElementBase, LazyStackElementBase n, WinUI.ScrollViewer sv)
                => UpdateLazyStack(n, sv, requestRerender),
            (RectangleElement, RectangleElement n, WinShapes.Rectangle r)
                => UpdateRectangle(n, r),
            (EllipseElement, EllipseElement n, WinShapes.Ellipse e)
                => UpdateEllipse(n, e),
            (LineElement, LineElement n, WinShapes.Line l)
                => UpdateLine(n, l),
            (PathElement o, PathElement n, WinShapes.Path p)
                => UpdatePath(o, n, p),
            (RelativePanelElement o, RelativePanelElement n, WinUI.RelativePanel rp)
                => UpdateRelativePanel(o, n, rp, requestRerender),
            (MediaPlayerElementElement, MediaPlayerElementElement n, WinUI.MediaPlayerElement mpe)
                => UpdateMediaPlayerElement(n, mpe),
            (AnimatedVisualPlayerElement, AnimatedVisualPlayerElement n, WinUI.AnimatedVisualPlayer avp)
                => UpdateAnimatedVisualPlayer(n, avp),
            (SemanticZoomElement o, SemanticZoomElement n, WinUI.SemanticZoom sz)
                => UpdateSemanticZoom(o, n, sz, requestRerender),
            (ListBoxElement o, ListBoxElement n, WinUI.ListBox lb)
                => UpdateListBox(o, n, lb),
            (SelectorBarElement o, SelectorBarElement n, WinUI.SelectorBar sbar)
                => UpdateSelectorBar(o, n, sbar),
            (PipsPagerElement, PipsPagerElement n, WinUI.PipsPager pp)
                => UpdatePipsPager(n, pp),
            (AnnotatedScrollBarElement, AnnotatedScrollBarElement n, WinUI.AnnotatedScrollBar asb)
                => UpdateAnnotatedScrollBar(n, asb),
            (PopupElement o, PopupElement n, WinUI.StackPanel popupWrap)
                => UpdatePopup(o, n, popupWrap, requestRerender),
            (RefreshContainerElement o, RefreshContainerElement n, WinUI.RefreshContainer rc)
                => UpdateRefreshContainer(o, n, rc, requestRerender),
            (MenuFlyoutElement o, MenuFlyoutElement n, UIElement mfTarget)
                => UpdateMenuFlyout(o, n, mfTarget, requestRerender),
            (FlyoutElement o, FlyoutElement n, UIElement flyTarget)
                => UpdateFlyoutElement(o, n, flyTarget, requestRerender),
            (CommandBarFlyoutElement o, CommandBarFlyoutElement n, UIElement cbfTarget)
                => UpdateCommandBarFlyout(o, n, cbfTarget, requestRerender),
            (CalendarViewElement, CalendarViewElement n, WinUI.CalendarView cv)
                => UpdateCalendarView(n, cv),
            (SwipeControlElement o, SwipeControlElement n, WinUI.SwipeControl swipe)
                => UpdateSwipeControl(o, n, swipe, requestRerender),
            (AnimatedIconElement, AnimatedIconElement n, WinUI.AnimatedIcon ai)
                => UpdateAnimatedIcon(n, ai),
            (ParallaxViewElement o, ParallaxViewElement n, WinUI.ParallaxView pv)
                => UpdateParallaxView(o, n, pv, requestRerender),
            (MapControlElement, MapControlElement n, WinUI.MapControl mc)
                => UpdateMapControl(n, mc),
            (FrameElement, FrameElement n, WinUI.Frame f)
                => UpdateFrame(n, f),
            (ErrorBoundaryElement oldEb, ErrorBoundaryElement newEb, Border)
                => UpdateErrorBoundary(oldEb, newEb, control, requestRerender),
            (FormFieldElement oldFf, FormFieldElement newFf, WinUI.StackPanel sp)
                => UpdateFormField(oldFf, newFf, sp, requestRerender),
            (ValidationVisualizerElement oldVv, ValidationVisualizerElement newVv, WinUI.StackPanel sp)
                => UpdateValidationVisualizer(oldVv, newVv, sp, requestRerender),
            (ValidationRuleElement, ValidationRuleElement n, WinUI.StackPanel)
                => UpdateValidationRule(n),
            (SemanticElement oldSem, SemanticElement newSem, Accessibility.SemanticPanel sp)
                => UpdateSemantic(oldSem, newSem, sp, requestRerender),
            (Hooks.AnnounceRegionElement, Hooks.AnnounceRegionElement, TextBlock)
                => null, // static element — nothing to update
            (XamlHostElement, XamlHostElement n, FrameworkElement hostCtrl)
                => UpdateXamlHost(n, hostCtrl),
            (XamlPageElement o, XamlPageElement n, WinUI.Frame f)
                => UpdateXamlPage(o, n, f),
            (ComponentElement, ComponentElement, _)
                => UpdateComponent(oldEl, newEl, control, requestRerender),
            (FuncElement, FuncElement, _)
                => UpdateComponent(oldEl, newEl, control, requestRerender),
            (MemoElement, MemoElement, _)
                => UpdateComponent(oldEl, newEl, control, requestRerender),
            _ => Mount(newEl, requestRerender),
        };
        }

        // Apply inline modifiers after update. When old modifiers existed but new
        // modifiers are null, pass an empty instance so ApplyModifiers can clear
        // stale values (same principle as the flex attached-property fix).
        var target = result ?? control;

        // Record the control for highlight overlay only when the element's own
        // WinUI properties were actually updated (not just children recursed).
        // Containers whose only change is children references are excluded — the
        // individual children will be captured if they change.
        if (result is null && _highlightModified is not null
            && (!Element.OwnPropsEqual(oldEl, newEl) || !ReferenceEquals(oldModifiers, modifiers)))
            _highlightModified.Add(control);
        if ((modifiers is not null || oldModifiers is not null) && target is FrameworkElement fe)
            ApplyModifiers(fe, oldModifiers, modifiers ?? new ElementModifiers(), requestRerender);

        // Re-apply the caption-derived default after modifiers have run so a
        // label change ("+ 1" → "+ 2") updates UIA Name when the author never
        // set an explicit name. No-ops when the author did.
        if (target is FrameworkElement captionFe)
            UpdateDefaultAutomationName(
                captionFe,
                ResolveCaptionForElement(oldEl),
                ResolveCaptionForElement(newEl));

        // Apply theme-resource bindings (ThemeRef → resolved Brush from WinUI resources)
        if (newEl.ThemeBindings is not null && target is FrameworkElement thFe)
            ApplyThemeBindings(thFe, newEl.ThemeBindings);

        // Apply per-control resource overrides (lightweight styling)
        if ((newEl.ResourceOverrides is not null || oldEl.ResourceOverrides is not null) && target is FrameworkElement resFe)
            ApplyResourceOverrides(resFe, oldEl.ResourceOverrides, newEl.ResourceOverrides);

        // Apply transitions after update (re-applies when transition config changes)
        if (newEl.ImplicitTransitions is not null || newEl.ThemeTransitions is not null)
            ApplyTransitions(target, newEl.ImplicitTransitions, newEl.ThemeTransitions);

        // Apply or clear Composition-layer layout animation
        if (newEl.LayoutAnimation is not null)
            ApplyLayoutAnimation(target, newEl.LayoutAnimation);
        else if (oldEl.LayoutAnimation is not null)
            ClearLayoutAnimation(target);

        // Apply or clear compositor property animation (.Animate() modifier)
        if (newEl.AnimationConfig is not null)
            ApplyPropertyAnimation(target, newEl.AnimationConfig, newEl.LayoutAnimation);
        else if (oldEl.AnimationConfig is not null)
            ClearPropertyAnimation(target, newEl.LayoutAnimation);

        // Apply or clear interaction states (.InteractionStates() modifier)
        if (newEl.InteractionStates is not null)
            ApplyInteractionStates(target, newEl.InteractionStates);
        else if (oldEl.InteractionStates is not null)
            ClearInteractionStates(target);

        // Apply keyframe animations (.Keyframes() modifier)
        if (newEl.KeyframeAnimations is not null)
            ApplyKeyframeAnimations(target, newEl.KeyframeAnimations);
        else if (oldEl.KeyframeAnimations is not null)
            ClearKeyframeAnimations(target, oldEl.KeyframeAnimations);

        // Apply or clear scroll-linked expression animations (.ScrollLinked() modifier)
        if (newEl.ScrollAnimation is not null)
            ApplyScrollAnimation(target, newEl.ScrollAnimation);
        else if (oldEl.ScrollAnimation is not null)
            ClearScrollAnimation(target, oldEl.ScrollAnimation);

        // Apply stagger delays to children (.Stagger() modifier)
        if (newEl.StaggerConfig is not null)
            ApplyStaggerDelays(target, newEl.StaggerConfig);

        }
        finally
        {
            if (ctxCount > 0)
                _contextScope.Pop(ctxCount);
        }

        return result;
    }

    private UIElement? UpdateText(TextBlockElement n, TextBlock tb)
    {
        if (tb.Text != n.Content) tb.Text = n.Content;
        if (n.FontSize.HasValue && tb.FontSize != n.FontSize.Value) tb.FontSize = n.FontSize.Value;
        if (n.Weight.HasValue && tb.FontWeight.Weight != n.Weight.Value.Weight) tb.FontWeight = n.Weight.Value;
        if (n.FontStyle.HasValue && tb.FontStyle != n.FontStyle.Value) tb.FontStyle = n.FontStyle.Value;
        if (n.HorizontalAlignment.HasValue && tb.HorizontalAlignment != n.HorizontalAlignment.Value) tb.HorizontalAlignment = n.HorizontalAlignment.Value;
        if (n.TextWrapping.HasValue && tb.TextWrapping != n.TextWrapping.Value) tb.TextWrapping = n.TextWrapping.Value;
        if (n.TextAlignment.HasValue && tb.TextAlignment != n.TextAlignment.Value) tb.TextAlignment = n.TextAlignment.Value;
        if (n.TextTrimming.HasValue && tb.TextTrimming != n.TextTrimming.Value) tb.TextTrimming = n.TextTrimming.Value;
        if (n.IsTextSelectionEnabled.HasValue && tb.IsTextSelectionEnabled != n.IsTextSelectionEnabled.Value) tb.IsTextSelectionEnabled = n.IsTextSelectionEnabled.Value;
        if (n.FontFamily is not null && tb.FontFamily != n.FontFamily) tb.FontFamily = n.FontFamily;
        ApplySetters(n.Setters, tb);
        return null;
    }

    /// <summary>
    /// EXP-2: Bitmask-based UpdateText — compares old vs new TextBlockElement (pure C#)
    /// to determine which properties changed, then only touches those WinUI properties.
    /// Avoids COM interop reads for unchanged properties.
    /// </summary>
    private UIElement? UpdateTextBitmask(TextBlockElement old, TextBlockElement n, TextBlock tb)
    {
        var diff = TextBlockElement.DiffProps(old, n);
        if (diff == TextPropChanged.None) return null;

        if ((diff & TextPropChanged.Content) != 0) tb.Text = n.Content;
        if ((diff & TextPropChanged.FontSize) != 0 && n.FontSize.HasValue) tb.FontSize = n.FontSize.Value;
        if ((diff & TextPropChanged.Weight) != 0 && n.Weight.HasValue) tb.FontWeight = n.Weight.Value;
        if ((diff & TextPropChanged.FontStyle) != 0 && n.FontStyle.HasValue) tb.FontStyle = n.FontStyle.Value;
        if ((diff & TextPropChanged.HorizontalAlignment) != 0 && n.HorizontalAlignment.HasValue) tb.HorizontalAlignment = n.HorizontalAlignment.Value;
        if ((diff & TextPropChanged.TextWrapping) != 0 && n.TextWrapping.HasValue) tb.TextWrapping = n.TextWrapping.Value;
        if ((diff & TextPropChanged.TextAlignment) != 0 && n.TextAlignment.HasValue) tb.TextAlignment = n.TextAlignment.Value;
        if ((diff & TextPropChanged.TextTrimming) != 0 && n.TextTrimming.HasValue) tb.TextTrimming = n.TextTrimming.Value;
        if ((diff & TextPropChanged.IsTextSelectionEnabled) != 0 && n.IsTextSelectionEnabled.HasValue) tb.IsTextSelectionEnabled = n.IsTextSelectionEnabled.Value;
        if ((diff & TextPropChanged.FontFamily) != 0 && n.FontFamily is not null) tb.FontFamily = n.FontFamily;
        if ((diff & TextPropChanged.Setters) != 0) ApplySetters(n.Setters, tb);
        return null;
    }

    private UIElement? UpdateRichTextBlock(RichTextBlockElement o, RichTextBlockElement n, WinUI.RichTextBlock rtb)
    {
        rtb.IsTextSelectionEnabled = n.IsTextSelectionEnabled;
        if (n.FontSize.HasValue) rtb.FontSize = n.FontSize.Value;

        var oldParas = o.Paragraphs;
        var newParas = n.Paragraphs;

        // Both use simple text (no Paragraphs) — fast path.
        if (oldParas is null && newParas is null)
        {
            if (o.Text != n.Text)
            {
                // Cache the WinRT collection reference to avoid repeated interop calls.
                var blocks = rtb.Blocks;
                if (blocks.Count > 0 &&
                    blocks[0] is Microsoft.UI.Xaml.Documents.Paragraph p0)
                {
                    var inlines = p0.Inlines;
                    if (inlines.Count > 0 && inlines[0] is Microsoft.UI.Xaml.Documents.Run r0)
                        r0.Text = n.Text;
                }
            }
            ApplySetters(n.Setters, rtb);
            return null;
        }

        // Structural mismatch (one has Paragraphs, other doesn't) — full rebuild.
        if (oldParas is null || newParas is null)
        {
            RebuildRichTextBlocks(n, rtb);
            ApplySetters(n.Setters, rtb);
            return null;
        }

        // Both have Paragraphs — diff incrementally.
        int oldCount = oldParas.Length;
        int newCount = newParas.Length;
        int commonCount = Math.Min(oldCount, newCount);

        // Cache the WinRT Blocks collection to avoid repeated interop calls.
        var rtbBlocks = rtb.Blocks;

        // Update existing paragraphs in place.
        for (int pi = 0; pi < commonCount; pi++)
        {
            var oldPara = oldParas[pi];
            var newPara = newParas[pi];

            // Skip paragraphs whose content is structurally identical.
            if (Element.ParagraphEqual(oldPara, newPara)) continue;

            if (rtbBlocks.Count <= pi) break;
            var winPara = (Microsoft.UI.Xaml.Documents.Paragraph)rtbBlocks[pi];

            DiffParagraphInlines(oldPara, newPara, winPara);
        }

        // Remove excess paragraphs.
        while (rtbBlocks.Count > newCount)
            rtbBlocks.RemoveAt(rtbBlocks.Count - 1);

        // Add new paragraphs.
        for (int pi = oldCount; pi < newCount; pi++)
            rtbBlocks.Add(MountParagraph(newParas[pi]));

        ApplySetters(n.Setters, rtb);
        return null;
    }

    private static void DiffParagraphInlines(RichTextParagraph oldPara, RichTextParagraph newPara,
        Microsoft.UI.Xaml.Documents.Paragraph winPara)
    {
        var oldInlines = oldPara.Inlines;
        var newInlines = newPara.Inlines;
        int oldCount = oldInlines.Length;
        int newCount = newInlines.Length;
        int commonCount = Math.Min(oldCount, newCount);

        // Cache the WinRT InlineCollection once — each .Inlines access is a managed→WinRT
        // interop call, and each indexed get (winInlines[i]) is another. For documents with
        // hundreds of inlines this was the dominant cost in the profile (~14% self CPU).
        var winInlines = winPara.Inlines;

        // Update existing inlines in place where types match.
        for (int i = 0; i < commonCount; i++)
        {
            var oldInl = oldInlines[i];
            var newInl = newInlines[i];

            // Skip inlines that are record-equal (no changes).
            if (oldInl == newInl) continue;

            if (oldInl.GetType() != newInl.GetType())
            {
                // Type changed — replace this inline.
                winInlines.RemoveAt(i);
                winInlines.Insert(i, MountInline(newInl));
                continue;
            }

            var winInline = winInlines[i];
            switch (newInl)
            {
                case RichTextRun newRun:
                    if (winInline is Microsoft.UI.Xaml.Documents.Run winRun)
                        UpdateRun((RichTextRun)oldInl, newRun, winRun);
                    break;
                case RichTextHyperlink newLink:
                    if (winInline is Microsoft.UI.Xaml.Documents.Hyperlink winHl)
                        UpdateHyperlink((RichTextHyperlink)oldInl, newLink, winHl);
                    break;
                case RichTextLineBreak:
                    break;
            }
        }

        // Remove excess inlines.
        while (winInlines.Count > newCount)
            winInlines.RemoveAt(winInlines.Count - 1);

        // Add new inlines.
        for (int i = oldCount; i < newCount; i++)
            winInlines.Add(MountInline(newInlines[i]));
    }

    private static void UpdateRun(RichTextRun oldRun, RichTextRun newRun,
        Microsoft.UI.Xaml.Documents.Run winRun)
    {
        if (oldRun.Text != newRun.Text)
            winRun.Text = newRun.Text;
        if (oldRun.IsBold != newRun.IsBold)
            winRun.FontWeight = newRun.IsBold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        if (oldRun.IsItalic != newRun.IsItalic)
            winRun.FontStyle = newRun.IsItalic ? global::Windows.UI.Text.FontStyle.Italic : global::Windows.UI.Text.FontStyle.Normal;
        if (oldRun.IsStrikethrough != newRun.IsStrikethrough)
            winRun.TextDecorations = newRun.IsStrikethrough ? global::Windows.UI.Text.TextDecorations.Strikethrough : global::Windows.UI.Text.TextDecorations.None;
        if (oldRun.FontSize != newRun.FontSize)
            winRun.FontSize = newRun.FontSize ?? (double)Microsoft.UI.Xaml.DependencyProperty.UnsetValue;
        if (oldRun.FontFamily != newRun.FontFamily)
        {
            if (newRun.FontFamily is not null)
                winRun.FontFamily = WinRTCache.GetFontFamily(newRun.FontFamily);
            else
                winRun.ClearValue(Microsoft.UI.Xaml.Documents.TextElement.FontFamilyProperty);
        }
        if (!ReferenceEquals(oldRun.Foreground, newRun.Foreground))
            winRun.Foreground = newRun.Foreground;
    }

    private static void UpdateHyperlink(RichTextHyperlink oldLink, RichTextHyperlink newLink,
        Microsoft.UI.Xaml.Documents.Hyperlink winHl)
    {
        if (oldLink.NavigateUri != newLink.NavigateUri)
        {
            try { winHl.NavigateUri = newLink.NavigateUri; }
            catch (Exception) { winHl.NavigateUri = new Uri("about:error"); }
            
        }
        if (oldLink.Text != newLink.Text && winHl.Inlines.Count > 0 &&
            winHl.Inlines[0] is Microsoft.UI.Xaml.Documents.Run hlRun)
            hlRun.Text = newLink.Text;
    }

    private static Microsoft.UI.Xaml.Documents.Inline MountInline(RichTextInline inline)
    {
        switch (inline)
        {
            case RichTextRun run:
                var r = new Microsoft.UI.Xaml.Documents.Run { Text = run.Text };
                if (run.IsBold) r.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                if (run.IsItalic) r.FontStyle = global::Windows.UI.Text.FontStyle.Italic;
                if (run.IsStrikethrough) r.TextDecorations = global::Windows.UI.Text.TextDecorations.Strikethrough;
                if (run.FontSize.HasValue) r.FontSize = run.FontSize.Value;
                if (run.FontFamily is not null) r.FontFamily = WinRTCache.GetFontFamily(run.FontFamily);
                if (run.Foreground is not null) r.Foreground = run.Foreground;
                return r;
            case RichTextHyperlink link:
                var l = link?.NavigateUri ?? new Uri("about:blank");
                l = l.ToString().Length < 1 ? l = new Uri("about:blank") : l;
                var hl = new Microsoft.UI.Xaml.Documents.Hyperlink();
                try { hl.NavigateUri = l; } catch { hl.NavigateUri = new Uri("about:blank"); }
                hl.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = link?.Text ?? ""});
                return hl;
            case RichTextLineBreak:
                return new Microsoft.UI.Xaml.Documents.LineBreak();
            default:
                return new Microsoft.UI.Xaml.Documents.Run { Text = "" };
        }
    }

    private static Microsoft.UI.Xaml.Documents.Paragraph MountParagraph(RichTextParagraph para)
    {
        var p = new Microsoft.UI.Xaml.Documents.Paragraph();
        foreach (var inline in para.Inlines)
            p.Inlines.Add(MountInline(inline));
        return p;
    }

    private static void RebuildRichTextBlocks(RichTextBlockElement n, WinUI.RichTextBlock rtb)
    {
        rtb.Blocks.Clear();
        if (n.Paragraphs is not null)
        {
            foreach (var para in n.Paragraphs)
                rtb.Blocks.Add(MountParagraph(para));
        }
        else
        {
            var p = new Microsoft.UI.Xaml.Documents.Paragraph();
            p.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = n.Text });
            rtb.Blocks.Add(p);
        }
    }

    private UIElement? UpdateButton(ButtonElement o, ButtonElement n, WinUI.Button b, Action requestRerender)
    {
        b.IsEnabled = n.IsEnabled;
        if (n.ContentElement is not null && o.ContentElement is not null && b.Content is UIElement existingContent)
        {
            var replacement = UpdateChild(o.ContentElement, n.ContentElement, existingContent, requestRerender);
            if (replacement is not null)
            {
                UnmountChild(existingContent);
                b.Content = replacement;
            }
        }
        else if (n.ContentElement is not null)
        {
            if (b.Content is UIElement oldContent) UnmountChild(oldContent);
            b.Content = Mount(n.ContentElement, requestRerender);
        }
        else
        {
            b.Content = n.Label;
        }
        SetElementTag(b, n);
        ApplySetters(n.Setters, b);
        return null;
    }

    private UIElement? UpdateHyperlinkButton(HyperlinkButtonElement n, WinUI.HyperlinkButton hb)
    {
        hb.Content = n.Content;
        if (n.NavigateUri is not null) hb.NavigateUri = n.NavigateUri;
        SetElementTag(hb, n);
        ApplySetters(n.Setters, hb);
        return null;
    }

    private UIElement? UpdateRepeatButton(RepeatButtonElement n, WinPrim.RepeatButton rb)
    {
        rb.Content = n.Label; rb.Delay = n.Delay; rb.Interval = n.Interval; SetElementTag(rb, n);
        ApplySetters(n.Setters, rb);
        return null;
    }

    private UIElement? UpdateToggleButton(ToggleButtonElement n, WinPrim.ToggleButton tb)
    {
        tb.Content = n.Label;
        if ((tb.IsChecked ?? false) != n.IsChecked) tb.IsChecked = n.IsChecked;
        SetElementTag(tb, n);
        ApplySetters(n.Setters, tb);
        return null;
    }

    private UIElement? UpdateDropDownButton(DropDownButtonElement n, WinUI.DropDownButton ddb)
    {
        if (ddb.Content as string != n.Label) ddb.Content = n.Label;
        SetElementTag(ddb, n);
        ApplySetters(n.Setters, ddb);
        return null;
    }

    private UIElement? UpdateSplitButton(SplitButtonElement n, WinUI.SplitButton sb)
    {
        sb.Content = n.Label; SetElementTag(sb, n);
        ApplySetters(n.Setters, sb);
        return null;
    }

    private UIElement? UpdateToggleSplitButton(ToggleSplitButtonElement n, WinUI.ToggleSplitButton tsb)
    {
        SetElementTag(tsb, n);
        tsb.Content = n.Label;
        if (tsb.IsChecked != n.IsChecked)
        {
            ChangeEchoSuppressor.BeginSuppress(tsb);
            tsb.IsChecked = n.IsChecked;
        }
        ApplySetters(n.Setters, tsb);
        return null;
    }

    private UIElement? UpdateTextField(TextFieldElement o, TextFieldElement n, TextBox tb)
    {
        // Tag first so any echoed TextChanged sees this element.
        SetElementTag(tb, n);
        if (o.Value != n.Value)
        {
            // Element value changed — always enforce
            if (tb.Text != n.Value)
            {
                ChangeEchoSuppressor.BeginSuppress(tb);
                tb.Text = n.Value;
            }
        }
        else if (n.OnChanged is not null && tb.Text != n.Value)
        {
            // Controlled mode (onChange wired): snap back filtered/rejected input.
            // The TextBox text diverges from the controlled value because the
            // callback filtered it to the same state (e.g. digits-only rejecting alpha).
            var caret = tb.SelectionStart;
            ChangeEchoSuppressor.BeginSuppress(tb);
            tb.Text = n.Value;
            tb.SelectionStart = Math.Min(caret, tb.Text.Length);
        }
        else if (n.OnChanged is null && tb.Text != n.Value)
        {
            // Uncontrolled divergence: value is set but no onChange to reconcile.
            // Log once per field to help developers catch mismatched bindings.
            _logger.LogWarning(
                "TextField value diverged from controlled value with no OnChanged handler. " +
                "Controlled: \"{ControlledValue}\", Actual: \"{ActualValue}\". " +
                "Wire up OnChanged to keep state in sync, or this field won't reflect user edits after re-renders.",
                Truncate(n.Value, 20), Truncate(tb.Text, 20));
        }
        tb.PlaceholderText = n.Placeholder ?? "";
        if (n.Header is not null) tb.Header = n.Header;
        if (n.IsReadOnly.HasValue) tb.IsReadOnly = n.IsReadOnly.Value;
        if (n.AcceptsReturn.HasValue) tb.AcceptsReturn = n.AcceptsReturn.Value;
        if (n.TextWrapping.HasValue) tb.TextWrapping = n.TextWrapping.Value;
        // Apply selection position after text — must come after Text is set so the range is valid
        if (n.SelectionStart.HasValue) tb.SelectionStart = Math.Min(n.SelectionStart.Value, tb.Text.Length);
        if (n.SelectionLength.HasValue) tb.SelectionLength = Math.Min(n.SelectionLength.Value, tb.Text.Length - tb.SelectionStart);
        ApplySetters(n.Setters, tb);
        return null;
    }

    private UIElement? UpdatePasswordBox(PasswordBoxElement n, WinUI.PasswordBox pb)
    {
        SetElementTag(pb, n);
        if (pb.Password != n.Password)
        {
            ChangeEchoSuppressor.BeginSuppress(pb);
            pb.Password = n.Password;
        }
        pb.PlaceholderText = n.PlaceholderText ?? "";
        ApplySetters(n.Setters, pb);
        return null;
    }

    private UIElement? UpdateNumberBox(NumberBoxElement n, WinUI.NumberBox nb)
    {
        SetElementTag(nb, n);
        // Set Min/Max before Value so a new, in-range Value doesn't get
        // coerced by a stale range. But Min/Max writes can themselves coerce
        // the existing Value, which raises ValueChanged — suppress those
        // echoes too, one token per write that might fire.
        if (nb.Minimum != n.Minimum)
        {
            if (nb.Value < n.Minimum) ChangeEchoSuppressor.BeginSuppress(nb);
            nb.Minimum = n.Minimum;
        }
        if (nb.Maximum != n.Maximum)
        {
            if (nb.Value > n.Maximum) ChangeEchoSuppressor.BeginSuppress(nb);
            nb.Maximum = n.Maximum;
        }
        if (nb.Value != n.Value)
        {
            ChangeEchoSuppressor.BeginSuppress(nb);
            nb.Value = n.Value;
        }
        nb.SmallChange = n.SmallChange; nb.LargeChange = n.LargeChange;
        nb.SpinButtonPlacementMode = n.SpinButtonPlacement;
        if (n.Header is not null) nb.Header = n.Header;
        ApplySetters(n.Setters, nb);
        return null;
    }

    private UIElement? UpdateAutoSuggestBox(AutoSuggestBoxElement n, WinUI.AutoSuggestBox asb)
    {
        // AutoSuggestBox already filters TextChanged to UserInput only, so
        // programmatic Text= is already safe. Suppress anyway for consistency
        // with the other editors (covers future handler changes).
        SetElementTag(asb, n);
        if (asb.Text != n.Text)
        {
            ChangeEchoSuppressor.BeginSuppress(asb);
            asb.Text = n.Text;
        }
        asb.PlaceholderText = n.PlaceholderText ?? "";
        if (n.Suggestions.Length > 0) asb.ItemsSource = n.Suggestions;
        ApplySetters(n.Setters, asb);
        return null;
    }

    private UIElement? UpdateCheckBox(CheckBoxElement n, WinUI.CheckBox cb)
    {
        SetElementTag(cb, n);
        cb.Content = n.Label;
        cb.IsThreeState = n.IsThreeState;
        var target = n.IsThreeState ? n.CheckedState : n.IsChecked;
        if (cb.IsChecked != target)
        {
            ChangeEchoSuppressor.BeginSuppress(cb);
            cb.IsChecked = target;
        }
        ApplySetters(n.Setters, cb);
        return null;
    }

    private UIElement? UpdateRadioButton(RadioButtonElement n, WinUI.RadioButton rb)
    {
        SetElementTag(rb, n);
        rb.Content = n.Label;
        if (rb.IsChecked != n.IsChecked)
        {
            ChangeEchoSuppressor.BeginSuppress(rb);
            rb.IsChecked = n.IsChecked;
        }
        if (n.GroupName is not null) rb.GroupName = n.GroupName;
        ApplySetters(n.Setters, rb);
        return null;
    }

    private UIElement? UpdateSlider(SliderElement n, WinUI.Slider s)
    {
        SetElementTag(s, n);
        // Min/Max before Value so a new, in-range Value doesn't get coerced
        // by a stale range. But Min/Max writes can themselves coerce the
        // existing Value, which raises ValueChanged — suppress those echoes
        // too, one token per write that might fire.
        if (s.Minimum != n.Min)
        {
            if (s.Value < n.Min) ChangeEchoSuppressor.BeginSuppress(s);
            s.Minimum = n.Min;
        }
        if (s.Maximum != n.Max)
        {
            if (s.Value > n.Max) ChangeEchoSuppressor.BeginSuppress(s);
            s.Maximum = n.Max;
        }
        if (s.Value != n.Value)
        {
            ChangeEchoSuppressor.BeginSuppress(s);
            s.Value = n.Value;
        }
        s.StepFrequency = n.StepFrequency;
        if (n.Header is not null) s.Header = n.Header;
        ApplySetters(n.Setters, s);
        return null;
    }

    private UIElement? UpdateToggleSwitch(ToggleSwitchElement n, WinUI.ToggleSwitch ts)
    {
        SetElementTag(ts, n);
        if (ts.IsOn != n.IsOn)
        {
            ChangeEchoSuppressor.BeginSuppress(ts);
            ts.IsOn = n.IsOn;
        }
        ts.OnContent = n.OnContent; ts.OffContent = n.OffContent;
        if (n.Header is not null) ts.Header = n.Header;
        ApplySetters(n.Setters, ts);
        return null;
    }

    private UIElement? UpdateRatingControl(RatingControlElement n, WinUI.RatingControl r)
    {
        SetElementTag(r, n);
        r.MaxRating = n.MaxRating;
        if (r.Value != n.Value)
        {
            ChangeEchoSuppressor.BeginSuppress(r);
            r.Value = n.Value;
        }
        r.IsReadOnly = n.IsReadOnly;
        r.Caption = n.Caption ?? "";
        ApplySetters(n.Setters, r);
        return null;
    }

    private UIElement? UpdateColorPicker(ColorPickerElement n, WinUI.ColorPicker cp)
    {
        // Tag FIRST so the ColorChanged echo (fired synchronously from the
        // programmatic Color= assignment in some WinAppSDK builds) resolves
        // against this element, not the previous one. Suppressor then drops
        // the echo entirely — preventing the cross-row value-swap observed
        // when a PropertyGrid bound to a selection re-renders.
        SetElementTag(cp, n);
        if (cp.Color != n.Color)
        {
            ChangeEchoSuppressor.BeginSuppress(cp);
            cp.Color = n.Color;
        }
        cp.IsAlphaEnabled = n.IsAlphaEnabled;
        ApplySetters(n.Setters, cp);
        return null;
    }

    private UIElement? UpdateCalendarDatePicker(CalendarDatePickerElement n, WinUI.CalendarDatePicker cdp)
    {
        SetElementTag(cdp, n);
        if (cdp.Date != n.Date)
        {
            ChangeEchoSuppressor.BeginSuppress(cdp);
            cdp.Date = n.Date;
        }
        ApplySetters(n.Setters, cdp);
        return null;
    }

    private UIElement? UpdateDatePicker(DatePickerElement n, WinUI.DatePicker dp)
    {
        SetElementTag(dp, n);
        if (dp.Date != n.Date)
        {
            ChangeEchoSuppressor.BeginSuppress(dp);
            dp.Date = n.Date;
        }
        ApplySetters(n.Setters, dp);
        return null;
    }

    private UIElement? UpdateTimePicker(TimePickerElement n, WinUI.TimePicker tp)
    {
        SetElementTag(tp, n);
        if (tp.Time != n.Time)
        {
            ChangeEchoSuppressor.BeginSuppress(tp);
            tp.Time = n.Time;
        }
        ApplySetters(n.Setters, tp);
        return null;
    }

    private UIElement? UpdateProgress(ProgressElement n, WinUI.ProgressBar pb)
    {
        pb.IsIndeterminate = n.IsIndeterminate; pb.Minimum = n.Minimum; pb.Maximum = n.Maximum;
        pb.ShowError = n.ShowError; pb.ShowPaused = n.ShowPaused;
        if (n.Value.HasValue) pb.Value = n.Value.Value;
        ApplySetters(n.Setters, pb);
        return null;
    }

    private UIElement? UpdateProgressRing(ProgressRingElement n, WinUI.ProgressRing pr)
    {
        pr.IsIndeterminate = n.IsIndeterminate; pr.IsActive = n.IsActive;
        if (n.Value.HasValue) pr.Value = n.Value.Value;
        ApplySetters(n.Setters, pr);
        return null;
    }

    private UIElement? UpdateImage(ImageElement o, ImageElement n, WinUI.Image img)
    {
        if (o.Source != n.Source)
        {
            var uri = new Uri(n.Source, UriKind.RelativeOrAbsolute);
            img.Source = n.Source.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                ? new SvgImageSource(uri)
                : new BitmapImage(uri);
        }
        if (n.Width.HasValue) img.Width = n.Width.Value;
        if (n.Height.HasValue) img.Height = n.Height.Value;
        ApplySetters(n.Setters, img);
        return null;
    }

    private UIElement? UpdatePersonPicture(PersonPictureElement n, WinUI.PersonPicture pp)
    {
        if (n.DisplayName is not null) pp.DisplayName = n.DisplayName;
        if (n.Initials is not null) pp.Initials = n.Initials;
        pp.IsGroup = n.IsGroup; pp.BadgeNumber = n.BadgeNumber;
        ApplySetters(n.Setters, pp);
        return null;
    }

    private UIElement? UpdateWebView2(WebView2Element o, WebView2Element n, WinUI.WebView2 wv)
    {
        if (n.Source is not null && n.Source != o.Source) wv.Source = n.Source;
        SetElementTag(wv, n);
        ApplySetters(n.Setters, wv);
        return null;
    }

    private UIElement? UpdateRichEditBox(RichEditBoxElement n, WinUI.RichEditBox reb)
    {
        reb.IsReadOnly = n.IsReadOnly;
        if (n.Header is not null) reb.Header = n.Header;
        if (n.PlaceholderText is not null) reb.PlaceholderText = n.PlaceholderText;
        SetElementTag(reb, n);
        ApplySetters(n.Setters, reb);
        return null;
    }

    private UIElement? UpdateWrapGrid(WrapGridElement o, WrapGridElement n, WinUI.VariableSizedWrapGrid wg, Action requestRerender)
    {
        wg.Orientation = n.Orientation;
        if (n.MaximumRowsOrColumns >= 0) wg.MaximumRowsOrColumns = n.MaximumRowsOrColumns;
        if (!double.IsNaN(n.ItemWidth)) wg.ItemWidth = n.ItemWidth;
        if (!double.IsNaN(n.ItemHeight)) wg.ItemHeight = n.ItemHeight;
        ReconcileChildren(o.Children, n.Children, wg, requestRerender);
        SetElementTag(wg, n);
        ApplySetters(n.Setters, wg);
        return null;
    }

    private UIElement? UpdateCanvas(CanvasElement o, CanvasElement n, WinUI.Canvas canvas, Action requestRerender)
    {
        if (n.Width.HasValue && n.Width != o.Width) canvas.Width = n.Width.Value;
        if (n.Height.HasValue && n.Height != o.Height) canvas.Height = n.Height.Value;
        if (n.Background is not null) canvas.Background = n.Background;

        ReconcileChildren(o.Children, n.Children, canvas, requestRerender);

        // Re-apply Canvas attached properties (Left/Top) — skip nulls/EmptyElements
        // to stay aligned with canvas.Children (ChildReconciler filters those out).
        int panelIdx = 0;
        for (int i = 0; i < n.Children.Length && panelIdx < canvas.Children.Count; i++)
        {
            if (n.Children[i] is null or EmptyElement) continue;
            var ca = n.Children[i].GetAttached<CanvasAttached>();
            if (ca is not null && canvas.Children[panelIdx] is FrameworkElement fe)
            {
                WinUI.Canvas.SetLeft(fe, ca.Left);
                WinUI.Canvas.SetTop(fe, ca.Top);
            }
            panelIdx++;
        }

        ApplySetters(n.Setters, canvas);
        return null;
    }

    private UIElement? UpdateStack(StackElement o, StackElement n, WinUI.StackPanel sp, Action requestRerender)
    {
        if (o.Orientation != n.Orientation) sp.Orientation = n.Orientation;
        if (o.Spacing != n.Spacing) sp.Spacing = n.Spacing;
        if (n.HorizontalAlignment.HasValue && n.HorizontalAlignment != o.HorizontalAlignment) sp.HorizontalAlignment = n.HorizontalAlignment.Value;
        if (n.VerticalAlignment.HasValue && n.VerticalAlignment != o.VerticalAlignment) sp.VerticalAlignment = n.VerticalAlignment.Value;
        ReconcileChildren(o.Children, n.Children, sp, requestRerender);
        // No Tag set — StackPanel has no event handlers. Avoids expensive COM call.
        ApplySetters(n.Setters, sp);
        return null;
    }

    private UIElement? UpdateScrollView(ScrollViewElement o, ScrollViewElement n, WinUI.ScrollViewer sv, Element newEl, Action requestRerender)
    {
        if (CanUpdate(o.Child, n.Child))
        {
            var childRepl = Update(o.Child, n.Child, sv.Content as UIElement ?? new WinUI.Grid(), requestRerender);
            if (childRepl is not null) return Mount(newEl, requestRerender);
        }
        else return Mount(newEl, requestRerender);
        sv.HorizontalScrollBarVisibility = n.HorizontalScrollBarVisibility;
        sv.VerticalScrollBarVisibility = n.VerticalScrollBarVisibility;
        sv.HorizontalScrollMode = (WinUI.ScrollMode)n.HorizontalScrollMode;
        sv.VerticalScrollMode = (WinUI.ScrollMode)n.VerticalScrollMode;
        sv.ZoomMode = (WinUI.ZoomMode)n.ZoomMode;
        ApplySetters(n.Setters, sv);
        return null;
    }

    private UIElement? UpdateBorder(BorderElement o, BorderElement n, WinUI.Border b, Element newEl, Action requestRerender)
    {
        if (CanUpdate(o.Child, n.Child))
        {
            if (b.Child is not null)
            {
                var childRepl = Update(o.Child, n.Child, b.Child, requestRerender);
                if (childRepl is not null) return Mount(newEl, requestRerender);
            }
        }
        else return Mount(newEl, requestRerender);

        if (n.CornerRadius.HasValue) b.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(n.CornerRadius.Value);
        if (n.Padding.HasValue) b.Padding = n.Padding.Value;
        if (n.Background is not null) b.Background = n.Background;
        if (n.BorderBrush is not null) b.BorderBrush = n.BorderBrush;
        if (n.BorderThickness.HasValue) b.BorderThickness = new Microsoft.UI.Xaml.Thickness(n.BorderThickness.Value);
        // No Tag set — Border has no event handlers.
        ApplySetters(n.Setters, b);
        return null;
    }

    private UIElement? UpdateExpander(ExpanderElement o, ExpanderElement n, WinUI.Expander exp, Action requestRerender)
    {
        exp.Header = n.Header; exp.IsExpanded = n.IsExpanded;
        exp.ExpandDirection = n.ExpandDirection;

        // Reconcile content child
        if (exp.Content is UIElement existingContent && CanUpdate(o.Content, n.Content))
        {
            var replacement = Update(o.Content, n.Content, existingContent, requestRerender);
            if (replacement is not null)
                exp.Content = replacement;
        }
        else
        {
            if (exp.Content is UIElement oldContent)
                Unmount(oldContent);
            exp.Content = Mount(n.Content, requestRerender);
        }

        SetElementTag(exp, n);
        ApplySetters(n.Setters, exp);
        return null;
    }

    private UIElement? UpdateNavigationHost(
        NavigationHostElement oldEl, NavigationHostElement newEl,
        WinUI.Grid grid, Action requestRerender)
    {
        if (!_navigationHostNodes.TryGetValue(grid, out var node))
        {
            // Lost tracking — remount from scratch
            return Mount(newEl, requestRerender);
        }

        var handle = (Navigation.INavigationHandle)newEl.NavigationHandle;
        var currentRoute = handle.CurrentRoute;

        // Update the RouteMap if the delegate reference changed (rare but possible if
        // the parent component recreates the lambda every render).
        node.RouteMap = newEl.RouteMap;

        // If the handle changed (different navigation stack wired up), re-subscribe
        if (!ReferenceEquals(node.Handle, handle))
        {
            if (node.RouteChangedHandler is not null)
                node.Handle.RouteChanged -= node.RouteChangedHandler;
            node.Handle.Detach();

            node.Handle = handle;
            void onRouteChanged() => requestRerender();
            handle.RouteChanged += onRouteChanged;
            node.RouteChangedHandler = onRouteChanged;

            // Re-wire lifecycle guard for the new handle
            handle.LifecycleGuard = ctx =>
            {
                InvokeNavigatingFrom(node.CurrentChildControl, ctx);
                if (!ctx.IsCancelled)
                {
                    node.PendingNavigationMode = ctx.Mode;
                    node.PendingPreviousRoute = ctx.Route;
                }
            };
        }

        node.RequestRerender = requestRerender;

        if (Equals(currentRoute, node.LastRenderedRoute) && node.CurrentChildElement is not null)
        {
            // Route unchanged — reconcile the existing child element in place
            var newChildElement = node.RouteMap(currentRoute);
            var replacement = node.CurrentChildControl is not null
                ? Update(node.CurrentChildElement, newChildElement, node.CurrentChildControl, requestRerender)
                : Mount(newChildElement, requestRerender);

            if (replacement is not null && node.CurrentChildControl is not null)
            {
                // Child control type changed — swap in grid
                var idx = grid.Children.IndexOf(node.CurrentChildControl);
                if (idx >= 0)
                    grid.Children[idx] = replacement;
                else
                    grid.Children.Add(replacement);
                Unmount(node.CurrentChildControl);
                node.CurrentChildControl = replacement;
            }
            else if (replacement is not null)
            {
                grid.Children.Add(replacement);
                node.CurrentChildControl = replacement;
            }

            node.CurrentChildElement = newChildElement;
        }
        else
        {
            // Route changed — transition from old page to new page.
            // Lifecycle sequence per spec:
            //   1. onNavigatingFrom (already done by LifecycleGuard before stack mutation)
            //   2-3. Stack mutation (already done)
            //   4-5. Resolve + mount new element (or restore from cache)
            //   6. Run transition animation
            //   7. onNavigatedTo (new page)
            //   8. onNavigatedFrom (old page)
            //   9. Unmount or cache old element

            var oldChildControl = node.CurrentChildControl;
            var oldChildElement = node.CurrentChildElement;
            var previousRoute = node.LastRenderedRoute;
            var pendingMode = node.PendingNavigationMode;
            var pendingPreviousRoute = node.PendingPreviousRoute;
            node.PendingNavigationMode = null;
            node.PendingPreviousRoute = null;

            // Collect lifecycle hooks from old page BEFORE detach/unmount
            var oldHooks = pendingMode is not null
                ? CollectLifecycleHooks(oldChildControl)
                : null;

            // Resolve transition: per-navigation override > host default
            var transitionOverride = handle.PendingTransitionOverride;
            handle.PendingTransitionOverride = null;
            var transition = transitionOverride ?? node.HostTransition;
            var mode = pendingMode ?? Navigation.NavigationMode.Push;

            // Resolve new child: check cache first, then mount fresh
            UIElement? newChildControl;
            Element? newChildElement;

            bool wasCacheHit = false;
            if (node.Cache is not null && node.Cache.TryGet(currentRoute, out var cached))
            {
                // Cache hit — restore the mounted control
                newChildControl = cached.MountedControl;
                newChildElement = cached.LastElement;
                node.Cache.Remove(currentRoute);
                wasCacheHit = true;
            }
            else
            {
                // Cache miss — mount fresh
                newChildElement = node.RouteMap(currentRoute);
                newChildControl = Mount(newChildElement, requestRerender);
            }

            // Destination-side guard: invoke onNavigatingTo on the new page.
            // If cancelled, revert to old page.
            if (!InvokeNavigatingTo(newChildControl, currentRoute, pendingPreviousRoute, mode))
            {
                if (!wasCacheHit && newChildControl is not null)
                    Unmount(newChildControl);
                return null;
            }

            // Update node state immediately
            node.CurrentChildElement = newChildElement;
            node.CurrentChildControl = newChildControl;
            node.LastRenderedRoute = currentRoute;

            // Action to finalize the old page (cache or unmount)
            void FinalizeOldPage(UIElement? oldCtrl, Element? oldElem, object? oldRoute)
            {
                if (oldCtrl is null) return;
                grid.Children.Remove(oldCtrl);

                if (node.Cache is not null && node.CacheMode != Navigation.NavigationCacheMode.Disabled
                    && oldRoute is not null)
                {
                    // Store in cache instead of unmounting
                    node.Cache.Add(oldRoute, new Navigation.CachedPage
                    {
                        MountedControl = oldCtrl,
                        LastElement = oldElem,
                        LastAccessed = DateTime.UtcNow,
                        CacheMode = node.CacheMode,
                    });
                }
                else
                {
                    Unmount(oldCtrl);
                }
            }

            // Determine whether to run an animated transition
            bool useAnimation = transition is not Navigation.SuppressTransition
                && oldChildControl is not null
                && newChildControl is not null;

            if (useAnimation)
            {
                // Mount new content at Opacity 0 alongside old content
                var inVisual = ElementCompositionPreview.GetElementVisual(newChildControl!);
                inVisual.Opacity = 0;
                grid.Children.Add(newChildControl!);
                node.TransitionInProgress = true;

                // Capture references for the completion callback
                var capturedOldControl = oldChildControl;
                var capturedOldElement = oldChildElement;
                var capturedOldRoute = previousRoute;
                var capturedNewControl = newChildControl!;
                var capturedMode = mode;
                var capturedCurrentRoute = currentRoute;
                var capturedPreviousRoute = pendingPreviousRoute;
                var capturedOldHooks = oldHooks;

                Navigation.TransitionEngine.RunTransition(
                    capturedOldControl!, capturedNewControl, transition, capturedMode,
                    onComplete: () =>
                    {
                        node.TransitionInProgress = false;
                        FinalizeOldPage(capturedOldControl, capturedOldElement, capturedOldRoute);

                        InvokePostNavigationLifecycle(
                            capturedNewControl, capturedOldHooks,
                            capturedCurrentRoute, capturedPreviousRoute, capturedMode);
                    });
            }
            else
            {
                // Instant swap (SuppressTransition or missing controls)
                FinalizeOldPage(oldChildControl, oldChildElement, previousRoute);

                if (newChildControl is not null)
                    grid.Children.Add(newChildControl);

                InvokePostNavigationLifecycle(
                    newChildControl, oldHooks,
                    currentRoute, pendingPreviousRoute, mode);
            }
        }

        // Update host properties if changed
        node.HostTransition = newEl.Transition;
        if (node.CacheMode != newEl.CacheMode)
        {
            node.CacheMode = newEl.CacheMode;
            if (newEl.CacheMode == Navigation.NavigationCacheMode.Disabled && node.Cache is not null)
            {
                node.Cache.Clear();
                node.Cache = null;
            }
            else if (newEl.CacheMode != Navigation.NavigationCacheMode.Disabled && node.Cache is null)
            {
                node.Cache = new Navigation.NavigationCache(newEl.CacheSize, evicted => Unmount(evicted));
            }
        }
        if (node.Cache is not null)
            node.Cache.MaxSize = newEl.CacheSize;

        return null; // Patched in place
    }

    private UIElement? UpdateNavigationView(NavigationViewElement o, NavigationViewElement n, WinUI.NavigationView nv, Action requestRerender)
    {
        nv.IsPaneOpen = n.IsPaneOpen; nv.IsBackEnabled = n.IsBackEnabled;

        // Reconcile content child instead of always remounting
        if (n.Content is not null && o.Content is not null
            && nv.Content is UIElement existingContent && CanUpdate(o.Content, n.Content))
        {
            var replacement = Update(o.Content, n.Content, existingContent, requestRerender);
            if (replacement is not null)
                nv.Content = replacement;
        }
        else if (n.Content is not null)
        {
            if (nv.Content is UIElement oldContent)
                Unmount(oldContent);
            nv.Content = Mount(n.Content, requestRerender);
        }
        else if (n.Content is null && nv.Content is UIElement staleContent)
        {
            Unmount(staleContent);
            nv.Content = null;
        }

        SetElementTag(nv, n);
        ApplySetters(n.Setters, nv);
        return null;
    }

    private UIElement? UpdateTitleBar(TitleBarElement o, TitleBarElement n, WinUI.TitleBar titleBar, Action requestRerender)
    {
        titleBar.Title = n.Title;
        if (n.Subtitle is not null) titleBar.Subtitle = n.Subtitle;
        titleBar.IsBackButtonVisible = n.IsBackButtonVisible;
        titleBar.IsBackButtonEnabled = n.IsBackButtonEnabled;
        titleBar.IsPaneToggleButtonVisible = n.IsPaneToggleButtonVisible;

        ReconcileChild(o.Content, n.Content,
            () => titleBar.Content as UIElement,
            c => titleBar.Content = c,
            () => titleBar.Content = null,
            requestRerender);

        ReconcileChild(o.RightHeader, n.RightHeader,
            () => titleBar.RightHeader as UIElement,
            c => titleBar.RightHeader = c,
            () => titleBar.RightHeader = null,
            requestRerender);

        SetElementTag(titleBar, n);
        ApplySetters(n.Setters, titleBar);
        return null;
    }

    private void ReconcileChild(Element? oldChild, Element? newChild,
        Func<UIElement?> getControl, Action<UIElement> setControl, Action clearControl,
        Action requestRerender)
    {
        if (newChild is not null && oldChild is not null
            && getControl() is UIElement existing && CanUpdate(oldChild, newChild))
        {
            var replacement = Update(oldChild, newChild, existing, requestRerender);
            if (replacement is not null) setControl(replacement);
        }
        else if (newChild is not null)
        {
            if (getControl() is UIElement old) Unmount(old);
            var mounted = Mount(newChild, requestRerender);
            if (mounted is not null) setControl(mounted);
        }
        else if (newChild is null && getControl() is UIElement stale)
        {
            Unmount(stale);
            clearControl();
        }
    }

    private UIElement? UpdateTabView(TabViewElement o, TabViewElement n, WinUI.TabView tabView, Action requestRerender)
    {
        // In-place reconcile so that state changes on descendants don't tear the
        // TabView down (which would re-animate the tab bar in and steal focus
        // from any control inside the active tab — see the Commanding Demo
        // regression where every keystroke blew away the selection).
        // Retag first so any events raised by property writes resolve through
        // the new element's closures.
        SetElementTag(tabView, n);

        var items = tabView.TabItems;
        int oldCount = o.Tabs.Length;
        int newCount = n.Tabs.Length;
        int common = Math.Min(oldCount, newCount);

        for (int i = 0; i < common; i++)
        {
            var oldTab = o.Tabs[i];
            var newTab = n.Tabs[i];
            if (items[i] is not WinUI.TabViewItem tvi) continue;

            if (tvi.Header as string != newTab.Header) tvi.Header = newTab.Header;
            if (tvi.IsClosable != newTab.IsClosable) tvi.IsClosable = newTab.IsClosable;
            if (newTab.Icon != oldTab.Icon)
                tvi.IconSource = ResolveIconSource(newTab.Icon);

            if (tvi.Content is UIElement existingContent && CanUpdate(oldTab.Content, newTab.Content))
            {
                var replacement = Update(oldTab.Content, newTab.Content, existingContent, requestRerender);
                if (replacement is not null) tvi.Content = replacement;
            }
            else
            {
                if (tvi.Content is UIElement stale) Unmount(stale);
                tvi.Content = Mount(newTab.Content, requestRerender);
            }
        }

        // Remove excess tabs
        for (int i = items.Count - 1; i >= newCount; i--)
        {
            if (items[i] is WinUI.TabViewItem stale && stale.Content is UIElement staleContent)
                Unmount(staleContent);
            items.RemoveAt(i);
        }

        // Add new tabs
        for (int i = oldCount; i < newCount; i++)
        {
            var tabItem = n.Tabs[i];
            var tvi = new WinUI.TabViewItem
            {
                Header = tabItem.Header,
                IsClosable = tabItem.IsClosable,
                Content = Mount(tabItem.Content, requestRerender),
            };
            if (tabItem.Icon is not null)
                tvi.IconSource = ResolveIconSource(tabItem.Icon);
            items.Add(tvi);
        }

        // Only sync SelectedIndex when the element itself changed it. Writing
        // on every update would clobber the user's current tab when the element
        // doesn't control SelectedIndex (common in "uncontrolled" samples).
        if (o.SelectedIndex != n.SelectedIndex
            && n.SelectedIndex >= 0 && n.SelectedIndex < newCount
            && tabView.SelectedIndex != n.SelectedIndex)
            tabView.SelectedIndex = n.SelectedIndex;

        if (tabView.IsAddTabButtonVisible != n.IsAddTabButtonVisible)
            tabView.IsAddTabButtonVisible = n.IsAddTabButtonVisible;

        ApplySetters(n.Setters, tabView);
        return null;
    }

    private UIElement? UpdatePivot(PivotElement o, PivotElement n, WinUI.Pivot pivot, Action requestRerender)
    {
        SetElementTag(pivot, n);

        var items = pivot.Items;
        int common = Math.Min(o.Items.Length, n.Items.Length);

        for (int i = 0; i < common; i++)
        {
            if (items[i] is not WinUI.PivotItem pi) continue;
            var oldItem = o.Items[i];
            var newItem = n.Items[i];

            if (pi.Header as string != newItem.Header) pi.Header = newItem.Header;

            if (pi.Content is UIElement existing && CanUpdate(oldItem.Content, newItem.Content))
            {
                var replacement = Update(oldItem.Content, newItem.Content, existing, requestRerender);
                if (replacement is not null) pi.Content = replacement;
            }
            else
            {
                if (pi.Content is UIElement stale) Unmount(stale);
                pi.Content = Mount(newItem.Content, requestRerender);
            }
        }

        for (int i = items.Count - 1; i >= n.Items.Length; i--)
        {
            if (items[i] is WinUI.PivotItem stale && stale.Content is UIElement sc) Unmount(sc);
            items.RemoveAt(i);
        }

        for (int i = o.Items.Length; i < n.Items.Length; i++)
        {
            var newItem = n.Items[i];
            items.Add(new WinUI.PivotItem { Header = newItem.Header, Content = Mount(newItem.Content, requestRerender) });
        }

        if (n.Title is not null && pivot.Title as string != n.Title) pivot.Title = n.Title;

        // Only sync SelectedIndex when the element changed it — see UpdateTabView.
        if (o.SelectedIndex != n.SelectedIndex
            && n.SelectedIndex >= 0 && n.SelectedIndex < items.Count
            && pivot.SelectedIndex != n.SelectedIndex)
            pivot.SelectedIndex = n.SelectedIndex;

        ApplySetters(n.Setters, pivot);
        return null;
    }

    private UIElement? UpdateRadioButtons(RadioButtonsElement o, RadioButtonsElement n, WinUI.RadioButtons rbg)
    {
        SetElementTag(rbg, n);
        if (!StringArrayEquals(o.Items, n.Items))
        {
            rbg.Items.Clear();
            foreach (var item in n.Items) rbg.Items.Add(item);
        }
        if (n.Header is not null && rbg.Header as string != n.Header) rbg.Header = n.Header;
        // Only sync when the element itself changed SelectedIndex.
        if (o.SelectedIndex != n.SelectedIndex && rbg.SelectedIndex != n.SelectedIndex)
            rbg.SelectedIndex = n.SelectedIndex;
        ApplySetters(n.Setters, rbg);
        return null;
    }

    private UIElement? UpdateComboBox(ComboBoxElement o, ComboBoxElement n, WinUI.ComboBox cb, Action requestRerender)
    {
        SetElementTag(cb, n);

        bool oldIsElements = o.ItemElements is not null;
        bool newIsElements = n.ItemElements is not null;

        // Mode switch: unmount any UIElement items (strings need no unmount),
        // then drop the whole list so the following code starts from scratch.
        if (oldIsElements != newIsElements)
        {
            for (int i = cb.Items.Count - 1; i >= 0; i--)
                if (cb.Items[i] is UIElement stale) Unmount(stale);
            cb.Items.Clear();
        }

        if (newIsElements)
        {
            var newEls = n.ItemElements!;
            // After a mode switch, oldEls is empty so we fall through to pure
            // append below — that's correct because cb.Items is empty too.
            var oldEls = oldIsElements ? o.ItemElements! : Array.Empty<Element>();
            int common = Math.Min(oldEls.Length, newEls.Length);
            for (int i = 0; i < common; i++)
            {
                if (cb.Items[i] is UIElement existing && CanUpdate(oldEls[i], newEls[i]))
                {
                    var replacement = Update(oldEls[i], newEls[i], existing, requestRerender);
                    if (replacement is not null) cb.Items[i] = replacement;
                }
                else
                {
                    if (cb.Items[i] is UIElement stale) Unmount(stale);
                    cb.Items[i] = Mount(newEls[i], requestRerender);
                }
            }
            for (int i = cb.Items.Count - 1; i >= newEls.Length; i--)
            {
                if (cb.Items[i] is UIElement stale) Unmount(stale);
                cb.Items.RemoveAt(i);
            }
            for (int i = oldEls.Length; i < newEls.Length; i++)
                cb.Items.Add(Mount(newEls[i], requestRerender));
        }
        else
        {
            // String items. After a mode switch cb.Items is empty, so fill it;
            // otherwise only refill when the string array actually differs.
            if (oldIsElements || !StringArrayEquals(o.Items, n.Items))
            {
                cb.Items.Clear();
                foreach (var item in n.Items) cb.Items.Add(item);
            }
        }

        if (o.SelectedIndex != n.SelectedIndex && cb.SelectedIndex != n.SelectedIndex)
            cb.SelectedIndex = n.SelectedIndex;
        cb.PlaceholderText = n.PlaceholderText ?? "";
        if (cb.IsEditable != n.IsEditable) cb.IsEditable = n.IsEditable;
        if (n.Header is not null && cb.Header as string != n.Header) cb.Header = n.Header;
        ApplySetters(n.Setters, cb);
        return null;
    }

    private UIElement? UpdateListBox(ListBoxElement o, ListBoxElement n, WinUI.ListBox lb)
    {
        SetElementTag(lb, n);
        if (!StringArrayEquals(o.Items, n.Items))
        {
            lb.Items.Clear();
            foreach (var item in n.Items) lb.Items.Add(item);
        }
        if (o.SelectedIndex != n.SelectedIndex && lb.SelectedIndex != n.SelectedIndex)
            lb.SelectedIndex = n.SelectedIndex;
        ApplySetters(n.Setters, lb);
        return null;
    }

    private UIElement? UpdateSelectorBar(SelectorBarElement o, SelectorBarElement n, WinUI.SelectorBar bar)
    {
        SetElementTag(bar, n);

        var items = bar.Items;
        int common = Math.Min(o.Items.Length, n.Items.Length);

        for (int i = 0; i < common; i++)
        {
            if (items[i] is not WinUI.SelectorBarItem sbi) continue;
            var oldItem = o.Items[i];
            var newItem = n.Items[i];

            if (sbi.Text != newItem.Text) sbi.Text = newItem.Text;
            if (oldItem.Icon != newItem.Icon)
                sbi.Icon = ResolveIconString(newItem.Icon ?? "");
        }

        for (int i = items.Count - 1; i >= n.Items.Length; i--)
            items.RemoveAt(i);

        for (int i = o.Items.Length; i < n.Items.Length; i++)
        {
            var newItem = n.Items[i];
            var sbi = new WinUI.SelectorBarItem { Text = newItem.Text };
            if (newItem.Icon is not null) sbi.Icon = ResolveIconString(newItem.Icon);
            items.Add(sbi);
        }

        // Only sync selection when the element moved it.
        if (o.SelectedIndex != n.SelectedIndex
            && n.SelectedIndex >= 0 && n.SelectedIndex < items.Count)
        {
            var desired = items[n.SelectedIndex];
            if (!ReferenceEquals(bar.SelectedItem, desired)) bar.SelectedItem = desired;
        }
        ApplySetters(n.Setters, bar);
        return null;
    }

    private UIElement? UpdateSplitView(SplitViewElement o, SplitViewElement n, WinUI.SplitView sv, Action requestRerender)
    {
        if (sv.IsPaneOpen != n.IsPaneOpen) sv.IsPaneOpen = n.IsPaneOpen;
        if (sv.OpenPaneLength != n.OpenPaneLength) sv.OpenPaneLength = n.OpenPaneLength;
        if (sv.CompactPaneLength != n.CompactPaneLength) sv.CompactPaneLength = n.CompactPaneLength;
        if (sv.DisplayMode != n.DisplayMode) sv.DisplayMode = n.DisplayMode;

        ReconcileChild(o.Pane, n.Pane,
            () => sv.Pane as UIElement,
            c => sv.Pane = c,
            () => sv.Pane = null,
            requestRerender);

        ReconcileChild(o.Content, n.Content,
            () => sv.Content as UIElement,
            c => sv.Content = c,
            () => sv.Content = null,
            requestRerender);

        SetElementTag(sv, n);
        ApplySetters(n.Setters, sv);
        return null;
    }

    private UIElement? UpdateSemanticZoom(SemanticZoomElement o, SemanticZoomElement n, WinUI.SemanticZoom sz, Action requestRerender)
    {
        // ZoomedInView/ZoomedOutView must be ISemanticZoomInformation — reconcile
        // in place when possible to keep list state; otherwise swap.
        if (sz.ZoomedInView is UIElement oldIn && CanUpdate(o.ZoomedInView, n.ZoomedInView))
        {
            var replacement = Update(o.ZoomedInView, n.ZoomedInView, oldIn, requestRerender);
            if (replacement is ISemanticZoomInformation szi) sz.ZoomedInView = szi;
        }
        else
        {
            if (sz.ZoomedInView is UIElement staleIn) Unmount(staleIn);
            if (Mount(n.ZoomedInView, requestRerender) is ISemanticZoomInformation szi) sz.ZoomedInView = szi;
        }

        if (sz.ZoomedOutView is UIElement oldOut && CanUpdate(o.ZoomedOutView, n.ZoomedOutView))
        {
            var replacement = Update(o.ZoomedOutView, n.ZoomedOutView, oldOut, requestRerender);
            if (replacement is ISemanticZoomInformation szo) sz.ZoomedOutView = szo;
        }
        else
        {
            if (sz.ZoomedOutView is UIElement staleOut) Unmount(staleOut);
            if (Mount(n.ZoomedOutView, requestRerender) is ISemanticZoomInformation szo) sz.ZoomedOutView = szo;
        }

        SetElementTag(sz, n);
        ApplySetters(n.Setters, sz);
        return null;
    }

    private UIElement? UpdateRelativePanel(RelativePanelElement o, RelativePanelElement n, WinUI.RelativePanel rp, Action requestRerender)
    {
        // RelativePanel's children reference each other by name for layout.
        // Reconcile in place when the children line up positionally; if the
        // count differs, fall back to a rebuild since attached-property
        // references depend on the name map.
        if (o.Children.Length != n.Children.Length)
        {
            foreach (var existing in rp.Children)
                if (existing is UIElement ue) Unmount(ue);
            rp.Children.Clear();
            // Re-run the mount logic so the two-pass attached-property wiring
            // stays consistent.
            var remount = Mount(n, requestRerender);
            return remount;
        }

        var nameMap = new Dictionary<string, UIElement>();
        for (int i = 0; i < n.Children.Length; i++)
        {
            if (rp.Children[i] is not UIElement existingCtrl) continue;
            UIElement? updated = existingCtrl;
            if (CanUpdate(o.Children[i], n.Children[i]))
            {
                var replacement = Update(o.Children[i], n.Children[i], existingCtrl, requestRerender);
                if (replacement is not null)
                {
                    rp.Children[i] = replacement;
                    updated = replacement;
                }
            }
            else
            {
                Unmount(existingCtrl);
                var mounted = Mount(n.Children[i], requestRerender);
                rp.Children[i] = mounted;
                updated = mounted;
            }
            var rpa = n.Children[i].GetAttached<RelativePanelAttached>();
            if (rpa is not null && updated is FrameworkElement fe)
            {
                fe.Name = rpa.Name;
                nameMap[rpa.Name] = updated;
            }
        }

        // Reapply relative attached properties using the refreshed name map.
        for (int i = 0; i < n.Children.Length; i++)
        {
            var rpa = n.Children[i].GetAttached<RelativePanelAttached>();
            if (rpa is null) continue;
            if (!nameMap.TryGetValue(rpa.Name, out var ctrl)) continue;
            if (rpa.RightOf is not null && nameMap.TryGetValue(rpa.RightOf, out var rightOf))
                WinUI.RelativePanel.SetRightOf(ctrl, rightOf);
            if (rpa.Below is not null && nameMap.TryGetValue(rpa.Below, out var below))
                WinUI.RelativePanel.SetBelow(ctrl, below);
            if (rpa.LeftOf is not null && nameMap.TryGetValue(rpa.LeftOf, out var leftOf))
                WinUI.RelativePanel.SetLeftOf(ctrl, leftOf);
            if (rpa.Above is not null && nameMap.TryGetValue(rpa.Above, out var above))
                WinUI.RelativePanel.SetAbove(ctrl, above);
            if (rpa.AlignLeftWith is not null && nameMap.TryGetValue(rpa.AlignLeftWith, out var alw))
                WinUI.RelativePanel.SetAlignLeftWith(ctrl, alw);
            if (rpa.AlignRightWith is not null && nameMap.TryGetValue(rpa.AlignRightWith, out var arw))
                WinUI.RelativePanel.SetAlignRightWith(ctrl, arw);
            if (rpa.AlignTopWith is not null && nameMap.TryGetValue(rpa.AlignTopWith, out var atw))
                WinUI.RelativePanel.SetAlignTopWith(ctrl, atw);
            if (rpa.AlignBottomWith is not null && nameMap.TryGetValue(rpa.AlignBottomWith, out var abw))
                WinUI.RelativePanel.SetAlignBottomWith(ctrl, abw);
            if (rpa.AlignHorizontalCenterWith is not null && nameMap.TryGetValue(rpa.AlignHorizontalCenterWith, out var ahcw))
                WinUI.RelativePanel.SetAlignHorizontalCenterWith(ctrl, ahcw);
            if (rpa.AlignVerticalCenterWith is not null && nameMap.TryGetValue(rpa.AlignVerticalCenterWith, out var avcw))
                WinUI.RelativePanel.SetAlignVerticalCenterWith(ctrl, avcw);
            WinUI.RelativePanel.SetAlignLeftWithPanel(ctrl, rpa.AlignLeftWithPanel);
            WinUI.RelativePanel.SetAlignRightWithPanel(ctrl, rpa.AlignRightWithPanel);
            WinUI.RelativePanel.SetAlignTopWithPanel(ctrl, rpa.AlignTopWithPanel);
            WinUI.RelativePanel.SetAlignBottomWithPanel(ctrl, rpa.AlignBottomWithPanel);
            WinUI.RelativePanel.SetAlignHorizontalCenterWithPanel(ctrl, rpa.AlignHorizontalCenterWithPanel);
            WinUI.RelativePanel.SetAlignVerticalCenterWithPanel(ctrl, rpa.AlignVerticalCenterWithPanel);
        }

        SetElementTag(rp, n);
        ApplySetters(n.Setters, rp);
        return null;
    }

    private UIElement? UpdatePopup(PopupElement o, PopupElement n, WinUI.StackPanel wrapper, Action requestRerender)
    {
        // The popup itself is the wrapper's first child. Update its scalar
        // props and reconcile the hosted Child in place so transient popup
        // state (focus, scroll) survives parent re-renders.
        if (wrapper.Children.Count == 0 || wrapper.Children[0] is not WinPrim.Popup popup)
            return Mount(n, requestRerender);

        // Retag first so Closed/Opened handlers that resolve callbacks via the
        // wrapper's Tag see the new element's closures.
        SetElementTag(wrapper, n);

        if (popup.IsOpen != n.IsOpen) popup.IsOpen = n.IsOpen;
        if (popup.IsLightDismissEnabled != n.IsLightDismissEnabled) popup.IsLightDismissEnabled = n.IsLightDismissEnabled;
        if (popup.HorizontalOffset != n.HorizontalOffset) popup.HorizontalOffset = n.HorizontalOffset;
        if (popup.VerticalOffset != n.VerticalOffset) popup.VerticalOffset = n.VerticalOffset;

        if (popup.Child is UIElement existing && CanUpdate(o.Child, n.Child))
        {
            var replacement = Update(o.Child, n.Child, existing, requestRerender);
            if (replacement is not null) popup.Child = replacement;
        }
        else
        {
            if (popup.Child is UIElement stale) Unmount(stale);
            popup.Child = Mount(n.Child, requestRerender) as UIElement;
        }

        ApplySetters(n.Setters, popup);
        return null;
    }

    private UIElement? UpdateRefreshContainer(RefreshContainerElement o, RefreshContainerElement n, WinUI.RefreshContainer rc, Action requestRerender)
    {
        if (rc.Content is UIElement existing && CanUpdate(o.Content, n.Content))
        {
            var replacement = Update(o.Content, n.Content, existing, requestRerender);
            if (replacement is not null) rc.Content = replacement;
        }
        else
        {
            if (rc.Content is UIElement stale) Unmount(stale);
            rc.Content = Mount(n.Content, requestRerender);
        }
        SetElementTag(rc, n);
        ApplySetters(n.Setters, rc);
        return null;
    }

    private UIElement? UpdateCommandBarFlyout(CommandBarFlyoutElement o, CommandBarFlyoutElement n, UIElement targetControl, Action requestRerender)
    {
        // Reconcile the target in place and reuse the attached flyout when
        // possible — re-attaching a brand-new flyout on every update would
        // close an already-open flyout and discard its transient state.
        UIElement? updated = targetControl;
        if (CanUpdate(o.Target, n.Target))
        {
            var replacement = Update(o.Target, n.Target, targetControl, requestRerender);
            if (replacement is not null) updated = replacement;
        }
        else
        {
            Unmount(targetControl);
            updated = Mount(n.Target, requestRerender);
        }

        if (updated is FrameworkElement targetFe)
        {
            SetElementTag(targetFe, n);
            var existing = WinPrim.FlyoutBase.GetAttachedFlyout(targetFe) as WinUI.CommandBarFlyout;
            var commandsChanged =
                !ReferenceEquals(o.PrimaryCommands, n.PrimaryCommands) ||
                !ReferenceEquals(o.SecondaryCommands, n.SecondaryCommands);

            if (existing is null)
            {
                var flyout = new WinUI.CommandBarFlyout { Placement = n.Placement };
                if (n.PrimaryCommands is not null)
                    foreach (var cmd in n.PrimaryCommands) flyout.PrimaryCommands.Add(CreateAppBarItem(cmd));
                if (n.SecondaryCommands is not null)
                    foreach (var cmd in n.SecondaryCommands) flyout.SecondaryCommands.Add(CreateAppBarItem(cmd));
                WinPrim.FlyoutBase.SetAttachedFlyout(targetFe, flyout);
                ApplySetters(n.Setters, flyout);
            }
            else
            {
                if (existing.Placement != n.Placement) existing.Placement = n.Placement;
                if (commandsChanged)
                {
                    existing.PrimaryCommands.Clear();
                    existing.SecondaryCommands.Clear();
                    if (n.PrimaryCommands is not null)
                        foreach (var cmd in n.PrimaryCommands) existing.PrimaryCommands.Add(CreateAppBarItem(cmd));
                    if (n.SecondaryCommands is not null)
                        foreach (var cmd in n.SecondaryCommands) existing.SecondaryCommands.Add(CreateAppBarItem(cmd));
                }
                ApplySetters(n.Setters, existing);
            }
        }
        return updated == targetControl ? null : updated;
    }

    private UIElement? UpdateMenuFlyout(MenuFlyoutElement o, MenuFlyoutElement n, UIElement targetControl, Action requestRerender)
    {
        UIElement? updated = targetControl;
        if (CanUpdate(o.Target, n.Target))
        {
            var replacement = Update(o.Target, n.Target, targetControl, requestRerender);
            if (replacement is not null) updated = replacement;
        }
        else
        {
            Unmount(targetControl);
            updated = Mount(n.Target, requestRerender);
        }

        if (updated is FrameworkElement targetFe)
        {
            SetElementTag(targetFe, n);
            // Retrieve the existing MenuFlyout and update items in place.
            WinPrim.FlyoutBase? existingFlyout = targetFe switch
            {
                WinUI.SplitButton sb => sb.Flyout,
                WinUI.Button btn => btn.Flyout,
                _ => WinPrim.FlyoutBase.GetAttachedFlyout(targetFe),
            };
            if (existingFlyout is WinUI.MenuFlyout mf)
            {
                UpdateMenuFlyoutItems(mf.Items, o.Items, n.Items);
                ApplySetters(n.Setters, mf);
            }
            else
            {
                // Flyout type changed or was missing — create fresh.
                var menuFlyout = new WinUI.MenuFlyout();
                foreach (var item in n.Items) menuFlyout.Items.Add(CreateMenuFlyoutItem(item));
                SetFlyoutOnControl(targetFe, menuFlyout);
                ApplySetters(n.Setters, menuFlyout);
            }
        }
        return updated == targetControl ? null : updated;
    }

    private UIElement? UpdateFlyoutElement(FlyoutElement o, FlyoutElement n, UIElement targetControl, Action requestRerender)
    {
        UIElement? updated = targetControl;
        if (CanUpdate(o.Target, n.Target))
        {
            var replacement = Update(o.Target, n.Target, targetControl, requestRerender);
            if (replacement is not null) updated = replacement;
        }
        else
        {
            Unmount(targetControl);
            updated = Mount(n.Target, requestRerender);
        }

        if (updated is FrameworkElement targetFe)
        {
            SetElementTag(targetFe, n);
            WinPrim.FlyoutBase? existingFlyout = targetFe switch
            {
                WinUI.SplitButton sb => sb.Flyout,
                WinUI.Button btn => btn.Flyout,
                _ => WinPrim.FlyoutBase.GetAttachedFlyout(targetFe),
            };

            if (existingFlyout is WinUI.Flyout flyout)
            {
                if (flyout.Content is UIElement existingContent && CanUpdate(o.FlyoutContent, n.FlyoutContent))
                {
                    var contentRepl = Update(o.FlyoutContent, n.FlyoutContent, existingContent, requestRerender);
                    if (contentRepl is not null) flyout.Content = contentRepl;
                }
                else
                {
                    if (flyout.Content is UIElement stale) Unmount(stale);
                    flyout.Content = Mount(n.FlyoutContent, requestRerender);
                }
                flyout.Placement = n.Placement;
                ApplySetters(n.Setters, flyout);
            }
            else
            {
                // No existing flyout or type mismatch — create fresh.
                var flyoutContent = Mount(n.FlyoutContent, requestRerender);
                var newFlyout = new WinUI.Flyout { Content = flyoutContent, Placement = n.Placement };
                // Route handlers through the target's Tag (already set to n above) so future
                // Update() calls that refresh the tag keep Opened/Closed pointing at the
                // current FlyoutElement's delegates.
                var handlerTarget = targetFe;
                newFlyout.Opened += (_, _) => (GetElementTag(handlerTarget) as FlyoutElement)?.OnOpened?.Invoke();
                newFlyout.Closed += (_, _) => (GetElementTag(handlerTarget) as FlyoutElement)?.OnClosed?.Invoke();
                SetFlyoutOnControl(targetFe, newFlyout);
                ApplySetters(n.Setters, newFlyout);
            }
            if (n.IsOpen && !o.IsOpen) WinPrim.FlyoutBase.ShowAttachedFlyout(targetFe);
        }
        return updated == targetControl ? null : updated;
    }

    private UIElement? UpdateViewbox(ViewboxElement o, ViewboxElement n, WinUI.Viewbox vb, Action requestRerender)
    {
        if (CanUpdate(o.Child, n.Child))
        {
            if (vb.Child is UIElement existingChild)
            {
                var childRepl = Update(o.Child, n.Child, existingChild, requestRerender);
                if (childRepl is not null) vb.Child = childRepl as UIElement;
            }
        }
        else
        {
            if (vb.Child is UIElement stale) Unmount(stale);
            vb.Child = Mount(n.Child, requestRerender) as UIElement;
        }
        ApplySetters(n.Setters, vb);
        return null;
    }

    private UIElement? UpdateSwipeControl(SwipeControlElement o, SwipeControlElement n, WinUI.SwipeControl sc, Action requestRerender)
    {
        if (sc.Content is UIElement existing && CanUpdate(o.Content, n.Content))
        {
            var replacement = Update(o.Content, n.Content, existing, requestRerender);
            if (replacement is not null) sc.Content = replacement;
        }
        else
        {
            if (sc.Content is UIElement stale) Unmount(stale);
            sc.Content = Mount(n.Content, requestRerender);
        }

        // Swipe items are thin data — rebuild the SwipeItems collections when
        // the definitions change. Reference-equal arrays skip the rebuild.
        if (!ReferenceEquals(o.LeftItems, n.LeftItems) || o.LeftItemsMode != n.LeftItemsMode)
        {
            if (n.LeftItems is { Length: > 0 })
            {
                var items = new SwipeItems { Mode = n.LeftItemsMode };
                foreach (var it in n.LeftItems) items.Add(CreateSwipeItem(it));
                sc.LeftItems = items;
            }
            else sc.LeftItems = null;
        }
        if (!ReferenceEquals(o.RightItems, n.RightItems) || o.RightItemsMode != n.RightItemsMode)
        {
            if (n.RightItems is { Length: > 0 })
            {
                var items = new SwipeItems { Mode = n.RightItemsMode };
                foreach (var it in n.RightItems) items.Add(CreateSwipeItem(it));
                sc.RightItems = items;
            }
            else sc.RightItems = null;
        }

        SetElementTag(sc, n);
        ApplySetters(n.Setters, sc);
        return null;
    }

    private UIElement? UpdateParallaxView(ParallaxViewElement o, ParallaxViewElement n, WinUI.ParallaxView pv, Action requestRerender)
    {
        if (pv.VerticalShift != n.VerticalShift) pv.VerticalShift = n.VerticalShift;
        if (pv.HorizontalShift != n.HorizontalShift) pv.HorizontalShift = n.HorizontalShift;
        if (pv.Child is UIElement existing && CanUpdate(o.Child, n.Child))
        {
            var replacement = Update(o.Child, n.Child, existing, requestRerender);
            if (replacement is not null) pv.Child = replacement as UIElement;
        }
        else
        {
            if (pv.Child is UIElement stale) Unmount(stale);
            pv.Child = Mount(n.Child, requestRerender) as UIElement;
        }
        ApplySetters(n.Setters, pv);
        return null;
    }

    private static bool StringArrayEquals(string[] a, string[] b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private UIElement? UpdateBreadcrumbBar(BreadcrumbBarElement n, WinUI.BreadcrumbBar bcb)
    {
        bcb.ItemsSource = n.Items.Select(i => i.Label).ToList();
        SetElementTag(bcb, n);
        ApplySetters(n.Setters, bcb);
        return null;
    }

    private UIElement? UpdateInfoBar(InfoBarElement n, WinUI.InfoBar ib)
    {
        ib.Title = n.Title ?? ""; ib.Message = n.Message ?? "";
        ib.Severity = n.Severity; ib.IsOpen = n.IsOpen; ib.IsClosable = n.IsClosable;
        SetElementTag(ib, n);
        ApplySetters(n.Setters, ib);
        return null;
    }

    private UIElement? UpdateInfoBadge(InfoBadgeElement n, WinUI.InfoBadge badge)
    {
        if (n.Value.HasValue) badge.Value = n.Value.Value;
        ApplySetters(n.Setters, badge);
        return null;
    }

    private UIElement? UpdateContentDialog(ContentDialogElement o, ContentDialogElement n, FrameworkElement fe, Action requestRerender)
    {
        if (n.IsOpen && !o.IsOpen) ShowContentDialog(n, requestRerender);
        SetElementTag(fe, n);
        return null;
    }

    private UIElement? UpdateTeachingTip(TeachingTipElement n, WinUI.TeachingTip tip)
    {
        tip.Title = n.Title; tip.Subtitle = n.Subtitle ?? ""; tip.IsOpen = n.IsOpen;
        SetElementTag(tip, n);
        ApplySetters(n.Setters, tip);
        return null;
    }

    private UIElement? UpdateListView(ListViewElement o, ListViewElement n, WinUI.ListView lv, Action requestRerender)
    {
        lv.SelectionMode = n.SelectionMode;
        lv.IsItemClickEnabled = n.OnItemClick is not null;
        if (n.Header is not null) lv.Header = n.Header;

        // Update ItemsSource — ContainerContentChanging re-mounts visible items via Tag.
        // Always set a new list when items differ (even same count) so WinUI re-realizes containers.
        if (!ReferenceEquals(o.Items, n.Items))
            lv.ItemsSource = Enumerable.Range(0, n.Items.Length).ToList();

        SetElementTag(lv, n);

        if (n.SelectedIndex >= 0) lv.SelectedIndex = n.SelectedIndex;
        ApplySetters(n.Setters, lv);
        return null;
    }

    private UIElement? UpdateGridView(GridViewElement o, GridViewElement n, WinUI.GridView gv, Action requestRerender)
    {
        gv.SelectionMode = n.SelectionMode;
        gv.IsItemClickEnabled = n.OnItemClick is not null;
        if (n.Header is not null) gv.Header = n.Header;

        if (!ReferenceEquals(o.Items, n.Items))
            gv.ItemsSource = Enumerable.Range(0, n.Items.Length).ToList();

        SetElementTag(gv, n);

        if (n.SelectedIndex >= 0) gv.SelectedIndex = n.SelectedIndex;
        ApplySetters(n.Setters, gv);
        return null;
    }

    private UIElement? UpdateFlipView(FlipViewElement o, FlipViewElement n, WinUI.FlipView fv, Action requestRerender)
    {
        ReconcileItemsChildren(o.Items, n.Items, fv, requestRerender);
        fv.SelectedIndex = n.SelectedIndex;
        SetElementTag(fv, n);
        ApplySetters(n.Setters, fv);
        return null;
    }

    /// <summary>
    /// Walks visible (realized) containers and reconciles each item's Element
    /// using the stored ContentControl.Tag as the old element.
    /// Null containers (virtualized out) are skipped — ContainerContentChanging handles them on scroll.
    /// </summary>
    private void RefreshRealizedContainers(WinUI.ListViewBase listViewBase, TemplatedListElementBase newEl, Action requestRerender)
    {
        for (int i = 0; i < newEl.ItemCount; i++)
        {
            var container = listViewBase.ContainerFromIndex(i) as WinUI.ListViewItem;
            if (container?.ContentTemplateRoot is not ContentControl cc) continue;

            var oldItemElement = cc.Tag as Element;
            var newItemElement = newEl.BuildItemView(i);

            if (oldItemElement is not null && cc.Content is UIElement existingCtrl && CanUpdate(oldItemElement, newItemElement))
            {
                var replacement = Update(oldItemElement, newItemElement, existingCtrl, requestRerender);
                if (replacement is not null)
                    cc.Content = replacement;
            }
            else
            {
                if (cc.Content is UIElement oldCtrl)
                    Unmount(oldCtrl);
                cc.Content = Mount(newItemElement, requestRerender);
            }
            cc.Tag = newItemElement;
        }
    }

    private UIElement? UpdateTemplatedListView(TemplatedListElementBase o, TemplatedListElementBase n, WinUI.ListView lv, Action requestRerender)
    {
        lv.SelectionMode = n.GetSelectionMode();
        lv.IsItemClickEnabled = n.GetIsItemClickEnabled();
        var header = n.GetHeader();
        if (header is not null) lv.Header = header;

        if (o.ItemCount != n.ItemCount)
            lv.ItemsSource = Enumerable.Range(0, n.ItemCount).ToList();
        else if (!n.SameItemsAs(o))
            RefreshRealizedContainers(lv, n, requestRerender);

        SetElementTag(lv, n);

        var selectedIndex = n.GetSelectedIndex();
        if (selectedIndex >= 0) lv.SelectedIndex = selectedIndex;
        n.ApplyControlSetters(lv);
        return null;
    }

    private UIElement? UpdateTemplatedGridView(TemplatedListElementBase o, TemplatedListElementBase n, WinUI.GridView gv, Action requestRerender)
    {
        gv.SelectionMode = n.GetSelectionMode();
        gv.IsItemClickEnabled = n.GetIsItemClickEnabled();
        var header = n.GetHeader();
        if (header is not null) gv.Header = header;

        if (o.ItemCount != n.ItemCount)
            gv.ItemsSource = Enumerable.Range(0, n.ItemCount).ToList();
        else if (!n.SameItemsAs(o))
            RefreshRealizedContainers(gv, n, requestRerender);

        SetElementTag(gv, n);

        var selectedIndex = n.GetSelectedIndex();
        if (selectedIndex >= 0) gv.SelectedIndex = selectedIndex;
        n.ApplyControlSetters(gv);
        return null;
    }

    private UIElement? UpdateTemplatedFlipView(TemplatedListElementBase o, TemplatedListElementBase n, WinUI.FlipView fv, Action requestRerender)
    {
        // FlipView items are pre-mounted directly (no ContainerContentChanging).
        // Build old element array from o, then reconcile like regular items.
        int oldCount = o.ItemCount;
        int newCount = n.ItemCount;
        int shared = Math.Min(oldCount, newCount);

        for (int i = 0; i < shared; i++)
        {
            var oldItemElement = o.BuildItemView(i);
            var newItemElement = n.BuildItemView(i);
            if (fv.Items[i] is UIElement existingCtrl && CanUpdate(oldItemElement, newItemElement))
            {
                var replacement = Update(oldItemElement, newItemElement, existingCtrl, requestRerender);
                if (replacement is not null && replacement != existingCtrl)
                    fv.Items[i] = replacement;
            }
            else
            {
                if (fv.Items[i] is UIElement oldCtrl) Unmount(oldCtrl);
                fv.Items[i] = Mount(newItemElement, requestRerender)!;
            }
        }

        // Remove excess
        for (int i = oldCount - 1; i >= shared; i--)
        {
            if (fv.Items[i] is UIElement oldCtrl) Unmount(oldCtrl);
            fv.Items.RemoveAt(i);
        }

        // Add new
        for (int i = shared; i < newCount; i++)
        {
            var ctrl = Mount(n.BuildItemView(i), requestRerender);
            if (ctrl is not null) fv.Items.Add(ctrl);
        }

        fv.SelectedIndex = n.GetSelectedIndex();
        SetElementTag(fv, n);
        n.ApplyControlSetters(fv);
        return null;
    }

    private UIElement? UpdateLazyStack(LazyStackElementBase n, WinUI.ScrollViewer sv, Action requestRerender)
    {
        if (sv.Content is WinUI.ItemsRepeater repeater)
        {
            // Try to update the existing factory in place. This avoids
            // replacing ItemTemplate, which would cause ItemsRepeater to
            // re-realize all items (modifying Children during layout →
            // "Cannot run layout in the middle of a collection change").
            // The factory keeps its identity; existing realized items
            // stay mounted. On next scroll or layout, IElementFactory.GetElement
            // uses the updated viewBuilder to produce new content.
            if (repeater.ItemTemplate is IElementFactory existingFactory && n.TryUpdateFactory(existingFactory))
            {
                // Item count may have changed — update the source
                var newSource = n.GetItemsSource();
                if (newSource is IReadOnlyList<int> newList
                    && repeater.ItemsSource is IList<int> oldList
                    && newList.Count != oldList.Count)
                {
                    repeater.ItemsSource = newSource;
                }

                // Reconcile realized items with the new viewBuilder output.
                // This updates existing controls via property diffs — no
                // collection modifications on the ItemsRepeater.
                n.RefreshRealizedItems(existingFactory, repeater);
            }
            else
            {
                // First mount or type mismatch — full replacement
                repeater.ItemsSource = n.GetItemsSource();
                repeater.ItemTemplate = n.CreateFactory(this, requestRerender, _pool);
            }
            if (repeater.Layout is WinUI.StackLayout layout)
                layout.Spacing = n.Spacing;
            SetElementTag(repeater, n);
            ApplySetters(n.RepeaterSetters, repeater);
        }
        SetElementTag(sv, n);
        ApplySetters(n.ScrollViewerSetters, sv);
        return null;
    }

    private UIElement? UpdateMenuBar(MenuBarElement o, MenuBarElement n, WinUI.MenuBar mb)
    {
        int oldCount = o.Items.Length;
        int newCount = n.Items.Length;
        int shared = Math.Min(oldCount, newCount);

        // Patch shared top-level menus
        for (int i = 0; i < shared; i++)
        {
            var mbi = (WinUI.MenuBarItem)mb.Items[i];
            if (o.Items[i].Title != n.Items[i].Title)
                mbi.Title = n.Items[i].Title;
            UpdateMenuFlyoutItems(mbi.Items, o.Items[i].Items, n.Items[i].Items);
        }

        // Remove excess top-level menus
        for (int i = oldCount - 1; i >= shared; i--)
            mb.Items.RemoveAt(i);

        // Add new top-level menus
        for (int i = shared; i < newCount; i++)
        {
            var mbi = new WinUI.MenuBarItem { Title = n.Items[i].Title };
            foreach (var item in n.Items[i].Items)
                mbi.Items.Add(CreateMenuFlyoutItem(item));
            mb.Items.Add(mbi);
        }

        ApplySetters(n.Setters, mb);
        return null;
    }

    private void UpdateMenuFlyoutItems(
        global::System.Collections.Generic.IList<WinUI.MenuFlyoutItemBase> target,
        MenuFlyoutItemBase[] oldSource,
        MenuFlyoutItemBase[] newSource)
    {
        int oldCount = oldSource.Length;
        int newCount = newSource.Length;
        int shared = Math.Min(oldCount, newCount);

        for (int i = 0; i < shared; i++)
        {
            switch (newSource[i])
            {
                case MenuFlyoutItemData mfi when target[i] is WinUI.MenuFlyoutItem existing:
                    existing.Text = mfi.Text;
                    existing.IsEnabled = mfi.IsEnabled;
                    existing.Icon = ResolveIcon(mfi.IconElement, mfi.Icon);
                    if (mfi.AccessKey is not null) existing.AccessKey = mfi.AccessKey;
                    if (mfi.Description is not null)
                    {
                        WinUI.ToolTipService.SetToolTip(existing, mfi.Description);
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(existing, mfi.Description);
                    }
                    existing.Tag = mfi;
                    break;

                case ToggleMenuFlyoutItemData toggle when target[i] is WinUI.ToggleMenuFlyoutItem toggleItem:
                    toggleItem.Text = toggle.Text;
                    toggleItem.IsChecked = toggle.IsChecked;
                    toggleItem.Icon = ResolveIcon(toggle.IconElement, toggle.Icon);
                    toggleItem.Tag = toggle;
                    break;

                case RadioMenuFlyoutItemData radio when target[i] is WinUI.RadioMenuFlyoutItem radioItem:
                    radioItem.Text = radio.Text;
                    radioItem.IsChecked = radio.IsChecked;
                    radioItem.Tag = radio;
                    break;

                case MenuFlyoutSeparatorData when target[i] is WinUI.MenuFlyoutSeparator:
                    break; // nothing to update

                case MenuFlyoutSubItemData sub when target[i] is WinUI.MenuFlyoutSubItem subItem:
                    subItem.Text = sub.Text;
                    subItem.Icon = ResolveIcon(sub.IconElement, sub.Icon);
                    // Recursively patch sub-items
                    var oldSub = oldSource[i] is MenuFlyoutSubItemData oldSubData ? oldSubData.Items : [];
                    UpdateMenuFlyoutItems(subItem.Items, oldSub, sub.Items);
                    break;

                default:
                    // Type mismatch — replace the item
                    target[i] = CreateMenuFlyoutItem(newSource[i]);
                    break;
            }
        }

        // Remove excess
        for (int i = oldCount - 1; i >= shared; i--)
            target.RemoveAt(i);

        // Add new
        for (int i = shared; i < newCount; i++)
            target.Add(CreateMenuFlyoutItem(newSource[i]));
    }

    private UIElement? UpdateCommandHost(CommandHostElement o, CommandHostElement n, WinUI.Grid host, Action requestRerender)
    {
        // Update child element
        if (host.Children.Count > 0 && host.Children[0] is UIElement existingChild)
        {
            var replacement = UpdateChild(o.Child, n.Child, existingChild, requestRerender);
            if (replacement is not null)
            {
                UnmountChild(existingChild);
                host.Children[0] = replacement;
            }
        }
        else
        {
            var child = Mount(n.Child, requestRerender);
            if (child is not null) host.Children.Add(child);
        }

        // Rebuild accelerators — clear and re-add (commands may have changed enabled state or handlers)
        host.KeyboardAccelerators.Clear();
        AddCommandHostAccelerators(host, n.Commands);

        SetElementTag(host, n);
        return null;
    }

    private UIElement? UpdateCommandBar(CommandBarElement o, CommandBarElement n, WinUI.CommandBar cb, Action requestRerender)
    {
        cb.DefaultLabelPosition = n.DefaultLabelPosition;
        cb.IsOpen = n.IsOpen;

        // Update primary commands in-place
        UpdateAppBarItems(cb.PrimaryCommands, n.PrimaryCommands);
        UpdateAppBarItems(cb.SecondaryCommands, n.SecondaryCommands);

        SetElementTag(cb, n);
        ApplySetters(n.Setters, cb);
        return null;
    }

    private static void UpdateAppBarItems(
        global::System.Collections.Generic.IList<WinUI.ICommandBarElement> target,
        AppBarItemBase[]? source)
    {
        int newCount = source?.Length ?? 0;
        int oldCount = target.Count;

        // Update shared range (only update if types match, otherwise replace)
        int shared = Math.Min(oldCount, newCount);
        for (int i = 0; i < shared; i++)
        {
            if (source is null) continue;
            switch (source[i])
            {
                case AppBarButtonData cmd when target[i] is WinUI.AppBarButton abb:
                    abb.Label = cmd.Label;
                    abb.IsEnabled = cmd.IsEnabled;
                    abb.Icon = ResolveIcon(cmd.IconElement, cmd.Icon);
                    if (cmd.AccessKey is not null) abb.AccessKey = cmd.AccessKey;
                    if (cmd.Description is not null)
                    {
                        WinUI.ToolTipService.SetToolTip(abb, cmd.Description);
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(abb, cmd.Description);
                    }
                    abb.Tag = cmd;
                    break;
                case AppBarToggleButtonData toggle when target[i] is WinUI.AppBarToggleButton atb:
                    atb.Label = toggle.Label;
                    atb.IsChecked = toggle.IsChecked;
                    if (toggle.Icon is not null) atb.Icon = new WinUI.SymbolIcon(ParseSymbol(toggle.Icon));
                    atb.Tag = toggle;
                    break;
                case AppBarSeparatorData when target[i] is WinUI.AppBarSeparator:
                    break; // nothing to update
                default:
                    // Type mismatch — replace
                    target[i] = CreateAppBarItem(source[i]);
                    break;
            }
        }

        // Remove excess
        for (int i = oldCount - 1; i >= shared; i--)
            target.RemoveAt(i);

        // Add new
        if (source is not null)
            for (int i = shared; i < newCount; i++)
                target.Add(CreateAppBarItem(source[i]));
    }

    private UIElement? UpdateGrid(Core.GridElement o, Core.GridElement n, WinUI.Grid g, Action requestRerender)
    {
        if (o.RowSpacing != n.RowSpacing) g.RowSpacing = n.RowSpacing;
        if (o.ColumnSpacing != n.ColumnSpacing) g.ColumnSpacing = n.ColumnSpacing;

        // Update column/row definitions when the GridDefinition changes.
        if (!ReferenceEquals(o.Definition, n.Definition))
        {
            var newCols = n.Definition.Columns;
            if (newCols.Length != g.ColumnDefinitions.Count)
            {
                g.ColumnDefinitions.Clear();
                foreach (var col in newCols) g.ColumnDefinitions.Add(ParseColumnDef(col));
            }
            else
            {
                for (int i = 0; i < newCols.Length; i++)
                {
                    var parsed = ParseColumnDef(newCols[i]);
                    if (g.ColumnDefinitions[i].Width != parsed.Width)
                        g.ColumnDefinitions[i].Width = parsed.Width;
                }
            }

            var newRows = n.Definition.Rows;
            if (newRows.Length != g.RowDefinitions.Count)
            {
                g.RowDefinitions.Clear();
                foreach (var row in newRows) g.RowDefinitions.Add(ParseRowDef(row));
            }
            else
            {
                for (int i = 0; i < newRows.Length; i++)
                {
                    var parsed = ParseRowDef(newRows[i]);
                    if (g.RowDefinitions[i].Height != parsed.Height)
                        g.RowDefinitions[i].Height = parsed.Height;
                }
            }
        }

        // When old and new child counts differ, the tree structure changed (e.g., a split
        // was added or removed). Delegate to ChildReconciler which handles this safely,
        // including the keyed path.
        if (o.Children.Length != n.Children.Length || o.Children.Length != g.Children.Count)
        {
            ReconcileChildren(o.Children, n.Children, g, requestRerender);
            // Re-apply grid placement for all children after reconciliation
            for (int i = 0; i < n.Children.Length && i < g.Children.Count; i++)
            {
                var ga = n.Children[i].GetAttached<GridAttached>();
                if (ga is not null && g.Children[i] is FrameworkElement fe)
                {
                    WinUI.Grid.SetRow(fe, ga.Row);
                    WinUI.Grid.SetColumn(fe, ga.Column);
                    if (ga.RowSpan > 1) WinUI.Grid.SetRowSpan(fe, ga.RowSpan);
                    if (ga.ColumnSpan > 1) WinUI.Grid.SetColumnSpan(fe, ga.ColumnSpan);
                }
            }
            SetElementTag(g, n);
            ApplySetters(n.Setters, g);
            return null;
        }

        // Fast path: same child count — reconcile positionally with bounds checks.
        int count = o.Children.Length;

        for (int i = 0; i < count; i++)
        {
            var oldChild = o.Children[i];
            var newChild = n.Children[i];

            // Early skip: element + modifiers + attached all identical — avoid COM
            // g.Children[i] read entirely. ShallowEquals already checks modifiers
            // and attached (GridAttached), so grid placement is also covered.
            if (Element.CanSkipUpdate(oldChild, newChild))
                continue;

            // Guard: recursive Reconcile may have modified g.Children (e.g., via
            // component re-renders that remove children from this grid).
            if (i >= g.Children.Count) break;

            var existingCtrl = g.Children[i];
            var replacement = Reconcile(oldChild, newChild, existingCtrl, requestRerender);
            var wasReplaced = replacement is not null && replacement != existingCtrl;
            if (wasReplaced && i < g.Children.Count)
            {
                g.Children[i] = replacement!;
            }
            // Update grid placement — re-apply when control was replaced (new control
            // defaults to Row=0/Column=0) or when the attached data changed.
            if (i < g.Children.Count)
            {
                var oldGa = oldChild.GetAttached<GridAttached>();
                var ga = newChild.GetAttached<GridAttached>();
                if (ga is not null && (wasReplaced || ga != oldGa) && g.Children[i] is FrameworkElement ctrl)
                {
                    WinUI.Grid.SetRow(ctrl, ga.Row);
                    WinUI.Grid.SetColumn(ctrl, ga.Column);
                    if (ga.RowSpan > 1) WinUI.Grid.SetRowSpan(ctrl, ga.RowSpan);
                    if (ga.ColumnSpan > 1) WinUI.Grid.SetColumnSpan(ctrl, ga.ColumnSpan);
                }
            }
        }

        SetElementTag(g, n);
        ApplySetters(n.Setters, g);
        return null;
    }

    private UIElement? UpdateTreeView(TreeViewElement o, TreeViewElement n, WinUI.TreeView tv, Action requestRerender)
    {
        // If the node data is reference-equal (same arrays), skip entirely
        if (ReferenceEquals(o.Nodes, n.Nodes))
        {
            SetElementTag(tv, n);
            return null;
        }

        // Diff the node tree to minimize WinUI interop calls
        DiffTreeViewNodes(tv.RootNodes, o.Nodes, n.Nodes, requestRerender);

        tv.SelectionMode = n.SelectionMode;
        SetElementTag(tv, n);
        ApplySetters(n.Setters, tv);
        return null;
    }

    /// <summary>
    /// Recursively diff TreeViewNode lists, reusing existing nodes where Content matches.
    /// Only adds/removes/updates nodes that actually changed, minimizing COM interop calls.
    /// Also reconciles ContentElement changes on existing nodes.
    ///
    /// Algorithm: Snapshot existing live nodes into a Content→node map. Clear the live list,
    /// then rebuild it in new order — reusing matched nodes and creating fresh ones.
    /// </summary>
    private void DiffTreeViewNodes(
        IList<WinUI.TreeViewNode> liveNodes,
        TreeViewNodeData[] oldData,
        TreeViewNodeData[] newData,
        Action requestRerender)
    {
        // Snapshot: map old Content → (live node, old data index).
        // Use the old data array for indexing since liveNodes mirrors it 1:1.
        var liveByContent = new Dictionary<string, (WinUI.TreeViewNode Node, int OldIdx)>(oldData.Length);
        for (int i = 0; i < oldData.Length && i < liveNodes.Count; i++)
            liveByContent.TryAdd(oldData[i].Content, (liveNodes[i], i));

        // Detach all live nodes so we can re-insert in new order
        liveNodes.Clear();

        for (int i = 0; i < newData.Length; i++)
        {
            var nd = newData[i];

            if (liveByContent.Remove(nd.Content, out var match))
            {
                var liveNode = match.Node;
                var oldNodeData = oldData[match.OldIdx];

                if (liveNode.IsExpanded != nd.IsExpanded)
                    liveNode.IsExpanded = nd.IsExpanded;

                ReconcileTreeNodeContent(liveNode, oldNodeData, nd, requestRerender);

                // Diff children
                var oldChildren = oldNodeData.Children;
                var newChildren = nd.Children;

                if (!ReferenceEquals(oldChildren, newChildren))
                {
                    if (newChildren is null)
                        liveNode.Children.Clear();
                    else if (oldChildren is null)
                    {
                        liveNode.Children.Clear();
                        foreach (var child in newChildren)
                            liveNode.Children.Add(CreateTreeNode(child));
                    }
                    else
                        DiffTreeViewNodes(liveNode.Children, oldChildren, newChildren, requestRerender);
                }

                liveNodes.Add(liveNode);
            }
            else
            {
                // New node
                liveNodes.Add(CreateTreeNode(nd));
            }
        }
        // Unmatched old nodes are simply not re-added — they're dropped.
    }

    /// <summary>
    /// Reconciles ContentElement changes on a TreeViewNode.
    /// When ContentElement is used, node.Content holds a mounted UIElement.
    /// </summary>
    private void ReconcileTreeNodeContent(
        WinUI.TreeViewNode liveNode,
        TreeViewNodeData? oldData,
        TreeViewNodeData newData,
        Action requestRerender)
    {
        var oldContentEl = oldData?.ContentElement;
        var newContentEl = newData.ContentElement;

        if (newContentEl is null && oldContentEl is null) return; // Both text-only, no change needed

        if (newContentEl is not null && oldContentEl is not null
            && liveNode.Content is UIElement existingCtrl
            && CanUpdate(oldContentEl, newContentEl))
        {
            // Reconcile in place
            var replacement = Update(oldContentEl, newContentEl, existingCtrl, requestRerender);
            if (replacement is not null)
                liveNode.Content = replacement;
        }
        else if (newContentEl is not null)
        {
            // Mount new content element
            if (liveNode.Content is UIElement oldCtrl)
                Unmount(oldCtrl);
            liveNode.Content = Mount(newContentEl, requestRerender);
        }
        else
        {
            // ContentElement removed, revert to data
            if (liveNode.Content is UIElement oldCtrl2)
                Unmount(oldCtrl2);
            liveNode.Content = newData;
        }
    }

    private UIElement? UpdateRectangle(RectangleElement n, WinShapes.Rectangle r)
    {
        if (n.Fill is not null) r.Fill = n.Fill;
        if (n.Stroke is not null) r.Stroke = n.Stroke;
        r.StrokeThickness = n.StrokeThickness;
        r.RadiusX = n.RadiusX;
        r.RadiusY = n.RadiusY;
        ApplySetters(n.Setters, r);
        return null;
    }

    private UIElement? UpdateEllipse(EllipseElement n, WinShapes.Ellipse e)
    {
        if (n.Fill is not null) e.Fill = n.Fill;
        if (n.Stroke is not null) e.Stroke = n.Stroke;
        e.StrokeThickness = n.StrokeThickness;
        ApplySetters(n.Setters, e);
        return null;
    }

    private UIElement? UpdateLine(LineElement n, WinShapes.Line l)
    {
        l.X1 = n.X1; l.Y1 = n.Y1;
        l.X2 = n.X2; l.Y2 = n.Y2;
        if (n.Stroke is not null) l.Stroke = n.Stroke;
        l.StrokeThickness = n.StrokeThickness;
        ApplySetters(n.Setters, l);
        return null;
    }

    private UIElement? UpdatePath(PathElement o, PathElement n, WinShapes.Path p)
    {
        // Skip expensive COM Geometry property set when the path data string hasn't changed.
        // PathDataParser.Parse creates a new PathGeometry COM object every call, so
        // reference equality is never true — compare the source string instead.
        bool pathChanged = n.PathDataString is null
            ? n.Data is not null
            : !string.Equals(n.PathDataString, o.PathDataString, StringComparison.Ordinal);
        if (pathChanged && n.Data is not null) p.Data = n.Data;

        if (n.Fill is not null) p.Fill = n.Fill;
        if (n.Stroke is not null) p.Stroke = n.Stroke;
        p.StrokeThickness = n.StrokeThickness;
        if (n.StrokeDashArray is not null) p.StrokeDashArray = n.StrokeDashArray;
        if (n.RenderTransform is not null) p.RenderTransform = n.RenderTransform;
        ApplySetters(n.Setters, p);
        return null;
    }

    private UIElement? UpdateMediaPlayerElement(MediaPlayerElementElement n, WinUI.MediaPlayerElement mpe)
    {
        mpe.AreTransportControlsEnabled = n.AreTransportControlsEnabled;
        mpe.AutoPlay = n.AutoPlay;
        SetElementTag(mpe, n);
        ApplySetters(n.Setters, mpe);
        return null;
    }

    private UIElement? UpdateAnimatedVisualPlayer(AnimatedVisualPlayerElement n, WinUI.AnimatedVisualPlayer avp)
    {
        avp.AutoPlay = n.AutoPlay;
        SetElementTag(avp, n);
        ApplySetters(n.Setters, avp);
        return null;
    }

    private UIElement? UpdatePipsPager(PipsPagerElement n, WinUI.PipsPager pp)
    {
        pp.NumberOfPages = n.NumberOfPages;
        pp.SelectedPageIndex = n.SelectedPageIndex;
        SetElementTag(pp, n);
        ApplySetters(n.Setters, pp);
        return null;
    }

    private UIElement? UpdateAnnotatedScrollBar(AnnotatedScrollBarElement n, WinUI.AnnotatedScrollBar asb)
    {
        ApplySetters(n.Setters, asb);
        return null;
    }

    private UIElement? UpdateCalendarView(CalendarViewElement n, WinUI.CalendarView cv)
    {
        cv.SelectionMode = n.SelectionMode;
        cv.IsGroupLabelVisible = n.IsGroupLabelVisible;
        cv.IsOutOfScopeEnabled = n.IsOutOfScopeEnabled;
        if (n.CalendarIdentifier is not null) cv.CalendarIdentifier = n.CalendarIdentifier;
        if (n.Language is not null && global::Windows.Globalization.Language.IsWellFormed(n.Language))
            cv.Language = n.Language;
        ApplySetters(n.Setters, cv);
        return null;
    }

    private UIElement? UpdateAnimatedIcon(AnimatedIconElement n, WinUI.AnimatedIcon ai)
    {
        if (n.Source is Microsoft.UI.Xaml.Controls.IAnimatedVisualSource2 src)
            ai.Source = src;
        if (n.FallbackIconSource is not null) ai.FallbackIconSource = n.FallbackIconSource;
        ApplySetters(n.Setters, ai);
        return null;
    }

    private UIElement? UpdateMapControl(MapControlElement n, WinUI.MapControl mc)
    {
        mc.ZoomLevel = n.ZoomLevel;
        if (n.MapServiceToken is not null) mc.MapServiceToken = n.MapServiceToken;
        ApplySetters(n.Setters, mc);
        return null;
    }

    private UIElement? UpdateFrame(FrameElement n, WinUI.Frame f)
    {
        // Frame navigation is inherently imperative — only apply setters on update
        ApplySetters(n.Setters, f);
        return null;
    }

    private UIElement? UpdateFormField(
        FormFieldElement oldFf, FormFieldElement newFf,
        WinUI.StackPanel panel, Action requestRerender)
    {
        // Fixed 3-child layout: [0] label, [1] content, [2] description/error
        if (panel.Children.Count != 3)
            return Mount(newFf, requestRerender);

        var fieldName = FormFieldHelpers.ResolveFieldName(newFf.FieldName, newFf.Content);

        // Auto-validate
        var attached = newFf.Content.GetAttached<ValidationAttached>();
        var valCtx = _contextScope.Read(ValidationContexts.Current);
        if (valCtx is not null && attached is not null && attached.Validators.Length > 0)
        {
            ValidationReconciler.ValidateAttached(valCtx, attached, attached.Value);
        }

        // [0] Update label
        if (panel.Children[0] is TextBlock labelTb)
        {
            var displayLabel = FormFieldHelpers.GetDisplayLabel(newFf.Label, newFf.Required);
            labelTb.Text = displayLabel;
            labelTb.Visibility = displayLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // [1] Patch content in-place (preserves caret position and focus)
        var existingContent = panel.Children[1];
        if (CanUpdate(oldFf.Content, newFf.Content))
        {
            var replacement = Update(oldFf.Content, newFf.Content, existingContent, requestRerender);
            if (replacement is not null)
            {
                // WinUI indexer assignment doesn't fully disconnect the old element's
                // parent state — use RemoveAt+Insert (see ChildCollection.Replace).
                Unmount(existingContent);
                panel.Children.RemoveAt(1);
                panel.Children.Insert(1, replacement);
                existingContent = replacement;
            }
        }
        else
        {
            // Content element type changed — must remount
            Unmount(existingContent);
            panel.Children.RemoveAt(1);
            var newContent = Mount(newFf.Content, requestRerender)
                ?? new WinUI.StackPanel { Visibility = Visibility.Collapsed };
            panel.Children.Insert(1, newContent);
            existingContent = newContent;
        }

        ApplyFormFieldAutomation(existingContent, newFf.Label);
        ApplyFormFieldErrorStyling(existingContent, valCtx, fieldName, newFf.ShowWhen);

        // [2] Update description/error text
        if (panel.Children[2] is TextBlock descTb)
        {
            ApplyFormFieldDescription(descTb, valCtx, fieldName, newFf.Description, newFf.ShowWhen);
        }

        SetElementTag(panel, newFf);
        return null; // patched in-place
    }

    private UIElement? UpdateValidationVisualizer(
        ValidationVisualizerElement oldVv, ValidationVisualizerElement newVv,
        WinUI.StackPanel panel, Action requestRerender)
    {
        // The visualizer layout varies by style and message state, so a full in-place
        // patch is complex. However, we can at least reconcile the content child when
        // styles match and the content element is updatable.
        if (oldVv.Style != newVv.Style)
            return Mount(newVv, requestRerender);

        // Find the content child — it's the form control, not the error display chrome.
        // In MountValidationVisualizer, content is added after style-specific elements,
        // except for Inline where error text comes after content.
        // For simplicity and correctness, remount the visualizer but reconcile the
        // content subtree to preserve control state.
        return Mount(newVv, requestRerender);
    }

    private UIElement? UpdateValidationRule(ValidationRuleElement rule)
    {
        var valCtx = _contextScope.Read(ValidationContexts.Current);
        if (valCtx is not null)
            rule.Evaluate(valCtx);
        return null; // keep existing collapsed placeholder
    }

    private UIElement? UpdateErrorBoundary(
        ErrorBoundaryElement oldEb, ErrorBoundaryElement newEb,
        UIElement control, Action requestRerender)
    {
        if (!_errorBoundaryNodes.TryGetValue(control, out var node))
            return Mount(newEb, requestRerender);

        var wrapper = (Border)control;
        var existingChild = wrapper.Child;

        // Always retry the child on re-render (error recovery).
        Element newRendered;
        Exception? caughtEx = null;

        _errorBoundaryDepth++;
        try
        {
            newRendered = newEb.Child;
            var newControl = Reconcile(node.RenderedElement, newEb.Child, existingChild, requestRerender);
            if (newControl != existingChild)
                wrapper.Child = newControl;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ErrorBoundary caught render error during update");
            caughtEx = ex;
            if (existingChild is not null)
                Unmount(existingChild);
            newRendered = newEb.Fallback(ex);
            wrapper.Child = Mount(newRendered, requestRerender);
        }
        finally
        {
            _errorBoundaryDepth--;
        }

        node.ChildElement = newEb.Child;
        node.RenderedElement = newRendered;
        node.CaughtException = caughtEx;
        node.Fallback = newEb.Fallback;

        return null;
    }

    private UIElement? UpdateComponent(Element oldEl, Element newEl, UIElement control, Action requestRerender)
    {
        ReconcileComponent(oldEl, newEl, control, requestRerender);
        return null;
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "…";

    // ════════════════════════════════════════════════════════════════════
    //  XamlHostElement / XamlPageElement — built-in XAML interop
    // ════════════════════════════════════════════════════════════════════

    private static UIElement? UpdateXamlHost(XamlHostElement newEl, FrameworkElement control)
    {
        newEl.Updater?.Invoke(control);
        SetElementTag(control, newEl);
        return null; // updated in place
    }

    private static UIElement? UpdateXamlPage(XamlPageElement oldEl, XamlPageElement newEl, WinUI.Frame frame)
    {
        if (oldEl.PageType != newEl.PageType || !Equals(oldEl.Parameter, newEl.Parameter))
            frame.Navigate(newEl.PageType, newEl.Parameter);
        SetElementTag(frame, newEl);
        return null; // updated in place
    }

    // ════════════════════════════════════════════════════════════════════
    //  SemanticElement — composite accessibility wrapper
    // ════════════════════════════════════════════════════════════════════

    private UIElement? UpdateSemantic(
        SemanticElement oldSem, SemanticElement newSem,
        Accessibility.SemanticPanel panel, Action requestRerender)
    {
        // Update semantic properties if changed
        var s = newSem.Semantics;
        if (oldSem.Semantics.Role != s.Role)
            panel.SemanticRole = s.Role;
        if (oldSem.Semantics.Value != s.Value)
            panel.SemanticValue = s.Value;
        if (oldSem.Semantics.RangeMin != s.RangeMin)
            panel.RangeMinimum = s.RangeMin ?? 0.0;
        if (oldSem.Semantics.RangeMax != s.RangeMax)
            panel.RangeMaximum = s.RangeMax ?? 0.0;
        if (oldSem.Semantics.RangeValue != s.RangeValue)
            panel.RangeValue = s.RangeValue ?? 0.0;
        if (oldSem.Semantics.IsReadOnly != s.IsReadOnly)
            panel.IsReadOnly = s.IsReadOnly;

        // Reconcile the child element
        var existingChild = panel.Children.Count > 0 ? panel.Children[0] : null;
        var newChild = Reconcile(oldSem.Child, newSem.Child, existingChild, requestRerender);
        if (newChild != existingChild)
        {
            panel.Children.Clear();
            if (newChild is not null)
                panel.Children.Add(newChild);
        }

        SetElementTag(panel, newSem);
        return null; // updated in place
    }
}
