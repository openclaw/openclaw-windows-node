// Seeded lobster identity for composer visits: the random stream, palettes, looks, colors and names.
// A faithful port of OpenClaw's Control UI at 4cffd43ee043275f6a4c693aaacee8d8e5a61288 (MIT; see
// THIRD_PARTY_NOTICES.md): ui/src/components/lobster-pet-{identity,palettes,lore,contract}.ts and the
// honorific from lobster-dex.ts. Draw order is part of the contract: a seed rolls the same lobster here as
// in the web UI, so traits are only ever appended and every chance/pick pair always burns both draws.
//
// Usage:
//   var seed = LobsterLooks.Seed($"{openingId}:{threadId}", LobsterLooks.LoadSalt);
//   var look = LobsterLooks.Create(seed, DateTime.Now);
//   var title = LobsterLooks.Title(look, seed, familiarity.Visits); // "Sir Pinchy", "✦ Buzz", ...
//   var colors = LobsterLooks.Colors(look);
namespace OpenClawTray.Chat.LobsterVisits;

/// <summary>Upstream <c>mulberry32</c> and the weighted/range helpers built on it.</summary>
internal static class LobsterRandom
{
    /// <summary>Bit-exact port: JavaScript int32 wraparound is C# unchecked uint arithmetic.</summary>
    internal static Func<double> Mulberry32(uint seed)
    {
        var state = seed;
        return () =>
        {
            unchecked
            {
                state += 0x6d2b79f5;
                var t = (state ^ (state >> 15)) * (1 | state);
                t = (t + (t ^ (t >> 7)) * (61 | t)) ^ t;
                return (t ^ (t >> 14)) / 4294967296.0;
            }
        };
    }

    internal static T PickWeighted<T>(Func<double> rng, IReadOnlyList<(T Value, double Weight)> entries)
    {
        var total = 0d;
        foreach (var entry in entries)
            total += entry.Weight;
        var roll = rng() * total;
        foreach (var (value, weight) in entries)
        {
            roll -= weight;
            if (roll <= 0)
                return value;
        }
        return entries[^1].Value;
    }

    internal static double Between(Func<double> rng, double min, double max) => min + rng() * (max - min);

    /// <summary>JavaScript <c>Math.round</c> (ties up) for the non-negative values these rolls produce.</summary>
    internal static double Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>FNV-1a over UTF-16 code units, as upstream <c>fnv1aUtf16</c>.</summary>
    internal static uint Fnv1a(string value)
    {
        var hash = 2166136261u;
        foreach (var ch in value)
            hash = unchecked((hash ^ ch) * 16777619u);
        return hash;
    }
}

internal enum LobsterAccessory { None, Crown, Sprout, Patch, Santa, Pumpkin, Party, Monocle }

internal enum LobsterAntennae { Perky, Droopy }

internal enum LobsterClawSize { Dainty, Regular, Mighty }

internal enum LobsterSide { Left, Right }

internal enum LobsterPersonality { Sleepy, Zoomy, Friendly, Showoff }

/// <summary>A palette's CSS colors. Two palettes use <c>var(--token, fallback)</c>, which renderers map to resources.</summary>
internal sealed record LobsterPalette(string Id, string Shell, string Claw);

/// <summary>Chimera's four donor colors; null for every other palette.</summary>
internal sealed record LobsterChimeraParts(string Body, string ClawLeft, string ClawRight, string Antennae);

internal sealed record LobsterLook(
    LobsterPalette Palette,
    double Scale,
    LobsterAccessory Accessory,
    LobsterAntennae Antennae,
    int SpotPct,
    int Facing,
    LobsterPersonality Personality,
    double BlinkDelaySeconds,
    LobsterClawSize ClawSize,
    bool TailFan,
    bool Shiny,
    LobsterSide? CrusherSide,
    bool Freckles,
    string? Glint,
    LobsterChimeraParts? ChimeraParts);

/// <summary>Resolved paint for one look, as upstream <c>lobsterLookStyle</c> sets its CSS variables.</summary>
internal sealed record LobsterColors(
    string Shell,
    string Claw,
    string? ClawLeft,
    string? ClawRight,
    string? Antennae,
    double ClawLeftScale,
    double ClawRightScale,
    string? Glint);

internal static class LobsterLooks
{
    /// <summary>One salt per app load: reopening rerolls every cast, while re-renders stay stable.</summary>
    internal static readonly uint LoadSalt = (uint)Random.Shared.NextInt64(0, uint.MaxValue);

