using System.Globalization;
using OpenClawTray.Infrastructure.Core;
using static OpenClawTray.Infrastructure.Factories;
using WinUIColor = global::Windows.UI.Color;

namespace OpenClawTray.Infrastructure.Controls;

/// <summary>
/// Catalog of inline editor factories for DataGrid and PropertyGrid. Each method
/// returns a <c>Func&lt;object, Action&lt;object&gt;, Element&gt;</c> matching the
/// <see cref="Data.FieldDescriptor.Editor"/> contract: takes the current cell
/// value and an onChange callback, returns the editor Element.
///
/// Use these via:
/// <list type="bullet">
///   <item><c>FieldDescriptor</c>'s <c>Editor</c> slot directly, or</item>
///   <item>A typed column factory like <c>NumberColumn&lt;T&gt;</c> (thin wrappers over this catalog), or</item>
///   <item><c>TypeRegistry</c> registration for metadata-driven discovery.</item>
/// </list>
/// </summary>
public static class Editors
{
    // ══════════════════════════════════════════════════════════════
    //  Text
    // ══════════════════════════════════════════════════════════════

    public static Func<object, Action<object>, Element> Text(
        string? placeholder = null,
        int? maxLength = null)
        => (value, onChange) =>
        {
            var el = TextField((string?)value ?? string.Empty, s => onChange(s), placeholder: placeholder);
            return maxLength is { } max
                ? el.Set(tb => tb.MaxLength = max)
                : el;
        };

    // ══════════════════════════════════════════════════════════════
    //  Bool
    // ══════════════════════════════════════════════════════════════

    public static Func<object, Action<object>, Element> CheckBox()
        => (value, onChange) =>
            Factories.CheckBox((bool)(value ?? false), b => onChange(b));

    public static Func<object, Action<object>, Element> Toggle(
        string? onContent = null,
        string? offContent = null)
        => (value, onChange) =>
            ToggleSwitch(
                (bool)(value ?? false),
                b => onChange(b),
                onContent: onContent,
                offContent: offContent);

    // ══════════════════════════════════════════════════════════════
    //  Numeric
    //  NumberBox operates in double; we bounce to/from the declared
    //  CLR type so the onChange callback hands the property setter a
    //  value it can cast without InvalidCastException.
    // ══════════════════════════════════════════════════════════════

    public static Func<object, Action<object>, Element> Number(
        Type numberType,
        double? min = null,
        double? max = null,
        double smallChange = 1)
        => (value, onChange) =>
        {
            var current = ToDouble(value);
            var el = NumberBox(current, v => onChange(FromDouble(numberType, v)));
            return el.Set(nb =>
            {
                if (min is { } mn) nb.Minimum = mn;
                if (max is { } mx) nb.Maximum = mx;
                nb.SmallChange = smallChange;
            });
        };

    public static Func<object, Action<object>, Element> Int(int? min = null, int? max = null)
        => Number(typeof(int), min, max);

    public static Func<object, Action<object>, Element> Long(long? min = null, long? max = null)
        => Number(typeof(long), min, max);

    public static Func<object, Action<object>, Element> Double(
        double? min = null, double? max = null)
        => Number(typeof(double), min, max);

    public static Func<object, Action<object>, Element> Decimal(
        decimal? min = null, decimal? max = null)
        => Number(typeof(decimal), (double?)min, (double?)max);

    public static Func<object, Action<object>, Element> Float(float? min = null, float? max = null)
        => Number(typeof(float), min, max);

    // ══════════════════════════════════════════════════════════════
    //  Date / Time
    // ══════════════════════════════════════════════════════════════

    public static Func<object, Action<object>, Element> Date()
        => (value, onChange) =>
        {
            global::System.DateTimeOffset dto;
            if (value is global::System.DateTime dt)
            {
                var kind = dt.Kind == DateTimeKind.Unspecified
                    ? global::System.DateTime.SpecifyKind(dt, DateTimeKind.Local)
                    : dt;
                dto = new global::System.DateTimeOffset(kind);
            }
            else if (value is global::System.DateTimeOffset off)
            {
                dto = off;
            }
            else
            {
                dto = global::System.DateTimeOffset.Now;
            }
            return DatePicker(dto, d => onChange(d.DateTime));
        };

    public static Func<object, Action<object>, Element> DateOffset()
        => (value, onChange) =>
        {
            var dto = (global::System.DateTimeOffset)(value ?? global::System.DateTimeOffset.Now);
            return DatePicker(dto, d => onChange(d));
        };

    public static Func<object, Action<object>, Element> DateOnly()
        => (value, onChange) =>
        {
            var d = (global::System.DateOnly)(value ?? global::System.DateOnly.FromDateTime(global::System.DateTime.Today));
            var dto = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return DatePicker(dto, picked =>
                onChange(global::System.DateOnly.FromDateTime(picked.DateTime)));
        };

    public static Func<object, Action<object>, Element> TimeOfDay()
        => (value, onChange) =>
        {
            var ts = value switch
            {
                global::System.TimeSpan t => t,
                global::System.TimeOnly to => to.ToTimeSpan(),
                null => global::System.TimeSpan.Zero,
                _ => global::System.TimeSpan.Zero,
            };
            return TimePicker(ts, t => onChange(value is TimeOnly ? TimeOnly.FromTimeSpan(t) : (object)t));
        };

    public static Func<object, Action<object>, Element> TimeSpanEditor()
        => (value, onChange) =>
        {
            var ts = (global::System.TimeSpan)(value ?? global::System.TimeSpan.Zero);
            return TimePicker(ts, t => onChange(t));
        };

