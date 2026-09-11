using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class AutoStartSettingsApplierTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplyLatestAsync_QueuedSave_ReadsPreferenceAfterGateAcquisition(bool initial)
    {
        using var gate = new SemaphoreSlim(1, 1);
        var preference = initial;
        var reads = 0;
        var writes = new List<bool>();

        await gate.WaitAsync();
        var pending = AutoStartSettingsApplier.ApplyLatestAsync(
            gate,
            () =>
            {
                reads++;
                return preference;
            },
            enabled =>
            {
                writes.Add(enabled);
                return Task.CompletedTask;
            });
        try
        {
            Assert.False(pending.IsCompleted);
            Assert.Equal(0, reads);
            Assert.Empty(writes);
            preference = !initial;
        }
        finally
        {
            gate.Release();
        }

        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, reads);
        Assert.Equal(new[] { !initial }, writes);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task ApplyLatestAsync_HoldsGateUntilWindowsWriteCompletes()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var releaseFirstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<bool>();
        var preference = true;
        var secondRead = false;

        var first = AutoStartSettingsApplier.ApplyLatestAsync(
            gate,
            () => preference,
            async enabled =>
            {
                await releaseFirstWrite.Task;
                writes.Add(enabled);
            });

        preference = false;
        var second = AutoStartSettingsApplier.ApplyLatestAsync(
            gate,
            () =>
            {
                secondRead = true;
                return preference;
            },
            enabled =>
            {
                writes.Add(enabled);
                return Task.CompletedTask;
            });
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.False(secondRead);
            Assert.Empty(writes);
        }
        finally
        {
            releaseFirstWrite.SetResult();
        }

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { true, false }, writes);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplyLatestAsync_FailureIsPropagatedAndReleasesGate(bool readFails)
    {
        using var gate = new SemaphoreSlim(1, 1);
        var failure = new InvalidOperationException("injected auto-start failure");
        var failing = AutoStartSettingsApplier.ApplyLatestAsync(
            gate,
            () => readFails ? throw failure : true,
            _ => Task.FromException(failure));

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => failing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(failure, actual);
        Assert.Equal(1, gate.CurrentCount);

        bool? applied = null;
        await AutoStartSettingsApplier.ApplyLatestAsync(
            gate,
            () => false,
            enabled =>
            {
                applied = enabled;
                return Task.CompletedTask;
            }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(false, applied);
        Assert.Equal(1, gate.CurrentCount);
    }
}
