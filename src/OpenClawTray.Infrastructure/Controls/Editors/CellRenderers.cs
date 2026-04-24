using System.Globalization;
using OpenClawTray.Infrastructure.Core;
using static OpenClawTray.Infrastructure.Factories;
using WinUIColor = global::Windows.UI.Color;

namespace OpenClawTray.Infrastructure.Controls;

/// <summary>
/// Catalog of read-mode cell renderers for DataGrid (and anywhere else
/// that needs a "display this value" factory). Each method returns a
/// <c>Func&lt;object, Element&gt;</c> matching <see cref="Data.FieldDescriptor.CellRenderer"/>.
///
/// Paired with <see cref="Editors"/> — the convention is that a specialized
/// column uses the matching pair (e.g. <c>CellRenderers.Date</c> + <c>Editors.DateTime</c>).
/// </summary>
public static class CellRenderers
{
    // ══════════════════════════════════════════════════════════════
    //  Text — default fallback, honors an optional .NET format spec.
    // ══════════════════════════════════════════════════════════════

    public static Func<object, Element> Text(string? format = null)
        => value => TextBlock(FormatValue(value, format));

    // ══════════════════════════════════════════════════════════════
    //  Bool
    // ══════════════════════════════════════════════════════════════

    public static Func<object, Element> CheckMark()
        => value =>
        {
            var on = (bool)(value ?? false);
            return TextBlock(on ? "✓" : string.Empty)
                .Foreground(on ? "#2e7d32" : "#9e9e9e")
                .FontSize(16);
        };

    public static Func<object, Element> ToggleIndicator()
        => value =>
        {
            var on = (bool)(value ?? false);
            // Read-mode stylized toggle: a small pill. HAlign.Left keeps the
            // pill at its natural content width — without this, Border stretches
            // to the cell's full width and visually bleeds into the next column.
            return Border(TextBlock(on ? "ON" : "OFF")
                    .Foreground(on ? "#FFFFFF" : "#555555")
                    .FontSize(10))
                .Background(on ? "#2e7d32" : "#e0e0e0")
                .CornerRadius(8)
                .Padding(6, 2)
                .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Left)
                .VAlign(Microsoft.UI.Xaml.VerticalAlignment.Center);
        };

    // ══════════════════════════════════════════════════════════════
    //  Numeric
    // ══════════════════════════════════════════════════════════════

    public static Func<object, Element> Number(string? format = null)
        => value => TextBlock(FormatValue(value, format))
            .TextAlignment(Microsoft.UI.Xaml.TextAlignment.Right)
            // Stretch the text block so TextAlignment.Right actually takes effect —
            // otherwise the TextBlock sizes to its content and sits at the left.
            .HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Stretch);

    // ══════════════════════════════════════════════════════════════
    //  Date / Time
    // ══════════════════════════════════════════════════════════════

    public static Func<object, Element> Date(string format = "d")
        => value => TextBlock(FormatValue(value, format));

    public static Func<object, Element> Time(string format = "t")
        => value => TextBlock(FormatValue(value, format));

    // ══════════════════════════════════════════════════════════════
    //  Hyperlink
    // ══════════════════════════════════════════════════════════════

    public static Func<object, Element> Hyperlink(string? displayTextFormat = null)
        => value =>
        {
            if (value is global::System.Uri uri)
                return HyperlinkButton(
                    displayTextFormat is null ? uri.ToString() : string.Format(displayTextFormat, uri),
                    navigateUri: uri);

            var text = value?.ToString() ?? string.Empty;
            if (global::System.Uri.TryCreate(text, UriKind.Absolute, out var parsed))
                return HyperlinkButton(text, navigateUri: parsed);

            return TextBlock(text);
        };

    // ══════════════════════════════════════════════════════════════
    //  Color — a small color swatch with the hex as a label next to it.
    // ══════════════════════════════════════════════════════════════

    public static Func<object, Element> ColorSwatch()
        => value =>
        {
            var color = (WinUIColor)(value ?? global::Microsoft.UI.Colors.Transparent);
            return HStack(6,
                Border(Empty())
                    .Background($"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}")
                    .Width(16).Height(16)
                    .CornerRadius(3)
                    .WithBorder("#80000000", 1),
                TextBlock(color.ToHexString()).FontSize(11).Foreground("#555555")
            );
        };

    // ══════════════════════════════════════════════════════════════
    //  Combo / Enum — just the string form of the value.
    // ══════════════════════════════════════════════════════════════

    public static Func<object, Element> Enum()
        => value => TextBlock(value?.ToString() ?? string.Empty);

    // ══════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════

    private static string FormatValue(object? value, string? format)
    {
        if (value is null) return string.Empty;
        if (format is not null && value is IFormattable f)
            return f.ToString(format, CultureInfo.CurrentCulture);
        return value.ToString() ?? string.Empty;
    }
}
