// Verbatim static SVG markup for the composer lobster, vendored from upstream as raw strings so coordinates stay
// exactly as drawn. LobsterArtwork composes these; LobsterSvg parses them.
// OpenClaw 4cffd43ee043275f6a4c693aaacee8d8e5a61288: ui/src/components/lobster-pet-artwork.tsx,
// lobster-pet-sprites.tsx, lobster-pet-sprites-wild.tsx, lobster-hat-sprites.tsx and lobster-pet-sprite.tsx (MIT;
// see THIRD_PARTY_NOTICES.md). JSX was serialized to plain SVG; React-only props were dropped.
//
// Usage:
//   var nodes = LobsterSvg.ParseFragment(LobsterSprites.Markup["TAIL_FAN"]);
//   var dome = LobsterSvg.ParseFragment(LobsterSprites.StandardDome);
namespace OpenClawTray.Chat.LobsterVisits;

internal static class LobsterSprites
{
    internal const string Body =
        "M60 8 C32 8 16 32 16 52 C16 72 30 90 44 95 L44 104 L54 104 L54 96 C58 97.5 62 97.5 66 96 L66 104 L76 104 L76 95 C90 90 104 72 104 52 C104 32 88 8 60 8 Z";

    internal const string StandardDome = $"""<path class="lob-standard-dome" d="{Body}" fill="var(--lob-shell)"></path>""";

    internal const string LeftClaw =
        """<path d="M20 42 C5 37 0 47 5 57 C10 67 20 62 25 52 C28 45 25 42 20 42 Z" fill="var(--lob-claw)"></path>""";

    internal const string RightClaw =
        """<path d="M100 42 C115 37 120 47 115 57 C110 67 100 62 95 52 C92 45 95 42 100 42 Z" fill="var(--lob-claw)"></path>""";

    internal const string Highlight = """<ellipse cx="48" cy="28" rx="20" ry="11" fill="#ffffff" opacity="0.1"></ellipse>""";

    internal const string EyesOpen = """
        <g class="lob-eye-open">
          <circle cx="45" cy="32" r="5.5" fill="#0a1014"></circle>
          <circle cx="75" cy="32" r="5.5" fill="#0a1014"></circle>
          <circle cx="46.5" cy="30.5" r="2.2" fill="var(--lob-glint, #00e5cc)"></circle>
          <circle cx="76.5" cy="30.5" r="2.2" fill="var(--lob-glint, #00e5cc)"></circle>
        </g>
        """;

    internal const string EyesClosed = """
        <g class="lob-eye-closed" fill="none" stroke="#0a1014" stroke-linecap="round">
          <path d="M39 33 Q45 28 51 33" stroke-width="3"></path>
          <path d="M69 33 Q75 28 81 33" stroke-width="3"></path>
        </g>
        """;

    internal static string BrowedFace(string mouth) => $"""
        <g stroke="#0a1014" stroke-linecap="round" fill="none">
          <path d="M37 24 L51 28" stroke-width="3.5"></path>
          <path d="M69 28 L83 24" stroke-width="3.5"></path>
          <path d="{mouth}" stroke-width="3"></path>
        </g>
        """;

    internal const string RetroMouth = "M49 45 Q59 51 69 45 L72 42";
    internal const string GrumpyMouth = "M50 48 Q60 42 70 48";

    internal const string FlatpackBody =
        """<path d="M48 10 C28 10 17 25 17 41 C17 58 29 69 48 69 C67 69 79 58 79 41 C79 25 68 10 48 10 Z" fill="var(--lob-shell)" stroke="#8a7c5f" stroke-width="2" stroke-dasharray="4 3"></path>""";