    public static Func<object, Action<object>, Element> TimeOnlyEditor()
        => (value, onChange) =>
        {
            var to = (global::System.TimeOnly)(value ?? global::System.TimeOnly.MinValue);
            return TimePicker(to.ToTimeSpan(), t => onChange(global::System.TimeOnly.FromTimeSpan(t)));
        };

    // ══════════════════════════════════════════════════════════════
    //  Uri / Hyperlink (edit mode = TextBox over the URL string).
    //  The display renderer is where the hyperlink rendering lives.
    // ══════════════════════════════════════════════════════════════

    public static Func<object, Action<object>, Element> Uri()
        => (value, onChange) =>
        {
            var text = value switch
            {
                global::System.Uri u => u.ToString(),
                null => string.Empty,
                _ => value.ToString() ?? string.Empty,
            };
            return TextField(text, s =>
            {
                // Only commit valid Uris; leave the field alone for partial input.
                if (global::System.Uri.TryCreate(s, UriKind.RelativeOrAbsolute, out var uri))
                    onChange(uri);
            }, placeholder: "https://...");
        };

    // ══════════════════════════════════════════════════════════════
    //  Choice / Combo
    // ══════════════════════════════════════════════════════════════

    public static Func<object, Action<object>, Element> Combo<TValue>(IReadOnlyList<TValue> choices)
        => (value, onChange) =>
        {
            var names = choices.Select(c => c?.ToString() ?? string.Empty).ToArray();
            var idx = 0;
            for (int i = 0; i < choices.Count; i++)
            {
                if (Equals(choices[i], value)) { idx = i; break; }
            }
            return ComboBox(names, idx, i => onChange(choices[i]!));
        };

    public static Func<object, Action<object>, Element> EnumCombo(Type enumType)
    {
        var names = Enum.GetNames(enumType);
        return (value, onChange) =>
        {
            var current = value?.ToString();
            var idx = Array.IndexOf(names, current);
            if (idx < 0) idx = 0;
            return ComboBox(names, idx, i => onChange(Enum.Parse(enumType, names[i])));
        };
    }

    // ══════════════════════════════════════════════════════════════
    //  Color — two variants.
    //    • Compact (DataGrid cell): swatch + hex text box. Typing a
    //      valid #RRGGBB / #AARRGGBB hex commits the color. No flyout —
    //      avoids a WinAppSDK 2.0-preview ColorPicker-in-Flyout crash.
    //    • Full (PropertyGrid / expanded): inline WinUI ColorPicker.
    // ══════════════════════════════════════════════════════════════

    public static Func<object, Action<object>, Element> ColorCompact()
        => (value, onChange) =>
        {
            var color = (WinUIColor)(value ?? global::Microsoft.UI.Colors.Transparent);
            var hex = color.ToHexString();
            return HStack(6,
                Border(Empty())
                    .Background($"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}")
                    .Width(20).Height(20)
                    .CornerRadius(3)
                    .WithBorder("#80000000", 1),
                TextField(hex, s =>
                {
                    if (TryParseHexColor(s, out var parsed))
                        onChange(parsed);
                }).Width(110)
            ).VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center);
        };

    public static Func<object, Action<object>, Element> Color()
        => (value, onChange) =>
        {
            var color = (WinUIColor)(value ?? global::Microsoft.UI.Colors.Transparent);
            return ColorPicker(color, c => onChange(c));
        };

    private static bool TryParseHexColor(string text, out WinUIColor color)
    {
        color = global::Microsoft.UI.Colors.Transparent;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('#');
        byte a = 0xFF, r, g, b;
        try
        {
            if (s.Length == 6)
            {
                r = Convert.ToByte(s.Substring(0, 2), 16);
                g = Convert.ToByte(s.Substring(2, 2), 16);
                b = Convert.ToByte(s.Substring(4, 2), 16);
            }
            else if (s.Length == 8)
            {
                a = Convert.ToByte(s.Substring(0, 2), 16);
                r = Convert.ToByte(s.Substring(2, 2), 16);
                g = Convert.ToByte(s.Substring(4, 2), 16);
                b = Convert.ToByte(s.Substring(6, 2), 16);
            }
            else return false;
        }
        catch { return false; }
        color = global::Windows.UI.Color.FromArgb(a, r, g, b);
        return true;
    }

    // ══════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════

    private static double ToDouble(object? value) => value switch
    {
        null => 0d,
        double d => d,
        float f => f,
        decimal m => (double)m,
        int i => i,
        long l => l,
        short s => s,
        byte b => b,
        sbyte sb => sb,
        ushort us => us,
        uint ui => ui,
        ulong ul => ul,
        _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
    };

    private static object FromDouble(Type targetType, double value)
    {
        if (targetType == typeof(double)) return value;
        if (targetType == typeof(float)) return (float)value;
        if (targetType == typeof(decimal)) return (decimal)value;
        if (targetType == typeof(int)) return (int)value;
        if (targetType == typeof(long)) return (long)value;
        if (targetType == typeof(short)) return (short)value;
        if (targetType == typeof(byte)) return (byte)value;
        if (targetType == typeof(sbyte)) return (sbyte)value;
        if (targetType == typeof(ushort)) return (ushort)value;
        if (targetType == typeof(uint)) return (uint)value;
        if (targetType == typeof(ulong)) return (ulong)value;
        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    internal static string ToHexString(this WinUIColor c)
        => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
