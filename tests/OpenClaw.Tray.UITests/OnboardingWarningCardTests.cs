using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OpenClawTray.FunctionalUI;
using OpenClawTray.FunctionalUI.Hosting;
using OpenClawTray.Onboarding.Pages;
using OpenClawTray.Onboarding.Services;
using OpenClawTray.Services;
using static OpenClawTray.FunctionalUI.Factories;
using static OpenClawTray.FunctionalUI.ElementExtensions;

namespace OpenClaw.Tray.UITests;

/// <summary>
/// Regression coverage for PR #257 (PR 241 follow-up).
///
/// PR 241 introduced two yellow warning cards on the Welcome and Ready pages.
/// They use a hard-coded yellow background but inherited the system theme
/// foreground, which renders as white in dark mode → unreadable. The fix sets
/// an explicit dark-brown <c>#3D2A0F</c> foreground on every TextBlock inside
/// those cards.
///
/// These tests exercise:
///   1. The new <c>Foreground</c> modifier on FunctionalUI propagates to the
///      rendered <see cref="TextBlock"/>.
///   2. The Welcome page security-notice card has explicit dark text on every
///      non-emoji TextBlock.
///   3. The Ready page node-mode card has explicit dark text on every non-emoji
///      TextBlock when node mode is enabled.
///
/// We assert at the visual-tree level rather than via a screenshot baseline:
/// the bug is "this brush is missing", which a brush comparison expresses
/// precisely without the maintenance cost of pixel diffs.
/// </summary>
[Collection(UICollection.Name)]
public sealed class OnboardingWarningCardTests
{
    private const uint ExpectedDarkText = 0xFF3D2A0FU; // ARGB
    private const uint WelcomeYellow   = 0xFFFFF4E0U;
    private const uint ReadyYellow     = 0xFFFFF3E0U;

    private readonly UIThreadFixture _ui;
    public OnboardingWarningCardTests(UIThreadFixture ui) => _ui = ui;

    [Fact]
    public async Task ForegroundModifier_AppliesExplicitBrushToTextBlock()
    {
        await _ui.ResetContainerAsync();

        var host = await _ui.RunOnUIAsync(() => Task.FromResult(MountFunctional(
            _ => TextBlock("hello").Foreground("#3D2A0F"))));

        // FunctionalHostControl renders via DispatcherQueue.TryEnqueue, so the
        // first call returns before Render() runs. A second RunOnUIAsync is
        // serialized after that queued render, so by the time it executes the
        // visual tree is built.
        await _ui.RunOnUIAsync(() =>
        {
            _ui.Container.UpdateLayout();
            var tb = FindLogicalDescendants<TextBlock>(host)
                .Single(t => t.Text == "hello");
            AssertSolidColor(tb.Foreground, ExpectedDarkText, "TextBlock.Foreground");
        });
    }

    [Fact]
    public async Task WelcomePage_WarningCard_HasExplicitDarkForegroundOnAllText()
    {
        await _ui.ResetContainerAsync();

        var host = await _ui.RunOnUIAsync(() => Task.FromResult(MountComponent(new WelcomePage())));

        await _ui.RunOnUIAsync(() =>
        {
            _ui.Container.UpdateLayout();
            var yellowBorder = FindLogicalDescendants<Border>(host)
                .Single(b => IsSolidColor(b.Background, WelcomeYellow));
            AssertNonEmojiTextHasForeground(yellowBorder, ExpectedDarkText);
        });
    }

