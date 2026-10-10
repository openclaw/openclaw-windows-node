namespace OpenClawTray.Services;

internal enum CaptureConsentType
{
    Screen,
    Camera,
    Location
}

internal sealed record SensitiveCapturePlan(
    CaptureConsentType ConsentType,
    string ToastTitleResourceKey,
    string ToastDetailResourceKey,
    string NotificationKey);

internal static class SensitiveCapturePlans
{
    public static SensitiveCapturePlan ScreenSnapshot { get; } = new(
        CaptureConsentType.Screen,
        "Toast_ScreenCaptured",
        "Toast_ScreenCapturedDetail",
        "node:screen-captured");

    public static SensitiveCapturePlan CameraSnap { get; } = new(
        CaptureConsentType.Camera,
        "Toast_CameraCaptured",
        "Toast_CameraCapturedDetail",
        "node:camera-captured");

    public static SensitiveCapturePlan LocationGet { get; } = new(
        CaptureConsentType.Location,
        "Toast_LocationRead",
        "Toast_LocationReadDetail",
        "node:location-read");
}

internal static class SensitiveCaptureExecutor
{
    public static async Task<TResult> ExecuteAsync<TResult>(
        SensitiveCapturePlan plan,
        Func<CaptureConsentType, CancellationToken, Task> ensureConsentAsync,
        Action<SensitiveCapturePlan> showIndicator,
        Func<CancellationToken, Task<TResult>> captureAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ensureConsentAsync);
        ArgumentNullException.ThrowIfNull(showIndicator);
        ArgumentNullException.ThrowIfNull(captureAsync);

        await ensureConsentAsync(plan.ConsentType, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        showIndicator(plan);
        cancellationToken.ThrowIfCancellationRequested();

        return await captureAsync(cancellationToken);
    }
}
