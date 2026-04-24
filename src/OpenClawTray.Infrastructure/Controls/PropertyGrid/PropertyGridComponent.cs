using System.ComponentModel;
using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Infrastructure.Data;
using OpenClawTray.Infrastructure.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using static OpenClawTray.Infrastructure.Factories;

namespace OpenClawTray.Infrastructure.Controls;

/// <summary>
/// The core PropertyGrid component. Uses FlexColumn layout with FlexRow per
/// property row. Category headers and property expand/collapse use a single
/// shared state dictionary.
/// </summary>
public class PropertyGridComponent : Component<PropertyGridElement>
{
    static ButtonElement BlankButton(Element content, Action onClick)
        => Button(content, onClick).Set(b =>
        {
            b.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.Colors.Transparent);
            b.BorderThickness = new Thickness(0);
            b.Padding = new Thickness(0);
            b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        }).HAlign(HorizontalAlignment.Stretch);

    public override Element Render()
    {
        var el = Props;
        var target = el.Target;
        var registry = el.Registry;

        // INPC observation — always consume the same hook slots
        var (_, forceRender) = UseReducer(false);
        var trackerRef = UseRef<ObservableTreeTracker?>(null);
        var inpc = target as INotifyPropertyChanged;
        UseEffect(() =>
        {
            if (inpc is null) return () => { };
            var tracker = new ObservableTreeTracker(() => forceRender(v => !v));
            trackerRef.Current = tracker;
            tracker.SyncSubscriptions(inpc);
            return () => tracker.Dispose();
        }, target);

        var meta = registry.Resolve(target.GetType());
        if (meta.Decompose is null)
            return TextBlock("No properties to display");

        var allDescriptors = meta.Decompose(target);

        var descriptors = el.Filter is not null
            ? allDescriptors.Where(el.Filter).ToList()
            : allDescriptors.ToList();

        var (searchText, setSearchText) = UseState("");
        if (el.ShowSearch && !string.IsNullOrEmpty(searchText))
        {
            var query = searchText;
            descriptors = descriptors
                .Where(d => (d.DisplayName ?? d.Name)
                    .Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var groups = descriptors
            .GroupBy(d => d.Category ?? "General")
            .OrderBy(g => g.Key == "General" ? "" : g.Key)
            .ToList();

        var rowTemplate = el.PropertyRowTemplate ?? PropertyGridDefaults.PropertyRowTemplate;
        var labelTemplate = el.PropertyLabelTemplate ?? PropertyGridDefaults.PropertyLabelTemplate;

        // Single expand state for both categories and composite properties
        var (expandState, setExpandState) = UseReducer(
            new Dictionary<string, bool>());

        var editChain = new EditChain(target, meta, el.OnRootChanged);

        // Build a flat list of rows using FlexColumn
        var rows = new List<Element?>();

        foreach (var group in groups)
        {
            var categoryName = group.Key;
            var catKey = $"cat:{categoryName}";
            var isExpanded = !expandState.TryGetValue(catKey, out var catExpanded) || catExpanded;

            // Category header — entire row is clickable
            var catName = categoryName;
            var catExp = isExpanded;
            rows.Add(
                BlankButton(
                    FlexRow(
                        TextBlock(categoryName).SemiBold().Flex(shrink: 0),
                        TextBlock(" ").Flex(grow: 1),
                        TextBlock(catExp ? "\u25BC" : "\u25B6").Flex(shrink: 0).Opacity(0.6)
                    ),
                    () => setExpandState(dict =>
                    {
                        var copy = new Dictionary<string, bool>(dict);
                        copy[$"cat:{catName}"] = !catExp;
                        return copy;
                    }))
                .HAlign(HorizontalAlignment.Stretch)
                .Height(36)
            );

            if (!isExpanded) continue;

            foreach (var desc in group)
            {
                RenderProperty(rows, desc, target, registry, el, rowTemplate, labelTemplate,
                    expandState, setExpandState, editChain, 0);
            }
        }

        var content = FlexColumn(rows.ToArray()) with { RowGap = 2 };

        if (el.ShowSearch)
        {
            return FlexColumn(
                TextField(searchText, setSearchText, placeholder: "Filter properties...")
                    .Width(300),
                content.Flex(grow: 1)
            ) with { RowGap = 8 };
        }

        return content;
    }

    private static void RenderProperty(
        List<Element?> rows,
        FieldDescriptor descriptor,
        object owner,
        TypeRegistry registry,
        PropertyGridElement el,
        PropertyRowTemplate rowTemplate,
        PropertyLabelTemplate labelTemplate,
        Dictionary<string, bool> expandState,
        Action<Func<Dictionary<string, bool>, Dictionary<string, bool>>> setExpandState,
        EditChain editChain,
        int indentLevel)
    {
        var propertyType = descriptor.FieldType;
        var meta = registry.Resolve(propertyType);
        var hasDecompose = meta.Decompose is not null && !IsPrimitiveOrEnum(propertyType);
        var hasEditor = meta.Editor is not null;

        var label = labelTemplate(descriptor, indentLevel);

        Element editor;
        if (hasEditor)
            editor = RenderEditor(descriptor, owner, meta, editChain);
        else
        {
            var value = descriptor.GetValue(owner);
            editor = TextBlock(value?.ToString() ?? "(null)");
        }

        // FullEditor "..." expand affordance — show a small button that opens
        // the FullEditor in a flyout when clicked. Appears automatically when
        // FullEditor is registered for this type.
        if (meta.FullEditor is not null && !descriptor.IsReadOnly)
        {
            var currentValue = descriptor.GetValue(owner);
            Action<object> onChange = newValue =>
            {
                if (descriptor.SetValue is not null)
                {
                    var newOwner = descriptor.SetValue(owner, newValue);
                    if (!ReferenceEquals(newOwner, owner))
                        editChain.PropagateNewOwner(descriptor.Name, newOwner);
                }
                else
                {
                    editChain.PropagateImmutableEdit(descriptor.Name, newValue);
                }
            };

            var fullEditorContent = meta.FullEditor(currentValue!, onChange);
            editor = FlexRow(
                editor.Flex(grow: 1),
                Button("\u2026", () => { })
                    .Width(28).Height(28)
                    .ToolTip("Expand editor")
                    .AutomationName($"Expand {descriptor.DisplayName ?? descriptor.Name}")
                    .WithFlyout(ContentFlyout(
                        fullEditorContent,
                        placement: Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom))
            ) with { AlignItems = FlexAlign.Center, ColumnGap = 4 };
        }

        // For decomposable types, add expand toggle and sub-properties
        if (hasDecompose)
        {
            var expandKey = $"prop:{editChain.BuildPath(descriptor.Name)}";
            var isExpanded = expandState.TryGetValue(expandKey, out var propExpanded) && propExpanded;
            var propExp = isExpanded;
            var propKey = expandKey;

            // Add toggle button next to the editor value
            editor = FlexRow(
                editor.Flex(grow: 1),
                BlankButton(propExp ? "\u25BC" : "\u25B6",
                    () => setExpandState(dict =>
                    {
                        var copy = new Dictionary<string, bool>(dict);
                        copy[propKey] = !propExp;
                        return copy;
                    })).Width(28).Height(28)
            ) with { AlignItems = FlexAlign.Center, ColumnGap = 4 };

            rows.Add(rowTemplate(descriptor, label, editor, indentLevel));

            if (isExpanded)
            {
                var value = descriptor.GetValue(owner);
                if (value is not null)
                {
                    var subDescriptors = meta.Decompose!(value);
                    var subEditChain = editChain.Push(descriptor, meta, value);
                    foreach (var subDesc in subDescriptors)
                    {
                        RenderProperty(rows, subDesc, value, registry, el, rowTemplate, labelTemplate,
                            expandState, setExpandState, subEditChain, indentLevel + 1);
                    }
                }
            }
        }
        else
        {
            rows.Add(rowTemplate(descriptor, label, editor, indentLevel));
        }
    }

    private static Element RenderEditor(
        FieldDescriptor descriptor,
        object owner,
        TypeMetadata meta,
        EditChain editChain)
    {
        var currentValue = descriptor.GetValue(owner);

        if (descriptor.IsReadOnly || (descriptor.SetValue is null && editChain.CannotPropagate(descriptor)))
            return RenderReadOnlyValue(currentValue, descriptor.FieldType);

        Action<object> onChange = newValue =>
        {
            if (descriptor.SetValue is not null)
            {
                var newOwner = descriptor.SetValue(owner, newValue);
                // If SetValue returned a different object (immutable), propagate upward
                if (!ReferenceEquals(newOwner, owner))
                    editChain.PropagateNewOwner(descriptor.Name, newOwner);
            }
            else
            {
                editChain.PropagateImmutableEdit(descriptor.Name, newValue);
            }
        };

        return meta.Editor!(currentValue!, onChange);
    }

    private static Element RenderReadOnlyValue(object? value, Type type)
    {
        if (type == typeof(bool))
            return ToggleSwitch((bool)(value ?? false), null).Disabled();
        if (type == typeof(string))
            return TextField((string)(value ?? ""), null).Disabled();
        return TextBlock(value?.ToString() ?? "(null)");
    }

    private static bool IsPrimitiveOrEnum(Type type)
        => type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal);
}

