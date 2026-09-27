namespace OpenClaw.SetupEngine.Tests;

/// <summary>Retire these lifecycle wiring guards when an in-process WinUI control test host covers them.</summary>
public sealed class OnboardingMascotSourceContractTests
{
    [Fact]
    public void Control_ExposesNativeMoodAndNeverStartsAConstructorTimer()
    {
        var source = Control();
        Assert.Contains("typeof(OnboardingMascotMood), typeof(OnboardingMascot)", source);
        Assert.Contains("public OnboardingMascotMood Mood", source);
        Assert.Contains("public bool IsAnimationEnabled", source);
        Assert.Contains("IsTabStop = false;", source);
        Assert.Contains("PointerMoved += OnPointerMoved;", source);
        Assert.Contains("Tapped += OnTapped;", source);
        Assert.Contains("if (!CanAnimate || !IsInteractive)", source);
        Assert.Contains("AccessibilityView.Raw", source);
        var constructor = source[source.IndexOf("public OnboardingMascot()", StringComparison.Ordinal)..
            source.IndexOf("public void SetPointerGaze", StringComparison.Ordinal)];
        Assert.DoesNotContain("new DispatcherTimer", constructor);
        Assert.DoesNotContain(".Start()", constructor);
        Assert.Contains("FramesPerSecond", source);
    }

    [Fact]
    public void Control_StopsForReducedMotionUnloadAncestorAndHostVisibility()
    {
        var source = Control();
        Assert.Contains("_uiSettings?.AnimationsEnabled == true", source);
        Assert.Contains("_root?.IsHostVisible == true", source);
        Assert.Contains("subscription.Element.Visibility == Visibility.Visible", source);
        Assert.Contains("EffectiveViewportChanged += OnEffectiveViewportChanged;", source);
        Assert.Contains("AnimationsEnabledChanged += OnAnimationsEnabledChanged;", source);
        Assert.Contains("AnimationsEnabledChanged -= OnAnimationsEnabledChanged;", source);
        Assert.Contains("ColorValuesChanged += OnColorsChanged;", source);
        Assert.Contains("ColorValuesChanged -= OnColorsChanged;", source);
        Assert.DoesNotContain(".HighContrastChanged +=", source);
        Assert.Contains("_root.Changed -= OnRootChanged;", source);
        Assert.Contains("UnregisterPropertyChangedCallback(VisibilityProperty, token)", source);
        Assert.Contains("_timer.Tick -= OnTick;", source);
        Assert.Contains("_timer.Stop();", source);
        Assert.Contains("_clock.Reset();", source);
        Assert.Contains("_animator.Advance(elapsed, CanAnimate)", source);
        Assert.Contains("_heroGlow?.Dispose();", source);
    }

    [Fact]
    public void Glow_CapturesTheActualArtworkAndUsesPinnedThemeConstants()
    {
        var source = File.ReadAllText(RepoPath("src", "OpenClaw.SetupEngine.UI", "Controls", "OnboardingMascotGlow.cs"));
        Assert.Contains("GetElementVisual(drawing.Artwork)", source);
        Assert.Contains("CreateVisualSurface()", source);
        Assert.Contains("_shadow.Mask = _mask", source);
        Assert.Contains("_shadow.BlurRadius = 12", source);
        Assert.Contains("239, 75, 88", source);
        Assert.Contains("255, 77, 77", source);
        Assert.Contains("light ? 0.2f : 0.4f", source);
        Assert.Contains("_visual.IsVisible = !highContrast", source);
        Assert.Contains("_surface.SourceVisual = null", source);
        Assert.Contains("_surface.Dispose()", source);
        Assert.DoesNotContain("Ellipse", source);
        Assert.DoesNotContain("Bitmap", source);
    }

    [Fact]
    public void PageHeroes_UseSharedOpticalSizeAndCompletionHeadwear()
    {
        var pages = RepoPath("src", "OpenClaw.SetupEngine.UI", "Pages");
        foreach (var file in Directory.EnumerateFiles(pages, "*.xaml"))
        {
            var document = System.Xml.Linq.XDocument.Load(file);
            foreach (var mascot in document.Descendants().Where(element => element.Name.LocalName == "OnboardingMascot"))
            {
                Assert.Null(mascot.Attribute("Width"));
                Assert.Null(mascot.Attribute("Height"));
            }
        }
        var match = System.Text.RegularExpressions.Regex.Match(Control(), @"public const double HeroSize = (\d+)");
        Assert.True(match.Success);
        var frame = int.Parse(match.Groups[1].Value);
        Assert.Equal(0, frame % 4);
        Assert.InRange(frame * 120d / 168, 124, 132);
        Assert.Contains("Accessory=\"GradCap\"", File.ReadAllText(Path.Combine(pages, "CompletePage.xaml")));
        Assert.Contains("Mood=\"Idle\"", File.ReadAllText(Path.Combine(pages, "SecurityNoticePage.xaml")));
        Assert.Contains("ReadinessError.IsOpen ? OnboardingMascotMood.Sad : OnboardingMascotMood.Happy",
            File.ReadAllText(Path.Combine(pages, "WelcomePage.xaml.cs")));
    }

    [Fact]
    public void Drawing_PreservesCanonicalBodyGeometryAndNativeVectorParts()
    {
        var source = File.ReadAllText(RepoPath("src", "OpenClaw.SetupEngine.UI", "Controls", "OnboardingMascotDrawing.cs"));
        Assert.Contains("Curve(30, 10, 15, 35, 15, 55)", source);
        Assert.Contains("Curve(55, 100, 60, 102, 65, 100)", source);
        Assert.Contains("Curve(105, 35, 90, 10, 60, 10)", source);
        Assert.Contains("CenterX = 26, CenterY = 53", source);
        Assert.Contains("CenterX = 94, CenterY = 53", source);
        Assert.Contains("Quad(35, 5, 30, 8)", source);
        Assert.Contains("Quad(85, 5, 90, 8)", source);
        Assert.Contains("Rgb(255, 112, 121)", source);
        Assert.Contains("Rgb(153, 27, 27)", source);
        Assert.Contains("UIElementType.WindowText", source);
        Assert.DoesNotContain("BitmapImage", source);
        Assert.DoesNotContain("XamlReader", source);
    }

    [Fact]
    public void Drawing_FrameChangesDoNotReparentGeometryAcrossParticles()
    {
        var source = File.ReadAllText(RepoPath("src", "OpenClaw.SetupEngine.UI", "Controls", "OnboardingMascotDrawing.cs"));
        var render = source[source.IndexOf("public void Render(", StringComparison.Ordinal)..
            source.IndexOf("private Eye AddEye(", StringComparison.Ordinal)];
        Assert.DoesNotContain(".Data =", render);
        Assert.DoesNotContain("new PathGeometry", render);
        Assert.Contains("_hearts[i].Opacity", render);
        Assert.Contains("_particles[i].Opacity", render);
        Assert.Contains("_hearts[i] = heart", source);
    }

    internal static string RepoPath(params string[] segments)
    {
        var root = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "openclaw-windows-node.slnx")))
                directory = directory.Parent;
            root = directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
        }
        return Path.Combine([root, .. segments]);
    }

    private static string Control() => File.ReadAllText(RepoPath("src", "OpenClaw.SetupEngine.UI", "Controls", "OnboardingMascot.cs"));
}
