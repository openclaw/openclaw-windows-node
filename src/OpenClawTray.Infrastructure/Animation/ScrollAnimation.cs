using Microsoft.UI.Xaml.Controls;

namespace OpenClawTray.Infrastructure.Animation;

/// <summary>
/// A single scroll-linked expression animation binding.
/// </summary>
public record ScrollExpression(string Property, string Expression);

/// <summary>
/// Configuration for scroll-linked expression animations.
/// </summary>
public record ScrollAnimationConfig(ScrollViewer ScrollViewer, ScrollExpression[] Expressions);

/// <summary>
/// Fluent builder for constructing scroll-linked expression animations.
/// </summary>
public class ScrollAnimationBuilder
{
    private readonly List<ScrollExpression> _expressions = new();

    /// <summary>
    /// Parallax effect: element translates at a fraction of scroll speed.
    /// </summary>
    public ScrollAnimationBuilder Parallax(float factor)
    {
        _expressions.Add(new ScrollExpression("Offset.Y", FormattableString.Invariant($"scroll.Translation.Y * {factor}f")));
        return this;
    }

    /// <summary>
    /// Fade out as scroll position increases from startOffset to endOffset.
    /// </summary>
    public ScrollAnimationBuilder FadeOut(float startOffset, float endOffset)
    {
        var expr = FormattableString.Invariant($"Clamp(1.0 - (scroll.Translation.Y * -1 - {startOffset}f) / ({endOffset}f - {startOffset}f), 0, 1)");
        _expressions.Add(new ScrollExpression("Opacity", expr));
        return this;
    }

    /// <summary>
    /// Fade in as scroll position increases from startOffset to endOffset.
    /// </summary>
    public ScrollAnimationBuilder FadeIn(float startOffset, float endOffset)
    {
        var expr = FormattableString.Invariant($"Clamp((scroll.Translation.Y * -1 - {startOffset}f) / ({endOffset}f - {startOffset}f), 0, 1)");
        _expressions.Add(new ScrollExpression("Opacity", expr));
        return this;
    }

    /// <summary>
    /// Scale range: uniform scale interpolates between from and to as scroll moves from scrollStart to scrollEnd.
    /// </summary>
    public ScrollAnimationBuilder ScaleRange(float scrollStart, float scrollEnd, float from, float to)
    {
        var expr = FormattableString.Invariant($"Vector3(Lerp({from}f, {to}f, Clamp((scroll.Translation.Y * -1 - {scrollStart}f) / ({scrollEnd}f - {scrollStart}f), 0, 1)), ") +
                   FormattableString.Invariant($"Lerp({from}f, {to}f, Clamp((scroll.Translation.Y * -1 - {scrollStart}f) / ({scrollEnd}f - {scrollStart}f), 0, 1)), 1)");
        _expressions.Add(new ScrollExpression("Scale", expr));
        return this;
    }

    /// <summary>
    /// Custom expression for advanced scenarios.
    /// </summary>
    public ScrollAnimationBuilder Expression(string property, string expression)
    {
        _expressions.Add(new ScrollExpression(property, expression));
        return this;
    }

    public ScrollExpression[] Build() => _expressions.ToArray();
}