/// <summary>
/// Tracks the path from a leaf property back to the root, enabling
/// immutable edit propagation via Compose at each level.
/// Each entry stores: the descriptor (as seen by the parent), the metadata
/// for the child type, the child value, and the parent object.
/// </summary>
internal class EditChain
{
    private readonly object _root;
    private readonly TypeMetadata _rootMeta;
    private readonly Action<object>? _onRootChanged;
    private readonly List<EditChainEntry> _path;

    public EditChain(object root, TypeMetadata rootMeta, Action<object>? onRootChanged)
    {
        _root = root;
        _rootMeta = rootMeta;
        _onRootChanged = onRootChanged;
        _path = new List<EditChainEntry>();
    }

    private EditChain(object root, TypeMetadata rootMeta, Action<object>? onRootChanged, List<EditChainEntry> path)
    {
        _root = root;
        _rootMeta = rootMeta;
        _onRootChanged = onRootChanged;
        _path = path;
    }

    public EditChain Push(FieldDescriptor descriptor, TypeMetadata meta, object currentValue)
    {
        // The parent is either the last entry's CurrentValue or the root
        var parent = _path.Count > 0 ? _path[^1].CurrentValue : _root;
        var newPath = new List<EditChainEntry>(_path) { new(descriptor, meta, currentValue, parent) };
        return new EditChain(_root, _rootMeta, _onRootChanged, newPath);
    }

