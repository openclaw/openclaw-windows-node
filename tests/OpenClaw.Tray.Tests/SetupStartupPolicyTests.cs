using OpenClawTray.Services;
using OpenClaw.Shared;

namespace OpenClaw.Tray.Tests;

public sealed class SetupStartupPolicyTests
{
    [Theory]
    [InlineData(StartupTaskInspection.Absent)]
    [InlineData(StartupTaskInspection.Different)]
    [InlineData(StartupTaskInspection.Unknown)]
    public void AmbiguousRegistrationNeverCreatesRunKey(StartupTaskInspection inspection)
    {
        var writes = 0;
        Assert.Throws<InvalidOperationException>(() => SetupStartupPolicy.ApplyUnpackaged(true,
            () => StartupTaskRegistrationOutcome.Unknown, () => inspection, () => true,
            () => writes++, () => { }));
        Assert.Equal(0, writes);
    }

    [Fact]
    public void AmbiguousRegistrationCanCompleteOnlyWithExactEnabledTaskProof()
    {
        var removedRunKey = false;
        SetupStartupPolicy.ApplyUnpackaged(true, () => StartupTaskRegistrationOutcome.Unknown,
            () => StartupTaskInspection.ExpectedEnabled, () => true,
            () => throw new Exception("Must not create duplicate startup"), () => removedRunKey = true);
        Assert.True(removedRunKey);
    }

    [Fact]
    public void DisableRequiresBothRunKeyRemovalAndSuccessfulTaskRemoval()
    {
        var keyRemoved = false;
        Assert.Throws<InvalidOperationException>(() => SetupStartupPolicy.ApplyUnpackaged(false,
            () => throw new Exception(), () => StartupTaskInspection.ExpectedEnabled, () => false, () => throw new Exception(),
            () => keyRemoved = true));
        Assert.True(keyRemoved);
        var taskRemoved = false;
        SetupStartupPolicy.ApplyUnpackaged(false, () => StartupTaskRegistrationOutcome.Rejected, () => StartupTaskInspection.ExpectedEnabled,
            () => { taskRemoved = true; return true; }, () => { }, () => { });
        Assert.True(taskRemoved);
    }

    [Fact]
    public void MissingTaskIsIdempotentButUnknownTaskStateIsNotSuccess()
    {
        SetupStartupPolicy.ApplyUnpackaged(false, () => StartupTaskRegistrationOutcome.Rejected, () => StartupTaskInspection.Absent,
            () => throw new Exception("Must not remove absent task"), () => { }, () => { });
        Assert.Throws<IOException>(() => SetupStartupPolicy.ApplyUnpackaged(false, () => StartupTaskRegistrationOutcome.Rejected,
            () => throw new IOException("Cannot query"), () => true, () => { }, () => { }));
    }

    [Fact]
    public void EnableConfirmsFallbackAndDoesNotSwallowRegistryFailures()
    {
        var fallback = false;
        SetupStartupPolicy.ApplyUnpackaged(true, () => StartupTaskRegistrationOutcome.Rejected, () => StartupTaskInspection.Absent, () => true,
            () => fallback = true, () => { });
        Assert.True(fallback);
        Assert.Throws<UnauthorizedAccessException>(() => SetupStartupPolicy.ApplyUnpackaged(true,
            () => StartupTaskRegistrationOutcome.Rejected, () => StartupTaskInspection.Absent, () => true, () => throw new UnauthorizedAccessException(), () => { }));
        Assert.Throws<IOException>(() => SetupStartupPolicy.ApplyUnpackaged(true,
            () => StartupTaskRegistrationOutcome.Registered, () => StartupTaskInspection.Absent, () => true, () => { }, () => throw new IOException()));
    }

    [Fact]
    public async Task ClassicFailureIsAcknowledgedBeforeRestartCanContinue()
    {
        var calls = new List<string>();
        var warning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stage = SetupStartupPolicy.ApplyClassicPreferenceAsync(true,
            _ => { calls.Add("apply"); throw new InvalidOperationException("Windows permission"); },
            () => { calls.Add("startup-warning"); return warning.Task; });
        Assert.False(stage.IsCompleted);
        warning.SetResult();
        await stage;
        calls.Add("restart");
        Assert.Equal(["apply", "startup-warning", "restart"], calls);
        await SetupStartupPolicy.ApplyClassicPreferenceAsync(null,
            _ => throw new Exception("Preserve must not apply"), () => throw new Exception("Must not warn"));
    }

    [Fact]
    public async Task ExplicitSetupUsesTheExistingMutationGateAndRejectsReplacedIntent()
    {
        using var gate = new SemaphoreSlim(0, 1);
        var preference = false;
        var writes = 0;
        var task = AutoStartSettingsApplier.ApplyExplicitAsync(gate, false, () => preference,
            _ => { writes++; return Task.CompletedTask; }, CancellationToken.None);
        preference = true;
        gate.Release();
        await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal(0, writes);
    }
}