    [Fact]
    public async Task ReadyPage_NodeModeCard_HasExplicitDarkForegroundOnAllText()
    {
        // Isolate SettingsManager from the real %APPDATA%\OpenClawTray
        // (per AGENTS.md: never use `new SettingsManager()` against real settings).
        var tempDir = Path.Combine(Path.GetTempPath(), "openclaw-uitests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            await _ui.ResetContainerAsync();

            var settings = new SettingsManager(tempDir);
            settings.EnableNodeMode = true; // forces ModeInfoCard() into the yellow node-mode branch
            using var state = new OnboardingState(settings)
            {
                Mode = ConnectionMode.Local,
            };

            var host = await _ui.RunOnUIAsync(() => Task.FromResult(MountFunctional(
                _ => Component<ReadyPage, OnboardingState>(state))));

            await _ui.RunOnUIAsync(() =>
            {
                _ui.Container.UpdateLayout();
                var yellowBorder = FindLogicalDescendants<Border>(host)
                    .Single(b => IsSolidColor(b.Background, ReadyYellow));
                AssertNonEmojiTextHasForeground(yellowBorder, ExpectedDarkText);
            });
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>Construct + mount a FunctionalHostControl on the UI thread.</summary>
    private FunctionalHostControl MountFunctional(System.Func<OpenClawTray.FunctionalUI.Core.RenderContext, OpenClawTray.FunctionalUI.Core.Element> render)
    {
        var host = new FunctionalHostControl();
        _ui.Container.Children.Add(host);
        host.Mount(render);
        return host;
    }

    /// <summary>Construct + mount a Component on a FunctionalHostControl on the UI thread.</summary>
    private FunctionalHostControl MountComponent(OpenClawTray.FunctionalUI.Core.Component component)
    {
        var host = new FunctionalHostControl();
        _ui.Container.Children.Add(host);
        host.Mount(component);
        return host;
    }

    // ---------- helpers ----------

    /// <summary>
    /// Walks the FunctionalUI logical tree (StackPanel/Border children, no XamlRoot
    /// required) collecting every descendant of the requested type.
    /// </summary>
    private static System.Collections.Generic.IEnumerable<T> FindLogicalDescendants<T>(
        Microsoft.UI.Xaml.DependencyObject root)
        where T : Microsoft.UI.Xaml.DependencyObject
    {
        if (root is T self) yield return self;

        switch (root)
        {
            case Panel panel:
                foreach (var c in panel.Children)
                    foreach (var d in FindLogicalDescendants<T>(c)) yield return d;
                break;
            case Border border when border.Child is Microsoft.UI.Xaml.FrameworkElement bc:
                foreach (var d in FindLogicalDescendants<T>(bc)) yield return d;
                break;
            case ContentControl cc when cc.Content is Microsoft.UI.Xaml.FrameworkElement cf:
                foreach (var d in FindLogicalDescendants<T>(cf)) yield return d;
                break;
        }
    }

    private static bool IsSolidColor(Brush? brush, uint argb) =>
        brush is SolidColorBrush scb && ColorToArgb(scb.Color) == argb;

    private static uint ColorToArgb(Windows.UI.Color c) =>
        ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private static void AssertSolidColor(Brush? brush, uint argb, string what)
    {
        Assert.NotNull(brush);
        var scb = Assert.IsType<SolidColorBrush>(brush);
        Assert.Equal(argb, ColorToArgb(scb.Color));
        _ = what;
    }

    /// <summary>
    /// Asserts that every <see cref="TextBlock"/> under <paramref name="card"/>
    /// whose text is a non-emoji string has the expected explicit foreground.
    /// Emoji TextBlocks are skipped because color emoji glyphs render from their
    /// own palette and ignore the foreground brush — explicitly coloring them
    /// would have no visible effect.
    /// </summary>
    private static void AssertNonEmojiTextHasForeground(Border card, uint expectedArgb)
    {
        var nonEmojiTexts = FindLogicalDescendants<TextBlock>(card)
            .Where(tb => !string.IsNullOrEmpty(tb.Text) && !IsEmojiOnly(tb.Text))
            .ToList();

        Assert.NotEmpty(nonEmojiTexts);
        foreach (var tb in nonEmojiTexts)
        {
            AssertSolidColor(tb.Foreground, expectedArgb, $"text \"{tb.Text}\"");
        }
    }

    /// <summary>
    /// Returns true if every code point in <paramref name="s"/> is outside the
    /// Basic Latin / Latin-1 range and either a Symbol or above U+1F000 (emoji
    /// plane). Used to skip warning-icon TextBlocks like "⚠️" and "🔌•".
    /// </summary>
    private static bool IsEmojiOnly(string s)
    {
        foreach (var rune in s.EnumerateRunes())
        {
            // Bullet character "•" (U+2022) is treated as text, not emoji.
            if (rune.Value == 0x2022) return false;
            // Latin / digit / whitespace → not emoji.
            if (rune.Value < 0x2300 && !char.IsSymbol((char)rune.Value)) return false;
        }
        return s.Length > 0;
    }
}
