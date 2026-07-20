using OpenClawTray.FunctionalUI.Core;
using static OpenClawTray.FunctionalUI.Factories;

namespace OpenClawTray.FunctionalUI.Tests;

/// <summary>
/// Record-level tests for the <see cref="RichTextBlockElement"/> primitive.
///
/// The reconciler that materializes elements into WinUI controls needs a live
/// dispatcher, so these tests assert the element-graph contract only: the
/// factory shape, that <c>Set(Action&lt;RichTextBlock&gt;)</c> captures a
/// setter, and that generic modifiers flow onto the element. The chat renderer
/// relies on all three to give a whole message one continuous selection scope.
/// </summary>
public sealed class RichTextBlockElementTests
{
    [Fact]
    public void RichTextBlock_Factory_CreatesRichTextBlockElement()
    {
        var element = RichTextBlock();

        Assert.IsType<RichTextBlockElement>(element);
        Assert.Empty(element.Setters);
    }

    [Fact]
    public void Set_CapturesSetterDelegate_AndReturnsSameElementType()
    {
        var element = RichTextBlock().Set(_ => { });

        Assert.IsType<RichTextBlockElement>(element);
        Assert.Single(element.Setters);
    }

    [Fact]
    public void Set_IsAdditive_PreservingSetterOrder()
    {
        var order = new List<int>();

        var element = RichTextBlock()
            .Set(_ => order.Add(1))
            .Set(_ => order.Add(2));

        Assert.Equal(2, element.Setters.Count);
    }

    [Fact]
    public void Modifiers_FlowOntoRichTextBlockElement()
    {
        var element = RichTextBlock().FontSize(14);

        Assert.Equal(14, element.Modifiers.FontSize);
    }
}