    public string BuildPath(string propertyName)
    {
        var parts = _path.Select(e => e.Descriptor.Name).ToList();
        parts.Add(propertyName);
        return string.Join(".", parts);
    }

    public bool CannotPropagate(FieldDescriptor descriptor)
    {
        if (descriptor.SetValue is not null) return false;
        for (int i = _path.Count - 1; i >= 0; i--)
        {
            if (_path[i].Descriptor.SetValue is not null) return false;
            if (_path[i].Meta.Compose is null) return true;
        }
        if (_rootMeta.Compose is null && _onRootChanged is null) return true;
        return false;
    }

    /// <summary>
    /// Propagates a new owner object upward through the chain when SetValue
    /// returned a different reference (immutable edit).
    /// </summary>
    public void PropagateNewOwner(string propertyName, object newOwner)
    {
        var currentObject = newOwner;

        for (int i = _path.Count - 1; i >= 0; i--)
        {
            var entry = _path[i];
            if (entry.Descriptor.SetValue is not null)
            {
                var result = entry.Descriptor.SetValue(entry.ParentValue, currentObject);
                if (ReferenceEquals(result, entry.ParentValue))
                    return; // Mutable ancestor absorbed the change
                currentObject = result;
            }
            else if (entry.Meta.Compose is not null)
            {
                currentObject = entry.Meta.Compose(entry.CurrentValue,
                    new Dictionary<string, object> { { entry.Descriptor.Name, currentObject } });
            }
            else return;
        }

        _onRootChanged?.Invoke(currentObject);
    }

    public void PropagateImmutableEdit(string propertyName, object newValue)
    {
        var updates = new Dictionary<string, object> { { propertyName, newValue } };

        for (int i = _path.Count - 1; i >= 0; i--)
        {
            var entry = _path[i];
            if (entry.Meta.Compose is not null)
            {
                var composed = entry.Meta.Compose(entry.CurrentValue, updates);

                // Check for mutable ancestor: SetValue that mutates in place
                if (entry.Descriptor.SetValue is not null)
                {
                    var result = entry.Descriptor.SetValue(entry.ParentValue, composed);
                    if (ReferenceEquals(result, entry.ParentValue))
                        return; // Mutable ancestor absorbed the change — stop
                }

                // Continue propagating upward via Compose chain
                updates = new Dictionary<string, object> { { entry.Descriptor.Name, composed } };
            }
            else return;
        }

        if (_rootMeta.Compose is not null)
        {
            var newRoot = _rootMeta.Compose(_root, updates);
            _onRootChanged?.Invoke(newRoot);
        }
    }
}

internal record EditChainEntry(
    FieldDescriptor Descriptor,
    TypeMetadata Meta,
    object CurrentValue,
    object ParentValue);
