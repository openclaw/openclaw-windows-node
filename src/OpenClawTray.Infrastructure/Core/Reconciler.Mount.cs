using OpenClawTray.Infrastructure.Animation;
using OpenClawTray.Infrastructure.Hooks;
using OpenClawTray.Infrastructure.Hosting;
using OpenClawTray.Infrastructure.Controls.Validation;
using Validation = OpenClawTray.Infrastructure.Controls.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using WinUI = Microsoft.UI.Xaml.Controls;
using WinPrim = Microsoft.UI.Xaml.Controls.Primitives;
using WinShapes = Microsoft.UI.Xaml.Shapes;
namespace OpenClawTray.Infrastructure.Core;

// AI-HINT: Reconciler.Mount.cs — creates real WinUI controls from Element descriptions.
// Mount() is a big switch over all Element subtypes → MountXxx() methods.
// Each MountXxx allocates (or rents from pool) a WinUI control, sets properties,
// wires event handlers via Tag pattern, and recurses for children.
// The Tag pattern: event handlers read the current Element from control.Tag to
// dispatch callbacks, so handlers are wired once and survive element recycling.
// Context values are pushed/popped around child processing.

public sealed partial class Reconciler
{
    /// <summary>
    /// Creates a WinUI control tree from an Element tree. Returns null for EmptyElement.
    /// </summary>
    public UIElement? Mount(Element element, Action requestRerender)
    {
        // Unwrap legacy ModifiedElement (backward compat)
        ElementModifiers? modifiers = element.Modifiers;
        if (element is ModifiedElement mod)
        {
            modifiers = mod.WrappedModifiers;
            if (mod.Inner.Modifiers is not null)
                modifiers = modifiers.Merge(mod.Inner.Modifiers);
            element = mod.Inner;
        }

        // Push context values onto scope before processing children
        var ctxValues = element.ContextValues;
        int ctxCount = 0;
        if (ctxValues is { Count: > 0 })
        {
            _contextScope.Push(ctxValues);
            ctxCount = ctxValues.Count;
        }

        UIElement? control;
        // Push stagger scope if this element has StaggerConfig — children mounted
        // inside MountXxx will consume stagger indices for their enter transitions.
        bool pushedStagger = element.StaggerConfig is not null;
        if (pushedStagger)
            PushStaggerScope(element.StaggerConfig!.Delay);
        try
        {

        // Registered types checked first
        if (_typeRegistry.TryGetValue(element.GetType(), out var reg))
        {
            control = reg.Mount(element, requestRerender, this);
        }
        else
        {
        control = element switch
        {
            TextBlockElement text => MountText(text),
            RichTextBlockElement richText => MountRichTextBlock(richText),
            ButtonElement btn => MountButton(btn, requestRerender),
            HyperlinkButtonElement hlBtn => MountHyperlinkButton(hlBtn),
            RepeatButtonElement repBtn => MountRepeatButton(repBtn),
            ToggleButtonElement togBtn => MountToggleButton(togBtn),
            DropDownButtonElement ddBtn => MountDropDownButton(ddBtn, requestRerender),
            SplitButtonElement spBtn => MountSplitButton(spBtn, requestRerender),
            ToggleSplitButtonElement tspBtn => MountToggleSplitButton(tspBtn, requestRerender),
            RichEditBoxElement reb => MountRichEditBox(reb),
            TextFieldElement tf => MountTextField(tf, requestRerender),
            PasswordBoxElement pw => MountPasswordBox(pw),
            NumberBoxElement nb => MountNumberBox(nb),
            AutoSuggestBoxElement asb => MountAutoSuggestBox(asb),
            CheckBoxElement cb => MountCheckBox(cb),
            RadioButtonElement rb => MountRadioButton(rb),
            RadioButtonsElement rbs => MountRadioButtons(rbs),
            ComboBoxElement combo => MountComboBox(combo, requestRerender),
            SliderElement sl => MountSlider(sl),
            ToggleSwitchElement ts => MountToggleSwitch(ts),
            RatingControlElement rc => MountRatingControl(rc),
            ColorPickerElement cp => MountColorPicker(cp),
            CalendarDatePickerElement cdp => MountCalendarDatePicker(cdp),
            DatePickerElement dp => MountDatePicker(dp),
            TimePickerElement tp => MountTimePicker(tp),
            ProgressElement prog => MountProgress(prog),
            ProgressRingElement ring => MountProgressRing(ring),
            ImageElement img => MountImage(img),
            PersonPictureElement pp => MountPersonPicture(pp),
            WebView2Element wv => MountWebView2(wv),
            WrapGridElement wg => MountWrapGrid(wg, requestRerender),
            StackElement stack => MountStack(stack, requestRerender),
            GridElement grid => MountGrid(grid, requestRerender),
            ScrollViewElement scroll => MountScrollView(scroll, requestRerender),
            BorderElement border => MountBorder(border, requestRerender),
            ExpanderElement exp => MountExpander(exp, requestRerender),
            SplitViewElement sv => MountSplitView(sv, requestRerender),
            ViewboxElement vb => MountViewbox(vb, requestRerender),
            CanvasElement cvs => MountCanvas(cvs, requestRerender),
            NavigationHostElement navHost => MountNavigationHost(navHost, requestRerender),
            NavigationViewElement nav => MountNavigationView(nav, requestRerender),
            TitleBarElement tb => MountTitleBar(tb, requestRerender),
            TabViewElement tab => MountTabView(tab, requestRerender),
            BreadcrumbBarElement bcb => MountBreadcrumbBar(bcb),
            PivotElement pvt => MountPivot(pvt, requestRerender),
            ListViewElement lv => MountListView(lv, requestRerender),
            GridViewElement gv => MountGridView(gv, requestRerender),
            TreeViewElement tv => MountTreeView(tv, requestRerender),
            FlipViewElement fv => MountFlipView(fv, requestRerender),
            InfoBarElement ib => MountInfoBar(ib),
            InfoBadgeElement badge => MountInfoBadge(badge),
            ContentDialogElement cdEl => MountContentDialog(cdEl, requestRerender),
            FlyoutElement flyEl => MountFlyout(flyEl, requestRerender),
            TeachingTipElement ttEl => MountTeachingTip(ttEl, requestRerender),
            MenuBarElement mbEl => MountMenuBar(mbEl),
            CommandBarElement cmdEl => MountCommandBar(cmdEl, requestRerender),
            MenuFlyoutElement mfEl => MountMenuFlyout(mfEl, requestRerender),
            TemplatedListElementBase tl => MountTemplatedList(tl, requestRerender),
            LazyStackElementBase lazy => MountLazyStack(lazy, requestRerender),
            RectangleElement rect => MountRectangle(rect),
            EllipseElement ell => MountEllipse(ell),
            LineElement ln => MountLine(ln),
            PathElement pa => MountPath(pa),
            RelativePanelElement rp => MountRelativePanel(rp, requestRerender),
            MediaPlayerElementElement mpe => MountMediaPlayerElement(mpe),
            AnimatedVisualPlayerElement avp => MountAnimatedVisualPlayer(avp),
            SemanticZoomElement sz => MountSemanticZoom(sz, requestRerender),
            ListBoxElement lb => MountListBox(lb),
            SelectorBarElement sb => MountSelectorBar(sb),
            PipsPagerElement pp => MountPipsPager(pp),
            AnnotatedScrollBarElement asb => MountAnnotatedScrollBar(asb),
            PopupElement popup => MountPopup(popup, requestRerender),
            RefreshContainerElement rc => MountRefreshContainer(rc, requestRerender),
            CommandBarFlyoutElement cbf => MountCommandBarFlyout(cbf, requestRerender),
            CalendarViewElement cv => MountCalendarView(cv),
            SwipeControlElement swipe => MountSwipeControl(swipe, requestRerender),
            AnimatedIconElement ai => MountAnimatedIcon(ai),
            ParallaxViewElement pv => MountParallaxView(pv, requestRerender),
            MapControlElement mc => MountMapControl(mc),
            FrameElement frame => MountFrame(frame),
            CommandHostElement ch => MountCommandHost(ch, requestRerender),
            ErrorBoundaryElement eb => MountErrorBoundary(eb, requestRerender),
            Validation.FormFieldElement ff => MountFormField(ff, requestRerender),
            Validation.ValidationVisualizerElement vv => MountValidationVisualizer(vv, requestRerender),
            Validation.ValidationRuleElement rule => MountValidationRule(rule),
            SemanticElement sem => MountSemantic(sem, requestRerender),
            AnnounceRegionElement ann => MountAnnounceRegion(ann),
            XamlHostElement host => MountXamlHost(host),
            XamlPageElement page => MountXamlPage(page),
            ComponentElement comp => MountComponent(comp, requestRerender),
            FuncElement func => MountFuncComponent(func, requestRerender),
            MemoElement memo => MountMemoComponent(memo, requestRerender),
            _ => null,
        };
        }

        if (control is not null)
        {
            DebugUIElementsCreated++;
            if (_highlightMounted is not null)
                _highlightMounted.Add(control);
        }

        // Apply inline modifiers after mounting
        if (modifiers is not null && control is FrameworkElement fe)
            ApplyModifiers(fe, modifiers, requestRerender);

        // After modifiers + setters have had a chance to set an explicit
        // AutomationName, fall back to the control's visible caption so UIA
        // clients that read AutomationProperties.Name directly don't see an
        // empty string on a Button("Save", …). Author-supplied names win.
        if (control is FrameworkElement captionFe)
            ApplyDefaultAutomationName(captionFe, ResolveCaptionForElement(element));

        // Apply theme-resource bindings (ThemeRef → resolved Brush from WinUI resources)
        if (element.ThemeBindings is not null && control is FrameworkElement thFe)
            ApplyThemeBindings(thFe, element.ThemeBindings);

        // Apply per-control resource overrides (lightweight styling)
        if (element.ResourceOverrides is not null && control is FrameworkElement resFe)
            ApplyResourceOverrides(resFe, null, element.ResourceOverrides);

        // Apply transitions after mounting (runs after .Set() callbacks)
        if (control is not null && (element.ImplicitTransitions is not null || element.ThemeTransitions is not null))
            ApplyTransitions(control, element.ImplicitTransitions, element.ThemeTransitions);

        // Apply Composition-layer layout animation (implicit Offset/Size animation on Visual)
        if (control is not null && element.LayoutAnimation is not null)
            ApplyLayoutAnimation(control, element.LayoutAnimation);

        // Apply compositor property animation (.Animate() modifier)
        if (control is not null && element.AnimationConfig is not null)
            ApplyPropertyAnimation(control, element.AnimationConfig, element.LayoutAnimation);

        // Apply enter transition (.Transition() modifier)
        if (control is not null && element.ElementTransition is not null)
        {
            var (staggerIdx, staggerDly) = ConsumeStaggerIndex();
            ApplyEnterTransition(control, element.ElementTransition, staggerIdx, staggerDly);
        }

        // Apply interaction states (.InteractionStates() modifier)
        if (control is not null && element.InteractionStates is not null)
            ApplyInteractionStates(control, element.InteractionStates);

        // Apply keyframe animations (.Keyframes() modifier)
        if (control is not null && element.KeyframeAnimations is not null)
            ApplyKeyframeAnimations(control, element.KeyframeAnimations);

        // Apply scroll-linked expression animations (.ScrollLinked() modifier)
        if (control is not null && element.ScrollAnimation is not null)
            ApplyScrollAnimation(control, element.ScrollAnimation);

        // Apply stagger delays to children (.Stagger() modifier)
        if (control is not null && element.StaggerConfig is not null)
            ApplyStaggerDelays(control, element.StaggerConfig);

        // Queue connected animation start if a prepared animation exists with this key
        if (control is not null && element.ConnectedAnimationKey is not null)
            QueueConnectedAnimationStart(control, element.ConnectedAnimationKey);

        }
        finally
        {
            if (pushedStagger)
                PopStaggerScope();
            if (ctxCount > 0)
                _contextScope.Pop(ctxCount);
        }

        return control;
    }

    private TextBlock MountText(TextBlockElement text)
    {
        var tb = _pool.TryRent(typeof(TextBlock)) as TextBlock ?? new TextBlock();
        tb.Text = text.Content;
        if (text.FontSize.HasValue) tb.FontSize = text.FontSize.Value;
        if (text.Weight.HasValue) tb.FontWeight = text.Weight.Value;
        if (text.FontStyle.HasValue) tb.FontStyle = text.FontStyle.Value;
        if (text.HorizontalAlignment.HasValue) tb.HorizontalAlignment = text.HorizontalAlignment.Value;
        if (text.TextWrapping.HasValue) tb.TextWrapping = text.TextWrapping.Value;
        if (text.TextAlignment.HasValue) tb.TextAlignment = text.TextAlignment.Value;
        if (text.TextTrimming.HasValue) tb.TextTrimming = text.TextTrimming.Value;
        if (text.IsTextSelectionEnabled.HasValue) tb.IsTextSelectionEnabled = text.IsTextSelectionEnabled.Value;
        if (text.FontFamily is not null) tb.FontFamily = text.FontFamily;
        ApplySetters(text.Setters, tb);
        return tb;
    }

