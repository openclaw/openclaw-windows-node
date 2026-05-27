using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenClawTray.Services;
using Xunit;

namespace OpenClaw.Tray.Tests;

public sealed class WinUiStartupGateTests
{
    [Fact]
    public void WaitForPackageReady_ReturnsImmediately_WhenStatusIsUnavailable()
    {
        var logs = new List<string>();
        var delays = 0;

        WinUiStartupGate.WaitForPackageReady(
            () => WinUiStartupGate.PackageReadiness.Unavailable(new InvalidOperationException("unpackaged")),
            _ => delays++,
            () => DateTimeOffset.UtcNow,
            (phase, _) => logs.Add(phase),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10));

        Assert.Equal(["Program.packageStatus.unavailable"], logs);
        Assert.Equal(0, delays);
    }

    [Fact]
    public void WaitForPackageReady_ReturnsImmediately_WhenPackageIsReady()
    {
        var logs = new List<string>();
        var delays = 0;

        WinUiStartupGate.WaitForPackageReady(
            () => Ready("status=OK"),
            _ => delays++,
            () => DateTimeOffset.UtcNow,
            (phase, _) => logs.Add(phase),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10));

        Assert.Single(logs);
        Assert.StartsWith("Program.packageStatus.ready status=OK", logs[0], StringComparison.Ordinal);
        Assert.Equal(0, delays);
    }

    [Fact]
    public void WaitForPackageReady_WaitsUntilPackageBecomesReady()
    {
        var logs = new List<string>();
        var delays = new List<TimeSpan>();
        var now = DateTimeOffset.UtcNow;
        var attempts = 0;

        WinUiStartupGate.WaitForPackageReady(
            () => ++attempts < 3 ? NotReady("status=DeploymentInProgress") : Ready("status=OK"),
            delay =>
            {
                delays.Add(delay);
                now += delay;
            },
            () => now,
            (phase, _) => logs.Add(phase),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(250));

        Assert.Equal(2, delays.Count);
        Assert.Contains(logs, phase => phase.StartsWith("Program.packageStatus.readyAfterWait attempts=2", StringComparison.Ordinal));
    }

    [Fact]
    public void WaitForPackageReady_StopsAtTimeout_WhenPackageNeverBecomesReady()
    {
        var logs = new List<string>();
        var now = DateTimeOffset.UtcNow;

        WinUiStartupGate.WaitForPackageReady(
            () => NotReady("status=DeploymentInProgress"),
            delay => now += delay,
            () => now,
            (phase, _) => logs.Add(phase),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(250));

        Assert.Contains(logs, phase => phase.StartsWith("Program.packageStatus.timeout attempts=2", StringComparison.Ordinal));
    }

    [Fact]
    public void WaitForFreshPackageActivationGrace_ReturnsImmediately_WhenInstallDateIsUnavailable()
    {
        var logs = new List<string>();
        var delays = 0;

        WinUiStartupGate.WaitForFreshPackageActivationGrace(
            () => null,
            _ => delays++,
            () => DateTimeOffset.UtcNow,
            (phase, _) => logs.Add(phase),
            TimeSpan.FromSeconds(7));

        Assert.Equal(["Program.packageInstallDate.unavailable"], logs);
        Assert.Equal(0, delays);
    }

    [Fact]
    public void WaitForFreshPackageActivationGrace_ReturnsImmediately_WhenPackageIsOldEnough()
    {
        var logs = new List<string>();
        var now = DateTimeOffset.UtcNow;

        WinUiStartupGate.WaitForFreshPackageActivationGrace(
            () => now - TimeSpan.FromMinutes(1),
            _ => throw new InvalidOperationException("Should not delay"),
            () => now,
            (phase, _) => logs.Add(phase),
            TimeSpan.FromSeconds(7));

        Assert.Single(logs);
        Assert.StartsWith("Program.packageInstallAge.ready", logs[0], StringComparison.Ordinal);
    }

    [Fact]
    public void WaitForFreshPackageActivationGrace_DelaysUntilPackageIsOldEnough()
    {
        var logs = new List<string>();
        var delays = new List<TimeSpan>();
        var now = DateTimeOffset.UtcNow;
        var installed = now - TimeSpan.FromSeconds(2);

        WinUiStartupGate.WaitForFreshPackageActivationGrace(
            () => installed,
            delay =>
            {
                delays.Add(delay);
                now += delay;
            },
            () => now,
            (phase, _) => logs.Add(phase),
            TimeSpan.FromSeconds(7));

        var delay = Assert.Single(delays);
        Assert.Equal(TimeSpan.FromSeconds(5), delay);
        Assert.Contains(logs, phase => phase.StartsWith("Program.packageInstallAge.readyAfterWait", StringComparison.Ordinal));
    }

    [Fact]
    public void IsXamlFactoryClassUnavailable_MatchesObservedTransientStartupFailures()
    {
        var classFactoryFailure = new COMException(
            "ClassFactory cannot supply requested class",
            WinUiStartupGate.ClassFactoryCannotSupplyRequestedClass);
        var unspecifiedFailure = new COMException(
            "Unspecified failure",
            WinUiStartupGate.UnspecifiedFailure);
        var classNotRegistered = new COMException("Class not registered", unchecked((int)0x80040154));

        Assert.True(WinUiStartupGate.IsXamlFactoryClassUnavailable(classFactoryFailure));
        Assert.True(WinUiStartupGate.IsXamlFactoryClassUnavailable(unspecifiedFailure));
        Assert.False(WinUiStartupGate.IsXamlFactoryClassUnavailable(classNotRegistered));
    }

    [Fact]
    public void RunWithXamlFactoryRetry_RetriesClassFactoryFailureThenSucceeds()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var logs = new List<string>();

        WinUiStartupGate.RunWithXamlFactoryRetry(
            () =>
            {
                attempts++;
                if (attempts < 3)
                    throw new COMException(
                        "ClassFactory cannot supply requested class",
                        WinUiStartupGate.ClassFactoryCannotSupplyRequestedClass);
            },
            delay => delays.Add(delay),
            (phase, _) => logs.Add(phase),
            [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500)]);

        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500)], delays);
        Assert.Contains(logs, phase => phase.Contains("retry attempt=1", StringComparison.Ordinal));
        Assert.Contains(logs, phase => phase.Contains("retry attempt=2", StringComparison.Ordinal));
    }

    [Fact]
    public void RunWithXamlFactoryRetry_RetriesUnspecifiedFailureThenSucceeds()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var logs = new List<string>();

        WinUiStartupGate.RunWithXamlFactoryRetry(
            () =>
            {
                attempts++;
                if (attempts == 1)
                    throw new COMException("Unspecified failure", WinUiStartupGate.UnspecifiedFailure);
            },
            delay => delays.Add(delay),
            (phase, _) => logs.Add(phase),
            [TimeSpan.FromMilliseconds(200)]);

        Assert.Equal(2, attempts);
        Assert.Equal([TimeSpan.FromMilliseconds(200)], delays);
        Assert.Contains(logs, phase => phase.Contains("retry attempt=1", StringComparison.Ordinal));
    }

    [Fact]
    public void RunWithXamlFactoryRetry_RethrowsAfterRetryBudget()
    {
        var attempts = 0;
        var logs = new List<string>();

        var ex = Assert.Throws<COMException>(() =>
            WinUiStartupGate.RunWithXamlFactoryRetry(
                () =>
                {
                    attempts++;
                    throw new COMException(
                        "ClassFactory cannot supply requested class",
                        WinUiStartupGate.ClassFactoryCannotSupplyRequestedClass);
                },
                _ => { },
                (phase, _) => logs.Add(phase),
                [TimeSpan.FromMilliseconds(200)]));

        Assert.True(WinUiStartupGate.IsXamlFactoryClassUnavailable(ex));
        Assert.Equal(2, attempts);
        Assert.Contains(logs, phase => phase.Contains("final attempts=1", StringComparison.Ordinal));
    }

    [Fact]
    public void RunWithXamlFactoryRetry_DoesNotRetryOtherComExceptions()
    {
        var attempts = 0;

        Assert.Throws<COMException>(() =>
            WinUiStartupGate.RunWithXamlFactoryRetry(
                () =>
                {
                    attempts++;
                    throw new COMException("Class not registered", unchecked((int)0x80040154));
                },
                _ => throw new InvalidOperationException("Should not delay"),
                (_, _) => { },
                [TimeSpan.FromMilliseconds(200)]));

        Assert.Equal(1, attempts);
    }

    private static WinUiStartupGate.PackageReadiness Ready(string description) =>
        new(true, true, description, null);

    private static WinUiStartupGate.PackageReadiness NotReady(string description) =>
        new(true, false, description, null);
}
