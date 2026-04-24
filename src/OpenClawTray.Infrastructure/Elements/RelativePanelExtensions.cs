using OpenClawTray.Infrastructure.Core;

namespace OpenClawTray.Infrastructure;

/// <summary>
/// Fluent extension methods for RelativePanel attached properties.
/// Usage: Rectangle().RelativePanel(name: "Rect1", rightOf: "Rect2")
/// </summary>
public static class RelativePanelExtensions
{
    /// <summary>
    /// Sets RelativePanel attached properties on this element.
    /// Only meaningful when the element is a child of a RelativePanel.
    /// </summary>
    public static T RelativePanel<T>(this T el,
        string name,
        string? rightOf = null,
        string? below = null,
        string? leftOf = null,
        string? above = null,
        string? alignLeftWith = null,
        string? alignRightWith = null,
        string? alignTopWith = null,
        string? alignBottomWith = null,
        string? alignHorizontalCenterWith = null,
        string? alignVerticalCenterWith = null,
        bool alignLeftWithPanel = false,
        bool alignRightWithPanel = false,
        bool alignTopWithPanel = false,
        bool alignBottomWithPanel = false,
        bool alignHorizontalCenterWithPanel = false,
        bool alignVerticalCenterWithPanel = false) where T : Element =>
        (T)el.SetAttached(new RelativePanelAttached(name)
        {
            RightOf = rightOf,
            Below = below,
            LeftOf = leftOf,
            Above = above,
            AlignLeftWith = alignLeftWith,
            AlignRightWith = alignRightWith,
            AlignTopWith = alignTopWith,
            AlignBottomWith = alignBottomWith,
            AlignHorizontalCenterWith = alignHorizontalCenterWith,
            AlignVerticalCenterWith = alignVerticalCenterWith,
            AlignLeftWithPanel = alignLeftWithPanel,
            AlignRightWithPanel = alignRightWithPanel,
            AlignTopWithPanel = alignTopWithPanel,
            AlignBottomWithPanel = alignBottomWithPanel,
            AlignHorizontalCenterWithPanel = alignHorizontalCenterWithPanel,
            AlignVerticalCenterWithPanel = alignVerticalCenterWithPanel,
        });
}
