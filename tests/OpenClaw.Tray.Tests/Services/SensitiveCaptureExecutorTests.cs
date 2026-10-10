using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests.Services;

public sealed class SensitiveCaptureExecutorTests
{
    [Theory]
    [InlineData("screen")]
    [InlineData("camera")]
    [InlineData("location")]
    public async Task ExecuteAsync_RequiresConsentAndIndicatorBeforeSensorAccess(
        string planName)
    {
        var plan = GetPlan(planName);
        var steps = new List<string>();

        var result = await SensitiveCaptureExecutor.ExecuteAsync(
            plan,
            (type, _) =>
            {
                Assert.Equal(plan.ConsentType, type);
                steps.Add("consent");
                return Task.CompletedTask;
            },
            indicatedPlan =>
            {
                Assert.Same(plan, indicatedPlan);
                steps.Add("indicator");
            },
            _ =>
            {
                steps.Add("sensor");
                return Task.FromResult("captured");
            },
            CancellationToken.None);

        Assert.Equal("captured", result);
        Assert.Equal(["consent", "indicator", "sensor"], steps);
    }

    private static SensitiveCapturePlan GetPlan(string planName) =>
        planName switch
        {
            "screen" => SensitiveCapturePlans.ScreenSnapshot,
            "camera" => SensitiveCapturePlans.CameraSnap,
            "location" => SensitiveCapturePlans.LocationGet,
            _ => throw new ArgumentOutOfRangeException(nameof(planName))
        };

    [Theory]
    [InlineData("screen")]
    [InlineData("camera")]
    [InlineData("location")]
    public async Task ExecuteAsync_DeniedConsentNeverIndicatesOrAccessesSensor(
        string planName)
    {
        var plan = GetPlan(planName);
        var indicatorShown = false;
        var sensorAccessed = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SensitiveCaptureExecutor.ExecuteAsync(
                plan,
                (_, _) => Task.FromException(new InvalidOperationException("denied")),
                _ => indicatorShown = true,
                _ =>
                {
                    sensorAccessed = true;
                    return Task.FromResult("captured");
                },
                CancellationToken.None));

        Assert.False(indicatorShown);
        Assert.False(sensorAccessed);
    }

    [Theory]
    [InlineData("screen")]
    [InlineData("camera")]
    [InlineData("location")]
    public async Task ExecuteAsync_ShowsIndicatorForEveryCapture(
        string planName)
    {
        var plan = GetPlan(planName);
        var indicatorCount = 0;

        for (var i = 0; i < 2; i++)
        {
            await SensitiveCaptureExecutor.ExecuteAsync(
                plan,
                (_, _) => Task.CompletedTask,
                _ => indicatorCount++,
                _ => Task.FromResult("captured"),
                CancellationToken.None);
        }

        Assert.Equal(2, indicatorCount);
    }

    [Fact]
    public void Plans_MapEachCommandToIndependentConsentAndLocalizedIndicator()
    {
        AssertPlan(
            SensitiveCapturePlans.ScreenSnapshot,
            CaptureConsentType.Screen,
            "Toast_ScreenCaptured",
            "Toast_ScreenCapturedDetail",
            "node:screen-captured");
        AssertPlan(
            SensitiveCapturePlans.CameraSnap,
            CaptureConsentType.Camera,
            "Toast_CameraCaptured",
            "Toast_CameraCapturedDetail",
            "node:camera-captured");
        AssertPlan(
            SensitiveCapturePlans.LocationGet,
            CaptureConsentType.Location,
            "Toast_LocationRead",
            "Toast_LocationReadDetail",
            "node:location-read");
    }

    private static void AssertPlan(
        SensitiveCapturePlan plan,
        CaptureConsentType consentType,
        string titleKey,
        string detailKey,
        string dedupeKey)
    {
        Assert.Equal(consentType, plan.ConsentType);
        Assert.Equal(titleKey, plan.ToastTitleResourceKey);
        Assert.Equal(detailKey, plan.ToastDetailResourceKey);
        Assert.Equal(dedupeKey, plan.NotificationKey);
    }
}