    // Rarity ladder from lobster-pet-palettes.ts; the classic-logo grails stay the final two entries.
    internal static readonly IReadOnlyList<(LobsterPalette Value, double Weight)> PaletteWeights =
    [
        (new("crimson", "#ff4f40", "#ff775f"), 26),
        (new("blue", "#4a7dfc", "#7fa4ff"), 7),
        (new("gold", "#f4b840", "#f9d47a"), 5),
        (new("lumen", "#1d2f4e", "#2e4a77"), 2),
        (new("magma", "#241214", "#3a1d18"), 2),
        (new("oilslick", "#15171d", "#23262e"), 2),
        (new("aurora", "#dce6f0", "#e9f0f7"), 2),
        (new("nebula", "#34255c", "#4a3a7d"), 2),
        (new("banana", "#f7e27d", "#f3d55b"), 2),
        (new("mood", "var(--accent, #7f77dd)", "var(--accent-hover, #9a93e8)"), 1.5),
        (new("bee", "#f4c531", "#2b2b23"), 1.5),
        (new("rubberduck", "#ffd93b", "#ffb03b"), 1.5),
        (new("watermelon", "#3f9d63", "#4fb072"), 1.5),
        (new("clawnstantine", "#7546a0", "#a576c4"), 1),
        (new("clawiestardust", "#f4ddd1", "#e76545"), 1),
        (new("taylorpinch", "#efcc78", "#d5485c"), 1),
        (new("clawtoodeetoo", "#e8edf1", "#3b68c4"), 1),
        (new("leonardodepinchy", "#ddc595", "#b99161"), 1),
        (new("shellvis", "#f0c49f", "#4b6694"), 1),
        (new("alexandergrahamshell", "#d7c3a5", "#aa8759"), 1),
        (new("clawtron", "#8d99a6", "#a2aeba"), 1),
        (new("selene", "#c9ced8", "#d8dde5"), 1),
        (new("geode", "#6b6474", "#7d7588"), 1),
        (new("ghost", "#dce8f2", "#ecf3fa"), 1),
        (new("glass", "#cfe4f4", "#e0eef8"), 1),
        (new("split", "#ff4f40", "#ff775f"), 1),
        (new("sourdough", "#d9a662", "#e6bc82"), 1),
        (new("zombie", "#9db08a", "#86a17a"), 1),
        (new("plush", "#e8967a", "#f2b09a"), 1),
        (new("balloon", "#ff5c8a", "#ff7ea1"), 1),
        (new("cryptid", "#6e6257", "#7d7263"), .9),
        (new("flatpack", "#d9c9a8", "#d9c9a8"), .9),
        (new("tinfoil", "#9aa4ad", "#a8b2bb"), .9),
        (new("actual", "#a63c28", "#8f3220"), .9),
        (new("cottoncandy", "#f6a8c9", "#a5c6f0"), .8),
        (new("disco", "#b8c4d8", "#cbd5e6"), .8),
        (new("chimera", "#b0685a", "#b0685a"), .75),
        (new("pixel", "#d84c3e", "#ef8f6a"), .7),
        (new("blueprint", "#123a66", "#123a66"), .7),
        (new("phosphor", "#0d2415", "#0f2b19"), .7),
        (new("ascii", "var(--lob-ascii-ink, #d8dee6)", "var(--lob-ascii-ink, #d8dee6)"), .7),
        (new("portal", "#4a9df8", "#ff9a2e"), .7),
        (new("notexture", "#ff00dc", "#111111"), .65),
        (new("loading", "#3a4150", "#454d5e"), .65),
        (new("eclipse", "#14161d", "#1d2026"), .65),
        (new("heisenbug", "#262a33", "#343945"), .6),
        (new("invisible", "rgba(127,140,160,0.07)", "rgba(127,140,160,0.07)"), .55),
        (new("retro", "#e8262c", "#f04a3e"), .5),
        (new("goldenretro", "#e8b422", "#f6cf5a"), .1),
    ];

    private static readonly (LobsterAccessory, double)[] Accessories =
        [(LobsterAccessory.None, 62), (LobsterAccessory.Sprout, 14), (LobsterAccessory.Patch, 14), (LobsterAccessory.Crown, 10)];

    private static readonly (LobsterPersonality, double)[] Personalities =
    [
        (LobsterPersonality.Sleepy, 25), (LobsterPersonality.Zoomy, 25),
        (LobsterPersonality.Friendly, 25), (LobsterPersonality.Showoff, 25),
    ];

    private static readonly (double, double)[] Scales = [(1.7, 25), (2, 55), (2.5, 20)];

    private static readonly (LobsterClawSize, double)[] ClawSizes =
        [(LobsterClawSize.Regular, 55), (LobsterClawSize.Dainty, 25), (LobsterClawSize.Mighty, 20)];

