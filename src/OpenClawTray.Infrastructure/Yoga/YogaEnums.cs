// C# port of Meta's Yoga layout engine enums.
// Ported from yoga/enums/*.h
// Public user-facing enums (FlexAlign, FlexDirection, etc.) have been moved to OpenClawTray.Infrastructure.Layout.
// AI-HINT: Internal enums used by the Yoga algorithm. User-facing enums are in OpenClawTray.Infrastructure.Layout.FlexEnums.

namespace OpenClawTray.Infrastructure.Layout;

internal enum YogaBoxSizing
{
    BorderBox = 0,
    ContentBox = 1,
}

internal enum YogaDimension
{
    Width = 0,
    Height = 1,
}

internal enum YogaDisplay
{
    Flex = 0,
    None = 1,
    Contents = 2,
    /// <summary>
    /// Grid layout is not yet implemented in this C# port.
    /// Setting this value will throw <see cref="NotImplementedException"/>.
    /// </summary>
    Grid = 3,
}

internal enum YogaEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3,
    Start = 4,
    End = 5,
    Horizontal = 6,
    Vertical = 7,
    All = 8,
}

internal enum YogaPhysicalEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3,
}

[Flags]
internal enum YogaErrata : uint
{
    None = 0,
    StretchFlexBasis = 1,
    AbsolutePositionWithoutInsetsExcludesPadding = 2,
    AbsolutePercentAgainstInnerSize = 4,
    All = 2147483647,
    Classic = 2147483646,
}

internal enum YogaExperimentalFeature
{
    WebFlexBasis = 0,
    FixFlexBasisFitContent = 1,
}

internal enum YogaGutter
{
    Column = 0,
    Row = 1,
    All = 2,
}

internal enum YogaLogLevel
{
    Error = 0,
    Warn = 1,
    Info = 2,
    Debug = 3,
    Verbose = 4,
    Fatal = 5,
}

internal enum YogaMeasureMode
{
    Undefined = 0,
    Exactly = 1,
    AtMost = 2,
}

internal enum YogaNodeType
{
    Default = 0,
    Text = 1,
}

internal enum YogaOverflow
{
    Visible = 0,
    Hidden = 1,
    Scroll = 2,
}

internal enum YogaUnit
{
    Undefined = 0,
    Point = 1,
    Percent = 2,
    Auto = 3,
    MaxContent = 4,
    FitContent = 5,
    Stretch = 6,
}

/// <summary>
/// Internal sizing mode used by the algorithm, maps to/from YogaMeasureMode.
/// </summary>
internal enum SizingMode
{
    StretchFit = 0,
    MaxContent = 1,
    FitContent = 2,
}
