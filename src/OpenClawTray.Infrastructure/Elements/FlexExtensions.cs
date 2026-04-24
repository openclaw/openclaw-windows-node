using OpenClawTray.Infrastructure.Core;
using OpenClawTray.Infrastructure.Layout;

namespace OpenClawTray.Infrastructure;

/// <summary>
/// Extension method for setting flex attached properties on elements.
/// </summary>
public static class FlexExtensions
{
    public static T Flex<T>(this T el,
        double grow = 0,
        double shrink = 1,
        double? basis = null,
        FlexAlign? alignSelf = null,
        FlexPositionType position = FlexPositionType.Relative,
        double? left = null,
        double? top = null,
        double? right = null,
        double? bottom = null
    ) where T : Element
    {
        if (grow < 0 || double.IsNaN(grow) || double.IsInfinity(grow))
            throw new ArgumentOutOfRangeException(nameof(grow), "Grow must be a non-negative, finite value.");

        if (shrink < 0 || double.IsNaN(shrink) || double.IsInfinity(shrink))
            throw new ArgumentOutOfRangeException(nameof(shrink), "Shrink must be a non-negative, finite value.");

        return (T)el.SetAttached(new FlexAttached(grow, shrink, basis, alignSelf, position, left, top, right, bottom));
    }
}
