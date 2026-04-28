using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClawTray.A2UI.Protocol;

namespace OpenClawTray.A2UI.Rendering.Renderers;

public sealed class ButtonRenderer : IComponentRenderer
{
    public string ComponentName => "Button";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var btn = new Button { HorizontalAlignment = HorizontalAlignment.Stretch };

        var childId = ctx.GetSingleChild(c, "child");
        if (childId != null)
            btn.Content = ctx.BuildChild(childId);

        var primary = c.Properties["primary"]?.GetValue<bool>() ?? false;
        if (primary)
        {
            if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accentStyle) && accentStyle is Style s)
                btn.Style = s;
        }

        var actionNode = c.Properties["action"];
        var actionName = (actionNode is JsonObject ao && ao["name"] is JsonValue nv && nv.TryGetValue<string>(out var n)) ? n : null;

        btn.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(actionName)) return;
            ctx.Actions.Raise(new A2UIAction
            {
                Name = actionName,
                SurfaceId = ctx.SurfaceId,
                SourceComponentId = c.Id,
                Context = ctx.BuildActionContext(c, actionNode),
            });
        };

        // Accessibility: an icon-only button has no text Content, so Narrator
        // would announce it as "button". Pull A2UI label/description; fall back
        // to the action name when neither is provided.
        AutomationHelpers.Apply(btn, c, ctx);
        if (string.IsNullOrEmpty(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(btn)))
            AutomationHelpers.SetName(btn, actionName);
        return btn;
    }
}

public sealed class CheckBoxRenderer : IComponentRenderer
{
    public string ComponentName => "CheckBox";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var cb = new CheckBox();

        var labelVal = ctx.GetValue(c, "label");
        void LabelUpdate() => cb.Content = ctx.ResolveString(labelVal) ?? string.Empty;
        LabelUpdate();
        ctx.WatchValue(c.Id, "label", labelVal, LabelUpdate);

        var valVal = ctx.GetValue(c, "value");
        bool inProgrammatic = false;

        void ValueUpdate()
        {
            inProgrammatic = true;
            try { cb.IsChecked = ctx.ResolveBoolean(valVal) ?? false; }
            finally { inProgrammatic = false; }
        }
        ValueUpdate();
        ctx.WatchValue(c.Id, "value", valVal, ValueUpdate);

        if (valVal?.HasPath == true)
        {
            cb.Checked += (_, _) =>
            {
                if (inProgrammatic) return;
                ctx.DataModel.Write(valVal.Path!, JsonValue.Create(true));
            };
            cb.Unchecked += (_, _) =>
            {
                if (inProgrammatic) return;
                ctx.DataModel.Write(valVal.Path!, JsonValue.Create(false));
            };
        }
        AutomationHelpers.Apply(cb, c, ctx);
        return cb;
    }
}

public sealed class TextFieldRenderer : IComponentRenderer
{
    public string ComponentName => "TextField";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var fieldType = c.Properties["textFieldType"]?.GetValue<string>() ?? "shortText";
        bool multiline = fieldType == "longText";
        bool obscured = fieldType == "obscured";

        FrameworkElement element;
        TextBox? textBox = null;
        PasswordBox? passwordBox = null;

        if (obscured)
        {
            passwordBox = new PasswordBox();
            element = passwordBox;
        }
        else
        {
            textBox = new TextBox
            {
                AcceptsReturn = multiline,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                MinHeight = multiline ? 80 : 32,
            };
            ApplyInputScope(textBox, fieldType);
            element = textBox;
        }

        var labelVal = ctx.GetValue(c, "label");
        void LabelUpdate()
        {
            var label = ctx.ResolveString(labelVal);
            if (textBox != null) textBox.Header = label;
            if (passwordBox != null) passwordBox.Header = label;
        }
        LabelUpdate();
        ctx.WatchValue(c.Id, "label", labelVal, LabelUpdate);

        var textVal = ctx.GetValue(c, "text");

        // Obscured fields are sensitive: register the bound path so dump/action-context
        // redact it (canvas.a2ui.dump is the loudest exfil channel). The value still
        // round-trips through the data model so a Submit button can read it via an
        // explicit dataBinding opt-in — but every other read path drops it.
        if (obscured && textVal?.HasPath == true)
            ctx.MarkSecretPath(textVal.Path);

        bool inProgrammatic = false;
        void TextUpdate()
        {
            inProgrammatic = true;
            try
            {
                var s = ctx.ResolveString(textVal) ?? string.Empty;
                if (textBox != null) textBox.Text = s;
                if (passwordBox != null) passwordBox.Password = s;
            }
            finally { inProgrammatic = false; }
        }
        TextUpdate();
        ctx.WatchValue(c.Id, "text", textVal, TextUpdate);

        if (textVal?.HasPath == true)
        {
            if (textBox != null)
            {
                textBox.TextChanged += (_, _) =>
                {
                    if (inProgrammatic) return;
                    ctx.DataModel.Write(textVal.Path!, JsonValue.Create(textBox.Text));
                };
            }
            if (passwordBox != null)
            {
                passwordBox.PasswordChanged += (_, _) =>
                {
                    if (inProgrammatic) return;
                    ctx.DataModel.Write(textVal.Path!, JsonValue.Create(passwordBox.Password));
                };
            }
        }

