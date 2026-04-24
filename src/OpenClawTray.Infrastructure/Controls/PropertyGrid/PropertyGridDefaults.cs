using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Infrastructure.Data;
using OpenClawTray.Infrastructure.Layout;
using Microsoft.UI.Xaml;
using static OpenClawTray.Infrastructure.Factories;

namespace OpenClawTray.Infrastructure.Controls;

/// <summary>
/// Default rendering templates for PropertyGrid — compact Windows 11 layout.
/// Uses FlexRow per property row: fixed 160px label, grow-1 editor.
/// </summary>
public static class PropertyGridDefaults
{
    public static Element PropertyLabelTemplate(
        FieldDescriptor descriptor, int indentLevel)
    =>
        TextBlock(descriptor.DisplayName ?? descriptor.Name)
            .ToolTip(descriptor.Description ?? "")
            .VAlign(VerticalAlignment.Center)
            .Margin(indentLevel * 4, 0, 0, 0)
            .AutomationName($"Label: {descriptor.DisplayName ?? descriptor.Name}");

    public static Element PropertyRowTemplate(
        FieldDescriptor descriptor, Element label, Element editor, int indentLevel)
    =>
        FlexRow(
            label.Flex(basis: 160, shrink: 0).Padding(indentLevel * 16, 0, 0, 0),
            editor.Flex(grow: 1)
                .AutomationName($"Editor: {descriptor.DisplayName ?? descriptor.Name}")
        ) with { AlignItems = FlexAlign.Center, ColumnGap = 8 };

    public static Element ArrayToolbarTemplate(
        string propertyName, int count, Func<Task>? onAdd)
    =>
        FlexRow(
            TextBlock($"{propertyName} ({count})").SemiBold(),
            onAdd is not null
                ? Button("+", async () => { try { await onAdd(); } catch (Exception ex) { global::System.Diagnostics.Debug.WriteLine($"PropertyGrid Add failed: {ex}"); } })
                    .Width(28).Height(28)
                    .AutomationName($"Add {propertyName} item")
                : null
        ) with { AlignItems = FlexAlign.Center, ColumnGap = 8 };

    public static Element ArrayItemTemplate(
        int index, string summary, bool isExpanded, Action<bool> onExpandedChanged,
        Action? onMoveUp, Action? onMoveDown, Action? onRemove)
    =>
        FlexRow(
            Button(isExpanded ? "\u25BC" : "\u25B6", () => onExpandedChanged(!isExpanded))
                .Width(24).Height(24),
            TextBlock($"[{index}]").Flex(basis: 40, shrink: 0),
            TextBlock(summary).Flex(grow: 1),
            onMoveUp is not null
                ? Button("\u25B2", onMoveUp).Width(28).Height(28)
                    .AutomationName($"Move item {index} up")
                : null,
            onMoveDown is not null
                ? Button("\u25BC", onMoveDown).Width(28).Height(28)
                    .AutomationName($"Move item {index} down")
                : null,
            onRemove is not null
                ? Button("\u2715", onRemove).Width(28).Height(28)
                    .AutomationName($"Remove item {index}")
                : null
        ) with { AlignItems = FlexAlign.Center, ColumnGap = 4 };
}