    private WinUI.RichTextBlock MountRichTextBlock(RichTextBlockElement richText)
    {
        var rtb = _pool.TryRent(typeof(WinUI.RichTextBlock)) as WinUI.RichTextBlock ?? new WinUI.RichTextBlock();
        rtb.IsTextSelectionEnabled = richText.IsTextSelectionEnabled;
        if (richText.TextWrapping.HasValue) rtb.TextWrapping = richText.TextWrapping.Value;
        if (richText.Paragraphs is not null)
        {
            foreach (var para in richText.Paragraphs)
            {
                var p = new Microsoft.UI.Xaml.Documents.Paragraph();
                foreach (var inline in para.Inlines)
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
                            p.Inlines.Add(r);
                            break;
                        case RichTextHyperlink link:
                            var hl = new Microsoft.UI.Xaml.Documents.Hyperlink();
                            try { hl.NavigateUri = link.NavigateUri; }
                            catch { hl.NavigateUri = new Uri("about:error"); }
                            hl.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = link.Text });
                            p.Inlines.Add(hl);
                            break;
                        case RichTextLineBreak:
                            p.Inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
                            break;
                    }
                }
                rtb.Blocks.Add(p);
            }
        }
        else
        {
            var paragraph = new Microsoft.UI.Xaml.Documents.Paragraph();
            paragraph.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = richText.Text });
            rtb.Blocks.Add(paragraph);
        }
        if (richText.FontSize.HasValue) rtb.FontSize = richText.FontSize.Value;
        ApplySetters(richText.Setters, rtb);
        return rtb;
    }

    private WinUI.Button MountButton(ButtonElement btn, Action requestRerender)
    {
        var rented = _pool.TryRent(typeof(WinUI.Button));
        var button = rented as WinUI.Button ?? new WinUI.Button();
        button.IsEnabled = btn.IsEnabled;
        if (btn.ContentElement is not null)
            button.Content = Mount(btn.ContentElement, requestRerender);
        else
            button.Content = btn.Label;
        SetElementTag(button, btn);
        if (rented is null) // Only wire events on fresh controls — pooled controls retain their handler
            button.Click += (s, _) => (GetElementTag((UIElement)s!) as ButtonElement)?.OnClick?.Invoke();
        ApplySetters(btn.Setters, button);
        return button;
    }

    private WinUI.HyperlinkButton MountHyperlinkButton(HyperlinkButtonElement hlBtn)
    {
        var hb = new WinUI.HyperlinkButton { Content = hlBtn.Content };
        if (hlBtn.NavigateUri is not null) hb.NavigateUri = hlBtn.NavigateUri;
        SetElementTag(hb, hlBtn);
        hb.Click += (s, _) => (GetElementTag((UIElement)s!) as HyperlinkButtonElement)?.OnClick?.Invoke();
        ApplySetters(hlBtn.Setters, hb);
        return hb;
    }

    private WinPrim.RepeatButton MountRepeatButton(RepeatButtonElement repBtn)
    {
        var rb = new WinPrim.RepeatButton { Content = repBtn.Label, Delay = repBtn.Delay, Interval = repBtn.Interval };
        SetElementTag(rb, repBtn);
        rb.Click += (s, _) => (GetElementTag((UIElement)s!) as RepeatButtonElement)?.OnClick?.Invoke();
        ApplySetters(repBtn.Setters, rb);
        return rb;
    }

    private WinPrim.ToggleButton MountToggleButton(ToggleButtonElement togBtn)
    {
        var tb = new WinPrim.ToggleButton { Content = togBtn.Label, IsChecked = togBtn.IsChecked };
        SetElementTag(tb, togBtn);
        // Bind to Click — fires only for real user toggles. Checked/Unchecked
        // would also fire when UpdateToggleButton rewrites IsChecked during a
        // state-driven rerender, which would re-enter the callback and loop.
        tb.Click += (s, _) =>
        {
            var t = (WinPrim.ToggleButton)s!;
            (GetElementTag(t) as ToggleButtonElement)?.OnToggled?.Invoke(t.IsChecked ?? false);
        };
        ApplySetters(togBtn.Setters, tb);
        return tb;
    }

    private WinUI.DropDownButton MountDropDownButton(DropDownButtonElement ddBtn, Action requestRerender)
    {
        var ddb = new WinUI.DropDownButton { Content = ddBtn.Label };
        if (ddBtn.Flyout is not null)
            ddb.Flyout = CreateFlyoutFromElement(ddBtn.Flyout, requestRerender);
        ApplySetters(ddBtn.Setters, ddb);
        return ddb;
    }

    private WinUI.SplitButton MountSplitButton(SplitButtonElement spBtn, Action requestRerender)
    {
        var sb = new WinUI.SplitButton { Content = spBtn.Label };
        SetElementTag(sb, spBtn);
        sb.Click += (s, _) => (GetElementTag((UIElement)s!) as SplitButtonElement)?.OnClick?.Invoke();
        if (spBtn.Flyout is not null)
            sb.Flyout = CreateFlyoutFromElement(spBtn.Flyout, requestRerender);
        ApplySetters(spBtn.Setters, sb);
        return sb;
    }

    private WinUI.ToggleSplitButton MountToggleSplitButton(ToggleSplitButtonElement tspBtn, Action requestRerender)
    {
        var tsb = new WinUI.ToggleSplitButton { Content = tspBtn.Label, IsChecked = tspBtn.IsChecked };
        SetElementTag(tsb, tspBtn);
        tsb.IsCheckedChanged += (s, _) =>
        {
            var t = (WinUI.ToggleSplitButton)s!;
            if (ChangeEchoSuppressor.ShouldSuppress(t)) return;
            (GetElementTag(t) as ToggleSplitButtonElement)?.OnIsCheckedChanged?.Invoke(t.IsChecked);
        };
        if (tspBtn.Flyout is not null)
            tsb.Flyout = CreateFlyoutFromElement(tspBtn.Flyout, requestRerender);
        ApplySetters(tspBtn.Setters, tsb);
        return tsb;
    }

    private TextBox MountTextField(TextFieldElement tf, Action requestRerender)
    {
        var rented = _pool.TryRent(typeof(TextBox));
        var textBox = rented as TextBox ?? new TextBox();
        // SetElementTag BEFORE writing Text: pooled controls retain their
        // previous tag, and programmatic Text= fires TextChanged on the pooled
        // event handler. Setting the new tag first ensures the handler reads
        // this mount's element, not the pool's last owner. The BeginSuppress
        // guard below is additional belt-and-suspenders against echo.
        SetElementTag(textBox, tf);
        if (rented is not null && textBox.Text != tf.Value)
            ChangeEchoSuppressor.BeginSuppress(textBox);
        textBox.Text = tf.Value;
        textBox.PlaceholderText = tf.Placeholder ?? "";
        if (tf.Header is not null) textBox.Header = tf.Header;
        if (tf.IsReadOnly == true) textBox.IsReadOnly = true;
        if (tf.AcceptsReturn == true) textBox.AcceptsReturn = true;
        if (tf.TextWrapping.HasValue) textBox.TextWrapping = tf.TextWrapping.Value;
        if (tf.SelectionStart.HasValue) textBox.SelectionStart = tf.SelectionStart.Value;
        if (tf.SelectionLength.HasValue) textBox.SelectionLength = tf.SelectionLength.Value;
        if (rented is null) // Only wire events on fresh controls — pooled controls retain their handler
        {
            textBox.TextChanged += (_, _) =>
            {
                if (ChangeEchoSuppressor.ShouldSuppress(textBox)) return;
                var tag = GetElementTag(textBox) as TextFieldElement;
                tag?.OnChanged?.Invoke(textBox.Text);
                // Controlled input: when onChange is wired, always request a
                // re-render so UpdateTextField can enforce the controlled value.
                // Coalesces with any setState re-render (CAS gate).
                // Without onChange the field is uncontrolled — no snap-back.
                if (tag?.OnChanged is not null)
                    requestRerender();
            };
            textBox.SelectionChanged += (_, _) => (GetElementTag(textBox) as TextFieldElement)?.OnSelectionChanged?.Invoke(textBox.SelectedText, textBox.SelectionStart, textBox.SelectionLength);
        }
        ApplySetters(tf.Setters, textBox);
        return textBox;
    }

    private WinUI.PasswordBox MountPasswordBox(PasswordBoxElement pw)
    {
        var pb = new WinUI.PasswordBox { Password = pw.Password, PlaceholderText = pw.PlaceholderText ?? "" };
        SetElementTag(pb, pw);
        pb.PasswordChanged += (s, _) =>
        {
            var c = (UIElement)s!;
            if (ChangeEchoSuppressor.ShouldSuppress(c)) return;
            (GetElementTag(c) as PasswordBoxElement)?.OnPasswordChanged?.Invoke(((WinUI.PasswordBox)c).Password);
        };
        ApplySetters(pw.Setters, pb);
        return pb;
    }

    private WinUI.NumberBox MountNumberBox(NumberBoxElement nb)
    {
        var numBox = new WinUI.NumberBox
        {
            Value = nb.Value, Minimum = nb.Minimum, Maximum = nb.Maximum,
            SmallChange = nb.SmallChange, LargeChange = nb.LargeChange,
            PlaceholderText = nb.PlaceholderText ?? "",
            SpinButtonPlacementMode = nb.SpinButtonPlacement,
        };
        if (nb.Header is not null) numBox.Header = nb.Header;
        SetElementTag(numBox, nb);
        numBox.ValueChanged += (s, _) =>
        {
            var box = (WinUI.NumberBox)s!;
            if (ChangeEchoSuppressor.ShouldSuppress(box)) return;
            (GetElementTag(box) as NumberBoxElement)?.OnValueChanged?.Invoke(box.Value);
        };
        ApplySetters(nb.Setters, numBox);
        return numBox;
    }

    private WinUI.AutoSuggestBox MountAutoSuggestBox(AutoSuggestBoxElement asb)
    {
        var box = new WinUI.AutoSuggestBox { Text = asb.Text, PlaceholderText = asb.PlaceholderText ?? "" };
        if (asb.Suggestions.Length > 0) box.ItemsSource = asb.Suggestions;
        SetElementTag(box, asb);
        box.TextChanged += (s, args) =>
        {
            if (args.Reason == WinUI.AutoSuggestionBoxTextChangeReason.UserInput)
                (GetElementTag((UIElement)s!) as AutoSuggestBoxElement)?.OnTextChanged?.Invoke(((WinUI.AutoSuggestBox)s!).Text);
        };
        box.QuerySubmitted += (s, args) =>
            (GetElementTag((UIElement)s!) as AutoSuggestBoxElement)?.OnQuerySubmitted?.Invoke(args.QueryText);
        box.SuggestionChosen += (s, args) =>
            (GetElementTag((UIElement)s!) as AutoSuggestBoxElement)?.OnSuggestionChosen?.Invoke(args.SelectedItem?.ToString() ?? "");
        ApplySetters(asb.Setters, box);
        return box;
    }

    private WinUI.CheckBox MountCheckBox(CheckBoxElement cb)
    {
        var checkBox = new WinUI.CheckBox { Content = cb.Label };
        if (cb.IsThreeState)
        {
            checkBox.IsThreeState = true;
            checkBox.IsChecked = cb.CheckedState;
        }
        else
        {
            checkBox.IsChecked = cb.IsChecked;
        }
        SetElementTag(checkBox, cb);
        checkBox.Checked += (s, _) =>
        {
            var c = (UIElement)s!;
            if (ChangeEchoSuppressor.ShouldSuppress(c)) return;
            var el = GetElementTag(c) as CheckBoxElement;
            el?.OnChanged?.Invoke(true);
            el?.OnCheckedStateChanged?.Invoke(true);
        };
        checkBox.Unchecked += (s, _) =>
        {
            var c = (UIElement)s!;
            if (ChangeEchoSuppressor.ShouldSuppress(c)) return;
            var el = GetElementTag(c) as CheckBoxElement;
            el?.OnChanged?.Invoke(false);
            el?.OnCheckedStateChanged?.Invoke(false);
        };
        checkBox.Indeterminate += (s, _) =>
        {
            var c = (UIElement)s!;
            if (ChangeEchoSuppressor.ShouldSuppress(c)) return;
            var el = GetElementTag(c) as CheckBoxElement;
            el?.OnCheckedStateChanged?.Invoke(null);
        };
        ApplySetters(cb.Setters, checkBox);
        return checkBox;
    }

    private WinUI.RadioButton MountRadioButton(RadioButtonElement rb)
    {
        var radio = new WinUI.RadioButton { Content = rb.Label, IsChecked = rb.IsChecked };
        if (rb.GroupName is not null) radio.GroupName = rb.GroupName;
        SetElementTag(radio, rb);
        radio.Checked += (s, _) =>
        {
            var c = (UIElement)s!;
            if (ChangeEchoSuppressor.ShouldSuppress(c)) return;
            (GetElementTag(c) as RadioButtonElement)?.OnChecked?.Invoke(true);
        };
        radio.Unchecked += (s, _) =>
        {
            var c = (UIElement)s!;
            if (ChangeEchoSuppressor.ShouldSuppress(c)) return;
            (GetElementTag(c) as RadioButtonElement)?.OnChecked?.Invoke(false);
        };
        ApplySetters(rb.Setters, radio);
        return radio;
    }

    private WinUI.RadioButtons MountRadioButtons(RadioButtonsElement rbs)
    {
        var rbGroup = new WinUI.RadioButtons { SelectedIndex = rbs.SelectedIndex };
        if (rbs.Header is not null) rbGroup.Header = rbs.Header;
        foreach (var item in rbs.Items) rbGroup.Items.Add(item);
        SetElementTag(rbGroup, rbs);
        rbGroup.SelectionChanged += (s, _) =>
        {
            var g = (WinUI.RadioButtons)s!;
            if (ChangeEchoSuppressor.ShouldSuppress(g)) return;
            (GetElementTag(g) as RadioButtonsElement)?.OnSelectionChanged?.Invoke(g.SelectedIndex);
        };
        ApplySetters(rbs.Setters, rbGroup);
        return rbGroup;
    }

    private WinUI.ComboBox MountComboBox(ComboBoxElement combo, Action requestRerender)
    {
        var cb = new WinUI.ComboBox
        {
            SelectedIndex = combo.SelectedIndex,
            PlaceholderText = combo.PlaceholderText ?? "",
            IsEditable = combo.IsEditable,
        };
        if (combo.Header is not null) cb.Header = combo.Header;
        if (combo.ItemElements is { } elements)
            foreach (var el in elements) cb.Items.Add(Mount(el, requestRerender));
        else
            foreach (var item in combo.Items) cb.Items.Add(item);
        SetElementTag(cb, combo);
        cb.SelectionChanged += (s, _) =>
        {
            var c = (WinUI.ComboBox)s!;
            if (ChangeEchoSuppressor.ShouldSuppress(c)) return;
            (GetElementTag(c) as ComboBoxElement)?.OnSelectionChanged?.Invoke(c.SelectedIndex);
        };
        ApplySetters(combo.Setters, cb);
        return cb;
    }

    private WinUI.Slider MountSlider(SliderElement sl)
    {
        var slider = new WinUI.Slider { Value = sl.Value, Minimum = sl.Min, Maximum = sl.Max, StepFrequency = sl.StepFrequency };
        if (sl.Header is not null) slider.Header = sl.Header;
        SetElementTag(slider, sl);
        slider.ValueChanged += (_, args) =>
        {
            if (ChangeEchoSuppressor.ShouldSuppress(slider)) return;
            (GetElementTag(slider) as SliderElement)?.OnChanged?.Invoke(args.NewValue);
        };
        ApplySetters(sl.Setters, slider);
        return slider;
    }

    private WinUI.ToggleSwitch MountToggleSwitch(ToggleSwitchElement ts)
    {
        var rented = _pool.TryRent(typeof(WinUI.ToggleSwitch));
        var toggle = rented as WinUI.ToggleSwitch ?? new WinUI.ToggleSwitch();
        // SetElementTag BEFORE IsOn= so a pooled control's retained handler
        // sees this mount's element, not the pool's last owner. Suppress the
        // echo fired by the programmatic IsOn write when it actually changes.
        SetElementTag(toggle, ts);
        if (rented is not null && toggle.IsOn != ts.IsOn)
            ChangeEchoSuppressor.BeginSuppress(toggle);
        toggle.IsOn = ts.IsOn;
        toggle.OnContent = ts.OnContent;
        toggle.OffContent = ts.OffContent;
        if (ts.Header is not null) toggle.Header = ts.Header;
        if (rented is null) // Only wire events on fresh controls — pooled controls retain their handler
            toggle.Toggled += (s, _) =>
            {
                var t = (WinUI.ToggleSwitch)s!;
                if (ChangeEchoSuppressor.ShouldSuppress(t)) return;
                (GetElementTag(t) as ToggleSwitchElement)?.OnChanged?.Invoke(t.IsOn);
            };
        ApplySetters(ts.Setters, toggle);
        return toggle;
    }

    private WinUI.RatingControl MountRatingControl(RatingControlElement rc)
    {
        var rating = new WinUI.RatingControl { Value = rc.Value, MaxRating = rc.MaxRating, IsReadOnly = rc.IsReadOnly, Caption = rc.Caption ?? "" };
        SetElementTag(rating, rc);
        rating.ValueChanged += (s, _) =>
        {
            var r = (WinUI.RatingControl)s!;
            if (ChangeEchoSuppressor.ShouldSuppress(r)) return;
            (GetElementTag(r) as RatingControlElement)?.OnValueChanged?.Invoke(r.Value);
        };
        ApplySetters(rc.Setters, rating);
        return rating;
    }

    private WinUI.ColorPicker MountColorPicker(ColorPickerElement cp)
    {
        var picker = new WinUI.ColorPicker
        {
            Color = cp.Color, IsAlphaEnabled = cp.IsAlphaEnabled, IsMoreButtonVisible = cp.IsMoreButtonVisible,
            IsColorSpectrumVisible = cp.IsColorSpectrumVisible, IsColorSliderVisible = cp.IsColorSliderVisible,
            IsColorChannelTextInputVisible = cp.IsColorChannelTextInputVisible, IsHexInputVisible = cp.IsHexInputVisible,
        };
        SetElementTag(picker, cp);
        picker.ColorChanged += (s, args) =>
        {
            var c = (UIElement)s!;
            if (ChangeEchoSuppressor.ShouldSuppress(c)) return;
            (GetElementTag(c) as ColorPickerElement)?.OnColorChanged?.Invoke(args.NewColor);
        };
        ApplySetters(cp.Setters, picker);
        return picker;
    }

    private WinUI.CalendarDatePicker MountCalendarDatePicker(CalendarDatePickerElement cdp)
    {
        var cal = new WinUI.CalendarDatePicker { Date = cdp.Date, PlaceholderText = cdp.PlaceholderText ?? "" };
        if (cdp.Header is not null) cal.Header = cdp.Header;
        if (cdp.MinDate.HasValue) cal.MinDate = cdp.MinDate.Value;
        if (cdp.MaxDate.HasValue) cal.MaxDate = cdp.MaxDate.Value;
        SetElementTag(cal, cdp);
        cal.DateChanged += (s, _) =>
        {
            var c = (WinUI.CalendarDatePicker)s!;
            if (ChangeEchoSuppressor.ShouldSuppress(c)) return;
            (GetElementTag(c) as CalendarDatePickerElement)?.OnDateChanged?.Invoke(c.Date);
        };
        ApplySetters(cdp.Setters, cal);
        return cal;
    }

    private WinUI.DatePicker MountDatePicker(DatePickerElement dp)
    {
        var picker = new WinUI.DatePicker { Date = dp.Date, DayVisible = dp.DayVisible, MonthVisible = dp.MonthVisible, YearVisible = dp.YearVisible };
        if (dp.Header is not null) picker.Header = dp.Header;
        if (dp.MinYear.HasValue) picker.MinYear = dp.MinYear.Value;
        if (dp.MaxYear.HasValue) picker.MaxYear = dp.MaxYear.Value;
        SetElementTag(picker, dp);
        picker.DateChanged += (s, args) =>
        {
            var c = (UIElement)s!;
            if (ChangeEchoSuppressor.ShouldSuppress(c)) return;
            (GetElementTag(c) as DatePickerElement)?.OnDateChanged?.Invoke(args.NewDate);
        };
        ApplySetters(dp.Setters, picker);
        return picker;
    }

    private WinUI.TimePicker MountTimePicker(TimePickerElement tp)
    {
        var picker = new WinUI.TimePicker { Time = tp.Time, MinuteIncrement = tp.MinuteIncrement };
        if (tp.Header is not null) picker.Header = tp.Header;
        SetElementTag(picker, tp);
        picker.TimeChanged += (s, args) =>
        {
            var c = (UIElement)s!;
            if (ChangeEchoSuppressor.ShouldSuppress(c)) return;
            (GetElementTag(c) as TimePickerElement)?.OnTimeChanged?.Invoke(args.NewTime);
        };
        ApplySetters(tp.Setters, picker);
        return picker;
    }

    private WinUI.ProgressBar MountProgress(ProgressElement prog)
    {
        var bar = _pool.TryRent(typeof(WinUI.ProgressBar)) as WinUI.ProgressBar ?? new WinUI.ProgressBar();
        bar.IsIndeterminate = prog.IsIndeterminate;
        bar.Minimum = prog.Minimum;
        bar.Maximum = prog.Maximum;
        bar.ShowError = prog.ShowError;
        bar.ShowPaused = prog.ShowPaused;
        if (prog.Value.HasValue) bar.Value = prog.Value.Value;
        ApplySetters(prog.Setters, bar);
        return bar;
    }

    private WinUI.ProgressRing MountProgressRing(ProgressRingElement ring)
    {
        var pr = _pool.TryRent(typeof(WinUI.ProgressRing)) as WinUI.ProgressRing ?? new WinUI.ProgressRing();
        pr.IsIndeterminate = ring.IsIndeterminate;
        pr.IsActive = ring.IsActive;
        pr.Minimum = ring.Minimum;
        pr.Maximum = ring.Maximum;
        if (ring.Value.HasValue) pr.Value = ring.Value.Value;
        ApplySetters(ring.Setters, pr);
        return pr;
    }

    private WinUI.Image MountImage(ImageElement img)
    {
        var image = _pool.TryRent(typeof(WinUI.Image)) as WinUI.Image ?? new WinUI.Image();
        try
        {
            var uri = new Uri(img.Source, UriKind.RelativeOrAbsolute);
            image.Source = img.Source.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                ? new SvgImageSource(uri)
                : new BitmapImage(uri);
        }
        catch (UriFormatException)
        {
            // Malformed URI — leave image source empty rather than crashing
        }
        if (img.Width.HasValue) image.Width = img.Width.Value;
        if (img.Height.HasValue) image.Height = img.Height.Value;
        ApplySetters(img.Setters, image);
        return image;
    }

    private WinUI.PersonPicture MountPersonPicture(PersonPictureElement pp)
    {
        var pic = new WinUI.PersonPicture { IsGroup = pp.IsGroup, BadgeNumber = pp.BadgeNumber };
        if (pp.DisplayName is not null) pic.DisplayName = pp.DisplayName;
        if (pp.Initials is not null) pic.Initials = pp.Initials;
        if (pp.ProfilePicture is not null)
            pic.ProfilePicture = new BitmapImage(new Uri(pp.ProfilePicture, UriKind.RelativeOrAbsolute));
        ApplySetters(pp.Setters, pic);
        return pic;
    }

    private WinUI.WebView2 MountWebView2(WebView2Element wv)
    {
        var webView = new WinUI.WebView2();
        if (wv.Source is not null) webView.Source = wv.Source;
        SetElementTag(webView, wv);
        webView.NavigationStarting += (s, args) =>
            (GetElementTag((UIElement)s!) as WebView2Element)?.OnNavigationStarting?.Invoke(new Uri(args.Uri));
        webView.NavigationCompleted += (s, _) =>
            (GetElementTag((UIElement)s!) as WebView2Element)?.OnNavigationCompleted?.Invoke(((WinUI.WebView2)s!).Source);
        ApplySetters(wv.Setters, webView);
        return webView;
    }

    private WinUI.RichEditBox MountRichEditBox(RichEditBoxElement reb)
    {
        var box = new WinUI.RichEditBox { IsReadOnly = reb.IsReadOnly };
        if (reb.Header is not null) box.Header = reb.Header;
        if (reb.PlaceholderText is not null) box.PlaceholderText = reb.PlaceholderText;
        if (!string.IsNullOrEmpty(reb.Text))
            box.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, reb.Text);
        SetElementTag(box, reb);
        box.TextChanged += (s, _) =>
        {
            var r = (WinUI.RichEditBox)s!;
            r.Document.GetText(Microsoft.UI.Text.TextGetOptions.None, out var text);
            (GetElementTag(r) as RichEditBoxElement)?.OnTextChanged?.Invoke(text?.TrimEnd('\r') ?? "");
        };
        ApplySetters(reb.Setters, box);
        return box;
    }

    private WinUI.VariableSizedWrapGrid MountWrapGrid(WrapGridElement wg, Action requestRerender)
    {
        var grid = new WinUI.VariableSizedWrapGrid { Orientation = wg.Orientation };
        if (wg.MaximumRowsOrColumns >= 0) grid.MaximumRowsOrColumns = wg.MaximumRowsOrColumns;
        if (!double.IsNaN(wg.ItemWidth)) grid.ItemWidth = wg.ItemWidth;
        if (!double.IsNaN(wg.ItemHeight)) grid.ItemHeight = wg.ItemHeight;
        foreach (var child in wg.Children)
        {
            if (child is null or EmptyElement) continue;
            var childControl = Mount(child, requestRerender);
            if (childControl is not null) grid.Children.Add(childControl);
        }
        SetElementTag(grid, wg);
        ApplySetters(wg.Setters, grid);
        return grid;
    }

    private WinUI.StackPanel MountStack(StackElement stack, Action requestRerender)
    {
        var panel = _pool.TryRent(typeof(WinUI.StackPanel)) as WinUI.StackPanel ?? new WinUI.StackPanel();
        panel.Orientation = stack.Orientation;
        panel.Spacing = stack.Spacing;
        if (stack.HorizontalAlignment.HasValue) panel.HorizontalAlignment = stack.HorizontalAlignment.Value;
        if (stack.VerticalAlignment.HasValue) panel.VerticalAlignment = stack.VerticalAlignment.Value;
        // Apply RequestedTheme before mounting children so that child ThemeRef
        // bindings resolve against the correct theme variant from the start.
        foreach (var child in stack.Children)
        {
            if (child is null or EmptyElement) continue;
            var childControl = Mount(child, requestRerender);
            if (childControl is not null) panel.Children.Add(childControl);
        }
        SetElementTag(panel, stack);
        ApplySetters(stack.Setters, panel);
        return panel;
    }

    private WinUI.Grid MountGrid(GridElement grid, Action requestRerender)
    {
        var g = _pool.TryRent(typeof(WinUI.Grid)) as WinUI.Grid ?? new WinUI.Grid();
        g.RowSpacing = grid.RowSpacing;
        g.ColumnSpacing = grid.ColumnSpacing;
        g.ColumnDefinitions.Clear();
        g.RowDefinitions.Clear();
        foreach (var col in grid.Definition.Columns) g.ColumnDefinitions.Add(ParseColumnDef(col));
        foreach (var row in grid.Definition.Rows) g.RowDefinitions.Add(ParseRowDef(row));
        foreach (var child in grid.Children)
        {
            var ctrl = Mount(child, requestRerender);
            if (ctrl is null) continue;
            var ga = child.GetAttached<GridAttached>();
            if (ga is not null && ctrl is FrameworkElement fe)
            {
                WinUI.Grid.SetRow(fe, ga.Row);
                WinUI.Grid.SetColumn(fe, ga.Column);
                if (ga.RowSpan > 1) WinUI.Grid.SetRowSpan(fe, ga.RowSpan);
                if (ga.ColumnSpan > 1) WinUI.Grid.SetColumnSpan(fe, ga.ColumnSpan);
            }
            g.Children.Add(ctrl);
        }
        SetElementTag(g, grid);
        ApplySetters(grid.Setters, g);
        return g;
    }

    private WinUI.ScrollViewer MountScrollView(ScrollViewElement scroll, Action requestRerender)
    {
        var sv = _pool.TryRent(typeof(WinUI.ScrollViewer)) as WinUI.ScrollViewer ?? new WinUI.ScrollViewer();
        sv.HorizontalScrollBarVisibility = scroll.HorizontalScrollBarVisibility;
        sv.VerticalScrollBarVisibility = scroll.VerticalScrollBarVisibility;
        sv.HorizontalScrollMode = (WinUI.ScrollMode)scroll.HorizontalScrollMode;
        sv.VerticalScrollMode = (WinUI.ScrollMode)scroll.VerticalScrollMode;
        sv.ZoomMode = (WinUI.ZoomMode)scroll.ZoomMode;
        sv.Content = Mount(scroll.Child, requestRerender);
        SetElementTag(sv, scroll);
        ApplySetters(scroll.Setters, sv);
        return sv;
    }

    private WinUI.Border MountBorder(BorderElement border, Action requestRerender)
    {
        var bdr = _pool.TryRent(typeof(WinUI.Border)) as WinUI.Border ?? new WinUI.Border();
        if (border.CornerRadius.HasValue) bdr.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(border.CornerRadius.Value);
        if (border.Padding.HasValue) bdr.Padding = border.Padding.Value;
        if (border.Background is not null) bdr.Background = border.Background;
        if (border.BorderBrush is not null) bdr.BorderBrush = border.BorderBrush;
        if (border.BorderThickness.HasValue) bdr.BorderThickness = new Microsoft.UI.Xaml.Thickness(border.BorderThickness.Value);
        // Apply RequestedTheme before mounting children so that child ThemeRef
        // bindings resolve against the correct theme variant from the start.
        bdr.Child = Mount(border.Child, requestRerender);
        SetElementTag(bdr, border);
        ApplySetters(border.Setters, bdr);
        return bdr;
    }

    private WinUI.Expander MountExpander(ExpanderElement exp, Action requestRerender)
    {
        var expander = new WinUI.Expander
        {
            Header = exp.Header, IsExpanded = exp.IsExpanded,
            ExpandDirection = exp.ExpandDirection,
        };
        expander.Content = Mount(exp.Content, requestRerender);
        SetElementTag(expander, exp);
        expander.Expanding += (s, _) => (GetElementTag((UIElement)s!) as ExpanderElement)?.OnExpandedChanged?.Invoke(true);
        expander.Collapsed += (s, _) => (GetElementTag((UIElement)s!) as ExpanderElement)?.OnExpandedChanged?.Invoke(false);
        ApplySetters(exp.Setters, expander);
        return expander;
    }

    private WinUI.SplitView MountSplitView(SplitViewElement svEl, Action requestRerender)
    {
        var splitView = new WinUI.SplitView
        {
            IsPaneOpen = svEl.IsPaneOpen, OpenPaneLength = svEl.OpenPaneLength,
            CompactPaneLength = svEl.CompactPaneLength, DisplayMode = svEl.DisplayMode,
        };
        if (svEl.Pane is not null) splitView.Pane = Mount(svEl.Pane, requestRerender);
        if (svEl.Content is not null) splitView.Content = Mount(svEl.Content, requestRerender);
        SetElementTag(splitView, svEl);
        splitView.PaneOpening += (s, _) => (GetElementTag((UIElement)s!) as SplitViewElement)?.OnPaneOpenChanged?.Invoke(true);
        splitView.PaneClosing += (s, _) => (GetElementTag((UIElement)s!) as SplitViewElement)?.OnPaneOpenChanged?.Invoke(false);
        ApplySetters(svEl.Setters, splitView);
        return splitView;
    }

    private WinUI.Viewbox MountViewbox(ViewboxElement vb, Action requestRerender)
    {
        var viewbox = _pool.TryRent(typeof(WinUI.Viewbox)) as WinUI.Viewbox ?? new WinUI.Viewbox();
        viewbox.Child = Mount(vb.Child, requestRerender) as UIElement;
        ApplySetters(vb.Setters, viewbox);
        return viewbox;
    }

    private WinUI.Canvas MountCanvas(CanvasElement cvs, Action requestRerender)
    {
        var canvas = _pool.TryRent(typeof(WinUI.Canvas)) as WinUI.Canvas ?? new WinUI.Canvas();
        if (cvs.Width.HasValue) canvas.Width = cvs.Width.Value;
        if (cvs.Height.HasValue) canvas.Height = cvs.Height.Value;
        if (cvs.Background is not null) canvas.Background = cvs.Background;
        foreach (var child in cvs.Children)
        {
            var ctrl = Mount(child, requestRerender);
            if (ctrl is null) continue;
            var ca = child.GetAttached<CanvasAttached>();
            if (ca is not null && ctrl is FrameworkElement fe)
            {
                WinUI.Canvas.SetLeft(fe, ca.Left);
                WinUI.Canvas.SetTop(fe, ca.Top);
            }
            canvas.Children.Add(ctrl);
        }
        SetElementTag(canvas, cvs);
        ApplySetters(cvs.Setters, canvas);
        return canvas;
    }

    private WinUI.Grid MountNavigationHost(NavigationHostElement element, Action requestRerender)
    {
        var grid = new WinUI.Grid();
        var handle = (Navigation.INavigationHandle)element.NavigationHandle;
        var routeMap = element.RouteMap;
        var currentRoute = handle.CurrentRoute;

        // Resolve and mount the initial route's element
        var childElement = routeMap(currentRoute);
        var childControl = Mount(childElement, requestRerender);
        if (childControl is not null)
            grid.Children.Add(childControl);

        // Track state for update/unmount
        var node = new NavigationHostNode
        {
            Handle = handle,
            LastRenderedRoute = currentRoute,
            CurrentChildElement = childElement,
            CurrentChildControl = childControl,
            RouteMap = routeMap,
            RequestRerender = requestRerender,
            HostTransition = element.Transition,
            CacheMode = element.CacheMode,
        };

        // Create page cache when caching is enabled
        if (element.CacheMode != Navigation.NavigationCacheMode.Disabled)
        {
            node.Cache = new Navigation.NavigationCache(
                element.CacheSize, evicted => Unmount(evicted));
        }

        // Subscribe to route changes so NavigationHost updates even if an intermediate
        // component's ShouldUpdate blocks the re-render propagation.
        void onRouteChanged() => requestRerender();
        handle.RouteChanged += onRouteChanged;
        node.RouteChangedHandler = onRouteChanged;

        // Wire lifecycle guard: invokes onNavigatingFrom callbacks from the current
        // page's component tree before the stack mutation. Records the navigation mode
        // and previous route for post-swap onNavigatedTo/onNavigatedFrom invocation.
        handle.LifecycleGuard = ctx =>
        {
            InvokeNavigatingFrom(node.CurrentChildControl, ctx);
            if (!ctx.IsCancelled)
            {
                node.PendingNavigationMode = ctx.Mode;
                node.PendingPreviousRoute = ctx.Route;
            }
        };

        _navigationHostNodes[grid] = node;
        return grid;
    }

    private WinUI.NavigationView MountNavigationView(NavigationViewElement nav, Action requestRerender)
    {
        var nv = new WinUI.NavigationView
        {
            IsPaneOpen = nav.IsPaneOpen, PaneDisplayMode = nav.PaneDisplayMode,
            IsBackEnabled = nav.IsBackEnabled, IsSettingsVisible = nav.IsSettingsVisible,
        };
        if (nav.PaneTitle is not null) nv.PaneTitle = nav.PaneTitle;
        if (nav.Header is not null) nv.Header = Mount(nav.Header, requestRerender);
        foreach (var item in nav.MenuItems)
        {
            if (item.IsHeader)
                nv.MenuItems.Add(new WinUI.NavigationViewItemHeader { Content = item.Content });
            else
                nv.MenuItems.Add(CreateNavItem(item));
        }
        if (nav.Content is not null) nv.Content = Mount(nav.Content, requestRerender);
        if (nav.SelectedTag is not null)
        {
            foreach (var mi in nv.MenuItems.OfType<WinUI.NavigationViewItem>())
                if (mi.Tag as string == nav.SelectedTag) { nv.SelectedItem = mi; break; }
        }
        SetElementTag(nv, nav);
        nv.SelectionChanged += (s, args) =>
        {
            var selected = args.SelectedItem as WinUI.NavigationViewItem;
            (GetElementTag((UIElement)s!) as NavigationViewElement)?.OnSelectionChanged?.Invoke(selected?.Tag as string);
        };
        nv.BackRequested += (s, _) => (GetElementTag((UIElement)s!) as NavigationViewElement)?.OnBackRequested?.Invoke();
        ApplySetters(nav.Setters, nv);
        return nv;
    }

    private static WinUI.NavigationViewItem CreateNavItem(NavigationViewItemData data)
    {
        var item = new WinUI.NavigationViewItem { Content = data.Content, Tag = data.Tag ?? data.Content };
        var icon = ResolveIcon(data.IconElement, data.Icon);
        if (icon is not null) item.Icon = icon;
        if (data.Children is not null)
            foreach (var child in data.Children) item.MenuItems.Add(CreateNavItem(child));
        return item;
    }

    private WinUI.TitleBar MountTitleBar(TitleBarElement tb, Action requestRerender)
    {
        var titleBar = new WinUI.TitleBar
        {
            Title = tb.Title,
            IsBackButtonVisible = tb.IsBackButtonVisible,
            IsBackButtonEnabled = tb.IsBackButtonEnabled,
            IsPaneToggleButtonVisible = tb.IsPaneToggleButtonVisible,
        };
        if (tb.Subtitle is not null) titleBar.Subtitle = tb.Subtitle;
        if (tb.Content is not null) titleBar.Content = Mount(tb.Content, requestRerender);
        if (tb.RightHeader is not null) titleBar.RightHeader = Mount(tb.RightHeader, requestRerender);
        SetElementTag(titleBar, tb);
        titleBar.BackRequested += (s, _) => (GetElementTag((UIElement)s!) as TitleBarElement)?.OnBackRequested?.Invoke();
        titleBar.PaneToggleRequested += (s, _) => (GetElementTag((UIElement)s!) as TitleBarElement)?.OnPaneToggleRequested?.Invoke();
        ApplySetters(tb.Setters, titleBar);

        // Register with the window for drag regions and caption buttons
        if (OpenClawTray.Infrastructure.ReactorApp.ActiveHost is { } host)
        {
            host.Window.ExtendsContentIntoTitleBar = true;
            host.Window.SetTitleBar(titleBar);
        }

        return titleBar;
    }

    private WinUI.TabView MountTabView(TabViewElement tab, Action requestRerender)
    {
        var tv = new WinUI.TabView { SelectedIndex = tab.SelectedIndex, IsAddTabButtonVisible = tab.IsAddTabButtonVisible };
        foreach (var tabItem in tab.Tabs)
        {
            var tvi = new WinUI.TabViewItem
            {
                Header = tabItem.Header, IsClosable = tabItem.IsClosable,
                Content = Mount(tabItem.Content, requestRerender),
            };
            if (tabItem.Icon is not null) tvi.IconSource = ResolveIconSource(tabItem.Icon);
            tv.TabItems.Add(tvi);
        }
        SetElementTag(tv, tab);
        tv.SelectionChanged += (s, _) =>
        {
            var t = (WinUI.TabView)s!;
            (GetElementTag(t) as TabViewElement)?.OnSelectionChanged?.Invoke(t.SelectedIndex);
        };
        tv.TabCloseRequested += (s, args) =>
        {
            var t = (WinUI.TabView)s!;
            var idx = t.TabItems.IndexOf(args.Tab);
            (GetElementTag(t) as TabViewElement)?.OnTabCloseRequested?.Invoke(idx);
        };
        tv.AddTabButtonClick += (s, _) => (GetElementTag((UIElement)s!) as TabViewElement)?.OnAddTabButtonClick?.Invoke();
        ApplySetters(tab.Setters, tv);
        return tv;
    }

    private WinUI.BreadcrumbBar MountBreadcrumbBar(BreadcrumbBarElement bcb)
    {
        var bar = new WinUI.BreadcrumbBar();
        bar.ItemsSource = bcb.Items.Select(i => i.Label).ToList();
        SetElementTag(bar, bcb);
        bar.ItemClicked += (s, args) =>
        {
            var el = GetElementTag((UIElement)s!) as BreadcrumbBarElement;
            if (el is not null && args.Index >= 0 && args.Index < el.Items.Length) el.OnItemClicked?.Invoke(el.Items[args.Index]);
        };
        ApplySetters(bcb.Setters, bar);
        return bar;
    }

    private WinUI.Pivot MountPivot(PivotElement pvt, Action requestRerender)
    {
        var pivot = new WinUI.Pivot { SelectedIndex = pvt.SelectedIndex };
        if (pvt.Title is not null) pivot.Title = pvt.Title;
        foreach (var item in pvt.Items)
        {
            var pi = new WinUI.PivotItem { Header = item.Header, Content = Mount(item.Content, requestRerender) };
            pivot.Items.Add(pi);
        }
        SetElementTag(pivot, pvt);
        pivot.SelectionChanged += (s, _) =>
        {
            var p = (WinUI.Pivot)s!;
            (GetElementTag(p) as PivotElement)?.OnSelectionChanged?.Invoke(p.SelectedIndex);
        };
        ApplySetters(pvt.Setters, pivot);
        return pivot;
    }

    private WinUI.ListView MountListView(ListViewElement lv, Action requestRerender)
    {
        var listView = new WinUI.ListView
        {
            SelectionMode = lv.SelectionMode,
            IsItemClickEnabled = lv.OnItemClick is not null,
        };
        if (lv.Header is not null) listView.Header = lv.Header;

        SetElementTag(listView, lv);

        // DataTemplate with a ContentControl shell — we populate its Content on demand
        listView.ItemTemplate = SharedContentControlTemplate.Value;

        listView.ContainerContentChanging += (sender, args) =>
        {
            if (args.InRecycleQueue)
            {
                if (args.ItemContainer.ContentTemplateRoot is ContentControl oldCc)
                {
                    if (oldCc.Content is UIElement oldCtrl)
                        UnmountChild(oldCtrl);
                    oldCc.Content = null;
                }
                return;
            }

            args.Handled = true;
            var items = (GetElementTag((UIElement)sender!) as ListViewElement)?.Items;
            if (items is not null && args.ItemIndex >= 0 && args.ItemIndex < items.Length
                && args.ItemContainer.ContentTemplateRoot is ContentControl cc)
            {
                var ctrl = Mount(items[args.ItemIndex], requestRerender);
                cc.Content = ctrl;
            }
        };

        listView.SelectionChanged += (s, _) =>
        {
            var l = (WinUI.ListView)s!;
            (GetElementTag(l) as ListViewElement)?.OnSelectionChanged?.Invoke(l.SelectedIndex);
        };
        listView.ItemClick += (s, args) =>
        {
            var l = (WinUI.ListView)s!;
            if (args.ClickedItem is int idx)
                (GetElementTag(l) as ListViewElement)?.OnItemClick?.Invoke(idx);
        };

        // Set ItemsSource LAST — triggers container creation which needs the handler above
        listView.ItemsSource = Enumerable.Range(0, lv.Items.Length).ToList();

        if (lv.SelectedIndex >= 0) listView.SelectedIndex = lv.SelectedIndex;
        ApplySetters(lv.Setters, listView);
        return listView;
    }

    private WinUI.GridView MountGridView(GridViewElement gv, Action requestRerender)
    {
        var gridView = new WinUI.GridView
        {
            SelectionMode = gv.SelectionMode,
            IsItemClickEnabled = gv.OnItemClick is not null,
        };
        if (gv.Header is not null) gridView.Header = gv.Header;

        SetElementTag(gridView, gv);

        gridView.ItemTemplate = SharedContentControlTemplate.Value;

        gridView.ContainerContentChanging += (sender, args) =>
        {
            if (args.InRecycleQueue)
            {
                if (args.ItemContainer.ContentTemplateRoot is ContentControl oldCc)
                {
                    if (oldCc.Content is UIElement oldCtrl)
                        UnmountChild(oldCtrl);
                    oldCc.Content = null;
                }
                return;
            }

            args.Handled = true;
            var items = (GetElementTag((UIElement)sender!) as GridViewElement)?.Items;
            if (items is not null && args.ItemIndex >= 0 && args.ItemIndex < items.Length
                && args.ItemContainer.ContentTemplateRoot is ContentControl cc)
            {
                var ctrl = Mount(items[args.ItemIndex], requestRerender);
                cc.Content = ctrl;
            }
        };

        gridView.SelectionChanged += (s, _) =>
        {
            var g = (WinUI.GridView)s!;
            (GetElementTag(g) as GridViewElement)?.OnSelectionChanged?.Invoke(g.SelectedIndex);
        };
        gridView.ItemClick += (s, args) =>
        {
            var g = (WinUI.GridView)s!;
            if (args.ClickedItem is int idx)
                (GetElementTag(g) as GridViewElement)?.OnItemClick?.Invoke(idx);
        };

        gridView.ItemsSource = Enumerable.Range(0, gv.Items.Length).ToList();

        if (gv.SelectedIndex >= 0) gridView.SelectedIndex = gv.SelectedIndex;
        ApplySetters(gv.Setters, gridView);
        return gridView;
    }

    private WinUI.TreeView MountTreeView(TreeViewElement tv, Action requestRerender)
    {
        var treeView = new WinUI.TreeView
        {
            SelectionMode = tv.SelectionMode,
            CanDragItems = tv.CanDragItems,
            AllowDrop = tv.AllowDrop,
            CanReorderItems = tv.CanReorderItems,
        };

        // Check if any node uses ContentElement for custom rendering
        bool hasContentElements = HasAnyContentElement(tv.Nodes);

        if (hasContentElements)
        {
            // Use ContentControl template so pre-mounted Elements display in the tree
            treeView.ItemTemplate = SharedContentControlTemplate.Value;
        }
        else
        {
            // Default: text-binding template for efficiency
            // In node-mode, DataContext of the template = TreeViewNode,
            // so {Binding Content.Content} resolves TreeViewNode.Content (TreeViewNodeData) → .Content (string).
            treeView.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                "<TextBlock Text='{Binding Content.Content}'/>" +
                "</DataTemplate>");
        }

        foreach (var node in tv.Nodes)
            treeView.RootNodes.Add(CreateTreeNode(node, hasContentElements, requestRerender));

        SetElementTag(treeView, tv);

        treeView.ItemInvoked += (s, args) =>
        {
            var t = (WinUI.TreeView)s!;
            if (args.InvokedItem is WinUI.TreeViewNode tvn
                && tvn.Content is TreeViewNodeData nodeData)
            {
                (GetElementTag(t) as TreeViewElement)?.OnItemInvoked?.Invoke(nodeData);
            }
        };

        treeView.Expanding += (s, args) =>
        {
            var t = (WinUI.TreeView)s!;
            if (args.Node.Content is TreeViewNodeData nodeData)
                (GetElementTag(t) as TreeViewElement)?.OnExpanding?.Invoke(nodeData);
        };

        ApplySetters(tv.Setters, treeView);
        return treeView;
    }

    private static bool HasAnyContentElement(TreeViewNodeData[] nodes)
    {
        foreach (var node in nodes)
        {
            if (node.ContentElement is not null) return true;
            if (node.Children is not null && HasAnyContentElement(node.Children)) return true;
        }
        return false;
    }

    private WinUI.TreeViewNode CreateTreeNode(TreeViewNodeData data, bool mountElements, Action requestRerender)
    {
        var node = new WinUI.TreeViewNode { IsExpanded = data.IsExpanded };

        if (mountElements && data.ContentElement is not null)
        {
            // Mount the Element and store the UIElement as Content.
            // The ContentControl template will display it via ContentPresenter.
            var ctrl = Mount(data.ContentElement, requestRerender);
            node.Content = ctrl;
        }
        else
        {
            node.Content = data;
        }

        if (data.Children is not null)
            foreach (var child in data.Children)
                node.Children.Add(CreateTreeNode(child, mountElements, requestRerender));
        return node;
    }

    /// <summary>Backward-compatible overload for non-ContentElement code paths.</summary>
    private static WinUI.TreeViewNode CreateTreeNode(TreeViewNodeData data)
    {
        var node = new WinUI.TreeViewNode { Content = data, IsExpanded = data.IsExpanded };
        if (data.Children is not null)
            foreach (var child in data.Children) node.Children.Add(CreateTreeNode(child));
        return node;
    }

    private WinUI.FlipView MountFlipView(FlipViewElement fv, Action requestRerender)
    {
        var flipView = new WinUI.FlipView { SelectedIndex = fv.SelectedIndex };
        foreach (var item in fv.Items)
        {
            var ctrl = Mount(item, requestRerender);
            if (ctrl is not null) flipView.Items.Add(ctrl);
        }
        SetElementTag(flipView, fv);
        flipView.SelectionChanged += (s, _) =>
        {
            var f = (WinUI.FlipView)s!;
            (GetElementTag(f) as FlipViewElement)?.OnSelectionChanged?.Invoke(f.SelectedIndex);
        };
        ApplySetters(fv.Setters, flipView);
        return flipView;
    }

    private UIElement MountTemplatedList(TemplatedListElementBase el, Action requestRerender)
    {
        return el.ControlKind switch
        {
            TemplatedControlKind.ListView => MountTemplatedListView(el, requestRerender),
            TemplatedControlKind.GridView => MountTemplatedGridView(el, requestRerender),
            TemplatedControlKind.FlipView => MountTemplatedFlipView(el, requestRerender),
            _ => throw new InvalidOperationException($"Unknown TemplatedControlKind: {el.ControlKind}")
        };
    }

    /// <summary>
    /// Shared ContainerContentChanging handler for all templated items controls.
    /// On materialize: calls viewBuilder, mounts element, stores in ContentControl.
    /// On recycle: unmounts child, clears content.
    /// </summary>
    private void HandleTemplatedContainerContentChanging(object sender, ContainerContentChangingEventArgs args, Action requestRerender)
    {
        if (args.InRecycleQueue)
        {
            if (args.ItemContainer.ContentTemplateRoot is ContentControl oldCc)
            {
                if (oldCc.Content is UIElement oldCtrl)
                    UnmountChild(oldCtrl);
                oldCc.Content = null;
                oldCc.Tag = null;
            }
            return;
        }

        args.Handled = true;
        var currentEl = GetElementTag((UIElement)sender!) as TemplatedListElementBase;
        if (currentEl is not null && args.ItemIndex >= 0 && args.ItemIndex < currentEl.ItemCount
            && args.ItemContainer.ContentTemplateRoot is ContentControl cc)
        {
            var itemElement = currentEl.BuildItemView(args.ItemIndex);
            var ctrl = Mount(itemElement, requestRerender);
            cc.Content = ctrl;
            cc.Tag = itemElement; // Store for later reconciliation
        }
    }

    private WinUI.ListView MountTemplatedListView(TemplatedListElementBase el, Action requestRerender)
    {
        var listView = new WinUI.ListView
        {
            SelectionMode = el.GetSelectionMode(),
            IsItemClickEnabled = el.GetIsItemClickEnabled(),
        };
        var header = el.GetHeader();
        if (header is not null) listView.Header = header;

        SetElementTag(listView, el);
        listView.ItemTemplate = SharedContentControlTemplate.Value;

        listView.ContainerContentChanging += (sender, args) =>
            HandleTemplatedContainerContentChanging(sender, args, requestRerender);

        listView.SelectionChanged += (s, _) =>
        {
            var l = (WinUI.ListView)s!;
            (GetElementTag(l) as TemplatedListElementBase)?.InvokeSelectionChanged(l.SelectedIndex);
        };
        listView.ItemClick += (s, args) =>
        {
            var l = (WinUI.ListView)s!;
            if (args.ClickedItem is int idx)
                (GetElementTag(l) as TemplatedListElementBase)?.InvokeItemClick(idx);
        };

        listView.ItemsSource = Enumerable.Range(0, el.ItemCount).ToList();

        var selectedIndex = el.GetSelectedIndex();
        if (selectedIndex >= 0) listView.SelectedIndex = selectedIndex;
        el.ApplyControlSetters(listView);
        return listView;
    }

    private WinUI.GridView MountTemplatedGridView(TemplatedListElementBase el, Action requestRerender)
    {
        var gridView = new WinUI.GridView
        {
            SelectionMode = el.GetSelectionMode(),
            IsItemClickEnabled = el.GetIsItemClickEnabled(),
        };
        var header = el.GetHeader();
        if (header is not null) gridView.Header = header;

        SetElementTag(gridView, el);
        gridView.ItemTemplate = SharedContentControlTemplate.Value;

        gridView.ContainerContentChanging += (sender, args) =>
            HandleTemplatedContainerContentChanging(sender, args, requestRerender);

        gridView.SelectionChanged += (s, _) =>
        {
            var g = (WinUI.GridView)s!;
            (GetElementTag(g) as TemplatedListElementBase)?.InvokeSelectionChanged(g.SelectedIndex);
        };
        gridView.ItemClick += (s, args) =>
        {
            var g = (WinUI.GridView)s!;
            if (args.ClickedItem is int idx)
                (GetElementTag(g) as TemplatedListElementBase)?.InvokeItemClick(idx);
        };

        gridView.ItemsSource = Enumerable.Range(0, el.ItemCount).ToList();

        var selectedIndex = el.GetSelectedIndex();
        if (selectedIndex >= 0) gridView.SelectedIndex = selectedIndex;
        el.ApplyControlSetters(gridView);
        return gridView;
    }

    private WinUI.FlipView MountTemplatedFlipView(TemplatedListElementBase el, Action requestRerender)
    {
        var flipView = new WinUI.FlipView();

        SetElementTag(flipView, el);

        // FlipView doesn't support ContainerContentChanging (not a ListViewBase).
        // Pre-mount all items — FlipView typically has few items so this is fine.
        for (int i = 0; i < el.ItemCount; i++)
        {
            var itemElement = el.BuildItemView(i);
            var ctrl = Mount(itemElement, requestRerender);
            if (ctrl is not null) flipView.Items.Add(ctrl);
        }

        flipView.SelectionChanged += (s, _) =>
        {
            var f = (WinUI.FlipView)s!;
            (GetElementTag(f) as TemplatedListElementBase)?.InvokeSelectionChanged(f.SelectedIndex);
        };

        var selectedIndex = el.GetSelectedIndex();
        if (selectedIndex >= 0) flipView.SelectedIndex = selectedIndex;
        el.ApplyControlSetters(flipView);
        return flipView;
    }

    private WinUI.InfoBar MountInfoBar(InfoBarElement ib)
    {
        var infoBar = new WinUI.InfoBar
        {
            Title = ib.Title ?? "", Message = ib.Message ?? "",
            Severity = ib.Severity, IsOpen = ib.IsOpen, IsClosable = ib.IsClosable,
        };
        if (ib.ActionButtonContent is not null)
        {
            infoBar.ActionButton = new WinUI.Button { Content = ib.ActionButtonContent };
            SetElementTag(infoBar.ActionButton, ib);
            ((WinUI.Button)infoBar.ActionButton).Click += (s, _) =>
                (GetElementTag((UIElement)s!) as InfoBarElement)?.OnActionButtonClick?.Invoke();
        }
        SetElementTag(infoBar, ib);
        infoBar.Closed += (s, _) => (GetElementTag((UIElement)s!) as InfoBarElement)?.OnClosed?.Invoke();
        ApplySetters(ib.Setters, infoBar);
        return infoBar;
    }

    private WinUI.InfoBadge MountInfoBadge(InfoBadgeElement badge)
    {
        var ib = _pool.TryRent(typeof(WinUI.InfoBadge)) as WinUI.InfoBadge ?? new WinUI.InfoBadge();
        if (badge.Value.HasValue) ib.Value = badge.Value.Value;
        ApplySetters(badge.Setters, ib);
        return ib;
    }

    private UIElement MountContentDialog(ContentDialogElement cdEl, Action requestRerender)
    {
        var placeholder = new WinUI.StackPanel { Visibility = Visibility.Collapsed };
        SetElementTag(placeholder, cdEl);
        if (cdEl.IsOpen) ShowContentDialog(cdEl, requestRerender);
        return placeholder;
    }

    private async void ShowContentDialog(ContentDialogElement cdEl, Action requestRerender)
    {
        var dialog = new WinUI.ContentDialog
        {
            Title = cdEl.Title, PrimaryButtonText = cdEl.PrimaryButtonText,
            DefaultButton = cdEl.DefaultButton,
            XamlRoot = null,
        };
        if (cdEl.SecondaryButtonText is not null) dialog.SecondaryButtonText = cdEl.SecondaryButtonText;
        if (cdEl.CloseButtonText is not null) dialog.CloseButtonText = cdEl.CloseButtonText;
        dialog.Content = Mount(cdEl.Content, requestRerender);
        ApplySetters(cdEl.Setters, dialog);
        try
        {
            if (dialog.Content is UIElement contentUi && contentUi.XamlRoot is not null)
                dialog.XamlRoot = contentUi.XamlRoot;
            var winUiResult = await dialog.ShowAsync();
            cdEl.OnClosed?.Invoke(winUiResult);
        }
        catch { }
    }

    private UIElement? MountFlyout(FlyoutElement flyEl, Action requestRerender)
    {
        var target = Mount(flyEl.Target, requestRerender);
        if (target is FrameworkElement targetFe)
        {
            var flyoutContent = Mount(flyEl.FlyoutContent, requestRerender);
            var flyout = new WinUI.Flyout { Content = flyoutContent, Placement = flyEl.Placement };
            SetElementTag(targetFe, flyEl);
            // Route handlers through the target's Tag so Update() refreshing the tag to the
            // new FlyoutElement causes subsequent Opened/Closed to fire the current delegates —
            // capturing flyEl directly would freeze handlers to the mount-time element.
            flyout.Opened += (_, _) => (GetElementTag(targetFe) as FlyoutElement)?.OnOpened?.Invoke();
            flyout.Closed += (_, _) => (GetElementTag(targetFe) as FlyoutElement)?.OnClosed?.Invoke();
            // SetFlyoutOnControl wires .Flyout on Button/SplitButton targets so
            // clicking opens the flyout natively; non-button targets fall back
            // to SetAttachedFlyout metadata (opened only via ShowAttachedFlyout).
            SetFlyoutOnControl(targetFe, flyout);
            ApplySetters(flyEl.Setters, flyout);
            if (flyEl.IsOpen) WinPrim.FlyoutBase.ShowAttachedFlyout(targetFe);
        }
        return target;
    }

    private WinUI.TeachingTip MountTeachingTip(TeachingTipElement ttEl, Action requestRerender)
    {
        var tip = new WinUI.TeachingTip { Title = ttEl.Title, Subtitle = ttEl.Subtitle ?? "", IsOpen = ttEl.IsOpen };
        if (ttEl.Content is not null) tip.Content = Mount(ttEl.Content, requestRerender);
        if (ttEl.ActionButtonContent is not null)
        {
            tip.ActionButtonContent = ttEl.ActionButtonContent;
            tip.ActionButtonClick += (_, _) => ttEl.OnActionButtonClick?.Invoke();
        }
        if (ttEl.CloseButtonContent is not null) tip.CloseButtonContent = ttEl.CloseButtonContent;
        SetElementTag(tip, ttEl);
        tip.Closed += (s, _) => (GetElementTag((UIElement)s!) as TeachingTipElement)?.OnClosed?.Invoke();
        ApplySetters(ttEl.Setters, tip);
        return tip;
    }

    private WinUI.MenuBar MountMenuBar(MenuBarElement mbEl)
    {
        var menuBar = new WinUI.MenuBar();
        foreach (var menuItem in mbEl.Items)
        {
            var mbi = new WinUI.MenuBarItem { Title = menuItem.Title };
            foreach (var flyoutItem in menuItem.Items) mbi.Items.Add(CreateMenuFlyoutItem(flyoutItem));
            menuBar.Items.Add(mbi);
        }
        ApplySetters(mbEl.Setters, menuBar);
        return menuBar;
    }

    private WinUI.Grid MountCommandHost(CommandHostElement ch, Action requestRerender)
    {
        var host = new WinUI.Grid();
        var child = Mount(ch.Child, requestRerender);
        if (child is not null) host.Children.Add(child);

        AddCommandHostAccelerators(host, ch.Commands);

        SetElementTag(host, ch);
        return host;
    }

    private static void AddCommandHostAccelerators(WinUI.Grid host, Command[] commands)
    {
        foreach (var cmd in commands)
        {
            if (cmd.Accelerator is null) continue;
            var ka = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
            {
                Key = cmd.Accelerator.Key,
                Modifiers = cmd.Accelerator.Modifiers,
            };
            var command = cmd;
            ka.Invoked += (s, e) =>
            {
                // Scope check: only fire if focus is within this CommandHost subtree
                var xamlRoot = host.XamlRoot;
                if (xamlRoot is null) return;
                var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(xamlRoot) as DependencyObject;
                if (focused is null || !IsDescendantOf(focused, host))
                {
                    // Don't mark handled — let other handlers process it
                    return;
                }

                e.Handled = true;
                if (command.IsEnabled)
                    command.Execute?.Invoke();
            };
            host.KeyboardAccelerators.Add(ka);
        }
    }

    private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        var current = element;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private WinUI.CommandBar MountCommandBar(CommandBarElement cmdEl, Action requestRerender)
    {
        var commandBar = new WinUI.CommandBar
        {
            DefaultLabelPosition = cmdEl.DefaultLabelPosition,
            IsOpen = cmdEl.IsOpen,
        };
        if (cmdEl.Content is not null) commandBar.Content = Mount(cmdEl.Content, requestRerender);
        if (cmdEl.PrimaryCommands is not null)
            foreach (var cmd in cmdEl.PrimaryCommands) commandBar.PrimaryCommands.Add(CreateAppBarItem(cmd));
        if (cmdEl.SecondaryCommands is not null)
            foreach (var cmd in cmdEl.SecondaryCommands) commandBar.SecondaryCommands.Add(CreateAppBarItem(cmd));
        SetElementTag(commandBar, cmdEl);
        ApplySetters(cmdEl.Setters, commandBar);
        return commandBar;
    }

    private static WinUI.ICommandBarElement CreateAppBarItem(AppBarItemBase item)
    {
        switch (item)
        {
            case AppBarButtonData cmd:
            {
                var abb = new WinUI.AppBarButton { Label = cmd.Label };
                abb.IsEnabled = cmd.IsEnabled;
                abb.Icon = ResolveIcon(cmd.IconElement, cmd.Icon);
                if (cmd.KeyboardAccelerators is not null)
                    foreach (var ka in cmd.KeyboardAccelerators)
                        abb.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = ka.Key, Modifiers = ka.Modifiers });
                if (cmd.AccessKey is not null) abb.AccessKey = cmd.AccessKey;
                if (cmd.Description is not null)
                {
                    WinUI.ToolTipService.SetToolTip(abb, cmd.Description);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(abb, cmd.Description);
                }
                abb.Tag = cmd;
                abb.Click += (s, _) => ((AppBarButtonData)((WinUI.AppBarButton)s!).Tag!).OnClick?.Invoke();
                return abb;
            }
            case AppBarToggleButtonData toggle:
            {
                var atb = new WinUI.AppBarToggleButton { Label = toggle.Label, IsChecked = toggle.IsChecked };
                atb.Icon = ResolveIcon(toggle.IconElement, toggle.Icon);
                atb.Tag = toggle;
                atb.Checked += (s, _) => ((AppBarToggleButtonData)((WinUI.AppBarToggleButton)s!).Tag!).OnToggled?.Invoke(true);
                atb.Unchecked += (s, _) => ((AppBarToggleButtonData)((WinUI.AppBarToggleButton)s!).Tag!).OnToggled?.Invoke(false);
                return atb;
            }
            case AppBarSeparatorData:
                return new WinUI.AppBarSeparator();
            default:
                return new WinUI.AppBarSeparator();
        }
    }

    private UIElement? MountMenuFlyout(MenuFlyoutElement mfEl, Action requestRerender)
    {
        var target = Mount(mfEl.Target, requestRerender);
        if (target is FrameworkElement targetFe)
        {
            var menuFlyout = new WinUI.MenuFlyout();
            foreach (var item in mfEl.Items) menuFlyout.Items.Add(CreateMenuFlyoutItem(item));
            SetElementTag(targetFe, mfEl);
            // Use SetFlyoutOnControl so clicking a Button/SplitButton target opens
            // the flyout via .Flyout; non-button targets fall back to attached-flyout
            // metadata (still requires explicit ShowAttachedFlyout to open).
            SetFlyoutOnControl(targetFe, menuFlyout);
            ApplySetters(mfEl.Setters, menuFlyout);
        }
        return target;
    }

    private static WinUI.MenuFlyoutItemBase CreateMenuFlyoutItem(MenuFlyoutItemBase item)
    {
        switch (item)
        {
            case MenuFlyoutItemData mfi:
            {
                var flyoutItem = new WinUI.MenuFlyoutItem { Text = mfi.Text };
                flyoutItem.IsEnabled = mfi.IsEnabled;
                flyoutItem.Icon = ResolveIcon(mfi.IconElement, mfi.Icon);
                if (mfi.KeyboardAccelerators is not null)
                    foreach (var ka in mfi.KeyboardAccelerators)
                        flyoutItem.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = ka.Key, Modifiers = ka.Modifiers });
                if (mfi.AccessKey is not null) flyoutItem.AccessKey = mfi.AccessKey;
                if (mfi.Description is not null)
                {
                    WinUI.ToolTipService.SetToolTip(flyoutItem, mfi.Description);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(flyoutItem, mfi.Description);
                }
                flyoutItem.Tag = mfi;
                flyoutItem.Click += (s, _) => ((MenuFlyoutItemData)((WinUI.MenuFlyoutItem)s!).Tag!).OnClick?.Invoke();
                return flyoutItem;
            }
            case ToggleMenuFlyoutItemData toggle:
            {
                var toggleItem = new WinUI.ToggleMenuFlyoutItem { Text = toggle.Text, IsChecked = toggle.IsChecked };
                toggleItem.Icon = ResolveIcon(toggle.IconElement, toggle.Icon);
                toggleItem.Tag = toggle;
                toggleItem.Click += (s, _) =>
                {
                    var ti = (WinUI.ToggleMenuFlyoutItem)s!;
                    ((ToggleMenuFlyoutItemData)ti.Tag!).OnToggled?.Invoke(ti.IsChecked);
                };
                return toggleItem;
            }
            case RadioMenuFlyoutItemData radio:
            {
                var radioItem = new WinUI.RadioMenuFlyoutItem { Text = radio.Text, GroupName = radio.GroupName, IsChecked = radio.IsChecked };
                radioItem.Tag = radio;
                radioItem.Click += (s, _) => ((RadioMenuFlyoutItemData)((WinUI.RadioMenuFlyoutItem)s!).Tag!).OnClick?.Invoke();
                return radioItem;
            }
            case MenuFlyoutSeparatorData:
                return new WinUI.MenuFlyoutSeparator();
            case MenuFlyoutSubItemData sub:
            {
                var subItem = new WinUI.MenuFlyoutSubItem { Text = sub.Text };
                subItem.Icon = ResolveIcon(sub.IconElement, sub.Icon);
                foreach (var child in sub.Items) subItem.Items.Add(CreateMenuFlyoutItem(child));
                return subItem;
            }
            default:
                return new WinUI.MenuFlyoutSeparator();
        }
    }

    private static WinUI.IconElement? ResolveIcon(IconData? iconData, string? iconSymbol)
    {
        if (iconData is not null)
        {
            return iconData switch
            {
                SymbolIconData sym => ResolveIconString(sym.Symbol) ?? new WinUI.SymbolIcon(Symbol.Placeholder),
                FontIconData fi => CreateFontIcon(fi),
                BitmapIconData bi => new WinUI.BitmapIcon { UriSource = bi.Source, ShowAsMonochrome = bi.ShowAsMonochrome },
                PathIconData pi => CreatePathIcon(pi),
                ImageIconData ii => new WinUI.ImageIcon { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(ii.Source) },
                _ => null,
            };
        }
        if (iconSymbol is not null) return ResolveIconString(iconSymbol);
        return null;
    }

    // Handles both Symbol enum names ("Home", "Edit") and raw Segoe Fluent
    // glyphs (""). A Symbol enum mismatch used to collapse to
    // Symbol.Placeholder, which rendered as a diamond — fall through to a
    // FontIcon with SymbolThemeFontFamily so glyph strings render correctly.
    private static WinUI.IconElement? ResolveIconString(string iconSymbol)
    {
        if (string.IsNullOrEmpty(iconSymbol)) return null;
        if (Enum.TryParse<Symbol>(iconSymbol, ignoreCase: true, out var symbol))
            return new WinUI.SymbolIcon(symbol);
        // Treat as a Segoe Fluent / MDL2 glyph codepoint.
        return new WinUI.FontIcon
        {
            Glyph = iconSymbol,
            FontFamily = Microsoft.UI.Xaml.Application.Current?.Resources["SymbolThemeFontFamily"] as Microsoft.UI.Xaml.Media.FontFamily
                         ?? new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
        };
    }

    // IconSource counterpart for controls (TabView, etc.) that take an
    // IconSource instead of IconElement. Same glyph-fallback semantics as
    // ResolveIconString.
    internal static WinUI.IconSource? ResolveIconSource(string? iconSymbol)
    {
        if (string.IsNullOrEmpty(iconSymbol)) return null;
        if (Enum.TryParse<Symbol>(iconSymbol, ignoreCase: true, out var symbol))
            return new WinUI.SymbolIconSource { Symbol = symbol };
        return new WinUI.FontIconSource
        {
            Glyph = iconSymbol,
            FontFamily = Microsoft.UI.Xaml.Application.Current?.Resources["SymbolThemeFontFamily"] as Microsoft.UI.Xaml.Media.FontFamily
                         ?? new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
        };
    }

    private static WinUI.FontIcon CreateFontIcon(FontIconData fi)
    {
        var icon = new WinUI.FontIcon { Glyph = fi.Glyph };
        if (fi.FontFamily is not null) icon.FontFamily = WinRTCache.GetFontFamily(fi.FontFamily);
        if (fi.FontSize.HasValue) icon.FontSize = fi.FontSize.Value;
        return icon;
    }

    private static WinUI.PathIcon CreatePathIcon(PathIconData pi)
    {
        var icon = new WinUI.PathIcon();
        if (Microsoft.UI.Xaml.Markup.XamlReader.Load(
            $"<Geometry xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>{pi.Data}</Geometry>")
            is Microsoft.UI.Xaml.Media.Geometry geo)
        {
            icon.Data = geo;
        }
        return icon;
    }

    // ════════════════════════════════════════════════════════════════
    //  Validation elements
    // ════════════════════════════════════════════════════════════════

    private WinUI.StackPanel MountFormField(FormFieldElement ff, Action requestRerender)
    {
        var panel = new WinUI.StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };

        // Resolve field name from explicit or auto-detected from Content's ValidationAttached
        var fieldName = FormFieldHelpers.ResolveFieldName(ff.FieldName, ff.Content);

        // Auto-validate: if Content has attached validators with a Value, run them now
        var attached = ff.Content.GetAttached<ValidationAttached>();
        var valCtx = _contextScope.Read(ValidationContexts.Current);
        if (valCtx is not null && attached is not null && attached.Validators.Length > 0)
        {
            ValidationReconciler.ValidateAttached(valCtx, attached, attached.Value);
        }

        // [0] Label — always present, collapsed when empty
        var displayLabel = FormFieldHelpers.GetDisplayLabel(ff.Label, ff.Required);
        var labelTb = new TextBlock
        {
            Text = displayLabel,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Visibility = displayLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        panel.Children.Add(labelTb);

        // [1] Content (the actual form control) — always present
        var contentControl = Mount(ff.Content, requestRerender);
        if (contentControl is not null)
        {
            ApplyFormFieldAutomation(contentControl, ff.Label);
            ApplyFormFieldErrorStyling(contentControl, valCtx, fieldName, ff.ShowWhen);
            panel.Children.Add(contentControl);
        }
        else
        {
            // Placeholder so indices stay fixed
            panel.Children.Add(new WinUI.StackPanel { Visibility = Visibility.Collapsed });
        }

        // [2] Description/error text — always present, collapsed when empty
        var descTb = new TextBlock { FontSize = 12 };
        ApplyFormFieldDescription(descTb, valCtx, fieldName, ff.Description, ff.ShowWhen);
        panel.Children.Add(descTb);

        SetElementTag(panel, ff);
        return panel;
    }

    private static void ApplyFormFieldAutomation(UIElement contentControl, string? label)
    {
        var automationName = FormFieldHelpers.GetAutomationName(label);
        if (automationName is not null && contentControl is FrameworkElement cfe)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cfe, automationName);
    }

    private static void ApplyFormFieldErrorStyling(
        UIElement contentControl, ValidationContext? valCtx, string? fieldName, ShowWhen showWhen)
    {
        if (contentControl is not WinUI.Control ctrl)
            return;

        if (valCtx is not null && fieldName is not null)
        {
            var severity = valCtx.HighestSeverity(fieldName);
            if (severity is not null && ErrorStyling.ShouldShowErrors(valCtx, fieldName, showWhen))
            {
                var brushKey = ErrorStyling.GetBrushKey(severity.Value);
                var brush = ThemeRef.Resolve(brushKey, ctrl);
                if (brush is not null)
                {
                    ctrl.BorderBrush = brush;
                    ctrl.BorderThickness = ErrorStyling.ErrorBorderThickness;
                }
                return;
            }
        }

        // Clear error styling — reset to default
        ctrl.ClearValue(WinUI.Control.BorderBrushProperty);
        ctrl.ClearValue(WinUI.Control.BorderThicknessProperty);
    }

    private static void ApplyFormFieldDescription(
        TextBlock descTb, ValidationContext? valCtx, string? fieldName,
        string? description, ShowWhen showWhen)
    {
        var (descText, isError) = FormFieldHelpers.GetDescriptionOrError(
            valCtx, fieldName, description, showWhen);

        if (descText is null)
        {
            descTb.Text = "";
            descTb.Visibility = Visibility.Collapsed;
            return;
        }

        descTb.Text = descText;
        descTb.Visibility = Visibility.Visible;
        descTb.Opacity = 1.0;

        if (isError)
        {
            var errorBrush = ThemeRef.Resolve(ErrorStyling.ErrorBrushKey, descTb);
            descTb.Foreground = errorBrush
                ?? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
        }
        else
        {
            descTb.ClearValue(TextBlock.ForegroundProperty);
            descTb.Opacity = 0.6;
        }
    }

    private WinUI.StackPanel MountValidationVisualizer(
        ValidationVisualizerElement vv, Action requestRerender)
    {
        var panel = new WinUI.StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };
        var valCtx = _contextScope.Read(ValidationContexts.Current);

        // Mount the content subtree first
        var contentControl = Mount(vv.Content, requestRerender);

        // Collect messages from the validation context
        var allMessages = valCtx?.GetAllMessages() ?? (IReadOnlyList<ValidationMessage>)[];
        var (caught, _) = ErrorBubbling.FilterMessages(allMessages, vv.SeverityFilter);
        var shouldDisplay = ErrorBubbling.ShouldDisplay(caught, vv.ShowWhen, valCtx);

        switch (vv.Style)
        {
            case VisualizerStyle.InfoBar when shouldDisplay && caught.Count > 0:
            {
                var severity = ErrorBubbling.HighestSeverity(caught);
                var infoBarSeverity = severity switch
                {
                    Severity.Error => InfoBarSeverity.Error,
                    Severity.Warning => InfoBarSeverity.Warning,
                    _ => InfoBarSeverity.Informational,
                };
                var infoBar = new WinUI.InfoBar
                {
                    Title = vv.Title ?? (severity == Severity.Error ? "Errors" : "Warnings"),
                    Message = string.Join("\n", caught.Select(m => m.Text)),
                    Severity = infoBarSeverity,
                    IsOpen = true,
                    IsClosable = false,
                };
                panel.Children.Add(infoBar);
                break;
            }
            case VisualizerStyle.Summary when shouldDisplay && caught.Count > 0:
            {
                if (vv.Title is not null)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = vv.Title,
                        FontSize = 13,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    });
                }
                foreach (var msg in caught)
                {
                    var bullet = new TextBlock
                    {
                        Text = $"• {msg.Text}",
                        FontSize = 12,
                    };
                    var brush = ThemeRef.Resolve(ErrorStyling.GetBrushKey(msg.Severity), bullet);
                    if (brush is not null) bullet.Foreground = brush;
                    panel.Children.Add(bullet);
                }
                break;
            }
            case VisualizerStyle.Custom when shouldDisplay && vv.CustomRender is not null:
            {
                var customElement = vv.CustomRender(caught);
                var customControl = Mount(customElement, requestRerender);
                if (customControl is not null)
                    panel.Children.Add(customControl);
                break;
            }
            case VisualizerStyle.Inline when shouldDisplay && caught.Count > 0:
            {
                // Inline errors rendered after the content below
                break;
            }
        }

        // Add the content control
        if (contentControl is not null)
            panel.Children.Add(contentControl);

        // Inline error text below the content
        if (vv.Style == VisualizerStyle.Inline && shouldDisplay && caught.Count > 0)
        {
            var errorText = string.Join(" • ", caught.Select(m => m.Text));
            var errorTb = new TextBlock { Text = errorText, FontSize = 12 };
            var brush = ThemeRef.Resolve(ErrorStyling.ErrorBrushKey, errorTb);
            if (brush is not null)
                errorTb.Foreground = brush;
            else
                errorTb.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.Red);
            panel.Children.Add(errorTb);
        }

        SetElementTag(panel, vv);
        return panel;
    }

    private UIElement MountValidationRule(ValidationRuleElement rule)
    {
        // Evaluate the rule against the nearest ValidationContext
        var valCtx = _contextScope.Read(ValidationContexts.Current);
        if (valCtx is not null)
            rule.Evaluate(valCtx);

        // Return a collapsed placeholder — validation rules produce no UI
        var placeholder = new WinUI.StackPanel { Visibility = Visibility.Collapsed };
        SetElementTag(placeholder, rule);
        return placeholder;
    }

    private UIElement MountErrorBoundary(ErrorBoundaryElement eb, Action requestRerender)
    {
        var wrapper = new Border();
        Element renderedElement;
        Exception? caughtEx = null;

        _errorBoundaryDepth++;
        try
        {
            renderedElement = eb.Child;
            wrapper.Child = Mount(eb.Child, requestRerender);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ErrorBoundary caught render error");
            caughtEx = ex;
            renderedElement = eb.Fallback(ex);
            wrapper.Child = Mount(renderedElement, requestRerender);
        }
        finally
        {
            _errorBoundaryDepth--;
        }

        _errorBoundaryNodes[wrapper] = new ErrorBoundaryNode
        {
            ChildElement = eb.Child,
            RenderedElement = renderedElement,
            CaughtException = caughtEx,
            Fallback = eb.Fallback,
        };

        return wrapper;
    }

    private UIElement MountComponent(ComponentElement compElement, Action requestRerender)
    {
        var component = compElement.CreateInstance();

        if (compElement.Props is not null && component is IPropsReceiver receiver)
            receiver.SetProps(compElement.Props);

        // Each component gets its own Border wrapper as an identity anchor
        // in _componentNodes, preventing key collisions when components nest.
        var wrapper = new Border();
        var node = new ComponentNode
        {
            Component = component, RenderedElement = null, Element = compElement,
            PreviousProps = compElement.Props,
        };
        _componentNodes[wrapper] = node;

        // Pass the component's own wrapped rerender to children so that child state
        // changes propagate SelfTriggered up through all component ancestors.
        var componentRerender = CreateComponentRerender(node, requestRerender);

        Element childElement;
        try
        {
            component.Context.BeginRender(componentRerender, _contextScope);
            childElement = component.Render();
            component.Context.FlushEffects();
        }
        catch (Exception ex) when (_errorBoundaryDepth == 0 && ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogError(ex, "Component Render() threw during mount: {ComponentName}", compElement.GetType().Name);
            childElement = new TextBlockElement($"⚠ Render error: {ex.Message}");
        }
        var childControl = Mount(childElement, componentRerender);

        wrapper.Child = childControl;
        node.RenderedElement = childElement;
        return wrapper;
    }

    private UIElement MountFuncComponent(FuncElement funcElement, Action requestRerender)
    {
        var ctx = new RenderContext();
        var wrapper = new Border();
        var node = new ComponentNode
        {
            Context = ctx, RenderedElement = null, Element = funcElement,
        };
        _componentNodes[wrapper] = node;

        // Pass the component's own wrapped rerender to children so that child state
        // changes propagate SelfTriggered up through all component ancestors.
        var componentRerender = CreateComponentRerender(node, requestRerender);

        Element childElement;
        try
        {
            ctx.BeginRender(componentRerender, _contextScope);
            childElement = funcElement.RenderFunc(ctx);
            ctx.FlushEffects();
        }
        catch (Exception ex) when (_errorBoundaryDepth == 0 && ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogError(ex, "FuncComponent Render() threw during mount");
            childElement = new TextBlockElement($"⚠ Render error: {ex.Message}");
        }
        var childControl = Mount(childElement, componentRerender);

        wrapper.Child = childControl;
        node.RenderedElement = childElement;
        return wrapper;
    }

    private UIElement MountMemoComponent(MemoElement memoElement, Action requestRerender)
    {
        var ctx = new RenderContext();
        var wrapper = new Border();
        var node = new ComponentNode
        {
            Context = ctx, RenderedElement = null, Element = memoElement,
            MemoDependencies = memoElement.Dependencies,
        };
        _componentNodes[wrapper] = node;

        // Pass the component's own wrapped rerender to children so that child state
        // changes propagate SelfTriggered up through all component ancestors.
        var componentRerender = CreateComponentRerender(node, requestRerender);

        Element childElement;
        try
        {
            ctx.BeginRender(componentRerender, _contextScope);
            childElement = memoElement.RenderFunc(ctx);
            ctx.FlushEffects();
        }
        catch (Exception ex) when (_errorBoundaryDepth == 0 && ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogError(ex, "MemoComponent Render() threw during mount");
            childElement = new TextBlockElement($"⚠ Render error: {ex.Message}");
        }
        var childControl = Mount(childElement, componentRerender);

        wrapper.Child = childControl;
        node.RenderedElement = childElement;
        return wrapper;
    }

    private UIElement MountLazyStack(LazyStackElementBase lazy, Action requestRerender)
    {
        var repeater = new WinUI.ItemsRepeater();

        repeater.Layout = new WinUI.StackLayout
        {
            Orientation = lazy.Orientation,
            Spacing = lazy.Spacing,
        };

        repeater.ItemsSource = lazy.GetItemsSource();
        repeater.ItemTemplate = lazy.CreateFactory(this, requestRerender, _pool);
        SetElementTag(repeater, lazy);
        ApplySetters(lazy.RepeaterSetters, repeater);

        var sv = _pool.TryRent(typeof(WinUI.ScrollViewer)) as WinUI.ScrollViewer ?? new WinUI.ScrollViewer();
        sv.Content = repeater;
        sv.HorizontalScrollBarVisibility = lazy.Orientation == Microsoft.UI.Xaml.Controls.Orientation.Horizontal
            ? WinUI.ScrollBarVisibility.Auto
            : WinUI.ScrollBarVisibility.Disabled;
        sv.VerticalScrollBarVisibility = lazy.Orientation == Microsoft.UI.Xaml.Controls.Orientation.Vertical
            ? WinUI.ScrollBarVisibility.Auto
            : WinUI.ScrollBarVisibility.Disabled;
        SetElementTag(sv, lazy);
        ApplySetters(lazy.ScrollViewerSetters, sv);

        return sv;
    }

    // ── Shape elements ──────────────────────────────────────────────────

    private WinShapes.Rectangle MountRectangle(RectangleElement rect)
    {
        var r = new WinShapes.Rectangle();
        if (rect.Fill is not null) r.Fill = rect.Fill;
        if (rect.Stroke is not null) r.Stroke = rect.Stroke;
        if (rect.StrokeThickness > 0) r.StrokeThickness = rect.StrokeThickness;
        if (rect.RadiusX > 0) r.RadiusX = rect.RadiusX;
        if (rect.RadiusY > 0) r.RadiusY = rect.RadiusY;
        ApplySetters(rect.Setters, r);
        return r;
    }

    private WinShapes.Ellipse MountEllipse(EllipseElement ell)
    {
        var e = new WinShapes.Ellipse();
        if (ell.Fill is not null) e.Fill = ell.Fill;
        if (ell.Stroke is not null) e.Stroke = ell.Stroke;
        if (ell.StrokeThickness > 0) e.StrokeThickness = ell.StrokeThickness;
        ApplySetters(ell.Setters, e);
        return e;
    }

    private WinShapes.Line MountLine(LineElement ln)
    {
        var l = new WinShapes.Line { X1 = ln.X1, Y1 = ln.Y1, X2 = ln.X2, Y2 = ln.Y2 };
        if (ln.Stroke is not null) l.Stroke = ln.Stroke;
        if (ln.StrokeThickness > 0) l.StrokeThickness = ln.StrokeThickness;
        ApplySetters(ln.Setters, l);
        return l;
    }

    private WinShapes.Path MountPath(PathElement pa)
    {
        var p = new WinShapes.Path();
        if (pa.Data is not null) p.Data = pa.Data;
        if (pa.Fill is not null) p.Fill = pa.Fill;
        if (pa.Stroke is not null) p.Stroke = pa.Stroke;
        if (pa.StrokeThickness > 0) p.StrokeThickness = pa.StrokeThickness;
        if (pa.StrokeDashArray is not null) p.StrokeDashArray = pa.StrokeDashArray;
        if (pa.RenderTransform is not null) p.RenderTransform = pa.RenderTransform;
        ApplySetters(pa.Setters, p);
        return p;
    }

    // ── RelativePanel ───────────────────────────────────────────────────

    private WinUI.RelativePanel MountRelativePanel(RelativePanelElement rp, Action requestRerender)
    {
        var panel = new WinUI.RelativePanel();
        var nameMap = new Dictionary<string, UIElement>();

        // First pass: mount all children and register names
        foreach (var child in rp.Children)
        {
            var ctrl = Mount(child, requestRerender);
            if (ctrl is null) continue;
            var rpa = child.GetAttached<RelativePanelAttached>();
            if (rpa is not null && ctrl is FrameworkElement fe) fe.Name = rpa.Name;
            if (rpa is not null) nameMap[rpa.Name] = ctrl;
            panel.Children.Add(ctrl);
        }

        // Second pass: apply attached properties using name references
        foreach (var child in rp.Children)
        {
            var rpa = child.GetAttached<RelativePanelAttached>();
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

            if (rpa.AlignLeftWithPanel) WinUI.RelativePanel.SetAlignLeftWithPanel(ctrl, true);
            if (rpa.AlignRightWithPanel) WinUI.RelativePanel.SetAlignRightWithPanel(ctrl, true);
            if (rpa.AlignTopWithPanel) WinUI.RelativePanel.SetAlignTopWithPanel(ctrl, true);
            if (rpa.AlignBottomWithPanel) WinUI.RelativePanel.SetAlignBottomWithPanel(ctrl, true);
            if (rpa.AlignHorizontalCenterWithPanel) WinUI.RelativePanel.SetAlignHorizontalCenterWithPanel(ctrl, true);
            if (rpa.AlignVerticalCenterWithPanel) WinUI.RelativePanel.SetAlignVerticalCenterWithPanel(ctrl, true);
        }

        SetElementTag(panel, rp);
        ApplySetters(rp.Setters, panel);
        return panel;
    }

    // ── MediaPlayerElement ──────────────────────────────────────────────

    private WinUI.MediaPlayerElement MountMediaPlayerElement(MediaPlayerElementElement mpe)
    {
        var player = new WinUI.MediaPlayerElement
        {
            AreTransportControlsEnabled = mpe.AreTransportControlsEnabled,
            AutoPlay = mpe.AutoPlay,
        };
        if (mpe.Source is not null)
            player.Source = global::Windows.Media.Core.MediaSource.CreateFromUri(new Uri(mpe.Source, UriKind.RelativeOrAbsolute));
        SetElementTag(player, mpe);
        ApplySetters(mpe.Setters, player);
        return player;
    }

    // ── AnimatedVisualPlayer ────────────────────────────────────────────

    private WinUI.AnimatedVisualPlayer MountAnimatedVisualPlayer(AnimatedVisualPlayerElement avp)
    {
        var player = new WinUI.AnimatedVisualPlayer { AutoPlay = avp.AutoPlay };
        SetElementTag(player, avp);
        ApplySetters(avp.Setters, player);
        return player;
    }

    // ── SemanticZoom ────────────────────────────────────────────────────

    private WinUI.SemanticZoom MountSemanticZoom(SemanticZoomElement sz, Action requestRerender)
    {
        var zoomedIn = Mount(sz.ZoomedInView, requestRerender);
        var zoomedOut = Mount(sz.ZoomedOutView, requestRerender);
        var semanticZoom = new WinUI.SemanticZoom();
        if (zoomedIn is ISemanticZoomInformation szi) semanticZoom.ZoomedInView = szi;
        if (zoomedOut is ISemanticZoomInformation szo) semanticZoom.ZoomedOutView = szo;
        SetElementTag(semanticZoom, sz);
        ApplySetters(sz.Setters, semanticZoom);
        return semanticZoom;
    }

    // ── ListBox ─────────────────────────────────────────────────────────

    private WinUI.ListBox MountListBox(ListBoxElement lb)
    {
        var listBox = new WinUI.ListBox { SelectedIndex = lb.SelectedIndex };
        foreach (var item in lb.Items) listBox.Items.Add(item);
        SetElementTag(listBox, lb);
        listBox.SelectionChanged += (s, _) =>
        {
            var l = (WinUI.ListBox)s!;
            (GetElementTag(l) as ListBoxElement)?.OnSelectionChanged?.Invoke(l.SelectedIndex);
        };
        ApplySetters(lb.Setters, listBox);
        return listBox;
    }

    // ── SelectorBar ─────────────────────────────────────────────────────

    private WinUI.SelectorBar MountSelectorBar(SelectorBarElement sb)
    {
        var selectorBar = new WinUI.SelectorBar();
        foreach (var item in sb.Items)
        {
            var sbi = new WinUI.SelectorBarItem { Text = item.Text };
            if (item.Icon is not null) sbi.Icon = new WinUI.SymbolIcon(ParseSymbol(item.Icon));
            selectorBar.Items.Add(sbi);
        }
        if (sb.SelectedIndex >= 0 && sb.SelectedIndex < selectorBar.Items.Count)
            selectorBar.SelectedItem = selectorBar.Items[sb.SelectedIndex];
        SetElementTag(selectorBar, sb);
        selectorBar.SelectionChanged += (s, _) =>
        {
            var bar = (WinUI.SelectorBar)s!;
            var idx = bar.Items.IndexOf(bar.SelectedItem);
            (GetElementTag(bar) as SelectorBarElement)?.OnSelectionChanged?.Invoke(idx);
        };
        ApplySetters(sb.Setters, selectorBar);
        return selectorBar;
    }

    // ── PipsPager ───────────────────────────────────────────────────────

    private WinUI.PipsPager MountPipsPager(PipsPagerElement pp)
    {
        var pager = new WinUI.PipsPager
        {
            NumberOfPages = pp.NumberOfPages,
            SelectedPageIndex = pp.SelectedPageIndex,
        };
        SetElementTag(pager, pp);
        pager.SelectedIndexChanged += (s, _) =>
        {
            var p = (WinUI.PipsPager)s!;
            (GetElementTag(p) as PipsPagerElement)?.OnSelectedIndexChanged?.Invoke(p.SelectedPageIndex);
        };
        ApplySetters(pp.Setters, pager);
        return pager;
    }

    // ── AnnotatedScrollBar ──────────────────────────────────────────────

    private WinUI.AnnotatedScrollBar MountAnnotatedScrollBar(AnnotatedScrollBarElement asb)
    {
        var scrollBar = new WinUI.AnnotatedScrollBar();
        ApplySetters(asb.Setters, scrollBar);
        return scrollBar;
    }

    // ── Popup ───────────────────────────────────────────────────────────

    private UIElement MountPopup(PopupElement popup, Action requestRerender)
    {
        // Popup is not a UIElement child, so we wrap it in a StackPanel
        var wrapper = new WinUI.StackPanel();
        var p = new WinPrim.Popup
        {
            IsOpen = popup.IsOpen,
            IsLightDismissEnabled = popup.IsLightDismissEnabled,
            HorizontalOffset = popup.HorizontalOffset,
            VerticalOffset = popup.VerticalOffset,
        };
        var child = Mount(popup.Child, requestRerender);
        p.Child = child as UIElement;
        SetElementTag(wrapper, popup);
        p.Closed += (s, _) => (GetElementTag(wrapper) as PopupElement)?.OnClosed?.Invoke();
        ApplySetters(popup.Setters, p);
        wrapper.Children.Add(p);
        return wrapper;
    }

    // ── RefreshContainer ────────────────────────────────────────────────

    private WinUI.RefreshContainer MountRefreshContainer(RefreshContainerElement rc, Action requestRerender)
    {
        var container = new WinUI.RefreshContainer();
        container.Content = Mount(rc.Content, requestRerender);
        SetElementTag(container, rc);
        container.RefreshRequested += (s, _) =>
            (GetElementTag((UIElement)s!) as RefreshContainerElement)?.OnRefreshRequested?.Invoke();
        ApplySetters(rc.Setters, container);
        return container;
    }

    // ── CommandBarFlyout ────────────────────────────────────────────────

    private UIElement? MountCommandBarFlyout(CommandBarFlyoutElement cbf, Action requestRerender)
    {
        var target = Mount(cbf.Target, requestRerender);
        if (target is FrameworkElement targetFe)
        {
            var flyout = new WinUI.CommandBarFlyout { Placement = cbf.Placement };
            if (cbf.PrimaryCommands is not null)
                foreach (var cmd in cbf.PrimaryCommands) flyout.PrimaryCommands.Add(CreateAppBarItem(cmd));
            if (cbf.SecondaryCommands is not null)
                foreach (var cmd in cbf.SecondaryCommands) flyout.SecondaryCommands.Add(CreateAppBarItem(cmd));
            SetElementTag(targetFe, cbf);
            WinPrim.FlyoutBase.SetAttachedFlyout(targetFe, flyout);
            ApplySetters(cbf.Setters, flyout);
        }
        return target;
    }

    // ── CalendarView ────────────────────────────────────────────────────

    private WinUI.CalendarView MountCalendarView(CalendarViewElement cv)
    {
        var calendarView = new WinUI.CalendarView
        {
            SelectionMode = cv.SelectionMode,
            IsGroupLabelVisible = cv.IsGroupLabelVisible,
            IsOutOfScopeEnabled = cv.IsOutOfScopeEnabled,
        };
        if (cv.CalendarIdentifier is not null) calendarView.CalendarIdentifier = cv.CalendarIdentifier;
        if (cv.Language is not null && global::Windows.Globalization.Language.IsWellFormed(cv.Language))
            calendarView.Language = cv.Language;
        ApplySetters(cv.Setters, calendarView);
        return calendarView;
    }

    // ── SwipeControl ──────────────────────────────────────────────────

    private WinUI.SwipeControl MountSwipeControl(SwipeControlElement swipe, Action requestRerender)
    {
        var sc = new WinUI.SwipeControl();
        sc.Content = Mount(swipe.Content, requestRerender);

        if (swipe.LeftItems is { Length: > 0 })
        {
            var leftItems = new SwipeItems { Mode = swipe.LeftItemsMode };
            foreach (var item in swipe.LeftItems) leftItems.Add(CreateSwipeItem(item));
            sc.LeftItems = leftItems;
        }

        if (swipe.RightItems is { Length: > 0 })
        {
            var rightItems = new SwipeItems { Mode = swipe.RightItemsMode };
            foreach (var item in swipe.RightItems) rightItems.Add(CreateSwipeItem(item));
            sc.RightItems = rightItems;
        }

        SetElementTag(sc, swipe);
        ApplySetters(swipe.Setters, sc);
        return sc;
    }

    private static SwipeItem CreateSwipeItem(SwipeItemData data)
    {
        var si = new SwipeItem
        {
            Text = data.Text,
            BehaviorOnInvoked = data.BehaviorOnInvoked,
        };
        if (data.IconSource is not null) si.IconSource = data.IconSource;
        if (data.Background is not null) si.Background = data.Background;
        if (data.Foreground is not null) si.Foreground = data.Foreground;
        if (data.OnInvoked is not null) si.Invoked += (s, e) => data.OnInvoked();
        return si;
    }

    // ── AnimatedIcon ──────────────────────────────────────────────────

    private WinUI.AnimatedIcon MountAnimatedIcon(AnimatedIconElement ai)
    {
        var icon = new WinUI.AnimatedIcon();
        if (ai.Source is Microsoft.UI.Xaml.Controls.IAnimatedVisualSource2 src)
            icon.Source = src;
        if (ai.FallbackIconSource is not null) icon.FallbackIconSource = ai.FallbackIconSource;
        ApplySetters(ai.Setters, icon);
        return icon;
    }

    // ── ParallaxView ──────────────────────────────────────────────────

    private WinUI.ParallaxView MountParallaxView(ParallaxViewElement pv, Action requestRerender)
    {
        var parallax = new WinUI.ParallaxView
        {
            VerticalShift = pv.VerticalShift,
            HorizontalShift = pv.HorizontalShift,
        };
        parallax.Child = Mount(pv.Child, requestRerender) as UIElement;
        ApplySetters(pv.Setters, parallax);
        return parallax;
    }

    // ── MapControl ────────────────────────────────────────────────────

    private WinUI.MapControl MountMapControl(MapControlElement mc)
    {
        var map = new WinUI.MapControl
        {
            ZoomLevel = mc.ZoomLevel,
        };
        if (mc.MapServiceToken is not null) map.MapServiceToken = mc.MapServiceToken;
        ApplySetters(mc.Setters, map);
        return map;
    }

    // ── Frame ─────────────────────────────────────────────────────────

    private WinUI.Frame MountFrame(FrameElement frame)
    {
        var f = new WinUI.Frame();
        if (frame.SourcePageType is not null)
            f.Navigate(frame.SourcePageType, frame.NavigationParameter);
        ApplySetters(frame.Setters, f);
        return f;
    }

    // ════════════════════════════════════════════════════════════════════
    //  XamlHostElement / XamlPageElement — built-in XAML interop
    // ════════════════════════════════════════════════════════════════════

    private static UIElement MountXamlHost(XamlHostElement host)
    {
        var control = host.Factory();
        host.Updater?.Invoke(control);
        SetElementTag(control, host);
        return control;
    }

    private static UIElement MountXamlPage(XamlPageElement page)
    {
        var frame = new WinUI.Frame();
        frame.Navigate(page.PageType, page.Parameter);
        SetElementTag(frame, page);
        return frame;
    }

    /// <summary>
    /// Mounts a zero-size hidden TextBlock for screen reader live-region announcements.
    /// The TextBlock is connected to the <see cref="AnnounceHandle"/> so that
    /// <see cref="AnnounceHandle.Announce"/> can raise UIA notifications through it.
    /// </summary>
    private static UIElement MountAnnounceRegion(AnnounceRegionElement ann)
    {
        var tb = new TextBlock
        {
            Width = 0,
            Height = 0,
            Opacity = 0,
            IsHitTestVisible = false,
            IsTabStop = false,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(
            tb, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
            tb, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        ann.Handle.SetTextBlock(tb);
        SetElementTag(tb, ann);
        return tb;
    }

    /// <summary>
    /// Mounts a SemanticPanel that provides custom automation semantics
    /// for composite Reactor components that can't override OnCreateAutomationPeer().
    /// </summary>
    private UIElement MountSemantic(SemanticElement sem, Action requestRerender)
    {
        var panel = new Accessibility.SemanticPanel();

        // Apply semantic properties
        var s = sem.Semantics;
        if (s.Role is not null) panel.SemanticRole = s.Role;
        if (s.Value is not null) panel.SemanticValue = s.Value;
        if (s.RangeMin.HasValue) panel.RangeMinimum = s.RangeMin.Value;
        if (s.RangeMax.HasValue) panel.RangeMaximum = s.RangeMax.Value;
        if (s.RangeValue.HasValue) panel.RangeValue = s.RangeValue.Value;
        panel.IsReadOnly = s.IsReadOnly;

        // Mount child inside the panel
        var childControl = Mount(sem.Child, requestRerender);
        if (childControl is not null)
            panel.Children.Add(childControl);

        SetElementTag(panel, sem);
        return panel;
    }
}
