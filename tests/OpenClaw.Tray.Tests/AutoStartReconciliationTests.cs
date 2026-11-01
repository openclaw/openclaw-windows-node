using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Pins <see cref="AutoStartReconciliation"/>, which decides which auto-start value the
/// app persists: <c>ReconcileAsync</c> at startup, and <c>ResolveAfterFailedChangeAsync</c>
/// after a change attempt throws.
///
/// The regression these guard: a transient failure to read or apply the Windows startup
/// state used to be reported as "disabled", and both call sites persist what they are
/// given. That silently erased an enabled preference, and unrecoverably so, because the
/// next launch reads the overwritten false and reconciliation agrees with it. An explicit
/// refusal from Windows is a different answer and must still report disabled, otherwise
/// the toggle claims an auto-start that will never happen.
/// </summary>
public sealed class AutoStartReconciliationTests
{
    private static Func<Task<AutoStartState>> Query(AutoStartState state) => () => Task.FromResult(state);

    private static Func<Task<AutoStartState>> QueryThrows(Exception ex) => () => Task.FromException<AutoStartState>(ex);

    private static Func<bool, Task> SetSucceeds() => _ => Task.CompletedTask;

    private static Func<bool, Task> SetThrows(Exception ex) => _ => Task.FromException(ex);

    [Theory]
    [InlineData(-1, true)]
    [InlineData(2, true)]
    [InlineData(6, true)]
    [InlineData(3, false)]
    [InlineData(7, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(258, false)]
    public void InstallerRun_OnlyAdoptsAnOwnedCommandWithoutWindowsDisablement(int approvalState, bool expected)
    {
        const string executable = @"C:\Program Files\OpenClaw\OpenClaw.Tray.WinUI.exe";
        byte[]? approval = null;
        if (approvalState >= 0)
        {
            approval = new byte[12];
            BitConverter.GetBytes(approvalState).CopyTo(approval, 0);
        }

        Assert.Equal(expected, AutoStartReconciliation.IsEnabledInstallerRun(
            $"\"{executable}\" --background", executable, approval));
    }

    [Fact]
    public void InstallerRun_RejectsMissingForeignOrMalformedRegistrations()
    {
        const string executable = @"C:\OpenClaw\OpenClaw.Tray.WinUI.exe";
        var command = $"\"{executable}\" --background";
        foreach (var value in new object?[] { null, 42, "", @"C:\other.exe --background", $"\"{executable}\"" })
            Assert.False(AutoStartReconciliation.IsEnabledInstallerRun(value, executable, null));
        foreach (var approval in new object[] { "enabled", new byte[0], new byte[] { 2 }, new byte[16] })
            Assert.False(AutoStartReconciliation.IsEnabledInstallerRun(command, executable, approval));
        Assert.False(AutoStartReconciliation.IsEnabledInstallerRun(command, null, null));
    }

    [Fact]
    public void UnpackagedReconciliation_DoesNotPersistScheduledTaskPresence()
    {
        var manager = File.ReadAllText(Path.Combine(TestRepositoryPaths.GetRepositoryRoot(),
            "src", "OpenClaw.Tray.WinUI", "Services", "AutoStartManager.cs"));
        var start = manager.IndexOf("public static Task<bool> ReconcileAutoStartAsync", StringComparison.Ordinal);
        var end = manager.IndexOf("private static void ThrowIfFixtureMutation", start, StringComparison.Ordinal);
        var reconciliation = manager[start..end];
        Assert.DoesNotContain("Task.Run(IsAutoStartEnabled)", reconciliation);
        Assert.DoesNotContain("WindowsStartupTaskRegistration.Exists", reconciliation);
        Assert.Contains("Task.Run(IsInstallerRunEnabled)", reconciliation);
        Assert.Contains("AutoStartReconciliation.IsEnabledInstallerRun", reconciliation);
        Assert.Contains(@"Explorer\StartupApproved\Run", manager);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task Unpackaged_ReportsAnExistingRegistrationAndKeepsAnEnabledPreference(
        bool configured, bool registered, bool expected)
    {
        var result = await AutoStartReconciliation.ReconcileUnpackagedAsync(
            configured, () => Task.FromResult(registered));

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task QueryThrows_KeepsEnabledPreference()
    {
        var result = await AutoStartReconciliation.ReconcileAsync(
            configured: true,
            QueryThrows(new InvalidOperationException("transient WinRT failure")),
            SetSucceeds());

        Assert.True(result);
    }

    [Fact]
    public async Task QueryUnknown_KeepsEnabledPreference()
    {
        var result = await AutoStartReconciliation.ReconcileAsync(
            configured: true,
            Query(AutoStartState.Unknown),
            SetSucceeds());

        Assert.True(result);
    }

    [Fact]
    public async Task QueryUnknown_KeepsDisabledPreference()
    {
        var result = await AutoStartReconciliation.ReconcileAsync(
            configured: false,
            Query(AutoStartState.Unknown),
            SetSucceeds());

        Assert.False(result);
    }

    [Fact]
    public async Task QueryFails_DoesNotAttemptToWriteState()
    {
        var setCalled = false;

        await AutoStartReconciliation.ReconcileAsync(
            configured: true,
            Query(AutoStartState.Unknown),
            _ =>
            {
                setCalled = true;
                return Task.CompletedTask;
            });

        Assert.False(setCalled);
    }

    [Fact]
    public async Task ExplicitRefusal_ReportsDisabled()
    {
        var result = await AutoStartReconciliation.ReconcileAsync(
            configured: true,
            Query(AutoStartState.Disabled),
            SetThrows(new AutoStartRefusedException("Windows startup is disabled by the user.")));

        Assert.False(result);
    }

    [Fact]
    public async Task EnableFailsWithoutRefusal_KeepsEnabledPreference()
    {
        var result = await AutoStartReconciliation.ReconcileAsync(
            configured: true,
            Query(AutoStartState.Disabled),
            SetThrows(new IOException("transient failure while enabling")));

        Assert.True(result);
    }

    [Fact]
    public async Task ConfiguredEnabledButWindowsDisabled_EnablesAndReportsEnabled()
    {
        var requested = (bool?)null;

        var result = await AutoStartReconciliation.ReconcileAsync(
            configured: true,
            Query(AutoStartState.Disabled),
            enable =>
            {
                requested = enable;
                return Task.CompletedTask;
            });

        Assert.True(result);
        Assert.True(requested);
    }

    [Fact]
    public async Task ConfiguredDisabledButWindowsEnabled_AdoptsWindowsState()
    {
        var setCalled = false;

        var result = await AutoStartReconciliation.ReconcileAsync(
            configured: false,
            Query(AutoStartState.Enabled),
            _ =>
            {
                setCalled = true;
                return Task.CompletedTask;
            });

        Assert.True(result);
        Assert.False(setCalled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StatesAlreadyAgree_LeavesPreferenceUnchanged(bool configured)
    {
        var setCalled = false;
        var actual = configured ? AutoStartState.Enabled : AutoStartState.Disabled;

        var result = await AutoStartReconciliation.ReconcileAsync(
            configured,
            Query(actual),
            _ =>
            {
                setCalled = true;
                return Task.CompletedTask;
            });

        Assert.Equal(configured, result);
        Assert.False(setCalled);
    }

    // ResolveAfterFailedChangeAsync: the rollback decision after a change attempt threw.
    // The caller has already written the requested value, so these pin when that write is
    // allowed to be overwritten.

    [Fact]
    public async Task FailedChange_Refusal_RollsBackToDisabledWithoutQuerying()
    {
        var queried = false;

        var result = await AutoStartReconciliation.ResolveAfterFailedChangeAsync(
            requested: true,
            new AutoStartRefusedException("Windows startup is disabled by the user."),
            () =>
            {
                queried = true;
                return Task.FromResult(AutoStartState.Enabled);
            });

        Assert.False(result);
        Assert.False(queried);
    }

    [Fact]
    public async Task FailedChange_QueryThrows_KeepsRequestedValue()
    {
        var result = await AutoStartReconciliation.ResolveAfterFailedChangeAsync(
            requested: true,
            new IOException("transient failure while enabling"),
            QueryThrows(new InvalidOperationException("transient WinRT failure")));

        Assert.True(result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedChange_QueryUnknown_KeepsRequestedValue(bool requested)
    {
        var result = await AutoStartReconciliation.ResolveAfterFailedChangeAsync(
            requested,
            new IOException("transient failure"),
            Query(AutoStartState.Unknown));

        Assert.Equal(requested, result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedChange_DefiniteQuery_OverridesRequestedValue(bool windowsEnabled)
    {
        var state = windowsEnabled ? AutoStartState.Enabled : AutoStartState.Disabled;

        var result = await AutoStartReconciliation.ResolveAfterFailedChangeAsync(
            requested: !windowsEnabled,
            new IOException("transient failure"),
            Query(state));

        Assert.Equal(windowsEnabled, result);
    }

    /// <summary>
    /// Startup reconciliation reads the stored preference, then awaits a StartupTask query.
    /// If the preference is rewritten while that query is in flight, persisting the result of
    /// the stale read would undo the newer value, so it is re-checked first.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PreferenceChangedDuringQuery_DiscardsReconciledValue(bool captured)
    {
        var shouldPersist = AutoStartReconciliation.ShouldPersistReconciledValue(
            captured: captured,
            current: !captured,
            reconciled: !captured);

        Assert.False(shouldPersist);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PreferenceUnchanged_PersistsDifferingReconciledValue(bool captured)
    {
        var shouldPersist = AutoStartReconciliation.ShouldPersistReconciledValue(
            captured: captured,
            current: captured,
            reconciled: !captured);

        Assert.True(shouldPersist);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReconciledValueMatchesPreference_PersistsNothing(bool captured)
    {
        var shouldPersist = AutoStartReconciliation.ShouldPersistReconciledValue(
            captured: captured,
            current: captured,
            reconciled: captured);

        Assert.False(shouldPersist);
    }

    /// <summary>
    /// The reconciled value agreeing with where the preference landed is not a reason to
    /// write it: the newer write already persisted that value through its own path.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PreferenceChangedToReconciledValue_StillDiscards(bool captured)
    {
        var shouldPersist = AutoStartReconciliation.ShouldPersistReconciledValue(
            captured: captured,
            current: !captured,
            reconciled: captured);

        Assert.False(shouldPersist);
    }
}
