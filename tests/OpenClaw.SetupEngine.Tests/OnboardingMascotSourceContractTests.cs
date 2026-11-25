namespace OpenClaw.SetupEngine.Tests;

/// <summary>Retire these lifecycle wiring guards when an in-process WinUI control test host covers them.</summary>
public sealed class OnboardingMascotSourceContractTests
{
    [Fact]
    public void Control_UsesNativePlayerWithoutManagedFrameUpdates()
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
        Assert.Contains("AnimatedVisualPlayer", source);
        Assert.Contains("_player.PlayAsync", source);
        Assert.Contains("_player.SetProgress", source);
        Assert.DoesNotContain("DispatcherTimer", source);
        Assert.DoesNotContain("CompositionTarget.Rendering", source);
        Assert.DoesNotContain("OnboardingMascotDrawing", source);
        var visual = File.ReadAllText(RepoPath("src", "OpenClaw.SetupEngine.UI", "Controls", "OnboardingMascotVisual.cs"));
        Assert.DoesNotContain("DispatcherTimer", visual);
        Assert.DoesNotContain("CompositionTarget.Rendering", visual);
        Assert.DoesNotContain("Lottie", visual);
        Assert.Contains("clock.Progress", visual);
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
        Assert.Contains("_player.Stop();", source);
        Assert.Contains("_player.Source = null;", source);
        Assert.Contains("_playback.SetActive(false);", source);
        Assert.Contains("_heroGlow?.Dispose();", source);
    }

    [Fact]
    public void Glow_CapturesTheActualArtworkAndUsesPinnedThemeConstants()
    {
        var source = File.ReadAllText(RepoPath("src", "OpenClaw.SetupEngine.UI", "Controls", "OnboardingMascotGlow.cs"));
        Assert.Contains("GetElementVisual(artwork)", source);
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
        // The companion pane owns the mascot; pages request headwear and moods through it.
        Assert.Contains("OnboardingMascotAccessory.GradCap", File.ReadAllText(Path.Combine(pages, "CompletePage.xaml.cs")));
        Assert.Contains("nameof(SecurityNoticePage) => new(\"Onboarding_Bubble_Welcome\", OnboardingMascotMood.Idle)",
            File.ReadAllText(RepoPath("src", "OpenClaw.SetupEngine.UI", "SetupCompanionPresentation.cs")));
        Assert.Contains("ReadinessError.IsOpen ? OnboardingMascotMood.Sad : OnboardingMascotMood.Happy",
            File.ReadAllText(Path.Combine(pages, "WelcomePage.xaml.cs")));
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
