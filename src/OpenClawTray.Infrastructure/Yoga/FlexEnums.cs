// Public flex layout enums, originally from Yoga.
// These are the user-facing enum types for flex layout configuration.
// AI-HINT: Maps 1:1 to CSS Flexbox enum values. Used by FlexPanel and YogaStyle.

namespace OpenClawTray.Infrastructure.Layout;

public enum FlexAlign
{
    Auto = 0,
    FlexStart = 1,
    Center = 2,
    FlexEnd = 3,
    Stretch = 4,
    Baseline = 5,
    SpaceBetween = 6,
    SpaceAround = 7,
    SpaceEvenly = 8,
    Start = 9,
    End = 10,
}

public enum FlexDirection
{
    Column = 0,
    ColumnReverse = 1,
    Row = 2,
    RowReverse = 3,
}

public enum FlexJustify
{
    Auto = 0,
    FlexStart = 1,
    Center = 2,
    FlexEnd = 3,
    SpaceBetween = 4,
    SpaceAround = 5,
    SpaceEvenly = 6,
    Stretch = 7,
    Start = 8,
    End = 9,
}

public enum FlexLayoutDirection
{
    Inherit = 0,
    LTR = 1,
    RTL = 2,
}

public enum FlexPositionType
{
    Static = 0,
    Relative = 1,
    Absolute = 2,
}

public enum FlexWrap
{
    NoWrap = 0,
    Wrap = 1,
    WrapReverse = 2,
}
