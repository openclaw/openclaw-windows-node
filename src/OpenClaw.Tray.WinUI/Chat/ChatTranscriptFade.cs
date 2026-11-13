using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace OpenClawTray.Chat;

/// <summary>
/// Fades the bottom edge of the chat transcript into the composer, like the web
/// transcript-edge fade, as a true alpha mask so translucent Mica stays visible
/// through the fade.
/// </summary>
/// <example>
/// <code>
/// Border(Border(itemsView))
///     .OnMount(control => ChatTranscriptFade.Attach((Border)control))
///     .OnUnmount(control => ChatTranscriptFade.Detach((Border)control));
/// </code>
/// </example>
/// <remarks>
/// Only the bottom band is redirected through a CompositionVisualSurface. The inner
/// Border clips that band from the normal rendering, while the ItemsView itself stays
/// opaque and live. The source surface samples the ItemsView before its parent's clip.
/// The outer Border owns the masked band, leaving navigation and composer visuals alone.
/// </remarks>
internal static class ChatTranscriptFade
{
    private sealed class State
    {
        public Border? Clipper { get; set; }
        public UIElement? Content { get; set; }
        public bool Active { get; set; }
        public EventHandler<object>? LayoutChanged { get; set; }
        public Band? Band { get; set; }
    }

    private sealed class Band(Border clipper, UIElement content) : IDisposable
    {
        private readonly Visual _clipperVisual = ElementCompositionPreview.GetElementVisual(clipper);
        private readonly CompositionClip? _originalClip = ElementCompositionPreview.GetElementVisual(clipper).Clip;
        private readonly InsetClip _clip = ElementCompositionPreview.GetElementVisual(clipper).Compositor.CreateInsetClip();
        private readonly CompositionVisualSurface _surface = ElementCompositionPreview.GetElementVisual(content).Compositor.CreateVisualSurface();
        private CompositionSurfaceBrush? _source;
        private CompositionLinearGradientBrush? _gradient;
        private CompositionColorGradientStop? _opaque;
        private CompositionColorGradientStop? _transparent;
        private CompositionMaskBrush? _mask;
        public UIElement Content { get; } = content;
        public SpriteVisual Sprite { get; } = ElementCompositionPreview.GetElementVisual(content).Compositor.CreateSpriteVisual();

        public void Initialize(Border host)
        {
            var visual = ElementCompositionPreview.GetElementVisual(Content);
            var compositor = visual.Compositor;
            _surface.SourceVisual = visual;
            _source = compositor.CreateSurfaceBrush(_surface);
            _source.Stretch = CompositionStretch.None;
            _source.HorizontalAlignmentRatio = 0;
            _source.VerticalAlignmentRatio = 0;
            _gradient = compositor.CreateLinearGradientBrush();
            _gradient.StartPoint = Vector2.Zero;
            _gradient.EndPoint = Vector2.UnitY;
            _opaque = compositor.CreateColorGradientStop(0, Colors.White);
            _transparent = compositor.CreateColorGradientStop(1, Colors.Transparent);
            _gradient.ColorStops.Add(_opaque);
            _gradient.ColorStops.Add(_transparent);
            _mask = compositor.CreateMaskBrush();
            _mask.Source = _source;
            _mask.Mask = _gradient;
            Sprite.Brush = _mask;
            Update();
            _clipperVisual.Clip = _clip;
            ElementCompositionPreview.SetElementChildVisual(host, Sprite);
        }

        public void Update()
        {
            var width = (float)clipper.ActualWidth;
            var height = (float)clipper.ActualHeight;
            var bandHeight = Math.Min((float)ChatVisuals.TranscriptFadeHeight, height);
            _clip.BottomInset = bandHeight;
            _surface.SourceOffset = new(0, height - bandHeight);
            _surface.SourceSize = new(width, bandHeight);
            Sprite.Size = new(width, bandHeight);
            Sprite.Offset = new(0, height - bandHeight, 0);
        }

        public void Dispose()
        {
            _clipperVisual.Clip = _originalClip;
            Sprite.Brush = null;
            _surface.SourceVisual = null;
            Sprite.Dispose();
            _mask?.Dispose();
            _source?.Dispose();
            _gradient?.Dispose();
            _opaque?.Dispose();
            _transparent?.Dispose();
            _surface.Dispose();
            _clip.Dispose();
        }
    }

    private static readonly ConditionalWeakTable<Border, State> s_states = new();

    internal static void Attach(Border host)
    {
        Detach(host);
        var state = new State();
        s_states.Add(host, state);
        // Border.Child is not a dependency property. Detect replacement after XAML
        // layout, without rebuilding composition resources on ordinary layout passes.
        state.LayoutChanged = (_, _) =>
        {
            if (!ReferenceEquals(host.Child, state.Clipper) ||
                !ReferenceEquals(state.Clipper?.Child, state.Content))
                Refresh(host);
        };
        host.SizeChanged += OnHostSizeChanged;
        host.Loaded += OnLoaded;
        host.Unloaded += OnUnloaded;
        Activate(host, state);
    }

    internal static void Detach(Border host)
    {
        host.SizeChanged -= OnHostSizeChanged;
        host.Loaded -= OnLoaded;
        host.Unloaded -= OnUnloaded;
        ChatThemeChanges.Unwatch(host);
        if (!s_states.TryGetValue(host, out var state))
            return;
        host.LayoutUpdated -= state.LayoutChanged;
        Release(host, state);
        s_states.Remove(host);
    }

    private static void OnLoaded(object sender, RoutedEventArgs args)
    {
        var host = (Border)sender;
        if (s_states.TryGetValue(host, out var state))
            Activate(host, state);
    }

    private static void Activate(Border host, State state)
    {
        state.Active = true;
        host.LayoutUpdated -= state.LayoutChanged;
        host.LayoutUpdated += state.LayoutChanged;
        ChatThemeChanges.Watch(host, () => Refresh(host));
        Refresh(host);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs args)
    {
        var host = (Border)sender;
        ChatThemeChanges.Unwatch(host);
        if (s_states.TryGetValue(host, out var state))
        {
            state.Active = false;
            host.LayoutUpdated -= state.LayoutChanged;
            Release(host, state);
        }
    }
    private static void OnHostSizeChanged(object sender, SizeChangedEventArgs args) => Refresh((Border)sender);

    private static void Refresh(Border host)
    {
        if (!s_states.TryGetValue(host, out var state) || !state.Active)
            return;
        if (!ReferenceEquals(state.Clipper, host.Child))
        {
            Release(host, state);
            state.Clipper = host.Child as Border;
        }
        state.Content = state.Clipper?.Child;
        if (state.Clipper is not { Child: { } content } clipper || ChatThemeChanges.IsHighContrast())
        {
            Release(host, state);
            return;
        }
        if (state.Band is null || !ReferenceEquals(state.Band.Content, content))
        {
            Release(host, state);
            var band = new Band(clipper, content);
            try { band.Initialize(host); }
            catch { band.Dispose(); throw; }
            state.Band = band;
        }
        state.Band.Update();
    }

    private static void Release(Border host, State state)
    {
        if (state.Band is null)
            return;
        ElementCompositionPreview.SetElementChildVisual(host, null);
        state.Band.Dispose();
        state.Band = null;
    }
}