        AutomationHelpers.Apply(element, c, ctx);
        return element;
    }

    private static void ApplyInputScope(TextBox tb, string fieldType)
    {
        var name = fieldType switch
        {
            "number" => Microsoft.UI.Xaml.Input.InputScopeNameValue.Number,
            "date" => Microsoft.UI.Xaml.Input.InputScopeNameValue.DateMonthNumber,
            _ => Microsoft.UI.Xaml.Input.InputScopeNameValue.Default,
        };
        var scope = new Microsoft.UI.Xaml.Input.InputScope();
        scope.Names.Add(new Microsoft.UI.Xaml.Input.InputScopeName(name));
        tb.InputScope = scope;
    }
}

public sealed class DateTimeInputRenderer : IComponentRenderer
{
    public string ComponentName => "DateTimeInput";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var enableDate = c.Properties["enableDate"]?.GetValue<bool>() ?? true;
        var enableTime = c.Properties["enableTime"]?.GetValue<bool>() ?? false;
        var outputFormat = c.Properties["outputFormat"]?.GetValue<string>();

        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        CalendarDatePicker? datePicker = null;
        TimePicker? timePicker = null;

        if (enableDate)
        {
            datePicker = new CalendarDatePicker { PlaceholderText = OpenClawTray.Helpers.LocalizationHelper.GetString("A2UI_DateTimeSelectDate") };
            sp.Children.Add(datePicker);
        }
        if (enableTime)
        {
            timePicker = new TimePicker();
            sp.Children.Add(timePicker);
        }

        var valVal = ctx.GetValue(c, "value");
        bool inProgrammatic = false;
        void ValueUpdate()
        {
            var s = ctx.ResolveString(valVal);
            if (string.IsNullOrEmpty(s)) return;
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
            {
                inProgrammatic = true;
                try
                {
                    if (datePicker != null) datePicker.Date = dto;
                    if (timePicker != null) timePicker.Time = dto.TimeOfDay;
                }
                finally { inProgrammatic = false; }
            }
        }
        ValueUpdate();
        ctx.WatchValue(c.Id, "value", valVal, ValueUpdate);

        if (valVal?.HasPath == true)
        {
            void Push()
            {
                if (inProgrammatic) return;
                var d = datePicker?.Date ?? DateTimeOffset.Now;
                var t = timePicker?.Time ?? d.TimeOfDay;
                var combined = new DateTimeOffset(d.Year, d.Month, d.Day,
                    t.Hours, t.Minutes, t.Seconds, d.Offset);
                string formatted;
                try
                {
                    formatted = !string.IsNullOrEmpty(outputFormat)
                        ? combined.ToString(outputFormat, CultureInfo.InvariantCulture)
                        : combined.ToString("o");
                }
                catch (FormatException)
                {
                    // Bogus agent-supplied format. Fall back to ISO-8601 so the
                    // surface keeps working — without this, every keystroke
                    // throws and the tab re-renders as the unknown placeholder.
                    formatted = combined.ToString("o");
                }
                ctx.DataModel.Write(valVal.Path!, JsonValue.Create(formatted));
            }
            if (datePicker != null) datePicker.DateChanged += (_, _) => Push();
            if (timePicker != null) timePicker.TimeChanged += (_, _) => Push();
        }
        AutomationHelpers.Apply(sp, c, ctx);
        return sp;
    }
}

public sealed class MultipleChoiceRenderer : IComponentRenderer
{
    public string ComponentName => "MultipleChoice";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var max = c.Properties["maxAllowedSelections"] is JsonValue jv && jv.TryGetValue<int>(out var m) ? m : int.MaxValue;
        var single = max == 1;

        var options = ParseOptions(c, ctx);

