namespace OpenClaw.SetupEngine.Tests;

public sealed class CleanupVisibilityRunStateTests
{
    [Fact]
    public void MissingReadiness_PreservesActualFailureRatherThanReportingDefaultSuccess()
    {
        var state = new CleanupVisibilityRunState();
        Assert.Null(state.RootExitCode);
        state.RecordRootExit(37);
        var error = Assert.Throws<InvalidOperationException>(state.RequireCompleteHandshake);
        Assert.Contains("Root native exit: 37", error.Message);
        Assert.Equal(37, state.RootExitCode);
    }

    [Fact]
    public void ReadyThenEarlySuccessfulExit_DoesNotPassWithoutObservationRelease()
    {
        var state = new CleanupVisibilityRunState();
        state.MarkWorkerReady();
        state.RecordRootExit(0);
        Assert.Throws<InvalidOperationException>(state.RequireCompleteHandshake);
    }

    [Fact]
    public void ExternalParent_MustMatchActualHostBeforeStartupSamplingStops()
    {
        var state = new CleanupVisibilityRunState();
        state.ConfigureExternalHost(@"C:\proof\OpenClaw.GatewayCleanupHost.exe");
        Assert.True(state.CaptureUnownedStartupSamples);
        Assert.Throws<InvalidOperationException>(() =>
            state.RegisterExternalWorker(12, 11, @"C:\other.exe", 100, 120));
        Assert.False(state.WorkerReady);
        Assert.True(state.CaptureUnownedStartupSamples);
        Assert.Throws<InvalidOperationException>(state.MarkWorkerReady);
        state.RegisterExternalWorker(12, 11, @"C:\proof\OpenClaw.GatewayCleanupHost.exe", 100, 120);
        Assert.False(state.CaptureUnownedStartupSamples);
        Assert.False(state.CanAttributeObservation(11, 99));
        Assert.True(state.CanAttributeObservation(11, 100));
        Assert.False(state.CanAttributeObservation(12, 119));
        Assert.True(state.CanAttributeObservation(12, 120));
    }

    [Theory]
    [InlineData(12, 0, 100, 120)]
    [InlineData(12, 12, 100, 120)]
    [InlineData(12, 11, -1, 120)]
    [InlineData(12, 11, 100, 99)]
    public void InvalidOrPreexistingExternalIdentity_CannotSignalReady(int worker, int host,
        double hostCreated, double workerCreated)
    {
        var state = new CleanupVisibilityRunState();
        state.ConfigureExternalHost(@"C:\proof\host.exe");
        Assert.Throws<InvalidOperationException>(() =>
            state.RegisterExternalWorker(worker, host, @"C:\proof\host.exe", hostCreated, workerCreated));
        Assert.False(state.WorkerReady);
    }

    [Theory]
    [InlineData(19, 2, false)]
    [InlineData(20, 1, false)]
    [InlineData(20, 2, true)]
    public void ReleaseRequiresExistingWindowAndTaskbarCoverage(int windows, int taskbars, bool expected)
    {
        var state = new CleanupVisibilityRunState();
        state.MarkWorkerReady();
        Assert.Equal(expected, state.CanRelease(windows, taskbars, false, false, false, false));
    }

    [Fact]
    public void PositiveControl_RequiresAllThreeIndependentVisibilityWitnesses()
    {
        var state = new CleanupVisibilityRunState();
        state.MarkWorkerReady();
        Assert.False(state.CanRelease(20, 2, true, true, true, false));
        Assert.False(state.CanRelease(20, 2, true, true, false, true));
        Assert.False(state.CanRelease(20, 2, true, false, true, true));
        Assert.True(state.CanRelease(20, 2, true, true, true, true));
        state.MarkReleased();
        state.RecordRootExit(0);
        state.RequireCompleteHandshake();
        Assert.False(state.CanRelease(20, 2, true, true, true, true));
    }
}