    // Per-geometry eyes, from lobster-pet-sprites-wild.tsx and the pixel frame in lobster-pet-artwork.tsx.
    internal static readonly IReadOnlyDictionary<string, (string Open, string Closed)> GeometryEyes =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["flatpack"] = (
                """<g class="lob-eye-open"><circle cx="39" cy="34" r="4.5" fill="#0a1014"></circle><circle cx="57" cy="34" r="4.5" fill="#0a1014"></circle><circle cx="40.2" cy="32.8" r="1.7" fill="var(--lob-glint, #00e5cc)"></circle><circle cx="58.2" cy="32.8" r="1.7" fill="var(--lob-glint, #00e5cc)"></circle></g>""",
                """<g class="lob-eye-closed" fill="none" stroke="#0a1014" stroke-linecap="round"><path d="M34 35 Q39 31 44 35" stroke-width="2.5"></path><path d="M52 35 Q57 31 62 35" stroke-width="2.5"></path></g>"""),
            ["loading"] = (
                """<g class="lob-eye-open"><circle cx="47" cy="43" r="5" fill="#4a5364"></circle><circle cx="73" cy="43" r="5" fill="#4a5364"></circle></g>""",
                """<g class="lob-eye-closed" fill="#4a5364"><rect x="41" y="42" width="12" height="3" rx="1.5"></rect><rect x="67" y="42" width="12" height="3" rx="1.5"></rect></g>"""),
            ["actual"] = (
                """<g class="lob-eye-open"><circle cx="45" cy="22" r="3.5" fill="#17100e"></circle><circle cx="75" cy="22" r="3.5" fill="#17100e"></circle><circle cx="46" cy="21" r="1" fill="#f2c6a8"></circle><circle cx="76" cy="21" r="1" fill="#f2c6a8"></circle></g>""",
                """<g class="lob-eye-closed" fill="none" stroke="#17100e" stroke-linecap="round"><path d="M41.5 22 H48.5" stroke-width="2"></path><path d="M71.5 22 H78.5" stroke-width="2"></path></g>"""),
            ["balloon"] = (
                """<g class="lob-eye-open"><circle cx="50" cy="30" r="4.5" fill="#0a1014"></circle><circle cx="70" cy="30" r="4.5" fill="#0a1014"></circle><circle cx="51.3" cy="28.7" r="1.8" fill="var(--lob-glint, #00e5cc)"></circle><circle cx="71.3" cy="28.7" r="1.8" fill="var(--lob-glint, #00e5cc)"></circle></g>""",
                """<g class="lob-eye-closed" fill="none" stroke="#0a1014" stroke-linecap="round"><path d="M45 31 Q50 27 55 31" stroke-width="2.5"></path><path d="M65 31 Q70 27 75 31" stroke-width="2.5"></path></g>"""),
            ["portal"] = (
                """<g class="lob-eye-open"><circle cx="45" cy="39" r="4" fill="#0a1014"></circle><circle cx="57" cy="37" r="4" fill="#0a1014"></circle><circle cx="46" cy="38" r="1.5" fill="var(--lob-glint, #00e5cc)"></circle><circle cx="58" cy="36" r="1.5" fill="var(--lob-glint, #00e5cc)"></circle></g>""",
                """<g class="lob-eye-closed" fill="none" stroke="#0a1014" stroke-linecap="round"><path d="M41 40 Q45 37 49 40" stroke-width="2.2"></path><path d="M53 38 Q57 35 61 38" stroke-width="2.2"></path></g>"""),
            ["pixel"] = (
                """<g class="lob-eye-open"><rect x="42" y="30" width="6" height="6" fill="#0a1014"></rect><rect x="72" y="30" width="6" height="6" fill="#0a1014"></rect><rect x="42" y="30" width="2.5" height="2.5" fill="#fff"></rect><rect x="72" y="30" width="2.5" height="2.5" fill="#fff"></rect></g>""",
                """<g class="lob-eye-closed" fill="#0a1014"><rect x="42" y="33" width="6" height="3"></rect><rect x="72" y="33" width="6" height="3"></rect></g>"""),
        };

    /// <summary>Upstream <c>seleneMoon</c>: the shadow disc slides across the lit disc by phase (0 new, 4 full).</summary>
    internal static string SeleneMoon(int phase)
    {
        const string Lit = "#f4f7fc";
        const string Dark = "#26304a";
        var back = $"""<circle cx="60" cy="64" r="12" fill="{Dark}"></circle>""";
        int[] offsets = [0, -5, -9, -14, 0, 14, 9, 5];
        return phase switch
        {
            0 => back + $"""<circle cx="60" cy="64" r="11" fill="none" stroke="{Lit}" stroke-width="1"></circle>""",
            4 => back + $"""<circle cx="60" cy="64" r="11" fill="{Lit}"></circle>""",
            _ => back + $"""<circle cx="60" cy="64" r="11" fill="{Lit}"></circle><circle cx="{60 + offsets[phase]}" cy="64" r="11" fill="{Dark}"></circle>""",
        };
    }

    /// <summary>Upstream NOTEXTURE_CHECKER: magenta/black 8px cells over the dome rows.</summary>
    internal static string NotextureChecker()
    {
        (int Y, int X, int Count)[] rows =
        [
            (12, 44, 4), (20, 36, 6), (28, 28, 8), (36, 20, 10), (44, 16, 11), (52, 16, 11),
            (60, 16, 11), (68, 20, 10), (76, 24, 9), (84, 32, 7), (92, 44, 4),
        ];
        var builder = new System.Text.StringBuilder("""<g class="lob-notexture">""");
        for (var row = 0; row < rows.Length; row++)
        {
            for (var col = 0; col < rows[row].Count; col++)
            {
                var fill = (row + col) % 2 == 0 ? "#ff00dc" : "#111111";
                builder.Append($"""<rect x="{rows[row].X + col * 8}" y="{rows[row].Y}" width="8" height="8" fill="{fill}"></rect>""");
            }
        }
        return builder.Append("</g>").ToString();
    }

    /// <summary>Named sprite markup. Keys mirror upstream constant names (PALETTE_PATTERNS.lumen, TAIL_FAN, ...).</summary>
    /// <summary>Balloon entrance rig (lobster-pet-sprites.tsx Balloon), viewBox 0 0 40 62.</summary>
    internal const string EntryBalloon = """
        <path d="M20 34 Q23 46 18 60" stroke="#8a949d" stroke-width="1.5" fill="none"></path>
        <ellipse cx="20" cy="16" rx="13" ry="15" fill="#ff5c8a"></ellipse>
        <path d="M17 30 L20 34.5 L23 30 Z" fill="#e0446f"></path>
        <ellipse cx="15" cy="10" rx="4" ry="6" fill="#ffffff" opacity="0.3"></ellipse>
        """;

    /// <summary>Sweep act broom (lobster-pet-scene-view.tsx), viewBox 0 0 24 40.</summary>
    internal const string Broom = """
        <path d="M12 2 L12 24" stroke="#8a5a2b" stroke-width="3" stroke-linecap="round" />
        <path d="M6 24 L18 24 L21 38 L3 38 Z" fill="#e8b04b" />
        <path d="M7.5 28 L6.5 36 M12 28 L12 36 M16.5 28 L17.5 36" stroke="#b6791f" stroke-width="1.5" />
        """;

    internal static readonly IReadOnlyDictionary<string, string> Markup = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GLITCH_GHOSTS"] = """
            <g class="lob-glitch-ghosts">
              <path d="M60 8 C32 8 16 32 16 52 C16 72 30 90 44 95 L44 104 L54 104 L54 96 C58 97.5 62 97.5 66 96 L66 104 L76 104 L76 95 C90 90 104 72 104 52 C104 32 88 8 60 8 Z" transform="translate(-3 0)" fill="#ff3355" opacity="0.4"></path>
              <path d="M60 8 C32 8 16 32 16 52 C16 72 30 90 44 95 L44 104 L54 104 L54 96 C58 97.5 62 97.5 66 96 L66 104 L76 104 L76 95 C90 90 104 72 104 52 C104 32 88 8 60 8 Z" transform="translate(3 1)" fill="#22d3ee" opacity="0.4"></path>
            </g>
            """,
        ["MICROPHONE"] = """
            <path class="lob-microphone" d="M106 32 L112 34 L106 56 Q105 58 103 57 Q101 56 102 54 Z" fill="#343747"></path>
            <path class="lob-microphone" d="M103 27 A7 7 0 1 1 117 27 A7 7 0 1 1 103 27 Z" fill="#666a80"></path>
            <path class="lob-microphone" d="M106 23 L114 25 M105 27 L113 29" fill="none" stroke="#e7e7f0" stroke-width="1.5" stroke-linecap="round"></path>
            """,
        ["PALETTE_RIGHT_CLAW_PROPS.leonardodepinchy"] = """
            <path class="lob-leonardodepinchy__brush" d="M107 32 L112 33 L106 57 Q104 59 102 56 Z" fill="#855233"></path>
            <path class="lob-leonardodepinchy__brush" d="M106 27 L114 29 L112 36 L104 34 Z" fill="#b4b7b5"></path>
            <path class="lob-leonardodepinchy__brush" d="M106 27 C105 21 110 18 111 11 C119 21 118 26 114 29 Z" fill="#e6c88e"></path>
            <path class="lob-leonardodepinchy__brush" d="M111 11 Q114 16 115 18 Q111 21 108 22 Q110 16 111 11 Z" fill="#4a89b8"></path>
            """,
        ["PALETTE_RIGHT_CLAW_PROPS.alexandergrahamshell"] = """
            <path class="lob-telephone-receiver" d="M105 33 L112 34 L108 56 Q105 59 102 55 Z" fill="#4b3c30"></path>
            <path class="lob-telephone-receiver" d="M102 29 C104 21 113 21 117 29 L117 34 Q109 39 101 34 Z" fill="#35312f"></path>
            <path class="lob-telephone-receiver" d="M103 30 Q110 34 116 30" fill="none" stroke="#bc955c" stroke-width="2" stroke-linecap="round"></path>
            <path class="lob-telephone-receiver" d="M105 56 Q101 63 108 65 Q115 67 110 71 Q106 74 112 77" fill="none" stroke="#574631" stroke-width="1.8" stroke-linecap="round"></path>
            """,
        ["PALETTE_PATTERNS.lumen"] = """
            <g class="lob-lumen" fill="#7ef5dd">
              <circle cx="36" cy="54" r="2.4"></circle>
              <circle cx="50" cy="66" r="2"></circle>
              <circle cx="66" cy="70" r="2.2"></circle>
              <circle cx="80" cy="60" r="2"></circle>
              <circle cx="88" cy="46" r="1.7"></circle>
              <circle cx="60" cy="86" r="1.7"></circle>
            </g>
            """,
        ["PALETTE_PATTERNS.magma"] = """
            <g class="lob-magma" fill="none" stroke="#ff6a3d" stroke-width="2" stroke-linecap="round">
              <path d="M40 44 L48 54 L42 66 L50 78"></path>
              <path d="M74 40 L68 52 L78 64"></path>
              <path d="M56 82 L62 90"></path>
            </g>
            """,
        ["PALETTE_PATTERNS.oilslick"] = """
            <g class="lob-oilsheen">
              <ellipse cx="46" cy="60" rx="22" ry="11" fill="#7f77dd" opacity="0.3" transform="rotate(-14 46 60)"></ellipse>
              <ellipse cx="76" cy="74" rx="17" ry="8" fill="#1d9e75" opacity="0.28" transform="rotate(10 76 74)"></ellipse>
            </g>
            """,
        ["PALETTE_PATTERNS.aurora"] = """
            <g class="lob-aurora" fill="none" stroke-linecap="round">
              <path class="lob-aurora__band1" d="M24 62 Q48 48 70 58 T102 54" stroke="#4ecfa6" stroke-width="6" opacity="0.5"></path>
              <path class="lob-aurora__band2" d="M28 76 Q54 62 78 72 T100 68" stroke="#a184ec" stroke-width="5" opacity="0.45"></path>
            </g>
            """,
        ["PALETTE_PATTERNS.nebula"] = """
            <g class="lob-nebula-stars">
              <circle cx="38" cy="52" r="1" fill="#fff"></circle>
              <circle cx="52" cy="70" r="1.2" fill="#fff"></circle>
              <circle cx="84" cy="48" r="1.4" fill="#fff"></circle>
              <circle cx="66" cy="86" r="1" fill="#fff"></circle>
              <circle cx="72" cy="60" r="1.6" fill="#8be9fd"></circle>
              <circle cx="46" cy="40" r="1.4" fill="#ff9de2"></circle>
              <path class="lob-twinkle" d="M60 52 L61.5 55.5 L65 57 L61.5 58.5 L60 62 L58.5 58.5 L55 57 L58.5 55.5 Z" fill="#fff"></path>
            </g>
            """,
        ["PALETTE_PATTERNS.glass"] = """
            <g class="lob-glass-glints" fill="none" stroke="#ffffff" stroke-width="2.5" stroke-linecap="round" opacity="0.7">
              <path d="M34 22 L28 32"></path>
              <path d="M40 16 L37 22"></path>
            </g>
            """,
        ["PALETTE_PATTERNS.geode"] = """
            <g class="lob-geode-facets">
              <polygon points="70,34 80,30 78,44" fill="#9b6ff0"></polygon>
              <polygon points="82,46 94,42 88,58" fill="#b48ef0"></polygon>
              <polygon points="72,58 84,62 74,74" fill="#7a4fd0"></polygon>
              <polygon points="86,68 96,64 90,80" fill="#9b6ff0"></polygon>
              <circle class="lob-twinkle" cx="90" cy="50" r="1.8" fill="#fff"></circle>
            </g>
            """,
        ["PALETTE_PATTERNS.phosphor"] = """
            <g class="lob-scanlines" stroke="#3fff7d" stroke-width="1" opacity="0.16">
              <path d="M40 20 H80 M30 27 H90 M26 34 H94 M21 41 H99 M18 48 H102 M17 55 H103 M17 62 H103 M18 69 H102 M22 76 H98 M31 83 H89 M45 90 H75"></path>
            </g>
            """,
        ["PALETTE_PATTERNS.blueprint"] = """
            <g class="lob-blueprint" fill="none" stroke="#cfe3ff">
              <path class="lob-bp-outline" d="M60 8 C32 8 16 32 16 52 C16 72 30 90 44 95 L44 104 L54 104 L54 96 C58 97.5 62 97.5 66 96 L66 104 L76 104 L76 95 C90 90 104 72 104 52 C104 32 88 8 60 8 Z" stroke-width="1.5" stroke-dasharray="5 3"></path>
              <path d="M54 58 H66 M60 52 V64" stroke-width="1" opacity="0.7"></path>
              <path d="M16 100 H104" stroke-width="1" stroke-dasharray="2 3" opacity="0.7"></path>
            </g>
            """,
        ["PALETTE_PATTERNS.clawnstantine"] = """
            <g class="lob-clawnstantine">
              <path d="M18 48 Q60 62 102 48 L104 52 C104 72 90 90 76 95 Q60 98 44 95 C30 90 16 72 16 52 Z" fill="#4e296e"></path>
              <path d="M28 61 Q30 72 39 78 M70 88 Q82 82 91 65" fill="none" stroke="#a576c4" stroke-width="1.6" stroke-linecap="round"></path>
              <path d="M81.5 45.5 C70 64 50 78 29.9 85.444 C32.971 88.632 36.349 91.249 39.814 93.121 C62 85 81 68 90.5 50.5 Z" fill="#e5bc62"></path>
              <path d="M84 51 C73 68 57 80 39 87" fill="none" stroke="#fff0bc" stroke-width="1.6" stroke-linecap="round"></path>
              <circle cx="85" cy="49" r="6" fill="#e5bc62"></circle>
              <circle cx="85" cy="49" r="3" fill="#58c8b5"></circle>
              <g class="lob-clawnstantine__laurel" fill="#e5bc62" stroke="#bd8a38" stroke-width="0.6">
                <path d="M31 27 Q31 15 50 11 M89 27 Q89 15 70 11" fill="none" stroke="#e5bc62" stroke-width="2"></path>
                <path d="M33 25 Q23 21 28 15 Q35 17 33 25 Z M37 19 Q28 13 34 9 Q41 12 37 19 Z M43 14 Q38 7 44 6 Q49 9 43 14 Z"></path>
                <path d="M87 25 Q97 21 92 15 Q85 17 87 25 Z M83 19 Q92 13 86 9 Q79 12 83 19 Z M77 14 Q82 7 76 6 Q71 9 77 14 Z"></path>
              </g>
            </g>
            """,
        ["PALETTE_PATTERNS.clawiestardust"] = """
            <g class="lob-clawiestardust">
              <path d="M72 14 L57 49 L70 43 L51 89 L85 38 L72 44 L83 18 Z" fill="#4dc8eb" transform="translate(2.5 0)"></path>
              <path d="M72 14 L57 49 L70 43 L51 89 L85 38 L72 44 L83 18 Z" fill="#e6393d"></path>
            </g>
            """,
        ["PALETTE_PATTERNS.taylorpinch"] = """
            <g class="lob-taylorpinch">
              <path d="M51 44 Q56 40 60 43 Q64 40 69 44 Q60 54 51 44 Z" fill="#c92e48"></path>
              <path d="M16 52 Q37 50 60 62 Q83 50 104 52 C104 72 90 90 76 95 Q60 98 44 95 C30 90 16 72 16 52 Z" fill="#9c82cf"></path>
              <path d="M22 57 Q40 56 60 68 Q80 56 98 57" fill="none" stroke="#d5c4f2" stroke-width="2" stroke-linecap="round"></path>
              <g fill="#fff4fb">
                <path d="M36 65 L37.5 69.5 L42 71 L37.5 72.5 L36 77 L34.5 72.5 L30 71 L34.5 69.5 Z"></path>
                <path d="M79 72 L80.5 76.5 L85 78 L80.5 79.5 L79 84 L77.5 79.5 L73 78 L77.5 76.5 Z"></path>
                <circle cx="57" cy="79" r="1.5"></circle>
                <circle cx="67" cy="88" r="1.5"></circle>
                <circle cx="87" cy="64" r="1.5"></circle>
              </g>
            </g>
            """,
        ["PALETTE_PATTERNS.clawtoodeetoo"] = """
            <g class="lob-clawtoodeetoo">
              <path d="M27 26 C35 14 45 8 60 8 C75 8 85 14 93 26 Q60 19 27 26 Z" fill="#b8c3cf"></path>
              <g fill="#2d5cbd">
                <path d="M37 17 L46 12 L46 22 L34 24 Z M54 9 H66 L67 21 H53 Z M74 12 L84 17 L86 24 L74 22 Z"></path>
                <path d="M20 46 Q60 42 100 46 L101 53 Q60 49 19 53 Z"></path>
                <rect x="53" y="58" width="14" height="9" rx="1.5"></rect>
                <rect x="53" y="71" width="14" height="9" rx="1.5"></rect>
              </g>
              <g fill="none" stroke="#9ba8b7" stroke-width="1.5">
                <rect x="32" y="59" width="13" height="25" rx="2"></rect>
                <rect x="75" y="59" width="13" height="25" rx="2"></rect>
                <path d="M54 86 H66 M54 90 H66" stroke-linecap="round"></path>
              </g>
              <circle cx="87" cy="49" r="2.5" fill="#ef5261"></circle>
            </g>
            """,
        ["PALETTE_PATTERNS.leonardodepinchy"] = """
            <g class="lob-leonardodepinchy">
              <path d="M18 48 Q60 61 102 48 L104 52 C104 72 90 90 76 95 Q60 98 44 95 C30 90 16 72 16 52 Z" fill="#5f6950"></path>
              <path d="M60 63 V93 M27 64 Q31 77 40 84 M93 64 Q89 77 80 84" fill="none" stroke="#87906e" stroke-width="1.6" stroke-linecap="round"></path>
              <path d="M40 42 Q60 52 80 42 Q78 58 60 66 Q42 58 40 42 Z" fill="#eee3ce"></path>
              <path d="M52 49 Q60 54 68 49" fill="none" stroke="#c7b99e" stroke-width="1.5" stroke-linecap="round"></path>
              <path d="M25 24 C29 9 51 5 67 8 C83 4 96 10 95 20 Q71 29 25 24 Z" fill="#49352c"></path>
              <path d="M28 23 Q61 27 91 21" fill="none" stroke="#765545" stroke-width="2" stroke-linecap="round"></path>
              <circle cx="38" cy="72" r="3" fill="#bd644f"></circle>
              <circle cx="77" cy="81" r="3" fill="#4a89b8"></circle>
              <circle cx="83" cy="65" r="2.5" fill="#dfb34f"></circle>
            </g>
            """,
        ["PALETTE_PATTERNS.shellvis"] = """
            <g class="lob-shellvis">
              <path d="M18 48 L43 47 L60 62 L77 47 L102 48 L104 52 C104 72 90 90 76 95 Q60 98 44 95 C30 90 16 72 16 52 Z" fill="#fff7e9"></path>
              <path d="M43 49 L48 66 L60 61 L72 66 L77 49 M26 80 Q60 87 94 80" fill="none" stroke="#d7ab4c" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"></path>
              <path d="M53 46 L60 50 L67 46 L64 54 L72 74 L65 71 L59 56 L51 68 L48 63 L56 53 Z" fill="#c84248"></path>
              <rect x="56" y="80" width="8" height="6" rx="1.5" fill="#d7ab4c"></rect>
              <path d="M24 26 Q21 10 41 9 Q42 1 62 5 C76 1 96 7 96 19 L91 29 Q82 21 77 20 Q59 31 43 21 Q34 21 24 26 Z" fill="#29272d"></path>
              <path d="M35 17 Q50 10 61 13 Q75 8 85 15" fill="none" stroke="#4d4750" stroke-width="2" stroke-linecap="round"></path>
              <path d="M25 27 L32 25 L32 42 L26 38 Z M88 25 L95 27 L94 38 L88 42 Z" fill="#29272d"></path>
            </g>
            """,
        ["PALETTE_PATTERNS.alexandergrahamshell"] = """
            <g class="lob-alexandergrahamshell">
              <path d="M18 48 Q60 58 102 48 L104 52 C104 72 90 90 76 95 Q60 98 44 95 C30 90 16 72 16 52 Z" fill="#3d4b53"></path>
              <path d="M37 47 L49 73 L60 63 L71 73 L83 47" fill="#f1ebdf"></path>
              <path d="M35 61 L45 86 M85 61 L75 86" fill="none" stroke="#65747a" stroke-width="1.6" stroke-linecap="round"></path>
              <path d="M28 29 Q25 18 35 15 L38 32 L32 42 Z M92 29 Q95 18 85 15 L82 32 L88 42 Z" fill="#c9ceca"></path>
              <path d="M31 38 Q40 47 47 43 Q60 52 73 43 Q80 47 89 38 Q88 59 76 66 Q71 76 60 77 Q49 76 44 66 Q32 59 31 38 Z" fill="#dfe0dc"></path>
              <path d="M51 52 Q60 57 69 52 M49 65 Q60 72 71 65" fill="none" stroke="#b8beba" stroke-width="1.5" stroke-linecap="round"></path>
              <path d="M50 78 L60 81 L70 78 V86 L60 83 L50 86 Z" fill="#8f3347"></path>
              <circle cx="60" cy="91" r="1.8" fill="#bc955c"></circle>
            </g>
            """,
        ["PALETTE_PATTERNS.clawtron"] = """
            <g class="lob-mecha">
              <g fill="none" stroke="#5f6a75" stroke-width="1.5">
                <path d="M28 56 Q60 66 92 56"></path>
                <path d="M34 74 Q60 82 86 74"></path>
              </g>
              <g fill="#5f6a75">
                <circle cx="36" cy="61" r="1.4"></circle>
                <circle cx="60" cy="64" r="1.4"></circle>
                <circle cx="84" cy="61" r="1.4"></circle>
              </g>
              <circle class="lob-led" cx="89" cy="7" r="3" fill="#ff4444"></circle>
            </g>
            """,
        ["PALETTE_PATTERNS.banana"] = """
            <g fill="#8a6430">
              <rect x="56" y="8" width="8" height="6" rx="2.5"></rect>
              <ellipse cx="60" cy="91" rx="6" ry="3.5"></ellipse>
              <circle cx="37" cy="48" r="2.5"></circle>
              <circle cx="79" cy="55" r="2"></circle>
              <circle cx="47" cy="70" r="1.8"></circle>
              <circle cx="72" cy="79" r="2.3"></circle>
            </g>
            """,
        ["PALETTE_PATTERNS.bee"] = """
            <g class="lob-bee-wings" fill="#ffffff" opacity="0.45">
              <ellipse cx="38" cy="14" rx="8" ry="4.5" transform="rotate(-24 38 14)"></ellipse>
              <ellipse cx="82" cy="14" rx="8" ry="4.5" transform="rotate(24 82 14)"></ellipse>
            </g>
            <g fill="#2b2b23" opacity="0.9">
              <path d="M19 42 Q60 51 101 42 L103 50 Q60 60 17 50 Z"></path>
              <path d="M17 58 Q60 67 103 58 L101 67 Q60 76 19 67 Z"></path>
              <path d="M24 76 Q60 84 96 76 L90 85 Q60 92 30 85 Z"></path>
            </g>
            """,
        ["PALETTE_PATTERNS.rubberduck"] = """
            <g>
              <ellipse cx="60" cy="71" rx="21" ry="14" fill="#ffffff" opacity="0.5"></ellipse>
              <rect x="47" y="41" width="26" height="8" rx="4" fill="#ff9a2e"></rect>
              <rect x="50" y="47" width="20" height="5" rx="2.5" fill="#e98322"></rect>
            </g>
            """,
        ["PALETTE_PATTERNS.sourdough"] = """
            <g fill="none" stroke="#a8763e" stroke-width="2.5" stroke-linecap="round">
              <path d="M38 23 Q45 29 52 30"></path>
              <path d="M52 17 Q59 24 66 25"></path>
              <path d="M67 18 Q74 24 81 25"></path>
            </g>
            <g fill="#ffffff" opacity="0.5">
              <circle cx="34" cy="39" r="1.2"></circle>
              <circle cx="86" cy="38" r="1"></circle>
              <circle cx="45" cy="57" r="1.4"></circle>
              <circle cx="74" cy="62" r="1.1"></circle>
              <circle cx="56" cy="78" r="1"></circle>
              <circle cx="83" cy="75" r="1.3"></circle>
            </g>
            """,
        ["PALETTE_PATTERNS.zombie"] = """
            <g fill="none" stroke="#5a6b52" stroke-width="2" stroke-linecap="round">
              <path d="M32 24 Q47 19 61 23"></path>
              <path d="M38 19 L40 26 M46 18 L47 25 M54 19 L53 26"></path>
              <path d="M57 72 Q72 78 87 72"></path>
              <path d="M65 72 L63 79 M73 73 L72 80 M81 71 L83 78"></path>
            </g>
            <ellipse cx="35" cy="61" rx="9" ry="6" fill="#86987a" opacity="0.8" transform="rotate(-18 35 61)"></ellipse>
            """,
        ["PALETTE_PATTERNS.plush"] = """
            <g fill="none" stroke="#c97a5e" stroke-width="1.5" stroke-dasharray="3 3">
              <path d="M30 32 Q60 4 90 32"></path>
              <path d="M60 50 Q58 72 60 96"></path>
            </g>
            <g class="lob-plush-button">
              <circle cx="78" cy="44" r="3.5" fill="#7a4a3a"></circle>
              <circle cx="76.8" cy="44" r="0.7" fill="#e8967a"></circle>
              <circle cx="79.2" cy="44" r="0.7" fill="#e8967a"></circle>
            </g>
            """,
        ["PALETTE_PATTERNS.disco"] = """
            <g class="lob-disco" fill="#ffffff">
              <rect x="42" y="18" width="4" height="4" opacity="0.3"></rect>
              <rect x="52" y="16" width="4" height="4" opacity="0.5"></rect>
              <rect x="63" y="17" width="4" height="4" opacity="0.25"></rect>
              <rect x="74" y="20" width="4" height="4" opacity="0.4"></rect>
              <rect x="31" y="40" width="4" height="4" opacity="0.4"></rect>
              <rect x="53" y="39" width="4" height="4" opacity="0.25"></rect>
              <rect x="65" y="42" width="4" height="4" opacity="0.5"></rect>
              <rect x="86" y="40" width="4" height="4" opacity="0.3"></rect>
              <rect x="39" y="59" width="4" height="4" opacity="0.25"></rect>
              <rect x="51" y="62" width="4" height="4" opacity="0.45"></rect>
              <rect x="68" y="60" width="4" height="4" opacity="0.3"></rect>
              <rect x="80" y="58" width="4" height="4" opacity="0.5"></rect>
              <rect x="49" y="79" width="4" height="4" opacity="0.35"></rect>
              <rect x="70" y="80" width="4" height="4" opacity="0.25"></rect>
            </g>
            """,
        ["ACCESSORY_SPRITES.sprout"] = """
            <g>
              <path d="M60 12 Q58 4 63 1" stroke="#3f9d63" stroke-width="3" stroke-linecap="round" fill="none"></path>
              <ellipse cx="67" cy="3" rx="5" ry="3" fill="#57c785" transform="rotate(-24 67 3)"></ellipse>
            </g>
            """,
        ["ACCESSORY_SPRITES.patch"] = """
            <g>
              <path d="M28 27 Q60 14 92 22" stroke="#101820" stroke-width="4" stroke-linecap="round" fill="none"></path>
              <circle cx="75" cy="32" r="9" fill="#101820"></circle>
            </g>
            """,
        ["ACCESSORY_SPRITES.monocle"] = """
            <g class="lob-monocle" fill="none" stroke="#f4b840">
              <circle cx="75" cy="32" r="8.5" stroke-width="2.5"></circle>
              <path d="M81 39 Q85 48 80 56" stroke-width="1.5"></path>
            </g>
            """,
        ["FRECKLE_SPOTS"] = """
            <g class="lob-freckles" fill="#ffffff" opacity="0.3">
              <circle cx="42" cy="45" r="1.6"></circle>
              <circle cx="50" cy="41" r="1.2"></circle>
              <circle cx="70" cy="45" r="1.6"></circle>
              <circle cx="78" cy="41" r="1.2"></circle>
              <circle cx="55" cy="62" r="1.4"></circle>
              <circle cx="67" cy="66" r="1.2"></circle>
            </g>
            """,
        ["PIXEL_PARTS"] = """
            <g class="lob-pixel-antennae" fill="#d84c3e">
              <rect x="30" y="0" width="6" height="6"></rect>
              <rect x="36" y="6" width="6" height="6"></rect>
              <rect x="42" y="12" width="6" height="6"></rect>
              <rect x="84" y="0" width="6" height="6"></rect>
              <rect x="78" y="6" width="6" height="6"></rect>
              <rect x="72" y="12" width="6" height="6"></rect>
            </g>
            <g class="lob-pixel-claws">
              <path d="M18 42 H6 V48 H0 V60 H6 V66 H18 V60 H24 V48 H18 Z M6 48 H12 V60 H6 Z" fill="#d84c3e"></path>
              <rect x="6" y="60" width="12" height="6" fill="#a83428"></rect>
              <path d="M102 42 H114 V48 H120 V60 H114 V66 H102 V60 H96 V48 H102 Z M108 48 H114 V60 H108 Z" fill="#d84c3e"></path>
              <rect x="102" y="60" width="12" height="6" fill="#a83428"></rect>
            </g>
            <g class="lob-pixel-body" fill="var(--lob-shell, #d84c3e)">
              <rect x="42" y="12" width="36" height="6"></rect>
              <rect x="36" y="18" width="48" height="6"></rect>
              <rect x="30" y="24" width="60" height="6"></rect>
              <rect x="24" y="30" width="72" height="48"></rect>
              <rect x="30" y="78" width="60" height="12"></rect>
              <rect x="36" y="90" width="48" height="6"></rect>
              <rect x="36" y="96" width="12" height="9"></rect>
              <rect x="72" y="96" width="12" height="9"></rect>
            </g>
            <g fill="#ef8f6a">
              <rect x="36" y="24" width="24" height="6"></rect>
              <rect x="30" y="30" width="18" height="12"></rect>
              <rect x="24" y="42" width="12" height="12"></rect>
            </g>
            <g fill="#a83428">
              <rect x="30" y="78" width="60" height="12"></rect>
              <rect x="36" y="90" width="48" height="6"></rect>
              <rect x="36" y="96" width="12" height="9"></rect>
              <rect x="72" y="96" width="12" height="9"></rect>
            </g>
            """,
        ["SPLIT_HALF"] = """
            <path class="lob-split-half" d="M60 8 C88 8 104 32 104 52 C104 72 90 90 76 95 L76 104 L66 104 L66 96 C64 96.8 62 97.1 60 97.1 L60 8 Z" fill="var(--lob-shell2, #46536b)"></path>
            """,
        ["RETRO_MEGA_CLAW"] = """
            <path d="M95 55 C112 53 119 39 116 25 C113 11 99 5 91 12 C88 15 87 19 88 23 C83 27 83 36 88 43 C91 49 93 52 95 55 Z" fill="var(--lob-claw)"></path>
            <path d="M92 14 C97 22 99 31 95 41" class="lob-retro-claw-line" stroke="#b8151b" stroke-width="3" stroke-linecap="round" fill="none"></path>
            """,
        ["RETRO_ANTENNAE"] = """
            <g class="lob-antennae" stroke="var(--lob-shell)" stroke-width="4" stroke-linecap="round" fill="none">
              <path d="M50 16 Q45 4 37 1"></path>
              <path d="M70 16 Q75 4 83 1"></path>
            </g>
            """,
        ["TAIL_FAN"] = """
            <g class="lob-tail">
              <ellipse cx="16" cy="84" rx="11" ry="7" transform="rotate(-32 16 84)"></ellipse>
              <ellipse cx="104" cy="84" rx="11" ry="7" transform="rotate(32 104 84)"></ellipse>
            </g>
            """,
        ["ANTENNAE_SPRITES.perky"] = """
            <g class="lob-antennae" stroke="var(--lob-shell)" stroke-width="4" stroke-linecap="round" fill="none">
              <path d="M46 14 Q38 4 31 7"></path>
              <path d="M74 14 Q82 4 89 7"></path>
            </g>
            """,
        ["ANTENNAE_SPRITES.droopy"] = """
            <g class="lob-antennae" stroke="var(--lob-shell)" stroke-width="4" stroke-linecap="round" fill="none">
              <path d="M46 14 Q36 8 34 18"></path>
              <path d="M74 14 Q84 8 86 18"></path>
            </g>
            """,
        ["FLATPACK_PARTS"] = """
            <g fill="none" stroke="#8a7c5f" stroke-width="1.5" stroke-dasharray="4 3">
              <path d="M27 48 Q50 58 75 61"></path>
              <path d="M68 47 Q79 58 92 70"></path>
            </g>
            <g class="lob-claw lob-claw--l">
              <path d="M76 53 C66 50 62 58 67 65 C72 72 82 68 85 60 C87 55 82 53 76 53 Z" fill="var(--lob-claw)" stroke="#8a7c5f" stroke-width="1.5" stroke-dasharray="4 3"></path>
            </g>
            <g class="lob-claw lob-claw--r">
              <path d="M98 65 C109 62 115 70 111 79 C107 88 96 84 91 76 C88 70 92 66 98 65 Z" fill="var(--lob-claw)" stroke="#8a7c5f" stroke-width="1.5" stroke-dasharray="4 3"></path>
            </g>
            <g fill="var(--lob-shell)" stroke="#8a7c5f" stroke-width="1.5" stroke-dasharray="4 3">
              <rect x="15" y="88" width="15" height="6" rx="2"></rect>
              <rect x="34" y="88" width="15" height="6" rx="2"></rect>
              <rect x="53" y="88" width="15" height="6" rx="2"></rect>
              <rect x="72" y="88" width="15" height="6" rx="2"></rect>
            </g>
            <g fill="none" stroke="#5f5648" stroke-width="3" stroke-linecap="round">
              <path d="M84 24 L101 8"></path>
              <path d="M93 31 L113 14"></path>
            </g>
            <g fill="#8a7c5f">
              <circle cx="105" cy="29" r="3"></circle>
              <circle cx="113" cy="35" r="3"></circle>
            </g>
            <path class="lob-flatpack__allen-key" d="M101 84 L101 99 L115 99" fill="none" stroke="#5f5648" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"></path>
            """,
        ["SKELETON_PARTS"] = """
            <rect class="lob-skeleton__block" style="--i:0" x="24" y="18" width="72" height="64" rx="10" fill="#2f3542"></rect>
            <rect class="lob-skeleton__block" style="--i:1" x="2" y="43" width="24" height="20" rx="8" fill="#39404f"></rect>
            <rect class="lob-skeleton__block" style="--i:2" x="94" y="43" width="24" height="20" rx="8" fill="#39404f"></rect>
            <path class="lob-skeleton__block" style="--i:3" d="M45 21 L34 7" stroke="#39404f" stroke-width="6" stroke-linecap="round"></path>
            <path class="lob-skeleton__block" style="--i:4" d="M75 21 L86 7" stroke="#39404f" stroke-width="6" stroke-linecap="round"></path>
            <rect class="lob-skeleton__block" style="--i:5" x="31" y="79" width="10" height="22" rx="4" fill="#2f3542"></rect>
            <rect class="lob-skeleton__block" style="--i:6" x="46" y="79" width="10" height="24" rx="4" fill="#39404f"></rect>
            <rect class="lob-skeleton__block" style="--i:7" x="64" y="79" width="10" height="24" rx="4" fill="#39404f"></rect>
            <rect class="lob-skeleton__block" style="--i:8" x="79" y="79" width="10" height="22" rx="4" fill="#2f3542"></rect>
            """,
        ["ACTUAL_PARTS"] = """
            <g fill="none" stroke="#6f281d" stroke-linecap="round">
              <path d="M47 27 Q27 5 2 3" stroke-width="2"></path>
              <path d="M73 27 Q93 5 118 3" stroke-width="2"></path>
              <path d="M46 29 Q34 16 22 13" stroke-width="1.3"></path>
              <path d="M74 29 Q86 16 98 13" stroke-width="1.3"></path>
            </g>
            <g fill="none" stroke="#7f3022" stroke-width="2.2" stroke-linecap="round">
              <path d="M39 48 Q22 52 10 65"></path>
              <path d="M38 56 Q20 62 8 77"></path>
              <path d="M40 65 Q24 72 15 88"></path>
              <path d="M43 74 Q31 82 27 97"></path>
              <path d="M81 48 Q98 52 110 65"></path>
              <path d="M82 56 Q100 62 112 77"></path>
              <path d="M80 65 Q96 72 105 88"></path>
              <path d="M77 74 Q89 82 93 97"></path>
            </g>
            <g class="lob-actual__tail" fill="#8f3220" stroke="#6f281d" stroke-width="1.4">
              <path d="M48 88 Q38 91 31 103 Q44 103 55 96 Z"></path>
              <path d="M72 88 Q82 91 89 103 Q76 103 65 96 Z"></path>
              <path d="M54 89 Q60 96 66 89 L69 104 Q60 101 51 104 Z"></path>
            </g>
            <g class="lob-claw lob-claw--l">
              <path d="M34 43 Q25 31 12 33 Q0 35 2 48 Q4 61 17 63 Q28 64 35 54 L30 49 Q22 54 15 50 Q10 47 13 42 Q17 37 24 42 Z" fill="var(--lob-claw)" stroke="#6f281d" stroke-width="1.5"></path>
            </g>
            <g class="lob-claw lob-claw--r">
              <path d="M86 44 Q94 32 104 34 Q113 35 116 42 L107 45 L118 49 Q115 58 105 60 Q96 61 87 53 Z" fill="var(--lob-claw)" stroke="#6f281d" stroke-width="1.5"></path>
            </g>
            <path class="lob-actual__carapace" d="M60 20 Q38 20 34 38 Q32 50 41 58 Q49 64 60 64 Q71 64 79 58 Q88 50 86 38 Q82 20 60 20 Z" fill="var(--lob-shell)" stroke="#6f281d" stroke-width="1.8"></path>
            <g class="lob-actual__abdomen" stroke="#6f281d" stroke-width="1.4">
              <path d="M39 54 Q60 62 81 54 L79 65 Q60 72 41 65 Z" fill="#b34b35"></path>
              <path d="M41 65 Q60 72 79 65 L76 76 Q60 82 44 76 Z" fill="#a63c28"></path>
              <path d="M44 76 Q60 82 76 76 L72 87 Q60 92 48 87 Z" fill="#b34b35"></path>
              <path d="M48 87 Q60 92 72 87 L67 96 Q60 99 53 96 Z" fill="#a63c28"></path>
            </g>
            <path d="M48 42 Q60 48 72 42 Q69 56 60 58 Q51 56 48 42 Z" fill="#bd6044" opacity="0.72"></path>
            <g fill="none" stroke="#6f281d" stroke-width="2.2" stroke-linecap="round">
              <path d="M47 28 L45 23"></path>
              <path d="M73 28 L75 23"></path>
            </g>
            """,
        ["BALLOON_STRING"] = """
            <path d="M55 96 L60 91 L65 96 L60 101 Z" fill="#d94b72"></path>
            <path d="M60 100 Q67 102 62 105 Q57 108 65 111" fill="none" stroke="#c9d2da" stroke-width="1.2" stroke-linecap="round"></path>
            """,
        ["BALLOON_PARTS"] = """
            <g fill="none" stroke="var(--lob-shell)" stroke-width="4" stroke-linecap="round">
              <path d="M48 13 Q45 5 42 3"></path>
              <path d="M72 13 Q75 5 78 3"></path>
            </g>
            <g class="lob-claw lob-claw--l" fill="var(--lob-claw)">
              <ellipse cx="29" cy="29" rx="9" ry="7" transform="rotate(-20 29 29)"></ellipse>
              <ellipse cx="20" cy="35" rx="8" ry="6" transform="rotate(18 20 35)"></ellipse>
            </g>
            <g class="lob-claw lob-claw--r" fill="var(--lob-claw)">
              <ellipse cx="91" cy="29" rx="9" ry="7" transform="rotate(20 91 29)"></ellipse>
              <ellipse cx="100" cy="35" rx="8" ry="6" transform="rotate(-18 100 35)"></ellipse>
            </g>
            <ellipse cx="60" cy="29" rx="24" ry="19" fill="var(--lob-shell)"></ellipse>
            <circle cx="60" cy="48" r="4" fill="#d94b72"></circle>
            <ellipse cx="60" cy="61" rx="18" ry="14" fill="var(--lob-shell)"></ellipse>
            <circle cx="60" cy="75" r="3.5" fill="#d94b72"></circle>
            <ellipse cx="60" cy="86" rx="13" ry="10" fill="var(--lob-shell)"></ellipse>
            <g fill="#ffffff" opacity="0.55">
              <ellipse cx="50" cy="22" rx="4" ry="10" transform="rotate(35 50 22)"></ellipse>
              <ellipse cx="52" cy="56" rx="3" ry="7" transform="rotate(32 52 56)"></ellipse>
              <ellipse cx="54" cy="82" rx="2.4" ry="5" transform="rotate(28 54 82)"></ellipse>
            </g>
            """,
        ["PORTAL_REAR"] = """
            <g class="lob-portal-rear" fill="#b0432f">
              <path d="M91 44 Q82 40 75 44 L82 51 L74 56 L82 61 L75 68 Q83 70 92 64 Z"></path>
              <path d="M88 52 Q82 49 78 45" fill="none" stroke="#7f3022" stroke-width="2"></path>
              <path d="M88 61 Q82 64 78 69" fill="none" stroke="#7f3022" stroke-width="2"></path>
            </g>
            <ellipse class="lob-portal-ring lob-portal-ring--orange" cx="92" cy="58" rx="14" ry="30" fill="#0d1b33" fill-opacity="0.85" stroke="currentColor" stroke-width="3" transform="rotate(10 92 58)"></ellipse>
            """,
        ["PORTAL_FRONT"] = """
            <ellipse class="lob-portal-ring lob-portal-ring--blue" cx="30" cy="50" rx="14" ry="30" fill="#0d1b33" fill-opacity="0.85" stroke="currentColor" stroke-width="3" transform="rotate(-12 30 50)"></ellipse>
            <path d="M31 30 Q45 20 56 30 Q62 39 60 53 Q58 64 47 70 L31 67 Z" fill="#b0432f"></path>
            <g class="lob-claw lob-claw--l">
              <path d="M52 54 Q58 48 63 51 Q67 56 63 62 Q58 67 52 63 Z" fill="#b0432f"></path>
            </g>
            <g fill="none" stroke="#b0432f" stroke-width="2" stroke-linecap="round">
              <path d="M42 31 Q43 16 50 7"></path>
              <path d="M52 28 Q58 14 66 9"></path>
            </g>
            """,
        ["WATERMELON_RIND"] = """
            <g class="lob-watermelon">
              <g fill="none" stroke="#2c7a4a" stroke-width="4" stroke-linecap="round">
                <path d="M31 25 Q39 13 48 11"></path>
                <path d="M48 22 Q54 10 60 9"></path>
                <path d="M66 22 Q72 10 80 15"></path>
                <path d="M82 28 Q89 20 94 30"></path>
              </g>
              <ellipse cx="60" cy="69" rx="35" ry="23" fill="#e5484d"></ellipse>
              <g fill="#28211c">
                <ellipse cx="42" cy="62" rx="2" ry="3.4" transform="rotate(-20 42 62)"></ellipse>
                <ellipse cx="58" cy="58" rx="2" ry="3.4" transform="rotate(8 58 58)"></ellipse>
                <ellipse cx="76" cy="64" rx="2" ry="3.4" transform="rotate(24 76 64)"></ellipse>
                <ellipse cx="49" cy="77" rx="2" ry="3.4" transform="rotate(18 49 77)"></ellipse>
                <ellipse cx="68" cy="79" rx="2" ry="3.4" transform="rotate(-16 68 79)"></ellipse>
                <ellipse cx="84" cy="75" rx="1.8" ry="3.1" transform="rotate(30 84 75)"></ellipse>
              </g>
            </g>
            """,
        ["TINFOIL_FOLDS"] = """
            <g class="lob-tinfoil" fill="none" stroke="#c9d2da" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">
              <path d="M27 31 L38 27 L45 34 L55 25 L64 33 L75 26 L91 34"></path>
              <path d="M20 51 L31 45 L43 54 L55 45 L68 54 L82 44 L100 53"></path>
              <path d="M23 70 L36 64 L47 73 L60 63 L74 72 L90 65 L98 72"></path>
              <path d="M35 86 L46 80 L58 87 L70 79 L84 85"></path>
            </g>
            """,
        ["TINFOIL_HAT"] = """
            <g class="lob-tinfoil-hat">
              <path d="M43 14 L57 0 L78 15 L60 11 Z" fill="#c0c9d1"></path>
              <path d="M57 0 L60 11 L78 15" fill="none" stroke="#7f8992" stroke-width="1.5"></path>
              <path d="M43 14 L60 11 L53 5" fill="none" stroke="#9aa4ad" stroke-width="1.5"></path>
            </g>
            """,
        ["ECLIPSE_CORONA"] = """
            <path class="lob-eclipse-corona" d="M29 31 Q40 10 61 9 Q82 9 94 31" fill="none" stroke="#ffe9b8" stroke-width="2.5" stroke-linecap="round"></path>
            """,
        ["CHIMERA_STITCHES"] = """
            <g class="lob-chimera-stitches" fill="none" stroke="#4a3f3a" stroke-width="1.8" stroke-linecap="round">
              <path d="M18 47 L27 50 M19 51 L22 46 M23 53 L26 48"></path>
              <path d="M93 50 L102 47 M94 48 L97 53 M98 46 L101 51"></path>
            </g>
            """,
        ["LOBSTER_HAT_SPRITES.crown"] = """
            <path d="M46 12 L46 2 L53 8 L60 0 L67 8 L74 2 L74 12 Q60 8 46 12 Z" fill="#f6c945"></path>
            """,
        ["LOBSTER_HAT_SPRITES.santa"] = """
            <g>
              <path d="M47 10 Q54 1 68 3 L72 9 Z" fill="#e0312f"></path>
              <circle cx="71" cy="3.5" r="3.5" fill="#f5f7fa"></circle>
              <ellipse cx="59" cy="10.5" rx="15" ry="3.5" fill="#f5f7fa"></ellipse>
            </g>
            """,
        ["LOBSTER_HAT_SPRITES.pumpkin"] = """
            <g>
              <ellipse cx="60" cy="6.5" rx="8.5" ry="5.5" fill="#e8871e"></ellipse>
              <path d="M56 2.5 Q56 6.5 56 10.5 M64 2.5 Q64 6.5 64 10.5" stroke="#c96a10" stroke-width="1.5" fill="none"></path>
              <path d="M60 1.5 Q60.5 0 63 0.5" stroke="#4c9a4c" stroke-width="2.5" stroke-linecap="round" fill="none"></path>
            </g>
            """,
        ["LOBSTER_HAT_SPRITES.party"] = """
            <g>
              <path d="M52 11 L60 0.5 L68 11 Z" fill="#7c5cff"></path>
              <path d="M55.5 6.5 L64.5 6.5" stroke="#ffd166" stroke-width="2"></path>
              <circle cx="60" cy="1" r="2.4" fill="#ff5c8a"></circle>
            </g>
            """,
    };
}