        if (single)
        {
            var combo = new ComboBox { PlaceholderText = OpenClawTray.Helpers.LocalizationHelper.GetString("A2UI_MultipleChoiceSelect") };
            foreach (var (label, value) in options)
                combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });

            var selVal = ctx.GetValue(c, "selections");
            bool inProgrammatic = false;
            void Update()
            {
                inProgrammatic = true;
                try
                {
                    var current = ResolveSingle(ctx, selVal);
                    foreach (var item in combo.Items)
                    {
                        if (item is ComboBoxItem cbi && (cbi.Tag as string) == current)
                        {
                            combo.SelectedItem = cbi;
                            break;
                        }
                    }
                }
                finally { inProgrammatic = false; }
            }
            Update();
            ctx.WatchValue(c.Id, "selections", selVal, Update);

            if (selVal?.HasPath == true)
            {
                combo.SelectionChanged += (_, _) =>
                {
                    if (inProgrammatic) return;
                    var tag = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
                    // Single-mode writes a scalar string (or null), not a one-element array.
                    // Other components binding to the same path expect a string in single
                    // mode; the previous JsonArray round-tripped as a stringified "[\"x\"]".
                    // ResolveSingle still tolerates either shape on read for back-compat.
                    JsonNode? value = tag != null ? JsonValue.Create(tag) : null;
                    ctx.DataModel.Write(selVal.Path!, value);
                };
            }
            AutomationHelpers.Apply(combo, c, ctx);
            return combo;
        }
        else
        {
            var list = new ListView { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 240 };
            foreach (var (label, value) in options)
                list.Items.Add(new ListViewItem { Content = label, Tag = value });

            var selVal = ctx.GetValue(c, "selections");
            bool inProgrammatic = false;
            void Update()
            {
                inProgrammatic = true;
                try
                {
                    var current = new HashSet<string>(ResolveMulti(ctx, selVal), StringComparer.Ordinal);
                    foreach (var item in list.Items)
                    {
                        if (item is ListViewItem lvi && lvi.Tag is string tag && current.Contains(tag))
                        {
                            if (!list.SelectedItems.Contains(lvi))
                                list.SelectedItems.Add(lvi);
                        }
                        else if (item is ListViewItem lvi2 && list.SelectedItems.Contains(lvi2))
                        {
                            list.SelectedItems.Remove(lvi2);
                        }
                    }
                }
                finally { inProgrammatic = false; }
            }
            Update();
            ctx.WatchValue(c.Id, "selections", selVal, Update);

            if (selVal?.HasPath == true)
            {
                list.SelectionChanged += (_, _) =>
                {
                    if (inProgrammatic) return;
                    var arr = new JsonArray();
                    foreach (var sel in list.SelectedItems)
                        if (sel is ListViewItem lvi && lvi.Tag is string tag) arr.Add(JsonValue.Create(tag));
                    ctx.DataModel.Write(selVal.Path!, arr);
                };
            }
            AutomationHelpers.Apply(list, c, ctx);
            return list;
        }
    }

    private static List<(string label, string value)> ParseOptions(A2UIComponentDef c, RenderContext ctx)
    {
        var result = new List<(string, string)>();
        if (c.Properties["options"] is not JsonArray arr) return result;
        foreach (var item in arr)
        {
            if (item is not JsonObject o) continue;
            var labelVal = A2UIValue.From(o["label"]);
            var label = ctx.ResolveString(labelVal) ?? "";
            var value = o["value"]?.GetValue<string>() ?? label;
            result.Add((label, value));
        }
        return result;
    }

    private static string? ResolveSingle(RenderContext ctx, A2UIValue? value)
    {
        if (value == null) return null;
        if (value.LiteralArray is { Count: > 0 } arr) return arr[0];
        if (value.HasPath)
        {
            var node = ctx.DataModel.Read(value.Path!);
            if (node is JsonArray ja && ja.Count > 0 && ja[0] is JsonValue jv && jv.TryGetValue<string>(out var s))
                return s;
            if (node is JsonValue v && v.TryGetValue<string>(out var s2)) return s2;
        }
        return null;
    }

    private static IEnumerable<string> ResolveMulti(RenderContext ctx, A2UIValue? value)
    {
        if (value == null) yield break;
        if (value.LiteralArray is { } arr)
        {
            foreach (var s in arr) yield return s;
            yield break;
        }
        if (value.HasPath)
        {
            var node = ctx.DataModel.Read(value.Path!);
            if (node is JsonArray ja)
            {
                foreach (var item in ja)
                    if (item is JsonValue jv && jv.TryGetValue<string>(out var s)) yield return s;
            }
        }
    }
}

public sealed class SliderRenderer : IComponentRenderer
{
    public string ComponentName => "Slider";

    public FrameworkElement Render(A2UIComponentDef c, RenderContext ctx)
    {
        var slider = new Slider
        {
            Minimum = c.Properties["minValue"] is JsonValue jvmin && jvmin.TryGetValue<double>(out var mn) ? mn : 0,
            Maximum = c.Properties["maxValue"] is JsonValue jvmax && jvmax.TryGetValue<double>(out var mx) ? mx : 100,
        };
        var valVal = ctx.GetValue(c, "value");
        bool inProgrammatic = false;
        void Update()
        {
            inProgrammatic = true;
            try
            {
                var n = ctx.ResolveNumber(valVal);
                if (n.HasValue) slider.Value = n.Value;
            }
            finally { inProgrammatic = false; }
        }
        Update();
        ctx.WatchValue(c.Id, "value", valVal, Update);

        if (valVal?.HasPath == true)
        {
            slider.ValueChanged += (_, e) =>
            {
                if (inProgrammatic) return;
                ctx.DataModel.Write(valVal.Path!, JsonValue.Create(e.NewValue));
            };
        }

        // Wire step / stepSize → StepFrequency. Default 1, matching WinUI.
        var step = c.Properties["step"] is JsonValue jvs && jvs.TryGetValue<double>(out var s1) ? s1
                 : c.Properties["stepSize"] is JsonValue jvs2 && jvs2.TryGetValue<double>(out var s2) ? s2
                 : 1.0;
        if (step > 0) slider.StepFrequency = step;

        AutomationHelpers.Apply(slider, c, ctx);
        return slider;
    }
}
