// Composes one lobster look into renderer-neutral artwork: a port of upstream LobsterSvg (scene props only: look
// and grumpy) plus the static paint rules lobster-pet.css applies to it. The result is a resolved node tree with
// concrete colours and absolute geometry, tagged with the roles the motion layer animates (antennae, claws,
// eyes...). CSS-driven decorative loops (twinkle, magma, aurora...) are rendered in their static base state.
// OpenClaw 4cffd43ee043275f6a4c693aaacee8d8e5a61288: ui/src/components/lobster-pet-artwork.tsx,
// lobster-pet-identity.ts (lobsterLookStyle), lobster-pet-moon.ts and ui/src/styles/lobster-pet.css (MIT; see
// THIRD_PARTY_NOTICES.md).
//
// Usage:
//   var art = LobsterArtwork.Compose(look, grumpy: false, offline: false, DateTimeOffset.Now, "#7f77dd", "#9a93e8");
//   foreach (var claw in art.Root.Find(LobsterArtRole.ClawRight)) { /* claw.Origin is the CSS transform origin */ }
namespace OpenClawTray.Chat.LobsterVisits;

/// <summary>What a node is to the motion layer. <see cref="None"/> nodes are static paint.</summary>
internal enum LobsterArtRole { None, Root, Antennae, Tail, ClawLeft, ClawRight, EyesOpen, EyesClosed, Pattern, Accessory, Face }

/// <summary>
/// One resolved element in the 120x105 viewBox. Groups carry children; leaves carry absolute geometry in their own
/// coordinate space, mapped to the parent's by <see cref="Transform"/>. Fill/stroke null means paint nothing.
/// </summary>
internal sealed record LobsterArtNode(
    LobsterSvgElement Element,
    LobsterArtRole Role,
    LobsterMatrix Transform,
    double Opacity,
    IReadOnlyList<LobsterPathSegment> Segments,
    LobsterRgba? Fill,
    bool EvenOdd,
    LobsterRgba? Stroke,
    double StrokeWidth,
    string LineCap,
    string LineJoin,
    IReadOnlyList<double>? Dash,
    IReadOnlyList<LobsterArtNode> Children)
{
    /// <summary>CSS transform origin (viewBox units) the motion layer rotates/scales this role around.</summary>
    internal (double X, double Y) Origin => Role switch
    {
        LobsterArtRole.ClawLeft => LobsterArtwork.ClawLeftOrigin,
        LobsterArtRole.ClawRight => LobsterArtwork.ClawRightOrigin,
        LobsterArtRole.Antennae => LobsterArtwork.AntennaeOrigin,
        LobsterArtRole.Root => LobsterArtwork.RootOrigin,
        _ => (0, 0),
    };

    /// <summary>Every descendant (including this node) with <paramref name="role"/>, in paint order.</summary>
    internal IEnumerable<LobsterArtNode> Find(LobsterArtRole role)
    {
        if (Role == role)
            yield return this;
        foreach (var child in Children)
        {
            foreach (var match in child.Find(role))
                yield return match;
        }
    }
}

/// <summary>A composed look: the paint tree plus the CSS variables it was resolved with.</summary>
internal sealed record LobsterArtwork(LobsterArtNode Root, IReadOnlyDictionary<string, string> Variables)
{
    internal const double ViewBoxWidth = 120;
    internal const double ViewBoxHeight = 105;
    internal const string DefaultGlint = "#00e5cc";
    internal const string OfflineGlint = "#8a949d";
    internal static readonly (double X, double Y) ClawLeftOrigin = (25, 52);
    internal static readonly (double X, double Y) ClawRightOrigin = (95, 52);
    internal static readonly (double X, double Y) AntennaeOrigin = (60, 14);
    // .lobster-pet__svg transform-origin: 50% 100%.
    internal static readonly (double X, double Y) RootOrigin = (60, 105);

