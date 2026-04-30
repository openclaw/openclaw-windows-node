using System;
using System.Collections.Generic;
using OpenClawTray.A2UI.Rendering.Renderers;

namespace OpenClawTray.A2UI.Rendering;

/// <summary>
/// Catalog-strict registry of component renderers. Built once at process
/// start. Unknown component types resolve to <see cref="UnknownRenderer"/>.
/// To add a new component: implement <see cref="IComponentRenderer"/>,
/// register it here, done.
/// </summary>
public sealed class ComponentRendererRegistry
{
    private readonly Dictionary<string, IComponentRenderer> _byName;
    private readonly IComponentRenderer _unknown;

    internal ComponentRendererRegistry(IEnumerable<IComponentRenderer> renderers, UnknownRenderer unknown)
    {
        _byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (var r in renderers) _byName[r.ComponentName] = r;
        _unknown = unknown;
    }

    public IComponentRenderer GetOrUnknown(string componentName) =>
        _byName.TryGetValue(componentName, out var r) ? r : _unknown;

    public IEnumerable<string> KnownNames => _byName.Keys;

    public static ComponentRendererRegistry BuildDefault(MediaResolver media)
    {
        var renderers = new IComponentRenderer[]
        {
            // Containers
            new RowRenderer(),
            new ColumnRenderer(),
            new ListRenderer(),
            new CardRenderer(),
            new TabsRenderer(),
            new ModalRenderer(),
            // Display
            new TextRenderer(),
            new ImageRenderer(media),
            new IconRenderer(),
            new VideoRenderer(media),
            new AudioPlayerRenderer(media),
            new DividerRenderer(),
            // Interactive
            new ButtonRenderer(),
            new CheckBoxRenderer(),
            new TextFieldRenderer(),
            new DateTimeInputRenderer(),
            new MultipleChoiceRenderer(),
            new SliderRenderer(),
        };
        return new ComponentRendererRegistry(renderers, new UnknownRenderer());
    }
}