    private static readonly string[] GlintTints = ["#ffd166", "#ff8ac2", "#b79bff"];
    private static readonly string[] ChimeraDonorIds = ["crimson", "blue", "gold", "banana", "watermelon"];

    // OpenClaw's repository was born 2025-11-24; on that day every visitor dresses as the classic logo.
    private const int AnniversaryMonth = 11;
    private const int AnniversaryDay = 24;

    internal static LobsterPalette Palette(string id) => PaletteWeights.First(entry => entry.Value.Id == id).Value;

    /// <summary>Upstream <c>lobsterPetSeed</c>: one salted seed per composer opening key.</summary>
    internal static uint Seed(string key, uint salt) => LobsterRandom.Fnv1a(key) ^ salt;

    /// <summary>Upstream <c>createLobsterPetLook</c>. <paramref name="now"/> is local time (seasonal wardrobe).</summary>
    internal static LobsterLook Create(uint seed, DateTime now)
    {
        var rng = LobsterRandom.Mulberry32(seed);
        var palette = LobsterRandom.PickWeighted(rng, PaletteWeights);
        var scale = LobsterRandom.PickWeighted(rng, Scales);
        var accessory = LobsterRandom.PickWeighted(rng, [.. Accessories, .. SeasonalAccessories(now)]);
        var antennae = rng() < .6 ? LobsterAntennae.Perky : LobsterAntennae.Droopy;
        var (low, high) = rng() < .5 ? (12, 38) : (60, 84);
        var spotPct = (int)LobsterRandom.Round(LobsterRandom.Between(rng, low, high));
        var facing = rng() < .5 ? 1 : -1;
        var personality = LobsterRandom.PickWeighted(rng, Personalities);
        var blinkDelay = LobsterRandom.Round(LobsterRandom.Between(rng, 0, 4) * 10) / 10;
        rng(); // The retired body-build draw keeps later traits aligned.
        var clawSize = LobsterRandom.PickWeighted(rng, ClawSizes);
        var tailFan = rng() < .3;
        var shiny = rng() < 1d / 512;
        var crusherRoll = rng();
        var crusherPick = rng() < .5 ? LobsterSide.Left : LobsterSide.Right;
        var freckles = rng() < .12;
        var glintRoll = rng();
        var glintPick = GlintTints[(int)Math.Floor(rng() * GlintTints.Length)];
        // Always burn all four distinct donor rolls, exposing them only for Chimera.
        var remaining = ChimeraDonorIds.Select(Palette).ToList();
        LobsterPalette Pick()
        {
            var index = (int)Math.Floor(rng() * remaining.Count);
            var donor = remaining[index];
            remaining.RemoveAt(index);
            return donor;
        }
        var parts = new LobsterChimeraParts(Pick().Shell, Pick().Shell, Pick().Shell, Pick().Shell);
        var look = new LobsterLook(palette, scale, accessory, antennae, spotPct, facing, personality, blinkDelay,
            clawSize, tailFan, shiny, crusherRoll < .15 ? crusherPick : null, freckles,
            glintRoll < .3 ? glintPick : null, palette.Id == "chimera" ? parts : null);
        // The LED rides the perky antenna; the undead do not do perky. Rolls above stay aligned either way.
        if (palette.Id == "clawtron")
            look = look with { Antennae = LobsterAntennae.Perky };
        if (palette.Id == "zombie")
            look = look with { Antennae = LobsterAntennae.Droopy };
        if (now.Month == AnniversaryMonth && now.Day == AnniversaryDay)
            look = look with { Palette = Palette("retro"), Accessory = LobsterAccessory.Party, ChimeraParts = null };
        return look;
    }

    // One weighted roll either way, so the rest of a seed's sequence is unchanged on any date.
    private static (LobsterAccessory, double)[] SeasonalAccessories(DateTime now) => now.Month switch
    {
        12 => [(LobsterAccessory.Santa, 18)],
        10 when now.Day >= 20 => [(LobsterAccessory.Pumpkin, 18)],
        // National Lobster Day (US, Sept 25): dress fancy.
        9 when now.Day == 25 => [(LobsterAccessory.Monocle, 24)],
        _ => [],
    };

    /// <summary>Upstream <c>lobsterLookStyle</c>: body/claw paint, Chimera part colors and per-side claw scale.</summary>
    internal static LobsterColors Colors(LobsterLook look)
    {
        var chimera = look.ChimeraParts;
        // Chimera's claws take the claw color of the palette that donated its body.
        var bodyDonorClaw = chimera is null
            ? null
            : PaletteWeights.Select(entry => entry.Value).FirstOrDefault(palette => palette.Shell == chimera.Body)?.Claw;
        return new LobsterColors(
            chimera?.Body ?? look.Palette.Shell,
            bodyDonorClaw ?? look.Palette.Claw,
            chimera?.ClawLeft,
            chimera?.ClawRight,
            chimera?.Antennae,
            ClawScale(look, LobsterSide.Left),
            ClawScale(look, LobsterSide.Right),
            look.Glint);
    }

