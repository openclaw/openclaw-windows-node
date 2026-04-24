// C# port of Meta's Yoga layout engine LayoutResults.
// Ported from yoga/node/LayoutResults.h, yoga/node/CachedMeasurement.h

using OpenClawTray.Infrastructure.Layout;

namespace OpenClawTray.Infrastructure.Layout;

/// <summary>
/// Cached measurement entry for avoiding redundant measure calls.
/// </summary>
internal struct CachedMeasurement
{
    public float AvailableWidth = -1;
    public float AvailableHeight = -1;
    public SizingMode WidthSizingMode = SizingMode.MaxContent;
    public SizingMode HeightSizingMode = SizingMode.MaxContent;
    public float ComputedWidth = -1;
    public float ComputedHeight = -1;

    public CachedMeasurement() { }

    public bool Equals(CachedMeasurement other)
    {
        bool isEqual = WidthSizingMode == other.WidthSizingMode &&
                       HeightSizingMode == other.HeightSizingMode;

        if (!YogaFloat.IsUndefined(AvailableWidth) || !YogaFloat.IsUndefined(other.AvailableWidth))
            isEqual = isEqual && AvailableWidth == other.AvailableWidth;
        if (!YogaFloat.IsUndefined(AvailableHeight) || !YogaFloat.IsUndefined(other.AvailableHeight))
            isEqual = isEqual && AvailableHeight == other.AvailableHeight;
        if (!YogaFloat.IsUndefined(ComputedWidth) || !YogaFloat.IsUndefined(other.ComputedWidth))
            isEqual = isEqual && ComputedWidth == other.ComputedWidth;
        if (!YogaFloat.IsUndefined(ComputedHeight) || !YogaFloat.IsUndefined(other.ComputedHeight))
            isEqual = isEqual && ComputedHeight == other.ComputedHeight;

        return isEqual;
    }
}

/// <summary>
/// Stores the computed layout results for a YogaNode after CalculateLayout().
/// </summary>
internal sealed class LayoutResults
{
    public const int MaxCachedMeasurements = 8;

    public uint ComputedFlexBasisGeneration;
    public float ComputedFlexBasis = float.NaN;

    // Cache invalidation tracking
    public uint GenerationCount;
    public uint ConfigVersion;
    public FlexLayoutDirection LastOwnerDirection = FlexLayoutDirection.Inherit;

    public uint NextCachedMeasurementsIndex;
    public readonly CachedMeasurement[] CachedMeasurements = new CachedMeasurement[MaxCachedMeasurements];
    public CachedMeasurement CachedLayout;

    // Direction and overflow
    private FlexLayoutDirection _direction = FlexLayoutDirection.Inherit;
    private bool _hadOverflow;

    // Dimensions
    private readonly float[] _dimensions = { float.NaN, float.NaN };
    private readonly float[] _measuredDimensions = { float.NaN, float.NaN };
    private readonly float[] _rawDimensions = { float.NaN, float.NaN };

    // Position, margin, border, padding (indexed by PhysicalEdge: Left=0, Top=1, Right=2, Bottom=3)
    private readonly float[] _position = new float[4];
    private readonly float[] _margin = new float[4];
    private readonly float[] _border = new float[4];
    private readonly float[] _padding = new float[4];

    public LayoutResults()
    {
        for (int i = 0; i < MaxCachedMeasurements; i++)
            CachedMeasurements[i] = new CachedMeasurement();
        CachedLayout = new CachedMeasurement();
    }

    public FlexLayoutDirection Direction
    {
        get => _direction;
        set => _direction = value;
    }

    public bool HadOverflow
    {
        get => _hadOverflow;
        set => _hadOverflow = value;
    }

    public float GetDimension(YogaDimension axis) => _dimensions[(int)axis];
    public void SetDimension(YogaDimension axis, float value) => _dimensions[(int)axis] = value;

    public float GetMeasuredDimension(YogaDimension axis) => _measuredDimensions[(int)axis];
    public void SetMeasuredDimension(YogaDimension axis, float value) => _measuredDimensions[(int)axis] = value;

    public float GetRawDimension(YogaDimension axis) => _rawDimensions[(int)axis];
    public void SetRawDimension(YogaDimension axis, float value) => _rawDimensions[(int)axis] = value;

    public float GetPosition(YogaPhysicalEdge edge) => _position[(int)edge];
    public void SetPosition(YogaPhysicalEdge edge, float value) => _position[(int)edge] = value;

    public float GetMargin(YogaPhysicalEdge edge) => _margin[(int)edge];
    public void SetMargin(YogaPhysicalEdge edge, float value) => _margin[(int)edge] = value;

    public float GetBorder(YogaPhysicalEdge edge) => _border[(int)edge];
    public void SetBorder(YogaPhysicalEdge edge, float value) => _border[(int)edge] = value;

    public float GetPadding(YogaPhysicalEdge edge) => _padding[(int)edge];
    public void SetPadding(YogaPhysicalEdge edge, float value) => _padding[(int)edge] = value;

    /// <summary>
    /// Reset all layout results to default values.
    /// </summary>
    public void Reset()
    {
        ComputedFlexBasisGeneration = 0;
        ComputedFlexBasis = float.NaN;
        GenerationCount = 0;
        ConfigVersion = 0;
        LastOwnerDirection = FlexLayoutDirection.Inherit;
        NextCachedMeasurementsIndex = 0;
        _direction = FlexLayoutDirection.Inherit;
        _hadOverflow = false;

        _dimensions[0] = float.NaN;
        _dimensions[1] = float.NaN;
        _measuredDimensions[0] = float.NaN;
        _measuredDimensions[1] = float.NaN;
        _rawDimensions[0] = float.NaN;
        _rawDimensions[1] = float.NaN;

        Array.Clear(_position);
        Array.Clear(_margin);
        Array.Clear(_border);
        Array.Clear(_padding);

        for (int i = 0; i < MaxCachedMeasurements; i++)
            CachedMeasurements[i] = new CachedMeasurement();
        CachedLayout = new CachedMeasurement();
    }

    public bool EqualTo(LayoutResults other)
    {
        if (_direction != other._direction || _hadOverflow != other._hadOverflow)
            return false;

        for (int i = 0; i < 2; i++)
        {
            if (!YogaFloat.InexactEquals(_dimensions[i], other._dimensions[i]))
                return false;
        }

        for (int i = 0; i < 2; i++)
        {
            if (!YogaFloat.InexactEquals(_measuredDimensions[i], other._measuredDimensions[i]))
                return false;
        }

        for (int i = 0; i < 4; i++)
        {
            if (!YogaFloat.InexactEquals(_position[i], other._position[i]) ||
                !YogaFloat.InexactEquals(_margin[i], other._margin[i]) ||
                !YogaFloat.InexactEquals(_border[i], other._border[i]) ||
                !YogaFloat.InexactEquals(_padding[i], other._padding[i]))
                return false;
        }

        return true;
    }
}
