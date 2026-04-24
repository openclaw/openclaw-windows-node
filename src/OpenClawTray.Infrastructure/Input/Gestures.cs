using global::Windows.Foundation;

namespace OpenClawTray.Infrastructure.Input;

/// <summary>
/// Phase of a continuous gesture (pan / pinch / rotate / long-press).
/// The callback contract: <see cref="Began"/> fires exactly once at the start,
/// <see cref="Changed"/> fires zero or more times as the gesture progresses,
/// then either <see cref="Ended"/> (normal completion) or <see cref="Cancelled"/>
/// (system aborted, pointer lost, escape key, …) fires exactly once.
/// </summary>
public enum GesturePhase
{
    /// <summary>Gesture has just become recognized and its parameters are stable.</summary>
    Began,
    /// <summary>Gesture is in progress; deltas are being reported.</summary>
    Changed,
    /// <summary>Gesture completed normally.</summary>
    Ended,
    /// <summary>Gesture was cancelled (pointer capture lost, window focus lost, …).</summary>
    Cancelled,
}

/// <summary>
/// Axis constraint for <c>.OnPan</c>. Controls which translation axes are tracked
/// and which flag bits end up in the computed <see cref="Microsoft.UI.Xaml.Input.ManipulationModes"/>.
/// </summary>
public enum PanAxis
{
    /// <summary>Both horizontal and vertical translation are tracked.</summary>
    Both,
    /// <summary>Only horizontal (X) translation is tracked.</summary>
    Horizontal,
    /// <summary>Only vertical (Y) translation is tracked.</summary>
    Vertical,
}

/// <summary>
/// Immutable snapshot of a pan gesture update. All coordinates are in the
/// element's local space.
/// </summary>
/// <param name="Translation">Total translation since <see cref="GesturePhase.Began"/>.</param>
/// <param name="Delta">Translation since the last <see cref="GesturePhase.Changed"/> callback (zero on <see cref="GesturePhase.Began"/>).</param>
/// <param name="Velocity">Instantaneous velocity in device-independent pixels per second.</param>
/// <param name="Position">Current pointer position in element-local space.</param>
/// <param name="StartPosition">Pointer position at <see cref="GesturePhase.Began"/>, in element-local space.</param>
/// <param name="Phase">Current gesture phase.</param>
/// <param name="IsInertial">True once WinUI is extrapolating inertia (after the user lifts their finger).</param>
public readonly record struct PanGesture(
    Point Translation,
    Point Delta,
    Point Velocity,
    Point Position,
    Point StartPosition,
    GesturePhase Phase,
    bool IsInertial);

/// <summary>
/// Immutable snapshot of a pinch (scale) gesture update.
/// </summary>
/// <param name="Scale">Cumulative scale factor relative to 1.0 at <see cref="GesturePhase.Began"/>.</param>
/// <param name="ScaleDelta">Scale change since the last callback (ratio; 1.0 = no change).</param>
/// <param name="Center">Pinch centroid in element-local space.</param>
/// <param name="Phase">Current gesture phase.</param>
/// <param name="IsInertial">True once inertia is being extrapolated.</param>
public readonly record struct PinchGesture(
    double Scale,
    double ScaleDelta,
    Point Center,
    GesturePhase Phase,
    bool IsInertial);

/// <summary>
/// Immutable snapshot of a rotate gesture update.
/// </summary>
/// <param name="Angle">Cumulative rotation in degrees since <see cref="GesturePhase.Began"/>.</param>
/// <param name="AngleDelta">Rotation since the last callback, in degrees.</param>
/// <param name="Center">Rotation pivot in element-local space.</param>
/// <param name="Phase">Current gesture phase.</param>
/// <param name="IsInertial">True once inertia is being extrapolated.</param>
public readonly record struct RotateGesture(
    double Angle,
    double AngleDelta,
    Point Center,
    GesturePhase Phase,
    bool IsInertial);

/// <summary>
/// Immutable snapshot of a long-press gesture. Fires <see cref="GesturePhase.Began"/>
/// the moment the press threshold is crossed, <see cref="GesturePhase.Ended"/> when
/// the pointer is released after that point, or <see cref="GesturePhase.Cancelled"/>
/// if the pointer moves beyond the cancel distance or capture is lost.
/// </summary>
/// <param name="Position">Pointer position in element-local space at the moment of the event.</param>
/// <param name="Duration">Elapsed time since the initial press.</param>
/// <param name="Phase">Current gesture phase.</param>
public readonly record struct LongPressGesture(
    Point Position,
    TimeSpan Duration,
    GesturePhase Phase);
