using System.Collections.Immutable;

namespace OpenClaw.Shared.Themes;

/// <summary>
/// Where built-in theme palettes come from. <see cref="BuiltinGatewayThemes.Table"/> is the bundled table; the
/// Companion's runtime source answers with the connected Gateway's own palettes first and the table second.
/// </summary>
public interface IBuiltinThemeSource
{
    /// <summary>The built-in theme <paramref name="id"/>, or false when this source cannot render it.</summary>
    bool TryGet(string? id, out ResolvedTheme theme);
}

/// <summary>
/// The built-in themes of one Gateway build, resolved from the stylesheets it serves (<see cref="ThemeProbe"/>).
/// Names, descriptions and branding come from that Gateway's <c>themes.list</c> descriptors when it sent them
/// (2026.9+; branding fields since 2026.10, absent ones meaning upstream's defaults), otherwise from the bundled table.
/// Themes the probe could not resolve completely, or whose text would be unreadable, are left out, so a lookup falls
/// back to the table and then to the Windows default.
/// <example><code>
/// var set = ServedBuiltinThemeSet.Build(sheets.Key, ThemeProbe.Parse(json), catalogBuiltins);
/// var source = set.Over(BuiltinGatewayThemes.Table);
/// </code></example>
/// </summary>
public sealed class ServedBuiltinThemeSet : IBuiltinThemeSource
{
    private readonly ImmutableDictionary<string, ResolvedTheme> _byId;

    private ServedBuiltinThemeSet(string key, ImmutableArray<ResolvedTheme> themes, bool hasGatewayDescriptors)
    {
        Key = key;
        Themes = themes;
        HasGatewayDescriptors = hasGatewayDescriptors;
        _byId = themes.ToImmutableDictionary(theme => theme.Id, StringComparer.Ordinal);
    }

    /// <summary>The Gateway build these palettes were resolved for (<see cref="ServedThemeStylesheets.Key"/>).</summary>
    public string Key { get; }

    /// <summary>The renderable built-ins, in the Gateway's catalog order when known, else table order.</summary>
    public ImmutableArray<ResolvedTheme> Themes { get; }

    /// <summary>True when names and branding came from the Gateway's <c>themes.list</c> rather than the table.</summary>
    public bool HasGatewayDescriptors { get; }

    public bool TryGet(string? id, out ResolvedTheme theme)
    {
        theme = null!;
        return id is not null && _byId.TryGetValue(id, out theme!);
    }

    /// <summary>This set first, then <paramref name="fallback"/> for ids it does not have.</summary>
    public IBuiltinThemeSource Over(IBuiltinThemeSource fallback) => new Layered(this, fallback);

    /// <param name="descriptors">The Gateway's built-in descriptors, or null when it has not sent a catalog.</param>
    public static ServedBuiltinThemeSet Build(string key, ThemeProbeResult probe, IReadOnlyList<GatewayThemeDescriptor>? descriptors)
    {
        var themes = ImmutableArray.CreateBuilder<ResolvedTheme>();
        // A repeated id (a malformed catalog or cache) keeps its first entry rather than failing the whole set.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (descriptors is not null)
        {
            foreach (var descriptor in descriptors.Where(entry => entry.Source == GatewayThemeSource.Builtin))
            {
                if (probe.Palettes.TryGetValue(descriptor.Id, out var palettes))
                {
                    Add(new ResolvedTheme(descriptor.Id, descriptor.Name, descriptor.Description, GatewayThemeSource.Builtin,
                        palettes.Light, palettes.Dark) { Branding = descriptor.Branding });
                }
            }
        }
        else
        {
            var tableOrder = BuiltinGatewayThemes.All.Select(theme => theme.Id).ToList();
            foreach (var (id, palettes) in probe.Palettes.OrderBy(entry => tableOrder.IndexOf(entry.Key) is var at and >= 0 ? at : int.MaxValue)
                         .ThenBy(entry => entry.Key, StringComparer.Ordinal))
            {
                // A built-in the table does not know has no name to show without a catalog; it is offered once one arrives.
                if (BuiltinGatewayThemes.TryGet(id, out var known))
                    Add(known with { Light = palettes.Light, Dark = palettes.Dark });
            }
        }
        return new ServedBuiltinThemeSet(key, themes.ToImmutable(), descriptors is not null);

        void Add(ResolvedTheme theme)
        {
            if (ThemeLegibility.IsReadable(theme) && seen.Add(theme.Id))
                themes.Add(theme);
        }
    }

    private sealed class Layered(IBuiltinThemeSource primary, IBuiltinThemeSource fallback) : IBuiltinThemeSource
    {
        public bool TryGet(string? id, out ResolvedTheme theme) =>
            primary.TryGet(id, out theme) || fallback.TryGet(id, out theme);
    }
}
