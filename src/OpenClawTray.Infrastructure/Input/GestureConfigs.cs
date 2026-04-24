namespace OpenClawTray.Infrastructure.Input;

/// <summary>
/// Immutable configuration for a <c>.OnPan</c> gesture recognizer attached to an element.
/// Stored on <see cref="Core.ElementModifiers.Pan"/>; the reconciler wires
/// <see cref="Microsoft.UI.Xaml.UIElement.ManipulationStarted"/> /
/// <see cref="Microsoft.UI.Xaml.UIElement.ManipulationDelta"/> /
/// <see cref="Microsoft.UI.Xaml.UIElement.ManipulationCompleted"/> trampolines
/// and enforces <see cref="MinimumDistance"/> gating before dispatching callbacks.
/// </summary>
public sealed record PanGestureConfig(
    Action<PanGesture> OnChanged)
{
    /// <summary>Fires after <see cref="MinimumDistance"/> is crossed, before the first Changed.</summary>
    public Action<PanGesture>? OnBegan { get; init; }

    /// <summary>Fires on normal manipulation completion (after <see cref="OnBegan"/> fired).</summary>
    public Action<PanGesture>? OnEnded { get; init; }

    /// <summary>
    /// Fires when manipulation is cancelled after <see cref="OnBegan"/> fired.
    /// If the threshold is never crossed, neither <see cref="OnBegan"/> nor this fires.
    /// </summary>
    public Action<PanGesture>? OnCancelled { get; init; }

    /// <summary>Cumulative translation (device-independent pixels) required before callbacks dispatch.</summary>
    public double MinimumDistance { get; init; }

    /// <summary>Axis constraint for translation tracking.</summary>
    public PanAxis Axis { get; init; } = PanAxis.Both;

    /// <summary>When true, include translate-inertia flags so the gesture continues to report after the pointer lifts.</summary>
    public bool WithInertia { get; init; }
}

/// <summary>
/// Immutable configuration for a <c>.OnPinch</c> gesture recognizer.
/// </summary>
public sealed record PinchGestureConfig(
    Action<PinchGesture> OnChanged)
{
    public Action<PinchGesture>? OnBegan { get; init; }
    public Action<PinchGesture>? OnEnded { get; init; }
    public Action<PinchGesture>? OnCancelled { get; init; }
    public bool WithInertia { get; init; }
}

/// <summary>
/// Immutable configuration for a <c>.OnRotate</c> gesture recognizer.
/// </summary>
public sealed record RotateGestureConfig(
    Action<RotateGesture> OnChanged)
{
    public Action<RotateGesture>? OnBegan { get; init; }
    public Action<RotateGesture>? OnEnded { get; init; }
    public Action<RotateGesture>? OnCancelled { get; init; }
    public bool WithInertia { get; init; }
}

/// <summary>
/// Immutable configuration for a <c>.OnLongPress</c> gesture recognizer.
/// Touch/pen input routes through <see cref="Microsoft.UI.Xaml.UIElement.Holding"/>.
/// Mouse input is opt-in via <see cref="EnableMouseEmulation"/>; when enabled,
/// the reconciler arms a dispatcher timer on <see cref="Microsoft.UI.Xaml.UIElement.PointerPressed"/>
/// and cancels it on release, capture loss, or pointer motion beyond <see cref="CancelDistance"/>.
/// </summary>
public sealed record LongPressGestureConfig(
    Action<LongPressGesture> OnTriggered)
{
    /// <summary>Minimum press duration before the gesture triggers.</summary>
    public TimeSpan MinimumDuration { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Cumulative pointer motion (device pixels) that cancels an in-progress press.</summary>
    public double CancelDistance { get; init; } = 10.0;

    /// <summary>When true, mouse input arms a dispatcher timer in addition to touch/pen Holding.</summary>
    public bool EnableMouseEmulation { get; init; }
}
