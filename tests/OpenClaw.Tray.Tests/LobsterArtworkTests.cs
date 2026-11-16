using OpenClawTray.Chat.LobsterVisits;

namespace OpenClaw.Tray.Tests;

public sealed class LobsterArtworkTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static LobsterLook Look(string palette = "crimson") =>
        LobsterLooks.Create(7, Now.DateTime) with
        {
            Palette = LobsterLooks.Palette(palette),
            Accessory = LobsterAccessory.None,
            ClawSize = LobsterClawSize.Regular,
            CrusherSide = null,
            Freckles = false,
            Glint = null,
            ChimeraParts = null,
            TailFan = false,
        };

    private static LobsterArtwork Compose(LobsterLook look, bool offline = false, bool grumpy = false) =>
        LobsterArtwork.Compose(look, grumpy, offline, Now, "#123456", "#654321");

    private static IEnumerable<LobsterArtNode> All(LobsterArtNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(All));

    private static IEnumerable<LobsterArtNode> Leaves(LobsterArtwork art) =>
        All(art.Root).Where(n => n.Element != LobsterSvgElement.Group);

    [Fact]
    public void PathParser_ResolvesRelativeImplicitAndCompactNumbers()
    {
        var segments = LobsterSvg.ParsePath("m10 20 5 5h-3v2l1.5.5-1-2zM0 0c1 1 2 2 3 3s1 1 2 2");

        Assert.Equal((10d, 20d), (segments[0].X, segments[0].Y));
        Assert.Equal(LobsterPathCommand.Move, segments[0].Command);
        // Implicit repeat after m is a relative lineto.
        Assert.Equal((LobsterPathCommand.Line, 15d, 25d), (segments[1].Command, segments[1].X, segments[1].Y));
        Assert.Equal((12d, 25d), (segments[2].X, segments[2].Y));
        Assert.Equal((12d, 27d), (segments[3].X, segments[3].Y));
        Assert.Equal((13.5d, 27.5d), (segments[4].X, segments[4].Y));
        Assert.Equal((12.5d, 25.5d), (segments[5].X, segments[5].Y));
        Assert.Equal((LobsterPathCommand.Close, 10d, 20d), (segments[6].Command, segments[6].X, segments[6].Y));
        var smooth = segments[^1];
        Assert.Equal(LobsterPathCommand.Cubic, smooth.Command);
        // S reflects the previous second control point (2,2) about the current point (3,3).
        Assert.Equal((4d, 4d, 4d, 4d, 5d, 5d), (smooth.X1, smooth.Y1, smooth.X2, smooth.Y2, smooth.X, smooth.Y));
    }

    [Fact]
    public void PathParser_ReadsArcFlagsWithoutSeparatorsAndQuadraticReflection()
    {
        var segments = LobsterSvg.ParsePath("M0 0a5 5 0 105 5Q10 0 20 0t10 0");

        var arc = segments[1];
        Assert.Equal(LobsterPathCommand.Arc, arc.Command);
        Assert.True(arc.LargeArc);
        Assert.False(arc.Sweep);
        Assert.Equal((5d, 5d, 5d, 5d), (arc.Rx, arc.Ry, arc.X, arc.Y));
        var reflected = segments[^1];
        Assert.Equal(LobsterPathCommand.Quadratic, reflected.Command);
        Assert.Equal((30d, 0d, 30d, 0d), (reflected.X1, reflected.Y1, reflected.X, reflected.Y));
    }

    [Fact]
    public void EveryVendoredSprite_ParsesWithinTheSubset()
    {
        var markup = LobsterSprites.Markup.Values
            .Concat([LobsterSprites.StandardDome, LobsterSprites.LeftClaw, LobsterSprites.RightClaw, LobsterSprites.Highlight,
                LobsterSprites.EyesOpen, LobsterSprites.EyesClosed, LobsterSprites.FlatpackBody, LobsterSprites.EntryBalloon,
                LobsterSprites.Broom, LobsterSprites.BrowedFace(LobsterSprites.GrumpyMouth), LobsterSprites.NotextureChecker()])
            .Concat(LobsterSprites.GeometryEyes.Values.SelectMany(e => new[] { e.Open, e.Closed }))
            .Concat(Enumerable.Range(0, 8).Select(LobsterSprites.SeleneMoon));

        foreach (var fragment in markup)
        {
            var nodes = LobsterSvg.ParseFragment(fragment);
            Assert.NotEmpty(nodes);
            foreach (var node in nodes)
                Geometry(node);
        }

        static void Geometry(LobsterSvgNode node)
        {
            if (node.Element != LobsterSvgElement.Group)
                Assert.NotEmpty(LobsterSvg.Geometry(node));
            foreach (var child in node.Children)
                Geometry(child);
        }
    }

    [Fact]
    public void EveryPaletteAndAccessory_ComposesWithRoles()
    {
        foreach (var palette in LobsterLooks.PaletteWeights.Select(p => p.Value.Id))
        {
            foreach (var accessory in Enum.GetValues<LobsterAccessory>())
            {
                var look = Look(palette) with { Accessory = accessory, Freckles = true, TailFan = true };
                if (palette == "chimera")
                    look = look with { ChimeraParts = new("#ff4f40", "#4a7dfc", "#f4b840", "#222222") };
                var art = Compose(look, grumpy: accessory == LobsterAccessory.Monocle);
                Assert.True(art.Root.Find(LobsterArtRole.ClawLeft).Count() <= 1, $"{palette}/{accessory} left");
                Assert.True(art.Root.Find(LobsterArtRole.ClawRight).Count() <= 1, $"{palette}/{accessory} right");
                Assert.True(art.Root.Find(LobsterArtRole.EyesOpen).Any(), $"{palette}/{accessory} eyes");
            }
        }
    }

    [Fact]
    public void Paint_ResolvesNestedVarsFallbacksAndColorMix()
    {
        var vars = new Dictionary<string, string> { ["--a"] = "var(--b)", ["--b"] = "#ff0000", ["--c"] = "rgba(0, 0, 255, 0.5)" };

        Assert.Equal("#ff0000", LobsterPaint.Resolve("var(--a)", vars)?.ToString());
        Assert.Equal("#00ff00", LobsterPaint.Resolve("var(--missing, var(--also-missing, #0f0))", vars)?.ToString());
        Assert.Equal("#0000ff80", LobsterPaint.Resolve("var(--c)", vars)?.ToString());
        Assert.Equal("#bf0040", LobsterPaint.Resolve("color-mix(in srgb, var(--b) 75%, #0000ff)", vars)?.ToString());
        Assert.Null(LobsterPaint.Resolve("none", vars));
        Assert.Null(LobsterPaint.Resolve("var(--missing)", vars));
    }

    [Fact]
    public void MoodPalette_PaintsTheRendererAccent()
    {
        var shell = Leaves(Compose(Look("mood"))).Select(n => n.Fill?.ToString());

        Assert.Contains("#123456", shell);
    }

    [Fact]
    public void OfflineGlint_OverridesTheSeededGlint()
    {
        static IEnumerable<string?> Glints(LobsterArtwork art) =>
            art.Root.Find(LobsterArtRole.EyesOpen).SelectMany(All).Select(n => n.Fill?.ToString());

        Assert.Contains("#00e5cc", Glints(Compose(Look())));
        Assert.Contains("#8a949d", Glints(Compose(Look() with { Glint = "#ff00ff" }, offline: true)));
        Assert.DoesNotContain("#ff00ff", Glints(Compose(Look() with { Glint = "#ff00ff" }, offline: true)));
    }

    [Fact]
    public void Chimera_DonorColoursReachEachClaw()
    {
        var look = Look("chimera") with { ChimeraParts = new("#ff4f40", "#4a7dfc", "#f4b840", "#222222") };
        var art = Compose(look);

        Assert.Contains("#4a7dfc", art.Root.Find(LobsterArtRole.ClawLeft).SelectMany(All).Select(n => n.Fill?.ToString()));
        Assert.Contains("#f4b840", art.Root.Find(LobsterArtRole.ClawRight).SelectMany(All).Select(n => n.Fill?.ToString()));
        Assert.Contains("#222222", art.Root.Find(LobsterArtRole.Antennae).SelectMany(All).Select(n => n.Stroke?.ToString()));
    }

    [Fact]
    public void Crusher_ScalesEachClawAroundItsOrigin()
    {
        var art = Compose(Look() with { CrusherSide = LobsterSide.Left });

        var left = art.Root.Find(LobsterArtRole.ClawLeft).Single().Children.First(c => c.Element == LobsterSvgElement.Path).Transform;
        var right = art.Root.Find(LobsterArtRole.ClawRight).Single().Children.First(c => c.Element == LobsterSvgElement.Path).Transform;
        Assert.Equal(1.18, left.A, 6);
        Assert.Equal(.85, right.A, 6);
        var (lx, ly) = left.Apply(25, 52);
        var (rx, ry) = right.Apply(95, 52);
        Assert.Equal((25d, 52d), (Math.Round(lx, 6), Math.Round(ly, 6)));
        Assert.Equal((95d, 52d), (Math.Round(rx, 6), Math.Round(ry, 6)));
    }

    [Fact]
    public void Freckles_AreSuppressedWhenThePaletteHasAPattern()
    {
        Assert.True(Leaves(Compose(Look() with { Freckles = true })).Count() > Leaves(Compose(Look())).Count());
        Assert.Equal(Leaves(Compose(Look("bee"))).Count(), Leaves(Compose(Look("bee") with { Freckles = true })).Count());
    }

    [Fact]
    public void InvisiblePalette_HasNoHighlight()
    {
        static bool HasHighlight(LobsterArtwork art) =>
            Leaves(art).Any(n => n.Element == LobsterSvgElement.Ellipse && n.Fill?.ToString() == "#ffffff" && Math.Abs(n.Opacity - .1) < 1e-9);

        Assert.True(HasHighlight(Compose(Look())));
        Assert.False(HasHighlight(Compose(Look("invisible"))));
    }

    [Fact]
    public void Accessory_NoneAddsNothingWhileHeadwearAddsAGroup()
    {
        Assert.Empty(Compose(Look()).Root.Find(LobsterArtRole.Accessory));
        Assert.NotEmpty(Compose(Look() with { Accessory = LobsterAccessory.Crown }).Root.Find(LobsterArtRole.Accessory));
    }

    [Fact]
    public void ClosedEyes_StartHidden()
    {
        Assert.All(Compose(Look()).Root.Find(LobsterArtRole.EyesClosed), n => Assert.Equal(0, n.Opacity));
    }

    [Fact]
    public void BreatheDelay_IsAStableNegativeOffsetWithinTheCycle()
    {
        var delays = LobsterLooks.PaletteWeights.Select(p => LobsterLooks.BreatheDelaySeconds(Look(p.Value.Id))).ToArray();

        Assert.All(delays, d => Assert.InRange(d, -3.3, 0));
        Assert.True(delays.Distinct().Count() > 10);
    }
}
