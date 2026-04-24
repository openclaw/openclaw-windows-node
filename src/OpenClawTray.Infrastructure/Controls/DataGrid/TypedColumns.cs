using System.Diagnostics.CodeAnalysis;
using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Infrastructure.Data;
using WinUIColor = global::Windows.UI.Color;

namespace OpenClawTray.Infrastructure.Controls;

/// <summary>
/// Typed column factories that ship specialized editors + cell renderers out of
/// the box. Thin wrappers over <see cref="ColumnDsl.Column{T}"/> that pre-wire
/// the matching pair from <see cref="Editors"/> and <see cref="CellRenderers"/>.
///
/// Use when the column type is known at the call site and you want HitTable-style
/// "batteries included" ergonomics. For discovery-driven columns, use
/// <see cref="ColumnDsl.AutoColumns{T}"/> with a <see cref="TypeRegistry"/>
/// populated via <see cref="TypeRegistry.WithDefaultEditors"/>.
/// </summary>
public static class TypedColumns
{
    // ══════════════════════════════════════════════════════════════
    //  Number — one factory covering int/long/short/byte/float/double/decimal.
    //  The accessor's declared property type drives value-conversion.
    // ══════════════════════════════════════════════════════════════

    public static ColumnBuilder<T> NumberColumn<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string name,
        Func<T, object?> accessor,
        double? min = null,
        double? max = null,
        double smallChange = 1,
        string? displayName = null,
        string? format = null,
        double? width = null,
        PinPosition pin = PinPosition.None)
    {
        var builder = ColumnDsl.Column(name, accessor,
            editable: true, displayName: displayName, format: format, width: width, pin: pin);
        var fieldType = ((FieldDescriptor)builder).FieldType;
        return builder
            .WithEditor(Editors.Number(fieldType, min, max, smallChange))
            .CellRenderer(CellRenderers.Number(format));
    }

    // ══════════════════════════════════════════════════════════════
    //  Bool
    // ══════════════════════════════════════════════════════════════

    public static ColumnBuilder<T> CheckBoxColumn<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string name,
        Func<T, object?> accessor,
        string? displayName = null,
        double? width = null,
        PinPosition pin = PinPosition.None)
        => ColumnDsl.Column(name, accessor,
                editable: true, displayName: displayName, width: width, pin: pin)
            .WithEditor(Editors.CheckBox())
            .CellRenderer(CellRenderers.CheckMark());

    public static ColumnBuilder<T> ToggleSwitchColumn<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string name,
        Func<T, object?> accessor,
        string? onContent = null,
        string? offContent = null,
        string? displayName = null,
        double? width = null,
        PinPosition pin = PinPosition.None)
        => ColumnDsl.Column(name, accessor,
                editable: true, displayName: displayName, width: width, pin: pin)
            .WithEditor(Editors.Toggle(onContent, offContent))
            .CellRenderer(CellRenderers.ToggleIndicator());

    // ══════════════════════════════════════════════════════════════
    //  Date / Time
    // ══════════════════════════════════════════════════════════════

    public static ColumnBuilder<T> DateColumn<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string name,
        Func<T, object?> accessor,
        string format = "d",
        string? displayName = null,
        double? width = null,
        PinPosition pin = PinPosition.None)
    {
        var builder = ColumnDsl.Column(name, accessor,
            editable: true, displayName: displayName, format: format, width: width, pin: pin);
        var fieldType = ((FieldDescriptor)builder).FieldType;
        var editor = fieldType == typeof(global::System.DateTimeOffset)
            ? Editors.DateOffset()
            : fieldType == typeof(global::System.DateOnly)
                ? Editors.DateOnly()
                : Editors.Date();
        return builder
            .WithEditor(editor)
            .CellRenderer(CellRenderers.Date(format));
    }

    public static ColumnBuilder<T> TimeColumn<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string name,
        Func<T, object?> accessor,
        string format = "t",
        string? displayName = null,
        double? width = null,
        PinPosition pin = PinPosition.None)
    {
        var builder = ColumnDsl.Column(name, accessor,
            editable: true, displayName: displayName, format: format, width: width, pin: pin);
        var fieldType = ((FieldDescriptor)builder).FieldType;
        var editor = fieldType == typeof(global::System.TimeOnly)
            ? Editors.TimeOnlyEditor()
            : Editors.TimeSpanEditor();
        return builder
            .WithEditor(editor)
            .CellRenderer(CellRenderers.Time(format));
    }

    // ══════════════════════════════════════════════════════════════
    //  Combo / Enum
    // ══════════════════════════════════════════════════════════════

    public static ColumnBuilder<T> ComboBoxColumn<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T, TValue>(
        string name,
        Func<T, object?> accessor,
        IReadOnlyList<TValue> choices,
        string? displayName = null,
        double? width = null,
        PinPosition pin = PinPosition.None)
        => ColumnDsl.Column(name, accessor,
                editable: true, displayName: displayName, width: width, pin: pin)
            .WithEditor(Editors.Combo(choices))
            .CellRenderer(CellRenderers.Enum());

    // ══════════════════════════════════════════════════════════════
    //  Hyperlink — underlying type may be Uri or string.
    // ══════════════════════════════════════════════════════════════

    public static ColumnBuilder<T> HyperlinkColumn<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string name,
        Func<T, object?> accessor,
        string? displayTextFormat = null,
        string? displayName = null,
        double? width = null,
        PinPosition pin = PinPosition.None)
    {
        var builder = ColumnDsl.Column(name, accessor,
            editable: true, displayName: displayName, width: width, pin: pin);
        var fieldType = ((FieldDescriptor)builder).FieldType;
        var editor = fieldType == typeof(global::System.Uri)
            ? Editors.Uri()
            : Editors.Text(placeholder: "https://...");
        return builder
            .WithEditor(editor)
            .CellRenderer(CellRenderers.Hyperlink(displayTextFormat));
    }

    // ══════════════════════════════════════════════════════════════
    //  Color
    // ══════════════════════════════════════════════════════════════

    public static ColumnBuilder<T> ColorColumn<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        string name,
        Func<T, object?> accessor,
        string? displayName = null,
        double? width = null,
        PinPosition pin = PinPosition.None)
        // Editors.ColorCompact (not .Color) — grid cells are ~40px tall, the
        // full inline ColorPicker would overflow ~300px and obscure adjacent
        // rows. Compact = swatch + hex text box; stays inside the row.
        => ColumnDsl.Column(name, accessor,
                editable: true, displayName: displayName, width: width, pin: pin)
            .WithEditor(Editors.ColorCompact())
            .CellRenderer(CellRenderers.ColorSwatch());
}