    /// <summary>Upstream <c>--lob-breathe-delay</c>: a negative per-palette offset (0 to -3.3s) so casts breathe out of step.</summary>
    internal static double BreatheDelaySeconds(LobsterLook look) =>
        -(((LobsterRandom.Fnv1a(look.Palette.Id) >> 8) % 34) / 10.0);

    // A crusher side grows mighty while the other stays dainty; otherwise both follow the rolled claw size.
    private static double ClawScale(LobsterLook look, LobsterSide side) => look.CrusherSide switch
    {
        null => look.ClawSize switch { LobsterClawSize.Dainty => .85, LobsterClawSize.Mighty => 1.18, _ => 1 },
        var crusher when crusher == side => 1.18,
        _ => .85,
    };

    // Names and titles are an untranslated easter-egg channel upstream (lobster-pet-lore.ts), not product copy.
    private static readonly Dictionary<string, string> SignatureNames = new(StringComparer.Ordinal)
    {
        ["blue"] = "Blueberry", ["gold"] = "Goldie", ["lumen"] = "Glimmer", ["magma"] = "Ember",
        ["oilslick"] = "Slick", ["aurora"] = "Borealis", ["nebula"] = "Cosmo", ["banana"] = "Peel",
        ["mood"] = "Ringo", ["bee"] = "Buzz", ["rubberduck"] = "Debuggy", ["watermelon"] = "Pips",
        ["clawnstantine"] = "Clawnstantine", ["clawiestardust"] = "Clawie Stardust",
        ["taylorpinch"] = "Taylor Pinch", ["clawtoodeetoo"] = "Clawtoo Deetoo",
        ["leonardodepinchy"] = "Leonardo DaPinchy", ["shellvis"] = "Shellvis",
        ["alexandergrahamshell"] = "Alexander Graham Shell", ["clawtron"] = "Clawtron", ["selene"] = "Selene",
        ["geode"] = "Amethyst", ["ghost"] = "Boo", ["glass"] = "Prism", ["split"] = "Picasso",
        ["sourdough"] = "Boule", ["zombie"] = "Shambles", ["plush"] = "Buttons", ["balloon"] = "Squeaky",
        ["cottoncandy"] = "Taffy", ["cryptid"] = "Nessie", ["flatpack"] = "Skaldjur", ["tinfoil"] = "Mulder",
        ["actual"] = "Homarus", ["disco"] = "Boogie", ["chimera"] = "Frankie", ["blueprint"] = "Prototype",
        ["phosphor"] = "TTY", ["ascii"] = "Figlet", ["portal"] = "Warp", ["notexture"] = "Magenta",
        ["loading"] = "Spinner", ["eclipse"] = "Umbra", ["heisenbug"] = "Segfault", ["invisible"] = "Nobody",
        ["pixel"] = "Sprite", ["retro"] = "OG", ["goldenretro"] = "24K",
    };

    // Legacy random-name slots stay stable; signature names are keyed by palette.
    private static readonly string[] PetNames =
    [
        "Pinchy", "Barnaby", "Thermidor", "Clawdette", "Sheldon", "Scuttles", "Bisque", "Crusty",
        "Snips", "Bubbles", "Clawdia", "Ferdinand", "Maple", "Pearl", "Biscuit", "Captain",
        "Ziggy", "Noodle", "Waffles", "Pippin", "Squirt", "Chip", "Clementine", "Moss",
    ];

    // Lifetime-visit milestones; the highest earned title wins.
    private static readonly (int Visits, string Title)[] Honorifics = [(250, "Elder"), (100, "Captain"), (50, "Sir")];

    /// <summary>Upstream <c>lobsterPetName</c>: the palette's signature name, else a seeded pet name.</summary>
    internal static string Name(LobsterLook look, uint seed) =>
        SignatureNames.TryGetValue(look.Palette.Id, out var name) ? name : PetNames[(seed >> 3) % (uint)PetNames.Length];

    internal static string? Honorific(int visits) =>
        Honorifics.FirstOrDefault(entry => visits >= entry.Visits).Title;

    /// <summary>The hover title: shiny sparkle, then the load-start honorific, then the name.</summary>
    internal static string Title(LobsterLook look, uint seed, int familiarityVisits)
    {
        var name = Name(look, seed);
        var titled = Honorific(familiarityVisits) is { } honorific ? $"{honorific} {name}" : name;
        return look.Shiny ? $"\u2726 {titled}" : titled;
    }
}