    /// <summary>Palettes whose upstream art is outside the static SVG subset; they draw the standard body instead.</summary>
    internal static readonly IReadOnlyDictionary<string, string> FallbackPalettes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ascii"] = "figlet glyph art uses <text>",
    };

    private const string PatternClass = "lob-art-pattern";
    private const string AccessoryClass = "lob-art-accessory";
    private const string FaceClass = "lob-art-face";

    // Object.hasOwn(PALETTE_PATTERNS, id): these palettes never get freckles, even when the value is null.
    private static readonly HashSet<string> PatternPalettes = new(StringComparer.Ordinal)
    {
        "lumen", "magma", "oilslick", "aurora", "nebula", "glass", "geode", "phosphor", "blueprint",
        "clawnstantine", "clawiestardust", "taylorpinch", "clawtoodeetoo", "leonardodepinchy", "shellvis",
        "alexandergrahamshell", "clawtron", "banana", "bee", "rubberduck", "sourdough", "zombie", "plush", "disco",
        "watermelon", "eclipse", "notexture", "chimera", "split", "retro", "heisenbug", "selene", "pixel", "balloon",
        "cryptid", "flatpack", "tinfoil", "actual", "loading", "ascii", "portal", "invisible", "goldenretro",
    };

    private static readonly HashSet<LobsterAccessory> Headwear =
    [
        LobsterAccessory.Crown, LobsterAccessory.Sprout, LobsterAccessory.Santa, LobsterAccessory.Pumpkin,
        LobsterAccessory.Party,
    ];

    // Class-driven --lob-glint per palette (lobster-pet.css); offline beats them, they beat the seeded glint.
    private static readonly Dictionary<string, string> PaletteGlints = new(StringComparer.Ordinal)
    {
        ["ghost"] = "#a8dcff", ["lumen"] = "#7ef5dd", ["magma"] = "#ff8a5c", ["oilslick"] = "#c084fc",
        ["aurora"] = "#3ec9a0", ["nebula"] = "#ff9de2", ["glass"] = "#bfeaff", ["geode"] = "#c9a6ff",
        ["phosphor"] = "#3fff7d", ["heisenbug"] = "#8be9fd", ["clawiestardust"] = "#8be9fd",
        ["blueprint"] = "#cfe3ff", ["clawnstantine"] = "#ffe09a", ["taylorpinch"] = "#d5c4f2",
        ["clawtoodeetoo"] = "#8cd8ff", ["leonardodepinchy"] = "#e5bd68", ["shellvis"] = "#f4ce7e",
        ["alexandergrahamshell"] = "#dec58e", ["clawtron"] = "#ffb020", ["selene"] = "#8fa8ff",
        ["eclipse"] = "#ffd166", ["sourdough"] = "#e6bc82", ["zombie"] = "#b8d49a", ["plush"] = "#ffb59e",
        ["disco"] = "#cbd5e6", ["cottoncandy"] = "#ff9fd6",
    };

    private static readonly Dictionary<string, double> SvgOpacity = new(StringComparer.Ordinal)
    {
        ["ghost"] = .62, ["glass"] = .38,
    };

    private const double SynodicMonthDays = 29.53059;
    private static readonly DateTimeOffset KnownNewMoon = new(2000, 1, 6, 18, 14, 0, TimeSpan.Zero);

    /// <summary>Upstream <c>moonPhaseFraction</c>: 0 at new moon, 0.5 at full.</summary>
    internal static double MoonPhaseFraction(DateTimeOffset date)
    {
        var days = (date - KnownNewMoon).TotalMilliseconds / 86_400_000;
        return ((days % SynodicMonthDays) + SynodicMonthDays) % SynodicMonthDays / SynodicMonthDays;
    }

    /// <summary>
    /// Composes <paramref name="look"/>. <paramref name="accent"/>/<paramref name="accentHover"/> feed the mood
    /// palette's <c>var(--accent)</c>/<c>var(--accent-hover)</c>; pass the app accent colours as CSS colour strings.
    /// <paramref name="now"/> picks Selene's moon phase.
    /// </summary>
    internal static LobsterArtwork Compose(
        LobsterLook look, bool grumpy, bool offline, DateTimeOffset now, string accent, string accentHover)
    {
        var id = look.Palette.Id;
        var colors = LobsterLooks.Colors(look);
        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--accent"] = accent,
            ["--accent-hover"] = accentHover,
            ["--lob-shell"] = colors.Shell,
            ["--lob-claw"] = colors.Claw,
            ["--lob-glint"] = offline ? OfflineGlint
                : PaletteGlints.TryGetValue(id, out var glint) ? glint
                : colors.Glint ?? DefaultGlint,
        };
        if (colors.ClawLeft is not null)
            vars["--lob-chimera-l"] = colors.ClawLeft;
        if (colors.ClawRight is not null)
            vars["--lob-chimera-r"] = colors.ClawRight;
        if (colors.Antennae is not null)
            vars["--lob-antennae-color"] = colors.Antennae;
        if (id == "split")
            vars["--lob-shell2"] = "#46536b";
        else if (id == "geode")
            vars["--lob-shell2"] = "#241b38";

        var markup = ComposeMarkup(look, grumpy, now);
        var styled = markup.Select(node => Restyle(node, id, offline, colors, null, false)).ToArray();
        var root = Resolve(
            LobsterSvgNode.Group(styled),
            vars,
            Inherited.Initial,
            LobsterArtRole.Root,
            SvgOpacity.TryGetValue(id, out var opacity) ? opacity : 1);
        return new LobsterArtwork(root, vars);
    }

    /// <summary>
    /// A standalone effect sprite drawn with its own fixed paint, such as the entry balloon or the sweep broom. Its
    /// coordinates are the sprite's own viewBox.
    /// </summary>
    internal static LobsterArtNode Sprite(string markup) =>
        Resolve(LobsterSvgNode.Group(LobsterSvg.ParseFragment(markup)), NoVariables, Inherited.Initial, LobsterArtRole.None);

    private static readonly IReadOnlyDictionary<string, string> NoVariables = new Dictionary<string, string>(StringComparer.Ordinal);

    // The JSX tree of LobsterSvg for the scene's props, as parsed nodes in paint order.
    private static List<LobsterSvgNode> ComposeMarkup(LobsterLook look, bool grumpy, DateTimeOffset now)
    {
        var id = look.Palette.Id;
        var nodes = new List<LobsterSvgNode>();
        var retro = id is "retro" or "goldenretro";
        var geometry = id != "ascii" && LobsterSprites.GeometryEyes.ContainsKey(id);
        if (geometry)
        {
            nodes.AddRange(GeometryBody(id));
        }
        else
        {
            nodes.AddRange(Parse(retro ? "RETRO_ANTENNAE" : look.Antennae == LobsterAntennae.Droopy
                ? "ANTENNAE_SPRITES.droopy" : "ANTENNAE_SPRITES.perky"));
            if (look.TailFan)
                nodes.AddRange(Parse("TAIL_FAN"));
            nodes.Add(LobsterSvgNode.Group(LobsterSvg.ParseFragment(LobsterSprites.LeftClaw), "lob-claw lob-claw--l"));
            if (!retro)
            {
                var claw = new List<LobsterSvgNode>();
                var prop = id switch
                {
                    "taylorpinch" or "shellvis" => "MICROPHONE",
                    "leonardodepinchy" or "alexandergrahamshell" => "PALETTE_RIGHT_CLAW_PROPS." + id,
                    _ => null,
                };
                if (prop is not null)
                    claw.AddRange(Parse(prop));
                claw.AddRange(LobsterSvg.ParseFragment(LobsterSprites.RightClaw));
                nodes.Add(LobsterSvgNode.Group(claw, "lob-claw lob-claw--r"));
            }
            var decorations = new List<LobsterSvgNode>();
            if (id == "heisenbug")
                decorations.AddRange(Parse("GLITCH_GHOSTS"));
            if (decorations.Count > 0)
                nodes.Add(LobsterSvgNode.Group(decorations, PatternClass));
            nodes.AddRange(LobsterSvg.ParseFragment(LobsterSprites.StandardDome));

            decorations = [];
            if (id is "split" or "geode")
                decorations.AddRange(Parse("SPLIT_HALF"));
            if (id == "selene")
            {
                var phase = (int)Math.Floor(MoonPhaseFraction(now) * 8 + .5) % 8;
                decorations.AddRange(LobsterSvg.ParseFragment(LobsterSprites.SeleneMoon(phase)));
            }
            var pattern = id switch
            {
                "watermelon" => LobsterSprites.Markup["WATERMELON_RIND"],
                "eclipse" => LobsterSprites.Markup["ECLIPSE_CORONA"],
                "notexture" => LobsterSprites.NotextureChecker(),
                "chimera" => LobsterSprites.Markup["CHIMERA_STITCHES"],
                _ => LobsterSprites.Markup.GetValueOrDefault("PALETTE_PATTERNS." + id),
            };
            if (pattern is not null)
                decorations.AddRange(LobsterSvg.ParseFragment(pattern));
            if (id == "tinfoil")
            {
                decorations.AddRange(Parse("TINFOIL_FOLDS"));
                if (!Headwear.Contains(look.Accessory))
                    decorations.AddRange(Parse("TINFOIL_HAT"));
            }
            if (look.Freckles && !PatternPalettes.Contains(id))
                decorations.AddRange(Parse("FRECKLE_SPOTS"));
            if (decorations.Count > 0)
                nodes.Add(LobsterSvgNode.Group(decorations, PatternClass));
            if (id != "invisible")
                nodes.AddRange(LobsterSvg.ParseFragment(LobsterSprites.Highlight));
            nodes.AddRange(LobsterSvg.ParseFragment(LobsterSprites.EyesOpen));
            nodes.AddRange(LobsterSvg.ParseFragment(LobsterSprites.EyesClosed));
        }

        if (retro)
        {
            nodes.Add(LobsterSvgNode.Group(LobsterSvg.ParseFragment(LobsterSprites.BrowedFace(LobsterSprites.RetroMouth)), FaceClass));
            nodes.Add(LobsterSvgNode.Group(Parse("RETRO_MEGA_CLAW"), "lob-claw lob-claw--r"));
        }
        if (grumpy && !retro && (!geometry || id == "pixel"))
            nodes.Add(LobsterSvgNode.Group(LobsterSvg.ParseFragment(LobsterSprites.BrowedFace(LobsterSprites.GrumpyMouth)), FaceClass));
        if (look.Accessory != LobsterAccessory.None && id != "flatpack")
            nodes.Add(LobsterSvgNode.Group(Parse(AccessoryKey(look.Accessory)), AccessoryClass));
        return nodes;
    }

    private static IEnumerable<LobsterSvgNode> GeometryBody(string id)
    {
        var (open, closed) = LobsterSprites.GeometryEyes[id];
        var eyes = LobsterSvg.ParseFragment(open).Concat(LobsterSvg.ParseFragment(closed)).ToArray();
        IEnumerable<LobsterSvgNode> children = id switch
        {
            "flatpack" => LobsterSvg.ParseFragment(LobsterSprites.FlatpackBody).Concat(eyes).Concat(Parse("FLATPACK_PARTS")),
            "loading" => Parse("SKELETON_PARTS").Concat(eyes),
            "actual" => Parse("ACTUAL_PARTS").Concat(eyes),
            "balloon" => Parse("BALLOON_PARTS").Concat(eyes).Concat(Parse("BALLOON_STRING")),
            "portal" => Parse("PORTAL_FRONT").Concat(eyes).Concat(Parse("PORTAL_REAR")),
            _ => Parse("PIXEL_PARTS").Concat(eyes),
        };
        var cls = id switch
        {
            "flatpack" => "lob-flatpack",
            "loading" => "lob-skeleton",
            "actual" => "lob-actual",
            "balloon" => "lob-balloon",
            "portal" => "lob-portal",
            _ => "lob-pixel-frame",
        };
        return [LobsterSvgNode.Group(children.ToArray(), cls)];
    }

    private static string AccessoryKey(LobsterAccessory accessory) => accessory switch
    {
        LobsterAccessory.Crown => "LOBSTER_HAT_SPRITES.crown",
        LobsterAccessory.Santa => "LOBSTER_HAT_SPRITES.santa",
        LobsterAccessory.Pumpkin => "LOBSTER_HAT_SPRITES.pumpkin",
        LobsterAccessory.Party => "LOBSTER_HAT_SPRITES.party",
        LobsterAccessory.Sprout => "ACCESSORY_SPRITES.sprout",
        LobsterAccessory.Patch => "ACCESSORY_SPRITES.patch",
        _ => "ACCESSORY_SPRITES.monocle",
    };

    private static IReadOnlyList<LobsterSvgNode> Parse(string key) => LobsterSvg.ParseFragment(LobsterSprites.Markup[key]);

    // Applies the static lobster-pet.css rules. CSS declarations beat presentation attributes, so they overwrite.
    private static LobsterSvgNode Restyle(
        LobsterSvgNode node, string id, bool offline, LobsterColors colors, string? clawSide, bool inAntennae)
    {
        var classes = node.Classes;
        if (classes.Contains("lob-claw--l"))
            clawSide = "l";
        else if (classes.Contains("lob-claw--r"))
            clawSide = "r";
        inAntennae |= classes.Contains("lob-antennae");

        if (classes.Contains("lob-tail"))
            node = node.With("fill", "color-mix(in srgb, var(--lob-shell, #ff4f40) 78%, #10181f)");
        if (classes.Contains("lob-eye-closed"))
            node = node.With("opacity", "0");
        if (classes.Contains("lob-portal-ring--blue"))
            node = node.With("color", "#4a9df8");
        else if (classes.Contains("lob-portal-ring--orange"))
            node = node.With("color", "#ff9a2e");
        if (id == "goldenretro" && classes.Contains("lob-retro-claw-line"))
            node = node.With("stroke", "#b8860b");
        if (offline)
        {
            string? dim = classes.Contains("lob-lumen") ? "0.4"
                : classes.Contains("lob-magma") ? "0.35"
                : classes.Contains("lob-scanlines") ? "0.05"
                : classes.Contains("lob-led") ? "0.2"
                : null;
            if (dim is not null)
                node = node.With("opacity", dim);
        }

        var children = node.Children;
        if (children.Count == 0)
            return node;
        var paths = children.Where(child => child.Element == LobsterSvgElement.Path).ToArray();
        var directClaw = classes.Contains("lob-claw--l") ? "l" : classes.Contains("lob-claw--r") ? "r" : null;
        var restyled = new LobsterSvgNode[children.Count];
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Element == LobsterSvgElement.Path)
            {
                if (clawSide is not null)
                {
                    var fill = (id, clawSide) switch
                    {
                        ("split", "r") => "var(--lob-shell2)",
                        ("notexture", "l") => "#ff00dc",
                        ("notexture", "r") => "#111111",
                        ("chimera", "l") => "var(--lob-chimera-l)",
                        ("chimera", "r") => "var(--lob-chimera-r)",
                        _ => null,
                    };
                    if (fill is not null)
                        child = child.With("fill", fill);
                }
                if (directClaw is not null)
                {
                    // .lob-claw--x > path { transform: scale(var(--lob-claw-x)) } replaces any transform attribute.
                    var (scale, origin) = directClaw == "l"
                        ? (colors.ClawLeftScale, ClawLeftOrigin)
                        : (colors.ClawRightScale, ClawRightOrigin);
                    child = child.With("transform", FormatMatrix(
                        LobsterMatrix.Around(LobsterMatrix.Scale(scale, scale), origin.X, origin.Y)));
                }
                if (inAntennae || classes.Contains("lob-antennae"))
                {
                    var first = ReferenceEquals(children[i], paths[0]);
                    var last = ReferenceEquals(children[i], paths[^1]);
                    var stroke = id switch
                    {
                        "split" when last => "var(--lob-shell2)",
                        "notexture" when first => "#ff00dc",
                        "notexture" when last => "#111111",
                        "chimera" => "var(--lob-antennae-color)",
                        "blueprint" => "#cfe3ff",
                        _ => null,
                    };
                    if (stroke is not null)
                        child = child.With("stroke", stroke);
                    if (id == "blueprint")
                        child = child.With("stroke-dasharray", "4 3");
                }
            }
            restyled[i] = Restyle(child, id, offline, colors, clawSide, inAntennae);
        }
        return node.WithChildren(restyled);
    }

    private static string FormatMatrix(LobsterMatrix m) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture, $"matrix({m.A} {m.B} {m.C} {m.D} {m.E} {m.F})");

    private sealed record Inherited(
        string? Fill, string? Stroke, double StrokeWidth, string LineCap, string LineJoin, string? Dash,
        double FillOpacity, double StrokeOpacity, string? Color, string FillRule)
    {
        internal static readonly Inherited Initial = new("black", null, 1, "butt", "miter", null, 1, 1, null, "nonzero");
    }

    private static LobsterArtNode Resolve(
        LobsterSvgNode node, IReadOnlyDictionary<string, string> vars, Inherited parent, LobsterArtRole? forcedRole = null,
        double? forcedOpacity = null)
    {
        var state = parent with
        {
            Fill = node["fill"] ?? parent.Fill,
            Stroke = node["stroke"] ?? parent.Stroke,
            StrokeWidth = node["stroke-width"] is not null ? node.Number("stroke-width") : parent.StrokeWidth,
            LineCap = node["stroke-linecap"] ?? parent.LineCap,
            LineJoin = node["stroke-linejoin"] ?? parent.LineJoin,
            Dash = node["stroke-dasharray"] ?? parent.Dash,
            FillOpacity = node["fill-opacity"] is not null ? node.Number("fill-opacity") : parent.FillOpacity,
            StrokeOpacity = node["stroke-opacity"] is not null ? node.Number("stroke-opacity") : parent.StrokeOpacity,
            Color = node["color"] ?? parent.Color,
            FillRule = node["fill-rule"] ?? parent.FillRule,
        };
        var hidden = string.Equals(node["display"], "none", StringComparison.Ordinal);
        var opacity = forcedOpacity ?? (hidden ? 0 : node["opacity"] is not null ? node.Number("opacity", 1) : 1);
        var role = forcedRole ?? RoleOf(node);
        var transform = LobsterSvg.ParseTransform(node["transform"]);
        if (node.Element == LobsterSvgElement.Group)
        {
            var children = node.Children.Select(child => Resolve(child, vars, state)).ToArray();
            return new LobsterArtNode(node.Element, role, transform, opacity, [], null, false, null, 0,
                state.LineCap, state.LineJoin, null, children);
        }
        var current = LobsterPaint.Resolve(state.Color, vars);
        var fill = Fade(LobsterPaint.Resolve(state.Fill, vars, current), state.FillOpacity);
        var stroke = Fade(LobsterPaint.Resolve(state.Stroke, vars, current), state.StrokeOpacity);
        return new LobsterArtNode(
            node.Element, role, transform, opacity, LobsterSvg.Geometry(node), fill,
            state.FillRule == "evenodd", stroke, state.StrokeWidth, state.LineCap, state.LineJoin,
            ParseDash(state.Dash), []);
    }

    private static LobsterArtRole RoleOf(LobsterSvgNode node)
    {
        var classes = node.Classes;
        if (classes.Contains("lob-claw--l")) return LobsterArtRole.ClawLeft;
        if (classes.Contains("lob-claw--r")) return LobsterArtRole.ClawRight;
        if (classes.Contains("lob-antennae")) return LobsterArtRole.Antennae;
        if (classes.Contains("lob-tail")) return LobsterArtRole.Tail;
        if (classes.Contains("lob-eye-open")) return LobsterArtRole.EyesOpen;
        if (classes.Contains("lob-eye-closed")) return LobsterArtRole.EyesClosed;
        if (classes.Contains(PatternClass)) return LobsterArtRole.Pattern;
        if (classes.Contains(AccessoryClass)) return LobsterArtRole.Accessory;
        if (classes.Contains(FaceClass)) return LobsterArtRole.Face;
        return LobsterArtRole.None;
    }

    private static LobsterRgba? Fade(LobsterRgba? color, double opacity) =>
        color is { } c && opacity < 1 ? c with { A = (byte)Math.Round(c.A * Math.Clamp(opacity, 0, 1)) } : color;

    private static IReadOnlyList<double>? ParseDash(string? dash)
    {
        if (dash is null || dash == "none")
            return null;
        var values = dash.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => double.Parse(part, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (values.Count % 2 == 1)
            values.AddRange(values.ToArray());
        return values.Count == 0 || values.All(v => v == 0) ? null : values;
    }
}
