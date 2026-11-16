// Proves the chat's theme artwork layer: it paints on its first mount (not only after a later update), uses the
// theme's artwork for the element's own mode at the chosen strength, fades on the new-chat surface, and draws nothing
// for the Windows default look, under High Contrast, or with transparency effects off, re-rendering open chats when those
// effects are turned off or on.
// Run with dotnet test tests\OpenClaw.Tray.UITests -r win-x64 --filter FullyQualifiedName~GatewayThemeBackgroundTests.
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenClaw.Shared.Themes;
using OpenClawTray.Chat;

namespace OpenClaw.Tray.UITests;

[Collection(UICollection.Name)]
public sealed class GatewayThemeBackgroundTests(UIThreadFixture ui)
{
    private ReactorHostControl? _host;

    // The chat runs in the App theme, which may be the opposite of Windows' mode (the app's own), so the artwork pair
    // and the fade follow the element's theme, known only once it is loaded.
    [Fact]
    public async Task ArtworkPaintsOnFirstMountAndYieldsToWindowsDefaultAndHighContrast()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var highContrast = ChatThemeChanges.IsHighContrast;
            var containerTheme = ui.Container.RequestedTheme;
            var manuscript = BuiltinGatewayThemes.All.Single(theme => theme.Id == "manuscript");
            var fullBleed = BackgroundPreference.Default with { Presentation = BackgroundPresentation.FullBleed, Visibility = 1 };
            // With Windows transparency effects off the Control UI draws no artwork either.
            var effects = new global::Windows.UI.ViewManagement.UISettings().AdvancedEffectsEnabled;
            var (chatTheme, chatMode, variant) = Application.Current.RequestedTheme == ApplicationTheme.Dark
                ? (ElementTheme.Light, ThemeColorMode.Light, "light")
                : (ElementTheme.Dark, ThemeColorMode.Dark, "dark");
            try
            {
                ChatThemeChanges.IsHighContrast = static () => false;
                ui.Container.RequestedTheme = chatTheme;
                ChatBackgroundArtwork.Set(manuscript, fullBleed);
                var conversation = await MountAsync(ThemeBackgroundSurface.Conversation);
                if (!effects)
                {
                    Assert.Null(conversation);
                    return;
                }
                Assert.Equal(chatTheme, conversation!.ActualTheme);
                var image = Assert.IsType<Border>(conversation.Child);
                var brush = Assert.IsType<ImageBrush>(image.Background);
                Assert.Equal(0.7, brush.Opacity, 3);
                Assert.EndsWith($"/manuscript-{variant}.png", Assert.IsType<BitmapImage>(brush.ImageSource).UriSource.AbsoluteUri);

                ChatBackgroundArtwork.Set(manuscript, BackgroundPreference.Default);
                var newSession = await MountAsync(ThemeBackgroundSurface.NewSession);
                var faded = Assert.IsType<Grid>(newSession!.Child);
                Assert.Equal(2, faded.Children.Count);
                var fade = Assert.IsType<LinearGradientBrush>(Assert.IsType<Border>(faded.Children[1]).Background);
                var shell = manuscript.PaletteFor(chatMode).Shell;
                Assert.All(fade.GradientStops, stop => Assert.Equal((shell.R, shell.G, shell.B), (stop.Color.R, stop.Color.G, stop.Color.B)));

                ChatBackgroundArtwork.Set(null, fullBleed);
                Assert.Null(await MountAsync(ThemeBackgroundSurface.Conversation));

                ChatThemeChanges.IsHighContrast = static () => true;
                ChatBackgroundArtwork.Set(manuscript, fullBleed);
                Assert.Null(await MountAsync(ThemeBackgroundSurface.Conversation));
            }
            finally
            {
                ChatThemeChanges.IsHighContrast = highContrast;
                ChatBackgroundArtwork.Set(null, null);
                _host?.Dispose();
                ui.Container.Children.Clear();
                ui.Container.RequestedTheme = containerTheme;
            }
        });
    }

    // Turning Windows transparency effects off or on while a chat is open re-renders it without, then with, the artwork.
    // Windows default has no artwork, so the change re-renders nothing there.
    [Fact]
    public async Task TransparencyEffectsChangesReRenderOpenChats()
    {
        await ui.ResetContainerAsync();
        await ui.RunOnUIAsync(async () =>
        {
            var highContrast = ChatThemeChanges.IsHighContrast;
            var transparency = ChatBackgroundArtwork.TransparencyEffectsEnabled;
            var manuscript = BuiltinGatewayThemes.All.Single(theme => theme.Id == "manuscript");
            var fullBleed = BackgroundPreference.Default with { Presentation = BackgroundPresentation.FullBleed, Visibility = 1 };
            var effects = true;
            long? notified = null;
            void OnChanged() => notified = ChatBackgroundArtwork.Revision;
            ChatBackgroundArtwork.Changed += OnChanged;
            try
            {
                ChatThemeChanges.IsHighContrast = static () => false;
                ChatBackgroundArtwork.TransparencyEffectsEnabled = () => effects;
                ChatBackgroundArtwork.Set(manuscript, fullBleed);
                Assert.NotNull(await MountAsync(ThemeBackgroundSurface.Conversation));

                effects = false;
                var revision = ChatBackgroundArtwork.Revision;
                ChatBackgroundArtwork.OnTransparencyEffectsChanged();
                Assert.Equal(revision + 1, notified);
                Assert.Null(await MountAsync(ThemeBackgroundSurface.Conversation));

                effects = true;
                ChatBackgroundArtwork.OnTransparencyEffectsChanged();
                Assert.Equal(revision + 2, notified);
                Assert.NotNull(await MountAsync(ThemeBackgroundSurface.Conversation));

                ChatBackgroundArtwork.Set(null, fullBleed);
                notified = null;
                ChatBackgroundArtwork.OnTransparencyEffectsChanged();
                Assert.Null(notified);
            }
            finally
            {
                ChatBackgroundArtwork.Changed -= OnChanged;
                ChatBackgroundArtwork.TransparencyEffectsEnabled = transparency;
                ChatThemeChanges.IsHighContrast = highContrast;
                ChatBackgroundArtwork.Set(null, null);
                _host?.Dispose();
                ui.Container.Children.Clear();
            }
        });
    }

    // Mounts the artwork for surface alone and returns its layer once rendered, or null when nothing is drawn.
    private async Task<Border?> MountAsync(ThemeBackgroundSurface surface)
    {
        ui.Container.Children.Clear();
        _host?.Dispose();
        var host = _host = new ReactorHostControl();
        host.Mount(_ => ChatBackgroundArtwork.Render(surface));
        ui.Container.Children.Add(new Border { Child = host, Width = 480, Height = 320 });
        if (ChatBackgroundArtwork.OpacityFor(surface) <= 0)
        {
            await TestSupport.WaitForRenderedConditionAsync(() => host.IsLoaded, "Empty artwork host loaded");
            Assert.DoesNotContain(TestSupport.FindDescendants<Border>(host), IsArtwork);
            return null;
        }
        Border? layer = null;
        await TestSupport.WaitForRenderedConditionAsync(
            () => (layer = TestSupport.FindDescendants<Border>(host).FirstOrDefault(IsArtwork)) is { IsLoaded: true },
            "Artwork layer mounted");
        return layer;
    }

    private static bool IsArtwork(Border border) => AutomationProperties.GetAutomationId(border) == "ChatBackgroundArtwork";
}
