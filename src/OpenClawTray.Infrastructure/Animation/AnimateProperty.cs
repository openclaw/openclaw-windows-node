namespace OpenClawTray.Infrastructure.Animation;

/// <summary>
/// Flags enum specifying which compositor properties should have implicit animations
/// applied via the .Animate() modifier.
/// </summary>
[Flags]
public enum AnimateProperty
{
    Opacity     = 1 << 0,
    Offset      = 1 << 1,   // Translation
    Scale       = 1 << 2,
    Rotation    = 1 << 3,
    CenterPoint = 1 << 4,
    All         = Opacity | Offset | Scale | Rotation | CenterPoint,
}

/// <summary>
/// Configuration for the .Animate() modifier — declaration-site implicit animation
/// on compositor Visual properties. Stored on the Element base record.
/// </summary>
public record AnimationConfig(Curve Curve, AnimateProperty Properties = AnimateProperty.All);
