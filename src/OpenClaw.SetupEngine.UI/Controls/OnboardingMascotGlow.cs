using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace OpenClaw.SetupEngine.UI.Controls;

/// <summary>
/// A live alpha mask of the existing retained artwork, not a second renderer.
/// The source excludes the shadow host so the capture never feeds back into itself.
/// </summary>
internal sealed class OnboardingMascotGlow : IDisposable
{
    private readonly OnboardingMascotDrawing _drawing;
    private readonly CompositionVisualSurface _surface;
    private readonly CompositionSurfaceBrush _mask;
    private readonly DropShadow _shadow;
    private readonly SpriteVisual _visual;

    public OnboardingMascotGlow(OnboardingMascotDrawing drawing)
    {
        _drawing = drawing;
        var source = ElementCompositionPreview.GetElementVisual(drawing.Artwork);
        var compositor = source.Compositor;
        _surface = compositor.CreateVisualSurface();
        _surface.SourceVisual = source;
        _surface.SourceSize = new Vector2(168);
        _mask = compositor.CreateSurfaceBrush(_surface);
        _shadow = compositor.CreateDropShadow();
        _shadow.Mask = _mask;
        // 12 native units become 13 DIPs at the canonical 130-DIP hero size.
        _shadow.BlurRadius = 12;
        _shadow.Offset = Vector3.Zero;
        _visual = compositor.CreateSpriteVisual();
        _visual.Size = new Vector2(168);
        _visual.Shadow = _shadow;
        ElementCompositionPreview.SetElementChildVisual(drawing.GlowHost, _visual);
    }

    public void SetPalette(bool light, bool highContrast)
    {
        _visual.IsVisible = !highContrast;
        _shadow.Color = light ? Color.FromArgb(255, 239, 75, 88) : Color.FromArgb(255, 255, 77, 77);
        _shadow.Opacity = light ? 0.2f : 0.4f;
    }

    public void Dispose()
    {
        ElementCompositionPreview.SetElementChildVisual(_drawing.GlowHost, null);
        _visual.Shadow = null;
        _shadow.Mask = null;
        _mask.Surface = null;
        _surface.SourceVisual = null;
        _visual.Dispose();
        _shadow.Dispose();
        _mask.Dispose();
        _surface.Dispose();
    }
}
